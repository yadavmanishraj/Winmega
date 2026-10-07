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
/// The property-changed callback forwards the item to
/// <see cref="FrameworkElement.DataContext"/>, which is the single
/// binding root for the control's markup: every binding in
/// SongRow.xaml is a classic {Binding} against the item itself, and
/// the Button.Flyout content inherits that same context. While the
/// property is null (a recycled row before the template assigns the
/// item) the bindings simply do not evaluate.
/// </summary>
public sealed partial class SongRow : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(SongItemViewModel),
            typeof(SongRow),
            new PropertyMetadata(null, OnViewModelChanged));

    public SongRow() => InitializeComponent();

    public SongItemViewModel? ViewModel
    {
        get => (SongItemViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SongRow)d).DataContext = e.NewValue;
}
