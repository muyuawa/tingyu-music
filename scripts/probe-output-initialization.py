"""Opt-in silent WASAPI initialization diagnostics; never calls Start or writes PCM."""
import argparse, ctypes as C, json, struct, uuid
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument("device");p.add_argument("output",type=Path);a=p.parse_args()
class GUID(C.Structure):
    _fields_=[("bytes",C.c_ubyte*16)]
    def __init__(self,value):super().__init__();self.bytes[:]=uuid.UUID(value).bytes_le
def call(obj,index,restype,argtypes,*values):
    table=C.cast(obj,C.POINTER(C.POINTER(C.c_void_p))).contents
    return C.WINFUNCTYPE(restype,C.c_void_p,*argtypes)(table[index])(obj,*values)
ole=C.OleDLL("ole32");ole.CoInitializeEx(None,2);enum=C.c_void_p();device=C.c_void_p();client=C.c_void_p()
def activate():
    global client
    if client.value:call(client,2,C.c_ulong,[])
    client=C.c_void_p();hr=call(device,3,C.c_long,[C.POINTER(GUID),C.c_ulong,C.c_void_p,C.POINTER(C.c_void_p)],C.byref(GUID("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")),23,None,C.byref(client));assert hr>=0,hex(hr&0xffffffff)
try:
    ole.CoCreateInstance(C.byref(GUID("BCDE0395-E52F-467C-8E3D-C4579291692E")),None,23,C.byref(GUID("A95664D2-9614-4F35-A746-DE8DB63617E6")),C.byref(enum))
    hr=call(enum,5,C.c_long,[C.c_wchar_p,C.POINTER(C.c_void_p)],a.device,C.byref(device));assert hr>=0,hex(hr&0xffffffff)
    activate();period=C.c_int64();minimum=C.c_int64();assert call(client,9,C.c_long,[C.POINTER(C.c_int64),C.POINTER(C.c_int64)],C.byref(period),C.byref(minimum))>=0
    rows=[]
    for bits,valid,kind in [(32,16,"extensible"),(16,16,"extensible"),(16,16,"pcm"),(24,16,"extensible"),(24,24,"extensible"),(24,24,"pcm"),(32,32,"extensible"),(32,32,"pcm")]:
        activate();rate=44100;fmt=struct.pack("<HHIIHHH",1 if kind=="pcm" else 65534,2,rate,rate*2*bits//8,2*bits//8,bits,0 if kind=="pcm" else 22)
        if kind!="pcm":fmt+=struct.pack("<HI",valid,3)+uuid.UUID("00000001-0000-0010-8000-00aa00389b71").bytes_le
        data=C.create_string_buffer(fmt);supported=call(client,7,C.c_long,[C.c_int,C.c_void_p,C.c_void_p],1,data,None)
        initialized=call(client,3,C.c_long,[C.c_int,C.c_ulong,C.c_int64,C.c_int64,C.c_void_p,C.c_void_p],1,0,1000000,period.value,data,None) if supported==0 else None
        rows.append(dict(container=bits,valid=valid,kind=kind,support=f"0x{supported&0xffffffff:08X}",initialize=None if initialized is None else f"0x{initialized&0xffffffff:08X}"))
    result=dict(rate=44100,channels=2,buffer_ms=100,period_hns=period.value,minimum_period_hns=minimum.value,start_called=False,formats=rows)
    a.output.write_text(json.dumps(result,indent=2),encoding="utf-8");print(json.dumps(result,indent=2))
finally:
    for obj in (client,device,enum):
        if obj.value:call(obj,2,C.c_ulong,[])
    ole.CoUninitialize()
