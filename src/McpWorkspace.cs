using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DosboxEnhanced;

public sealed partial class MainWindow
{
    static string Arg(JsonObject args,string key,string fallback="") => args[key]?.GetValue<string>()??fallback;
    static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    static string FriendlyName(string value)
    {
        value=value.Trim();if(value.Length is <1 or >160 || value.IndexOfAny(['\r','\n','\0'])>=0)throw new ArgumentException("Use a name of 1–160 characters on one line.");return value;
    }
    object MouseInfo() => new {captured=mouseCaptured,releaseShortcut=releaseShortcut.ToString(),captureOnClick=preferences.Mouse.CaptureOnClick,hidePointer=preferences.Mouse.HidePointer,sensitivity=preferences.Mouse.Sensitivity};
    internal object McpMouseCapture(JsonObject args)
    {
        var old=preferences.Mouse;
        var candidate=old with {CaptureOnClick=args["capture_on_click"]?.GetValue<bool>()??old.CaptureOnClick,HidePointer=args["hide_pointer"]?.GetValue<bool>()??old.HidePointer,ReleaseShortcut=Arg(args,"release_shortcut",old.ReleaseShortcut),Sensitivity=args["sensitivity"]?.GetValue<double>()??old.Sensitivity};
        var shortcut=MouseShortcut.Parse(candidate.ReleaseShortcut);candidate=candidate with {ReleaseShortcut=shortcut.ToString()};
        bool? capture=args["captured"]?.GetValue<bool>();
        if(capture==true && (core?.IsReady!=true || core.IsPaused || !IsActive || image.ActualWidth<2))throw new InvalidOperationException("Capture requires the running, unpaused game window to already be in the foreground. MCP does not activate it; DOS mouse automation needs no Windows capture.");
        bool changed=candidate!=old;
        if(changed)ReleaseMouseCapture();
        preferences.Mouse=candidate;releaseShortcut=shortcut;
        try
        {
            if(capture==true && !CaptureGameMouse())throw new InvalidOperationException("The game window is not ready to capture the mouse.");
            if(capture==false) {ReleaseMouseCapture(revealMenu:true,restorePointer:true);Keyboard.ClearFocus();ShowMouseHint("Mouse free · click the game to capture again");}
            if(changed)preferences.Save();UpdateMouseUi();return MouseInfo();
        }
        catch { preferences.Mouse=old;releaseShortcut=MouseShortcut.Parse(old.ReleaseShortcut);UpdateMouseUi();throw; }
    }
    void SyncWorkspace(EnvironmentProfile profile)
    {
        environments.Items.Refresh();
        UpdateProgramPicker();
        if(!ReferenceEquals(selected,profile))return;
        pickerEnvironmentId=profile.Id;pickerPath=profile.Startup;
        name.Text=profile.Name;startup.Text=profile.Startup;LoadCpuControls(profile);
        if(!memory.Items.Contains(profile.Memory))memory.Items.Add(profile.Memory);memory.SelectedItem=profile.Memory;
        drives.ItemsSource=profile.Drives;RefreshBrowserDrives();
    }
    internal object McpEnvironment(JsonObject args)
    {
        string action=Arg(args,"action");
        if(action is not ("create" or "copy" or "update" or "select" or "remove"))throw new ArgumentException("Environment action must be create, copy, update, select or remove.");
        var source=action=="create"?null:McpProfile(Arg(args,"environment_id"));
        if(source!=null && !library.Environments.Contains(source))throw new ArgumentException("Only saved environments can be managed; a quick run is a transient session.");
        if(action is "select" or "remove")
        {
            if(args["name"]!=null || args["startup"]!=null)throw new ArgumentException("Name/startup are only accepted for create, copy or update.");
            if(action=="select") {environments.SelectedItem=source;return new{selected=ProfileInfo(source!)};}
            string removed=source!.Id;library.Environments.Remove(source);library.Save();
            if(selected==null && library.Environments.Count>0)environments.SelectedIndex=0;
            UpdateProgramPicker();
            return new{removed,foldersDeleted=false,statesDeleted=false,runningUnchanged=running?.Id==removed};
        }
        var candidate=source==null?new EnvironmentProfile():Clone(source);
        if(action is "create" or "copy")candidate.Id=Guid.NewGuid().ToString("N");
        candidate.Name=args["name"]!=null?FriendlyName(Arg(args,"name")):action=="copy"?source!.Name+" (copy)":source==null?"DOS workspace "+(library.Environments.Count+1):source.Name;
        if(args["startup"]!=null)candidate.Startup=Arg(args,"startup").Trim();
        if(action=="create")
        {
            // This is app-owned storage, using a fresh ID generated here.
            string folder=Path.Combine(Paths.Data,"environments",candidate.Id,"drive-c");Directory.CreateDirectory(folder);candidate.Drives.Add(new(){Folder=folder});
        }
        Configuration.Generate(candidate);
        if(action=="update") {source!.Name=candidate.Name;source.Startup=candidate.Startup;library.Save();SyncWorkspace(source);return new{environment=ProfileInfo(source),sharedFolders=false};}
        library.Environments.Add(candidate);library.Save();environments.Items.Refresh();
        RefreshPrograms();
        return new{environment=ProfileInfo(candidate),sharedFolders=action=="copy"};
    }
    internal object McpDrive(JsonObject args)
    {
        var profile=McpProfile(Arg(args,"environment_id"));if(!library.Environments.Contains(profile))throw new ArgumentException("Select a saved environment.");
        string action=Arg(args,"action"),letter=Arg(args,"drive").TrimEnd(':').ToUpperInvariant();
        if(action is not ("map" or "update" or "remove") || letter.Length!=1 || letter[0]<'A' || letter[0]>'Y')throw new ArgumentException("Use map/update/remove and drive A–Y; Z is built-in.");
        var candidate=Clone(profile);var existing=candidate.Drives.FirstOrDefault(d=>d.Letter.Equals(letter,StringComparison.OrdinalIgnoreCase));
        bool clearedStartup=false;
        if(action=="remove")
        {
            if(existing==null)throw new ArgumentException("Drive not found.");
            if(args.Any(p=>p.Key is "source_environment_id" or "source_drive" or "path" or "label" or "type"))throw new ArgumentException("Removal takes only environment, action and drive.");
            candidate.Drives.Remove(existing);
            if(candidate.Startup.StartsWith(letter+":",StringComparison.OrdinalIgnoreCase)){candidate.Startup="";clearedStartup=true;}
        }
        else
        {
            if(action=="update" && existing==null)throw new ArgumentException("Drive not found; use map.");
            bool remap=args["source_drive"]!=null;
            if(action=="map" && !remap)throw new ArgumentException("Map needs source_drive from an already approved environment.");
            if(!remap && (args["source_environment_id"]!=null || args["path"]!=null))throw new ArgumentException("Source environment/path needs source_drive.");
            Drive replacement=existing??new(){Letter=letter};
            if(remap)
            {
                var map=Mapped(McpProfile(Arg(args,"source_environment_id",profile.Id)),Arg(args,"source_drive"),Arg(args,"path"));
                if(!Directory.Exists(map.host))throw new ArgumentException("Choose an existing mapped folder.");
                replacement=Clone(map.drive);replacement.Letter=letter;replacement.Folder=map.host;
                if(map.relative.Length>0)
                {
                    var prefix=map.relative.TrimEnd('\\')+"\\";
                    replacement.DisplayNames=map.drive.DisplayNames.Where(p=>p.Key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToDictionary(p=>p.Key[prefix.Length..],p=>p.Value);
                    replacement.Launchers=map.drive.Launchers.Where(p=>p.Key.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToDictionary(p=>p.Key[prefix.Length..],p=>p.Value);
                    if(Mapping(map.drive.Launchers,map.relative) is string launcher)replacement.Launchers[""]=launcher;
                }
            }
            if(args["label"]!=null)replacement.Label=Arg(args,"label").Length==0?"":FriendlyName(Arg(args,"label"));
            if(args["type"]!=null)replacement.Type=Arg(args,"type");
            if(existing!=null)candidate.Drives.Remove(existing);candidate.Drives.Add(replacement);
        }
        Configuration.Generate(candidate);profile.Drives=candidate.Drives;profile.Startup=candidate.Startup;library.Save();SyncWorkspace(profile);
        return new{environment=ProfileInfo(profile),startupCleared=clearedStartup,applies="Drive changes take effect on the next launch/reload; mapped folders are shared and writable."};
    }
    internal object McpQuick(string id,string drive,string path)
    {
        var map=Mapped(McpProfile(id),drive,path);QuickRun(map.host);return new{launched=ProfileInfo(running!),standalone=true};
    }
    static ProgramEntry CopyProgram(ProgramEntry p)=>new(){Path=p.Path,Name=p.Name,Visible=p.Visible,Available=p.Available};
    bool ProgramAvailable(EnvironmentProfile profile,ProgramEntry program)
    {
        try {if(program.Path.Length<3 || program.Path[1]!=':')return false;var map=Mapped(profile,program.Path[..1],program.Path[2..]);return File.Exists(map.host);}
        catch(ArgumentException){return false;}
    }
    internal object McpPrograms(string id,bool includeHidden,int offset,int count)
    {
        var profile=McpProfile(id);var entries=profile.Programs.Select((p,i)=>(p,i)).Where(e=>includeHidden||e.p.Visible).ToArray();
        return new{environment_id=profile.Id,discover_new=profile.DiscoverPrograms,total=entries.Length,offset,next_offset=offset+count<entries.Length?(int?)(offset+count):null,
            programs=entries.Skip(offset).Take(count).Select(e=>new{path=e.p.Path,name=e.p.Name,visible=e.p.Visible,available=ProgramAvailable(profile,e.p),order=e.i}).ToArray()};
    }
    internal async Task<object> McpManageProgramsAsync(JsonObject args,CancellationToken token)
    {
        string id=Arg(args,"environment_id");
        var snapshot=await Dispatcher.InvokeAsync(()=>Clone(McpProfile(id)),DispatcherPriority.Background,token);
        bool Allowed(Drive drive,string relative) {try{Mapped(snapshot,drive.Letter,relative);return true;}catch(ArgumentException){return false;}}
        ProgramScan? scan=args["scan"]?.GetValue<bool>()==true?await Task.Run(()=>ProgramCatalog.Scan(snapshot.Drives,token,Allowed),token):null;
        return await Dispatcher.InvokeAsync(()=>
        {
            var profile=McpProfile(id);if(scan!=null && JsonSerializer.Serialize(profile.Drives)!=JsonSerializer.Serialize(snapshot.Drives))throw new InvalidOperationException("Mappings changed during scanning; scan again.");
            var working=profile.Programs.Select(CopyProgram).ToList();if(scan!=null)ProgramCatalog.Merge(working,scan,true);
            if(args["updates"] is JsonObject updates)
            {
                foreach(var update in updates)
                {
                    var entry=working.FirstOrDefault(p=>p.Path.Equals(update.Key,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("Unknown program path: "+update.Key);
                    if(update.Value is not JsonObject values || values.Any(v=>v.Key is not ("visible" or "name")))throw new ArgumentException("Each update accepts visible and/or name.");
                    if(values.ContainsKey("visible")) {if(values["visible"] is not JsonValue visible || !visible.TryGetValue<bool>(out var show))throw new ArgumentException("Program visibility must be a boolean.");entry.Visible=show;}
                    if(values.ContainsKey("name")) {if(values["name"] is not JsonValue title || !title.TryGetValue<string>(out var text))throw new ArgumentException("Program names must be strings.");entry.Name=FriendlyName(text);}
                }
            }
            if(args["order"] is JsonArray order)
            {
                var requested=order.Select(p=>p!.GetValue<string>()).ToArray();if(requested.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=requested.Length)throw new ArgumentException("Order cannot repeat a program.");
                var sorted=requested.Select(path=>working.FirstOrDefault(p=>p.Path.Equals(path,StringComparison.OrdinalIgnoreCase))??throw new ArgumentException("Unknown program in order: "+path)).ToList();
                sorted.AddRange(working.Except(sorted));working=sorted;
            }
            profile.Programs=working;profile.DiscoverPrograms=args["discover_new"]?.GetValue<bool>()??profile.DiscoverPrograms;library.Save();
            UpdateProgramPicker();
            if(scan!=null)updateOpenManager?.Invoke(profile,scan);
            return (object)new{saved=true,environment_id=id,total=working.Count,discover_new=profile.DiscoverPrograms,scan_complete=scan?.Complete,scan_note=scan?.Note};
        },DispatcherPriority.Background,token);
    }
}
