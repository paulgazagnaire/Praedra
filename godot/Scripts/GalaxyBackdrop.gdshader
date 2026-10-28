// The galaxy drawn behind the map (Paul 2026-10-28: "a galaxy illustration in the back of the sectors... very detailed, just in code").
// Drawing only: nothing here touches the game. Coordinates are galaxy units (the disc's radius is 1, the centre is Charter).
// The arms follow the two-arm spiral the systems were laid out on (specs/creative/galaxy-shape/README.md and build_gal.py:
// pitch 17 degrees, arm phase 67 degrees, two arms), and their light is weighted by where the systems really are (the density map).
// Everything is computed per pixel, so zooming in shows finer dust and resolves single stars instead of blurring.
shader_type canvas_item;

uniform vec2 view_size;             // the map control's size in pixels
uniform vec2 view_center;           // world point at the middle of the control
uniform float px_per_unit = 1.0;    // pixels (points) per world unit at the current zoom
uniform float pixel_scale = 1.0;    // screen pixels per point (2 on a Retina screen), for star sizes and noise detail
uniform vec2 galaxy_center;         // world position of the centre (Charter)
uniform float galaxy_radius = 480.0;
uniform float arm_phase = 1.17;     // radians at the inner end of the arms
uniform float arm_wind = 1.145;     // radians the arms turn per e-fold of radius
uniform sampler2D density : filter_linear, repeat_disable;  // where the systems are, 0..1, over centre +- 1.15 radius
uniform vec3 background = vec3(0.067, 0.071, 0.078);
uniform float strength = 1.0;       // overall brightness of the galaxy over the background

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

