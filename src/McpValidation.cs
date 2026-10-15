using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    public async Task RunMcpValidation()
    {
        var checks=new List<string>(); var report=new Dictionary<string,object?>();
        var original=preferences.Mcp; var audioBefore=preferences.Audio;var mouseBefore=preferences.Mouse;var selectedBefore=selected;var envPanelBefore=preferences.EnvironmentVisible;var effectsPanelBefore=preferences.EffectsVisible;
        var addedEnvironments=new List<string>();
        string deniedLink=Path.Combine(Paths.Root,"tests","fixtures","mcp","UNAPPROV");
        var prefPath=Path.Combine(Paths.Data,"preferences.json"); var originalPrefs=File.Exists(prefPath)?File.ReadAllBytes(prefPath):null;
        var fixture=Path.Combine(Paths.Root,"tests","fixtures","mcp");Directory.CreateDirectory(fixture);
        var greeting=Encoding.ASCII.GetBytes("MCP DOS READY\r\n$");var verified=Encoding.ASCII.GetBytes("KEYBOARD VERIFIED\r\n$");
        var program=new List<byte>{0xba,0x14,0x01,0xb4,0x09,0xcd,0x21,0xb4,0x00,0xcd,0x16,0xba,(byte)(0x14+greeting.Length),0x01,0xb4,0x09,0xcd,0x21,0xeb,0xf3};program.AddRange(greeting);program.AddRange(verified);File.WriteAllBytes(Path.Combine(fixture,"VERIFY.COM"),program.ToArray());
        var profile=new EnvironmentProfile { Id="mcp-test-"+Guid.NewGuid().ToString("N"),Name="MCP integration validation", Startup="C:\\VERIFY.COM",Effects=new(Enabled:false,NvidiaUpscaling:false,NvidiaFrameGeneration:false),Drives=[new(){Folder=fixture},new(){Letter="D",Folder=Path.Combine(Paths.Root,"tests","fixtures","drive-d")}] };
        Directory.CreateDirectory(profile.Drives[1].Folder);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var settings=original with {Enabled=true,AllowRemote=false,AllowControl=true,Port=port};
        using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(20)};
        string session="";int nextId=0;
        void Check(bool condition,string name) {if(!condition)throw new Exception(name);checks.Add(name);}
        async Task<(int status,JsonObject? body)> Request(string method,JsonObject? parameters=null,bool notify=false,string? auth=null,string? origin=null,string? url=null,string? customVersion=null)
        {
            var request=new JsonObject{["jsonrpc"]="2.0",["method"]=method};if(!notify)request["id"]=++nextId;if(parameters!=null)request["params"]=parameters;
            using var message=new HttpRequestMessage(HttpMethod.Post,url??settings.Url){Content=new StringContent(request.ToJsonString(),Encoding.UTF8,"application/json")};
            message.Headers.Accept.ParseAdd("application/json, text/event-stream");if(auth!="none")message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",auth??settings.Token);
            if(session.Length>0)message.Headers.Add("Mcp-Session-Id",session);message.Headers.Add("MCP-Protocol-Version",customVersion??"2025-11-25");if(origin!=null)message.Headers.Add("Origin",origin);
            using var response=await client.SendAsync(message);if(response.Headers.TryGetValues("Mcp-Session-Id",out var h))session=h.First();var body=await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode,body.StartsWith('{')?JsonNode.Parse(body) as JsonObject:null);
        }
        async Task<JsonObject> Tool(string name,object? args=null)
        {var response=await Request("tools/call",new JsonObject{["name"]=name,["arguments"]=JsonSerializer.SerializeToNode(args??new{})});Check(response.status==200,"HTTP tool response: "+name);return response.body?["result"] as JsonObject??throw new Exception("Missing MCP result for "+name+": "+(response.body?.ToJsonString()??"empty response"));}
        try
        {
            library.Environments.Add(profile);library.Save();environments.SelectedItem=profile;Launch(profile);
            for(int i=0;i<100&&core?.Latest==null;i++)await Task.Delay(50);
            Check(core?.IsReady==true,"real DOS core started");report["core_memory_bytes"]=core!.MemoryBytes;
            await ConfigureMcp(settings);Check(mcp!.Settings.Enabled&&!mcp.Settings.AllowRemote,"local-only server enabled");
            Check((await Request("ping",auth:"none")).status==401,"missing token rejected");
            Check((await Request("ping",auth:new string('0',64))).status==401,"wrong token rejected");
            Check((await Request("ping",origin:"http://evil.example")).status==403,"foreign browser origin rejected");
            Check((await Request("ping",customVersion:"invalid")).status==400,"unsupported protocol rejected");
            var initialized=await Request("initialize",new JsonObject{["protocolVersion"]="2025-11-25",["capabilities"]=new JsonObject(),["clientInfo"]=new JsonObject{["name"]="self-test",["version"]="1"}});
            Check(initialized.body?["result"]?["protocolVersion"]?.GetValue<string>()=="2025-11-25"&&session.Length>0,"MCP initialization and session header");
            Check((await Request("tools/list")).body?["error"]!=null,"initialization notification required");
            Check((await Request("notifications/initialized",notify:true)).status==202,"initialized notification accepted");
            var listed=await Request("tools/list");Check(((JsonArray)listed.body!["result"]!["tools"]!).Count==23,"all twenty-three tools discovered");
            Check((await Tool("dosbox_status"))["structuredContent"]?["ready"]?.GetValue<bool>()==true,"live status available");
            Check((await Tool("dosbox_list_files",new{environment_id=profile.Id,drive="C"}))["isError"]?.GetValue<bool>()==false,"mapped folder browsing");
            Check((await Tool("dosbox_list_files",new{environment_id=profile.Id,drive="C",path="..\\.."}))["isError"]?.GetValue<bool>()==true,"path traversal rejected");
            Check((await Tool("dosbox_audio",new{volume=101}))["isError"]?.GetValue<bool>()==true,"numeric bounds rejected");
            var shot=await Tool("dosbox_screenshot");var encoded=shot["content"]![1]!["data"]!.GetValue<string>();var bytes=Convert.FromBase64String(encoded);Check(bytes.Take(8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}),"screenshot is valid PNG image content");File.WriteAllBytes(Path.Combine(Paths.Root,"tests","results","mcp-screen.png"),bytes);
            var before=SHA256.HashData(core.Latest!.Pixels);Check((await Tool("dosbox_press_keys",new{keys=new[]{"a"},hold_ms=80}))["isError"]!.GetValue<bool>()==false,"keyboard tool completed");await Task.Delay(250);Check(!before.SequenceEqual(SHA256.HashData(core.Latest!.Pixels)),"MCP keyboard changed real DOS screen");
            var text=await Tool("dosbox_read_text");report["text_result"]=text.DeepClone();Check(!text["isError"]!.GetValue<bool>()&&text["structuredContent"]?["text"]?.GetValue<string>().Contains("VERIFIED",StringComparison.OrdinalIgnoreCase)==true,"real DOS screen text read through native Windows OCR/RAM");
            Check(!(await Tool("dosbox_type_text",new{text="abc",delay_ms=15}))["isError"]!.GetValue<bool>(),"ASCII typing tool");
            Check(!(await Tool("dosbox_mouse",new{dx=10,dy=-5,button="left"}))["isError"]!.GetValue<bool>(),"mouse tool stays inside DOS");
            var gains=await Tool("dosbox_audio",new{volume=50,muted=false});await Task.Delay(100);Check(!gains["isError"]!.GetValue<bool>()&&core.ActualAudioGain==.5,"audio tool affects live PCM gain");
            Check(!(await Tool("dosbox_display",new{aspect="pixels",sampling="sharp"}))["isError"]!.GetValue<bool>()&&scaler.SelectedIndex==1&&aspectMode.SelectedIndex==1,"display tool controls actual view");
            Check(!(await Tool("dosbox_effects",new{intensity=.3,tone=.8,upscaling=true,frame_generation=false}))["isError"]!.GetValue<bool>()&&phoenix.Settings.Intensity==.3&&!phoenix.Settings.NvidiaFrameGeneration,"graphics tool updates actual effect settings");
            Check(!(await Tool("dosbox_effects",new{upscale_order="before_nr",upscale_factor=3}))["isError"]!.GetValue<bool>()&&phoenix.Settings.UpscaleOrder=="before_nr"&&phoenix.Settings.UpscaleFactor==3,"MCP selects SR before NR and the native-resolution multiplier");
            Check((await Tool("dosbox_effects",new{upscale_order="invalid"}))["isError"]!.GetValue<bool>()&&phoenix.Settings.UpscaleOrder=="before_nr","invalid processing order is rejected without changing settings");
            Check((await Tool("dosbox_effects",new{upscale_factor=5}))["isError"]!.GetValue<bool>()&&phoenix.Settings.UpscaleFactor==3,"invalid upscale factor is rejected without changing settings");
            Check(!(await Tool("dosbox_effects",new{upscaling=false,upscale_order="after_nr",upscale_factor=4}))["isError"]!.GetValue<bool>()&&!phoenix.Settings.NvidiaUpscaling&&phoenix.Settings.UpscaleOrder=="after_nr","MCP can skip upscaling and restore NR-first ordering");
            Check(!(await Tool("dosbox_effects",new{input_resize=true,input_scale=1.25,input_filter="linear"}))["isError"]!.GetValue<bool>()&&phoenix.Settings.InputResize&&phoenix.Settings.InputScale==1.25&&phoenix.Settings.InputFilter=="linear"&&!phoenix.Settings.NvidiaUpscaling,"MCP controls optional input resize independently of SR");
            Check((await Tool("dosbox_effects",new{input_filter="invalid"}))["isError"]!.GetValue<bool>()&&phoenix.Settings.InputFilter=="linear","invalid input filter is rejected without changing effects");
            Check((await Tool("dosbox_effects",new{input_scale=5}))["isError"]!.GetValue<bool>()&&phoenix.Settings.InputScale==1.25,"invalid input size is rejected without changing effects");
            Check((await Tool("dosbox_effects",new{input_scale=.1}))["isError"]!.GetValue<bool>()&&phoenix.Settings.InputScale==1.25,"MCP rejects resize below the fractional slider range");
            Check(!(await Tool("dosbox_effects",new{input_scale=.75}))["isError"]!.GetValue<bool>()&&phoenix.Settings.InputScale==.75,"MCP supports fractional downscaling");
            Check(!(await Tool("dosbox_effects",new{input_scale=2}))["isError"]!.GetValue<bool>()&&phoenix.Settings.InputScale==2,"integer MCP resize values remain compatible");
            Check(!(await Tool("dosbox_effects",new{input_resize=false}))["isError"]!.GetValue<bool>()&&!phoenix.Settings.InputResize,"MCP can restore native input");
            var beforeCpu=core;
            Check(!(await Tool("dosbox_configure",new{environment_id=profile.Id,settings=new{cpu=new{cycles="23456"}}}))["isError"]!.GetValue<bool>()&&core.RequestedCpuCycles=="23456"&&ReferenceEquals(core,beforeCpu),"MCP changes CPU cycles live without replacing the DOS machine");
            Check(!(await Tool("dosbox_configure",new{environment_id=profile.Id,settings=new{cpu=new{cycles="auto"}}}))["isError"]!.GetValue<bool>()&&core.RequestedCpuCycles=="auto"&&profile.ManualCycles==23456,"MCP restores Auto and retains the manual speed");
            if(core.MemoryBytes==0)Check((await Tool("dosbox_read_memory",new{offset=0,count=1}))["isError"]!.GetValue<bool>(),"unsupported RAM reported explicitly");
            await Tool("dosbox_session",new{action="pause"});Check(core.IsPaused,"pause tool");await Tool("dosbox_session",new{action="resume"});Check(!core.IsPaused,"resume tool");
            var configured=await Tool("dosbox_configure",new {environment_id=profile.Id,settings=new{cpu=new{core="normal"},mixer=new{rate="22050"},dos=new{xms="false"},libretro=new{dosbox_pure_force60fps="false"}}});Check(!configured["isError"]!.GetValue<bool>()&&Configuration.Generate(profile).Contains("xms=false"),"arbitrary DOSBox section/key settings persisted");
            Check(!(await Tool("dosbox_session",new{action="save_state"}))["isError"]!.GetValue<bool>(),"save-state tool succeeds in actual DOS executable");Check(!(await Tool("dosbox_session",new{action="load_state"}))["isError"]!.GetValue<bool>(),"load-state tool restores actual DOS executable");
            Check(!(await Tool("dosbox_launch",new{environment_id=profile.Id}))["isError"]!.GetValue<bool>(),"launch tool reloads configured environment");for(int i=0;i<100&&core?.Latest==null;i++)await Task.Delay(50);Check(core?.IsReady==true&&runningConfig.Contains("rate=22050"),"configured INI and core options loaded after restart");
            var injection=await Tool("dosbox_configure",new {environment_id=profile.Id,settings=new{cpu=new{cycles="auto\n[autoexec]\nexit"}}});Check(injection["isError"]!.GetValue<bool>(),"INI command injection rejected");
            var created=await Tool("dosbox_create_folder",new{environment_id=profile.Id,drive="C",name="MCPTEST"});Check(!created["isError"]!.GetValue<bool>()&&Directory.Exists(Path.Combine(profile.Drives[0].Folder,"MCPTEST")),"folder created through MCP");
            var configCatalog=await Tool("dosbox_get_configuration",new{environment_id=profile.Id});Check(configCatalog["structuredContent"]?["settingsCatalog"] is JsonArray catalog&&catalog.Count>40&&configCatalog["structuredContent"]?["coreOptionsCatalog"] is JsonArray pureCatalog&&pureCatalog.Count>10,"configuration tool advertises structured settings and installed core options");
            var independentPanels=await Tool("dosbox_display",new{environment_panel=false,effects_panel=true});Check(!independentPanels["isError"]!.GetValue<bool>()&&!environmentVisible&&effectsVisible,"display tool independently hides environment and shows effects");
            Check(!ApplicationPreferences.Load().EnvironmentVisible&&ApplicationPreferences.Load().EffectsVisible,"MCP panel visibility persists to app preferences");
            FocusForValidation();stage.Focus();await Task.Delay(100);
            var mouseConfigured=await Tool("dosbox_mouse_capture",new{capture_on_click=true,hide_pointer=true,release_shortcut="Ctrl+Alt+M",sensitivity=1.25});Check(!mouseConfigured["isError"]!.GetValue<bool>()&&preferences.Mouse.ReleaseShortcut=="Ctrl+Alt+M"&&ApplicationPreferences.Load().Mouse.Sensitivity==1.25,"MCP configures and persists the real mouse shortcut, pointer options and sensitivity");
            var invalidShortcut=await Tool("dosbox_mouse_capture",new{release_shortcut="F11",hide_pointer=false});Check(invalidShortcut["isError"]!.GetValue<bool>()&&preferences.Mouse.HidePointer&&preferences.Mouse.ReleaseShortcut=="Ctrl+Alt+M","invalid mouse shortcut is rejected without partially saving other changes");
            var capturedMouse=await Tool("dosbox_mouse_capture",new{captured=true});Check(!capturedMouse["isError"]!.GetValue<bool>()&&mouseCaptured,"MCP captures the foreground DOS window through the real capture mechanism");
            Check((await Tool("dosbox_status"))["structuredContent"]?["mouse"]?["captured"]?.GetValue<bool>()==true,"MCP status exposes active capture and all configured controls");
            Check(!(await Tool("dosbox_mouse",new{dx=4,dy=2,button="middle"}))["isError"]!.GetValue<bool>(),"MCP supports middle mouse input independently of host capture");
            await Tool("dosbox_mouse_capture",new{captured=false});Check(!mouseCaptured,"MCP releases Windows capture explicitly");
            var otherCaptureWindow=new Window{Owner=this,Width=200,Height=90,Title="MCP capture foreground boundary"};otherCaptureWindow.Show();await Task.Delay(70);
            Check((await Tool("dosbox_mouse_capture",new{captured=true}))["isError"]!.GetValue<bool>()&&!mouseCaptured,"MCP refuses capture while a settings/other window is foreground");otherCaptureWindow.Close();FocusForValidation();
            await Tool("dosbox_display",new{fullscreen=true,environment_panel=true,effects_panel=false,menu_visible=true});Check(fullscreen&&environmentVisible&&!effectsVisible&&fullscreenMenu.IsOpen,"MCP controls the independent fullscreen environment and menu");
            await Tool("dosbox_mouse_capture",new{captured=true});await Tool("dosbox_mouse_capture",new{captured=false});Check(!mouseCaptured&&fullscreenMenu.IsOpen,"MCP fullscreen release reveals the menu immediately");
            await Tool("dosbox_display",new{fullscreen=false,panels=true});
            File.WriteAllText(Path.Combine(fixture,"MCPTEST","TOOLS.BAT"),"@echo off\r\necho TOOLS\r\n");
            var scannedPrograms=await Tool("dosbox_manage_programs",new{environment_id=profile.Id,scan=true});Check(!scannedPrograms["isError"]!.GetValue<bool>()&&profile.Programs.Any(p=>p.Path=="C:\\MCPTEST\\TOOLS.BAT"),"MCP scans nested programs on approved mapped drives");
            var programPage=await Tool("dosbox_list_programs",new{environment_id=profile.Id,count=1});Check(programPage["structuredContent"]?["programs"] is JsonArray page&&page.Count==1&&programPage["structuredContent"]?["next_offset"]!=null,"read-only program listing is paginated");
            var programUpdates=new JsonObject {["C:\\VERIFY.COM"]=new JsonObject{["visible"]=false,["name"]="Hidden verification"}};
            await Tool("dosbox_manage_programs",new{environment_id=profile.Id,updates=programUpdates,order=new[]{"C:\\MCPTEST\\TOOLS.BAT","C:\\VERIFY.COM"},discover_new=false});
            Check(!profile.Programs.Single(p=>p.Path=="C:\\VERIFY.COM").Visible&&profile.Programs[0].Path=="C:\\MCPTEST\\TOOLS.BAT"&&!profile.DiscoverPrograms,"MCP hides, renames, reorders and disables automatic program additions");
            await Tool("dosbox_manage_programs",new{environment_id=profile.Id,scan=true});Check(!profile.Programs.Single(p=>p.Path=="C:\\VERIFY.COM").Visible&&profile.Programs[1].Name=="Hidden verification","MCP rescans preserve hidden entries, custom names and ordering");
            var visiblePrograms=await Tool("dosbox_list_programs",new{environment_id=profile.Id,include_hidden=false});Check(visiblePrograms["structuredContent"]!["programs"]!.AsArray().All(p=>p!["path"]!.GetValue<string>()!="C:\\VERIFY.COM"),"MCP can list only the programs shown in the dropdown");
            var invalidOrder=await Tool("dosbox_manage_programs",new{environment_id=profile.Id,order=new[]{"C:\\MISSING.COM"},discover_new=true});Check(invalidOrder["isError"]!.GetValue<bool>()&&!profile.DiscoverPrograms,"invalid program ordering cannot partially change discovery settings");
            string deniedTarget=Path.Combine(Paths.Root,"tests","fixtures","mcp-unapproved");Directory.CreateDirectory(deniedTarget);File.WriteAllText(Path.Combine(deniedTarget,"SECRET.BAT"),"echo PRIVATE");
            using(var junction=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe"))
            { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,ArgumentList={"-NoProfile","-NonInteractive","-Command","New-Item -ItemType Junction -Path '"+deniedLink.Replace("'","''")+"' -Value '"+deniedTarget.Replace("'","''")+"' -ErrorAction Stop | Out-Null"} })!)
            { await junction.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Check(junction.ExitCode==0,"unapproved-link boundary fixture created"); }
            await Tool("dosbox_manage_programs",new{environment_id=profile.Id,scan=true});Check(!profile.Programs.Any(p=>p.Path.Contains("SECRET",StringComparison.OrdinalIgnoreCase)),"MCP discovery does not follow unapproved folder links outside mapped roots");Directory.Delete(deniedLink);
            var copiedEnvironment=await Tool("dosbox_environment",new{action="copy",environment_id=profile.Id,name="MCP environment copy"});string copyId=copiedEnvironment["structuredContent"]!["environment"]!["id"]!.GetValue<string>();addedEnvironments.Add(copyId);
            var copyProfile=McpProfile(copyId);Check(copyProfile.Drives[0].Folder==profile.Drives[0].Folder&&!ReferenceEquals(copyProfile.Programs,profile.Programs)&&!copyProfile.Programs[1].Visible,"MCP environment copy preserves settings, hidden list and shared folders independently");
            string exportedSettings=(await Tool("dosbox_get_configuration",new{environment_id=copyId}))["structuredContent"]!["settingsIni"]!.GetValue<string>();Check(!(await Tool("dosbox_configure",new{environment_id=copyId,ini=exportedSettings}))["isError"]!.GetValue<bool>(),"MCP settings export round-trips directly through INI import");
            string largeIni=exportedSettings+string.Concat(Enumerable.Repeat("#\r\n",20000));Check(!(await Tool("dosbox_configure",new{environment_id=copyId,ini=largeIni}))["isError"]!.GetValue<bool>(),"large settings INI fits the bounded HTTP transport without truncation");
            await Tool("dosbox_environment",new{action="update",environment_id=copyId,name="Renamed MCP copy",startup="C:\\MCPTEST\\TOOLS.BAT"});Check(copyProfile.Name=="Renamed MCP copy"&&profile.Startup=="C:\\VERIFY.COM","MCP renames/edits a copy without altering the original environment");
            Check(programPicker.Choices.Any(p=>p.Environment.Id==copyId&&p.Environment.Name=="Renamed MCP copy"),"MCP changes appear in the all-environment picker without selecting the edited environment");
            await Tool("dosbox_manage_programs",new{environment_id=copyId,updates=new JsonObject{["C:\\VERIFY.COM"]=new JsonObject{["visible"]=true}}});Check(!profile.Programs.Single(p=>p.Path=="C:\\VERIFY.COM").Visible,"copied program visibility is independent through MCP");
            await Tool("dosbox_manage_programs",new{environment_id=copyId,updates=new JsonObject{["C:\\VERIFY.COM"]=new JsonObject{["name"]="Friendly MCP launcher"}}});
            Check(programPicker.Choices.Any(p=>p.Environment.Id==copyId&&p.Program.Path=="C:\\VERIFY.COM"&&p.Program.Name=="Friendly MCP launcher"),"MCP friendly names and visibility update program choices in other environments");
            await Tool("dosbox_configure",new{environment_id=copyId,ini="[cpu]\ncore=normal\ncycles=5000\n[dosbox]\nmemsize=12\n[custom]\nkeep=yes"});Check(copyProfile.Memory==12&&copyProfile.Cycles=="5000"&&copyProfile.DosboxSettings["custom"]["keep"]=="yes","MCP imports full INI into per-environment settings");
            await Tool("dosbox_configure",new{environment_id=copyId,settings=new JsonObject{["cpu"]=new JsonObject{["core"]=null},["custom"]=null}});Check(!copyProfile.DosboxSettings.GetValueOrDefault("cpu",[]).ContainsKey("core")&&!copyProfile.DosboxSettings.ContainsKey("custom"),"MCP resets individual overrides and whole sections with null");
            await Tool("dosbox_configure",new{environment_id=copyId,replace=true,settings=new{mixer=new{rate="44100"}}});Check(copyProfile.Memory==32&&copyProfile.Cycles=="auto"&&copyProfile.DosboxSettings["mixer"]["rate"]=="44100","MCP replacement resets omitted common settings to app defaults");
            await Tool("dosbox_environment",new{action="select",environment_id=copyId});Check(ReferenceEquals(selected,copyProfile),"MCP selects the current UI environment");await Tool("dosbox_environment",new{action="select",environment_id=profile.Id});
            var newEnvironment=await Tool("dosbox_environment",new{action="create",name="MCP new environment"});string newId=newEnvironment["structuredContent"]!["environment"]!["id"]!.GetValue<string>();addedEnvironments.Add(newId);Check(Directory.Exists(McpProfile(newId).Drives[0].Folder)&&McpProfile(newId).Drives[0].Folder!=fixture,"MCP creates an environment with its own empty C folder");
            await Tool("dosbox_drive",new{environment_id=newId,action="map",drive="E",source_environment_id=profile.Id,source_drive="C",path="MCPTEST",label="Approved tools"});Check(McpProfile(newId).Drives.Single(d=>d.Letter=="E").Folder==Path.Combine(fixture,"MCPTEST"),"MCP maps a virtual drive to an approved subfolder");
            Check((await Tool("dosbox_drive",new{environment_id=newId,action="map",drive="F",source_environment_id=profile.Id,source_drive="C",path="..\\.."}))["isError"]!.GetValue<bool>()&&!McpProfile(newId).Drives.Any(d=>d.Letter=="F"),"MCP drive mapping rejects escape without creating a partial drive");
            await Tool("dosbox_environment",new{action="update",environment_id=newId,startup="E:\\TOOLS.BAT"});await Tool("dosbox_drive",new{environment_id=newId,action="update",drive="E",label="Tools renamed",type="cdrom"});Check(McpProfile(newId).Drives.Single(d=>d.Letter=="E").Type=="cdrom","MCP edits drive type/label");
            await Tool("dosbox_drive",new{environment_id=newId,action="remove",drive="E"});Check(McpProfile(newId).Startup==""&&Directory.Exists(Path.Combine(fixture,"MCPTEST")),"MCP drive removal clears dependent startup but preserves folders");
            await Tool("dosbox_quick_run",new{environment_id=profile.Id,drive="C",path="VERIFY.COM"});for(int i=0;i<100&&core?.Latest==null;i++)await Task.Delay(50);Check(core?.IsReady==true&&running?.Id!=profile.Id&&profile.Startup=="C:\\VERIFY.COM","MCP standalone runner uses mapped programs without changing environment startup");
            await Tool("dosbox_session",new{action="stop"});Check((await Tool("dosbox_get_configuration",new{environment_id=copyId}))["structuredContent"]?["coreOptionsCatalog"] is JsonArray cachedOptions&&cachedOptions.Count>10,"core option catalog remains available through MCP after stopping DOS");
            Check(!(await Tool("dosbox_mouse_capture",new{captured=false}))["isError"]!.GetValue<bool>(),"MCP release remains usable without a DOS session");
            foreach(string added in addedEnvironments.ToArray()) {await Tool("dosbox_environment",new{action="remove",environment_id=added});addedEnvironments.Remove(added);}
            Check(!programPicker.Choices.Any(p=>p.Environment.Id==copyId||p.Environment.Id==newId),"MCP removal immediately removes environment groups from the picker");
            Check(Directory.Exists(fixture)&&File.Exists(Path.Combine(fixture,"MCPTEST","TOOLS.BAT")),"MCP profile removal preserves shared game files");
            await Tool("dosbox_launch",new{environment_id=profile.Id});for(int i=0;i<100&&core?.Latest==null;i++)await Task.Delay(50);
            var localIp=Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(ip=>ip.AddressFamily==AddressFamily.InterNetwork&&!IPAddress.IsLoopback(ip));
            if(localIp!=null)
            {
                bool denied=false;try{await Request("ping",url:$"http://{localIp}:{port}/mcp");}catch(HttpRequestException){denied=true;}Check(denied,"LAN connection refused while remote checkbox off");
                settings=settings with {AllowRemote=true};await ConfigureMcp(settings);session="";Check((await Request("ping",url:$"http://{localIp}:{port}/mcp")).status==200,"LAN connection accepted with remote checkbox on");Check((await Request("ping",auth:"none",url:$"http://{localIp}:{port}/mcp")).status==401,"LAN still requires authentication");
            }
            settings=settings with {AllowRemote=false,AllowControl=false};await ConfigureMcp(settings);session="";await Request("initialize",new JsonObject{["protocolVersion"]="2025-11-25"});await Request("notifications/initialized",notify:true);
            if(localIp!=null){bool blockedAgain=false;try{await Request("ping",url:$"http://{localIp}:{port}/mcp");}catch(HttpRequestException){blockedAgain=true;}Check(blockedAgain,"unchecking remote closes LAN listener again");}
            var observe=await Request("tools/list");Check(((JsonArray)observe.body!["result"]!["tools"]!).Count==8,"observation mode hides control tools");Check((await Tool("dosbox_press_keys",new{keys=new[]{"a"}}))["isError"]!.GetValue<bool>(),"observation mode rejects direct control calls");
            foreach(var control in new[]{("dosbox_mouse_capture",new JsonObject{["captured"]=true}),("dosbox_manage_programs",new JsonObject{["environment_id"]=profile.Id,["scan"]=true}),("dosbox_environment",new JsonObject{["action"]="copy",["environment_id"]=profile.Id}),("dosbox_drive",new JsonObject{["environment_id"]=profile.Id,["action"]="remove",["drive"]="D"}),("dosbox_quick_run",new JsonObject{["environment_id"]=profile.Id,["drive"]="C",["path"]="VERIFY.COM"})})Check((await Tool(control.Item1,control.Item2))["isError"]!.GetValue<bool>(),"observation mode rejects "+control.Item1);
            Check(!(await Tool("dosbox_list_programs",new{environment_id=profile.Id}))["isError"]!.GetValue<bool>(),"observation mode reads the managed program menu");
            // The same published executable is the entire stdio bridge.
            using(var bridge=Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"DosboxEnhanced.exe"),"--mcp-stdio"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true})!)
            {
                await bridge.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\"}}");await bridge.StandardInput.FlushAsync();var reply=await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));Check(JsonNode.Parse(reply!)?["result"]?["serverInfo"]?["name"]?.GetValue<string>()=="dosbox-enhanced","published executable stdio initialization");
                await bridge.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");await bridge.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");await bridge.StandardInput.FlushAsync();var tools=await bridge.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));Check(JsonNode.Parse(tools!)?["result"]?["tools"] is JsonArray array&&array.Count==8,"stdio tool discovery without extra runtime");bridge.StandardInput.Close();await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Check(bridge.ExitCode==0,"stdio EOF shuts down cleanly");
            }
            ShowMcpPanel();await Task.Delay(150);CaptureMcpPanel(Path.Combine(Paths.Root,"tests","results","mcp-panel.png"));mcpPanel?.Close();
            settings=settings with {Enabled=false};await ConfigureMcp(settings);bool closed=false;try{await Request("ping");}catch(HttpRequestException){closed=true;}Check(closed,"disable stops listening");
            report["passed"]=true;
        }
        catch(Exception e){report["passed"]=false;report["error"]=e.ToString();}
        finally
        {
            report["checks"]=checks;report["count"]=checks.Count;
            ReleaseMouseCapture();if(fullscreen)ToggleFullscreen();mcpPanel?.Close();await ConfigureMcp(original,false);preferences.Mcp=original;preferences.Audio=audioBefore;preferences.Mouse=mouseBefore;releaseShortcut=MouseShortcut.Parse(mouseBefore.ReleaseShortcut);UpdateMouseUi();preferences.EnvironmentVisible=envPanelBefore;preferences.EffectsVisible=effectsPanelBefore;environmentVisible=envPanelBefore;effectsVisible=effectsPanelBefore;ApplyLayout();
            if(originalPrefs!=null)File.WriteAllBytes(prefPath,originalPrefs);else if(File.Exists(prefPath))File.Delete(prefPath);
            foreach(string added in addedEnvironments){var temporary=library.Environments.FirstOrDefault(p=>p.Id==added);if(temporary!=null)library.Environments.Remove(temporary);}library.Environments.Remove(profile);library.Save();Stop();environments.SelectedItem=selectedBefore;
            var folder=Path.Combine(profile.Drives[0].Folder,"MCPTEST");var temporaryProgram=Path.Combine(folder,"TOOLS.BAT");if(File.Exists(temporaryProgram))File.Delete(temporaryProgram);if(Directory.Exists(folder)&&!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder);
            if(Directory.Exists(deniedLink))Directory.Delete(deniedLink);
            Paths.AtomicWrite(Path.Combine(Paths.Root,"tests","results","mcp-integration.json"),JsonSerializer.Serialize(report,Paths.Json));Close();
        }
    }
    void CaptureMcpPanel(string path)
    {
        if(mcpPanel?.Content is not System.Windows.Media.Visual content)return;
        var target=new System.Windows.Media.Imaging.RenderTargetBitmap((int)mcpPanel.ActualWidth,(int)mcpPanel.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);target.Render(content);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));using var stream=File.Create(path);encoder.Save(stream);
    }
}

