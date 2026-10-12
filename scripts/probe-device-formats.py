"""Read-only WASAPI descriptor probe. Never calls Initialize, Start or GetService."""
import argparse, ctypes as C, json, struct, uuid
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument("device");p.add_argument("output",type=Path);args=p.parse_args()
class GUID(C.Structure):
    _fields_=[("bytes",C.c_ubyte*16)]
    def __init__(self,value):super().__init__();self.bytes[:]=uuid.UUID(value).bytes_le
ole=C.OleDLL("ole32");ole.CoInitializeEx(None,2)
def call(obj,index,restype,argtypes,*values):
    table=C.cast(obj,C.POINTER(C.POINTER(C.c_void_p))).contents
    return C.WINFUNCTYPE(restype,C.c_void_p,*argtypes)(table[index])(obj,*values)
enum=C.c_void_p();device=C.c_void_p();client=C.c_void_p()
try:
    ole.CoCreateInstance(C.byref(GUID("BCDE0395-E52F-467C-8E3D-C4579291692E")),None,23,C.byref(GUID("A95664D2-9614-4F35-A746-DE8DB63617E6")),C.byref(enum))
    hr=call(enum,5,C.c_long,[C.c_wchar_p,C.POINTER(C.c_void_p)],args.device,C.byref(device));assert hr>=0,hex(hr&0xffffffff)
    hr=call(device,3,C.c_long,[C.POINTER(GUID),C.c_ulong,C.c_void_p,C.POINTER(C.c_void_p)],C.byref(GUID("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")),23,None,C.byref(client));assert hr>=0,hex(hr&0xffffffff)
    results=[]
    for rate in (44100,48000,96000,192000):
        for valid in (16,24,32):
            for bits in (16,24,32):
                if bits<valid:continue
                for kind,precision in (("extensible",valid),("extensible",bits),("pcm",bits)):
                    fmt=struct.pack("<HHIIHHH",1 if kind=="pcm" else 65534,2,rate,rate*2*bits//8,2*bits//8,bits,0 if kind=="pcm" else 22)
                    if kind!="pcm":fmt+=struct.pack("<HI",precision,3)+uuid.UUID("00000001-0000-0010-8000-00aa00389b71").bytes_le
                    data=C.create_string_buffer(fmt)
                    hr=call(client,7,C.c_long,[C.c_int,C.c_void_p,C.c_void_p],1,data,None)
                    row=dict(rate=rate,source_bits=valid,container=bits,valid_bits=precision,descriptor=kind,hresult=f"0x{hr&0xffffffff:08X}",supported=hr==0)
                    if row not in results:results.append(row)
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(dict(audio_output_opened=False,initialize_called=False,formats=results),indent=2),encoding="utf-8")
    for rate in (44100,48000,96000,192000):
        supported=[f"{r['container']}/{r['valid_bits']} {r['descriptor']}" for r in results if r["rate"]==rate and r["source_bits"]==16 and r["supported"]]
        print(rate,"Hz accepts:",", ".join(supported) or "none")
finally:
    for obj in (client,device,enum):
        if obj.value:call(obj,2,C.c_ulong,[])
    ole.CoUninitialize()
