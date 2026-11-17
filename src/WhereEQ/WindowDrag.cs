using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace WhereEQ;

/// <summary>
/// Moves a window by dragging <c>handle</c> without Window.DragMove, so Windows Snap doesn't grab the overlay
/// and maximize or tile it when it reaches a screen edge. The window can be placed right up against any edge.
/// </summary>
public static class WindowDrag
{
    public static void Attach(UIElement handle, Window window)
    {
        Point? grab = null; // mouse position inside the window when the drag started (DIPs)

        handle.MouseLeftButtonDown += (_, e) =>
        {
            // Let buttons on the handle work normally.
            if (e.OriginalSource is DependencyObject d && FindButton(d, handle)) return;
            grab = e.GetPosition(window);
            handle.CaptureMouse();
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (grab is not { } g || e.LeftButton != MouseButtonState.Pressed) return;
            var p = CursorInDips(window);
            window.Left = p.X - g.X;
            window.Top = p.Y - g.Y;
        };
        handle.MouseLeftButtonUp += (_, _) => { grab = null; handle.ReleaseMouseCapture(); };
        handle.LostMouseCapture += (_, _) => grab = null;
    }

    private static bool FindButton(DependencyObject d, UIElement stop)
    {
        for (var x = d; x != null && x != stop; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    /// <summary>Cursor position in the window's device-independent screen coordinates.</summary>
    public static Point CursorInDips(Visual window)
    {
        GetCursorPos(out var pt);
        var src = PresentationSource.FromVisual(window);
        var p = new Point(pt.X, pt.Y);
        return src?.CompositionTarget != null ? src.CompositionTarget.TransformFromDevice.Transform(p) : p;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
}
