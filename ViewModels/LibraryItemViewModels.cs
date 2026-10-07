using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Omega.Core.Persistence;

namespace Omega.ViewModels;

/// <summary>One user playlist row in Library (wraps the persistence contract's LibraryPlaylist).</summary>
public sealed class PlaylistItemViewModel
{
    public PlaylistItemViewModel(LibraryPlaylist playlist) => Playlist = playlist;

    public LibraryPlaylist Playlist { get; }

    public string Id => Playlist.Id;

    public string Name => Playlist.Name;

    public string SongCountText => Res.Format("PlaylistSongCountFormat", Playlist.SongCount);

    public string UpdatedText => Playlist.UpdatedAt.ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>
/// One downloads row in Library (wraps the persistence contract's
/// DownloadRecord). Status copy is localised; the stored
/// <see cref="DownloadRecord.ErrorMessage"/> is shown verbatim for
/// failed downloads.
/// </summary>
public partial class DownloadItemViewModel : ObservableObject
{
    private readonly Func<DownloadItemViewModel, Task> _deleteHandler;

    public DownloadItemViewModel(DownloadRecord record, Func<DownloadItemViewModel, Task> deleteHandler)
    {
        Record = record;
        _deleteHandler = deleteHandler;
    }

    public DownloadRecord Record { get; }

    public string Title => Record.Title;

    public string Artists => Record.Artists;

    public ImageSource? Artwork => ArtworkHelper.From(Record.ImageUrl);

    public string StatusText => Record.Status switch
    {
        DownloadStatus.Downloading => Res.Get("DownloadStatusDownloading"),
        DownloadStatus.Completed => Res.Get("DownloadStatusCompleted"),
        DownloadStatus.Failed => Res.Get("DownloadStatusFailed"),
        _ => string.Empty,
    };

    public bool IsDownloading => Record.Status == DownloadStatus.Downloading;

    public bool IsFailed => Record.Status == DownloadStatus.Failed;

    /// <summary>Progress is a whole-percent value (0–100) per the persistence contract.</summary>
    public double ProgressValue => Record.Progress;

    public string ProgressText => string.Create(CultureInfo.CurrentCulture, $"{Record.Progress}%");

    public string ErrorText => Record.ErrorMessage ?? string.Empty;

    public bool HasErrorText => IsFailed && !string.IsNullOrWhiteSpace(Record.ErrorMessage);

    public string UpdatedText => Record.UpdatedAt.ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Automation name for the row's delete button.</summary>
    public string DeleteName => Res.Get("Delete");

    [RelayCommand]
    private Task DeleteAsync() => _deleteHandler(this);
}
