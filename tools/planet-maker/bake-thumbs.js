// Bakes one small picture of every system's planet, drawn by the Planet Maker itself, for the game's sector window, and packs them
// into one sheet the game loads (godot/art/planets/atlas.png + atlas.json). The game cannot run the Planet Maker's WebGL shader, so it
// shows these pictures instead. Rebuild them whenever systems.json (the Lore Master's presets) changes:
//
//   node tools/planet-maker/bake-thumbs.js bake /tmp/planet-thumbs        (about 10 s a picture in software GL; 555 systems)
//   node tools/planet-maker/bake-thumbs.js pack /tmp/planet-thumbs        (writes godot/art/planets/atlas.png and atlas.json)
//
// To share the bake between cores, run several at once: SHARD=0/3 node ... bake DIR & SHARD=1/3 ... & SHARD=2/3 ...
// To redo a few systems only: node ... bake DIR Charter "The Thaw"; then pack again. Pictures missing from DIR are copied from the
// current sheet, so a fresh DIR with only the redone systems is enough (apply-saved.py prints the names Paul's saved looks changed).
// Each picture is SIZE (128) dots square, drawn with the dither style on (Grain, the planet's own colours, one canvas pixel per dot), so
// the game shows it with nearest filtering at a whole number of screen pixels per dot. The sky is left black and the camera frames the
// planet and its rings; moons further out are cropped. Needs Node and Playwright with Chromium (preinstalled in cloud sessions).
const path = require('path');
const fs = require('fs');
const { chromium } = require('playwright');

const SIZE = 128;
const slug = (t) => t.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');

async function open() {
  const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
  const page = await browser.newPage({ viewport: { width: 900, height: 700 } });
  page.setDefaultTimeout(180000);
  page.on('pageerror', (e) => console.error('page error:', e.message));
  await page.goto('file://' + path.join(__dirname, 'index.html'), { timeout: 180000 });
  await page.waitForFunction(() => window.__pm && window.__pm.SYSTEMS.length > 0);
  return { browser, page };
}

async function bake(dir, only) {
  fs.mkdirSync(dir, { recursive: true });
  // the names from systems.json itself: the page has no charset line, so a file:// load reads accents wrongly (Ciénaga)
  const names = JSON.parse(fs.readFileSync(path.join(__dirname, 'systems.json'), 'utf8')).map((s) => s.n);
  // a name that is no system (a two-word name split by the shell, QA-75) would bake nothing and leave the old picture: stop instead
  const unknown = [...only].filter((n) => !names.includes(n));
  if (unknown.length) throw new Error('not a system: ' + unknown.join(', ') + ' (quote the names, or use mapfile; see README)');
  const { browser, page } = await open();
  const kinds = await page.evaluate(() => window.__pm.SYSTEMS.map((s, i) => { window.__pm.loadSystem(i); return window.__pm.P.kind; }));
  const [shard, shards] = (process.env.SHARD || '0/1').split('/').map(Number);
  if (shard === 0) fs.writeFileSync(path.join(dir, 'kinds.json'), JSON.stringify(Object.fromEntries(names.map((n, i) => [n, kinds[i]]))));
  for (let i = 0; i < names.length; i++) {
    if (only.size ? !only.has(names[i]) : i % shards !== shard) continue;
    const url = await page.evaluate(({ i, size }) => {
      const pm = window.__pm;
      pm.freeze(true);
      pm.setDither({ on: true, pat: 'grain', pal: 'own', dot: 1, contrast: 1.15, bright: 0.02, levels: 4 });
      pm.loadSystem(i);
      const P = pm.P;
      P.starDens = 0;
      const mega = P.kind === 'ring' || P.kind === 'shell';
      const ring = P.ring.on && !mega ? (P.ring.inner + P.ring.width) * 1.04 : 0;
      const moons = (P.moonList || []).slice(0, P.moons).reduce((m, o) => Math.max(m, o.orbit + o.size), 0);
      // the Planet Maker pulls back to fit the farthest moon; a thumbnail frames the planet and its rings instead
      P.zoom = P.zoom * Math.max(1.3, ring, moons * 0.95) / Math.max(1.3, ring);
      const cv = document.getElementById('cv');
      cv.width = size; cv.height = size;
      pm.setQ(1);
      pm.draw();
      return cv.toDataURL('image/png');
    }, { i, size: SIZE });
    fs.writeFileSync(path.join(dir, slug(names[i]) + '.png'), Buffer.from(url.split(',')[1], 'base64'));
    if (i % 30 === shard) console.log(`${i + 1}/${names.length} ${names[i]}`);
  }
  await browser.close();
}

