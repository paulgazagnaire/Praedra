// render.js IN.html OUT.png WIDTH HEIGHT: draws an HTML fragment at 2x with the game's own fonts (godot/fonts), close to what Paper shows
const { chromium } = require('playwright');
const fs = require('fs'), path = require('path');
(async () => {
  const [inp, out, w, h] = process.argv.slice(2);
  const fonts = path.resolve(__dirname, '..', '..', 'godot', 'fonts');
  const face = (family, weight, file) => `@font-face{font-family:'${family}';font-weight:${weight};src:url('file://${fonts}/${file}')}`;
  const css = [face('Space Grotesk', 400, 'SpaceGrotesk-Regular.ttf'), face('Space Grotesk', 500, 'SpaceGrotesk-Medium.ttf'),
    face('BitPotion', 500, 'BitPotion.ttf'), face('JetBrains Mono', 400, 'JetBrainsMono-Regular.ttf'), face('JetBrains Mono', 500, 'JetBrainsMono-Medium.ttf'),
    face('Inter', 400, 'Inter-Regular.otf'), face('Inter', 500, 'Inter-Medium.otf'), face('Inter', 600, 'Inter-SemiBold.otf'),
    '*{box-sizing:border-box;margin:0;padding:0} body{background:#000}'].join('\n');
  const wrap = path.resolve(inp) + '.wrap.html';
  fs.writeFileSync(wrap, `<!doctype html><meta charset=utf-8><style>${css}</style>${fs.readFileSync(inp, 'utf8')}`);
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: +w, height: +h }, deviceScaleFactor: 2 });
  await page.goto('file://' + wrap);
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: out });
  await browser.close();
})();
