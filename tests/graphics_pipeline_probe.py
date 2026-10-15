"""Exercise the app's native GPU bridge in a separate, bounded test process."""
import argparse
import ctypes
import hashlib
import json
import time
from ctypes import wintypes
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("renderer", choices=["phoenix", "nvidia", "off"])
parser.add_argument("--frames", type=int, default=8)
parser.add_argument("--window", action="store_true")
parser.add_argument("--child", action="store_true")
parser.add_argument("--order", choices=["before_nr", "after_nr"], default="after_nr")
parser.add_argument("--factor", type=int, choices=[2, 3, 4], default=4)
parser.add_argument("--upscale", action=argparse.BooleanOptionalAction, default=True)
parser.add_argument("--input", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
parser.add_argument("--app-root", type=Path)
parser.add_argument("--input-factor", type=float, default=1)
parser.add_argument("--input-filter", choices=["nearest", "linear"], default="nearest")
parser.add_argument("--reconfigure", action="store_true", help="Stress live order/size and DOS resolution changes on one device")
parser.add_argument("--config", type=Path)
parser.add_argument("--interval", type=float, default=1/30)
parser.add_argument("--intensity", type=float, default=2)
parser.add_argument("--tone", type=float, default=2)
parser.add_argument("--structure", type=float, default=2)
args = parser.parse_args()
root = (args.app_root or Path(__file__).resolve().parents[1]).resolve()
dll = ctypes.CDLL(str(root / "runtime" / "dosbox_nvngx.dll"), winmode=0x1100)
dll.db_graphics_create.argtypes = [ctypes.c_char_p, ctypes.c_void_p]
dll.db_graphics_create.restype = ctypes.c_void_p
dll.db_graphics_error.restype = ctypes.c_char_p
dll.db_graphics_frame.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.c_uint32, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_size_t]
dll.db_graphics_status.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t]
dll.db_graphics_status.restype = ctypes.c_size_t
dll.db_graphics_destroy.argtypes = [ctypes.c_void_p]
dll.db_graphics_reconfigure.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
results = {"renderer": args.renderer, "passed": False, "frames": []}
session = None
hwnd = None
parent = None
user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.CreateWindowExW.restype = ctypes.c_void_p
user32.CreateWindowExW.argtypes = [wintypes.DWORD, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.DWORD,
                                 ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                 ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
user32.DestroyWindow.argtypes = [ctypes.c_void_p]
user32.SetForegroundWindow.argtypes = [ctypes.c_void_p]
user32.GetForegroundWindow.restype = ctypes.c_void_p
user32.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
user32.AttachThreadInput.argtypes = [wintypes.DWORD, wintypes.DWORD, wintypes.BOOL]
kernel32 = ctypes.WinDLL("kernel32")
window_proc_type = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, ctypes.c_void_p, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)
user32.DefWindowProcW.argtypes = [ctypes.c_void_p, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
user32.DefWindowProcW.restype = ctypes.c_ssize_t
@window_proc_type
def window_proc(handle, message, wparam, lparam):
    return user32.DefWindowProcW(handle, message, wparam, lparam)
class WindowClass(ctypes.Structure):
    _fields_ = [("size", wintypes.UINT), ("style", wintypes.UINT), ("proc", window_proc_type),
                ("class_extra", ctypes.c_int), ("window_extra", ctypes.c_int), ("instance", ctypes.c_void_p),
                ("icon", ctypes.c_void_p), ("cursor", ctypes.c_void_p), ("background", ctypes.c_void_p),
                ("menu", wintypes.LPCWSTR), ("name", wintypes.LPCWSTR), ("small_icon", ctypes.c_void_p)]
window_class = WindowClass(size=ctypes.sizeof(WindowClass), proc=window_proc, name="DosboxGpuProbe")
user32.RegisterClassExW(ctypes.byref(window_class))
user32.UpdateWindow.argtypes = [ctypes.c_void_p]
user32.SetWindowPos.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT]
def pump():
    message = wintypes.MSG()
    while user32.PeekMessageW(ctypes.byref(message), None, 0, 0, 1):
        user32.TranslateMessage(ctypes.byref(message))
        user32.DispatchMessageW(ctypes.byref(message))
