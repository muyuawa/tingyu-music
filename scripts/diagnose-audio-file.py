"""Decode a supplied file without playing it; optional explicit silent output open.

The output-open option calls muyu_open then destroys the engine. It never calls
muyu_play / IAudioClient.Start and writes no audio file or library data.
"""
import argparse, ctypes as C, hashlib, json, os, subprocess
from pathlib import Path

p=argparse.ArgumentParser();p.add_argument("file",type=Path);p.add_argument("runtime",type=Path);p.add_argument("report",type=Path);p.add_argument("--device");p.add_argument("--gain",type=float,default=0);p.add_argument("--ffmpeg",type=Path);a=p.parse_args()
class Info(C.Structure):
    _fields_=[("rate",C.c_int),("bits",C.c_int),("channels",C.c_int),("container_bits",C.c_int),("frames",C.c_int64),("encoding",C.c_int)]
class Options(C.Structure):
    _fields_=[("rate",C.c_int),("bits",C.c_int),("channels",C.c_int),("buffer_ms",C.c_int),("gain",C.c_double),("eq",C.c_double*10),("dop",C.c_int)]
source=a.file.resolve();before=hashlib.sha256(source.read_bytes()).hexdigest()
with os.add_dll_directory(str(a.runtime.resolve())):
    dll=C.CDLL(str(a.runtime.resolve()/"MuyuAudio.dll"))
    dll.muyu_probe.argtypes=[C.c_wchar_p,C.POINTER(Info)];dll.muyu_probe.restype=C.c_int
    info=Info();hr=dll.muyu_probe(str(source),C.byref(info));assert hr>=0,f"probe 0x{hr&0xffffffff:08X}"
    buffer=C.create_string_buffer(info.frames*info.channels*info.container_bits//8)
    dll.muyu_read_pcm.argtypes=[C.c_wchar_p,C.c_int64,C.c_int,C.c_void_p,C.c_int];dll.muyu_read_pcm.restype=C.c_int
    count=dll.muyu_read_pcm(str(source),0,info.frames,buffer,len(buffer));assert count==info.frames,(count,info.frames)
    pcm_hash=hashlib.sha256(buffer.raw).hexdigest()
    result=dict(file=source.name,runtime=str(a.runtime.resolve()),file_sha256=before,source={k:getattr(info,k) for k,_ in Info._fields_},native_full_decode=True,decoded_frames=count,pcm_sha256=pcm_hash,play_called=False,output_opened=False)
    if a.ffmpeg:
        codec=f"pcm_s{info.container_bits}le"
        external=subprocess.run([str(a.ffmpeg),"-nostdin","-hide_banner","-loglevel","error","-i",str(source),"-map","0:a:0","-c:a",codec,"-f","hash","-hash","sha256","-"],capture_output=True,text=True,check=True)
        external_hash=external.stdout.strip().split("=")[-1].lower()
        result.update(ffmpeg_pcm_sha256=external_hash,independent_pcm_matches=external_hash==pcm_hash,ffmpeg_errors=external.stderr)
    if a.device:
        dll.muyu_create.restype=C.c_void_p;dll.muyu_destroy.argtypes=[C.c_void_p]
        dll.muyu_configure.argtypes=[C.c_void_p,Options];dll.muyu_configure.restype=C.c_int
        dll.muyu_open.argtypes=[C.c_void_p,C.c_wchar_p,C.c_wchar_p,C.c_int,C.c_int64,C.c_int64];dll.muyu_open.restype=C.c_int
        dll.muyu_error.argtypes=[C.c_void_p,C.c_wchar_p,C.c_int]
        engine=dll.muyu_create();assert engine
        try:
            options=Options();options.buffer_ms=100;options.gain=a.gain;assert dll.muyu_configure(engine,options)>=0
            opened=dll.muyu_open(engine,str(source),a.device,0,0,-1);error=C.create_unicode_buffer(2048);dll.muyu_error(engine,error,len(error))
            result.update(initialize_attempted=True,output_opened=opened>=0,open_hresult=f"0x{opened&0xffffffff:08X}",open_error=error.value,gain=a.gain)
        finally:dll.muyu_destroy(engine)
result["original_file_unchanged"]=before==hashlib.sha256(source.read_bytes()).hexdigest()
a.report.parent.mkdir(parents=True,exist_ok=True);a.report.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8");print(json.dumps(result,ensure_ascii=False,indent=2))
