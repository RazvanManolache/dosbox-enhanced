using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DosboxEnhanced;

public record MouseSettings(bool CaptureOnClick = true, bool HidePointer = true, string ReleaseShortcut = "Caps Lock", double Sensitivity = 1);
public sealed record MouseShortcut(Key Key, ModifierKeys Modifiers)
{
    public static MouseShortcut Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        ModifierKeys modifiers = ModifierKeys.None;
        if (parts.Length == 0) throw new ArgumentException("Choose a release shortcut.");
        foreach (var modifier in parts[..^1])
            modifiers |= modifier.ToLowerInvariant() switch { "ctrl" or "control" => ModifierKeys.Control, "alt" => ModifierKeys.Alt, "shift" => ModifierKeys.Shift, _ => throw new ArgumentException("Use Ctrl, Alt or Shift as shortcut modifiers.") };
        var keyName = parts[^1].Replace(" ", "");
        if (keyName.Length == 1 && char.IsAsciiDigit(keyName[0])) keyName = "D" + keyName;
        if (keyName.Equals("Esc", StringComparison.OrdinalIgnoreCase)) keyName = "Escape";
        if (keyName.Equals("ScrollLock", StringComparison.OrdinalIgnoreCase)) keyName = "Scroll";
        if (!Enum.TryParse<Key>(keyName, true, out var key) || !Enum.IsDefined(key) || MainWindow.MapKey(key) == 0 || key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt)
            throw new ArgumentException("Use a key such as Caps Lock, F12 or M, optionally with Ctrl, Alt or Shift.");
        if (key == Key.F11 || (key == Key.Enter && modifiers.HasFlag(ModifierKeys.Alt)) || (key == Key.F10 && modifiers.HasFlag(ModifierKeys.Control)))
            throw new ArgumentException("F11, Alt+Enter and Ctrl+F10 remain built-in fullscreen/menu shortcuts. Choose another release shortcut.");
        if (key == Key.Delete && modifiers.HasFlag(ModifierKeys.Control) && modifiers.HasFlag(ModifierKeys.Alt)) throw new ArgumentException("Ctrl+Alt+Delete is reserved by Windows. Choose another release shortcut.");
        return new(key, modifiers);
    }
    public override string ToString() => (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + (Key == Key.CapsLock ? "Caps Lock" : Key == Key.Scroll ? "Scroll Lock" : Key is >= Key.D0 and <= Key.D9 ? ((int)(Key-Key.D0)).ToString() : Key.ToString());
}

