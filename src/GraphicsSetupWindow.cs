using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace DosboxEnhanced;

public sealed class GraphicsSetupWindow : Window
{
    readonly TextBlock state=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,12)};
    readonly TextBlock progress=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)};
    readonly CheckBox model=new(){Content="Also download OpenNR compatibility runtime · 675 MB",Margin=new Thickness(0,8,0,8)};
    readonly ComboBox modelKind=new(){ItemsSource=new[]{"Original","Lightweight"},SelectedIndex=0,Margin=new Thickness(0,6,0,12)};
    readonly Button download=new(){Content="Download / repair missing graphics files",Padding=new Thickness(12,8,12,8)};
    CancellationTokenSource cancel=new();
    bool busy;
    public GraphicsSetupWindow()
    {
        Title="Graphics setup";Width=650;Height=820;MinHeight=640;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(24)};panel.Children.Add(new TextBlock{Text="Graphics files",FontSize=24});panel.Children.Add(state);
        panel.Children.Add(new TextBlock{Text="NVIDIA SR / Frame Generation are downloaded from NVIDIA's pinned Streamline release. Phoenix and the DOS engine are included in the portable app.",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(model);panel.Children.Add(new TextBlock{Text="Optional model download: community-modified OpenNR runtime from NODIX DLSS-5-MANAGER 1.3.7. Archive and DLL hashes are pinned. You can instead select your own signed NVIDIA model below. No models are bundled with this app.",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(download);download.Click+=async(_,_)=>await Run(async()=>{await GraphicsSetup.Install(Paths.Root,model.IsChecked==true,new Progress<string>(s=>progress.Text=s),cancel.Token);if(model.IsChecked==true)await ApplyModel();});
        var repair=new Button{Content="Repair bundled DOS / Phoenix engines",Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,8,0,0)};panel.Children.Add(repair);
        repair.Click+=async(_,_)=>await Run(async()=>await GraphicsSetup.RepairBundled(Paths.Root,new Progress<string>(s=>progress.Text=s),cancel.Token));
        panel.Children.Add(new TextBlock{Text="Model used by Phoenix",Margin=new Thickness(0,18,0,0)});panel.Children.Add(modelKind);
        var apply=new Button{Content="Use selected model",Padding=new Thickness(12,8,12,8)};panel.Children.Add(apply);apply.Click+=async(_,_)=>await Run(ApplyModel);
        var choose=new Button{Content="Choose existing model DLL…",Padding=new Thickness(12,8,12,8)};panel.Children.Add(choose);
        choose.Click+=async(_,_)=>{if(busy)return;var picker=new OpenFileDialog{Filter="Neural model (nvngx_dlssnr.dll)|nvngx_dlssnr.dll"};if(picker.ShowDialog(this)==true)await Run(async()=>await GraphicsSetup.ConfigureModel(Paths.Root,picker.FileName,modelKind.SelectedIndex==1?"small-fp8":"original"));};
        panel.Children.Add(progress);var restart=new Button{Content="Restart DOSBox Enhanced",Padding=new Thickness(12,8,12,8)};panel.Children.Add(restart);
        restart.Click+=(_,_)=>{if(busy)return;Process.Start(new ProcessStartInfo(Path.Combine(Paths.Root,"app/DosboxEnhanced.exe"),"--restart-after "+Environment.ProcessId){UseShellExecute=true,WorkingDirectory=Paths.Root});Application.Current.Shutdown();};
        Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};state.Text=GraphicsSetup.Inspect(Paths.Root);
        try{var active=JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Root,"graphics.json")));modelKind.SelectedIndex=active?["weight_source"]?["model"]?.GetValue<string>()=="small-fp8"?1:0;}catch{}
        Closing+=(_,e)=>{if(busy){cancel.Cancel();e.Cancel=true;progress.Text="Cancelling…";}};
    }
    async Task ApplyModel()
    {
        var config=JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.Root,"graphics.json")))!;
        string path=config["nr_dll"]?.GetValue<string>()??throw new InvalidOperationException("Download or choose a model DLL first.");
        await GraphicsSetup.ConfigureModel(Paths.Root,Path.GetFullPath(Path.Combine(Paths.Root,path)),modelKind.SelectedIndex==1?"small-fp8":"original");
    }
    async Task Run(Func<Task> action)
    {
        if(busy)return;cancel.Dispose();cancel=new();busy=true;download.IsEnabled=model.IsEnabled=modelKind.IsEnabled=false;
        try{await action();progress.Text="Files verified. Restart the app to use the selected runtime and model.";}
        catch(OperationCanceledException){progress.Text="Download cancelled. Verified installed files are retained.";}
        catch(Exception e){progress.Text=e.Message;}
        finally{busy=false;download.IsEnabled=model.IsEnabled=modelKind.IsEnabled=true;state.Text=GraphicsSetup.Inspect(Paths.Root);}
    }
}