async function pack(dir) {
  const kinds = JSON.parse(fs.readFileSync(path.join(dir, 'kinds.json'), 'utf8'));
  const names = Object.keys(kinds).sort();
  const cols = Math.ceil(Math.sqrt(names.length));
  const out = path.resolve(__dirname, '..', '..', 'godot', 'art', 'planets');
  // a picture not baked into DIR comes from its cell in the current sheet
  let old = null;
  try { old = { idx: JSON.parse(fs.readFileSync(path.join(out, 'atlas.json'), 'utf8')), png: 'data:image/png;base64,' + fs.readFileSync(path.join(out, 'atlas.png')).toString('base64') }; } catch (e) {}
  const missing = names.filter((n) => !fs.existsSync(path.join(dir, slug(n) + '.png')) && !(old && old.idx.size === SIZE && old.idx.systems[n]));
  if (missing.length) throw new Error(`${missing.length} pictures missing, e.g. ${missing.slice(0, 5).join(', ')}`);
  const { browser, page } = await open();
  const pics = names.map((n) => fs.existsSync(path.join(dir, slug(n) + '.png')) ? 'data:image/png;base64,' + fs.readFileSync(path.join(dir, slug(n) + '.png')).toString('base64') : old.idx.systems[n][0]);
  const url = await page.evaluate(async ({ pics, cols, size, old }) => {
    const c = document.createElement('canvas');
    c.width = cols * size; c.height = Math.ceil(pics.length / cols) * size;
    const g = c.getContext('2d');
    g.imageSmoothingEnabled = false;
    let sheet = null;
    if (old) { sheet = new Image(); sheet.src = old.png; await sheet.decode(); }
    for (let i = 0; i < pics.length; i++) {
      const x = (i % cols) * size, y = Math.floor(i / cols) * size;
      if (typeof pics[i] === 'number') { const k = pics[i], oc = old.idx.cols; g.drawImage(sheet, (k % oc) * size, Math.floor(k / oc) * size, size, size, x, y, size, size); continue; }
      const im = new Image(); im.src = pics[i]; await im.decode();
      g.drawImage(im, x, y);
    }
    return c.toDataURL('image/png');
  }, { pics, cols, size: SIZE, old });
  await browser.close();
  fs.mkdirSync(out, { recursive: true });
  fs.writeFileSync(path.join(out, 'atlas.png'), Buffer.from(url.split(',')[1], 'base64'));
  const systems = Object.fromEntries(names.map((n, i) => [n, [i, kinds[n]]]));
  fs.writeFileSync(path.join(out, 'atlas.json'), JSON.stringify({ size: SIZE, cols, source: 'tools/planet-maker/bake-thumbs.js', systems }) + '\n');
  console.log(`packed ${names.length} pictures, ${cols} a row, into ${out}`);
}

(async () => {
  const [cmd, dir, ...only] = process.argv.slice(2);
  if (!dir || (cmd !== 'bake' && cmd !== 'pack')) { console.error('usage: bake-thumbs.js bake|pack DIR [system...]'); process.exit(2); }
  if (cmd === 'bake') await bake(path.resolve(dir), new Set(only));
  else await pack(path.resolve(dir));
})().catch((e) => { console.error(e); process.exit(1); });
