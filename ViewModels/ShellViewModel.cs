using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Omega.ViewModels;

/// <summary>
/// Shell state for the floating Now Playing bar. Phase 0/1 placeholder:
/// the command flips local state only — the real <c>PlayerService</c>
/// (MediaPlayer + SMTC, design §7) binds here in the playback phase.
///
/// AOT rule (design §3.4 / MVVMTK0045): [ObservableProperty] is used on
/// PARTIAL PROPERTIES only — the field form is a compile error under
/// CsWinRT/AOT and is banned in this codebase.
/// </summary>
public partial class ShellViewModel : ObservableObject
{
    public ShellViewModel()
    {
        NowPlayingTitle = "Nothing playing";
        NowPlayingSubtitle = "Pick a song to start listening";
    }

    [ObservableProperty]
    public partial string NowPlayingTitle { get; set; }

    [ObservableProperty]
    public partial string NowPlayingSubtitle { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [RelayCommand]
    private void TogglePlayPause() => IsPlaying = !IsPlaying;
}
