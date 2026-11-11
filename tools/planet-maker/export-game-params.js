// Writes every system's planet settings for the game's live planets (godot/art/planets/planets-live.json), read by
// godot/Scripts/Ui/PlanetLive.cs. The sector window draws each planet live with the Planet Maker's own shader (Paul 2026-11-11:
// "the planets need to be animated, like in the planet maker tool, and following the parameters of each planets"), so it needs
// the settings exactly as the page draws them. They are taken from the page itself: for every system, loadSystem() (Paul's saved
// look from system-overrides.json first, else the lore sheet's preset over preset-base.json, else the first guess from its
// description), then the moons the page fills in for the moon count (ensureMoons), so nothing of the page's merging is re-done here.
//
//   node tools/planet-maker/export-game-params.js
//
// The file keeps it small: "base" holds the value most systems share for each setting, and each system lists only what differs
// (a ring or a moon list is written whole when it differs). Numbers keep 7 significant digits (the shader works in 32-bit floats).
// Needs Node and Playwright with Chromium (preinstalled in cloud sessions). Nothing is drawn, so it takes a few seconds.
const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright');

const OUT = path.resolve(__dirname, '..', '..', 'godot', 'art', 'planets', 'planets-live.json');
// what the page keeps in P but never draws with (or the game sets itself: no stars behind a tile)
const SKIP = new Set(['name', 'starDens']);

const round = (v) => {
  if (typeof v === 'number') return Number.isInteger(v) ? v : Number(v.toPrecision(7));
  if (Array.isArray(v)) return v.map(round);
  if (v && typeof v === 'object') return Object.fromEntries(Object.keys(v).sort().map((k) => [k, round(v[k])]));
  return v;
};

(async () => {
  require('child_process').execFileSync('python3', ['build.py', 'planet-maker.src.html', 'systems.json', 'index.html'], { cwd: __dirname, stdio: 'ignore' });
  const browser = await chromium.launch();
  const page = await browser.newPage();
  page.on('pageerror', (e) => console.error('page error:', e.message));
  // the page asks for its web fonts, and its script waits for that stylesheet; nothing here needs them, so refuse them at once
  await page.route(/fonts\.(googleapis|gstatic)\.com/, (r) => r.abort());
  page.setDefaultTimeout(180000);
  await page.goto('file://' + path.join(__dirname, 'index.html'));
  await page.waitForFunction(() => window.__pm && window.__pm.SYSTEMS.length > 0);
  // the names from systems.json itself: the page has no charset line, so a file:// load reads accents wrongly (as bake-thumbs.js)
  const names = JSON.parse(fs.readFileSync(path.join(__dirname, 'systems.json'), 'utf8')).map((s) => s.n);
  const all = await page.evaluate(() => {
    const pm = window.__pm;
    pm.freeze(true);
    return pm.SYSTEMS.map((s, i) => {
      pm.loadSystem(i);
      const n = pm.ensureMoons(pm.P);
      const P = JSON.parse(JSON.stringify(pm.P));
      P.moonList = P.moonList.slice(0, n);
      return P;
    });
  });
  await browser.close();
  if (all.length !== names.length) throw new Error(`the page has ${all.length} systems, systems.json ${names.length}`);

  const sys = all.map((P) => { const o = {}; for (const k of Object.keys(P).sort()) if (!SKIP.has(k)) o[k] = round(P[k]); return o; });
  // the base: for each setting, the value the most systems have
  const base = {};
  const keys = [...new Set(sys.flatMap((o) => Object.keys(o)))].sort();
  for (const k of keys) {
    const count = new Map();
    for (const o of sys) if (k in o) { const j = JSON.stringify(o[k]); count.set(j, (count.get(j) || 0) + 1); }
    const best = [...count.entries()].sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1))[0];
    if (best) base[k] = JSON.parse(best[0]);
  }
  const systems = {};
  names.forEach((name, i) => {
    const o = {};
    for (const k of Object.keys(sys[i])) if (JSON.stringify(sys[i][k]) !== JSON.stringify(base[k])) o[k] = sys[i][k];
    for (const k of keys) if (!(k in sys[i])) throw new Error(`${name} has no ${k}`);
    systems[name] = o;
  });
  const out = { format: 'praedra-planets-live', v: 1, source: 'tools/planet-maker/export-game-params.js', base, systems };
  fs.writeFileSync(OUT, JSON.stringify(out) + '\n');
  console.log(`wrote ${path.relative(process.cwd(), OUT)}: ${names.length} systems, ${(fs.statSync(OUT).size / 1024).toFixed(0)} KB`);
})().catch((e) => { console.error(e); process.exit(1); });
