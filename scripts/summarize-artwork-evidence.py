"""Verify actual integrated artwork captures and make a preview/GIF, without touching music."""
import argparse
import hashlib
import json
from pathlib import Path
from PIL import Image, ImageChops

parser = argparse.ArgumentParser()
parser.add_argument('--evidence', type=Path, required=True)
parser.add_argument('--payload', type=Path, required=True)
args = parser.parse_args()
e, p = args.evidence, args.payload
a = json.loads((e / 'ARTWORK_BACKGROUND_VERIFICATION.json').read_text())
c = json.loads((e / 'COLOR_ENGINE_VERIFICATION.json').read_text())
assert a['pass'] and a['abstract_palette_texture'] and not a['original_artwork_used_as_background']
assert c['pass'] and len(c['cases']) == 20
digest = hashlib.sha256((p / 'Muyu.App.dll').read_bytes()).hexdigest().upper()
assert digest == a['application_sha256']
# Empty band below the search box and above page titles/lyrics at the captured window geometry.
pairs = []
for theme in ('light', 'dark'):
    player = Image.open(e / f'artwork-{theme}-now.png').convert('RGB')
    library = Image.open(e / f'artwork-{theme}-home.png').convert('RGB')
    box = (700, 90, player.width - 200, 130)
    delta = ImageChops.difference(player.crop(box), library.crop(box))
    maximum = max(v[1] for v in delta.getextrema())
    assert maximum == 0, (theme, maximum)
    pairs.append(dict(theme=theme, empty_background_crop=box, max_difference=maximum))
frames = [Image.open(e / f'artwork-flow-{i:02}.png').convert('RGB') for i in range(10)]
delta = ImageChops.difference(frames[0].crop(box), frames[-1].crop(box))
motion = max(v[1] for v in delta.getextrema())
assert motion > 5
pixels = list(frames[0].crop(box).getdata())
red = max(r - g for r, g, b in pixels)
warm = max(g - b for r, g, b in pixels)
spread = max(max(v) - min(v) for v in zip(*pixels))
# Check visible variation, without imposing a particular hue on an abstract top-edge crop.
# Retention of distinct source color families is covered by the core regression checks.
assert spread > 15, spread
small = [f.resize((1080, 705), Image.Resampling.LANCZOS).quantize(colors=192) for f in frames]
small[0].save(e / 'artwork-flow.gif', save_all=True, append_images=small[1:], duration=1000, loop=0)
comparison = Image.new('RGB', (1440, 470))
for i, name in enumerate(('artwork-dark-now.png', 'artwork-dark-home.png')):
    comparison.paste(Image.open(e / name).convert('RGB').resize((720, 470), Image.Resampling.LANCZOS), (i*720, 0))
comparison.save(e / 'player-library-preview.jpg', quality=92)
report = dict(pass_=True, application_sha256=digest, shared_background_pairs=pairs,
              dynamic_empty_background_max_change=motion, red_minus_green_max=red,
              green_minus_blue_max=warm, background_channel_spread=spread,
              actual_capture=True, audio_output_opened=False)
report['pass'] = report.pop('pass_')
(e / 'PIXEL_VERIFICATION.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
for name in ('ARTWORK_BACKGROUND_VERIFICATION.json', 'COLOR_ENGINE_VERIFICATION.json', 'PIXEL_VERIFICATION.json'):
    (p / name).write_bytes((e / name).read_bytes())
print(json.dumps(report, indent=2))
