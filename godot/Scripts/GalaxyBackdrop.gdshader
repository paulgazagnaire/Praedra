// The galaxy drawn behind the map (Paul 2026-10-28: "a galaxy illustration in the back of the sectors... very detailed, just in code").
// Drawing only: nothing here touches the game. Coordinates are galaxy units (the disc's radius is 1, the centre is Charter).
// This is the Galaxy Maker's shader (tools/galaxy-maker/galaxy-maker.src.html) line for line, so a look tuned there is the same here
// (Paul 2026-10-29: "I want the exact same thing I designed in the galaxy maker"). GalaxyBackdrop.cs feeds it the settings from
// godot/ui/galaxy-backdrop.json; the defaults below are the Galaxy Maker's "Game today" look, without movement.
// The arms follow the two-arm spiral the systems were laid out on (specs/creative/galaxy-shape/README.md and build_gal.py), and
// their light is weighted by where the systems really are (the density map).
shader_type canvas_item;

uniform vec2 view_size;             // the map control's size in points
uniform vec2 view_center;           // world point at the middle of the control
uniform float px_per_unit = 1.0;    // points per world unit at the current zoom
uniform float pixel_scale = 1.0;    // buffer pixels per point (2 on a Retina screen; fewer with the dither's dots)
uniform vec2 galaxy_center;         // world position of the centre (Charter)
uniform float galaxy_radius = 480.0;
uniform float time = 0.0;           // seconds, for the movement
uniform sampler2D density : filter_linear, repeat_disable;     // where the systems are, 0..1, over centre +- 1.15 radius
uniform sampler2D blue_noise : filter_nearest, repeat_enable;  // 64 x 64 blue noise ranks, for the Grain dither

uniform vec4 arm = vec4(1.1694, 1.1448, 3.0, 0.35);   // x phase (radians), y winding per e-fold of radius, z arm sharpness, w wobble
uniform vec4 light_mix = vec4(1.0, 1.0, 1.0, 1.0);   // x bulge, y bulge size, z disc, w arms
uniform vec4 shape_mix = vec4(1.0, 0.3, 1.0, 1.0);   // x rim radius, y how much the systems shape the light, z clouds, w star-forming knots
uniform vec4 dust_mix = vec4(0.85, 0.55, 0.42, 1.0); // x lanes, y filaments, z lane offset, w overall brightness
uniform vec4 star_mix = vec4(1.0, 0.45, 1.0, 0.32);  // x stars, y foreground stars, z star density, w dimming for the map
uniform vec4 move = vec4(0.0);                        // x gas drift, y knot breathing, z twinkle strength, w share of stars that twinkle
uniform float twinkle_speed = 0.6;
uniform vec3 c_bulge = vec3(1.0, 0.839, 0.620);
uniform vec3 c_disc = vec3(0.961, 0.902, 0.820);
uniform vec3 c_arm = vec3(0.600, 0.722, 1.0);
uniform vec3 c_knot = vec3(1.0, 0.459, 0.580);
uniform vec3 c_dust = vec3(0.302, 0.239, 0.220);
uniform vec3 c_bg = vec3(0.067, 0.071, 0.078);

uniform bool dither_on = false;
uniform vec4 dither_a = vec4(3.0, 2.0, 1.15, 0.0);  // x pattern (0 fine grid, 1 coarse grid, 2 halftone, 3 grain), y colours in the palette (0: own colours), z contrast, w brightness
uniform float dither_levels = 4.0;                   // steps per colour with its own colours
uniform vec3 dither_pal[4];

// ---- noise (hash without sine, value noise with quintic fade) ----
float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 hash22(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.xx + p3.yz) * p3.zy); }
float hash13(vec3 p) { p = fract(p * 0.1031); p += dot(p, p.zyx + 31.32); return fract((p.x + p.y) * p.z); }

