# check_frames.py RAW OUT [scenes...]: draws each scene's window from its dump (render.js) and compares it with the game's own pixels.
# Prints the mean difference and the share of pixels off by more than 60 of 255; OUT/NAME.cmp.png is game | HTML | difference x3.
import json, os, subprocess, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dump2html import Window, px
from build_frames import FRAMES
from PIL import Image, ImageChops, ImageStat
raw, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
scenes = [(s, k) for _, _, sc in FRAMES for s, k, _ in sc] + [('four', 'Screen')]
only = sys.argv[3:]
for scene, key in scenes:
    if only and scene not in only: continue
    w = Window(json.load(open(f'{raw}/{scene}.json')), f'{raw}/{scene}.png', f'{out}/img', 'file://' + os.path.abspath(f'{out}/img'), scene,
               map_shot=f'{raw}/{scene}.map.png' if scene == 'four' else None)
    h, n = w.window_html(key, 0, 0)
    W, H = int(round(n['w'])), int(round(n['h']))
    open(f'{out}/{scene}.html', 'w').write(h)
    subprocess.run(['node', os.path.join(os.path.dirname(os.path.abspath(__file__)), 'render.js'), f'{out}/{scene}.html', f'{out}/{scene}.png', str(W), str(H)], check=True, capture_output=True)
    r = Image.open(f'{out}/{scene}.png').convert('RGB')
    g = Image.open(f'{raw}/{scene}.png').convert('RGB').crop((int(n['x'] * 2), int(n['y'] * 2), int(n['x'] * 2) + r.width, int(n['y'] * 2) + r.height))
    d = ImageChops.difference(r, g).convert('L')
    bad = sum(1 for v in d.getdata() if v > 60) / (r.width * r.height)
    print(f'{scene:20s} mean {ImageStat.Stat(d).mean[0]:5.2f}  >60: {bad*100:5.2f}%')
    side = Image.new('RGB', (r.width * 3 + 40, r.height), 'white')
    side.paste(g, (0, 0)); side.paste(r, (r.width + 20, 0)); side.paste(Image.eval(d, lambda v: min(255, v * 3)).convert('RGB'), (2 * r.width + 40, 0))
    side.save(f'{out}/{scene}.cmp.png')