// noise laid along the arms: across an arm it changes fast, along it slowly, so dust and star clouds stream with the spiral
float armNoise(float d, float lr, float upp, int octaves)
{
    float sum = 0.0, amp = 0.5, norm = 0.0, freq = 1.0;
    vec3 p = vec3(cos(d) * 3.0, sin(d) * 3.0, lr * 2.2);
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

// one layer of point stars on a grid of 'cells' per galaxy radius; stars sit in the brighter parts of the galaxy
vec3 stars(vec2 q, float cells, float upp, float light, float seed, float base)
{
    float cellPx = 1.0 / (cells * upp);
    float vis = smoothstep(3.0, 9.0, cellPx);   // a layer whose stars would be closer than a few pixels is left out
    if (vis <= 0.0) return vec3(0.0);
    vec2 g = q * cells + seed;
    vec2 id = floor(g);
    vec2 h = hash22(id);
    float pick = hash12(id + 41.7);
    float chance = clamp(base + light * 0.75, 0.0, 0.9);
    if (pick > chance) return vec3(0.0);
    vec2 at = id + 0.15 + 0.7 * h;
    float distPx = length(g - at) / cells / upp;
    float mag = pow(hash12(id + 9.2), 6.0);
    float size = 0.55 + 1.1 * mag;
    float core = exp(-distPx * distPx / (size * size));
    float glow = mag * 0.25 * exp(-distPx / (2.5 + 5.0 * mag)) * (1.0 - smoothstep(0.06, 0.15, length(g - at))); // fades before the cell edge
    float t = hash12(id + 3.3);
    vec3 tint = t < 0.15 ? vec3(1.0, 0.78, 0.58) : t < 0.55 ? vec3(1.0, 0.95, 0.88) : vec3(0.74, 0.84, 1.0);
    return tint * (core * (0.35 + 0.9 * mag) + glow) * vis;
}

void fragment()
{
    vec2 sp = UV * view_size;
    vec2 w = view_center + (sp - view_size * 0.5) / px_per_unit;
    vec2 q = (w - galaxy_center) / galaxy_radius;
    float upp = 1.0 / (px_per_unit * pixel_scale * galaxy_radius); // galaxy units per screen pixel
    float r = length(q);
    float th = atan(q.y, q.x);
    float rr = max(r, 0.035);
    float lr = log(rr / 0.04);

    // where the systems are (smooth), outside the map: nothing
    vec2 duv = q / 2.3 + 0.5;
    float dens = texture(density, duv).r;

    // spiral arms: two log-spiral arms, an arm's light falls off across it; a little noise bends the arms so they are not perfect curves
    float wobble = (fbm2(q * 3.0 + 4.0, upp, 3) - 0.5) * 0.35;
    float d = th - (arm_phase + arm_wind * lr) + wobble;
    float across = 0.5 + 0.5 * cos(2.0 * d);         // 1 on an arm's spine, 0 between the arms
    float arm = pow(across, 3.0);
    float armSharp = pow(across, 9.0);
    // the leading side of each arm carries the dust lane, just inside the young stars
    float laneProfile = pow(0.5 + 0.5 * cos(2.0 * (d + 0.42)), 10.0);

    // streaming clouds along the arms and clumpy star clouds in the disc
    float streams = armNoise(d, lr, upp, 5);
    vec2 unwound = mat2(vec2(cos(-arm_wind * lr), sin(-arm_wind * lr)), vec2(-sin(-arm_wind * lr), cos(-arm_wind * lr))) * q;
    float clumps = fbm2(q * 9.0 + vec2(13.0, 7.0) + (streams - 0.5) * 0.6, upp, 8);
    float fine = fbm2(unwound * 30.0 + 2.0, upp, 6);

    // light: bulge (old, warm), bar-less inner disc, arms (young, blue), all shaped by where the systems are
    float edge = 1.0 - smoothstep(0.86, 1.08 + 0.10 * (clumps - 0.5), r);
    float bulge = 1.30 * exp(-pow(r / 0.07, 0.85)) + 0.45 * exp(-r / 0.16);
    float disc = 0.20 * exp(-r / 0.85) * edge;
    float armLight = (0.06 + 0.95 * arm * smoothstep(0.06, 0.20, r)) * exp(-r / 0.95) * edge;
    float cloud = smoothstep(0.35, 0.85, clumps * 0.6 + streams * 0.4 + fine * 0.25 - 0.12);
    armLight *= 0.35 + 1.25 * cloud;
    float shape = mix(0.70, 1.30, smoothstep(0.02, 0.6, dens));
    disc *= shape;
    armLight *= shape;

    vec3 warm = vec3(1.00, 0.84, 0.62);
    vec3 cream = vec3(0.96, 0.90, 0.82);
    vec3 young = vec3(0.60, 0.72, 1.00);
    vec3 col = warm * bulge + cream * disc + mix(cream, young, smoothstep(0.12, 0.45, r) * (0.4 + 0.6 * armSharp)) * armLight;

    // star-forming knots along the arm spines: small, faint, pinkish (rare)
    float knotsN = fbm2(q * 70.0 + 31.0, upp, 4);
    float knots = smoothstep(0.70, 0.86, knotsN) * armSharp * smoothstep(0.15, 0.3, r) * edge;
    col += vec3(1.0, 0.46, 0.58) * knots * 0.22;
    col += young * smoothstep(0.66, 0.80, knotsN) * armSharp * edge * 0.10;

    // dust: dark lanes on the arms' leading edges, and filaments through the disc and across the bulge
    float rid = ridged2(unwound * 14.0 + vec2(streams * 1.5, 0.0), upp, 7);
    float dustLane = laneProfile * smoothstep(0.08, 0.22, r) * (0.45 + 0.75 * rid) * edge;
    float dustWeb = smoothstep(0.55, 0.95, rid) * (0.25 + 0.5 * arm) * smoothstep(0.03, 0.12, r) * edge;
    float dust = clamp(dustLane * 0.85 + dustWeb * 0.55, 0.0, 0.92);
    col *= mix(vec3(1.0), vec3(0.30, 0.24, 0.22), dust);

    // a faint outer halo so the rim fades into space instead of stopping
    col += vec3(0.55, 0.62, 0.80) * 0.035 * exp(-r / 0.6) * (1.0 - edge * 0.5);

    // stars: unresolved grain, then single stars that appear as the map is zoomed in
    float light = clamp((bulge * 0.3 + disc + armLight) * 1.6, 0.0, 1.0);
    col += cream * (fine - 0.45) * 0.08 * light;
    vec3 st = stars(q, 90.0, upp, light, 0.0, 0.01) + stars(q, 260.0, upp, light, 7.3, 0.0) + stars(q, 760.0, upp, light, 19.1, 0.0) + stars(q, 2200.0, upp, light, 2.9, 0.0);
    st *= 1.0 - dust * 0.8;
    // a sparse field of foreground stars everywhere, so open space is not empty
    st += stars(q, 40.0, upp, 0.0, 51.0, 0.035) * 0.45;

    // tone: soft shoulder, then kept dim so the map on top stays the thing you read
    vec3 lit = vec3(1.0) - exp(-col * 1.3);
    vec3 outc = background + strength * (lit * 0.32 + st * 0.45);
    COLOR = vec4(outc, 1.0);
}
