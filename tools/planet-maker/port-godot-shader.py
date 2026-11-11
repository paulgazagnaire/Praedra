"""Writes the game's live planet shader (godot/Scripts/Ui/PlanetLive.gdshader) from the Planet Maker's own fragment shader.

The game's sector window draws every planet live with the Planet Maker's shader (Paul 2026-11-11: "the planets need to be animated,
like in the planet maker tool, and following the parameters of each planets"). The maths is not copied by hand: this script takes the
shader out of planet-maker.src.html and changes only what Godot's shading language needs, so a change in the tool reaches the game by
running it again:

    python3 tools/planet-maker/port-godot-shader.py

What it changes (each change is checked: if the tool's shader no longer has the line it expects, the script stops):
- GLSL ES 3 header lines out, `shader_type canvas_item` in; `main` becomes `fragment`.
- uniforms declared several to a line are split one to a line (Godot takes one name per uniform line); the blue noise is nearest-filtered.
- Godot has no global variables that change. gPx and gPxC (planet radii per pixel) only ever hold the pixel size main() works out
  from two uniforms, so they become that expression. gL and gFlat (the light shadePlanet uses) become two arguments of shadePlanet; the
  other functions that read gL always see the sun (megaFloor puts it back after its call), so they read uL.
- gl_FragCoord exists only in fragment(): main's fc is made from UV (bottom-left origin, as gl_FragCoord), and the dither functions
  get it as an argument.
- The see-through background. The tool's thumbnails (bake-thumbs.js, clearCanvas) draw the picture twice, on a black sky and on a white
  one, and keep a dot where both agree or where it is not sky. Everything main() does to the sky colour is either add to it or mix it
  with something by a single number, so the white picture is the black one plus how much sky still shows through (tr). main() keeps tr
  as it goes and seeThrough() makes the same decision in one pass.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, 'planet-maker.src.html')
OUT = os.path.join(HERE, '..', '..', 'godot', 'Scripts', 'Ui', 'PlanetLive.gdshader')


def fail(msg):
    sys.exit('port-godot-shader: ' + msg + ' (the Planet Maker shader changed: update this script)')


def once(s, old, new):
    if s.count(old) != 1:
        fail(f'expected exactly one {old!r}, found {s.count(old)}')
    return s.replace(old, new)


def split_args(s):
    """The top-level arguments of a call's argument text."""
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch in '([':
            depth += 1
        elif ch in ')]':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur.strip()); cur = ''
        else:
            cur += ch
    out.append(cur.strip())
    return out