float noise2(vec2 p)
{
    vec2 i = floor(p), f = fract(p);
    vec2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float a = hash12(i), b = hash12(i + vec2(1, 0)), c = hash12(i + vec2(0, 1)), d = hash12(i + vec2(1, 1));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float noise3(vec3 p)
{
    vec3 i = floor(p), f = fract(p);
    vec3 u = f * f * (3.0 - 2.0 * f);
    float n000 = hash13(i), n100 = hash13(i + vec3(1, 0, 0)), n010 = hash13(i + vec3(0, 1, 0)), n110 = hash13(i + vec3(1, 1, 0));
    float n001 = hash13(i + vec3(0, 0, 1)), n101 = hash13(i + vec3(1, 0, 1)), n011 = hash13(i + vec3(0, 1, 1)), n111 = hash13(i + vec3(1, 1, 1));
    return mix(mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y), mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y), u.z);
}

const mat2 ROT = mat2(vec2(0.80, 0.60), vec2(-0.60, 0.80));

// fractal noise; octaves finer than about two pixels are skipped (upp: galaxy units per pixel)
float fbm2(vec2 p, float upp, int octaves)
{
    float sum = 0.0, amp = 0.5, freq = 1.0, norm = 0.0;
    for (int k = 0; k < 9; k++)
    {
        if (k >= octaves) break;
        float fade = 1.0 - smoothstep(0.25, 0.5, freq * upp);
        if (fade <= 0.0) break;
        sum += amp * fade * noise2(p);
        norm += amp * fade;
        p = ROT * p * 2.03 + 17.1;
        freq *= 2.03;
        amp *= 0.52;
    }
    return norm > 0.0 ? sum / norm : 0.5;
}

float ridged2(vec2 p, float upp, int octaves)
{
    float sum = 0.0, amp = 0.5, freq = 1.0, norm = 0.0;
    for (int k = 0; k < 8; k++)
    {
        if (k >= octaves) break;
        float fade = 1.0 - smoothstep(0.25, 0.5, freq * upp);
        if (fade <= 0.0) break;
        float n = 1.0 - abs(noise2(p) * 2.0 - 1.0);
        sum += amp * fade * n * n;
        norm += amp * fade;
        p = ROT * p * 2.11 + 5.3;
        freq *= 2.11;
        amp *= 0.55;
    }
    return norm > 0.0 ? sum / norm : 0.0;
}

// noise laid along the arms: across an arm it changes fast, along it slowly, so dust and star clouds stream with the spiral;
// 'flow' slides it outward along the arms, which is the gas drift
float armNoise(float d, float lr, float flow, float upp, int octaves)
{
    float sum = 0.0, amp = 0.5, norm = 0.0, freq = 1.0;
    vec3 p = vec3(cos(d) * 3.0, sin(d) * 3.0, lr * 2.2 - flow);
    for (int k = 0; k < 6; k++)
    {
        if (k >= octaves) break;
        float fade = 1.0 - smoothstep(0.08, 0.2, freq * upp * 3.0);
        if (fade <= 0.0) break;
        sum += amp * fade * noise3(p);
        norm += amp * fade;
        p = p * 2.07 + vec3(3.1, 7.7, 1.3);
        freq *= 2.07;
        amp *= 0.5;
    }
    return norm > 0.0 ? sum / norm : 0.5;
}

// one layer of point stars on a grid of 'cells' per galaxy radius; stars sit in the brighter parts of the galaxy.
// A share of them twinkle: a slow breath and, now and then, a short flare, each star on its own clock.
vec3 stars(vec2 q, float cells, float upp, float light, float seed, float base)
{
    float cellPx = 1.0 / (cells * upp);
    float vis = smoothstep(3.0, 9.0, cellPx);   // a layer whose stars would be closer than a few pixels is left out
    if (vis <= 0.0) return vec3(0.0);
    vec2 g = q * cells + seed;
    vec2 id = floor(g);
    vec2 h = hash22(id);
    float pick = hash12(id + 41.7);
    float chance = clamp((base + light * 0.75) * star_mix.z, 0.0, 0.9);
    if (pick > chance) return vec3(0.0);
    vec2 at = id + 0.15 + 0.7 * h;
    float distPx = length(g - at) / cells / upp;
    float mag = pow(hash12(id + 9.2), 6.0);
    float size = 0.55 + 1.1 * mag;
    float core = exp(-distPx * distPx / (size * size));
    float glow = mag * 0.25 * exp(-distPx / (2.5 + 5.0 * mag)) * (1.0 - smoothstep(0.06, 0.15, length(g - at))); // fades before the cell edge
    float t = hash12(id + 3.3);
    vec3 tint = t < 0.15 ? vec3(1.0, 0.78, 0.58) : t < 0.55 ? vec3(1.0, 0.95, 0.88) : vec3(0.74, 0.84, 1.0);
    float tw = 1.0;
    if (hash12(id + 77.1) < move.w)
    {
        float s = 0.5 + 0.5 * sin(time * twinkle_speed * (0.5 + h.x) + hash12(id + 5.5) * 6.2832);
        float s2 = 0.5 + 0.5 * sin(time * twinkle_speed * 0.37 * (0.6 + h.y) + hash12(id + 8.8) * 6.2832);
        tw = 1.0 + move.z * (2.2 * pow(s * s2, 14.0) - 0.45 * (1.0 - s));
    }
    return tint * (core * (0.35 + 0.9 * mag) + glow) * vis * tw;
}

