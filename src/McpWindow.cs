using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    readonly CheckBox mcpEnabled = new() { Content = "MCP", Margin = new Thickness(10,0,4,0), VerticalAlignment = VerticalAlignment.Center };
    McpServer? mcp;
    Window? mcpPanel;
    bool changingMcp;
    bool mcpStartupHandled;
    TextBlock? mcpPanelStatus;
    CheckBox? panelEnabled, panelRemote, panelControl;
    TextBox? panelPort, panelConfig;
    public McpServer Mcp => mcp!;
    void InitializeMcp()
    {
        mcp = new(this); changingMcp = true; mcpEnabled.IsChecked = preferences.Mcp.Enabled; changingMcp = false;
        mcpEnabled.Checked += async (_, _) => { if (!changingMcp) await ConfigureMcp(preferences.Mcp with { Enabled = true }); };
        mcpEnabled.Unchecked += async (_, _) => { if (!changingMcp) await ConfigureMcp(preferences.Mcp with { Enabled = false }); };
        Loaded += async (_, _) => { if (mcpStartupHandled) return; mcpStartupHandled = true; if (preferences.Mcp.Enabled) await ConfigureMcp(preferences.Mcp, false); };
    }
    async Task ConfigureMcp(McpSettings settings, bool save = true)
    {
        try { await mcp!.Configure(settings); preferences.Mcp = settings; if (save) preferences.Save(); }
        catch (Exception e) { preferences.Mcp = preferences.Mcp with { Enabled = false }; if (save) preferences.Save(); status.Text = "MCP: " + e.Message; }
        finally { changingMcp = true; mcpEnabled.IsChecked = preferences.Mcp.Enabled; if (panelEnabled != null) panelEnabled.IsChecked = preferences.Mcp.Enabled; changingMcp = false; UpdateMcpUi(); }
    }
    void UpdateMcpUi()
    {
        if (mcp == null) return;
        mcpEnabled.ToolTip = mcp.Status + "\n" + mcp.LastAction;
        if (mcpPanelStatus != null) mcpPanelStatus.Text = mcp.Status + "\n" + mcp.ConnectedClients + " connected client(s) · " + mcp.LastAction;
    }
    public void ShowMcpPanel()
    {
        ReleaseMouseCapture();
        if (mcpPanel != null) { mcpPanel.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(20) };
        var dialog = new Window { Owner = this, Title = "AI connection · DOSBox MCP", Width = 790, Height = 760, MinWidth = 650, MinHeight = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground, Resources = Resources };
        mcpPanel = dialog;
        panel.Children.Add(Label("Connect your AI to this DOS workspace", 22));
        panel.Children.Add(Label("Built in: no Python, Node, separate DOSBox fork or extra service. Leave this app open while your AI uses its tools."));
        panelEnabled = new() { Content = "Enable MCP server", IsChecked = preferences.Mcp.Enabled }; panel.Children.Add(panelEnabled);
        panelEnabled.Checked += async (_, _) => { if (!changingMcp) await ApplyPanelMcp(true); };
        panelEnabled.Unchecked += async (_, _) => { if (!changingMcp) await ConfigureMcp(preferences.Mcp with { Enabled = false }); };
        panelRemote = new() { Content = "Allow connections from other computers", IsChecked = preferences.Mcp.AllowRemote }; panel.Children.Add(panelRemote);
        panel.Children.Add(Label("Unchecked: this computer only. Checked: listen on this computer's network interfaces. Every connection requires the connection key. HTTP traffic is not encrypted."));
        panelControl = new() { Content = "Allow AI control (input, mouse, profiles, programs, settings and drives)", IsChecked = preferences.Mcp.AllowControl }; panel.Children.Add(panelControl);
        panel.Children.Add(Label("Unchecked: observe only. Control mode can type DOS commands and run programs; mounted folders are writable."));
        var portRow = new WrapPanel(); portRow.Children.Add(Label("Port")); panelPort = new() { Text = preferences.Mcp.Port.ToString(), Width = 90 }; portRow.Children.Add(panelPort); portRow.Children.Add(Button("Apply settings", () => _ = ApplyPanelMcp(panelEnabled.IsChecked == true))); panel.Children.Add(portRow);
        async void ModeChanged(object sender, RoutedEventArgs args) { if (!changingMcp) await ConfigureMcp(preferences.Mcp with { AllowRemote = panelRemote?.IsChecked == true, AllowControl = panelControl?.IsChecked == true }); }
        panelRemote.Checked += ModeChanged; panelRemote.Unchecked += ModeChanged; panelControl.Checked += ModeChanged; panelControl.Unchecked += ModeChanged;
        mcpPanelStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,8), Foreground = Brush(90,202,207) }; panel.Children.Add(mcpPanelStatus);
        panel.Children.Add(Label("1. Add this server to your AI client", 18));
        panel.Children.Add(Label("For a client with command / args fields, use the executable below and --mcp-stdio. It connects to this running app. For an HTTP client, copy the address and authorization configuration instead. On another computer, replace 127.0.0.1 with a copied remote address. Changing permissions or the key requires reconnecting your AI client."));
        panelConfig = new() { Text = McpInstructions.StdioConfiguration(), IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, Height = 158, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(panelConfig);
        var copies = new WrapPanel();
        copies.Children.Add(Button("Copy command / args config", () => { panelConfig.Text = McpInstructions.StdioConfiguration(); Clipboard.SetText(panelConfig.Text); }));
        copies.Children.Add(Button("Copy HTTP config + key", () => { panelConfig.Text = McpInstructions.HttpConfiguration(preferences.Mcp); Clipboard.SetText(panelConfig.Text); }));
        copies.Children.Add(Button("Copy remote addresses", () => Clipboard.SetText(string.Join("\n", RemoteAddresses(preferences.Mcp))))); panel.Children.Add(copies);
        panel.Children.Add(Button("Create a new connection key", () => _ = ConfigureMcp(preferences.Mcp with { Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant() })));
        panel.Children.Add(Label("2. Give this to the AI", 18));
        var instructions = new TextBox { Text = McpInstructions.Agent, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 128, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; panel.Children.Add(instructions);
        panel.Children.Add(Button("Copy instructions for AI", () => Clipboard.SetText(McpInstructions.Agent)));
        panel.Children.Add(Label("Available: status, environments, mapped files, matching original/enhanced screenshot pairs, screen text through built-in Windows OCR (or RAM when exposed), keys, mouse, pause/state control, audio, effects and all supported DOSBox INI settings. RAM reads depend on the core. Unavailable: Lua, debugger/CPU registers, port I/O and video recording."));
        dialog.Content = new ScrollViewer { Content = panel, Background = Background, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.Closed += (_, _) => { mcpPanel = null; mcpPanelStatus = null; panelEnabled = panelRemote = panelControl = null; panelPort = panelConfig = null; };
        UpdateMcpUi(); dialog.Show();
    }
    async Task ApplyPanelMcp(bool enabled)
    {
        if (panelPort == null || !int.TryParse(panelPort.Text, out int port) || port is <1024 or >65535) { if (mcpPanelStatus != null) mcpPanelStatus.Text = "Choose a port from 1024 to 65535."; return; }
        await ConfigureMcp(preferences.Mcp with { Enabled = enabled, Port = port, AllowRemote = panelRemote?.IsChecked == true, AllowControl = panelControl?.IsChecked == true });
    }
    static string[] RemoteAddresses(McpSettings settings) => settings.AllowRemote ? Dns.GetHostAddresses(Dns.GetHostName()).Where(ip=>ip.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip)).Select(ip=>$"http://{ip}:{settings.Port}/mcp").ToArray() : [settings.Url + " (remote connections are disabled)"];
    void StoreDosboxSettings(EnvironmentProfile profile, Dictionary<string, Dictionary<string,string>> settings)
    {
        var candidate = JsonSerializer.Deserialize<EnvironmentProfile>(JsonSerializer.Serialize(profile))!;
        // Keep the common controls synchronized when their values fit those controls.
        settings = DosboxIni.Parse(DosboxIni.Format(settings));
        if (!settings.TryGetValue("dosbox", out var existingDos) || !existingDos.ContainsKey("memsize")) candidate.Memory = 32;
        if (!settings.TryGetValue("cpu", out var existingCpu) || !existingCpu.ContainsKey("cycles")) candidate.Cycles = "auto";
        if (settings.TryGetValue("dosbox",out var dos) && dos.TryGetValue("memsize",out var size) && int.TryParse(size,out var memorySize) && memorySize is >=1 and <=1024) { candidate.Memory=memorySize; dos.Remove("memsize"); }
        if (settings.TryGetValue("cpu",out var cpu) && cpu.TryGetValue("cycles",out var value) && System.Text.RegularExpressions.Regex.IsMatch(value,"^(auto|max|[1-9][0-9]*)$")) { candidate.Cycles=value; cpu.Remove("cycles"); }
        candidate.DosboxSettings=settings; Configuration.Generate(candidate);
        profile.Memory=candidate.Memory; profile.Cycles=candidate.Cycles; if(int.TryParse(candidate.Cycles,out int manual))profile.ManualCycles=manual; profile.DosboxSettings=settings; library.Save();
        if (ReferenceEquals(selected,profile)) { changing=true; if (!memory.Items.Contains(profile.Memory)) memory.Items.Add(profile.Memory); memory.SelectedItem=profile.Memory; LoadCpuControls(profile); changing=false; }
    }
    EnvironmentProfile McpProfile(string id) => library.Environments.FirstOrDefault(p=>p.Id==id) ?? (running?.Id==id?running:throw new ArgumentException("Unknown environment ID. Use dosbox_list_environments."));
    object ProfileInfo(EnvironmentProfile p) => new { id=p.Id, name=p.Name, startup=p.Startup, drives=p.Drives.Select(d=>new { letter=d.Letter,label=d.Label,type=d.Type,folder=d.Folder }), settings=DosboxIni.Effective(p), effects=p.Effects,manualCycles=p.ManualCycles,discoverNewPrograms=p.DiscoverPrograms,programCount=p.Programs.Count };
    internal object McpEnvironments() => new { environments=library.Environments.Select(ProfileInfo).ToArray(), running=running==null?null:ProfileInfo(running) };
    internal object McpStatus() => new { running=running==null?null:ProfileInfo(running), ready=core?.IsReady??false, paused=core?.IsPaused??false, coreVersion=core?.CoreVersion,
        cpu=new { requested=core?.RequestedCpuCycles, liveControl=core?.LiveCpuCycles??false, acknowledgedUpdates=core?.CpuCycleUpdates??0 },
        selectedEnvironmentId=selected?.Id,frame=core?.Latest?.Sequence, width=core?.Latest?.Width,height=core?.Latest?.Height, memoryBytes=core?.MemoryBytes??0, audio=preferences.Audio, mouse=MouseInfo(),
        graphics=JsonNode.Parse(phoenix.RuntimeStatus), graphicsStatus=phoenix.Status, frameGeneration=phoenix.FrameGenerationStatus,display=new{fullscreen,panels,environment_panel=environmentVisible,effects_panel=effectsVisible,menu_visible=menu.Visibility==Visibility.Visible,aspect=aspectMode.SelectedIndex,sampling=scaler.SelectedIndex},
        mcp=new { enabled=preferences.Mcp.Enabled,allowRemote=preferences.Mcp.AllowRemote,allowControl=preferences.Mcp.AllowControl,status=mcp!.Status }, error=core?.Error };
    internal object McpConfiguration(string id) { var p=McpProfile(id); return new { environment=ProfileInfo(p),ini=Configuration.Generate(p),settingsIni=DosboxIni.Format(DosboxIni.Effective(p)),settingsCatalog=ConfigurationCatalog.Settings,coreOptionsCatalog=CoreCatalog().ToArray(), applies="Common CPU cycles apply live when available; other settings take effect at next launch/reload; [libretro] accepts dosbox_pure_ frontend options from the catalog. Export/import settingsIni; ini also includes managed drive/startup commands and cannot be imported as settings. Null removes overrides; replace or settings INI imports are supported." }; }
    static (Drive drive, string root, string host, string relative) Mapped(EnvironmentProfile p,string letter,string relative)
    {
        var drive=p.Drives.FirstOrDefault(d=>d.Letter.Equals(letter.TrimEnd(':'),StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("Drive is not mapped.");
        relative=relative.Replace('/','\\').TrimStart('\\');
        var pieces=relative.Split('\\',StringSplitOptions.RemoveEmptyEntries);
        if(pieces.Any(part=>part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars())>=0)) throw new ArgumentException("Use a relative DOS path inside the mapped drive.");
        var root=Path.GetFullPath(drive.Folder); var current=root; string prefix="";
        foreach(var piece in pieces)
        {
            current=Path.Combine(current,piece); prefix=prefix.Length==0?piece:prefix+"\\"+piece;
            if(Directory.Exists(current) && new DirectoryInfo(current).LinkTarget!=null)
            {
                var target=new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName??throw new ArgumentException("Broken mapped folder link.");
                bool curated=drive.DisplayNames.Keys.Any(k=>k.Equals(prefix,StringComparison.OrdinalIgnoreCase)) || drive.Launchers.Keys.Any(k=>k.Equals(prefix,StringComparison.OrdinalIgnoreCase));
                if(!curated && !target.StartsWith(Path.TrimEndingDirectorySeparator(root)+"\\",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("This folder link leaves the mapped drive.");
            }
            else if(File.Exists(current) && new FileInfo(current).LinkTarget!=null) throw new ArgumentException("File links are not accessible through MCP.");
        }
        return (drive,root,current,string.Join("\\",pieces));
    }
    internal object McpFiles(string id,string letter,string path)
    {
        var map=Mapped(McpProfile(id),letter,path); if(!Directory.Exists(map.host)) throw new ArgumentException("Folder not found.");
        var entries=new DirectoryInfo(map.host).EnumerateFileSystemInfos().OrderBy(f=>f is DirectoryInfo?0:1).ThenBy(f=>f.Name,StringComparer.OrdinalIgnoreCase).Take(201).ToArray();
        return new { drive=map.drive.Letter,path=map.relative,truncated=entries.Length>200,entries=entries.Take(200).Select(f=> {var rel=Path.GetRelativePath(map.root,f.FullName);return new { name=f.Name,displayName=Mapping(map.drive.DisplayNames,rel)??f.Name,directory=f is DirectoryInfo,path=map.drive.Letter+":\\"+rel,launcher=Mapping(map.drive.Launchers,rel) };}).ToArray() };
    }
    static string? Mapping(Dictionary<string,string> entries,string path) => entries.FirstOrDefault(p=>p.Key.Equals(path,StringComparison.OrdinalIgnoreCase)).Value;
    internal object McpCreateFolder(string id,string letter,string path,string folder)
    { var map=Mapped(McpProfile(id),letter,path); var created=DriveFolders.Create(map.root,map.host,folder); return new { created=map.drive.Letter+":\\"+Path.GetRelativePath(map.root,created) }; }
    internal object McpLaunch(string id,string path)
    {
        var profile=McpProfile(id);
        if(path.Length>0)
        {
            if(path.Length<3 || path[1]!=':') throw new ArgumentException("Use a DOS path beginning with a mapped drive letter.");
            var map=Mapped(profile,path[..1],path[2..]); var program=map.relative;
            if(Directory.Exists(map.host)) { var launcher=Mapping(map.drive.Launchers,map.relative)??throw new ArgumentException("This folder has no configured launcher; select an EXE/COM/BAT.");program=Path.Combine(program,launcher);map=Mapped(profile,map.drive.Letter,program); }
            if(!File.Exists(map.host)) throw new ArgumentException("DOS program not found.");
            profile=JsonSerializer.Deserialize<EnvironmentProfile>(JsonSerializer.Serialize(profile))!;profile.Startup=map.drive.Letter+":\\"+program;
        }
        Launch(profile); return new { launched=ProfileInfo(profile) };
    }
    internal async Task<object> McpConfigure(JsonObject args)
    {
        string id=Arg(args,"environment_id");var profile=McpProfile(id);
        if((args["settings"]!=null)==(args["ini"]!=null))throw new ArgumentException("Provide either settings or ini.");
        var settings=args["replace"]?.GetValue<bool>()==true?new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase):DosboxIni.Effective(profile);
        var changes=args["ini"] is JsonNode ini?JsonSerializer.SerializeToNode(DosboxIni.Parse(ini.GetValue<string>()))!.AsObject():args["settings"]!.AsObject();
        if(args["ini"]!=null)settings=new(StringComparer.OrdinalIgnoreCase);
        foreach(var section in changes)
        {
            string sectionName=section.Key.Trim().ToLowerInvariant();
            if(section.Value==null) {if(sectionName=="autoexec")throw new ArgumentException("Autoexec is managed by drives/startup.");settings.Remove(sectionName);continue;}
            if(section.Value is not JsonObject entries)throw new ArgumentException("Each section must be an object of setting strings or null.");
            foreach(var entry in entries)
            {
                if(entry.Value==null) {if(sectionName=="autoexec")throw new ArgumentException("Autoexec is managed by drives/startup.");if(settings.TryGetValue(sectionName,out var values))values.Remove(entry.Key.Trim().ToLowerInvariant());continue;}
                if(entry.Value is not JsonValue v || !v.TryGetValue<string>(out var value))throw new ArgumentException("DOSBox setting values must be strings or null.");
                DosboxIni.Set(settings,section.Key,entry.Key,value);
            }
        }
        foreach(var definition in ConfigurationCatalog.Settings)if(settings.TryGetValue(definition.Section,out var values)&&values.TryGetValue(definition.Key,out var value))ConfigurationCatalog.Validate(definition,value);
        StoreDosboxSettings(profile,settings); bool reloaded=args["restart"]?.GetValue<bool>()==true&&running?.Id==id;if(reloaded)Launch(profile);bool cpuAppliedLive=!reloaded&&await ApplyCpuCycles(profile);return new { saved=true,reloaded,cpuAppliedLive,settings=DosboxIni.Effective(profile) };
    }
    internal Task McpSession(string action)
    {
        if(core==null || running==null)throw new InvalidOperationException("No DOS session is running.");
        switch(action) { case "pause":ReleaseMouseCapture();core.Pause(true);break;case "resume":core.Pause(false);break;case "reset":ReleaseMouseCapture();core.Reset();phoenix.Reset();break;case "reload":Launch(McpProfile(running.Id));break;case "stop":Stop();break;case "save_state":return StateOperation(true);case "load_state":ReleaseMouseCapture();return StateOperation(false);default:throw new ArgumentException("Unknown session action."); }return Task.CompletedTask;
    }
    internal object McpAudio(JsonObject args) { if(args["volume"]!=null)volume.Value=args["volume"]!.GetValue<double>();if(args["muted"]!=null)muted.IsChecked=args["muted"]!.GetValue<bool>();preferences.Save();return preferences.Audio; }
    internal object McpDisplay(JsonObject args)
    {
        int Index(string key,string[] values) { var value=args[key]!.GetValue<string>();int index=Array.IndexOf(values,value);return index>=0?index:throw new ArgumentException("Invalid "+key+"; use "+string.Join(", ",values)); }
        int? aspect=args["aspect"]==null?null:Index("aspect",["dos","pixels","stretch"]),sampling=args["sampling"]==null?null:Index("sampling",["smooth","sharp","integer"]);
        bool targetFullscreen=args["fullscreen"]?.GetValue<bool>()??fullscreen;
        if(!targetFullscreen&&args["menu_visible"]?.GetValue<bool>()==false)throw new ArgumentException("The windowed toolbar stays visible; menu_visible hides only the fullscreen menu.");
        if(args["fullscreen"]!=null && fullscreen!=args["fullscreen"]!.GetValue<bool>())ToggleFullscreen();if(args["panels"]!=null){SetAllPanels(args["panels"]!.GetValue<bool>());if(fullscreen)ShowMenu(panels);}
        if(args["environment_panel"]!=null || args["effects_panel"]!=null) {ReleaseMouseCapture();environmentVisible=args["environment_panel"]?.GetValue<bool>()??environmentVisible;effectsVisible=args["effects_panel"]?.GetValue<bool>()??effectsVisible;SavePanePreferences();ApplyLayout();if(fullscreen&&panels)ShowMenu(true);}
        if(args["menu_visible"] is JsonNode visible)ShowMenu(visible.GetValue<bool>());
        if(aspect!=null)aspectMode.SelectedIndex=aspect.Value;if(sampling!=null)scaler.SelectedIndex=sampling.Value;preferences.Save();return new{fullscreen,panels,environment_panel=environmentVisible,effects_panel=effectsVisible,menu_visible=menu.Visibility==Visibility.Visible,aspect=aspectMode.SelectedIndex,sampling=scaler.SelectedIndex};
    }
    internal object McpEffects(JsonObject args)
    {
        if(args["renderer"]!=null && args["renderer"]!.GetValue<string>() is not ("phoenix" or "nvidia"))throw new ArgumentException("Renderer must be phoenix or nvidia.");
        if(args["upscale_order"]!=null && args["upscale_order"]!.GetValue<string>() is not ("before_nr" or "after_nr"))throw new ArgumentException("Upscale order must be before_nr or after_nr.");
        if(args["upscale_factor"]!=null && args["upscale_factor"]!.GetValue<int>() is <2 or >4)throw new ArgumentException("Upscale factor must be 2, 3 or 4.");
        if(args["input_scale"] is JsonNode resizeScale && (!double.IsFinite(resizeScale.GetValue<double>()) || resizeScale.GetValue<double>() is < .25 or > 4))throw new ArgumentException("Input scale must be between 0.25 and 4, including fractions.");
        if(args["input_filter"]!=null && args["input_filter"]!.GetValue<string>() is not ("nearest" or "linear"))throw new ArgumentException("Input filter must be nearest or linear.");
        PhoenixOptions? advanced = null;
        if(args["phoenix"] is JsonObject advancedJson) { advanced=System.Text.Json.JsonSerializer.Deserialize<PhoenixOptions>(advancedJson.ToJsonString(),new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive=true })!; advanced.Validate(); }
        changing=true;
        if(advanced!=null) LoadPhoenixPanel(advanced);
        if(args["input_resize"]!=null)inputResize.IsChecked=args["input_resize"]!.GetValue<bool>();
        if(args["input_scale"]!=null)inputScale.Value=args["input_scale"]!.GetValue<double>();
        if(args["input_filter"]!=null)inputFilter.SelectedIndex=args["input_filter"]!.GetValue<string>()=="linear"?1:0;
        if(args["upscale_order"]!=null)upscaleOrder.SelectedIndex=args["upscale_order"]!.GetValue<string>()=="before_nr"?1:0;
        if(args["upscale_factor"]!=null)upscaleFactor.SelectedItem=args["upscale_factor"]!.GetValue<int>();
        if(args["renderer"]!=null)renderer.SelectedIndex=args["renderer"]!.GetValue<string>()=="nvidia"?1:0;
        if(args["enabled"]!=null)enabled.IsChecked=args["enabled"]!.GetValue<bool>();if(args["intensity"]!=null)intensity.Value=args["intensity"]!.GetValue<double>();if(args["tone"]!=null)tone.Value=args["tone"]!.GetValue<double>();if(args["structure"]!=null)structure.Value=args["structure"]!.GetValue<double>();
        if(args["style"]!=null)style.SelectedIndex=args["style"]!.GetValue<int>();if(args["passes"]!=null)passes.SelectedItem=args["passes"]!.GetValue<int>();if(args["compare"]!=null)compare.IsChecked=args["compare"]!.GetValue<bool>();if(args["upscaling"]!=null)nvidiaUpscaling.IsChecked=args["upscaling"]!.GetValue<bool>();if(args["frame_generation"]!=null)nvidiaFrameGeneration.IsChecked=args["frame_generation"]!.GetValue<bool>();
        changing=false;UpdateEffects();if(running!=null)running.Effects=phoenix.Settings;return phoenix.Settings;
    }
    internal VideoFrame McpFrame(bool enhanced) => enhanced ? phoenix.Latest??throw new InvalidOperationException("Enhanced image not ready; request the original screenshot.") : core?.Latest??throw new InvalidOperationException("No DOS image available.");
}
