#include "bridge.h"
#include <windows.h>
#include <wrl/client.h>
#include <dxgi1_6.h>
#include <dxgidebug.h>
#include <d3d12.h>
#include <d3d12sdklayers.h>
#include <d3dcompiler.h>
#include <wintrust.h>
#include <softpub.h>
#include <immintrin.h>
#include <nvsdk_ngx.h>
#include <nvsdk_ngx_defs.h>
#include <sl.h>
#include <sl_dlss_g.h>
#include <sl_reflex.h>
#include <phoenix_native.h>
#include <json.hpp>
#include <filesystem>
#include <vector>
#include <string>
#include <stdexcept>
#include <chrono>
#include <thread>
#include <sstream>
#include <cmath>
#include <algorithm>
#include <fstream>
using Microsoft::WRL::ComPtr;
using Json = nlohmann::json;
namespace fs = std::filesystem;
static thread_local std::string last_error;
static uint16_t half_value(float f,int rounding) { return uint16_t(_mm_extract_epi16(_mm_cvtps_ph(_mm_set_ss(f),0),0)); }
static float float_value(uint16_t h) { return _mm_cvtss_f32(_mm_cvtph_ps(_mm_cvtsi32_si128(h))); }
static void require(bool condition, const std::string& reason) { if(!condition) throw std::runtime_error(reason); }
static void hr(HRESULT code) { if(FAILED(code)) { char s[48]; sprintf_s(s,"Graphics HRESULT 0x%08X",unsigned(code)); throw std::runtime_error(s); } }
static void ngx(NVSDK_NGX_Result code, const char* stage) { if(!NVSDK_NGX_SUCCEED(code)) { char s[128]; sprintf_s(s,"%s: NGX 0x%08X",stage,unsigned(code)); throw std::runtime_error(s); } }
template<class T> static T symbol(HMODULE module, const char* name) { auto fn=GetProcAddress(module,name); require(fn!=nullptr,std::string("Missing export: ")+name); return reinterpret_cast<T>(fn); }
static void transition(ID3D12GraphicsCommandList* cmd, ID3D12Resource* image, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
 if(before==after) return; D3D12_RESOURCE_BARRIER b{}; b.Type=D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; b.Transition={image,D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,before,after};cmd->ResourceBarrier(1,&b);
}
static bool signed_file(const fs::path& file) {
 WINTRUST_FILE_INFO f{}; f.cbStruct=sizeof(f);f.pcwszFilePath=file.c_str();
 WINTRUST_DATA d{};d.cbStruct=sizeof(d);d.dwUIChoice=WTD_UI_NONE;d.fdwRevocationChecks=WTD_REVOKE_NONE;d.dwUnionChoice=WTD_CHOICE_FILE;d.pFile=&f;d.dwStateAction=WTD_STATEACTION_VERIFY;d.dwProvFlags=WTD_CACHE_ONLY_URL_RETRIEVAL;
 GUID action=WINTRUST_ACTION_GENERIC_VERIFY_V2;auto result=WinVerifyTrust(nullptr,&action,&d);d.dwStateAction=WTD_STATEACTION_CLOSE;WinVerifyTrust(nullptr,&action,&d);return result==ERROR_SUCCESS;
}
struct Plane { ComPtr<ID3D12Resource> image,transfer;D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};UINT64 bytes=0;unsigned pixel_bytes=0; };
struct Bridge {
 const std::thread::id owner=std::this_thread::get_id();Json config,status;
 unsigned sw,sh,iw,ih,ow,oh,nw,nh;bool upscale,upscale_first,input_linear;
 HWND hwnd;ComPtr<ID3D12Device> device,proxy_device;ComPtr<ID3D12CommandQueue> queue,proxy_queue;
 ComPtr<IDXGIFactory4> factory;ComPtr<IDXGISwapChain3> swap;ComPtr<ID3D12Resource> backbuffers[3];
 ComPtr<ID3D12CommandAllocator> allocator;ComPtr<ID3D12GraphicsCommandList> command;ComPtr<ID3D12Fence> fence;HANDLE fence_event=nullptr;uint64_t fence_value=0;
 Plane color,motion,depth,neural_motion,neural,feedback,upscaled,final;ComPtr<ID3D12DescriptorHeap> descriptors;ComPtr<ID3D12RootSignature> root;ComPtr<ID3D12PipelineState> compose;
 HMODULE interposer=nullptr,nr_module=nullptr;bool sl_ready=false,ngx_ready=false,fg_active=false,reset_needed=true;
 PFun_slInit* sl_init=nullptr;PFun_slShutdown* sl_shutdown=nullptr;PFun_slUpgradeInterface* sl_upgrade=nullptr;
 PFun_slSetD3DDevice* sl_device=nullptr;PFun_slGetNativeInterface* sl_native=nullptr;PFun_slGetFeatureFunction* sl_feature=nullptr;
 PFun_slGetNewFrameToken* sl_token=nullptr;PFun_slSetConstants* sl_constants=nullptr;PFun_slSetTagForFrame* sl_tags=nullptr;
 PFun_slDLSSGSetOptions* fg_options=nullptr;PFun_slDLSSGGetState* fg_state=nullptr;PFun_slReflexSetOptions* reflex_options=nullptr;PFun_slReflexSleep* reflex_sleep=nullptr;PFun_slPCLSetMarker* marker=nullptr;
 sl::ViewportHandle viewport{0};uint32_t frame_index=0;uint64_t frames=0,presented=0,generated=0,nr_frames=0,phoenix_frames=0,sr_frames=0;
 NVSDK_NGX_Parameter* parameters=nullptr,*sr_params=nullptr;NVSDK_NGX_Handle* nr_handle=nullptr,*sr_handle=nullptr;
 using NrInit=NVSDK_NGX_Result(NVSDK_CONV*)(unsigned long long,const wchar_t*,ID3D12Device*,NVSDK_NGX_Version,const NVSDK_NGX_Parameter*);
 using NrCreate=NVSDK_NGX_Result(NVSDK_CONV*)(ID3D12GraphicsCommandList*,NVSDK_NGX_Feature,const NVSDK_NGX_Parameter*,NVSDK_NGX_Handle**);
 using NrEval=NVSDK_NGX_Result(NVSDK_CONV*)(ID3D12GraphicsCommandList*,const NVSDK_NGX_Handle*,const NVSDK_NGX_Parameter*,PFN_NVSDK_NGX_ProgressCallback);
 using NrRelease=NVSDK_NGX_Result(NVSDK_CONV*)(NVSDK_NGX_Handle*);
 NrCreate nr_create=nullptr;NrEval nr_eval=nullptr;NrRelease nr_release=nullptr;
 PhoenixNativeSession* phoenix=nullptr;std::vector<uint8_t> previous_luma;std::string fg_error;double neural_ms=0,sr_ms=0,total_ms=0;
 Bridge(Json c,HWND window):config(std::move(c)),hwnd(window) {
  geometry(config);
  try { init_streamline();
  UINT factory_flags=0;
  if(config.value("debug",false)){ComPtr<ID3D12Debug> debug;if(SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)))){debug->EnableDebugLayer();factory_flags=DXGI_CREATE_FACTORY_DEBUG;}}
  hr(CreateDXGIFactory2(factory_flags,IID_PPV_ARGS(&factory)));ComPtr<IDXGIAdapter1> adapter;
  for(UINT i=0;factory->EnumAdapters1(i,&adapter)!=DXGI_ERROR_NOT_FOUND;++i){DXGI_ADAPTER_DESC1 desc{};adapter->GetDesc1(&desc);if(desc.VendorId==0x10de && !(desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE))break;adapter.Reset();}
  require(adapter!=nullptr,"NVIDIA graphics adapter not found");hr(D3D12CreateDevice(adapter.Get(),D3D_FEATURE_LEVEL_12_0,IID_PPV_ARGS(&device)));
  proxy_device=device;
  if(sl_ready){sl_check(sl_device(device.Get()),"Streamline device");sl_check(sl_upgrade(reinterpret_cast<void**>(proxy_device.GetAddressOf())),"Streamline device proxy");sl_check(sl_upgrade(reinterpret_cast<void**>(factory.GetAddressOf())),"Streamline factory proxy");}
  D3D12_COMMAND_QUEUE_DESC q{};hr(proxy_device->CreateCommandQueue(&q,IID_PPV_ARGS(&proxy_queue)));queue=proxy_queue;
  if(sl_ready){queue.Reset();sl_check(sl_native(proxy_queue.Get(),reinterpret_cast<void**>(queue.GetAddressOf())),"Streamline native queue");}
  hr(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT,IID_PPV_ARGS(&allocator)));hr(device->CreateCommandList(0,D3D12_COMMAND_LIST_TYPE_DIRECT,allocator.Get(),nullptr,IID_PPV_ARGS(&command)));hr(command->Close());
  hr(device->CreateFence(0,D3D12_FENCE_FLAG_NONE,IID_PPV_ARGS(&fence)));fence_event=CreateEventW(nullptr,FALSE,FALSE,nullptr);require(fence_event!=nullptr,"Graphics fence event unavailable");
  images();
  init_shader();init_ngx();if(hwnd)create_swap(false);
  } catch (...) { cleanup(); throw; }
 }
 void geometry(const Json& c) {
  unsigned w=c.at("source_width"),h=c.at("source_height");bool sr=c.value("upscaling",true);unsigned factor=sr?c.value("upscale_factor",4u):1u;double entry=c.value("input_factor",1.0);
  require(w&&h&&w<=4096&&h<=4096&&factor>=1&&factor<=4&&std::isfinite(entry)&&entry>=.25&&entry<=4,"Invalid graphics dimensions or input scale (0.25 to 4)");
  unsigned width=std::max(1u,unsigned(std::floor(w*entry+.5))),height=std::max(1u,unsigned(std::floor(h*entry+.5)));
  require(width*factor<=8192&&height*factor<=8192&&size_t(width)*height*factor*factor<=16777216,"Requested graphics output is too large");
  sw=w;sh=h;input_linear=c.value("input_filter",std::string("nearest"))=="linear";iw=width;ih=height;upscale=sr;upscale_first=sr&&c.value("upscale_order",std::string("after_nr"))=="before_nr";ow=iw*factor;oh=ih*factor;nw=upscale_first?ow:iw;nh=upscale_first?oh:ih;
  if(!status.is_object())status=Json::object();
  status.update({{"source_width",sw},{"source_height",sh},{"input_width",iw},{"input_height",ih},{"neural_width",nw},{"neural_height",nh},{"output_width",ow},{"output_height",oh},{"upscale_order",upscale_first?"before_nr":"after_nr"},{"upscale_factor",factor},{"input_factor",entry},{"input_filter",input_linear?"linear":"nearest"},{"pre_resize",iw!=sw||ih!=sh},{"motion_source","estimated block motion from processing input"},{"depth_source","flat 2D composition plane"}});
 }
 void images() {
  color=plane(iw,ih,DXGI_FORMAT_R16G16B16A16_FLOAT,8,true);motion=plane(iw,ih,DXGI_FORMAT_R16G16_FLOAT,4,true);depth=plane(iw,ih,DXGI_FORMAT_R32_FLOAT,4,true);
  if(upscale_first)neural_motion=plane(nw,nh,DXGI_FORMAT_R16G16_FLOAT,4,true);
  neural=plane(nw,nh,DXGI_FORMAT_R16G16B16A16_FLOAT,8,false);feedback=plane(nw,nh,DXGI_FORMAT_R16G16B16A16_FLOAT,8,true);upscaled=plane(ow,oh,DXGI_FORMAT_R16G16B16A16_FLOAT,8,false);final=plane(ow,oh,DXGI_FORMAT_R8G8B8A8_UNORM,4,false,true);
 }
 void reconfigure(const Json& c) {
  require(owner==std::this_thread::get_id(),"Graphics session thread mismatch");wait();
  if(fg_options){sl::DLSSGOptions off{};off.mode=sl::DLSSGMode::eOff;sl_check(fg_options(viewport,off),"Suspend frame generation for layout change");}fg_active=false;
  swap.Reset();for(auto& b:backbuffers)b.Reset();
  if(phoenix){require(phoenix_native_destroy(phoenix)==0,phoenix_native_error());phoenix=nullptr;}
  if(nr_handle){ngx(nr_release(nr_handle),"Release NR for layout change");nr_handle=nullptr;}
  if(sr_handle){ngx(NVSDK_NGX_D3D12_ReleaseFeature(sr_handle),"Release SR for layout change");sr_handle=nullptr;}
  parameters->Reset();sr_params->Reset();color={};motion={};depth={};neural_motion={};neural={};feedback={};upscaled={};final={};
  geometry(c);images();previous_luma.clear();reset_needed=true;if(hwnd)create_swap(false);
 }
 void sl_check(sl::Result r,const char* stage) { require(r==sl::Result::eOk,std::string(stage)+": Streamline result "+std::to_string(int(r))); }
 template<class T> T* feature(sl::Feature f,const char* name) {void* fn=nullptr;sl_check(sl_feature(f,name,fn),name);require(fn!=nullptr,std::string("Unavailable Streamline feature ")+name);return reinterpret_cast<T*>(fn);}
 void init_streamline() {
  auto path=fs::u8path(config.at("streamline").get<std::string>());require(signed_file(path),"Streamline signature validation failed");
  interposer=LoadLibraryExW(path.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);require(interposer!=nullptr,"Cannot load Streamline");
  sl_init=symbol<PFun_slInit*>(interposer,"slInit");sl_shutdown=symbol<PFun_slShutdown*>(interposer,"slShutdown");sl_upgrade=symbol<PFun_slUpgradeInterface*>(interposer,"slUpgradeInterface");sl_device=symbol<PFun_slSetD3DDevice*>(interposer,"slSetD3DDevice");sl_native=symbol<PFun_slGetNativeInterface*>(interposer,"slGetNativeInterface");sl_feature=symbol<PFun_slGetFeatureFunction*>(interposer,"slGetFeatureFunction");sl_token=symbol<PFun_slGetNewFrameToken*>(interposer,"slGetNewFrameToken");sl_constants=symbol<PFun_slSetConstants*>(interposer,"slSetConstants");sl_tags=symbol<PFun_slSetTagForFrame*>(interposer,"slSetTagForFrame");
  sl::Preferences pref{};sl::Feature features[]={sl::kFeatureDLSS_G,sl::kFeatureReflex};auto plugins=path.parent_path().wstring();const wchar_t* paths[]={plugins.c_str()};auto logs=fs::u8path(config.at("cache").get<std::string>()).wstring();fs::create_directories(logs);
  pref.featuresToLoad=features;pref.numFeaturesToLoad=2;pref.pathsToPlugins=paths;pref.numPathsToPlugins=1;pref.pathToLogsAndData=logs.c_str();pref.projectId="853cf9ad-19cb-4f49-96b6-7b3896477d20";pref.engine=sl::EngineType::eCustom;pref.engineVersion="1.0";pref.flags=sl::PreferenceFlags::eUseManualHooking|sl::PreferenceFlags::eUseFrameBasedResourceTagging|sl::PreferenceFlags::eDisableCLStateTracking;pref.logLevel=config.value("debug",false)?sl::LogLevel::eVerbose:sl::LogLevel::eDefault;
  sl_check(sl_init(pref,sl::kSDKVersion),"Streamline initialization");sl_ready=true; HMODULE pinned=nullptr;require(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN|GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,reinterpret_cast<LPCWSTR>(sl_init),&pinned)!=FALSE,"Cannot retain process-wide graphics hooks");
 }
 Plane plane(unsigned w,unsigned h,DXGI_FORMAT fmt,unsigned pixel,bool upload,bool readback=false){
  Plane p;p.pixel_bytes=pixel;D3D12_HEAP_PROPERTIES heap{};heap.Type=D3D12_HEAP_TYPE_DEFAULT;
  D3D12_RESOURCE_DESC d{};d.Dimension=D3D12_RESOURCE_DIMENSION_TEXTURE2D;d.Width=w;d.Height=h;d.DepthOrArraySize=1;d.MipLevels=1;d.Format=fmt;d.SampleDesc.Count=1;d.Flags=D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
  hr(device->CreateCommittedResource(&heap,D3D12_HEAP_FLAG_NONE,&d,upload?D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE:D3D12_RESOURCE_STATE_UNORDERED_ACCESS,nullptr,IID_PPV_ARGS(&p.image)));
  device->GetCopyableFootprints(&d,0,1,0,&p.footprint,nullptr,nullptr,&p.bytes);
  if(upload||readback){heap.Type=upload?D3D12_HEAP_TYPE_UPLOAD:D3D12_HEAP_TYPE_READBACK;D3D12_RESOURCE_DESC b{};b.Dimension=D3D12_RESOURCE_DIMENSION_BUFFER;b.Width=p.bytes;b.Height=1;b.DepthOrArraySize=1;b.MipLevels=1;b.SampleDesc.Count=1;b.Layout=D3D12_TEXTURE_LAYOUT_ROW_MAJOR;hr(device->CreateCommittedResource(&heap,D3D12_HEAP_FLAG_NONE,&b,upload?D3D12_RESOURCE_STATE_GENERIC_READ:D3D12_RESOURCE_STATE_COPY_DEST,nullptr,IID_PPV_ARGS(&p.transfer)));}
  return p;
 }
 void begin(){hr(allocator->Reset());hr(command->Reset(allocator.Get(),nullptr));}
 void wait(){auto v=++fence_value;hr(queue->Signal(fence.Get(),v));if(fence->GetCompletedValue()<v){hr(fence->SetEventOnCompletion(v,fence_event));require(WaitForSingleObject(fence_event,15000)==WAIT_OBJECT_0,"Graphics queue timed out");}}
 void submit(){hr(command->Close());ID3D12CommandList* lists[]={command.Get()};queue->ExecuteCommandLists(1,lists);wait();}
 void upload(Plane& p,const void* data,unsigned h,unsigned w){void* dst=nullptr;hr(p.transfer->Map(0,nullptr,&dst));for(unsigned y=0;y<h;y++)memcpy(static_cast<uint8_t*>(dst)+p.footprint.Offset+y*p.footprint.Footprint.RowPitch,static_cast<const uint8_t*>(data)+size_t(y)*w*p.pixel_bytes,size_t(w)*p.pixel_bytes);p.transfer->Unmap(0,nullptr);transition(command.Get(),p.image.Get(),D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,D3D12_RESOURCE_STATE_COPY_DEST);D3D12_TEXTURE_COPY_LOCATION from{},to{};from.pResource=p.transfer.Get();from.Type=D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;from.PlacedFootprint=p.footprint;to.pResource=p.image.Get();to.Type=D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;command->CopyTextureRegion(&to,0,0,0,&from,nullptr);transition(command.Get(),p.image.Get(),D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);}
 void init_ngx(){auto dir=fs::u8path(config.at("sr_dll").get<std::string>()).parent_path().wstring();const wchar_t* paths[]={dir.c_str()};NVSDK_NGX_FeatureCommonInfo info{};info.PathListInfo.Path=paths;info.PathListInfo.Length=1;auto cache=fs::u8path(config.at("cache").get<std::string>()).wstring();ngx(NVSDK_NGX_D3D12_Init_with_ProjectID("853cf9ad-19cb-4f49-96b6-7b3896477d20",NVSDK_NGX_ENGINE_TYPE_CUSTOM,"1.0",cache.c_str(),device.Get(),&info),"NGX initialization");ngx_ready=true;ngx(NVSDK_NGX_D3D12_AllocateParameters(&parameters),"NR parameters");ngx(NVSDK_NGX_D3D12_AllocateParameters(&sr_params),"SR parameters");}
 void init_shader();void ensure_nr();void run_nr(const Json& options,bool reset);void ensure_sr();void run_sr(bool reset);void run_phoenix(const Json& options,bool reset);
 void create_swap(bool fg);void prepare_fg(bool requested);void present(bool requested,bool reset);
 void frame(const uint8_t* input,size_t capacity,unsigned w,unsigned h,const Json& options,uint8_t* output,size_t output_capacity);
 void record_error(const char* reason){std::ofstream log(fs::u8path(config.at("cache").get<std::string>())/"bridge-error.txt");log<<reason<<"\n"<<status.dump(2)<<"\n";ComPtr<ID3D12InfoQueue> q;if(SUCCEEDED(device.As(&q))){for(UINT64 i=0;i<q->GetNumStoredMessages();i++){SIZE_T n=0;q->GetMessage(i,nullptr,&n);std::vector<uint8_t> bytes(n);auto* m=reinterpret_cast<D3D12_MESSAGE*>(bytes.data());q->GetMessage(i,m,&n);if(m->Severity<=D3D12_MESSAGE_SEVERITY_WARNING)log<<unsigned(m->ID)<<": "<<m->pDescription<<"\n";}} ComPtr<IDXGIInfoQueue> dx;if(SUCCEEDED(DXGIGetDebugInterface1(0,IID_PPV_ARGS(&dx))))for(UINT64 i=0;i<dx->GetNumStoredMessages(DXGI_DEBUG_ALL);i++){SIZE_T n=0;dx->GetMessage(DXGI_DEBUG_ALL,i,nullptr,&n);std::vector<uint8_t> bytes(n);auto* m=reinterpret_cast<DXGI_INFO_QUEUE_MESSAGE*>(bytes.data());dx->GetMessage(DXGI_DEBUG_ALL,i,m,&n);log<<"DXGI "<<m->ID<<": "<<m->pDescription<<"\n";}}
 void cleanup(){if(queue&&fence)try{wait();}catch(...){}if(phoenix)phoenix_native_destroy(phoenix);if(nr_handle&&nr_release)nr_release(nr_handle);if(sr_handle)NVSDK_NGX_D3D12_ReleaseFeature(sr_handle);if(parameters)NVSDK_NGX_D3D12_DestroyParameters(parameters);if(sr_params)NVSDK_NGX_D3D12_DestroyParameters(sr_params);swap.Reset();for(auto& b:backbuffers)b.Reset();if(sl_ready)sl_shutdown();if(ngx_ready)NVSDK_NGX_D3D12_Shutdown1(device.Get());if(nr_module)FreeLibrary(nr_module);factory.Reset();proxy_queue.Reset();proxy_device.Reset();queue.Reset();device.Reset();if(interposer)FreeLibrary(interposer);if(fence_event)CloseHandle(fence_event);}
 ~Bridge(){cleanup();}
};
void Bridge::init_shader(){
 const char* source=R"(
 Texture2D<float4> original:register(t0);Texture2D<float4> processed:register(t1);RWTexture2D<float4> target:register(u0);
 SamplerState linearSampler:register(s0);SamplerState pointSampler:register(s1);cbuffer Params:register(b0){float strength;uint compare;uint width;uint height;uint look;uint sharp;};
 [numthreads(8,8,1)]void main(uint3 id:SV_DispatchThreadID){if(id.x>=width||id.y>=height)return;float2 uv=(float2(id.xy)+0.5)/float2(width,height);float4 a=sharp?original.SampleLevel(pointSampler,uv,0):original.SampleLevel(linearSampler,uv,0);float4 b=sharp?processed.SampleLevel(pointSampler,uv,0):processed.SampleLevel(linearSampler,uv,0);float3 rgb=lerp(a.rgb,b.rgb,strength);if(look==1)rgb=rgb*float3(1.07,1.01,0.94);if(look==2)rgb=rgb*float3(0.94,1.02,1.07);if(compare&&id.x<width/2)rgb=a.rgb;target[id.xy]=float4(saturate(rgb),1);})";
 ComPtr<ID3DBlob> shader,error;hr(D3DCompile(source,strlen(source),nullptr,nullptr,nullptr,"main","cs_5_0",D3DCOMPILE_OPTIMIZATION_LEVEL3,0,&shader,&error));
 D3D12_DESCRIPTOR_RANGE ranges[2]{};ranges[0]={D3D12_DESCRIPTOR_RANGE_TYPE_SRV,2,0,0,0};ranges[1]={D3D12_DESCRIPTOR_RANGE_TYPE_UAV,1,0,0,2};
 D3D12_ROOT_PARAMETER params[2]{};params[0].ParameterType=D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;params[0].DescriptorTable={2,ranges};params[1].ParameterType=D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;params[1].Constants={0,0,6};
 D3D12_STATIC_SAMPLER_DESC sampler{};sampler.Filter=D3D12_FILTER_MIN_MAG_MIP_LINEAR;sampler.AddressU=sampler.AddressV=sampler.AddressW=D3D12_TEXTURE_ADDRESS_MODE_CLAMP;sampler.MaxLOD=D3D12_FLOAT32_MAX;sampler.ShaderVisibility=D3D12_SHADER_VISIBILITY_ALL;
 D3D12_STATIC_SAMPLER_DESC samplers[2]={sampler,sampler};samplers[1].Filter=D3D12_FILTER_MIN_MAG_MIP_POINT;samplers[1].ShaderRegister=1;D3D12_ROOT_SIGNATURE_DESC desc{};desc.NumParameters=2;desc.pParameters=params;desc.NumStaticSamplers=2;desc.pStaticSamplers=samplers;ComPtr<ID3DBlob> serialized;hr(D3D12SerializeRootSignature(&desc,D3D_ROOT_SIGNATURE_VERSION_1,&serialized,&error));hr(device->CreateRootSignature(0,serialized->GetBufferPointer(),serialized->GetBufferSize(),IID_PPV_ARGS(&root)));
 D3D12_COMPUTE_PIPELINE_STATE_DESC pipeline{};pipeline.pRootSignature=root.Get();pipeline.CS={shader->GetBufferPointer(),shader->GetBufferSize()};hr(device->CreateComputePipelineState(&pipeline,IID_PPV_ARGS(&compose)));
 D3D12_DESCRIPTOR_HEAP_DESC heap{};heap.Type=D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;heap.NumDescriptors=3;heap.Flags=D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;hr(device->CreateDescriptorHeap(&heap,IID_PPV_ARGS(&descriptors)));
}
void Bridge::ensure_nr(){
 if(nr_handle)return;
 if(!nr_module){auto dll=fs::u8path(config.at("nr_dll").get<std::string>());nr_module=LoadLibraryExW(dll.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);require(nr_module!=nullptr,"Cannot load NVIDIA NR runtime");
 auto init=symbol<NrInit>(nr_module,"NVSDK_NGX_D3D12_Init_Ext");auto cache=fs::u8path(config.at("cache").get<std::string>()).wstring();ngx(init(0x1000000ULL,cache.c_str(),device.Get(),NVSDK_NGX_Version_API,parameters),"NVIDIA NR initialization");
 nr_create=symbol<NrCreate>(nr_module,"NVSDK_NGX_D3D12_CreateFeature");nr_eval=symbol<NrEval>(nr_module,"NVSDK_NGX_D3D12_EvaluateFeature");nr_release=symbol<NrRelease>(nr_module,"NVSDK_NGX_D3D12_ReleaseFeature");}parameters->Reset();
 parameters->Set("CreationNodeMask",1u);parameters->Set("VisibilityNodeMask",1u);
 for(auto key:{"DLSSNR.Width","DLSSNR.InputWidth","DLSSNR.OutputWidth","DLSSNR.Output.Width","Width"})parameters->Set(key,nw);
 for(auto key:{"DLSSNR.Height","DLSSNR.InputHeight","DLSSNR.OutputHeight","DLSSNR.Output.Height","Height"})parameters->Set(key,nh);
 parameters->Set("DLSSNR.Upscaling",0u);parameters->Set("DLSSNR.Scale",1.f);parameters->Set("DLSSNR.ScalingRatio",1.f);parameters->Set("MV.Scale.X",1.f);parameters->Set("MV.Scale.Y",1.f);parameters->Set("DLSSNR.Hint.Render.Preset",0u);parameters->Set("PerfQualityValue",0u);parameters->Set("DLSSNR.Hdr",0u);parameters->Set("DLSSNR.SDR",1u);parameters->Set("DLSSNR.AutoExposure",0u);parameters->Set("InPreExposure",1.f);parameters->Set("InExposureScale",1.f);parameters->Set("DLSS.Feature.Create.Flags",0u);
 begin();ngx(nr_create(command.Get(),static_cast<NVSDK_NGX_Feature>(18),parameters,&nr_handle),"NVIDIA NR creation");submit();require(nr_handle!=nullptr,"NVIDIA NR handle absent");
}
void Bridge::run_nr(const Json& o,bool reset){
 ensure_nr();parameters->Reset();
 for(auto key:{"DLSSNR.Color","Color"})parameters->Set(key,(upscale_first?upscaled:color).image.Get());for(auto key:{"DLSSNR.Output","Output"})parameters->Set(key,neural.image.Get());for(auto key:{"DLSSNR.MVec","MVec","MotionVectors"})parameters->Set(key,(upscale_first?neural_motion:motion).image.Get());
 for(auto prefix:{"Color","MVec","Output"}){auto base=std::string("DLSSNR.")+prefix;parameters->Set((base+"SubrectBaseX").c_str(),0u);parameters->Set((base+"SubrectBaseY").c_str(),0u);parameters->Set((base+"SubrectWidth").c_str(),nw);parameters->Set((base+"SubrectHeight").c_str(),nh);}
 parameters->Set("Width",nw);parameters->Set("Height",nh);parameters->Set("DLSSNR.MVecScaleX",1.f);parameters->Set("DLSSNR.MVecScaleY",1.f);parameters->Set("DLSSNR.OutputWidth",nw);parameters->Set("DLSSNR.OutputHeight",nh);parameters->Set("DLSSNR.Scale",1.f);parameters->Set("DLSSNR.ScalingRatio",1.f);parameters->Set("DLSSNR.Enabled",1u);parameters->Set("DLSSNR.Reset",unsigned(reset));parameters->Set("DLSSNR.Intensity",1.f);parameters->Set("DLSSNR.LocalToneStrength",o.value("Tone",1.f));parameters->Set("DLSSNR.LocalStructureStrength",o.value("Structure",1.f));parameters->Set("DLSSNR.SkinStructureStrength",-1.f);parameters->Set("DLSSNR.UseAutoMask",1u);parameters->Set("DLSSNR.Style",0u);parameters->Set("DLSSNR.UICorrection",1u);parameters->Set("DLSSNR.GlobalToneStrength",1.f);parameters->Set("DLSSNR.Hdr",0u);parameters->Set("DLSSNR.SDR",1u);parameters->Set("DLSSNR.Jitter.Offset.X",0.f);parameters->Set("DLSSNR.Jitter.Offset.Y",0.f);parameters->Set("Jitter.Offset.X",0.f);parameters->Set("Jitter.Offset.Y",0.f);parameters->Set("DLSS.Pre.Exposure",1.f);parameters->Set("DLSS.Exposure.Scale",1.f);
 begin();ngx(nr_eval(command.Get(),nr_handle,parameters,nullptr),"NVIDIA NR evaluation");submit();++nr_frames;
}
void Bridge::run_phoenix(const Json& o,bool reset){
 const auto p=o.value("Phoenix",Json::object());
 std::array<int,8> repeats{1,1,1,1,1,1,1,1};
 if(p.contains("Repeats")){std::istringstream parts(p.at("Repeats").get<std::string>());std::string v;for(auto& n:repeats){require(bool(std::getline(parts,v,',')),"Eight block repeat counts required");n=std::stoi(v);}require(!std::getline(parts,v,','),"Eight block repeat counts required");}
 Json tuning={{"intensity",1.f},{"style",o.value("Style",0u)},{"local_tone",o.value("Tone",1.f)},{"local_structure",o.value("Structure",1.f)},
  {"skin_structure",p.value("Skin",-1.f)},{"auto_mask",p.value("AutoMask",true)},{"temporal",p.value("Temporal",true)},
  {"neural_scale",p.value("NeuralScale",100.f)},{"central_blocks",p.value("Blocks",8u)},{"repeats",repeats},
  {"network_passes",std::clamp(o.value("Passes",1),1,4)},{"color_strength",p.value("ColorStrength",1.f)},
  {"target_fps",p.value("TargetFps",0.f)},{"max_processing_ms",p.value("MaxMilliseconds",0.f)}};
 Json pipeline={{"PipelineMonitoring",p.value("Monitoring",0)},{"PipelinePriority",p.value("Priority",0)},
  {"AdjustCentralBlocks",true},{"AdjustNeuralResolution",true},{"AdjustNetworkPasses",true},
  {"SceneDetection",p.value("SceneDetection",false)},{"ProcessingChain",true},
  {"ChainBrightness",p.value("Brightness",0.f)},{"ChainContrast",p.value("Contrast",1.f)},
  {"ChainSaturation",p.value("Saturation",1.f)},{"ChainGamma",p.value("Gamma",1.f)}};
 if(!phoenix){Json c={{"width",nw},{"height",nh},{"bundle_path",config.at("native_bundle")},{"weight_source",config.at("weight_source")},{"tuning",tuning},{"pipeline",pipeline}};auto s=c.dump();phoenix=phoenix_native_create(s.c_str(),device.Get(),queue.Get());require(phoenix!=nullptr,phoenix_native_error());}
 auto t=tuning.dump(),pc=pipeline.dump();
 require(phoenix_native_set_tuning(phoenix,t.c_str())==0,phoenix_native_error());
 require(phoenix_native_set_pipeline(phoenix,pc.c_str())==0,phoenix_native_error());
 PhoenixNativeControls controls;phoenix_native_default_controls(&controls);controls.reset=reset;
 controls.local_tone=o.value("Tone",1.f);controls.local_structure=o.value("Structure",1.f);controls.skin_structure=p.value("Skin",-1.f);controls.auto_mask=p.value("AutoMask",true);
 require(phoenix_native_frame_d3d12(phoenix,(upscale_first?upscaled:color).image.Get(),D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,(upscale_first?neural_motion:motion).image.Get(),D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,neural.image.Get(),D3D12_RESOURCE_STATE_UNORDERED_ACCESS,&controls)==0,phoenix_native_error());
 ++phoenix_frames;
 auto size=phoenix_native_status(phoenix,nullptr,0);std::vector<char> result(size);phoenix_native_status(phoenix,result.data(),size);status["phoenix"]=Json::parse(result.data());
}
void Bridge::ensure_sr(){
 if(sr_handle)return;sr_params->Reset();sr_params->Set(NVSDK_NGX_Parameter_Width,iw);sr_params->Set(NVSDK_NGX_Parameter_Height,ih);sr_params->Set(NVSDK_NGX_Parameter_OutWidth,ow);sr_params->Set(NVSDK_NGX_Parameter_OutHeight,oh);sr_params->Set(NVSDK_NGX_Parameter_PerfQualityValue,0);sr_params->Set("DLSS.Feature.Create.Flags",2);sr_params->Set("CreationNodeMask",1u);sr_params->Set("VisibilityNodeMask",1u);begin();ngx(NVSDK_NGX_D3D12_CreateFeature(command.Get(),NVSDK_NGX_Feature_SuperSampling,sr_params,&sr_handle),"NVIDIA SR creation");submit();
}
void Bridge::run_sr(bool reset){
 ensure_sr();auto* input=upscale_first?color.image.Get():neural.image.Get();auto input_state=upscale_first?D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE:D3D12_RESOURCE_STATE_UNORDERED_ACCESS;sr_params->Reset();sr_params->Set(NVSDK_NGX_Parameter_Color,input);sr_params->Set(NVSDK_NGX_Parameter_Output,upscaled.image.Get());sr_params->Set(NVSDK_NGX_Parameter_Depth,depth.image.Get());sr_params->Set(NVSDK_NGX_Parameter_MotionVectors,motion.image.Get());sr_params->Set(NVSDK_NGX_Parameter_Reset,int(reset));sr_params->Set("MV.Scale.X",1.f);sr_params->Set("MV.Scale.Y",1.f);sr_params->Set("Jitter.Offset.X",0.f);sr_params->Set("Jitter.Offset.Y",0.f);sr_params->Set("DLSS.Pre.Exposure",1.f);sr_params->Set("DLSS.Exposure.Scale",1.f);sr_params->Set("DLSS.Render.Subrect.Dimensions.Width",iw);sr_params->Set("DLSS.Render.Subrect.Dimensions.Height",ih);
 begin();transition(command.Get(),input,input_state,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);ngx(NVSDK_NGX_D3D12_EvaluateFeature(command.Get(),sr_handle,sr_params,nullptr),"NVIDIA SR evaluation");transition(command.Get(),input,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,input_state);submit();++sr_frames;
}
void Bridge::create_swap(bool fg){
 if(!hwnd)return;wait();for(auto& b:backbuffers)b.Reset();swap.Reset();
 if(fg_options){sl::DLSSGOptions opts{};opts.mode=fg?sl::DLSSGMode::eOn:sl::DLSSGMode::eOff;opts.numFramesToGenerate=1;opts.numBackBuffers=3;opts.colorWidth=ow;opts.colorHeight=oh;opts.mvecDepthWidth=iw;opts.mvecDepthHeight=ih;opts.colorBufferFormat=DXGI_FORMAT_R8G8B8A8_UNORM;opts.mvecBufferFormat=DXGI_FORMAT_R16G16_FLOAT;opts.depthBufferFormat=DXGI_FORMAT_R32_FLOAT;opts.hudLessBufferFormat=DXGI_FORMAT_R8G8B8A8_UNORM;sl_check(fg_options(viewport,opts),"Frame generation options");}
 DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=ow;desc.Height=oh;desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;desc.SampleDesc.Count=1;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=3;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;desc.Scaling=DXGI_SCALING_STRETCH;desc.AlphaMode=DXGI_ALPHA_MODE_IGNORE;desc.Flags=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT|DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
 ComPtr<IDXGISwapChain1> base;hr(factory->CreateSwapChainForHwnd(proxy_queue.Get(),hwnd,&desc,nullptr,nullptr,&base));hr(base.As(&swap));factory->MakeWindowAssociation(hwnd,DXGI_MWA_NO_ALT_ENTER);for(UINT i=0;i<3;i++)hr(swap->GetBuffer(i,IID_PPV_ARGS(&backbuffers[i])));fg_active=fg;reset_needed=true;
}
void Bridge::prepare_fg(bool requested){
 if(requested && !fg_options){
  try {fg_options=feature<PFun_slDLSSGSetOptions>(sl::kFeatureDLSS_G,"slDLSSGSetOptions");fg_state=feature<PFun_slDLSSGGetState>(sl::kFeatureDLSS_G,"slDLSSGGetState");reflex_options=feature<PFun_slReflexSetOptions>(sl::kFeatureReflex,"slReflexSetOptions");reflex_sleep=feature<PFun_slReflexSleep>(sl::kFeatureReflex,"slReflexSleep");marker=feature<PFun_slPCLSetMarker>(sl::kFeaturePCL,"slPCLSetMarker");sl::ReflexOptions opts{};opts.mode=sl::ReflexMode::eLowLatency;sl_check(reflex_options(opts),"Reflex options");}
  catch(const std::exception& e){fg_error=e.what();requested=false;}
 }
 requested=requested&&hwnd&&fg_options&&fg_state&&marker;
 if(requested!=fg_active)create_swap(requested);
}
void Bridge::present(bool requested,bool reset){
 prepare_fg(requested);if(!swap)return;
 sl::FrameToken* token=nullptr;bool active=fg_active;
 if(active){sl_check(sl_token(token,&frame_index),"Frame token");if(reflex_sleep)sl_check(reflex_sleep(*token),"Reflex sleep");sl_check(marker(sl::PCLMarker::eSimulationStart,*token),"Simulation marker");sl_check(marker(sl::PCLMarker::eSimulationEnd,*token),"Simulation marker");sl::Constants c{};for(unsigned i=0;i<4;i++){sl::float4 row={0,0,0,0};(&row.x)[i]=1;c.cameraViewToClip[i]=c.clipToCameraView[i]=c.clipToPrevClip[i]=c.prevClipToClip[i]=row;}c.cameraPos={0,0,0};c.cameraUp={0,1,0};c.cameraRight={1,0,0};c.cameraFwd={0,0,1};c.cameraNear=.1f;c.cameraFar=1000;c.cameraFOV=1.0472f;c.cameraAspectRatio=float(ow)/oh;c.jitterOffset={0,0};c.mvecScale={1.f/iw,1.f/ih};c.cameraPinholeOffset={0,0};c.depthInverted=sl::Boolean::eFalse;c.cameraMotionIncluded=sl::Boolean::eTrue;c.motionVectors3D=sl::Boolean::eFalse;c.reset=(reset||reset_needed)?sl::Boolean::eTrue:sl::Boolean::eFalse;c.orthographicProjection=sl::Boolean::eFalse;float yscale=1.f/tanf(c.cameraFOV*.5f),xscale=yscale/c.cameraAspectRatio,a=c.cameraFar/(c.cameraFar-c.cameraNear),b=-c.cameraNear*a;c.cameraViewToClip[0]={xscale,0,0,0};c.cameraViewToClip[1]={0,yscale,0,0};c.cameraViewToClip[2]={0,0,a,1};c.cameraViewToClip[3]={0,0,b,0};c.clipToCameraView[0]={1/xscale,0,0,0};c.clipToCameraView[1]={0,1/yscale,0,0};c.clipToCameraView[2]={0,0,0,1/b};c.clipToCameraView[3]={0,0,1,-a/b};c.motionVectorsDilated=sl::Boolean::eFalse;c.motionVectorsJittered=sl::Boolean::eFalse;sl_check(sl_constants(c,*token,viewport),"Frame generation constants");}
 begin();auto index=swap->GetCurrentBackBufferIndex();auto* back=backbuffers[index].Get();transition(command.Get(),final.image.Get(),D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_COPY_SOURCE);transition(command.Get(),back,D3D12_RESOURCE_STATE_PRESENT,D3D12_RESOURCE_STATE_COPY_DEST);command->CopyResource(back,final.image.Get());transition(command.Get(),back,D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_STATE_PRESENT);transition(command.Get(),final.image.Get(),D3D12_RESOURCE_STATE_COPY_SOURCE,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);
 if(active){sl::Resource m(sl::ResourceType::eTex2d,motion.image.Get(),D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),d(sl::ResourceType::eTex2d,depth.image.Get(),D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE),c(sl::ResourceType::eTex2d,final.image.Get(),D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);sl::Resource bb(sl::ResourceType::eTex2d,back,D3D12_RESOURCE_STATE_PRESENT);sl::Extent lo{0,0,iw,ih},hi{0,0,ow,oh};sl::ResourceTag tags[]={{&m,sl::kBufferTypeMotionVectors,sl::ResourceLifecycle::eValidUntilPresent,&lo},{&d,sl::kBufferTypeDepth,sl::ResourceLifecycle::eValidUntilPresent,&lo},{&c,sl::kBufferTypeHUDLessColor,sl::ResourceLifecycle::eValidUntilPresent,&hi},{&bb,sl::kBufferTypeBackbuffer,sl::ResourceLifecycle::eValidUntilPresent,&hi}};sl_check(sl_tags(*token,viewport,tags,4,command.Get()),"Frame generation resource tags");sl_check(marker(sl::PCLMarker::eRenderSubmitStart,*token),"Render marker");}
 submit();if(active){sl_check(marker(sl::PCLMarker::eRenderSubmitEnd,*token),"Render marker");sl_check(marker(sl::PCLMarker::ePresentStart,*token),"Present marker");}
 auto presentResult=swap->Present(1,0);status["present_hresult"]=unsigned(presentResult);if(active){sl_check(marker(sl::PCLMarker::ePresentEnd,*token),"Present marker");sl::DLSSGState state{};sl_check(fg_state(viewport,state,nullptr),"Frame generation state");status["frame_generation_status_bits"]=unsigned(state.status);status["frame_generation_max_generated"]=state.numFramesToGenerateMax;auto actual=state.numFramesActuallyPresented;presented+=actual;if(actual>1)generated+=actual-1;if(state.inputsProcessingCompletionFence && state.lastPresentInputsProcessingCompletionFenceValue){auto* f=static_cast<ID3D12Fence*>(state.inputsProcessingCompletionFence);if(f->GetCompletedValue()<state.lastPresentInputsProcessingCompletionFenceValue){hr(f->SetEventOnCompletion(state.lastPresentInputsProcessingCompletionFenceValue,fence_event));require(WaitForSingleObject(fence_event,15000)==WAIT_OBJECT_0,"Frame generation input fence timeout");}}}else ++presented;hr(presentResult);
 begin();transition(command.Get(),final.image.Get(),D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);submit();++frame_index;reset_needed=false;
}
void Bridge::frame(const uint8_t* input,size_t capacity,unsigned w,unsigned h,const Json& o,uint8_t* output,size_t output_capacity){
 require(owner==std::this_thread::get_id(),"Graphics session thread mismatch");require(input&&w&&h&&w<=4096&&h<=4096&&capacity>=size_t(w)*h*4,"Invalid DOS frame capacity");require(w==sw&&h==sh,"Source resolution changed; reconfigure the graphics session");require(output&&output_capacity>=size_t(ow)*oh*4,"Invalid output capacity");
 auto started=std::chrono::steady_clock::now();bool reset=o.value("reset",false)||previous_luma.empty();std::vector<uint16_t> rgb(size_t(iw)*ih*4),mv(size_t(iw)*ih*2,0);std::vector<float> z(size_t(iw)*ih,.5f);std::vector<uint8_t> luma(size_t(iw)*ih);uint64_t diff=0;
 for(unsigned y=0;y<ih;y++)for(unsigned x=0;x<iw;x++){
  float bgr[3];
  if((iw!=sw||ih!=sh)&&input_linear){float fx=std::clamp((x+.5f)*sw/iw-.5f,0.f,float(sw-1)),fy=std::clamp((y+.5f)*sh/ih-.5f,0.f,float(sh-1));unsigned x0=unsigned(fx),y0=unsigned(fy),x1=std::min(x0+1,sw-1),y1=std::min(y0+1,sh-1);float ax=fx-x0,ay=fy-y0;
   for(unsigned c=0;c<3;c++)bgr[c]=(input[(size_t(y0)*sw+x0)*4+c]*(1-ax)+input[(size_t(y0)*sw+x1)*4+c]*ax)*(1-ay)+(input[(size_t(y1)*sw+x0)*4+c]*(1-ax)+input[(size_t(y1)*sw+x1)*4+c]*ax)*ay;
  }else{unsigned sx=unsigned((uint64_t(2*x+1)*sw)/(2*iw)),sy=unsigned((uint64_t(2*y+1)*sh)/(2*ih));auto* p=input+(size_t(sy)*sw+sx)*4;for(unsigned c=0;c<3;c++)bgr[c]=p[c];}
  auto i=size_t(y)*iw+x;rgb[i*4]=half_value(bgr[2]/255.f,0);rgb[i*4+1]=half_value(bgr[1]/255.f,0);rgb[i*4+2]=half_value(bgr[0]/255.f,0);rgb[i*4+3]=half_value(1.f,0);luma[i]=uint8_t((bgr[2]*77+bgr[1]*150+bgr[0]*29)/256);if(!previous_luma.empty())diff+=abs(int(luma[i])-int(previous_luma[i]));}
 if(!previous_luma.empty()&&diff/(iw*ih)>50)reset=true;
 if(!reset){for(int by=0;by<int(ih);by+=16)for(int bx=0;bx<int(iw);bx+=16){int best=INT_MAX,bestx=0,besty=0;
  for(int dy=-8;dy<=8;dy++)for(int dx=-8;dx<=8;dx++){int cost=abs(dx)+abs(dy);for(int sy=2;sy<16;sy+=4)for(int sx=2;sx<16;sx+=4){int x=std::min(int(iw)-1,bx+sx),y=std::min(int(ih)-1,by+sy),px=std::clamp(x+dx,0,int(iw)-1),py=std::clamp(y+dy,0,int(ih)-1);cost+=abs(int(luma[y*iw+x])-int(previous_luma[py*iw+px]))*4;}if(cost<best){best=cost;bestx=dx;besty=dy;}}
  for(int y=by;y<std::min(by+16,int(ih));y++)for(int x=bx;x<std::min(bx+16,int(iw));x++){auto i=(size_t(y)*iw+x)*2;mv[i]=half_value(float(bestx),0);mv[i+1]=half_value(float(besty),0);}
 }}
 previous_luma=std::move(luma);begin();upload(color,rgb.data(),ih,iw);upload(motion,mv.data(),ih,iw);upload(depth,z.data(),ih,iw);
 if(upscale_first){std::vector<uint16_t> high_motion(size_t(nw)*nh*2);float scale_x=float(nw)/iw,scale_y=float(nh)/ih;
  for(unsigned y=0;y<nh;y++)for(unsigned x=0;x<nw;x++){auto src=(size_t(uint64_t(y)*ih/nh)*iw+uint64_t(x)*iw/nw)*2;auto dst=(size_t(y)*nw+x)*2;
   high_motion[dst]=half_value(float_value(mv[src])*scale_x,0);high_motion[dst+1]=half_value(float_value(mv[src+1])*scale_y,0);}
  upload(neural_motion,high_motion.data(),nh,nw);
 }submit();
 sr_ms=0;if(upscale_first){auto sr_start=std::chrono::steady_clock::now();run_sr(reset);sr_ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-sr_start).count();begin();transition(command.Get(),upscaled.image.Get(),D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);submit();}
 auto neural_start=std::chrono::steady_clock::now();bool enabled=o.value("Enabled",true);std::string renderer=o.value("Renderer",std::string("phoenix"));
 if(enabled){if(renderer=="nvidia")run_nr(o,reset);else run_phoenix(o,reset);}else {auto* input_image=(upscale_first?upscaled:color).image.Get();begin();transition(command.Get(),input_image,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,D3D12_RESOURCE_STATE_COPY_SOURCE);transition(command.Get(),neural.image.Get(),D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_COPY_DEST);command->CopyResource(neural.image.Get(),input_image);transition(command.Get(),input_image,D3D12_RESOURCE_STATE_COPY_SOURCE,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);transition(command.Get(),neural.image.Get(),D3D12_RESOURCE_STATE_COPY_DEST,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);submit();}
 neural_ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-neural_start).count();
 if(upscale_first){begin();transition(command.Get(),upscaled.image.Get(),D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);submit();}
 if(upscale&&!upscale_first){auto sr_start=std::chrono::steady_clock::now();run_sr(reset);sr_ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-sr_start).count();}
 auto* processed=upscale&&!upscale_first?upscaled.image.Get():neural.image.Get();D3D12_SHADER_RESOURCE_VIEW_DESC view{};view.Format=DXGI_FORMAT_R16G16B16A16_FLOAT;view.ViewDimension=D3D12_SRV_DIMENSION_TEXTURE2D;view.Shader4ComponentMapping=D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;view.Texture2D.MipLevels=1;auto handle=descriptors->GetCPUDescriptorHandleForHeapStart();auto step=device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);device->CreateShaderResourceView(color.image.Get(),&view,handle);handle.ptr+=step;device->CreateShaderResourceView(processed,&view,handle);handle.ptr+=step;D3D12_UNORDERED_ACCESS_VIEW_DESC uav{};uav.Format=DXGI_FORMAT_R8G8B8A8_UNORM;uav.ViewDimension=D3D12_UAV_DIMENSION_TEXTURE2D;device->CreateUnorderedAccessView(final.image.Get(),nullptr,&uav,handle);
 begin();transition(command.Get(),processed,D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE);command->SetPipelineState(compose.Get());command->SetComputeRootSignature(root.Get());ID3D12DescriptorHeap* heaps[]={descriptors.Get()};command->SetDescriptorHeaps(1,heaps);command->SetComputeRootDescriptorTable(0,descriptors->GetGPUDescriptorHandleForHeapStart());struct {float strength;unsigned compare,width,height,look,sharp;} constants{enabled?o.value("Intensity",.65f):1.f,unsigned(o.value("Compare",false)),ow,oh,enabled&&renderer!="phoenix"?o.value("Style",0u):0u,unsigned(o.value("SharpPixels",false))};command->SetComputeRoot32BitConstants(1,6,&constants,0);command->Dispatch((ow+7)/8,(oh+7)/8,1);transition(command.Get(),processed,D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);transition(command.Get(),final.image.Get(),D3D12_RESOURCE_STATE_UNORDERED_ACCESS,D3D12_RESOURCE_STATE_COPY_SOURCE);
 D3D12_TEXTURE_COPY_LOCATION from{},to{};from.pResource=final.image.Get();from.Type=D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;to.pResource=final.transfer.Get();to.Type=D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;to.PlacedFootprint=final.footprint;command->CopyTextureRegion(&to,0,0,0,&from,nullptr);transition(command.Get(),final.image.Get(),D3D12_RESOURCE_STATE_COPY_SOURCE,D3D12_RESOURCE_STATE_UNORDERED_ACCESS);submit();void* mapped=nullptr;hr(final.transfer->Map(0,nullptr,&mapped));for(unsigned y=0;y<oh;y++){auto* row=static_cast<uint8_t*>(mapped)+final.footprint.Offset+y*final.footprint.Footprint.RowPitch;for(unsigned x=0;x<ow;x++){auto i=(size_t(y)*ow+x)*4;output[i]=row[x*4+2];output[i+1]=row[x*4+1];output[i+2]=row[x*4];output[i+3]=255;}}final.transfer->Unmap(0,nullptr);
 present(o.value("NvidiaFrameGeneration",true)&&!o.value("paused",false)&&!o.value("menu",false)&&!o.value("background",false),reset);++frames;total_ms=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-started).count();status["source_width"]=w;status["source_height"]=h;status["renderer"]=enabled?renderer:"off";status["nr_frames"]=nr_frames;status["phoenix_frames"]=phoenix_frames;status["sr_frames"]=sr_frames;status["frames"]=frames;status["presented"]=presented;status["generated"]=generated;status["fg_active"]=fg_active;status["fg_error"]=fg_error;status["neural_ms"]=neural_ms;status["sr_ms"]=sr_ms;status["total_ms"]=total_ms;status["reset"]=reset;status["upscaler"]=upscale?"nvidia_dlss_sr":"off_native";
}
extern "C" void* db_graphics_create(const char* json,void* hwnd){try{last_error.clear();require(json!=nullptr,"Configuration absent");return new Bridge(Json::parse(json),static_cast<HWND>(hwnd));}catch(const std::exception& e){last_error=e.what();return nullptr;}}
extern "C" int db_graphics_reconfigure(void* session,const char* json){try{last_error.clear();require(session&&json,"Graphics session/configuration absent");static_cast<Bridge*>(session)->reconfigure(Json::parse(json));return 0;}catch(const std::exception& e){last_error=e.what();return -1;}}
extern "C" int db_graphics_frame(void* session,const uint8_t* input,size_t capacity,uint32_t w,uint32_t h,const char* settings,uint8_t* output,size_t output_capacity){try{last_error.clear();require(session&&settings,"Graphics session/settings absent");static_cast<Bridge*>(session)->frame(input,capacity,w,h,Json::parse(settings),output,output_capacity);return 0;}catch(const std::exception& e){last_error=e.what();if(session)static_cast<Bridge*>(session)->record_error(e.what());return -1;}}
extern "C" size_t db_graphics_status(void* session,char* dst,size_t capacity){if(!session)return 0;auto text=static_cast<Bridge*>(session)->status.dump();size_t required=text.size()+1;if(dst&&capacity>=required)memcpy(dst,text.c_str(),required);return required;}
extern "C" const char* db_graphics_error(){return last_error.c_str();}
extern "C" int db_graphics_destroy(void* session){try{if(session){auto* bridge=static_cast<Bridge*>(session);require(bridge->owner==std::this_thread::get_id(),"Graphics destruction thread mismatch");bridge->wait();delete bridge;}return 0;}catch(const std::exception& e){last_error=e.what();return -1;}}