// ---- the dither style (as in the Planet Maker): one buffer pixel is one dot ----
float bayer(ivec2 p, int bits)
{
    int x = p.x, xy = p.x ^ p.y, v = 0;
    for (int b = 0; b < 3; b++) { if (b >= bits) break; v = (v << 2) | (((xy >> b) & 1) << 1) | ((x >> b) & 1); }
    return (float(v) + 0.5) / float(1 << (2 * bits));
}

float ditherThr(ivec2 c)
{
    int pat = int(dither_a.x + 0.5);
    if (pat == 0) return bayer(c & ivec2(7), 3);
    if (pat == 1) return bayer(c & ivec2(3), 2);
    if (pat == 2)
    {
        vec2 r = mat2(vec2(0.7071, 0.7071), vec2(-0.7071, 0.7071)) * (vec2(c) + 0.5) / 5.0;
        vec2 g = fract(r) - 0.5;
        return clamp(1.0 - length(g) * 1.41421, 0.0, 1.0) * 0.98 + 0.01;
    }
    return texelFetch(blue_noise, c & ivec2(63), 0).r * (255.0 / 256.0) + 0.5 / 256.0;
}

vec3 ditherCol(vec3 col, ivec2 c)
{
    float t = ditherThr(c);
    vec3 cc = clamp((col - 0.5) * dither_a.z + 0.5 + dither_a.w, 0.0, 1.0);
    int n = int(dither_a.y + 0.5);
    if (n < 2) { float lv = dither_levels - 1.0; return min(floor(cc * lv + t), vec3(lv)) / lv; }
    float L = dot(cc, vec3(0.2126, 0.7152, 0.0722)) * float(n - 1);
    float i = floor(L);
    if (L - i > t) i += 1.0;
    return dither_pal[int(min(i, float(n - 1)))];
}

