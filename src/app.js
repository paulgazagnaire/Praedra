/* ===== RENDER/APP (DOM side; the sim above never touches this) ===== */
(function () {
  'use strict';
  var cv = document.getElementById('cv');
  var g = cv.getContext('2d');
  var hud = document.getElementById('hud');
  var banner = document.getElementById('banner');
  var help = document.getElementById('help');
  var ui = {
    speed: document.getElementById('speed'),
    grav: document.getElementById('grav'),
    gravVal: document.getElementById('gravVal'),
    newbattle: document.getElementById('newbattle'),
    restart: document.getElementById('restart'),
    pause: document.getElementById('pause'),
    mode: document.getElementById('mode'),
    modeinfo: document.getElementById('modeinfo'),
    matchup: document.getElementById('matchup'),
    // --- setup overlay ---
    setup: document.getElementById('setup'),
    rows: { A: document.getElementById('rowsA'), B: document.getElementById('rowsB') },
    presets: { A: document.getElementById('presetA'), B: document.getElementById('presetB') },
    budget: { A: document.getElementById('budgetA'), B: document.getElementById('budgetB') },
    setSeed: document.getElementById('setSeed'),
    setRandom: document.getElementById('setRandom'),
    setDensity: document.getElementById('setDensity'),
    densityVal: document.getElementById('densityVal'),
    setGrav: document.getElementById('setGrav'),
    setGravVal: document.getElementById('setGravVal'),
    setDestr: document.getElementById('setDestr'),
    setMode: document.getElementById('setMode'),
    startBtn: document.getElementById('startBtn'),
    startReason: document.getElementById('startReason'),
  };
  var COLORS = { A: '#4da3ff', B: '#ff7a5c' };
  var SEL_COLOR = '#7cffb2';
  // Ship visual models (render-side only). Vertices in multiples of def.radius; +x = nose, +y = starboard.
  var SHIP_PALETTE = {
    A: { base: '#4da3ff', dark: '#2b5f9e', accent: '#b3d6ff' },
    B: { base: '#ff7a5c', dark: '#a34630', accent: '#ffc9b5' }
  };
  var HULLS = {
    interceptor: [[1.6,0],[-0.1,0.5],[-1.0,0.95],[-0.75,0.25],[-0.75,-0.25],[-1.0,-0.95],[-0.1,-0.5]],
    bomber:      [[1.5,0],[0.6,0.35],[0.2,0.95],[-0.5,1.0],[-0.55,0.4],[-1.1,0.3],[-1.1,-0.3],[-0.55,-0.4],[-0.5,-1.0],[0.2,-0.95],[0.6,-0.35]],
    frigate:     [[1.7,0],[1.05,0.40],[0.35,0.52],[-0.75,0.52],[-1.05,0.38],[-1.3,0.38],[-1.3,-0.38],[-1.05,-0.38],[-0.75,-0.52],[0.35,-0.52],[1.05,-0.40]],
    destroyer:   [[1.8,0],[1.0,0.42],[0.2,0.55],[-1.0,0.55],[-1.6,0.42],[-1.6,-0.42],[-1.0,-0.55],[0.2,-0.55],[1.0,-0.42]],
    battleship:  [[1.7,0],[1.15,0.30],[0.55,0.46],[-0.9,0.50],[-1.25,0.42],[-1.5,0.30],[-1.5,-0.30],[-1.25,-0.42],[-0.9,-0.50],[0.55,-0.46],[1.15,-0.30]]
  };
  var ENGINE = { // [exitX, plumeHalfWidth] in r-units
    interceptor: [-0.75, 0.25], bomber: [-1.1, 0.28], frigate: [-1.3, 0.34],
    destroyer:   [-1.6, 0.36],  battleship: [-1.5, 0.40]
  };
  var TURRET_X_FALLBACK = [0.90, 0.30, -0.55]; // battleship turret centers, x*r on centerline
  // LOD_DETAIL tuned down from the spec's 3 to 2.5: at the default 1600x1000 window against
  // this sim's 8000x5600 arena (scale ~0.168), the frigate (r=16) sits at px~2.69 -- just under
  // 3, so it fell back to hull+marker like the smaller classes and read as an indistinct blob.
  // 2.5 keeps interceptor (px~1.35) and bomber (px~1.68) in the simplified tier as designed
  // (they're meant to stay simple "at full zoom-out") while bringing the frigate's dark
  // engine block + accent deck strip into view, restoring 5-way silhouette distinguishability.
  var LOD_DETAIL = 2.5, LOD_FINE = 7;          // thresholds on px = def.radius * scale

  function polyPath(g, pts, r) {
    g.beginPath(); g.moveTo(pts[0][0] * r, pts[0][1] * r);
    for (var i = 1; i < pts.length; i++) g.lineTo(pts[i][0] * r, pts[i][1] * r);
    g.closePath();
  }

  function drawPlume(g, r, exitX, halfW, throttle) {
    if (!(throttle > 0.02)) return;
    var len = r * (0.7 + 1.6 * throttle) * (0.85 + 0.3 * Math.random());
    var w = r * halfW * (0.6 + 0.4 * throttle), ex = exitX * r;
    g.fillStyle = 'rgba(120,180,255,' + (0.25 + 0.35 * throttle) + ')';
    g.beginPath(); g.moveTo(ex, w); g.lineTo(ex - len, 0); g.lineTo(ex, -w); g.closePath(); g.fill();
    g.fillStyle = 'rgba(230,244,255,' + (0.5 + 0.35 * throttle) + ')';
    g.beginPath(); g.moveTo(ex, w * 0.45); g.lineTo(ex - len * 0.6, 0); g.lineTo(ex, -w * 0.45); g.closePath(); g.fill();
  }

  // Draws hull + machinery/superstructure layers + LOD-gated fine details + turrets.
  // Called from drawShip inside the translated+rotated ship frame.
  function drawHull(g, s, scale) {
    var r = s.def.radius, P = SHIP_PALETTE[s.team], px = r * scale;
    var eng = ENGINE[s.cls];
    drawPlume(g, r, eng[0], eng[1], s.throttle || 0);
    g.fillStyle = P.base; g.strokeStyle = '#0a0f18'; g.lineWidth = 1.5 / scale;
    polyPath(g, HULLS[s.cls], r); g.fill(); g.stroke();
    if (px < LOD_DETAIL) { // tiny: keep white nose marker, skip detail
      g.strokeStyle = '#fff'; g.lineWidth = 1.5 / scale;
      g.beginPath(); g.moveTo(r * 0.4, 0); g.lineTo(r * 1.7, 0); g.stroke();
      return;
    }
    var fine = px >= LOD_FINE;
    if (s.cls === 'interceptor') {
      g.fillStyle = P.accent;
      g.beginPath(); g.moveTo(0.9 * r, 0); g.lineTo(0.1 * r, 0.18 * r); g.lineTo(0.1 * r, -0.18 * r); g.closePath(); g.fill();
    } else if (s.cls === 'bomber') {
      g.fillStyle = P.accent;
      g.beginPath(); g.moveTo(0.95 * r, 0); g.lineTo(0.3 * r, 0.16 * r); g.lineTo(0.3 * r, -0.16 * r); g.closePath(); g.fill();
      if (fine) {
        g.fillStyle = P.dark;
        g.fillRect(-0.15 * r, 0.55 * r, 0.55 * r, 0.22 * r);
        g.fillRect(-0.15 * r, -0.77 * r, 0.55 * r, 0.22 * r);
      }
    } else if (s.cls === 'frigate') {
      g.fillStyle = P.dark;   g.fillRect(-1.3 * r, -0.40 * r, 0.35 * r, 0.80 * r);
      g.fillStyle = P.accent; g.fillRect(-0.55 * r, -0.12 * r, 1.30 * r, 0.24 * r);
      if (fine) {
        g.fillStyle = P.dark;
        g.fillRect(0.85 * r, 0.10 * r, 0.16 * r, 0.16 * r);
        g.fillRect(0.85 * r, -0.26 * r, 0.16 * r, 0.16 * r);
      }
    } else if (s.cls === 'destroyer') {
      g.fillStyle = P.dark;   g.fillRect(0.20 * r, -0.07 * r, 1.35 * r, 0.14 * r);   // spinal railgun
      g.fillStyle = P.accent; g.fillRect(-0.55 * r, -0.28 * r, 0.65 * r, 0.56 * r);  // bridge
      g.fillStyle = P.dark;   g.fillRect(-1.6 * r, -0.34 * r, 0.30 * r, 0.68 * r);   // engines
      if (fine) {
        g.fillStyle = P.accent;
        g.fillRect(1.50 * r, -0.04 * r, 0.12 * r, 0.08 * r);
        g.fillRect(-1.56 * r, -0.26 * r, 0.12 * r, 0.18 * r);
        g.fillRect(-1.56 * r, 0.08 * r, 0.12 * r, 0.18 * r);
      }
    } else if (s.cls === 'battleship') {
      g.fillStyle = P.dark;   g.fillRect(-1.10 * r, -0.14 * r, 2.50 * r, 0.28 * r);  // armored spine
      g.fillStyle = P.accent; g.fillRect(-0.32 * r, -0.22 * r, 0.38 * r, 0.44 * r);  // superstructure
      g.fillStyle = P.dark;   g.fillRect(-1.5 * r, -0.36 * r, 0.28 * r, 0.72 * r);   // engines
      if (fine) {
        g.fillStyle = P.accent;
        for (var ny = 0; ny < 3; ny++) g.fillRect(-1.46 * r, (-0.28 + 0.21 * ny) * r, 0.12 * r, 0.14 * r);
        g.strokeStyle = P.dark; g.lineWidth = 1 / scale;
        g.beginPath();
        g.moveTo(-0.8 * r, 0.40 * r); g.lineTo(0.5 * r, 0.40 * r);
        g.moveTo(-0.8 * r, -0.40 * r); g.lineTo(0.5 * r, -0.40 * r);
        g.stroke();
      }
      var HR = match.config.heavyRail;
      var coolMax = (HR && HR.cooldown) || 4;                          // per-turret cooldown, from CONFIG
      var loadMax = (HR && HR.loadTime) || 1.2;                        // rail-charge time, from CONFIG
      var turretX = (HR && HR.turretMounts) || TURRET_X_FALLBACK;      // fractions of def.radius, sim-mirrored
      var ts = s.turrets || [];
      for (var i = 0; i < ts.length && i < turretX.length; i++) {
        var t = ts[i], a = t.ang - s.heading;
        var f = Math.max(0, Math.min(1, (t.cool - (coolMax - 0.18)) / 0.18)); // 1 just fired -> 0
        var lf = Math.max(0, Math.min(1, (t.load || 0) / loadMax));           // rail-charge 0 -> 1
        g.save(); g.translate(turretX[i] * r, 0); g.rotate(a);
        g.fillStyle = P.dark;
        g.beginPath(); g.arc(0, 0, 0.18 * r, 0, 6.2832); g.fill();
        // twin rails (it reads railgun) with recoil on the whole cradle
        g.fillRect((0.10 - 0.06 * f) * r, -0.085 * r, 0.46 * r, 0.055 * r);
        g.fillRect((0.10 - 0.06 * f) * r,  0.030 * r, 0.46 * r, 0.055 * r);
        g.fillStyle = P.base;
        g.beginPath(); g.arc(0, 0, 0.11 * r, 0, 6.2832); g.fill();
        if (lf > 0.04 && fine) {
          // charge glow between the rails, cool blue brightening to white as the load completes
          g.fillStyle = lf >= 1 ? 'rgba(255,255,255,0.95)'
                                : 'rgba(150,220,255,' + (0.25 + 0.6 * lf) + ')';
          g.fillRect((0.12 - 0.06 * f) * r, -0.022 * r, 0.42 * r * lf, 0.044 * r);
        }
        if (f > 0) {
          g.fillStyle = 'rgba(255,235,180,' + f + ')';
          g.beginPath(); g.moveTo((0.56 - 0.06 * f) * r, 0);
          g.lineTo(0.80 * r, 0.09 * r); g.lineTo(0.80 * r, -0.09 * r); g.closePath(); g.fill();
        }
        g.restore();
      }
    }
  }
  var match = null, paused = false, effects = [], markers = [];
  var overlayVisible = false;       // setup screen shown -> sim never steps
  var selectedSet = new Set();      // ids of currently-selected friendly (team A) ships
  var renderedEnemyIds = [];        // enemy ids drawn as real ships last frame (fog predicate)
  var hover = null, drag = null, down = null;

  function isSpectate() { return ui.mode.value === 'spectate'; }

  /* ================= FLEET SETUP SCREEN ================= */
  var CFG = Praedra.defaultConfig();
  var BUDGET = CFG.fleetPoints;                                  // 42
  // heavies first: spawn ranks put earlier array entries at the front
  var CLASSES = [
    { cls: 'battleship',  letter: 'BB', glyph: '⬢' },       // ⬢ hexagon (heaviest — front rank)
    { cls: 'destroyer',   letter: 'D', glyph: '▮' },        // ▮ rectangle
    { cls: 'frigate',     letter: 'F', glyph: '■' },        // ■ square
    { cls: 'bomber',      letter: 'B', glyph: '◆' },        // ◆ diamond
    { cls: 'interceptor', letter: 'I', glyph: '▲' },        // ▲ triangle
  ];
  function costOf(cls) { return CFG.ships[cls].cost; }
  // per-team live counts
  var fleets = { A: emptyCounts(), B: emptyCounts() };
  var currentSetup = null;          // snapshot used by the running match / restart
  // DOM handles built by buildSetupDOM()
  var countEl = { A: {}, B: {} }, subEl = { A: {}, B: {} }, plusEl = { A: {}, B: {} }, minusEl = { A: {}, B: {} };

  // generalised over CLASSES so a new class row (e.g. battleship) can't desync the setup UI
  function emptyCounts() { var c = {}; for (var i = 0; i < CLASSES.length; i++) c[CLASSES[i].cls] = 0; return c; }
  function copyCounts(c) { var o = {}; for (var i = 0; i < CLASSES.length; i++) o[CLASSES[i].cls] = c[CLASSES[i].cls] || 0; return o; }
  function spentOf(counts) { var s = 0; for (var i = 0; i < CLASSES.length; i++) s += counts[CLASSES[i].cls] * costOf(CLASSES[i].cls); return s; }
  function shipsOf(counts) { var n = 0; for (var i = 0; i < CLASSES.length; i++) n += counts[CLASSES[i].cls]; return n; }
  function countsFromPreset(name) {
    var arr = CFG.presets[name] || [], c = emptyCounts();
    for (var i = 0; i < arr.length; i++) if (c[arr[i]] != null) c[arr[i]]++;
    return c;
  }
  function fleetArray(counts) {                                  // heavies-first class-name array for createMatch
    var out = [];
    for (var i = 0; i < CLASSES.length; i++) { var cl = CLASSES[i].cls; for (var k = 0; k < counts[cl]; k++) out.push(cl); }
    return out;
  }
  // over-budget is impossible: clamp any assignment so spent never exceeds BUDGET
  function setCount(team, cls, n) {
    n = Math.max(0, Math.floor(n));
    var others = spentOf(fleets[team]) - fleets[team][cls] * costOf(cls);
    var maxN = Math.floor((BUDGET - others) / costOf(cls));
    fleets[team][cls] = Math.min(n, Math.max(0, maxN));
    renderSetup();
  }
  function applyPreset(team, name) { fleets[team] = countsFromPreset(name); renderSetup(); }
  function mirrorA() { fleets.B = copyCounts(fleets.A); renderSetup(); }

  function buildSetupDOM() {
    ['A', 'B'].forEach(function (team) {
      // preset buttons
      var presetSpecs = [['Railgun', 'RAILGUN'], ['Swarm', 'SWARM'], ['Balanced', 'BALANCED']];
      presetSpecs.forEach(function (p) {
        var b = document.createElement('button');
        b.textContent = p[0];
        b.onclick = (function (nm) { return function () { applyPreset(team, nm); }; })(p[1]);
        ui.presets[team].appendChild(b);
      });
      if (team === 'B') {
        var mb = document.createElement('button');
        mb.textContent = 'Mirror A'; mb.onclick = mirrorA;
        ui.presets[team].appendChild(mb);
      }
      // class rows
      CLASSES.forEach(function (c) {
        var row = document.createElement('div'); row.className = 'classRow';
        var gl = document.createElement('span'); gl.className = 'glyph'; gl.textContent = c.glyph;
        var nm = document.createElement('span'); nm.className = 'cname'; nm.textContent = c.cls;
        var co = document.createElement('span'); co.className = 'ccost'; co.textContent = costOf(c.cls) + ' pts';
        var st = document.createElement('span'); st.className = 'stepper';
        var minus = document.createElement('button'); minus.textContent = '−';
        var cnt = document.createElement('span'); cnt.className = 'count';
        var plus = document.createElement('button'); plus.textContent = '+';
        minus.onclick = (function (cl) { return function () { setCount(team, cl, fleets[team][cl] - 1); }; })(c.cls);
        plus.onclick = (function (cl) { return function () { setCount(team, cl, fleets[team][cl] + 1); }; })(c.cls);
        st.appendChild(minus); st.appendChild(cnt); st.appendChild(plus);
        var sub = document.createElement('span'); sub.className = 'subtotal';
        row.appendChild(gl); row.appendChild(nm); row.appendChild(co); row.appendChild(st); row.appendChild(sub);
        ui.rows[team].appendChild(row);
        countEl[team][c.cls] = cnt; subEl[team][c.cls] = sub;
        plusEl[team][c.cls] = plus; minusEl[team][c.cls] = minus;
      });
    });
  }

  function teamTag(counts) {                                     // "42pts (2D 4F 5B 8I)"
    var parts = [];
    for (var i = 0; i < CLASSES.length; i++) { var n = counts[CLASSES[i].cls]; if (n) parts.push(n + CLASSES[i].letter); }
    return spentOf(counts) + 'pts (' + (parts.length ? parts.join(' ') : '—') + ')';
  }
  function updateMatchup() {
    ui.matchup.textContent = 'A ' + teamTag(fleets.A) + '  vs  B ' + teamTag(fleets.B);
  }

  function renderSetup() {
    ['A', 'B'].forEach(function (team) {
      var counts = fleets[team], spent = spentOf(counts), rem = BUDGET - spent;
      for (var i = 0; i < CLASSES.length; i++) {
        var cl = CLASSES[i].cls, n = counts[cl], cost = costOf(cl);
        countEl[team][cl].textContent = n;
        subEl[team][cl].textContent = (n * cost) + ' pts';
        plusEl[team][cl].disabled = (cost > rem);                // + disabled when the class no longer fits
        minusEl[team][cl].disabled = (n <= 0);
      }
      var bud = ui.budget[team];
      bud.className = 'budget' + (spent === BUDGET ? ' full' : (spent === 0 ? ' empty' : ''));
      bud.innerHTML = '<span class="spent">SPENT ' + spent + ' / ' + BUDGET + '</span>' +
        '<span class="rem">' + rem + ' left · ' + shipsOf(counts) + ' ships</span>';
    });
    // START validity: only 0 ships is invalid; any total up to 42 is legal
    var na = shipsOf(fleets.A), nb = shipsOf(fleets.B), reason = '';
    if (na === 0 && nb === 0) reason = 'Both teams need at least one ship.';
    else if (na === 0) reason = 'Team A has no ships.';
    else if (nb === 0) reason = 'Team B has no ships.';
    ui.startBtn.disabled = !!reason;
    ui.startReason.textContent = reason;
    updateMatchup();
  }

  /* ---- mode kept in sync between the bar and the overlay ---- */
  function setMode(m) {
    if (m !== 'play' && m !== 'spectate') m = 'play';
    ui.mode.value = m; ui.setMode.value = m;
    updateModeInfo();
  }
  function updateModeInfo() {
    ui.modeinfo.textContent = 'newtonian | ' + (isSpectate()
      ? 'spectate (fog off, AI vs AI)'
      : 'play: you command TEAM A (blue)');
  }
  function setPaused(v) { paused = v; ui.pause.textContent = paused ? 'resume' : 'pause'; }

  /* ---- overlay show / hide ---- */
  function showSetup() {
    overlayVisible = true;
    ui.setDensity.value = String(currentDensity());   // keep the slider readout coherent
    ui.densityVal.textContent = (parseFloat(ui.setDensity.value) || 0).toFixed(2);
    ui.setGrav.value = ui.grav.value;                 // bar slider is the live source of truth
    ui.setGravVal.textContent = '×' + currentGravity().toFixed(2);
    ui.setMode.value = ui.mode.value;
    ui.setup.classList.remove('hidden');
    renderSetup();
  }
  function hideSetup() { overlayVisible = false; ui.setup.classList.add('hidden'); }
  function currentDensity() { return clampNum(parseFloat(ui.setDensity.value), 0, 1, 0.5); }
  function currentGravity() { return clampNum(parseFloat(ui.setGrav.value), 0, 3, 1); }
  function clampNum(v, lo, hi, dflt) { if (isNaN(v)) return dflt; return v < lo ? lo : v > hi ? hi : v; }

  /* ---- build / (re)create the match ---- */
  // Arena aspect follows the WINDOW so the field fills the full screen width (matching
  // aspect + fit-min view = edge-to-edge). Height keeps the tuned 5600; width scales with
  // the viewport (clamped for pathological windows). Captured once per battle into
  // currentSetup so "restart" reproduces the exact same arena regardless of later resizes.
  function arenaForViewport() {
    var w = cv.clientWidth || 1, h = cv.clientHeight || 1;
    var aspect = clampNum(w / h, 1.0, 2.6, 10 / 7);
    return { w: Math.round(CFG.arena.h * aspect / 20) * 20, h: CFG.arena.h };
  }
  function createFromSetup(s) {
    match = Praedra.createMatch({
      seed: s.seed,
      overrides: { terrainDensity: s.density, destructibleAsteroids: s.destr,
                   arena: s.arena || arenaForViewport(),
                   gravity: { G: CFG.gravity.G * s.grav } },
      teamA: fleetArray(s.A), teamB: fleetArray(s.B),
    });
    ui.grav.value = String(s.grav);                       // live slider mirrors the match
    ui.gravVal.textContent = '×' + s.grav.toFixed(2);
    effects = []; markers = []; selectedSet.clear();
    renderedEnemyIds = []; hover = null; drag = null; down = null;
    banner.style.display = 'none';
    setPaused(false);
    updateModeInfo();
    updateMatchup();
  }
  function startBattle() {
    if (shipsOf(fleets.A) === 0 || shipsOf(fleets.B) === 0) return false;
    currentSetup = {
      A: copyCounts(fleets.A), B: copyCounts(fleets.B),
      seed: parseInt(ui.setSeed.value, 10) || 1,
      density: currentDensity(),
      grav: currentGravity(),
      destr: ui.setDestr.checked,
      arena: arenaForViewport(),
    };
    createFromSetup(currentSetup);
    hideSetup();
    return true;
  }
  function restartBattle() {                                     // same fleets / seed / density, no overlay
    if (!currentSetup) { showSetup(); return; }
    createFromSetup(currentSetup);
  }

  buildSetupDOM();
  ui.startBtn.onclick = startBattle;
  ui.newbattle.onclick = showSetup;
  ui.restart.onclick = restartBattle;
  ui.pause.onclick = function () { setPaused(!paused); };
  ui.mode.onchange = function () { setMode(ui.mode.value); };
  ui.setMode.onchange = function () { setMode(ui.setMode.value); };
  ui.setRandom.onclick = function () { ui.setSeed.value = 1 + Math.floor(Math.random() * 999999); };
  ui.setDensity.oninput = function () { ui.densityVal.textContent = currentDensity().toFixed(2); };
  ui.setGrav.oninput = function () { ui.setGravVal.textContent = '×' + currentGravity().toFixed(2); };
  ui.grav.oninput = function () {                          // LIVE gravity tuning mid-match
    var m = clampNum(parseFloat(ui.grav.value), 0, 3, 1);
    ui.gravVal.textContent = '×' + m.toFixed(2);
    if (match) match.config.gravity.G = CFG.gravity.G * m; // the sim reads config every tick
    if (currentSetup) currentSetup.grav = m;               // restart keeps the tuned value
  };
  help.textContent =
    'L-click select | drag box | shift+click toggle | dbl-click all of type\n' +
    'R-click move / attack ship / blast rock | shift+R attack-move | S hold\n' +
    'Space pause (orders work paused) | Esc deselect | mode: play / spectate';

  /* ---- view transform: identical math to render(); used for screen<->world hit-testing ---- */
  function getView() {
    var w = cv.clientWidth, h = cv.clientHeight, cfg = match.config;
    var scale = Math.min(w / cfg.arena.w, h / cfg.arena.h); // exact fit: arena aspect tracks the window, so this fills the full width
    return { w: w, h: h, scale: scale,
             ox: (w - cfg.arena.w * scale) / 2, oy: (h - cfg.arena.h * scale) / 2 };
  }
  function evToCanvas(e) {
    var r = cv.getBoundingClientRect();
    return { x: e.clientX - r.left, y: e.clientY - r.top };
  }

  /* ---- selection bookkeeping ---- */
  function pruneSelection() {
    if (!match) return;
    var byId = match.state.shipById, dead = [];
    selectedSet.forEach(function (id) {
      var s = byId[id];
      if (!s || !s.alive || s.team !== 'A') dead.push(id);
    });
    for (var i = 0; i < dead.length; i++) selectedSet.delete(dead[i]);
  }
  function aliveSelectedIds() {
    var out = [];
    selectedSet.forEach(function (id) {
      var s = match.state.shipById[id];
      if (s && s.alive && s.team === 'A') out.push(id);
    });
    return out;
  }
  function selectionSummary() {
    var ids = aliveSelectedIds();
    if (!ids.length) return 'none';
    var counts = {};
    for (var i = 0; i < ids.length; i++) { var c = match.state.shipById[ids[i]].cls; counts[c] = (counts[c] || 0) + 1; }
    var parts = []; for (var k in counts) parts.push(counts[k] + ' ' + k);
    return ids.length + '  (' + parts.join(', ') + ')';
  }
  function enemyVisible(sh) {                     // fog-of-war predicate for team-B ships
    if (isSpectate()) return true;
    var det = match.state.detA;
    for (var i = 0; i < det.length; i++) if (det[i].id === sh.id) return true;
    return false;
  }

  /* ---- screen-space pickers (pixel radii: ships radius+6 min 10; asteroids exact) ---- */
  function pickFriendly(sx, sy, v) {
    var ships = match.state.ships, best = null, bd = Infinity;
    for (var i = 0; i < ships.length; i++) {
      var s = ships[i];
      if (!s.alive || s.team !== 'A') continue;
      var dx = sx - (v.ox + s.x * v.scale), dy = sy - (v.oy + s.y * v.scale);
      var d = Math.sqrt(dx * dx + dy * dy), pr = Math.max(10, s.def.radius * v.scale + 6);
      if (d <= pr && d < bd) { bd = d; best = s; }
    }
    return best;
  }
  function pickEnemy(sx, sy, v) {
    var ships = match.state.ships, best = null, bd = Infinity;
    for (var i = 0; i < ships.length; i++) {
      var s = ships[i];
      if (!s.alive || s.team !== 'B' || !enemyVisible(s)) continue;
      var dx = sx - (v.ox + s.x * v.scale), dy = sy - (v.oy + s.y * v.scale);
      var d = Math.sqrt(dx * dx + dy * dy), pr = Math.max(10, s.def.radius * v.scale + 6);
      if (d <= pr && d < bd) { bd = d; best = s; }
    }
    return best;
  }
  function pickRock(sx, sy, v) {
    var A = match.state.asteroids, best = null, bd = Infinity;
    for (var i = 0; i < A.length; i++) {
      var o = A[i];
      if (!o.alive) continue;
      var dx = sx - (v.ox + o.x * v.scale), dy = sy - (v.oy + o.y * v.scale);
      var d = Math.sqrt(dx * dx + dy * dy), pr = o.r * v.scale;   // exact radius
      if (d <= pr && d < bd) { bd = d; best = o; }
    }
    return best;
  }

  /* ---- order feedback markers ---- */
  function selectionCentroid() {
    var ids = aliveSelectedIds(), cx = 0, cy = 0;
    if (!ids.length) return null;
    for (var i = 0; i < ids.length; i++) { var s = match.state.shipById[ids[i]]; cx += s.x; cy += s.y; }
    return { x: cx / ids.length, y: cy / ids.length };
  }
  function addMarker(kind, x, y, targetId) {
    var c = selectionCentroid();
    markers.push({ kind: kind, x: x, y: y, targetId: (targetId == null ? null : targetId),
                   cx: c ? c.x : x, cy: c ? c.y : y, ttl: 1.5, max: 1.5 });
  }

  function issueSmartOrder(canvas, shift) {
    var ids = aliveSelectedIds();
    if (!ids.length) return;
    var v = getView();
    var wx = (canvas.x - v.ox) / v.scale, wy = (canvas.y - v.oy) / v.scale;
    if (shift) {
      Praedra.issueOrder(match, ids, { type: 'attackmove', x: wx, y: wy });
      addMarker('attackmove', wx, wy, null);
      return;
    }
    var en = pickEnemy(canvas.x, canvas.y, v);
    if (en) { Praedra.issueOrder(match, ids, { type: 'attack', targetId: en.id }); addMarker('attack', en.x, en.y, en.id); return; }
    var rk = pickRock(canvas.x, canvas.y, v);
    if (rk) { Praedra.issueOrder(match, ids, { type: 'attackrock', targetId: rk.id }); addMarker('attackrock', rk.x, rk.y, null); return; }
    Praedra.issueOrder(match, ids, { type: 'move', x: wx, y: wy }); addMarker('move', wx, wy, null);
  }

  /* ---- selection gestures ---- */
  function clickSelect(sx, sy, shift) {
    var f = pickFriendly(sx, sy, getView());
    if (f) {
      if (shift) { if (selectedSet.has(f.id)) selectedSet.delete(f.id); else selectedSet.add(f.id); }
      else { selectedSet.clear(); selectedSet.add(f.id); }
    } else if (!shift) {
      selectedSet.clear();
    }
  }
  function boxSelect(r, shift) {
    var v = getView();
    var xmin = Math.min(r.x0, r.x1), xmax = Math.max(r.x0, r.x1);
    var ymin = Math.min(r.y0, r.y1), ymax = Math.max(r.y0, r.y1);
    if (!shift) selectedSet.clear();
    var ships = match.state.ships;
    for (var i = 0; i < ships.length; i++) {
      var s = ships[i];
      if (!s.alive || s.team !== 'A') continue;
      var scx = v.ox + s.x * v.scale, scy = v.oy + s.y * v.scale;
      if (scx >= xmin && scx <= xmax && scy >= ymin && scy <= ymax) selectedSet.add(s.id);
    }
  }
  function updateHover(sx, sy) {
    var v = getView();
    var f = pickFriendly(sx, sy, v);
    if (f) { hover = { kind: 'friendly', s: f }; cv.style.cursor = 'pointer'; return; }
    if (aliveSelectedIds().length) {
      var en = pickEnemy(sx, sy, v);
      if (en) { hover = { kind: 'enemy', s: en }; cv.style.cursor = 'cell'; return; }
      var rk = pickRock(sx, sy, v);
      if (rk) { hover = { kind: 'rock', o: rk }; cv.style.cursor = 'cell'; return; }
    }
    hover = null; cv.style.cursor = 'crosshair';
  }

  /* ---- input listeners ---- */
  cv.addEventListener('mousedown', function (e) {
    if (e.button !== 0 || !match) return;
    var c = evToCanvas(e);
    down = { x: c.x, y: c.y, shift: e.shiftKey, moved: false };
    drag = null;
    e.preventDefault();
  });
  window.addEventListener('mousemove', function (e) {
    if (!match) return;
    var c = evToCanvas(e);
    if (down) {
      var dx = c.x - down.x, dy = c.y - down.y;
      if (down.moved || dx * dx + dy * dy > 25) { down.moved = true; drag = { x0: down.x, y0: down.y, x1: c.x, y1: c.y }; }
    } else {
      updateHover(c.x, c.y);
    }
  });
  window.addEventListener('mouseup', function (e) {
    if (e.button !== 0 || !down || !match) return;
    var c = evToCanvas(e);
    if (down.moved && drag) boxSelect(drag, down.shift);
    else clickSelect(c.x, c.y, down.shift);
    down = null; drag = null;
  });
  cv.addEventListener('contextmenu', function (e) {
    e.preventDefault();
    if (match) issueSmartOrder(evToCanvas(e), e.shiftKey);
  });
  cv.addEventListener('dblclick', function (e) {
    if (!match) return;
    var c = evToCanvas(e);
    var f = pickFriendly(c.x, c.y, getView());
    if (!f) return;
    selectedSet.clear();
    var ships = match.state.ships;
    for (var i = 0; i < ships.length; i++) {
      var s = ships[i];
      if (s.alive && s.team === 'A' && s.cls === f.cls) selectedSet.add(s.id);
    }
  });
  window.addEventListener('keydown', function (e) {
    if (overlayVisible || !match) return;                        // setup screen owns the keyboard
    var tag = (document.activeElement && document.activeElement.tagName) || '';
    if (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA') return;
    if (e.code === 'Space') {
      e.preventDefault(); setPaused(!paused);
    } else if (e.key === 'Escape') {
      selectedSet.clear();
    } else if (e.key === 's' || e.key === 'S') {
      var ids = aliveSelectedIds();
      if (!ids.length) return;
      Praedra.issueOrder(match, ids, { type: 'hold' });
      for (var i = 0; i < ids.length; i++) {
        var s = match.state.shipById[ids[i]];
        markers.push({ kind: 'hold', x: s.x, y: s.y, targetId: null, cx: s.x, cy: s.y, ttl: 1.2, max: 1.2 });
      }
    }
  });

  /* ---- lumpy-polygon drawing for rocks & tumbling debris ---- */
  function drawAsteroid(o, scale, massive) {
    var n = o.shape.length;
    g.beginPath();
    for (var i = 0; i < n; i++) {
      var a = o.rot + i * 6.2831853 / n, rr = o.r * o.shape[i];
      var px = o.x + Math.cos(a) * rr, py = o.y + Math.sin(a) * rr;
      if (i === 0) g.moveTo(px, py); else g.lineTo(px, py);
    }
    g.closePath();
    g.fillStyle = massive ? '#4a4344' : (o.moving ? '#585048' : '#3c4148');
    g.strokeStyle = massive ? '#75655a' : '#565e68';
    g.lineWidth = 1 / scale;
    g.fill(); g.stroke();
    if (massive) {                            // cratered core: the monster reads at a glance
      g.strokeStyle = 'rgba(18,14,12,0.55)';
      g.lineWidth = 2 / scale;
      g.beginPath(); g.arc(o.x, o.y, o.r * 0.52, 0, 6.2832); g.stroke();
      g.beginPath(); g.arc(o.x - o.r * 0.3, o.y + o.r * 0.22, o.r * 0.16, 0, 6.2832); g.stroke();
      g.beginPath(); g.arc(o.x + o.r * 0.34, o.y - o.r * 0.18, o.r * 0.11, 0, 6.2832); g.stroke();
    }
  }
  /* Local gravity field for visual effects — mirrors the sim's bounded-well model. */
  function fieldAt(st, GRV, x, y) {
    var gx = 0, gy = 0;
    if (!GRV || GRV.G <= 0) return { x: 0, y: 0 };
    var A = st.asteroids;
    for (var i = 0; i < A.length; i++) {
      var o = A[i];
      if (!o.alive || o.r < GRV.sourceMinRadius) continue;
      var dx = o.x - x, dy = o.y - y;
      var d2 = dx * dx + dy * dy;
      if (d2 < 1e-6) continue;
      var reach = o.r * GRV.wellReach;
      if (d2 > reach * reach) continue;
      var soft = GRV.softening * o.r;
      var acc = Math.min(GRV.maxAccel, GRV.G * o.r * o.r * o.r / (d2 + soft * soft));
      var d = Math.sqrt(d2), fade0 = reach * 0.85;
      if (d > fade0) acc *= (reach - d) / (reach - fade0);
      gx += (dx / d) * acc; gy += (dy / d) * acc;
    }
    return { x: gx, y: gy };
  }
  function drawGhost(x, y, scale) {                // faded last-seen marker for lost contacts
    g.strokeStyle = 'rgba(255,122,92,0.4)';
    g.lineWidth = 1.2 / scale;
    g.beginPath(); g.arc(x, y, 9, 0, 6.2832); g.stroke();
    g.beginPath();
    g.moveTo(x - 4, y); g.lineTo(x + 4, y);
    g.moveTo(x, y - 4); g.lineTo(x, y + 4);
    g.stroke();
  }
  function drawReticle(x, y, r) {                  // attack-hover targeting reticle
    g.beginPath(); g.arc(x, y, r, 0, 6.2832); g.stroke();
    g.beginPath();
    g.moveTo(x - r * 1.5, y); g.lineTo(x - r * 0.7, y);
    g.moveTo(x + r * 0.7, y); g.lineTo(x + r * 1.5, y);
    g.moveTo(x, y - r * 1.5); g.lineTo(x, y - r * 0.7);
    g.moveTo(x, y + r * 0.7); g.lineTo(x, y + r * 1.5);
    g.stroke();
  }

  /* ---- tiny debug/automation handle ---- */
  window.__praedra = {
    match: function () { return match; },
    select: function (ids) {
      selectedSet.clear();
      if (ids) for (var i = 0; i < ids.length; i++) {
        var s = match.state.shipById[ids[i]];
        if (s && s.alive && s.team === 'A') selectedSet.add(s.id);
      }
      return aliveSelectedIds();
    },
    selected: function () { return aliveSelectedIds(); },
    renderedEnemies: function () { return renderedEnemyIds.slice(); },
    view: function () { return getView(); },
    worldToScreen: function (x, y) { var v = getView(); return { x: v.ox + x * v.scale, y: v.oy + y * v.scale }; },
    setMode: function (m) { setMode(m); },
    setPaused: function (v) { setPaused(!!v); },
    setGravity: function (mult) {                   // live-tune hook (same path as the slider)
      ui.grav.value = String(mult);
      ui.grav.oninput();
      return match ? match.config.gravity.G : null;
    },
    gravityG: function () { return match ? match.config.gravity.G : null; },
    isPaused: function () { return paused; },
    // --- setup-screen automation ---
    setupOpen: function () { return overlayVisible; },
    counts: function (team) { return copyCounts(fleets[team]); },
    setFleet: function (team, spec) {
      spec = spec || {};
      // build target (start from current, override provided keys) then clamp the WHOLE
      // fleet heavy-first so a valid full spec lands exactly and over-budget stays impossible
      var target = copyCounts(fleets[team]);
      for (var i = 0; i < CLASSES.length; i++) { var cl = CLASSES[i].cls; if (spec[cl] != null) target[cl] = Math.max(0, Math.floor(spec[cl])); }
      var rem = BUDGET, out = emptyCounts();
      for (var j = 0; j < CLASSES.length; j++) {
        var c2 = CLASSES[j].cls, cost = costOf(c2);
        var n = Math.min(target[c2], Math.floor(rem / cost));
        out[c2] = n; rem -= n * cost;
      }
      fleets[team] = out;
      renderSetup();
      return copyCounts(fleets[team]);
    },
    preset: function (team, name) { applyPreset(team, name); return copyCounts(fleets[team]); },
    start: function () { return startBattle(); },
    openSetup: function () { showSetup(); },
  };

  function consumeEvents() {
    var evs = match.state.events;
    for (var i = 0; i < evs.length; i++) {
      var e = evs[i], ttl = 0.4;
      if (e.kind === 'launch' || e.kind === 'order') continue;   // launch: silent; order: app draws its own markers
      if (e.kind === 'pd') ttl = 0.12;
      else if (e.kind === 'gat') ttl = 0.09;
      else if (e.kind === 'hrail') ttl = 0.45;       // heavy-rail impact sparks (x==x2 point records)
      else if (e.kind === 'hrailMuzzle') ttl = 0.16; // turret muzzle flash, brief
      else if (e.kind === 'boom') ttl = 0.5;
      else if (e.kind === 'shatter') ttl = 0.6;
      else if (e.kind === 'shipboom') ttl = 0.8;
      var fx = { e: e, ttl: ttl, max: ttl };
      if (e.kind === 'shatter' || e.kind === 'shipboom') {       // flying specks (app-side RNG is fine)
        var nsp = e.kind === 'shatter' ? 6 : 8, sp = [];
        for (var k = 0; k < nsp; k++) sp.push({ a: Math.random() * 6.2832, v: 0.6 + Math.random() * 0.9, l: 0.25 + Math.random() * 0.5 });
        fx.specks = sp;
      }
      effects.push(fx);
    }
    evs.length = 0;
  }

  function fleetValue(team) {
    var v = 0, st = match.state;
    for (var i = 0; i < st.ships.length; i++) {
      var s = st.ships[i];
      if (s.alive && s.team === team) v += s.def.cost * (s.hp / s.maxHp);
    }
    return v;
  }

  function drawShip(s, scale, selected) {
    var r = s.def.radius;
    // selection ring (under the hull, in world space)
    if (selected) {
      g.strokeStyle = SEL_COLOR;
      g.lineWidth = 2 / scale;
      g.beginPath(); g.arc(s.x, s.y, r + 6, 0, 6.2832); g.stroke();
    }
    // hold indicator: small hollow square — station-keeping, weapons free
    if (s.order && s.order.type === 'hold') {
      var hs = r + 5;
      g.strokeStyle = 'rgba(200,220,255,0.65)';
      g.lineWidth = 1 / scale;
      g.strokeRect(s.x - hs, s.y - hs, hs * 2, hs * 2);
    }
    g.save();
    g.translate(s.x, s.y);
    // velocity vector so momentum reads
    g.strokeStyle = 'rgba(255,255,255,0.25)';
    g.lineWidth = 1 / scale;
    g.beginPath(); g.moveTo(0, 0); g.lineTo(s.vx * 1.2, s.vy * 1.2); g.stroke();
    g.rotate(s.heading);
    drawHull(g, s, scale);
    g.restore();
    // hp bar
    var frac = s.hp / s.maxHp;
    if (frac < 0.999) {
      var bw = Math.max(28, r);
      g.fillStyle = '#222';
      g.fillRect(s.x - bw / 2, s.y - s.def.radius - 10, bw, 3);
      g.fillStyle = frac > 0.5 ? '#6c6' : (frac > 0.25 ? '#cc5' : '#c55');
      g.fillRect(s.x - bw / 2, s.y - s.def.radius - 10, bw * frac, 3);
    }
  }

  function render() {
    var dpr = window.devicePixelRatio || 1;
    var w = cv.clientWidth, h = cv.clientHeight;
    if (cv.width !== w * dpr || cv.height !== h * dpr) { cv.width = w * dpr; cv.height = h * dpr; }
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.fillStyle = '#06080d';
    g.fillRect(0, 0, w, h);
    if (!match) return;
    var st = match.state, cfg = match.config;
    var scale = Math.min(w / cfg.arena.w, h / cfg.arena.h); // exact fit: arena aspect tracks the window, so this fills the full width
    var ox = (w - cfg.arena.w * scale) / 2, oy = (h - cfg.arena.h * scale) / 2;
    g.translate(ox, oy); g.scale(scale, scale);

    g.strokeStyle = '#1c2636';
    g.lineWidth = 2 / scale;
    g.strokeRect(0, 0, cfg.arena.w, cfg.arena.h);

    // gravity wells first (under everything): faint rings where the pull still bites
    var GRV = cfg.gravity;
    var massiveR = GRV && GRV.G > 0 ? GRV.sourceMinRadius : Infinity;
    if (GRV && GRV.G > 0) {
      for (var gv = 0; gv < st.asteroids.length; gv++) {
        var go = st.asteroids[gv];
        if (!go.alive || go.r < massiveR) continue;
        var gm = go.r * go.r * go.r;
        var gsoft2 = (GRV.softening * go.r) * (GRV.softening * go.r);
        var reach = go.r * (GRV.wellReach || 1e9); // wells are bounded — rings must be too
        var thrs = [4, GRV.rockWake || 1.2];       // px/s^2 contours: strong pull, and the
                                                   // wake threshold — the ring inside which
                                                   // settled rocks actually start to creep
        var prevRing = 0;
        for (var gt = 0; gt < thrs.length; gt++) {
          var rr2 = GRV.G * gm / thrs[gt] - gsoft2;
          if (rr2 <= go.r * go.r) continue;
          var ringR = Math.min(Math.sqrt(rr2), reach);
          if (ringR <= prevRing * 1.04) continue;  // both contours clamped to the reach: draw once
          prevRing = ringR;
          g.strokeStyle = gt === 0 ? 'rgba(150,175,255,0.11)' : 'rgba(150,175,255,0.055)';
          g.lineWidth = 1 / scale;
          g.beginPath(); g.arc(go.x, go.y, ringR, 0, 6.2832); g.stroke();
        }
      }
    }
    // asteroids + tumbling debris (lumpy polygons)
    for (var i = 0; i < st.asteroids.length; i++) {
      var o = st.asteroids[i];
      if (o.alive) drawAsteroid(o, scale, o.r >= massiveR);
    }
    // torpedoes / bombs
    for (var t = 0; t < st.torps.length; t++) {
      var tp = st.torps[t];
      if (!tp.alive) continue;
      g.strokeStyle = COLORS[tp.team];
      g.lineWidth = 2 / scale;
      g.beginPath(); g.moveTo(tp.x - tp.vx * 0.05, tp.y - tp.vy * 0.05); g.lineTo(tp.x, tp.y); g.stroke();
    }
    g.fillStyle = '#ffd75c';
    for (var b = 0; b < st.bombs.length; b++) {
      var bm = st.bombs[b];
      if (!bm.alive) continue;
      g.beginPath(); g.arc(bm.x, bm.y, 3, 0, 6.2832); g.fill();
    }
    // heavy-rail slugs: elongated kinetic tracers along velocity — clearly a solid round, not a beam
    var slugs = st.slugs || [];
    for (var sg0 = 0; sg0 < slugs.length; sg0++) {
      var sg = slugs[sg0];
      if (!sg.alive) continue;
      var stx = sg.vx * 0.011, sty = sg.vy * 0.011; // ~26px streak at slugSpeed 2400
      g.strokeStyle = 'rgba(255,150,70,0.55)';      // warm orange tail
      g.lineWidth = 3 / scale;
      g.beginPath(); g.moveTo(sg.x - stx, sg.y - sty); g.lineTo(sg.x, sg.y); g.stroke();
      g.strokeStyle = 'rgba(255,244,220,0.95)';     // white-hot core
      g.lineWidth = 1.6 / scale;
      g.beginPath(); g.moveTo(sg.x - stx * 0.45, sg.y - sty * 0.45); g.lineTo(sg.x, sg.y); g.stroke();
    }
    // effects (firing lines fade; blocked shots visibly end at the rock)
    for (var e2 = effects.length - 1; e2 >= 0; e2--) {
      var fx = effects[e2], ev = fx.e, a = fx.ttl / fx.max;
      if (ev.kind === 'rail') {
        // hitscan mechanically — but a well bends even a slug a little. Sagitta is
        // real physics (g_perp * tFlight^2 / 8 at slugSpeed), boosted for legibility:
        // deflection goes as 1/v^2, so the rail barely bows where a torpedo curls.
        var rdx = ev.x2 - ev.x, rdy = ev.y2 - ev.y;
        var rl = Math.sqrt(rdx * rdx + rdy * rdy) || 1;
        var perpx = -rdy / rl, perpy = rdx / rl;
        var fld = fieldAt(st, GRV, (ev.x + ev.x2) / 2, (ev.y + ev.y2) / 2);
        var tFly = rl / cfg.railgun.slugSpeed;
        var sag = (fld.x * perpx + fld.y * perpy) * tFly * tFly / 8 * (GRV.slugBendVisual || 0);
        if (sag > 30) sag = 30; else if (sag < -30) sag = -30;
        g.strokeStyle = 'rgba(240,248,255,' + (0.85 * a) + ')';
        g.lineWidth = (ev.hit === 'ship' ? 2.5 : 1.5) / scale;
        g.beginPath(); g.moveTo(ev.x, ev.y);
        if (sag > 0.5 || sag < -0.5) // control point at 2x sagitta bows the curve by sag
          g.quadraticCurveTo((ev.x + ev.x2) / 2 + perpx * sag * 2, (ev.y + ev.y2) / 2 + perpy * sag * 2, ev.x2, ev.y2);
        else g.lineTo(ev.x2, ev.y2);
        g.stroke();
      } else if (ev.kind === 'hrail') {
        // heavy-rail slug IMPACT (x==x2 point records now — the flying slug itself is drawn from
        // st.slugs): ship = hot flash + expanding ring, rock = dusty spark, miss = faint fizzle
        if (ev.hit === 'ship') {
          g.strokeStyle = 'rgba(255,240,200,' + a + ')';
          g.lineWidth = 2 / scale;
          g.beginPath(); g.arc(ev.x, ev.y, 4 + 14 * (1 - a), 0, 6.2832); g.stroke();
          g.fillStyle = 'rgba(255,255,255,' + (0.85 * a) + ')';
          g.beginPath(); g.arc(ev.x, ev.y, 3.5 * a + 1, 0, 6.2832); g.fill();
        } else if (ev.hit === 'rock') {
          g.fillStyle = 'rgba(205,190,164,' + (0.8 * a) + ')';
          g.beginPath(); g.arc(ev.x, ev.y, 3 + 6 * (1 - a), 0, 6.2832); g.fill();
        } else { // miss: the spent round fizzles out downrange
          g.fillStyle = 'rgba(255,240,200,' + (0.3 * a) + ')';
          g.beginPath(); g.arc(ev.x, ev.y, 2.5, 0, 6.2832); g.fill();
        }
      } else if (ev.kind === 'hrailMuzzle') {
        // turret muzzle flash, oriented along the barrel (ev.ang)
        var mca = Math.cos(ev.ang), msa = Math.sin(ev.ang);
        g.strokeStyle = 'rgba(255,246,214,' + (0.9 * a) + ')';
        g.lineWidth = 3 / scale;
        g.beginPath(); g.moveTo(ev.x, ev.y); g.lineTo(ev.x + mca * 26 * a, ev.y + msa * 26 * a); g.stroke();
        g.fillStyle = 'rgba(255,214,140,' + (0.7 * a) + ')';
        g.beginPath(); g.arc(ev.x, ev.y, 6 * a + 1.5, 0, 6.2832); g.fill();
        for (var mk = -1; mk <= 1; mk += 2) { // radial specks off the rails
          g.strokeStyle = 'rgba(255,214,140,' + (0.5 * a) + ')';
          g.lineWidth = 1.2 / scale;
          g.beginPath(); g.moveTo(ev.x, ev.y);
          g.lineTo(ev.x + (mca - msa * mk * 0.45) * 14 * a, ev.y + (msa + mca * mk * 0.45) * 14 * a);
          g.stroke();
        }
      } else if (ev.kind === 'pd') {
        g.strokeStyle = 'rgba(120,235,255,' + (0.7 * a) + ')';
        g.lineWidth = 1 / scale;
        g.beginPath(); g.moveTo(ev.x, ev.y); g.lineTo(ev.x2, ev.y2); g.stroke();
      } else if (ev.kind === 'gat') {
        g.strokeStyle = 'rgba(255,225,120,' + (0.55 * a) + ')';
        g.lineWidth = 1 / scale;
        g.beginPath(); g.moveTo(ev.x, ev.y); g.lineTo(ev.x2, ev.y2); g.stroke();
      } else if (ev.kind === 'shatter') {
        var prog = 1 - a, rr = ev.r * (0.5 + 1.5 * prog);
        g.strokeStyle = 'rgba(152,142,124,' + (0.7 * a) + ')';
        g.lineWidth = 2 / scale;
        g.beginPath(); g.arc(ev.x, ev.y, rr, 0, 6.2832); g.stroke();
        if (fx.specks) {
          g.strokeStyle = 'rgba(122,112,98,' + (0.85 * a) + ')';
          g.lineWidth = 1.4 / scale;
          for (var sk = 0; sk < fx.specks.length; sk++) {
            var sp = fx.specks[sk], d0 = ev.r * (0.4 + prog * sp.v * 1.4), d1 = d0 + ev.r * sp.l;
            g.beginPath();
            g.moveTo(ev.x + Math.cos(sp.a) * d0, ev.y + Math.sin(sp.a) * d0);
            g.lineTo(ev.x + Math.cos(sp.a) * d1, ev.y + Math.sin(sp.a) * d1);
            g.stroke();
          }
        }
      } else if (ev.kind === 'boom' || ev.kind === 'shipboom') {
        g.strokeStyle = 'rgba(255,160,80,' + a + ')';
        g.lineWidth = 2 / scale;
        g.beginPath(); g.arc(ev.x, ev.y, ev.r * (1.6 - 0.6 * a), 0, 6.2832); g.stroke();
        if (fx.specks && ev.kind === 'shipboom') {
          var pg = 1 - a;
          g.strokeStyle = 'rgba(255,190,120,' + (0.8 * a) + ')';
          g.lineWidth = 1.5 / scale;
          for (var q = 0; q < fx.specks.length; q++) {
            var s3 = fx.specks[q], e0 = ev.r * (0.3 + pg * s3.v * 1.4), e1 = e0 + ev.r * s3.l;
            g.beginPath();
            g.moveTo(ev.x + Math.cos(s3.a) * e0, ev.y + Math.sin(s3.a) * e0);
            g.lineTo(ev.x + Math.cos(s3.a) * e1, ev.y + Math.sin(s3.a) * e1);
            g.stroke();
          }
        }
      }
    }
    // order markers (fade ~1.5s): green move / red-ish attack-move + attack flash
    for (var mi = markers.length - 1; mi >= 0; mi--) {
      var mk = markers[mi], ma = mk.ttl / mk.max;
      var col = mk.kind === 'move' ? '124,255,150'
        : (mk.kind === 'hold' ? '200,220,255' : '255,110,90');
      var tx = mk.x, ty = mk.y;
      if (mk.targetId != null) { var ts = st.shipById[mk.targetId]; if (ts && ts.alive) { tx = ts.x; ty = ts.y; } }
      if (mk.kind === 'move' || mk.kind === 'attackmove') {
        g.strokeStyle = 'rgba(' + col + ',' + (0.5 * ma) + ')';
        g.lineWidth = 1.2 / scale;
        g.beginPath(); g.moveTo(mk.cx, mk.cy); g.lineTo(mk.x, mk.y); g.stroke();
        g.fillStyle = 'rgba(' + col + ',' + (0.9 * ma) + ')';
        g.beginPath(); g.arc(mk.x, mk.y, 4, 0, 6.2832); g.fill();
        g.strokeStyle = 'rgba(' + col + ',' + (0.7 * ma) + ')';
        g.lineWidth = 1.5 / scale;
        g.beginPath(); g.arc(mk.x, mk.y, 14 * (1.4 - 0.4 * ma), 0, 6.2832); g.stroke();
      } else {
        g.strokeStyle = 'rgba(' + col + ',' + (0.85 * ma) + ')';
        g.lineWidth = 2 / scale;
        var fr = (mk.kind === 'hold' ? 10 : 18) * (1.5 - 0.5 * ma);
        g.beginPath(); g.arc(tx, ty, fr, 0, 6.2832); g.stroke();
      }
    }
    // ghosts: undetected enemies remembered within memorySeconds (play mode only)
    renderedEnemyIds = [];
    if (!isSpectate()) {
      var mem = cfg.detection.memorySeconds || 5;
      for (var gi = 0; gi < st.ships.length; gi++) {
        var gs = st.ships[gi];
        if (!gs.alive || gs.team !== 'B' || enemyVisible(gs)) continue;
        var ls = st.lastSeenShip[gs.id];
        if (ls && (st.time - ls.t) < mem) drawGhost(ls.x, ls.y, scale);
      }
    }
    // ships: friendlies always; enemies only if visible under the fog
    for (var s2 = 0; s2 < st.ships.length; s2++) {
      var sh = st.ships[s2];
      if (!sh.alive) continue;
      if (sh.team === 'B') {
        if (!enemyVisible(sh)) continue;
        renderedEnemyIds.push(sh.id);
      }
      drawShip(sh, scale, sh.team === 'A' && selectedSet.has(sh.id));
    }
    // attack reticle over hovered enemy/rock when a selection exists
    if (hover && (hover.kind === 'enemy' || hover.kind === 'rock') && aliveSelectedIds().length) {
      var ht = hover.kind === 'enemy' ? hover.s : hover.o;
      if (ht && ht.alive) {
        g.strokeStyle = '#ff5a3c';
        g.lineWidth = 1.5 / scale;
        drawReticle(ht.x, ht.y, (hover.kind === 'enemy' ? ht.def.radius : ht.r) + 8);
      }
    }

    // ---- screen-space overlay ----
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    if (drag) {
      var rx = Math.min(drag.x0, drag.x1), ry = Math.min(drag.y0, drag.y1);
      var rw = Math.abs(drag.x1 - drag.x0), rh = Math.abs(drag.y1 - drag.y0);
      g.fillStyle = 'rgba(124,255,178,0.10)';
      g.fillRect(rx, ry, rw, rh);
      g.strokeStyle = 'rgba(124,255,178,0.85)';
      g.lineWidth = 1;
      g.strokeRect(rx + 0.5, ry + 0.5, rw, rh);
    }

    pruneSelection();
    hud.textContent = 't=' + (st.tick / 60).toFixed(0) + 's / ' + cfg.matchTimerSeconds + 's' +
      '   A ' + fleetValue('A').toFixed(1) + '  vs  B ' + fleetValue('B').toFixed(1) +
      (paused ? '   [PAUSED]' : '') +
      (match.done ? '   [ended: ' + match.result.winner + ' by ' + match.result.reason + ']' : '') +
      '\nselected: ' + selectionSummary();
    if (match.done && banner.style.display === 'none') {
      banner.textContent = match.result.winner === 'draw' ? 'DRAW' :
        'TEAM ' + match.result.winner + ' WINS  (' + match.result.reason + ')';
      banner.style.color = match.result.winner === 'draw' ? '#ccc' : COLORS[match.result.winner];
      banner.style.display = 'block';
    }
  }

  var last = performance.now();
  function frame(now) {
    requestAnimationFrame(frame);
    var dtReal = Math.min(0.1, (now - last) / 1000);
    last = now;
    if (match && !paused && !match.done && !overlayVisible) {   // setup screen freezes the sim
      var steps = parseInt(ui.speed.value, 10) || 1;
      for (var i = 0; i < steps && !match.done; i++) match.step();
    }
    if (match) consumeEvents();   // every frame: clears order events even while paused
    for (var e = effects.length - 1; e >= 0; e--) {
      effects[e].ttl -= dtReal;
      if (effects[e].ttl <= 0) effects.splice(e, 1);
    }
    for (var m = markers.length - 1; m >= 0; m--) {
      markers[m].ttl -= dtReal;
      if (markers[m].ttl <= 0) markers.splice(m, 1);
    }
    render();
  }

  /* ---- first-load defaults: BALANCED vs BALANCED, seed 1, density 0.5, play, overlay open ---- */
  fleets.A = countsFromPreset('BALANCED');
  fleets.B = countsFromPreset('BALANCED');
  ui.setSeed.value = '1';
  ui.setDensity.value = '0.5';
  ui.setGrav.value = '1'; ui.grav.value = '1';
  ui.setDestr.checked = true;
  setMode('play');
  showSetup();                    // no match steps until START
  requestAnimationFrame(frame);
})();
