using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    void CaptureCheatDock(string path){
        toolsTabs.UpdateLayout();var size=new Size(toolsTabs.ActualWidth,toolsTabs.ActualHeight);var drawing=new DrawingVisual();
        using(var context=drawing.RenderOpen())context.DrawRectangle(new VisualBrush(toolsTabs){Stretch=Stretch.Fill},null,new Rect(size));
        var target=new RenderTargetBitmap((int)Math.Ceiling(size.Width),(int)Math.Ceiling(size.Height),96,96,PixelFormats.Pbgra32);target.Render(drawing);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(target));using var stream=File.Create(path);encoder.Save(stream);
    }
    internal async Task RunCheatUiValidation()
    {
        var checks=new List<string>();var report=new Dictionary<string,object?>();
        void Check(bool value,string message){if(!value)throw new Exception(message);checks.Add(message);}
        var savedEnvironmentVisible=preferences.EnvironmentVisible;var savedEffectsVisible=preferences.EffectsVisible;var savedToolsWidth=preferences.ToolsWidth;var savedToolsTab=preferences.ToolsTab;
        try {
            timer.Stop(); // The emulator runs, but this UI test does not submit frames to the graphics worker.
            WindowState=WindowState.Normal;ShowActivated=false;ShowCheats();await Task.Delay(300);UpdateLayout();
            Check(cheatPanel is {IsVisible:true}&&toolsTabs.SelectedIndex==1&&IsEnabled&&Application.Current.Windows.OfType<Window>().Count()==1,"cheats opens in the right dock without a floating window");
            Check((string)cheatType.SelectedValue=="u16"&&cheatType.Text.Contains("16-bit"),"numeric type picker uses readable byte-size labels");
            Check(cheatFilter.Items.Count==12&&cheatType.Items.Count==10,"all numeric types and scan filters are available");
            Check(cheatTabs.Items.Count==4&&pointerMode.Items.Count==4,"pointer chain and disassembly tabs expose all four DOS pointer formats");
            cheatFilter.SelectedValue="unknown";Check(!cheatValue.IsEnabled&&!cheatUpper.IsEnabled,"unknown initial scan needs no numeric input");
            cheatFilter.SelectedValue="between";Check(cheatValue.IsEnabled&&cheatUpper.IsEnabled,"between scan enables both numeric bounds");
            cheatFilter.SelectedValue="exact";await ScanCheats(true);
            Check(cheatMessage.Text.Contains("Launch a DOS program"),"scanning without a running game gives an inline explanation");
            var folder=Path.Combine(Paths.Root,"tests/results");Directory.CreateDirectory(folder);CaptureCheatDock(Path.Combine(folder,"cheat-panel.png"));
            cheatValue.Text="draft";double initialGameWidth=stage.ActualWidth;SetToolsWidth(620);UpdateLayout();
            Check(stage.ActualWidth<initialGameWidth-100&&toolsTabs.ActualWidth>580,"resizing the dock adjusts the game area without covering it");
            SetToolsWidth(480);ShowToolTab(0);await Task.Delay(70);Check(!cheatTimer!.IsEnabled&&toolsTabs.SelectedIndex==0,"switching to DLSS suspends cheat polling");
            ShowCheats();Check(cheatValue.Text=="draft"&&cheatTimer.IsEnabled,"tab switching preserves the cheat search form");
            ShowToolTab(1,true);UpdateLayout();Check(!effectsVisible&&!cheatTimer.IsEnabled,"the active toolbar icon hides the dock and stops polling");
            ShowCheats();await Task.Delay(100);Check(effectsVisible&&cheatPanel!.IsVisible&&cheatType.Parent!=null,"the dock reopens with the same controls and state");
            Width=1050;Height=700;UpdateLayout();await Task.Delay(100);
            Check(stage.ActualWidth>=300&&cheatResults.ActualHeight>=80&&cheatType.ActualWidth<250,"narrow-window layout keeps both the game and compact scan results usable");
            CaptureElement(root,Path.Combine(folder,"cheat-dock-narrow.png"));Width=1440;Height=920;environmentVisible=false;ApplyLayout();UpdateLayout();
            Check(menu.Children.OfType<Button>().Any(b=>b.ToolTip?.ToString()?.StartsWith("Cheats ·")==true),"icon toolbar exposes the cheat panel");
            string fixture=Path.Combine(Paths.Root,"tests/fixtures/cheats");Directory.CreateDirectory(fixture);
            File.WriteAllBytes(Path.Combine(fixture,"CHEAT.COM"),[0xc7,0x06,0,2,0x35,0xa1,0x30,0xe4,0xcd,0x16,0xff,0x0e,0,2,0xeb,0xf6]);
            running=new EnvironmentProfile{Id="cheat-ui-validation",Name="Cheat UI validation",Startup="C:\\CHEAT.COM",Drives=[new(){Folder=fixture}]};
            string config=Path.Combine(fixture,"ui.conf");File.WriteAllText(config,Configuration.Generate(running));core=new(config,Path.Combine(fixture,"ui-saves"),sound:false);
            for(int i=0;i<100;i++){await Task.Delay(50);if(core.Error!=null)throw new Exception(core.Error);if(core.IsReady&&core.MemoryBytes>0)break;}
            await RefreshCheatPanel();if(core.Latest is {} frame){welcome.Visibility=Visibility.Collapsed;Present(frame);}var session=Cheats();
            foreach(var old in session.Watches.ToArray())await session.Remove(old.Id,CancellationToken.None);
            cheatType.SelectedValue="u16";cheatFilter.SelectedValue="exact";cheatValue.Text="41269";await ScanCheats(true);
            Check(session.Search.Count==2&&cheatResults.Items.Count==2,"Search tab displays both matches from real DOS RAM");
            core.Key(97,true,97,0);await Task.Delay(70);core.Key(97,false,0,0);await Task.Delay(130);
            cheatFilter.SelectedValue="decreased_by";cheatValue.Text="1";await ScanCheats(false);
            Check(session.Search.Count==1&&cheatResults.Items.Count==1,"Next scan narrows the displayed results after gameplay input");
            cheatResults.SelectedIndex=0;await AddCheatResult();var watch=session.Watches.Single();
            var tabs=cheatTabs;tabs.SelectedIndex=1;await Task.Delay(100);
            cheatWatches.SelectedItem=watch;cheatName.Text="Health";cheatTarget.Text="777";await EditCheatWatch();
            Check(watch.Name=="Health"&&BitConverter.ToUInt16(await core.ReadMemory((int)watch.Address,2,CancellationToken.None))==777,"Watches tab names and edits an actual guest value");
            cheatLock.IsChecked=true;for(int i=0;i<60&&!watch.Frozen;i++)await Task.Delay(30);
            Check(watch.Frozen,"lock checkbox enables the live memory lock");
            cheatTarget.Text="invalid draft";cheatLock.IsChecked=false;for(int i=0;i<60&&watch.Frozen;i++)await Task.Delay(30);
            Check(!watch.Frozen,"unlock works even when the target editor contains an invalid unsaved draft");
            core.Key(97,true,97,0);await Task.Delay(70);core.Key(97,false,0,0);await Task.Delay(130);
            Check(BitConverter.ToUInt16(await core.ReadMemory((int)watch.Address,2,CancellationToken.None))==776,"gameplay changes the value again after the UI unlocks it");
            cheatTarget.Text="777";cheatLock.IsChecked=true;for(int i=0;i<60&&(pendingCheatLocks>0||!watch.Frozen);i++)await Task.Delay(30);
            await RefreshCheatPanel();ShowToolTab(0);await Task.Delay(60);
            Check(core.ActiveCheatLocks==1&&watch.Frozen,"switching to DLSS leaves active memory locks running");ShowCheats();
            CaptureCheatDock(Path.Combine(folder,"cheat-watches.png"));
            long pointerRoot=watch.Address+0x100;var region=(await core.GetRamRegions(CancellationToken.None)).Single(r=>watch.Address>=r.Start&&watch.Address<r.Start+r.Length);
            await core.SetCheatValue("ui-pointer",pointerRoot,BitConverter.GetBytes(checked((uint)(region.PhysicalStart!.Value+watch.Address-region.Start))),false,core.MemoryRevision,CancellationToken.None);
            pointerTarget.Text=watch.AddressText;pointerDepth.Text="1";pointerOffset.Text="0";tabs.SelectedIndex=2;await ScanPointerUi(false);
            Check(pointerResults.Items.Count>0&&session.Pointers!.Candidates.Any(c=>c.Path.BaseAddress==pointerRoot),"Pointer chains tab finds the real guest pointer");
            pointerResults.SelectedItem=session.Pointers!.Candidates.First(c=>c.Path.BaseAddress==pointerRoot);await AddPointerUi();var dynamicWatch=session.Watches.Single(w=>w.Pointer!=null);
            Check(dynamicWatch.Address==watch.Address&&dynamicWatch.Current=="777","Add chain creates a saved dynamic watch through the UI");
            tabs.SelectedIndex=2;UpdateLayout();CaptureCheatDock(Path.Combine(folder,"cheat-pointers.png"));
            tabs.SelectedIndex=3;disasmAddress.Text="0x"+(watch.Address-0x100).ToString("X");await DisassembleUi();
            Check(disasmResults.ItemsSource is DisassemblyLine[] decoded&&decoded.Length>0&&decoded[0].Instruction.Contains("mov"),"Disassembly tab decodes the running DOS fixture");
            CaptureCheatDock(Path.Combine(folder,"cheat-disassembly.png"));await session.Remove(dynamicWatch.Id,CancellationToken.None);session.ClearPointers();
            await session.Remove(watch.Id,CancellationToken.None);tabs.SelectedIndex=0;CaptureCheatDock(Path.Combine(folder,"cheat-panel.png"));
            UpdateLayout();CaptureElement(root,Path.Combine(folder,"cheat-dock-workspace.png"));
            ShowToolTab(0);UpdateLayout();CaptureCheatDock(Path.Combine(folder,"dlss-dock.png"));
            report["passed"]=true;
        }catch(Exception e){report["passed"]=false;report["error"]=e.ToString();}
        finally{preferences.EnvironmentVisible=savedEnvironmentVisible;preferences.EffectsVisible=savedEffectsVisible;preferences.ToolsWidth=savedToolsWidth;preferences.ToolsTab=savedToolsTab;environmentVisible=savedEnvironmentVisible;effectsVisible=savedEffectsVisible;toolsTabs.SelectedIndex=savedToolsTab=="cheats"?1:0;ApplyLayout();report["checks"]=checks;Paths.AtomicWrite(Path.Combine(Paths.Root,"tests/results/cheat-ui.json"),JsonSerializer.Serialize(report,Paths.Json));Close();}
    }
}