void fragment()
{
    vec2 sp = UV * view_size;
    vec2 w = view_center + (sp - view_size * 0.5) / px_per_unit;
    vec2 q = (w - galaxy_center) / galaxy_radius;
    float upp = 1.0 / (px_per_unit * pixel_scale * galaxy_radius); // galaxy units per buffer pixel
    float r = length(q);
    float th = atan(q.y, q.x);
    float rr = max(r, 0.035);
    float lr = log(rr / 0.04);
    float flow = time * move.x * 0.05;

    // where the systems are (smooth), outside the map: nothing
    float dens = texture(density, q / 2.3 + 0.5).r;

    // spiral arms: two log-spiral arms, an arm's light falls off across it; a little noise bends the arms so they are not perfect curves
    float wobble = (fbm2(q * 3.0 + 4.0, upp, 3) - 0.5) * arm.w;
    float d = th - (arm.x + arm.y * lr) + wobble;
    float across = 0.5 + 0.5 * cos(2.0 * d);         // 1 on an arm's spine, 0 between the arms
    float onArm = pow(across, arm.z);
    float armSharp = pow(across, arm.z * 3.0);
    // the leading side of each arm carries the dust lane, just inside the young stars
    float laneProfile = pow(0.5 + 0.5 * cos(2.0 * (d + dust_mix.z)), 10.0);

    // streaming clouds along the arms and clumpy star clouds in the disc
    float streams = armNoise(d, lr, flow, upp, 5);
    vec2 unwound = mat2(vec2(cos(-arm.y * lr), sin(-arm.y * lr)), vec2(-sin(-arm.y * lr), cos(-arm.y * lr))) * q;
    float clumps = fbm2(q * 9.0 + vec2(13.0, 7.0) + (streams - 0.5) * 0.6, upp, 8);
    float fine = fbm2(unwound * 30.0 + 2.0, upp, 6);

    // light: bulge (old, warm), bar-less inner disc, arms (young, blue), all shaped by where the systems are
    float edge = 1.0 - smoothstep(0.86 * shape_mix.x, (1.08 + 0.10 * (clumps - 0.5)) * shape_mix.x, r);
    float bs = light_mix.y;
    float bulge = light_mix.x * (1.30 * exp(-pow(r / (0.07 * bs), 0.85)) + 0.45 * exp(-r / (0.16 * bs)));
    float disc = light_mix.z * 0.20 * exp(-r / 0.85) * edge;
    float armLight = (0.06 + 0.95 * light_mix.w * onArm * smoothstep(0.06, 0.20, r)) * exp(-r / 0.95) * edge;
    float cloud = smoothstep(0.35, 0.85, clumps * 0.6 + streams * 0.4 + fine * 0.25 - 0.12);
    armLight *= mix(1.0, 0.35 + 1.25 * cloud, shape_mix.z);
    float follow = mix(1.0 - shape_mix.y, 1.0 + shape_mix.y, smoothstep(0.02, 0.6, dens));
    disc *= follow;
    armLight *= follow;

    vec3 col = c_bulge * bulge + c_disc * disc + mix(c_disc, c_arm, smoothstep(0.12, 0.45, r) * (0.4 + 0.6 * armSharp)) * armLight;

    // star-forming knots along the arm spines: small, faint, pinkish; they breathe slowly
    float knotsN = fbm2(q * 70.0 + 31.0, upp, 4);
    float breathe = 1.0 + move.y * 0.6 * sin(time * 0.5 + knotsN * 37.0 + q.x * 9.0);
    float knots = smoothstep(0.70, 0.86, knotsN) * armSharp * smoothstep(0.15, 0.3, r) * edge;
    col += c_knot * knots * 0.22 * shape_mix.w * breathe;
    col += c_arm * smoothstep(0.66, 0.80, knotsN) * armSharp * edge * 0.10 * shape_mix.w;

    // dust: dark lanes on the arms' leading edges, and filaments through the disc and across the bulge
    float rid = ridged2(unwound * 14.0 + vec2(streams * 1.5, 0.0), upp, 7);
    float dustLane = laneProfile * smoothstep(0.08, 0.22, r) * (0.45 + 0.75 * rid) * edge;
    float dustWeb = smoothstep(0.55, 0.95, rid) * (0.25 + 0.5 * onArm) * smoothstep(0.03, 0.12, r) * edge;
    float dust = clamp(dustLane * dust_mix.x + dustWeb * dust_mix.y, 0.0, 0.92);
    col *= mix(vec3(1.0), c_dust, dust);

    // a faint outer halo so the rim fades into space instead of stopping
    col += c_arm * 0.85 * 0.035 * exp(-r / 0.6) * (1.0 - edge * 0.5);

    // stars: unresolved grain, then single stars that appear as the map is zoomed in
    float light = clamp((bulge * 0.3 + disc + armLight) * 1.6, 0.0, 1.0);
    col += c_disc * (fine - 0.45) * 0.08 * light;
    vec3 st = stars(q, 90.0, upp, light, 0.0, 0.01) + stars(q, 260.0, upp, light, 7.3, 0.0) + stars(q, 760.0, upp, light, 19.1, 0.0) + stars(q, 2200.0, upp, light, 2.9, 0.0);
    st *= 1.0 - dust * 0.8;
    st *= star_mix.x;
    // a sparse field of foreground stars everywhere, so open space is not empty
    st += stars(q, 40.0, upp, 0.0, 51.0, 0.035) * star_mix.y;

    // tone: soft shoulder, then kept dim ('dimming for the map') so the map on top stays the thing you read
    vec3 lit = vec3(1.0) - exp(-col * 1.3);
    vec3 outc = clamp(c_bg + dust_mix.w * (lit * star_mix.w + st * 0.45), 0.0, 1.0);
    if (dither_on) outc = ditherCol(outc, ivec2(FRAGCOORD.xy));
    COLOR = vec4(outc, 1.0);
}
