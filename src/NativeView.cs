using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Diagnostics;

namespace DosboxEnhanced;

// DLSS-G presents only to a top-level HWND. The owned surface follows this
// layout placeholder, while the surrounding WPF controls remain interactive.
public sealed class NativeView : HwndHost
{
    public event Action<IntPtr>? WindowReady;
    public event Action<int, IntPtr, IntPtr>? NativeInput;
    public IntPtr Surface;
    IntPtr placeholder;
    readonly string className = "DosboxPresentation" + Guid.NewGuid().ToString("N");
    readonly WindowProc procedure;
    Rect previous;
    public double PixelWidth => previous.Width;
    public double PixelHeight => previous.Height;
    bool shown;
    public bool HidePointer { get; set; }
    long placementChanged;
    public bool PlacementSettled => (Stopwatch.GetTimestamp() - placementChanged) / (double)Stopwatch.Frequency > .35;
    delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
    {
        public uint size, style; public WindowProc procedure; public int classExtra, windowExtra;
        public IntPtr instance, icon, cursor, background; public string? menu, name; public IntPtr smallIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct MouseTracking { public uint size, flags; public IntPtr hwnd; public uint hoverTime; }
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref MouseTracking tracking);
    public bool PresentationActive { get { GetWindowThreadProcessId(GetForegroundWindow(), out var process); return process == Environment.ProcessId; } }
    public NativeView() { procedure = SurfaceProc; IsVisibleChanged += (_, _) => UpdatePlacement(); }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        placeholder = CreateWindowEx(0, "STATIC", "DOS layout", unchecked((int)0x56000000), 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var definition = new WindowClass { size = (uint)Marshal.SizeOf<WindowClass>(), procedure = procedure, name = className };
        if (RegisterClassEx(ref definition) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        // Tool window avoids an extra taskbar entry; no-activate preserves menu focus.
        Surface = CreateWindowEx(0x08000080, className, "DOS native graphics", unchecked((int)0x86000000), 0, 0, 1, 1, GetAncestor(parent.Handle, 2), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (Surface == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        WindowReady?.Invoke(Surface); return new(this, placeholder);
    }
    protected override void OnWindowPositionChanged(Rect rectangle) { base.OnWindowPositionChanged(rectangle); UpdatePlacement(); }
    public void UpdatePlacement()
    {
        if (Surface == IntPtr.Zero || PresentationSource.FromVisual(this) == null) return;
        var owner = Window.GetWindow(this);
        bool visible = IsVisible && ActualWidth > 1 && ActualHeight > 1 && owner?.WindowState != WindowState.Minimized;
        if (visible)
        {
            var start = PointToScreen(new Point(0, 0)); var end = PointToScreen(new Point(ActualWidth, ActualHeight));
            var bounds = new Rect(start, end);
            if (bounds != previous) { placementChanged = Stopwatch.GetTimestamp(); SetWindowPos(Surface, IntPtr.Zero, (int)start.X, (int)start.Y, (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 0x14); previous = bounds; }
        }
        if (visible != shown) { ShowWindow(Surface, visible ? 4 : 0); shown = visible; }
    }
    IntPtr SurfaceProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == 0x20) { SetCursor(HidePointer ? IntPtr.Zero : LoadCursor(IntPtr.Zero, new IntPtr(32512))); return new IntPtr(1); }
        if (message == 0x21)
        {
            // A real game click activates the owner so keyboard focus and FG resume.
            if (Dispatcher.CheckAccess()) Window.GetWindow(this)?.Activate();
            else Dispatcher.BeginInvoke(() => Window.GetWindow(this)?.Activate());
            return new IntPtr(3); // MA_NOACTIVATE: keyboard stays on the WPF stage.
        }
        if (message == 0x200) { var tracking = new MouseTracking { size = (uint)Marshal.SizeOf<MouseTracking>(), flags = 2, hwnd = hwnd }; TrackMouseEvent(ref tracking); }
        if (message is >= 0x100 and <= 0x105 || message is >= 0x200 and <= 0x20a || message is 8 or 0x2a3 or 0x215 or 0x1f)
        {
            if (Dispatcher.CheckAccess()) NativeInput?.Invoke((int)message, wParam, lParam);
            else Dispatcher.BeginInvoke(() => NativeInput?.Invoke((int)message, wParam, lParam));
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (Surface != IntPtr.Zero) DestroyWindow(Surface);
        Surface = IntPtr.Zero; DestroyWindow(hwnd.Handle); placeholder = IntPtr.Zero;
        UnregisterClass(className, IntPtr.Zero);
    }
}
