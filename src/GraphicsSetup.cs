using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SharpCompress.Archives.Rar;

namespace DosboxEnhanced;

public static class GraphicsSetup
{
    public const string ModelUrl = "https://github.com/NODIX-TECH/DLSS-5-MANAGER/releases/download/1.3.7/DLSS.5.MANAGER.rar";
    public const string ArchiveHash = "ac6ddca163ea5753d951af2e2d373197feaf1bdd4a027d308a38d5df491ee910";
    public const string ModelHash = "cdfa6c1ec784867182164c7836f324f087b5c9f374d0c9fe46f54999feabf3ed";
    public static string Hash(string file) { using var s=File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
    public static async Task Download(HttpClient client,string url,string file,string hash,IProgress<string>? progress,CancellationToken token)
    {
        if (File.Exists(file) && await Task.Run(()=>Hash(file)==hash,token)) { progress?.Report("Verified cached download"); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); string partial=file+".partial";
        try
        {
            using var response=await client.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token); response.EnsureSuccessStatusCode();
            const long limit=1024L*1024*1024;
            if(response.Content.Headers.ContentLength>limit) throw new InvalidDataException("Download exceeds the supported size.");
            using(var output=new FileStream(partial,FileMode.Create,FileAccess.Write,FileShare.None))
            using(var input=await response.Content.ReadAsStreamAsync(token))
            {
                var buffer=new byte[131072]; long total=0; int size;
                while((size=await input.ReadAsync(buffer,token))>0) { total+=size; if(total>limit)throw new InvalidDataException("Download exceeds the supported size."); await output.WriteAsync(buffer.AsMemory(0,size),token); progress?.Report($"Downloading · {total/1048576.0:0.0} MB"); }
            }
            if(await Task.Run(()=>Hash(partial),token)!=hash) throw new InvalidDataException("Download checksum mismatch; nothing installed.");
            File.Move(partial,file,true);
        }
        finally { if(File.Exists(partial))File.Delete(partial); }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TrustFile { public uint Size; public IntPtr Path, Handle, Subject; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct TrustData { public uint Size; public IntPtr Policy,Sip; public uint Ui,Revocation,Choice; public IntPtr File; public uint Action; public IntPtr State,Url; public uint Flags,Context; public IntPtr Signature; }
    [System.Runtime.InteropServices.DllImport("wintrust.dll",ExactSpelling=true)]
    static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]
    static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,EntryPoint="FindResourceW")]
    static extern IntPtr FindResource(IntPtr module,string name,IntPtr type);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    public static Task<bool> Signed(string file) => Task.Run(() =>
    {
        var path=System.Runtime.InteropServices.Marshal.StringToHGlobalUni(file);
        var pointer=System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf<TrustFile>());
        try {
            var entry=new TrustFile{Size=(uint)System.Runtime.InteropServices.Marshal.SizeOf<TrustFile>(),Path=path};
            System.Runtime.InteropServices.Marshal.StructureToPtr(entry,pointer,false);
            var data=new TrustData{Size=(uint)System.Runtime.InteropServices.Marshal.SizeOf<TrustData>(),Ui=2,Choice=1,File=pointer,Flags=0x1000};
            var action=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new IntPtr(-1),ref action,ref data)==0;
        }
        finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);System.Runtime.InteropServices.Marshal.FreeHGlobal(path);}
    });
    public static async Task ConfigureModel(string root,string model,string modelId)
    {
        model=Path.GetFullPath(model); if(!File.Exists(model))throw new FileNotFoundException("Choose a model DLL.");
        if(modelId is not ("original" or "small-fp8"))throw new ArgumentException("Choose Original or Lightweight.");
        string hash=await Task.Run(()=>Hash(model));
        if(hash!=ModelHash && !await Signed(model)) throw new InvalidDataException("Model signature is invalid and its hash is not the pinned compatibility runtime.");
        var resource=LoadLibraryEx(model,IntPtr.Zero,0x60); // Data/image resources only; never execute the selected DLL.
        if(resource==IntPtr.Zero)throw new InvalidDataException("Cannot read this model DLL's resources.");
        try { if(FindResource(resource,modelId=="small-fp8"?"WEIGHTS_CC_SMALL_FP8":"WEIGHTS_HT",new IntPtr(10))==IntPtr.Zero)throw new InvalidDataException("This DLL does not contain the selected model. Use another DLL or choose Original."); }
        finally{FreeLibrary(resource);}
        string path=Path.Combine(root,"graphics.json");
        var config=File.Exists(path)?JsonNode.Parse(File.ReadAllText(path))!.AsObject():new JsonObject();
        config["nr_dll"]=model; config["weight_source"]=new JsonObject{["kind"]="nvidia_dll",["path"]=model,["sha256"]=hash,["model"]=modelId};
        config["native_bundle"]="runtime/native"; config["cache"]="data/graphics-cache";
        config["sr_dll"]??="runtime/nvidia/nvngx_dlss.dll"; config["streamline"]??="runtime/nvidia/sl.interposer.dll";
        Paths.AtomicWrite(path,config.ToJsonString(Paths.Json));
    }
    public static async Task RepairBundled(string root,IProgress<string> progress,CancellationToken token)
    {
        using var http=new HttpClient { Timeout=TimeSpan.FromMinutes(15) };http.DefaultRequestHeaders.UserAgent.ParseAdd("DOSBox-Enhanced/0.6");
        const string api="https://api.github.com/repos/RazvanManolache/dosbox-enhanced/releases/tags/v0.6";
        var release=JsonNode.Parse(await http.GetStringAsync(api,token))!;
        var asset=release["assets"]!.AsArray().Single(a=>a!["name"]!.GetValue<string>()=="DOSBox-Enhanced-0.6-win-x64.zip")!;
        string url=asset["browser_download_url"]!.GetValue<string>();
        if(!url.StartsWith("https://github.com/RazvanManolache/dosbox-enhanced/releases/download/v0.6/",StringComparison.Ordinal))throw new InvalidDataException("Unexpected release download location.");
        string? digest=asset["digest"]?.GetValue<string>();
        if(digest is null||!digest.StartsWith("sha256:")||digest.Length!=71)throw new InvalidDataException("GitHub did not provide a release checksum. Re-extract the verified portable release.");
        string archive=Path.Combine(root,"data/downloads/dosbox-components-0.6.zip");await Download(http,url,archive,digest[7..],progress,token);
        using var zip=ZipFile.OpenRead(archive);const string prefix="DOSBox-Enhanced-0.6-win-x64/";
        var manifest=JsonNode.Parse(new StreamReader((zip.GetEntry(prefix+"FILES.json")??throw new InvalidDataException("Release manifest missing.")).Open()).ReadToEnd())!.AsArray();
        foreach(var item in manifest)
        {
            string path=item!["path"]!.GetValue<string>();
            if(!(path is "runtime/phoenix_native.dll" or "runtime/dosbox_nvngx.dll" or "runtime/dosbox_pure_libretro.dll" || path.StartsWith("runtime/native/",StringComparison.Ordinal)))continue;
            string target=Path.GetFullPath(Path.Combine(root,path));
            if(!target.StartsWith(Path.GetFullPath(Path.Combine(root,"runtime"))+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid release component path.");
            string expected=item["sha256"]!.GetValue<string>();if(File.Exists(target)&&Hash(target)==expected)continue;
            token.ThrowIfCancellationRequested();var entry=zip.GetEntry(prefix+path)??throw new InvalidDataException("Release component absent.");
            if(entry.Length>64L*1024*1024)throw new InvalidDataException("Oversized release component.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);string temp=target+".partial";
            try{entry.ExtractToFile(temp,true);if(Hash(temp)!=expected)throw new InvalidDataException("Release component checksum mismatch.");File.Move(temp,target,true);}
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        progress.Report("Bundled engines repaired. Restart the app to apply.");
    }
    public static async Task Install(string root,bool model,IProgress<string> progress,CancellationToken token)
    {
        using var http=new HttpClient { Timeout=TimeSpan.FromMinutes(15) }; http.DefaultRequestHeaders.UserAgent.ParseAdd("DOSBox-Enhanced/0.6");
        var spec=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"runtime.lock.json")))!["nvidia"]!;
        var downloads=Path.Combine(root,"data/downloads"); Directory.CreateDirectory(downloads);
        string archive=Path.Combine(downloads,"streamline-"+spec["version"]!.GetValue<string>()+".zip");
        await Download(http,spec["url"]!.GetValue<string>(),archive,spec["archiveSha256"]!.GetValue<string>(),progress,token);
        // A versioned vendor folder also avoids overwriting a DLL in a running session.
        string vendor=Path.Combine(root,"runtime/vendor/streamline-"+spec["version"]!.GetValue<string>()); Directory.CreateDirectory(vendor);
        using(var zip=ZipFile.OpenRead(archive))
        {
            foreach(var item in spec["files"]!.AsArray())
            {
                token.ThrowIfCancellationRequested(); string name=item!["name"]!.GetValue<string>();
                if(Path.GetFileName(name)!=name)throw new InvalidDataException("Invalid runtime file name.");
                string target=Path.Combine(vendor,name),temp=Path.Combine(vendor,".pending",name);
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                if(File.Exists(target)&&Hash(target)==item["sha256"]!.GetValue<string>())continue;
                var entry=zip.GetEntry("bin/x64/"+name)??throw new InvalidDataException("Runtime missing from verified archive.");
                try { entry.ExtractToFile(temp,true); if(Hash(temp)!=item["sha256"]!.GetValue<string>()||!await Signed(temp))throw new InvalidDataException("Runtime authentication failed: "+name); File.Move(temp,target,true); }
                finally { if(File.Exists(temp))File.Delete(temp); }
            }
            foreach(string name in new[]{"license.txt","3rd-party-licenses.md","bin/x64/nvngx_dlss.license.txt","bin/x64/reflex.license.txt"})
                zip.GetEntry(name)?.ExtractToFile(Path.Combine(vendor,Path.GetFileName(name)),true);
        }
        var configFile=Path.Combine(root,"graphics.json");var config=File.Exists(configFile)?JsonNode.Parse(File.ReadAllText(configFile))!.AsObject():new JsonObject();
        config["sr_dll"]=Path.GetRelativePath(root,Path.Combine(vendor,"nvngx_dlss.dll"));config["streamline"]=Path.GetRelativePath(root,Path.Combine(vendor,"sl.interposer.dll"));
        config["native_bundle"]="runtime/native";config["cache"]="data/graphics-cache";
        Paths.AtomicWrite(configFile,config.ToJsonString(Paths.Json));
        if(model)
        {
            string rar=Path.Combine(downloads,"opennr-1.3.7.rar");await Download(http,ModelUrl,rar,ArchiveHash,progress,token);
            string folder=Path.Combine(root,"data/models/opennr-310.9.16");Directory.CreateDirectory(folder);string file=Path.Combine(folder,"nvngx_dlssnr.dll"),temp=file+".partial";
            using var source=RarArchive.OpenArchive(rar);
            var entries=source.Entries.Where(e=>e.Key?.Replace('\\','/').Equals("mod files/dlss 5/nvngx_dlssnr.dll",StringComparison.OrdinalIgnoreCase)==true).ToArray();
            if(entries.Length!=1||entries[0].IsEncrypted||entries[0].Size!=276162560)throw new InvalidDataException("Unexpected compatibility runtime member.");
            try
            {
                using(var input=await entries[0].OpenEntryStreamAsync(token))using(var output=File.Create(temp))await input.CopyToAsync(output,token);
                if(Hash(temp)!=ModelHash)throw new InvalidDataException("Compatibility runtime checksum mismatch.");File.Move(temp,file,true);
            }
            finally { if(File.Exists(temp))File.Delete(temp); }
            await ConfigureModel(root,file,"original");
        }
        progress.Report("Files verified and configured. Restart DOSBox Enhanced to apply.");
    }
    public static string Inspect(string root)
    {
        string PathStatus(string label,string path)=>$"{(File.Exists(path)?"✓":"○")} {label}";
        JsonObject c=new();try{c=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"graphics.json")))!.AsObject();}catch{}
        string P(string key){try{var p=c[key]?.GetValue<string>()??"";return string.IsNullOrWhiteSpace(p)?"":Path.GetFullPath(Path.Combine(root,p));}catch{return "";}}
        return string.Join("\n",new[]{PathStatus("DOS engine",Path.Combine(root,"runtime/dosbox_pure_libretro.dll")),PathStatus("Phoenix 0.17 library",Path.Combine(root,"runtime/phoenix_native.dll")),PathStatus("DOS graphics bridge",Path.Combine(root,"runtime/dosbox_nvngx.dll")),PathStatus("Neural kernel bundle",Path.Combine(root,"runtime/native/native_manifest.json")),PathStatus("Model DLL",P("nr_dll")),PathStatus("NVIDIA upscaling",P("sr_dll")),PathStatus("NVIDIA frame generation / Streamline",P("streamline"))});
    }
}