public sealed partial class MainWindow
{
    volatile bool mouseCaptured, mouseReleasePending;
    bool rawMouseRegistered;
    long rawMousePackets, forwardedMouseMoves;
    volatile MouseShortcut releaseShortcut = new(Key.CapsLock, ModifierKeys.None);
    double fractionalMouseX, fractionalMouseY, mouseHintUntil;
    CursorPoint mouseRestorePoint;
    MouseRect mouseClipBounds;
    Point? absoluteRawPoint;
    readonly HashSet<Key> reservedMouseKeys = [];
    Button? mouseButton;
    Window? mouseSettingsWindow;
    readonly TextBlock mouseHelp = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush(221, 231, 241), Margin = new Thickness(0, 9, 0, 3) };
    readonly TextBlock mouseHintText = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    readonly Popup mouseHint = new() { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Relative, IsHitTestVisible = false };
    IntPtr keyboardHook;
    Thread? mouseShortcutThread;
    uint mouseShortcutThreadId;
    readonly HashSet<Key> hookReservedKeys = [];
    KeyboardHookProc? keyboardHookProcedure;
    HwndSource? mouseMessageSource;
    delegate IntPtr KeyboardHookProc(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct MouseRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct RawMouseDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    // RAWINPUTHEADER is 24 bytes in this x64 app, followed by the 24-byte RAWMOUSE.
    [StructLayout(LayoutKind.Explicit, Size = 48)] struct RawMousePacket
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(24)] public ushort Flags;
        [FieldOffset(36)] public int X;
        [FieldOffset(40)] public int Y;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern bool ClipCursor(ref MouseRect rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] static extern bool UnclipCursor(IntPtr rect);
    [DllImport("user32.dll")] static extern bool GetClipCursor(out MouseRect rect);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetCapture();
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int metric);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices([In] RawMouseDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr input, uint command, out RawMousePacket packet, ref uint size, uint headerSize);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, KeyboardHookProc procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint MouseForegroundProcess(IntPtr hwnd, out uint process);
    [StructLayout(LayoutKind.Sequential)] struct ShortcutMessage { public IntPtr Window; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public CursorPoint Point; public uint Private; }
    [DllImport("user32.dll")] static extern int GetMessage(out ShortcutMessage message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref ShortcutMessage message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref ShortcutMessage message);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);

    void InitializeMouseCapture()
    {
        try { releaseShortcut = MouseShortcut.Parse(preferences.Mouse.ReleaseShortcut); }
        catch (ArgumentException) { preferences.Mouse = preferences.Mouse with { ReleaseShortcut = "Caps Lock" }; }
        mouseHint.PlacementTarget = image;
        mouseHint.Child = new Border { Background = Brush(27, 57, 67), BorderBrush = Brush(74, 160, 174), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(15, 10, 15, 10), Width = 370, Child = mouseHintText, IsHitTestVisible = false };
        Loaded += (_, _) => { mouseMessageSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); mouseMessageSource?.AddHook(MouseMessages); };
        LocationChanged += (_, _) => ReleaseMouseCapture();
        StateChanged += (_, _) => ReleaseMouseCapture();
        UpdateMouseUi();
    }
    bool CaptureGameMouse()
    {
        if (mouseCaptured) return true;
        if (core?.IsReady != true || core.IsPaused || !nativeView.PresentationActive || image.ActualWidth < 2 || image.ActualHeight < 2) return false;
        EnsureMouseHooks();
        var bounds = GameMouseBounds(); GetCursorPos(out mouseRestorePoint);
        stage.Focus();
        if (!ClipCursor(ref bounds)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not capture the mouse.");
        mouseClipBounds = bounds;
        mouseReleasePending = false; mouseCaptured = true; fractionalMouseX = fractionalMouseY = 0; absoluteRawPoint = previousMouse = null;
        SetCapture(nativeView.IsVisible ? nativeView.Surface : new WindowInteropHelper(this).Handle);
        nativeView.HidePointer = preferences.Mouse.HidePointer; Mouse.OverrideCursor = preferences.Mouse.HidePointer ? Cursors.None : null;
        if (preferences.Mouse.HidePointer) SetCursor(IntPtr.Zero);
        dwell.Reset(); if (fullscreen) ShowMenu(false);
        ShowMouseHint("Mouse captured · " + releaseShortcut + " to release"); UpdateMouseUi(); return true;
    }
    MouseRect GameMouseBounds()
    {
        var start = image.PointToScreen(new Point(0, 0)); var end = image.PointToScreen(new Point(image.ActualWidth, image.ActualHeight));
        return new() { Left = (int)Math.Ceiling(start.X), Top = (int)Math.Ceiling(start.Y), Right = (int)Math.Floor(end.X), Bottom = (int)Math.Floor(end.Y) };
    }
    void EnsureMouseHooks()
    {
        if (mouseMessageSource == null) throw new InvalidOperationException("The game window is not ready for mouse capture.");
        if (keyboardHook == IntPtr.Zero)
        {
            var ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            mouseShortcutThread = new Thread(() =>
            {
                mouseShortcutThreadId = GetCurrentThreadId(); keyboardHookProcedure = MouseKeyboardHook;
                keyboardHook = SetWindowsHookEx(13, keyboardHookProcedure, GetModuleHandle(null), 0);
                ready.SetResult(keyboardHook == IntPtr.Zero ? new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enable the mouse release shortcut.") : null);
                try { if (keyboardHook != IntPtr.Zero) while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); } }
                finally { if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook); keyboardHook = IntPtr.Zero; }
            }) { IsBackground = true, Name = "DOS mouse release shortcut" };
            mouseShortcutThread.Start();
            // Installation is short; the actual shortcut loop stays off the WPF thread.
            if (!ready.Task.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("The mouse release shortcut is not ready; try again.");
            if (ready.Task.Result is Exception error) throw error;
        }
        if (!rawMouseRegistered)
        {
            var device = new RawMouseDevice { Page = 1, Usage = 2, Flags = 0, Target = new WindowInteropHelper(this).Handle };
            if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawMouseDevice>())) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enable relative mouse input.");
            rawMouseRegistered = true;
        }
    }
    IntPtr MouseMessages(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0xff && mouseCaptured) ReadRawMouse(lParam);
        if (message == 0x1f || (message == 0x1c && wParam == IntPtr.Zero)) ReleaseMouseCapture();
        return IntPtr.Zero; // Windows still needs DefWindowProc to clean up WM_INPUT.
    }
    void ReadRawMouse(IntPtr input)
    {
        if (!nativeView.PresentationActive) { ReleaseMouseCapture(); return; }
        uint size = 48;
        if (GetRawInputData(input, 0x10000003, out var packet, ref size, 24) == uint.MaxValue || size < 48 || packet.Type != 0) return;
        rawMousePackets++;
        if ((packet.Flags & 1) == 0) ForwardRelativeMouse(packet.X, packet.Y);
        else
        {
            bool allScreens = (packet.Flags & 2) != 0;
            var p = new Point(packet.X * GetSystemMetrics(allScreens ? 78 : 0) / 65535.0, packet.Y * GetSystemMetrics(allScreens ? 79 : 1) / 65535.0);
            if (absoluteRawPoint is Point old) ForwardRelativeMouse(p.X - old.X, p.Y - old.Y);
            absoluteRawPoint = p;
        }
    }
    void ForwardRelativeMouse(double x, double y)
    {
        if (!mouseCaptured || mouseReleasePending || core == null) return;
        double sensitivity = double.IsFinite(preferences.Mouse.Sensitivity) ? Math.Clamp(preferences.Mouse.Sensitivity, .25, 4) : 1;
        fractionalMouseX += x * sensitivity; fractionalMouseY += y * sensitivity;
        int dx = (int)fractionalMouseX, dy = (int)fractionalMouseY; fractionalMouseX -= dx; fractionalMouseY -= dy;
        if (dx != 0 || dy != 0) { core.MouseMove(dx, dy); forwardedMouseMoves++; }
    }
    bool HandleMouseReleaseKey(Key key, ModifierKeys modifiers, bool down)
    {
        if (reservedMouseKeys.Contains(key)) { if (!down) reservedMouseKeys.Remove(key); return true; }
        if (down && mouseCaptured && key == Key.F10 && modifiers.HasFlag(ModifierKeys.Control))
        {
            reservedMouseKeys.Add(key); ReleaseMouseCapture(revealMenu: true, restorePointer: true); ShowMenu(true); SetAllPanels(!panels);
            Keyboard.ClearFocus(); ShowMouseHint("Mouse free · click the game to capture again"); return true;
        }
        if (!down || !mouseCaptured || key != releaseShortcut.Key || (modifiers & releaseShortcut.Modifiers) != releaseShortcut.Modifiers) return false;
        reservedMouseKeys.Add(key); ReleaseMouseCapture(revealMenu: true, restorePointer: true);
        Keyboard.ClearFocus(); ShowMouseHint("Mouse free · click the game to capture again"); return true;
    }
    IntPtr MouseKeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && (mouseCaptured || hookReservedKeys.Count > 0))
        {
            var key = KeyInterop.KeyFromVirtualKey(Marshal.ReadInt32(data)); bool down = message.ToInt64() is 0x100 or 0x104;
            if (hookReservedKeys.Contains(key)) { if (!down) hookReservedKeys.Remove(key); return new IntPtr(1); }
            MouseForegroundProcess(GetForegroundWindow(), out uint foreground);
            ModifierKeys modifiers = ModifierKeys.None;
            if (GetAsyncKeyState(0x11) < 0) modifiers |= ModifierKeys.Control;
            if (GetAsyncKeyState(0x12) < 0) modifiers |= ModifierKeys.Alt;
            if (GetAsyncKeyState(0x10) < 0) modifiers |= ModifierKeys.Shift;
            var shortcut = releaseShortcut;
            bool emergency = key == Key.F10 && modifiers.HasFlag(ModifierKeys.Control);
            if (down && mouseCaptured && foreground == Environment.ProcessId && (emergency || (key == shortcut.Key && (modifiers & shortcut.Modifiers) == shortcut.Modifiers)))
            {
                hookReservedKeys.Add(key); mouseReleasePending = true;
                // Unlock immediately, even if drawing, saving or the DOS core delays WPF.
                UnclipCursor(IntPtr.Zero);
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, () =>
                {
                    mouseReleasePending = false; ReleaseMouseCapture(revealMenu: true, restorePointer: true);
                    if (emergency) { if (nativeView.PresentationActive) ShowMenu(true); SetAllPanels(!panels); }
                    Keyboard.ClearFocus(); ShowMouseHint("Mouse free · click the game to capture again");
                });
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }
    void ReleaseMouseCapture(bool revealMenu = false, bool restorePointer = false)
    {
        if (!mouseCaptured) return;
        mouseCaptured = false; nativeView.HidePointer = false; Mouse.OverrideCursor = null;
        UnclipCursor(IntPtr.Zero); ReleaseCapture(); SetCursor(LoadCursor(IntPtr.Zero, new IntPtr(32512)));
        if (restorePointer && nativeView.PresentationActive) SetCursorPos(mouseRestorePoint.X, mouseRestorePoint.Y);
        core?.ReleaseKeys(); previousMouse = absoluteRawPoint = null; fractionalMouseX = fractionalMouseY = 0;
        dwell.Reset(); mouseHint.IsOpen = false; UpdateMouseUi();
        if (revealMenu && fullscreen && nativeView.PresentationActive) ShowMenu(true);
    }
    void TickMouseCapture()
    {
        if (mouseCaptured && (core?.IsReady != true || core.IsPaused || !nativeView.PresentationActive || !stage.IsKeyboardFocusWithin || WindowState == WindowState.Minimized)) ReleaseMouseCapture();
        if (mouseCaptured && !mouseReleasePending)
        {
            var bounds = GameMouseBounds();
            if (!bounds.Equals(mouseClipBounds)) { if (ClipCursor(ref bounds)) mouseClipBounds = bounds; else ReleaseMouseCapture(); }
        }
        if (mouseHint.IsOpen && (clock.Elapsed.TotalSeconds > mouseHintUntil || !nativeView.PresentationActive)) mouseHint.IsOpen = false;
    }
    void ShowMouseHint(string text)
    {
        mouseHintText.Text = text; mouseHint.HorizontalOffset = Math.Max(8, (image.ActualWidth - 370) / 2); mouseHint.VerticalOffset = 18;
        mouseHintUntil = clock.Elapsed.TotalSeconds + 3; mouseHint.IsOpen = IsLoaded && nativeView.PresentationActive;
    }
    void UpdateMouseUi()
    {
        if (mouseButton != null)
        {
            mouseButton.Background = mouseCaptured ? Brush(40, 100, 100) : Brush(30, 40, 54);
            mouseButton.ToolTip = mouseCaptured ? "Mouse captured · " + releaseShortcut + " to release" : "Mouse capture settings · " + releaseShortcut + " to release";
            AutomationProperties.SetName(mouseButton, mouseButton.ToolTip.ToString());
        }
        mouseHelp.Text = (preferences.Mouse.CaptureOnClick ? "Click the game to capture the mouse.\n" + releaseShortcut + ": free the mouse\n" : "Mouse capture is off.\n") + "F11: fullscreen\nCtrl+F10: free mouse & open panels\nFullscreen menu: hold the free pointer at the top for 1 second.";
    }
    void ShowMouseSettings()
    {
        ReleaseMouseCapture();
        if (mouseSettingsWindow != null) { mouseSettingsWindow.Activate(); return; }
        var dialog = mouseSettingsWindow = new Window { Owner = this, Title = "Mouse capture", Width = 570, Height = 510, MinWidth = 500, MinHeight = 470, Background = Background, Foreground = Foreground, Resources = Resources, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Label("Play freely. Leave easily.", 23));
        panel.Children.Add(Label("Click once to capture; that first click is kept out of the game. Your release shortcut works in every environment and quick run."));
        var capture = new CheckBox { Content = "Capture the mouse when I click the game", IsChecked = preferences.Mouse.CaptureOnClick }; panel.Children.Add(capture);
        var hide = new CheckBox { Content = "Hide the Windows pointer while captured", IsChecked = preferences.Mouse.HidePointer }; panel.Children.Add(hide);
        panel.Children.Add(Label("Release shortcut"));
        var shortcut = new ComboBox { IsEditable = true, ItemsSource = new[] { "Caps Lock", "F12", "Ctrl+Alt+M", "Ctrl+Shift+M", "Scroll Lock", "Escape" }, Text = releaseShortcut.ToString() }; panel.Children.Add(shortcut);
        panel.Children.Add(Label("Choose a shortcut or type one. It is reserved while captured. Caps Lock releases without changing Windows Caps Lock. Ctrl+F10 is always an additional exit."));
        panel.Children.Add(Label("Mouse sensitivity"));
        var sensitivity = new Slider { Minimum = .25, Maximum = 4, Value = Math.Clamp(preferences.Mouse.Sensitivity, .25, 4), TickFrequency = .25 };
        var gain = Label($"{sensitivity.Value:F2}×"); sensitivity.ValueChanged += (_, _) => gain.Text = $"{sensitivity.Value:F2}×"; panel.Children.Add(sensitivity); panel.Children.Add(gain);
        var error = Label(""); error.Foreground = Brushes.Salmon; panel.Children.Add(error);
        var buttons = new WrapPanel(); panel.Children.Add(buttons);
        buttons.Children.Add(Button("Save", () =>
        {
            try { var parsed = MouseShortcut.Parse(shortcut.Text); preferences.Mouse = new(capture.IsChecked == true, hide.IsChecked == true, parsed.ToString(), sensitivity.Value); releaseShortcut = parsed; preferences.Save(); UpdateMouseUi(); dialog.Close(); }
            catch (ArgumentException e) { error.Text = e.Message; }
        }));
        buttons.Children.Add(Button("Cancel", dialog.Close)); dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.Closed += (_, _) => mouseSettingsWindow = null; dialog.Show();
    }
    void DisposeMouseCapture()
    {
        ReleaseMouseCapture(); mouseHint.IsOpen = false; mouseSettingsWindow?.Close();
        if (mouseShortcutThreadId != 0) PostThreadMessage(mouseShortcutThreadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        mouseShortcutThread?.Join(1000); mouseShortcutThreadId = 0;
        mouseMessageSource?.RemoveHook(MouseMessages);
        if (rawMouseRegistered) RegisterRawInputDevices([new() { Page = 1, Usage = 2, Flags = 1 }], 1, (uint)Marshal.SizeOf<RawMouseDevice>());
        rawMouseRegistered = false;
    }
}
