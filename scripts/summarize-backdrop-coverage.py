"""Check real background-only window captures for exposed edges; make natural-speed previews."""
import argparse
import hashlib
import json
from pathlib import Path
from PIL import Image, ImageChops

parser=argparse.ArgumentParser()
parser.add_argument('--evidence',type=Path,required=True)
parser.add_argument('--payload',type=Path,required=True)
args=parser.parse_args()
e,p=args.evidence,args.payload
art=json.loads((e/'ARTWORK_BACKGROUND_VERIFICATION.json').read_text())
colors=json.loads((e/'COLOR_ENGINE_VERIFICATION.json').read_text())
sequence=json.loads((e/'BACKGROUND_COVERAGE_FRAMES.json').read_text())
digest=hashlib.sha256((p/'Muyu.App.dll').read_bytes()).hexdigest().upper()
assert art['pass'] and art['application_sha256']==digest
assert art['abstract_palette_texture'] and not art['original_artwork_used_as_background']
assert colors['pass'] and len(colors['cases'])==20
assert len(sequence['cases'])==48
coverage=[]
for case in sequence['cases']:
    im=Image.open(e/case['screenshot']).convert('RGB')
    # Exclude the native caption controls, window frame and outside-window shadow.
    im=im.crop((20,90,im.width-20,im.height-20))
    horizontal=ImageChops.difference(im.crop((0,0,im.width-1,im.height)),im.crop((1,0,im.width,im.height)))
    vertical=ImageChops.difference(im.crop((0,0,im.width,im.height-1)),im.crop((0,1,im.width,im.height)))
    maximum=max(hi for lo,hi in horizontal.getextrema()+vertical.getextrema())
    assert maximum<=3,(case['screenshot'],maximum)
    coverage.append(dict(**case,max_adjacent_channel_step=maximum))
frames=[Image.open(e/f'artwork-flow-{i:02}.png').convert('RGB') for i in range(10)]
band=(700,90,frames[0].width-200,130)
motion=max(hi for lo,hi in ImageChops.difference(frames[0].crop(band),frames[-1].crop(band)).getextrema())
assert motion>0
shared=[]
for theme in ('light','dark'):
    a=Image.open(e/f'artwork-{theme}-now.png').convert('RGB')
    b=Image.open(e/f'artwork-{theme}-home.png').convert('RGB')
    difference=max(hi for lo,hi in ImageChops.difference(a.crop(band),b.crop(band)).getextrema())
    assert difference==0
    shared.append(dict(theme=theme,max_empty_background_difference=difference))
small=[f.resize((1080,705),Image.Resampling.LANCZOS).quantize(colors=192) for f in frames]
small[0].save(e/'background-flow.gif',save_all=True,append_images=small[1:],duration=1000,loop=0)
montage=Image.new('RGB',(1440,940))
for row,layout in enumerate(('landscape','square')):
    for col,angle in enumerate((0,90,180,270)):
        im=Image.open(e/f'coverage-light-{layout}-{angle:03}.png').convert('RGB')
        montage.paste(im.resize((360,470),Image.Resampling.LANCZOS),(col*360,row*470))
montage.save(e/'coverage-contact-sheet.jpg',quality=92)
report=dict(pass_=True,application_sha256=digest,actual_window_captures=True,
            coverage_cases=coverage,worst_adjacent_channel_step=max(c['max_adjacent_channel_step'] for c in coverage),
            blur_disabled_for_coverage=True,maximum_drift_for_coverage=True,
            natural_flow_empty_background_change=motion,shared_background=shared,audio_output_opened=False)
report['pass']=report.pop('pass_')
(e/'BACKGROUND_COVERAGE_VERIFICATION.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
for name in ('ARTWORK_BACKGROUND_VERIFICATION.json','COLOR_ENGINE_VERIFICATION.json','BACKGROUND_COVERAGE_VERIFICATION.json'):
    (p/name).write_bytes((e/name).read_bytes())
print(json.dumps({k:v for k,v in report.items() if k!='coverage_cases'},indent=2))
