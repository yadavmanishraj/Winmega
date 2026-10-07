using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Omega.Core.Models;
using Omega.Core.Persistence;
using Omega.Core.Upstream;
using Windows.Storage;

namespace Omega.Services;

/// <summary>
/// Download engine (FX3 — audit M2/M4: the Downloads tab previously had
/// no creation path at all). Resolves a song's stream ladder the way
/// <see cref="PlayerService"/> does (a mapped <see cref="Song"/>
/// carries its decrypted ladder; a ladder-less song is re-resolved via
/// <see cref="JioSaavnClient.GetSongsByIdsAsync"/>), streams the best
/// rung to <c>LocalFolder\Downloads</c> with plain HttpClient, and
/// mirrors the lifecycle into <see cref="ILibraryStore"/> download
/// records: Downloading (progress upserts, throttled) → Completed with
/// the local path, or Failed with the error message. Records are the
/// single source of truth for the Library Downloads tab; completed
/// downloads play from the local file (see
/// LibraryViewModel.PlayDownloadAsync).
///
/// Threading: downloads run on the thread pool; the store is the same
/// singleton the view models use. <see cref="RecordChanged"/> is
/// raised on the captured UI <see cref="DispatcherQueue"/> when one
/// was available at construction (the composition root builds this
/// service on the UI thread), otherwise on the completing thread.
/// </summary>
public sealed class DownloadService
{
    private static readonly string[] KnownAudioExtensions = { ".mp4", ".m4a", ".mp3", ".aac" };

    private readonly ILibraryStore _store;
    private readonly JioSaavnClient _client;
    private readonly HttpClient _http = new();
    private readonly DispatcherQueue? _dispatcher;
    private readonly ConcurrentDictionary<string, Lazy<Task<DownloadRecord>>> _inFlight = new();

