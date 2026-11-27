namespace ParentalControl.Domain.Requests;

/// <summary>A window rectangle in device-independent units.</summary>
public readonly record struct WindowRect(double Left, double Top, double Width, double Height);

/// <summary>
/// The start position of the Request Window (request-window). The window has a preferred size of 560 × 760. On a small
/// screen (for example 1024 × 768 with the taskbar), that size is higher than the work area, and the title bar goes
/// above the top edge of the screen. Then the child cannot move the window, and the close button is cut off. The
/// placement makes the window fit into the work area and puts it in the centre of the work area.
/// </summary>
public static class RequestWindowPlacement
{
    /// <summary>
    /// The window rectangle: the preferred size, made smaller to fit the work area, but not smaller than the minimum
    /// size, in the centre of the work area. If the work area is smaller than the minimum size, the window starts at the
    /// top-left corner of the work area, so that its title bar is visible.
    /// </summary>
    public static WindowRect Fit(WindowRect workArea, double preferredWidth, double preferredHeight, double minWidth, double minHeight)
    {
        var width = Math.Max(minWidth, Math.Min(preferredWidth, workArea.Width));
        var height = Math.Max(minHeight, Math.Min(preferredHeight, workArea.Height));
        var left = workArea.Left + Math.Max(0, (workArea.Width - width) / 2);
        var top = workArea.Top + Math.Max(0, (workArea.Height - height) / 2);
        return new WindowRect(left, top, width, height);
    }
}