def main():
    html = open(SRC, encoding='utf8').read()
    m = re.search(r'<script id="fs" type="x-shader/x-fragment">(.*?)</script>', html, re.S)
    if not m:
        fail('no <script id="fs">')
    s = m.group(1)

    # ---- header
    s = once(s, '#version 300 es\n', '')
    s = once(s, 'precision highp float;\n', '')
    s = once(s, 'out vec4 fragColor;\n', '')

    # ---- Godot has PI built in (the same float: 3.14159265 and Godot's PI round to the same 32-bit number)
    s = once(s, 'const float PI = 3.14159265;\n', '')

    # ---- Godot builds a matrix from columns, not from 4 or 9 numbers: the same numbers, column by column
    def mat_cols(mm):
        n = int(mm.group(1)); args = split_args(mm.group(2))
        if len(args) != n * n:
            return mm.group(0)
        return f'mat{n}(' + ', '.join(f'vec{n}(' + ', '.join(args[i * n:(i + 1) * n]) + ')' for i in range(n)) + ')'
    s = re.sub(r'\bmat([23])\(((?:\s*-?[\d.]+\s*,?)+)\)', mat_cols, s)

    # ---- uniforms: one name per line
    def split_uniform(mm):
        indent, typ, names, rest = mm.group(1), mm.group(2), mm.group(3), mm.group(4)
        parts = [n.strip() for n in names.split(',')]
        if len(parts) == 1:
            return mm.group(0)
        return '\n'.join(f'{indent}uniform {typ} {n};' for n in parts) + (rest or '')
    s = re.sub(r'^([ \t]*)uniform\s+(\w+)\s+([\w\s,\[\]]+);([^\n]*)$', split_uniform, s, flags=re.M)
    s = once(s, 'uniform sampler2D uBlue;', 'uniform sampler2D uBlue : filter_nearest, repeat_enable;')

    # ---- globals that change: gPx, gPxC
    px = '(2.0 / (uRes.y * uZoom))'
    s = once(s, 'float gPx = 0.0;', f'#define gPx {px}')
    s = once(s, 'float gPxC = 0.0;', f'#define gPxC {px}')
    s = once(s, '  float px = 2.0 / (uRes.y * uZoom);\n  gPxC = px;\n', '  float px = 2.0 / (uRes.y * uZoom);\n')
    s = once(s, '  gPx = px;\n', '')

    # ---- gL and gFlat: arguments of shadePlanet
    s = once(s, 'vec3 gL = vec3(0.0, 0.0, 1.0);\n', '')
    s = once(s, 'float gFlat = 0.0;', '')
    s = once(s, 'vec3 shadePlanet(vec3 p, out float shine) {', 'vec3 shadePlanet(vec3 p, out float shine, vec3 gL, float gFlat) {')
    body_start = s.index('vec3 shadePlanet(')
    body_end = s.index('\n}\n', body_start) + 3
    head, body, tail = s[:body_start], s[body_start:body_end], s[body_end:]
    s = once(tail, '  gL = normalize(pv * (day * 1.45 - 0.55) + tg * (0.20 + 0.55 * day));\n  gFlat = 1.0;\n  float shine;\n  vec3 c = shadePlanet(pv, shine);\n  gL = uL; gFlat = 0.0;\n',
             '  vec3 floorL = normalize(pv * (day * 1.45 - 0.55) + tg * (0.20 + 0.55 * day));\n  float shine;\n  vec3 c = shadePlanet(pv, shine, floorL, 1.0);\n')
    s = once(s, '  gL = uL; gFlat = 0.0;\n', '')
    s = once(s, '    vec3 pc = shadePlanet(p, shine);', '    vec3 pc = shadePlanet(p, shine, uL, 0.0);')
    s = re.sub(r'\bgL\b', 'uL', s)
    if re.search(r'\bgFlat\b', s):
        fail('gFlat is still read outside shadePlanet')
    s = head + body + s

    # ---- gl_FragCoord
    s = once(s, 'float ditherThr() {\n  ivec2 c = ivec2(gl_FragCoord.xy);', 'float ditherThr(vec2 fragc) {\n  ivec2 c = ivec2(fragc);')
    s = once(s, 'vec3 ditherCol(vec3 col) {\n  float t = ditherThr();', 'vec3 ditherCol(vec3 col, vec2 fragc) {\n  float t = ditherThr(fragc);')
    s = once(s, 'vec3 finish(vec3 col) {', 'vec3 finish(vec3 col, vec2 fragc) {')
    s = once(s, '  col = ditherCol(col);', '  col = ditherCol(col, fragc);')
    if 'gl_FragCoord' in s.replace('void main() {\n  vec2 fc = gl_FragCoord.xy;', ''):
        fail('gl_FragCoord is used outside main')

    # Godot has no ivec2 & int: the mask as an ivec2 (the dither's pattern cell)
    di = s.index('float ditherThr(')
    de = s.index('\n}\n', di)
    s = s[:di] + re.sub(r'\bc & (\d+)', r'c & ivec2(\1)', s[di:de]) + s[de:]

    # ---- main
    # main() becomes pmMain(fc), returning the colour (Godot's fragment() may not return early, as main() does for a ring world)
    s = once(s, 'void main() {\n  vec2 fc = gl_FragCoord.xy;', 'vec4 pmMain(vec2 fc) {')
    mi = s.index('vec4 pmMain(vec2 fc) {')
    mbody = s[mi:]
    mbody = once(mbody, '  vec3 col = uBg + stars(fc);\n', '  vec3 col = uBg + stars(fc);\n  float tr = 1.0;   // how much of the sky still shows through (see seeThrough)\n')

    # every mix of the colour with something by one number lets that much less sky through
    def track(mm):
        args = split_args(mm.group(2))
        if len(args) != 3:
            fail('a mix in main() without three arguments: ' + mm.group(0))
        return f'{mm.group(1)}{{ col = mix(col, {args[1]}, {args[2]}); tr *= 1.0 - ({args[2]}); }}'
    mixes = len(re.findall(r'col = mix\(col,', mbody))
    mbody = re.sub(r'(\s)col = mix\((col,.*?)\);(?=\s*(?:\}|\n|if|for))', track, mbody)
    if mbody.count('tr *= 1.0 -') != mixes:
        fail('could not follow every mix in main()')
    if re.search(r"(?<!vec3 )\bcol\s*(\*=|/=|=(?!=)(?!\s*mix\(col))", mbody):
        fail('main() changes the colour in a way seeThrough does not follow')
    n_out = mbody.count('fragColor = vec4(finish(col), 1.0);')
    if n_out != 2:
        fail('expected two writes of fragColor in main()')
    mbody = mbody.replace('fragColor = vec4(finish(col), 1.0);\n    return;', 'return seeThrough(col, tr, fc);')
    mbody = once(mbody, 'fragColor = vec4(finish(col), 1.0);', 'return seeThrough(col, tr, fc);')
    s = s[:mi] + mbody.rstrip() + '''

void fragment() {
  // gl_FragCoord: pixel centres, origin at the bottom left (the buffer is uRes pixels, one per dot)
  COLOR = pmMain(floor(vec2(UV.x, 1.0 - UV.y) * uRes) + 0.5);
}
'''

    see = '''
// The see-through background (the tool's clearCanvas, done in one pass): fa is the picture on a black sky, fb on a white one. With
// the dither on, a dot is kept where both agree or where it is not the black sky, and dropped otherwise, so the dots stay crisp.
// Without it, the alpha is how much the two differ (the tool's own formula, with its finish() of a white sky).
vec4 seeThrough(vec3 col, float tr, vec2 fragc) {
  vec3 fa = finish(col, fragc), fb = finish(col + tr, fragc);
#ifdef PM_DITHER
  vec3 k0 = finish(uBg, fragc);
  bool same = all(equal(fa, fb)), sky = all(equal(fa, k0));
  return vec4(fa, (same || !sky) ? 1.0 : 0.0);
#else
  float white = pow(1.0 / 1.08, 0.96);
  float al = clamp(1.0 - dot(fb - fa, vec3(1.0 / 3.0)) / white, 0.0, 1.0);
  return vec4(al > 0.004 ? min(fa / al, vec3(1.0)) : vec3(0.0), al);
#endif
}
'''
    s = once(s, '\n#ifdef PM_HARV\n// A harvested world:', see + '\n#ifdef PM_HARV\n// A harvested world:')

    header = '''// GENERATED by tools/planet-maker/port-godot-shader.py from the Planet Maker's fragment shader (tools/planet-maker/planet-maker.src.html).
// Do not edit: change the tool's shader and run the script again.
// The planets of the sector window, drawn live (Paul 2026-11-11: "in the sector window. the planets need to be animated, like in the
// planet maker tool, and following the parameters of each planets"). PlanetLive.cs feeds it each system's settings exactly as the
// tool's drawP() does, and compiles it in the tool's variants by putting #define lines (PM_CLOUDS, PM_HIQ, ...) after render_mode.
shader_type canvas_item;
render_mode unshaded, blend_disabled;
'''
    s = header + s.lstrip('\n')
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    open(OUT, 'w', encoding='utf8').write(s.rstrip() + '\n')
    print(f'wrote {os.path.relpath(OUT)} ({s.count(chr(10))} lines)')


if __name__ == '__main__':
    main()
