using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    PhoenixOptions advancedSettings = new();
    readonly StackPanel phoenixAdvanced = new();
    readonly Dictionary<string, Slider> phoenixSliders = new();
    readonly CheckBox temporalReuse = new() { Content = "Temporal reuse" }, autoMask = new() { Content = "Automatic conditioning mask" }, sceneDetection = new() { Content = "Detect scene changes" };
    readonly ComboBox priority = new(), monitoring = new();
    readonly ComboBox[] blockRepeats = Enumerable.Range(0,8).Select(_ => new ComboBox { MinWidth = 48, Margin = new Thickness(2) }).ToArray();
    bool loadingPhoenix;
    void RefreshStyleChoices()
    {
        string[] labels=renderer.SelectedIndex==1?["Default","Warm","Cool"]:["Default","Natural","Cinematic"];
        if(style.Items.Count==3&&Equals(style.Items[1],labels[1]))return;
        int index=Math.Max(0,style.SelectedIndex);bool prior=changing;changing=true;
        style.Items.Clear();foreach(string text in labels)style.Items.Add(text);style.SelectedIndex=index;changing=prior;
    }
    void BuildPhoenixPanel(StackPanel parent)
    {
        parent.Children.Insert(1, Button("Graphics setup / download files…", ShowGraphicsSetup));
        var basic = new StackPanel();
        basic.Children.Add(temporalReuse); basic.Children.Add(autoMask); basic.Children.Add(sceneDetection);
        AddPhoenixSlider(basic,"Skin", "Skin structure · −1 follows structure",-1,2,.1);
        AddPhoenixSlider(basic,"ColorStrength","Style color strength",0,1,.05);
        AddPhoenixSlider(basic,"NeuralScale","Neural processing size · %",10,100,5);
        AddPhoenixSlider(basic,"Blocks","Central block limit · fewer is faster",1,8,1);
        basic.Children.Add(Label("Block repeats · Original uses 8 / Lightweight uses 2",12));
        var repeats = new WrapPanel(); foreach (var box in blockRepeats) { foreach (int n in Enumerable.Range(0,5)) box.Items.Add(n); box.SelectedItem = 1; box.SelectionChanged += (_,_) => SavePhoenixPanel(); repeats.Children.Add(box); } basic.Children.Add(repeats);
        phoenixAdvanced.Children.Add(new Expander { Foreground=Brush(221,231,241), Header = "Phoenix · detail & temporal", Content = basic, IsExpanded = false, Margin = new Thickness(0,8,0,8) });
        var performance = new StackPanel();
        performance.Children.Add(Label("Priority")); foreach (string text in new[] { "Fixed quality", "Responsiveness", "Quality first" }) priority.Items.Add(text); performance.Children.Add(priority);
        AddPhoenixSlider(performance,"TargetFps","Processing budget · target FPS (0 off)",0,240,5);
        AddPhoenixSlider(performance,"MaxMilliseconds","Maximum processing time · ms (0 off)",0,100,1);
        performance.Children.Add(Label("Monitoring")); foreach (string text in new[] { "Off", "Basic", "Detailed GPU timings" }) monitoring.Items.Add(text); performance.Children.Add(monitoring);
        performance.Children.Add(Label("Budgets are targets, not an FPS guarantee. Detailed monitoring adds overhead.",12));
        phoenixAdvanced.Children.Add(new Expander { Foreground=Brush(221,231,241), Header = "Phoenix · performance", Content = performance, Margin = new Thickness(0,4,0,8) });
        var color = new StackPanel();
        AddPhoenixSlider(color,"Brightness","Brightness",-2,2,.05); AddPhoenixSlider(color,"Contrast","Contrast",0,4,.05);
        AddPhoenixSlider(color,"Saturation","Saturation",0,2,.05); AddPhoenixSlider(color,"Gamma","Gamma",.1,4,.05);
        color.Children.Add(Button("Reset Phoenix advanced settings", () => { LoadPhoenixPanel(new()); UpdateEffects(); }));
        phoenixAdvanced.Children.Add(new Expander { Foreground=Brush(221,231,241), Header = "Phoenix · color finishing", Content = color, Margin = new Thickness(0,4,0,8) });
        parent.Children.Insert(parent.Children.IndexOf(phoenixStatus), phoenixAdvanced);
        foreach (var box in new[] { temporalReuse,autoMask,sceneDetection }) { box.Checked += (_,_) => SavePhoenixPanel(); box.Unchecked += (_,_) => SavePhoenixPanel(); }
        priority.SelectionChanged += (_,_) => SavePhoenixPanel(); monitoring.SelectionChanged += (_,_) => SavePhoenixPanel();
        LoadPhoenixPanel(new());
    }
    void AddPhoenixSlider(StackPanel panel,string key,string title,double min,double max,double step)
    {
        var label = Label(title,12); var value = new TextBlock { Foreground = Brushes.LightSteelBlue, HorizontalAlignment = HorizontalAlignment.Right };
        var slider = new Slider { Minimum=min, Maximum=max, TickFrequency=step, IsSnapToTickEnabled=true, SmallChange=step, LargeChange=step*5, ToolTip=title };
        phoenixSliders[key]=slider; panel.Children.Add(label); panel.Children.Add(value); panel.Children.Add(slider);
        slider.ValueChanged += (_,_) => { value.Text=slider.Value.ToString("0.##"); if (!slider.IsMouseCaptureWithin) SavePhoenixPanel(); };
        slider.LostMouseCapture += (_,_) => SavePhoenixPanel();
        value.Text=slider.Value.ToString("0.##");
    }
    void AddQuickSlider(StackPanel panel,string text,Slider slider)
    {
        var row=new DockPanel();var number=new TextBlock{Foreground=Brush(157,180,204),HorizontalAlignment=HorizontalAlignment.Right,Text=slider.Value.ToString("0.00"),Margin=new Thickness(0,9,0,0)};
        DockPanel.SetDock(number,Dock.Right);row.Children.Add(number);row.Children.Add(Label(text));panel.Children.Add(row);panel.Children.Add(slider);
        slider.ValueChanged+=(_,_)=>number.Text=slider.Value.ToString("0.00");
    }
    void LoadPhoenixPanel(PhoenixOptions options)
    {
        loadingPhoenix=true; advancedSettings=options;
        var json=JsonSerializer.SerializeToElement(options);
        foreach (var (key,slider) in phoenixSliders) slider.Value=json.GetProperty(key).GetDouble();
        temporalReuse.IsChecked=options.Temporal; autoMask.IsChecked=options.AutoMask; sceneDetection.IsChecked=options.SceneDetection;
        priority.SelectedIndex=options.Priority; monitoring.SelectedIndex=options.Monitoring;
        var parts=options.Repeats.Split(','); for(int i=0;i<8;i++) blockRepeats[i].SelectedItem=int.TryParse(parts.ElementAtOrDefault(i),out int n)?Math.Clamp(n,0,4):1;
        loadingPhoenix=false;
    }
    void SavePhoenixPanel()
    {
        if (loadingPhoenix || changing || phoenixSliders.Count != 10) return;
        double V(string key)=>phoenixSliders[key].Value;
        advancedSettings=new(V("Skin"),autoMask.IsChecked==true,temporalReuse.IsChecked==true,V("NeuralScale"),(int)V("Blocks"),string.Join(',',blockRepeats.Select(b=>b.SelectedItem is int n?n:1)),
            V("ColorStrength"),V("TargetFps"),V("MaxMilliseconds"),Math.Max(0,priority.SelectedIndex),Math.Max(0,monitoring.SelectedIndex),sceneDetection.IsChecked==true,V("Brightness"),V("Contrast"),V("Saturation"),V("Gamma"));
        advancedSettings.Validate(); UpdateEffects();
    }
    void ShowGraphicsSetup()
    {
        ReleaseMouseCapture(); core?.ReleaseKeys();
        var setup=new GraphicsSetupWindow { Owner=this, Resources=Resources, Background=Brush(19,27,39), Foreground=Brush(221,231,241) }; setup.ShowDialog();
        // Reload the external paths on the worker; no DLL is loaded on the UI thread.
        status.Text="Graphics setup updated. Restart the app to apply runtime or model changes.";
    }
    internal async Task CaptureReleaseUi()
    {
        var output=System.IO.Path.Combine(Paths.Root,"tests/results");System.IO.Directory.CreateDirectory(output);
        SetAllPanels(true);foreach(var section in phoenixAdvanced.Children.OfType<Expander>().Take(1))section.IsExpanded=true;
        await Task.Delay(500);UpdateLayout();CaptureWindow(System.IO.Path.Combine(output,"phoenix-controls.png"));
        var setup=new GraphicsSetupWindow{Owner=this,Resources=Resources,Background=Brush(19,27,39),Foreground=Brush(221,231,241)};setup.Show();await Task.Delay(500);
        CaptureDialog(setup,System.IO.Path.Combine(output,"graphics-setup.png"));setup.Close();Close();
    }
}