    public DownloadService(ILibraryStore store, JioSaavnClient client)
    {
        _store = store;
        _client = client;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Raised after every record upsert (start, progress steps,
    /// completion, failure) so an attached Library page can update its
    /// Downloads rows live.
    /// </summary>
    public event EventHandler<DownloadRecord>? RecordChanged;

    /// <summary>
    /// Downloads <paramref name="song"/> (or joins the in-flight
    /// download for the same song). Never throws: the returned record
    /// carries the outcome — Completed with <see cref="DownloadRecord.LocalPath"/>
    /// set, or Failed with <see cref="DownloadRecord.ErrorMessage"/>.
    /// </summary>
    public Task<DownloadRecord> DownloadAsync(Song song)
    {
        // Lazy so a concurrent duplicate call can never start a
        // second transfer for the same song.
        Lazy<Task<DownloadRecord>> lazy = _inFlight.GetOrAdd(
            song.Id,
            _ => new Lazy<Task<DownloadRecord>>(() => RunDownloadAsync(song)));
        return lazy.Value;
    }

    /// <summary>
    /// Retries a download from its song id alone (Downloads-tab failed
    /// rows): the full song is re-resolved upstream, because a
    /// <see cref="DownloadRecord"/> carries display metadata only.
    /// Returns null when the song no longer resolves upstream — the
    /// existing record is left untouched in that case.
    /// </summary>
    public async Task<DownloadRecord?> RetryAsync(string songId)
    {
        Song? song = null;
        try
        {
            IReadOnlyList<Song> resolved = await _client.GetSongsByIdsAsync(new[] { songId });
            if (resolved.Count > 0)
            {
                song = resolved[0];
            }
        }
        catch (Exception)
        {
            // Resolution failed — same null outcome as "not found".
        }

        return song is null ? null : await DownloadAsync(song);
    }

    /// <summary>True when a completed download for the song exists on disk.</summary>
    public async Task<bool> IsDownloadedAsync(string songId)
    {
        IReadOnlyList<DownloadRecord> records = await _store.GetDownloadsAsync();
        foreach (DownloadRecord record in records)
        {
            if (record.SongId == songId)
            {
                return record.Status == DownloadStatus.Completed
                    && record.LocalPath is not null
                    && File.Exists(record.LocalPath);
            }
        }

        return false;
    }

    /// <summary>Deletes the local file (when present) and the store record.</summary>
    public async Task DeleteAsync(DownloadRecord record)
    {
        if (record.LocalPath is { } path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // The record delete below still proceeds: a locked or
                // already-gone file must not strand the row forever.
            }
        }

        await _store.DeleteDownloadAsync(record.SongId);
    }

    private async Task<DownloadRecord> RunDownloadAsync(Song song)
    {
        try
        {
            return await DownloadCoreAsync(song);
        }
        finally
        {
            _inFlight.TryRemove(song.Id, out _);
        }
    }

    private async Task<DownloadRecord> DownloadCoreAsync(Song song)
    {
        Song resolved = song;
        if (resolved.StreamUrls.Count == 0)
        {
            try
            {
                IReadOnlyList<Song> fresh = await _client.GetSongsByIdsAsync(new[] { song.Id });
                if (fresh.Count > 0)
                {
                    resolved = fresh[0];
                }
            }
            catch (Exception)
            {
                // Fall through: the ladder check below fails the record.
            }
        }

        string? url = resolved.BestStreamUrl;
        if (url is null)
        {
            return await UpsertAsync(resolved, null, DownloadStatus.Failed, 0,
                $"“{resolved.Name}” has no playable stream to download.");
        }

        string? localPath = null;
        try
        {
            StorageFolder folder = await ApplicationData.Current.LocalFolder
                .CreateFolderAsync("Downloads", CreationCollisionOption.OpenIfExists);
            localPath = Path.Combine(folder.Path, FileNameFor(resolved.Id, url));

            await UpsertAsync(resolved, null, DownloadStatus.Downloading, 0, null);

            using HttpResponseMessage response = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            long read = 0;
            int lastReported = 0;
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(
                localPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                int n;
                while ((n = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total > 0)
                    {
                        int percent = (int)(read * 100 / total);
                        if (percent >= lastReported + 5 && percent < 100)
                        {
                            lastReported = percent;
                            await UpsertAsync(resolved, null, DownloadStatus.Downloading, percent, null);
                        }
                    }
                }
            }

            return await UpsertAsync(resolved, localPath, DownloadStatus.Completed, 100, null);
        }
        catch (Exception ex)
        {
            if (localPath is not null)
            {
                try
                {
                    if (File.Exists(localPath))
                    {
                        File.Delete(localPath);
                    }
                }
                catch (Exception)
                {
                    // Partial-file cleanup is best-effort.
                }
            }

            return await UpsertAsync(resolved, null, DownloadStatus.Failed, 0, ex.Message);
        }
    }

    private async Task<DownloadRecord> UpsertAsync(
        Song song, string? localPath, DownloadStatus status, int progress, string? error)
    {
        var record = new DownloadRecord(
            SongId: song.Id,
            Title: song.Name,
            Artists: song.PrimaryArtistNames,
            ImageUrl: song.Image.Best,
            LocalPath: localPath,
            Status: status,
            Progress: progress,
            ErrorMessage: error,
            UpdatedAt: DateTimeOffset.UtcNow);
        await _store.UpsertDownloadAsync(record);
        RaiseRecordChanged(record);
        return record;
    }

    private void RaiseRecordChanged(DownloadRecord record)
    {
        EventHandler<DownloadRecord>? handler = RecordChanged;
        if (handler is null)
        {
            return;
        }

        if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => handler(this, record));
        }
        else
        {
            handler(this, record);
        }
    }

    /// <summary>Builds a filesystem-safe file name from the song id and the stream URL's extension.</summary>
    private static string FileNameFor(string songId, string url)
    {
        string extension = ".mp4";
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            string candidate = Path.GetExtension(uri.AbsolutePath);
            foreach (string known in KnownAudioExtensions)
            {
                if (string.Equals(candidate, known, StringComparison.OrdinalIgnoreCase))
                {
                    extension = known;
                    break;
                }
            }
        }

        char[] chars = songId.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
            {
                chars[i] = '_';
            }
        }

        return new string(chars) + extension;
    }
}
