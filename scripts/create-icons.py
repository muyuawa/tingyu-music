from PIL import Image, ImageDraw
from pathlib import Path
root=Path(r'D:\am\MuyuMusic\src\Muyu.App\Assets')
root.mkdir(exist_ok=True)
for name in ['app','previous','play','pause','next']:
    im=Image.new('RGBA',(256,256),(0,0,0,0)); d=ImageDraw.Draw(im)
    if name=='app':
        d.rounded_rectangle((8,8,248,248),radius=64,fill='#D94768')
        d.polygon([(113,60),(199,42),(199,170),(176,170),(176,83),(136,92),(136,187),(113,187)],fill='white')
        d.ellipse((70,164,136,215),fill='white');d.ellipse((133,147,199,198),fill='white')
    else:
        d.ellipse((8,8,248,248),fill='#D94768')
        if name=='play': d.polygon([(94,65),(94,191),(194,128)],fill='white')
        if name=='pause': d.rounded_rectangle((81,67,115,189),8,fill='white');d.rounded_rectangle((141,67,175,189),8,fill='white')
        if name=='previous': d.rectangle((66,67,86,189),fill='white');d.polygon([(187,67),(187,189),(87,128)],fill='white')
        if name=='next': d.rectangle((170,67,190,189),fill='white');d.polygon([(69,67),(69,189),(169,128)],fill='white')
    im.save(root/(name+'.ico'),sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])