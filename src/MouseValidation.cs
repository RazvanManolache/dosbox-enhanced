using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct ValidationInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public int X;
        [FieldOffset(12)] public int Y;
        [FieldOffset(20)] public uint MouseFlags;
        [FieldOffset(8)] public ushort KeyCode;
        [FieldOffset(12)] public uint KeyFlags;
    }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, [In] ValidationInput[] inputs, int size);
    [DllImport("user32.dll")] static extern short GetKeyState(int key);
    static void SendValidationInput(params ValidationInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<ValidationInput>()) != inputs.Length) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    static ValidationInput KeyInput(int key, bool down) => new() { Type = 1, KeyCode = (ushort)key, KeyFlags = down ? 0u : 2u };
    static byte[] MouseProbeProgram()
    {
        var code = new List<byte>(); var addresses = new List<(int Offset, string Label, int Extra)>();
        void Emit(params byte[] bytes) => code.AddRange(bytes);
        void Address(string label, int extra = 0) { addresses.Add((code.Count, label, extra)); Emit(0, 0); }
        Emit(0xb8,0,0,0xcd,0x33,0xba); Address("text"); Emit(0xb4,9,0xcd,0x21);
        int wait = code.Count;
        Emit(0xb4,0,0xcd,0x16,0xb8,3,0,0xcd,0x33,0x89,0x1e); Address("state");
        Emit(0x89,0x0e); Address("state",2); Emit(0x89,0x16); Address("state",4);
        Emit(0xb8,0x40,0,0x8e,0xc0,0x31,0xc0,0x26,0xa0,0x17,0,0xa3); Address("state",6);
        Emit(0xba); Address("file"); Emit(0x31,0xc9,0xb4,0x3c,0xcd,0x21,0x89,0xc3,0xba); Address("state");
        Emit(0xb9,8,0,0xb4,0x40,0xcd,0x21,0xb4,0x3e,0xcd,0x21,0xeb); Emit(unchecked((byte)(wait - (code.Count + 1))));
        var labels = new Dictionary<string,int> { ["text"] = code.Count + 0x100 }; code.AddRange(System.Text.Encoding.ASCII.GetBytes("MOUSE TEST READY\r\n$"));
        labels["state"] = code.Count + 0x100; Emit(0,0,0,0,0,0,0,0); labels["file"] = code.Count + 0x100; code.AddRange(System.Text.Encoding.ASCII.GetBytes("STATE.BIN\0"));
        foreach (var patch in addresses) { int value = labels[patch.Label] + patch.Extra; code[patch.Offset] = (byte)value; code[patch.Offset+1] = (byte)(value >> 8); }
        return code.ToArray();
    }
    public async Task RunMouseValidation()
    {
        var checks = new List<string>(); var report = new Dictionary<string,object?>(); var mouseBefore = preferences.Mouse; var original = selected;
        bool capsBefore = (GetKeyState(20) & 1) != 0; GetCursorPos(out var cursorBefore); GetClipCursor(out var clipBefore);
        var profile = new EnvironmentProfile { Id = "mouse-test-" + Guid.NewGuid().ToString("N"), Name = "Mouse capture validation", Effects = new(Enabled:false,NvidiaUpscaling:false,NvidiaFrameGeneration:false), Startup = "C:\\MOUSEPOS.COM" };
        string folder = Path.Combine(Paths.Root,"tests","fixtures",profile.Id); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder,"MOUSEPOS.COM"),MouseProbeProgram()); profile.Drives.Add(new(){Folder=folder});
        var results = Path.Combine(Paths.Root,"tests","results"); Directory.CreateDirectory(results);
        void Check(bool condition,string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        async Task ClickGame()
        {
            FocusForValidation();
            PostMessage(nativeView.Surface,0x201,new IntPtr(1),new IntPtr(100|(100<<16))); await Task.Delay(70);
            PostMessage(nativeView.Surface,0x202,IntPtr.Zero,new IntPtr(100|(100<<16))); await Task.Delay(70);
            Check(mouseCaptured,"clicking the native game captures the mouse");
        }
        async Task<ushort[]> State()
        {
            string path = Path.Combine(folder,"STATE.BIN"); if (File.Exists(path)) File.Delete(path);
            core!.Key(32,true,32); core.Key(32,false);
            for (int i=0;i<100;i++)
            {
                await Task.Delay(20);
                try { if (File.Exists(path) && new FileInfo(path).Length == 8) { var bytes=File.ReadAllBytes(path); return [BitConverter.ToUInt16(bytes,0),BitConverter.ToUInt16(bytes,2),BitConverter.ToUInt16(bytes,4),BitConverter.ToUInt16(bytes,6)]; } }
                catch (IOException) { }
            }
            throw new Exception("DOS mouse probe did not write its state.");
        }
        async Task ReleaseWithCaps()
        { SendValidationInput(KeyInput(20,true),KeyInput(20,false)); await Task.Delay(100); }
        try
        {
            preferences.Mouse = new(); releaseShortcut = MouseShortcut.Parse("Caps Lock"); UpdateMouseUi();
            library.Environments.Add(profile); environments.SelectedItem=profile; Launch(profile);
            for (int i=0;i<100 && core?.Latest==null;i++) await Task.Delay(50);
            await Task.Delay(200); Check(core?.IsReady==true,"real DOS mouse probe started"); FocusForValidation();
            var before = await State(); await ClickGame();
            var firstClick = await State(); Check(firstClick[0]==0,"the capture click does not press a DOS mouse button");
            GetClipCursor(out var bounds); var desired = GameMouseBounds();
            Check(bounds.Left==desired.Left && bounds.Right==desired.Right && bounds.Top==desired.Top && bounds.Bottom==desired.Bottom,"Windows confines the pointer to the actual game picture");
            Check(nativeView.HidePointer && Mouse.OverrideCursor==Cursors.None && mouseHint.IsOpen,"capture hides the Windows pointer and displays the exit hint");
            var hintVisual=(FrameworkElement)mouseHint.Child!; hintVisual.UpdateLayout(); var hintImage=new RenderTargetBitmap((int)Math.Ceiling(hintVisual.ActualWidth),(int)Math.Ceiling(hintVisual.ActualHeight),96,96,PixelFormats.Pbgra32); hintImage.Render(hintVisual); var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(hintImage));using(var output=File.Create(Path.Combine(results,"mouse-capture-hint.png")))encoder.Save(output);
            long rawBefore = rawMousePackets;
            SendValidationInput(new ValidationInput(){Type=0,X=80,Y=20,MouseFlags=1}); await Task.Delay(150);
            var moved = await State(); report["rawPacketsDuringMove"]=rawMousePackets-rawBefore; report["dosBefore"]=before; report["dosAfterMove"]=moved;
            Check(rawMousePackets>rawBefore && forwardedMouseMoves>0 && (moved[1]!=firstClick[1] || moved[2]!=firstClick[2]),"real Windows raw mouse packets move the DOS mouse driver");
            PostMessage(nativeView.Surface,0x207,new IntPtr(16),new IntPtr(100|(100<<16))); await Task.Delay(60); Check((await State())[0]==4,"middle mouse is delivered as its own DOS button"); PostMessage(nativeView.Surface,0x208,IntPtr.Zero,new IntPtr(100|(100<<16))); await Task.Delay(50);
            PostMessage(nativeView.Surface,0x201,new IntPtr(1),new IntPtr(100|(100<<16))); await Task.Delay(80);
            Check((await State())[0]==1,"subsequent game clicks reach DOS while captured");
            await ReleaseWithCaps(); Check(!mouseCaptured && Mouse.OverrideCursor==null && !nativeView.HidePointer,"Caps Lock releases capture and restores the visible pointer");
            Check(((GetKeyState(20)&1)!=0)==capsBefore,"release shortcut leaves Windows Caps Lock unchanged");
            GetClipCursor(out var free); Check(free.Left==clipBefore.Left && free.Right==clipBefore.Right && free.Top==clipBefore.Top && free.Bottom==clipBefore.Bottom,"release removes Windows cursor confinement");
            await ClickGame();
            using (var started = new ManualResetEventSlim())
            {
                var unlock = Task.Run(() =>
                {
                    started.Set(); SendValidationInput(KeyInput(20,true),KeyInput(20,false));
                    for (int i=0;i<30;i++) { GetClipCursor(out var clip); if (clip.Left==clipBefore.Left && clip.Right==clipBefore.Right && clip.Top==clipBefore.Top && clip.Bottom==clipBefore.Bottom) return true; Thread.Sleep(5); }
                    return false;
                });
                started.Wait(TimeSpan.FromSeconds(1)); Thread.Sleep(250);
                Check(await unlock,"the release shortcut unlocks Windows even while the drawing thread is blocked"); await Task.Delay(100);
                Check(!mouseCaptured,"drawing-thread recovery completes mouse release cleanly");
            }
            var released = await State(); Check(released[0]==0 && (released[3]&64)==(before[3]&64),"release clears a held DOS mouse button and does not toggle DOS Caps Lock");
            SendValidationInput(new ValidationInput(){Type=0,X=50,Y=15,MouseFlags=1}); await Task.Delay(100); var freeMove=await State(); Check(freeMove[1]==released[1] && freeMove[2]==released[2],"free-pointer motion stays out of the game");
            ShowMouseSettings(); await Task.Delay(100); CaptureDialog(mouseSettingsWindow!,Path.Combine(results,"mouse-settings.png"));
            var selector=Descendants<ComboBox>(mouseSettingsWindow!).Single(); selector.Text="Ctrl+Alt+M";
            Descendants<Button>(mouseSettingsWindow!).Single(b=>Equals(b.Content,"Save")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(preferences.Mouse.ReleaseShortcut=="Ctrl+Alt+M" && ApplicationPreferences.Load().Mouse.ReleaseShortcut=="Ctrl+Alt+M","mouse settings save the user-configured shortcut across restart");
            await ClickGame();
            SendValidationInput(KeyInput(123,true),KeyInput(123,false)); await Task.Delay(100); Check(mouseCaptured,"unconfigured function keys remain available to the game");
            SendValidationInput(KeyInput(17,true),KeyInput(18,true),KeyInput(77,true),KeyInput(77,false),KeyInput(18,false),KeyInput(17,false)); await Task.Delay(100);
            Check(!mouseCaptured,"the configured Ctrl+Alt+M chord releases through the Windows keyboard hook");
            await ClickGame(); volume.Focus(); await Task.Delay(60); Check(!mouseCaptured,"moving keyboard focus to a menu control releases the mouse");
            await ClickGame(); core!.Pause(true); await Task.Delay(70); Check(!mouseCaptured,"pausing releases the mouse"); core.Pause(false);
            await ClickGame(); ShowDosboxSettings(); await Task.Delay(60); Check(!mouseCaptured,"opening configuration releases the mouse"); configurationWindow!.Close();
            await ClickGame(); var other=new Window{Owner=this,Width=180,Height=90,Title="Focus-loss validation"};other.Show();await Task.Delay(100);Check(!mouseCaptured,"switching to another window releases capture");other.Close();FocusForValidation();
            preferences.Mouse=new();releaseShortcut=MouseShortcut.Parse("Caps Lock");UpdateMouseUi(); ToggleFullscreen();await Task.Delay(150);await ClickGame();
            Check(!TestTopPointer(400,0,0) && !TestTopPointer(400,0,1.1),"captured motion cannot accidentally reveal the top-edge menu");
            await ReleaseWithCaps();Check(!mouseCaptured && fullscreenMenu.IsOpen && menu.Visibility==Visibility.Visible,"fullscreen release immediately makes the menu available");
            await ClickGame(); SendValidationInput(KeyInput(17,true),KeyInput(121,true),KeyInput(121,false),KeyInput(17,false));await Task.Delay(100);Check(!mouseCaptured && fullscreenMenu.IsOpen,"Ctrl+F10 remains an emergency release and menu shortcut");
            ToggleFullscreen();await Task.Delay(100);await ClickGame(); Stop();Check(!mouseCaptured,"stopping the DOS session releases the mouse");
            report["passed"]=true;
        }
        catch (Exception e) { report["passed"]=false;report["error"]=e.ToString(); }
        finally
        {
            ReleaseMouseCapture(); Stop(); mouseSettingsWindow?.Close(); configurationWindow?.Close();if(fullscreen)ToggleFullscreen();
            preferences.Mouse=mouseBefore;try{releaseShortcut=MouseShortcut.Parse(mouseBefore.ReleaseShortcut);}catch{releaseShortcut=new(Key.CapsLock,ModifierKeys.None);}UpdateMouseUi();preferences.Save();
            library.Environments.Remove(profile);library.Save();environments.SelectedItem=original;
            if (((GetKeyState(20)&1)!=0)!=capsBefore) SendValidationInput(KeyInput(20,true),KeyInput(20,false));
            SetCursorPos(cursorBefore.X,cursorBefore.Y);report["checks"]=checks;report["count"]=checks.Count; Paths.AtomicWrite(Path.Combine(results,"mouse-validation.json"),JsonSerializer.Serialize(report,Paths.Json));Close();
        }
    }
}