try:
    if args.window:
        hwnd = user32.CreateWindowExW(0, "DosboxGpuProbe", "DOSBox GPU presentation validation", 0x90000000, 80, 80, 1280, 768, None, None, None, None)
        if not hwnd:
            raise ctypes.WinError(ctypes.get_last_error())
        if args.child:
            parent = hwnd
            hwnd = user32.CreateWindowExW(0, "STATIC", "DOS child presentation", 0x56000000, 0, 0, 1280, 768, parent, None, None, None)
        results["foreground_granted"] = bool(user32.SetForegroundWindow(parent or hwnd))
        if not results["foreground_granted"]:
            foreground_thread = user32.GetWindowThreadProcessId(user32.GetForegroundWindow(), None)
            current_thread = kernel32.GetCurrentThreadId()
            attached = bool(user32.AttachThreadInput(current_thread, foreground_thread, True))
            try:
                results["foreground_granted"] = bool(user32.SetForegroundWindow(parent or hwnd))
            finally:
                if attached:
                    user32.AttachThreadInput(current_thread, foreground_thread, False)
        user32.SetWindowPos(parent or hwnd, ctypes.c_void_p(-1), 0, 0, 0, 0, 0x13)
        user32.UpdateWindow(hwnd)
        pump()
    from PIL import Image
    import numpy as np
    image = Image.open(args.input).convert("RGBA")
    w, h = image.size
    factor = args.factor if args.upscale else 1
    assert .25 <= args.input_factor <= 4, "Input scale must be 0.25 to 4"
    iw, ih = max(1,int(w * args.input_factor+.5)), max(1,int(h * args.input_factor+.5))
    ow, oh = iw * factor, ih * factor
    cfg = json.loads((args.config or root / "graphics.json").read_text(encoding="utf-8-sig"))
    for key in ("nr_dll", "sr_dll", "streamline", "native_bundle", "cache"):
        if key in cfg and not Path(cfg[key]).is_absolute(): cfg[key] = str((root/cfg[key]).resolve())
    if not Path(cfg["weight_source"]["path"]).is_absolute(): cfg["weight_source"]["path"] = str((root/cfg["weight_source"]["path"]).resolve())
    cfg.update(source_width=w, source_height=h, upscaling=args.upscale, upscale_order=args.order, upscale_factor=factor,input_factor=args.input_factor,input_filter=args.input_filter)
    session = dll.db_graphics_create(json.dumps(cfg).encode(), hwnd)
    if not session: raise RuntimeError(dll.db_graphics_error().decode())
    source = image.tobytes("raw", "BGRA")
    pixels = (ctypes.c_ubyte * len(source)).from_buffer_copy(source)
    output = (ctypes.c_ubyte * (ow * oh * 4))()
    options = dict(Renderer=args.renderer, Enabled=args.renderer!="off", Intensity=args.intensity, Tone=args.tone, Structure=args.structure,
                   Style=0, Passes=1, Compare=False, NvidiaUpscaling=args.upscale, NvidiaFrameGeneration=args.window)
    results["controls"] = options
    for frame in range(args.frames):
        target=time.perf_counter()+args.interval; pump()
        if args.window and user32.GetForegroundWindow() != (parent or hwnd): user32.SetForegroundWindow(parent or hwnd)
        start=time.perf_counter()
        if dll.db_graphics_frame(session,pixels,len(source),w,h,json.dumps(dict(options,reset=frame==0)).encode(),output,len(output)):
            raise RuntimeError(dll.db_graphics_error().decode())
        results["frames"].append(dict(ms=(time.perf_counter()-start)*1000,sha256=hashlib.sha256(output).hexdigest()))
        results["foreground_matches"] = user32.GetForegroundWindow() == (parent or hwnd)
        while time.perf_counter()<target: pump(); time.sleep(.001)
    size=dll.db_graphics_status(session,None,0); status=ctypes.create_string_buffer(size); dll.db_graphics_status(session,status,size)
    runtime=results["runtime"]=json.loads(status.value)
    expected_nr=(ow,oh) if args.upscale and args.order=="before_nr" else (iw,ih)
    enhanced=np.frombuffer(output,dtype=np.uint8).reshape(oh,ow,4)
    original=np.asarray(image.resize((ow,oh),Image.Resampling.NEAREST))[:,:, [2,1,0,3]]
    checks={
      "native_source":(runtime["source_width"],runtime["source_height"])==(w,h),
      "input_dimensions":(runtime["input_width"],runtime["input_height"])==(iw,ih),
      "neural_dimensions":(runtime["neural_width"],runtime["neural_height"])==expected_nr,
      "output_dimensions":(runtime["output_width"],runtime["output_height"])==(ow,oh),
      "resize_enabled_state":runtime["pre_resize"] == ((iw,ih)!=(w,h)),
      "nonuniform_output":len(set(bytes(output)[::4]))>8,
      "opaque_output":bool(np.all(enhanced[:,:,3]==255)),
      "sr_counter":runtime["sr_frames"]==(args.frames if args.upscale else 0),
      "nr_counter":runtime["nr_frames" if args.renderer=="nvidia" else "phoenix_frames"]==(args.frames if args.renderer!="off" else 0),
    }
    if args.renderer=="off" and not args.upscale:
        if args.input_factor==1: checks["native_identity"]=bytes(output)==source
        elif args.input_filter=="nearest":
            # Pixel-center sampling, choosing the right/bottom pixel at an exact tie.
            # Pillow NEAREST uses floating-point tie behavior at fractional ratios.
            xs=((2*np.arange(ow,dtype=np.int64)+1)*w)//(2*ow)
            ys=((2*np.arange(oh,dtype=np.int64)+1)*h)//(2*oh)
            reference=np.asarray(image)[ys[:,None],xs[None,:]][:,:,[2,1,0,3]]
            checks["nearest_reference"]=bool(np.array_equal(enhanced,reference))
        else:
            # Separable interpolation without a downsampling antialias kernel.
            xs=(np.arange(ow)+.5)*w/ow-.5;ys=(np.arange(oh)+.5)*h/oh-.5
            rgba=np.asarray(image).astype(float)
            horizontal=np.stack([np.stack([np.interp(xs,np.arange(w),row[:,c]) for c in range(4)],axis=1) for row in rgba])
            reference=np.stack([np.stack([np.interp(ys,np.arange(h),horizontal[:,x,c]) for c in range(4)],axis=1) for x in range(ow)],axis=1)
            reference=np.floor(reference+.5).astype(np.uint8)[:,:,[2,1,0,3]]
            maximum=int(np.abs(reference.astype(int)-enhanced.astype(int)).max());results["bilinear_max_byte_error"]=maximum;checks["bilinear_reference"]=maximum<=1
    if args.window and not args.child: checks["frame_generation"]=runtime["generated"]>0 and runtime["frame_generation_status_bits"]==0
    results.update(checks=checks,passed=all(checks.values()),mean_abs_rgb_difference=float(np.abs(enhanced[:,:,:3].astype(float)-original[:,:,:3]).mean()))
    args.output.parent.mkdir(parents=True,exist_ok=True)
    Image.frombytes("RGBA",(ow,oh),bytes(output),"raw","BGRA").save(args.output.with_suffix(".png"))
    if args.reconfigure:
        transitions=[]
        cases=[(320,200,False,"after_nr",4,"off"),(640,480,False,"after_nr",4,"off"),
               (720,400,False,"after_nr",4,"off"),(321,201,False,"after_nr",4,"off"),
               (320,200,True,"before_nr",4,args.renderer),(320,200,True,"after_nr",4,args.renderer),
               (640,480,True,"after_nr",2,args.renderer),(320,200,False,"after_nr",4,args.renderer)]
        cases=[(*case,1,"nearest") for case in cases]+[(321,201,True,"after_nr",2,args.renderer,1.25,"nearest"),(321,201,True,"before_nr",2,args.renderer,1.5,"linear"),(320,200,False,"after_nr",4,args.renderer,.75,"nearest"),(320,200,False,"after_nr",4,"off",.25,"linear")]
        for width,height,sr,order,scale,renderer,entry,resize_filter in cases*2:
            cfg.update(source_width=width,source_height=height,upscaling=sr,upscale_order=order,upscale_factor=scale,input_factor=entry,input_filter=resize_filter)
            if dll.db_graphics_reconfigure(session,json.dumps(cfg).encode()): raise RuntimeError(dll.db_graphics_error().decode())
            yy,xx=np.mgrid[:height,:width]; fixture=np.stack([(xx*3)%256,(yy*5)%256,(xx+yy)%256,np.full_like(xx,255)],axis=2).astype(np.uint8).tobytes()
            source_pixels=(ctypes.c_ubyte*len(fixture)).from_buffer_copy(fixture)
            rw,rh=max(1,int(width*entry+.5)),max(1,int(height*entry+.5))
            fw,fh=rw*(scale if sr else 1),rh*(scale if sr else 1)
            target=(ctypes.c_ubyte*(fw*fh*4))()
            for frame in range(4):
                opts=dict(options,Renderer=renderer,Enabled=renderer!="off",NvidiaUpscaling=sr,NvidiaFrameGeneration=False,reset=frame==0)
                if dll.db_graphics_frame(session,source_pixels,len(fixture),width,height,json.dumps(opts).encode(),target,len(target)): raise RuntimeError(dll.db_graphics_error().decode())
            size=dll.db_graphics_status(session,None,0); report=ctypes.create_string_buffer(size);dll.db_graphics_status(session,report,size);current=json.loads(report.value)
            valid=(current["source_width"],current["source_height"],current["input_width"],current["input_height"],current["output_width"],current["output_height"])==(width,height,rw,rh,fw,fh)
            if renderer=="off" and not sr and entry==1: valid &= bytes(target)==fixture
            transitions.append(dict(source=[width,height],output=[fw,fh],renderer=renderer,upscaling=sr,order=order,input_factor=entry,input_filter=resize_filter,passed=bool(valid)))
        results["reconfigurations"]=transitions;results["passed"] &= all(t["passed"] for t in transitions)
except Exception as error:
    results["error"]=str(error)
finally:
    if session:
        results["destroy_result"]=dll.db_graphics_destroy(session)
        if results["destroy_result"]: results["passed"]=False
    if hwnd: user32.DestroyWindow(hwnd)
    if parent: user32.DestroyWindow(parent)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(results,indent=2),encoding="utf-8")
    print(json.dumps({k:v for k,v in results.items() if k!="frames"},indent=2))
raise SystemExit(0 if results["passed"] else 1)
