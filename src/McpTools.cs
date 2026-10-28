using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DosboxEnhanced;

public static class McpTools
{
    sealed record Definition(string Name, string Description, bool Observe, JsonObject Schema);
    static JsonObject Field(string type, string description, double? minimum = null, double? maximum = null)
    {
        var value = new JsonObject { ["type"] = type, ["description"] = description };
        if (minimum != null) value["minimum"] = minimum; if (maximum != null) value["maximum"] = maximum;
        if (type == "array") value["items"] = new JsonObject { ["type"] = "string" }; return value;
    }
    static JsonObject PathsField(string description) { var field=Field("array",description);field["maxItems"]=2000;return field; }
    static JsonObject IniField() { var field=Field("string","Complete settings INI, up to 64 KB. Mutually exclusive with settings; no autoexec commands.");field["maxLength"]=65536;return field; }
    static Definition Tool(string name, string description, bool observe, string[] required, params (string key, JsonObject value)[] fields)
    {
        var properties = new JsonObject(); foreach (var field in fields) properties[field.key] = field.value;
        return new(name, description, observe, new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = JsonSerializer.SerializeToNode(required), ["additionalProperties"] = false });
    }
    static readonly Definition[] definitions = [
        Tool("dosbox_status", "Observe running environment, emulation, graphics, audio and MCP status.", true, []),
        Tool("dosbox_list_environments", "List saved DOS environments and mounted drives; use their IDs and DOS paths.", true, []),
        Tool("dosbox_list_programs", "Read the managed executable dropdown, including visibility, order and availability. Newly found files require a scan/automatic discovery.", true, ["environment_id"], ("environment_id",Field("string","Environment ID")),("include_hidden",Field("boolean","Include hidden entries; defaults to true")),("offset",Field("integer","List offset",0,20000)),("count",Field("integer","Entries per page; default 200",1,1000))),
        Tool("dosbox_list_files", "Browse a mapped DOS drive. Results include friendly category names and curated launchers.", true, ["environment_id", "drive"], ("environment_id", Field("string", "Saved environment ID")), ("drive", Field("string", "DOS drive letter, e.g. C")), ("path", Field("string", "Relative folder, e.g. ACTION; blank means drive root"))),
        Tool("dosbox_screenshot", "Capture PNG image content. Original pixels by default; enhanced=true requests processed output. pair=true returns the original and enhanced output from one completed graphics call, with settings, dimensions and PNG checksums. Does not save files. Pair capture requires ready graphics; no unmatched fallback. Generated presentation frames are excluded.", true, [], ("enhanced", Field("boolean", "Capture enhanced output instead of raw DOS pixels; cannot be true with pair")), ("pair", Field("boolean", "Return matching original and enhanced PNGs, in that order, plus metadata"))),
        Tool("dosbox_read_text", "Read DOS screen text. Uses VGA RAM when available, otherwise the built-in Windows OCR engine; verify uncertain OCR against screenshots.", true, []),
        Tool("dosbox_read_memory", "Read a bounded range of emulator RAM, never host-process memory. Available only if the core exposes RAM.", true, ["offset", "count"], ("offset", Field("integer", "Physical RAM byte offset", 0, 268435455)), ("count", Field("integer", "Bytes to read", 1, 4096))),
        Tool("dosbox_get_configuration", "Get effective DOSBox INI settings, drive mappings and startup. Any supported section/key can be configured.", true, ["environment_id"], ("environment_id", Field("string", "Saved environment ID"))),
        Tool("dosbox_launch", "Launch a saved environment or a DOS path within one of its mounted drives. A game folder can use its curated launcher.", false, ["environment_id"], ("environment_id", Field("string", "Saved environment ID")), ("path", Field("string", "Optional DOS program/folder path, e.g. C:\\ACTION\\CONAN"))),
        Tool("dosbox_session", "Pause, resume, reset, reload current configuration, stop, save state or load state. action: pause/resume/reset/reload/stop/save_state/load_state.", false, ["action"], ("action", Field("string", "Session action"))),
        Tool("dosbox_type_text", "Type ASCII text directly into DOS, including newline for Enter. At most 2048 characters; do not type into Windows.", false, ["text"], ("text", Field("string", "DOS text, with \\n for Enter")), ("delay_ms", Field("integer", "Delay between characters", 0, 30))),
        Tool("dosbox_press_keys", "Press a DOS key/chord then release it. Examples: [enter], [ctrl,c], [alt,f4], [left]. This targets DOS; use dosbox_mouse_capture to free the Windows pointer.", false, ["keys"], ("keys", Field("array", "Key names")), ("hold_ms", Field("integer", "How long to hold", 20, 5000))),
        Tool("dosbox_mouse", "Move the DOS mouse relatively, click left/right/middle, or use the wheel. Does not move the Windows pointer and does not require host mouse capture.", false, [], ("dx", Field("integer", "Horizontal DOS pixel delta", -4096, 4096)), ("dy", Field("integer", "Vertical DOS pixel delta", -4096, 4096)), ("button", Field("string", "Optional left, right or middle click")), ("wheel", Field("integer", "Wheel direction", -1, 1))),
        Tool("dosbox_mouse_capture", "Set/release Windows mouse capture and configure the app-wide release shortcut, click-to-capture, pointer visibility and physical mouse sensitivity. Capture requires a running, unpaused foreground game window; never activates another window. Release works without a session. DOS mouse automation needs no host capture.", false, [],("captured",Field("boolean","Capture or free the Windows pointer")),("capture_on_click",Field("boolean","Automatically capture on game click")),("hide_pointer",Field("boolean","Hide the Windows pointer while captured")),("release_shortcut",Field("string","For example Caps Lock, F12 or Ctrl+Alt+M; Ctrl+F10 always remains an escape")),("sensitivity",Field("number","Physical mouse sensitivity multiplier",.25,4))),
        Tool("dosbox_audio", "Set the app's DOS audio volume and/or mute without changing system audio.", false, [], ("volume", Field("number", "Volume percent", 0, 100)), ("muted", Field("boolean", "Mute DOS audio"))),
        Tool("dosbox_display", "Configure fullscreen, separate environment/effect panels, fullscreen menu, aspect and sampling. Individual panel flags override panels. Windowed panel choices persist.", false, [], ("fullscreen", Field("boolean", "Fullscreen")), ("panels", Field("boolean", "Show both panels")),("environment_panel",Field("boolean","Show/hide the environment independently")),("effects_panel",Field("boolean","Show/hide Beautify effects independently")),("menu_visible",Field("boolean","Reveal/hide the fullscreen menu; windowed toolbar stays visible")), ("aspect", Field("string", "dos, pixels or stretch")), ("sampling", Field("string", "smooth, sharp or integer"))),
        Tool("dosbox_effects", "Set renderer/effects and independent NVIDIA SR/FG. Native DOS input; upscale_order selects SR before or after NR; disabling SR leaves native resolution. Renderer: phoenix/nvidia; style: 0 default, 1 Natural / 2 Cinematic for Phoenix, 1 warm / 2 cool for NVIDIA; Phoenix DLSS5 passes: 1–4.", false, [],
            ("phoenix", Field("object", "Phoenix advanced settings: Skin (-1..2), AutoMask, Temporal, NeuralScale (10..100), Blocks (1..8), Repeats (eight comma-separated 0..4 counts), ColorStrength (0..1), TargetFps (0..240), MaxMilliseconds (0..1000), Priority (0 fixed/1 responsiveness/2 quality), Monitoring (0 off/1 basic/2 detailed), SceneDetection, Brightness (-2..2), Contrast (0..4), Saturation (0..2), Gamma (0.1..4). Omitted members use defaults.")), ("renderer", Field("string", "phoenix or nvidia")), ("enabled", Field("boolean", "Neural rendering")), ("intensity", Field("number", "Effect intensity", 0, 2)), ("tone", Field("number", "Local tone", 0, 2)), ("structure", Field("number", "Structure", 0, 2)), ("style", Field("integer", "Phoenix: 0 default, 1 Natural, 2 Cinematic; NVIDIA: 1 warm, 2 cool", 0, 2)), ("passes", Field("integer", "Phoenix DLSS5 passes", 1, 4)), ("compare", Field("boolean", "Before/after split")), ("upscaling", Field("boolean", "NVIDIA SR")), ("upscale_order", Field("string", "after_nr (native NR then SR, default) or before_nr (SR then NR)")), ("upscale_factor", Field("integer", "Native resolution multiplier", 2, 4)), ("input_resize", Field("boolean", "Optional resize before NR/SR; off by default")), ("input_scale", Field("number", "Fractional input resize multiplier (for example 1.25)", .25, 4)), ("input_filter", Field("string", "nearest (sharp pixels) or linear (smooth)")), ("frame_generation", Field("boolean", "NVIDIA FG"))),
        Tool("dosbox_configure", "Save supported DOSBox/core settings. Supply settings or complete ini. Null keys/sections remove overrides; replace=true replaces all settings. restart=true reloads the running matching environment. Drives/startup manage autoexec.", false, ["environment_id"], ("environment_id", Field("string", "Saved environment ID")), ("settings", Field("object", "Section objects of string values or null to reset keys; a null section removes it")),("ini",IniField()),("replace",Field("boolean","Replace all settings instead of merging")), ("restart", Field("boolean", "Reload if this environment is running"))),
        Tool("dosbox_manage_programs", "Scan mapped folders and manage the executable dropdown: hidden/shown entries, custom names, order and automatic additions. Hidden entries stay hidden after scanning. Omitted order entries retain their relative order at the end.", false,["environment_id"],("environment_id",Field("string","Environment ID")),("scan",Field("boolean","Discover files now, including when automatic additions are off")),("discover_new",Field("boolean","Automatically add new programs")),("updates",Field("object","DOS path to {visible:boolean,name:string}; existing managed entries only")),("order",PathsField("DOS paths to place first in the requested order"))),
        Tool("dosbox_environment", "Create, copy, update, select or remove a saved environment. Copies have independent settings/program lists and shared game folders. Create gets its own empty C drive. Remove deletes only the profile; never game folders or states.",false,["action"],("action",Field("string","create, copy, update, select or remove")),("environment_id",Field("string","Required except create")),("name",Field("string","Optional environment name")),("startup",Field("string","Optional DOS EXE/COM/BAT startup; blank opens DOS prompt"))),
        Tool("dosbox_drive", "Map a drive to an existing approved drive or its subfolder, change a label/type, or remove the mapping. New host roots must first be mapped by the user in the app. Never deletes folders. Removal clears startup if it used that drive.",false,["environment_id","action","drive"],("environment_id",Field("string","Destination environment")),("action",Field("string","map, update or remove")),("drive",Field("string","Destination letter A-Y")),("source_environment_id",Field("string","Existing approved environment; defaults to destination")),("source_drive",Field("string","Required for map; optional for update to remap")),("path",Field("string","Relative source subfolder")),("label",Field("string","Friendly drive label")),("type",Field("string","dir or cdrom"))),
        Tool("dosbox_quick_run", "Run a DOS EXE/COM/BAT within an approved drive as a standalone quick run, mounting its parent as C. Does not change the source environment's startup.",false,["environment_id","drive","path"],("environment_id",Field("string","Existing approved environment")),("drive",Field("string","Source drive letter")),("path",Field("string","Relative program file"))),
        Tool("dosbox_create_folder", "Create a nested folder inside a mapped drive; existing files/folders are never overwritten.", false, ["environment_id", "drive", "name"], ("environment_id", Field("string", "Environment ID")), ("drive", Field("string", "DOS drive letter")), ("path", Field("string", "Relative parent folder")), ("name", Field("string", "New folder name")))
    ];
    public static object[] List(bool control) => definitions.Where(d => control || d.Observe).Select(d => (object)new { name = d.Name, description = d.Description, inputSchema = d.Schema, annotations = new { readOnlyHint = d.Observe, destructiveHint = !d.Observe, openWorldHint = false } }).ToArray();
    public static bool Known(string name) => definitions.Any(d => d.Name == name);
    public static bool ReadOnly(string name) => definitions.First(d => d.Name == name).Observe;
    public static JsonObject Text(object data, bool error = false) { var node = JsonSerializer.SerializeToNode(data)!; return new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = node.ToJsonString() }), ["structuredContent"] = node, ["isError"] = error }; }
    static void Validate(string name, JsonObject args)
    {
        var schema = definitions.First(d => d.Name == name).Schema; var properties = (JsonObject)schema["properties"]!;
        foreach (var required in (JsonArray)schema["required"]!) if (!args.ContainsKey(required!.GetValue<string>())) throw new ArgumentException("Missing argument: " + required.GetValue<string>());
        foreach (var pair in args)
        {
            if (properties[pair.Key] is not JsonObject rule) throw new ArgumentException("Unknown argument: " + pair.Key);
            if (pair.Value == null) throw new ArgumentException("Null argument: " + pair.Key);
            var type = rule["type"]!.GetValue<string>(); var element = JsonSerializer.SerializeToElement(pair.Value);
            bool valid = type switch { "string" => element.ValueKind == JsonValueKind.String && element.GetString()!.Length <= (rule["maxLength"]?.GetValue<int>()??4096), "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out _), "number" => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var n) && double.IsFinite(n),
                "object" => pair.Value is JsonObject, "array" => pair.Value is JsonArray array && array.Count <= (rule["maxItems"]?.GetValue<int>()??8) && array.All(v => v is JsonValue j && j.TryGetValue<string>(out var item) && item.Length<=4096), _ => false };
            if (!valid) throw new ArgumentException("Invalid type/size for " + pair.Key);
            if (type is "integer" or "number") { var value = element.GetDouble(); if (rule["minimum"] is JsonNode lo && value < lo.GetValue<double>() || rule["maximum"] is JsonNode hi && value > hi.GetValue<double>()) throw new ArgumentException("Out-of-range argument: " + pair.Key); }
        }
    }
    static string S(JsonObject a, string key, string fallback = "") => a[key]?.GetValue<string>() ?? fallback;
    static int I(JsonObject a, string key, int fallback = 0) => a[key]?.GetValue<int>() ?? fallback;
    static bool B(JsonObject a, string key, bool fallback = false) => a[key]?.GetValue<bool>() ?? fallback;
    static Task<T> Ui<T>(MainWindow w, Func<T> action, CancellationToken token) => w.Dispatcher.InvokeAsync(action, DispatcherPriority.Background, token).Task;
    static Task Ui(MainWindow w, Action action, CancellationToken token) => w.Dispatcher.InvokeAsync(action, DispatcherPriority.Background, token).Task;
    static async Task<DosCore> Core(MainWindow w, CancellationToken token) => await Ui(w, () => w.Core is { IsReady: true } c ? c : throw new InvalidOperationException("Launch a DOS environment first."), token);
    public static async Task<JsonObject> Call(MainWindow window, string name, JsonObject args, CancellationToken token)
    {
        Validate(name, args);
        switch (name)
        {
            case "dosbox_status": return Text(await Ui(window, window.McpStatus, token));
            case "dosbox_list_environments": return Text(await Ui(window, window.McpEnvironments, token));
            case "dosbox_list_programs": return Text(await Ui(window,()=>window.McpPrograms(S(args,"environment_id"),B(args,"include_hidden",true),I(args,"offset"),I(args,"count",200)),token));
            case "dosbox_manage_programs": return Text(await window.McpManageProgramsAsync(args,token));
            case "dosbox_environment": return Text(await Ui(window,()=>window.McpEnvironment(args),token));
            case "dosbox_drive": return Text(await Ui(window,()=>window.McpDrive(args),token));
            case "dosbox_quick_run": return Text(await Ui(window,()=>window.McpQuick(S(args,"environment_id"),S(args,"drive"),S(args,"path")),token));
            case "dosbox_get_configuration": return Text(await Ui(window, () => window.McpConfiguration(S(args,"environment_id")), token));
            case "dosbox_list_files": return Text(await Ui(window, () => window.McpFiles(S(args,"environment_id"), S(args,"drive"), S(args,"path")), token));
            case "dosbox_create_folder": return Text(await Ui(window, () => window.McpCreateFolder(S(args,"environment_id"), S(args,"drive"), S(args,"path"), S(args,"name")), token));
            case "dosbox_launch": return Text(await Ui(window, () => window.McpLaunch(S(args,"environment_id"), S(args,"path")), token));
            case "dosbox_configure": var configurationTask = await Ui(window, () => window.McpConfigure(args), token); return Text(await configurationTask.WaitAsync(TimeSpan.FromSeconds(15), token));
            case "dosbox_session":
                var sessionAction = await Ui(window, () => window.McpSession(S(args,"action")), token); await sessionAction.WaitAsync(TimeSpan.FromSeconds(15), token); return Text(new { action = S(args,"action"), completed = true });
            case "dosbox_audio": return Text(await Ui(window, () => window.McpAudio(args), token));
            case "dosbox_display": return Text(await Ui(window, () => window.McpDisplay(args), token));
            case "dosbox_effects": return Text(await Ui(window, () => window.McpEffects(args), token));
            case "dosbox_mouse_capture": return Text(await Ui(window,()=>window.McpMouseCapture(args),token));
            case "dosbox_screenshot":
                if (B(args,"pair")) {
                    if (B(args,"enhanced")) throw new ArgumentException("Use pair=true or enhanced=true, not both.");
                    return await Ui(window, () => {
                        var pair = window.McpFramePair(); var original = Screenshots.Encode(pair.Original); var enhanced = Screenshots.Encode(pair.Enhanced);
                        var metadata = JsonSerializer.SerializeToNode(Screenshots.Describe(pair, original, enhanced))!;
                        return new JsonObject { ["content"] = new JsonArray(
                            new JsonObject { ["type"] = "text", ["text"] = metadata.ToJsonString() },
                            new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(original) },
                            new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(enhanced) }),
                            ["structuredContent"] = metadata, ["isError"] = false };
                    }, token);
                }
                var frame = await Ui(window, () => window.McpFrame(B(args,"enhanced")), token);
                var png = await Ui(window, () => { var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null, frame.Pixels, frame.Width*4); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream); return Convert.ToBase64String(stream.ToArray()); }, token);
                return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(new { width=frame.Width, height=frame.Height, frame=frame.Sequence, enhanced=B(args,"enhanced") }) }, new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = png }), ["isError"] = false };
            case "dosbox_read_text":
                var textCore = await Core(window,token);
                if(textCore.MemoryBytes>0) { var textResult=await textCore.ReadText(token);var node=JsonSerializer.SerializeToNode(textResult);if(node?["textMode"]?.GetValue<bool>()==true)return Text(textResult); }
                var textFrame=await Ui(window,()=>window.McpFrame(false),token);return Text(await await Ui(window,()=>ScreenText.Read(textFrame,token),token));
            case "dosbox_read_memory": var memory = await (await Core(window, token)).ReadMemory(I(args,"offset"), I(args,"count"), token); return Text(new { offset=I(args,"offset"), count=memory.Length, hex=Convert.ToHexString(memory), base64=Convert.ToBase64String(memory) });
            case "dosbox_type_text":
                var typedCore = await Core(window, token); var text = S(args,"text").Replace("\r\n","\n").Replace('\r','\n');
                if (text.Length > 2048 || text.Any(c => c is not ('\n' or '\t') && (c < 32 || c > 126))) throw new ArgumentException("Type up to 2048 ASCII characters, tabs or newlines.");
                if(text.Length*(10+I(args,"delay_ms",10))>50000)throw new ArgumentException("Use shorter text chunks; one typing call must stay under 50 seconds.");
                try { foreach (char ch in text) { token.ThrowIfCancellationRequested(); if (!await Ui(window, () => ReferenceEquals(window.Core, typedCore), token)) throw new InvalidOperationException("DOS session changed while typing.");
                    uint key = ch == '\n' ? 13u : ch == '\t' ? 9u : char.IsAsciiLetterUpper(ch) ? char.ToLowerInvariant(ch) : ch; ushort shift = char.IsAsciiLetterUpper(ch) ? (ushort)1 : (ushort)0;
                    typedCore.Key(key,true,ch=='\n'?13u:ch,shift); await Task.Delay(10, token); typedCore.Key(key,false); await Task.Delay(I(args,"delay_ms",10),token); } }
                finally { typedCore.ReleaseKeys(); } return Text(new { typed = text.Length });
            case "dosbox_press_keys":
                var keyCore = await Core(window,token); var chord = ((JsonArray)args["keys"]!).Select(k => KeyCode(k!.GetValue<string>())).ToArray(); if (chord.Length == 0) throw new ArgumentException("Choose at least one key.");
                ushort modifiers = (ushort)((chord.Any(k=>k is 303 or 304)?1:0)|(chord.Any(k=>k is 305 or 306)?64:0)|(chord.Any(k=>k is 307 or 308)?256:0));
                try { foreach(var key in chord) keyCore.Key(key,true,key is >=32 and <127 ? key:0,modifiers); await Task.Delay(I(args,"hold_ms",80),token); }
                finally { foreach(var key in chord.Reverse()) keyCore.Key(key,false); keyCore.ReleaseKeys(); } return Text(new { pressed = args["keys"]!.DeepClone() });
            case "dosbox_mouse":
                var button=S(args,"button");int mask=button switch { ""=>0,"left"=>1,"right"=>2,"middle"=>4,_=>throw new ArgumentException("Mouse button must be left, right or middle.") };
                var mouseCore = await Core(window,token); mouseCore.MouseMove(I(args,"dx"),I(args,"dy")); mouseCore.MouseWheel(I(args,"wheel"));
                if (mask!=0) { try { mouseCore.MouseButton(mask,true); await Task.Delay(80,token); } finally { mouseCore.MouseButton(mask,false); } } return Text(new { moved=true });
            default: throw new ArgumentException("Unknown tool.");
        }
    }
    static uint KeyCode(string name)
    {
        var normalized = name.Trim().ToLowerInvariant(); Key key = normalized switch { "ctrl" or "control"=>Key.LeftCtrl,"alt"=>Key.LeftAlt,"shift"=>Key.LeftShift,"esc"=>Key.Escape,"return"=>Key.Enter,"space"=>Key.Space,_=>Key.None };
        if (key == Key.None) { if (normalized.Length==1 && char.IsAsciiDigit(normalized[0])) key=Key.D0+(normalized[0]-'0'); else if (!Enum.TryParse<Key>(normalized,true,out key)) throw new ArgumentException("Unknown DOS key: "+name); }
        var code=MainWindow.MapKey(key); return code!=0?code:throw new ArgumentException("Unsupported DOS key: "+name);
    }
}
