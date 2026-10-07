using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Omega.Controls;

/// <summary>
/// The Now Playing panel's resize grip: a <see cref="Border"/> that
/// carries its own cursor. <c>UIElement.ProtectedCursor</c> is
/// protected, so the panel cannot assign it on a plain Border
/// (CS1540) — the grip derives from Border and assigns it on itself.
/// While the pointer hovers the grip strip, the cursor is the
/// west-east resize arrow.
/// </summary>
public sealed class ResizeGripBorder : Border
{
    public ResizeGripBorder()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
