function visibleRange(state, ship) {
  var D = state.config.detection;
  return ship.def.signature * lerp(D.thrustMultMin, D.thrustMultMax, ship.throttle || 0);
}
/* Team-shared sensor picture: an enemy is detected if ANY friendly has LOS to it
   within its (thrust-modulated) signature range. Sweeps every few ticks. */
function computeDetection(state) {
  var D = state.config.detection;
  if (state.tick % D.checkEvery !== 1 && state.detReady) return;
  state.detReady = true;
  var teams = [['A', state.aliveA, state.aliveB], ['B', state.aliveB, state.aliveA]];
  for (var t = 0; t < 2; t++) {
    var team = teams[t][0], own = teams[t][1], foes = teams[t][2];
    var det = [];
    for (var e = 0; e < foes.length; e++) {
      var foe = foes[e];
      var vr = visibleRange(state, foe);
      for (var o = 0; o < own.length; o++) {
        var me = own[o];
        if (dist(me.x, me.y, foe.x, foe.y) <= vr && losShips(state, me, foe)) {
          det.push(foe);
          state.lastSeenShip[foe.id] = { x: foe.x, y: foe.y, t: state.time };
          state.lastContact[team] = { x: foe.x, y: foe.y, t: state.time };
          break;
        }
      }
    }
    if (team === 'A') state.detA = det; else state.detB = det;
  }
}
function isDetectedBy(state, team, ship) {
  var det = team === 'A' ? state.detA : state.detB;
  for (var i = 0; i < det.length; i++) if (det[i].id === ship.id) return true;
  return false;
}
/* Where a ship with no live contacts should look: recent memory, else the enemy
   fleet's rough centroid (strategic picture, not targeting — it gates no weapon).
   The old enemy-SPAWN fallback was equally omniscient but pointed at where the
   enemy USED to be; on titan-scale maps that turned endgames into six-minute
   hide-and-seek around the monster rock's LOS shadow and ran out the clock. */
