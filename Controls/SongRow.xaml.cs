using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Omega.ViewModels;

namespace Omega.Controls;

/// <summary>
/// The shared song row, hosted as a UserControl (D1 — see the header
/// comment in SongRow.xaml for why the shared dictionary template it
/// replaces never evaluated its bindings at runtime).
///
/// Binding host: <see cref="ViewModel"/> is a dependency property so
/// a page-local DataTemplate can create the control and hand it the
/// row item (<c>&lt;controls:SongRow ViewModel="{x:Bind}" /&gt;</c>).
/// Every binding in SongRow.xaml is a compiled
/// <c>{x:Bind ViewModel.&lt;path&gt;, Mode=OneWay}</c> rooted at this
/// property: the XAML compiler types the paths against
/// <see cref="SongItemViewModel"/> (so the bindings are trimming/AOT
/// clean — no WMC1510), and the compiled binding re-evaluates
/// whenever this property changes, which is what makes recycled rows
/// pick up their new item. Nothing forwards to DataContext — the
/// markup never binds against it.
/// </summary>
public sealed partial class SongRow : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(SongItemViewModel),
            typeof(SongRow),
            new PropertyMetadata(null));

    public SongRow() => InitializeComponent();

    public SongItemViewModel? ViewModel
    {
        get => (SongItemViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }
}
