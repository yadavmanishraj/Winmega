using Microsoft.UI.Xaml;

namespace Omega.Templates;

/// <summary>
/// Code-behind for the shared song-row templates dictionary —
/// required because x:Bind in a standalone ResourceDictionary needs
/// an x:Class with a compiled code-behind part.
/// </summary>
public sealed partial class SongRowTemplates : ResourceDictionary
{
    public SongRowTemplates() => InitializeComponent();
}
