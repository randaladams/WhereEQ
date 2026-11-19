using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WhereEQ;

/// <summary>
/// Moves a window by dragging <c>handle</c> without Window.DragMove, so Windows Snap doesn't grab the overlay
/// and maximize or tile it when it reaches a screen edge. The window is kept on screen so its controls can't be
/// dragged out of reach.
/// </summary>
public static class WindowDrag
{
    /// <summary>Pixels (DIPs) the mouse must move before a press on the content turns into a drag, so plain
    /// clicks (select a row, open a menu) still work.</summary>
    private const double Threshold = 4;

    /// <param name="handle">Element to drag by. With <paramref name="anywhere"/>, presses anywhere inside it
    /// start a drag once the mouse moves a few pixels, except on buttons, text boxes, sliders and the like.</param>
    /// <param name="canDrag">Checked when a drag would start (e.g. "not locked").</param>
    public static void Attach(UIElement handle, Window window, bool anywhere = false, Func<bool>? canDrag = null)
    {
        Point? press = null; // mouse position inside the window at the press (DIPs)
        var dragging = false;

        void Down(object? _, MouseButtonEventArgs e)
        {
            if (canDrag?.Invoke() == false) return;
            if (e.OriginalSource is DependencyObject d && IsControl(d, handle)) return;
            press = e.GetPosition(window);
            dragging = !anywhere;
            if (dragging) { handle.CaptureMouse(); e.Handled = true; }
        }

        void Move(object? _, MouseEventArgs e)
        {
            if (press is not { } g) return;
            if (e.LeftButton != MouseButtonState.Pressed) { press = null; dragging = false; return; }
            if (!dragging)
            {
                var now = e.GetPosition(window);
                if (Math.Abs(now.X - g.X) < Threshold && Math.Abs(now.Y - g.Y) < Threshold) return;
                dragging = true;
                handle.CaptureMouse();
            }
            var p = CursorInDips(window);
            MoveTo(window, p.X - g.X, p.Y - g.Y);
        }

        void Up(object? _, MouseButtonEventArgs e)
        {
            if (dragging) e.Handled = true; // a drag isn't also a click on the row underneath
            press = null;
            dragging = false;
            handle.ReleaseMouseCapture();
        }

        if (anywhere)
        {
            handle.PreviewMouseLeftButtonDown += Down;
            handle.PreviewMouseMove += Move;
            handle.PreviewMouseLeftButtonUp += Up;
        }
        else
        {
            handle.MouseLeftButtonDown += Down;
            handle.MouseMove += Move;
            handle.MouseLeftButtonUp += Up;
        }
        handle.LostMouseCapture += (_, _) => { press = null; dragging = false; };
    }

    /// <summary>Places the window, keeping it inside the screen area so it can always be grabbed again.</summary>
    public static void MoveTo(Window window, double left, double top)
    {
        var minL = SystemParameters.VirtualScreenLeft;
        var minT = SystemParameters.VirtualScreenTop;
        var maxL = minL + SystemParameters.VirtualScreenWidth - window.ActualWidth;
        var maxT = minT + SystemParameters.VirtualScreenHeight - window.ActualHeight;
        window.Left = Math.Max(minL, Math.Min(left, Math.Max(minL, maxL)));
        window.Top = Math.Max(minT, Math.Min(top, Math.Max(minT, maxT)));
    }

    /// <summary>Pulls a window back onto the screen if it's partly off it (e.g. after a resize or a screen change).</summary>
    public static void KeepOnScreen(Window window) => MoveTo(window, window.Left, window.Top);

    /// <summary>True when the press is on something that uses the mouse itself.</summary>
    private static bool IsControl(DependencyObject d, UIElement stop)
    {
        for (var x = d; x != null && x != stop; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x is ButtonBase or TextBoxBase or RangeBase or Thumb or ComboBox or ResizeGrip or ScrollBar or MenuItem)
                return true;
        return false;
    }

    /// <summary>
    /// Draws a see-through overlay on the CPU instead of the graphics card. A transparent (layered) WPF window drawn
    /// on the GPU is copied back from the GPU on every repaint, which makes the GPU wait and can stall the game
    /// underneath (seen as the game pausing once the buffs list started repainting every second). These windows
    /// are small, so CPU drawing is cheap. WHEREEQ_GPU_OVERLAYS=1 turns it off (for comparison).
    /// </summary>
    public static void UseSoftwareRendering(System.Windows.Interop.HwndSource? source)
    {
        if (Environment.GetEnvironmentVariable("WHEREEQ_GPU_OVERLAYS") == "1") return;
        if (source?.CompositionTarget is { } target) target.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
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
