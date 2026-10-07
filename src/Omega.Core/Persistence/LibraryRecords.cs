namespace Omega.Core.Persistence;

/// <summary>
/// A user-created, on-device playlist as exposed by
/// <see cref="ILibraryStore"/>. The id is the store's row id rendered as
/// a string, so the app layer never deals with the storage key type.
/// </summary>
public sealed record LibraryPlaylist(
    string Id,
    string Name,
    int SongCount,
    DateTimeOffset UpdatedAt);

/// <summary>Download lifecycle as surfaced to the Library page.</summary>
public enum DownloadStatus
{
    Downloading,
    Completed,
    Failed,
}

/// <summary>
/// One downloads-table row: everything the Library Downloads section
/// renders (and a retry needs) without touching the file system.
/// <see cref="Progress"/> is a whole percent (0–100).
/// </summary>
public sealed record DownloadRecord(
    string SongId,
    string Title,
    string Artists,
    string? ImageUrl,
    string? LocalPath,
    DownloadStatus Status,
    int Progress,
    string? ErrorMessage,
    DateTimeOffset UpdatedAt);
