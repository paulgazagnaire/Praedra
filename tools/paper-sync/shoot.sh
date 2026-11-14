#!/bin/bash
# shoot.sh OUT_DIR [names...]: the game states of the Paper frames, each as a 2x screenshot (NAME.png), what every control draws
# (NAME.json, --dump-ui) and the map alone (NAME.map.png). Needs xvfb-run and GODOT (the Godot 4.7.1 mono binary); never run two at once.
set -u
G=${GODOT:?set GODOT to the Godot 4.7.1 mono binary}
O=$(realpath -m "$1"); shift; mkdir -p "$O"
cd "$(dirname "$0")/../../godot"
ONLY=("$@")
want() { [ ${#ONLY[@]} -eq 0 ] && return 0; for w in "${ONLY[@]}"; do [ "$w" = "$1" ] && return 0; done; return 1; }
shot() {
  name=$1; shift; want "$name" || return 0
  timeout 240 xvfb-run -a -s "-screen 0 2880x1620x24" "$G" --path . --rendering-driver opengl3 --resolution 2880x1620 -- \
    --screenshot "$O/$name.png" 40 --dump-ui "$O/$name.json" "$@" >"$O/$name.log" 2>&1
  echo "$name $? $(test -f "$O/$name.json" && echo ok || echo MISSING)"
}
H="--newgame --side Hand --seed 1"
shot fleet-command $H --window fleet Charter:Command
shot fleet-ships $H --window fleet Charter:Ships
shot fleet-fighters $H --window fleet Charter:Fighters
shot fleet-troops $H --window fleet Charter:Troops
shot fleet-orders $H --window fleet Charter:Orders
shot ground-leaders $H --window ground Charter
shot ground-troops $H --window troops Charter
shot ground-fighters $H --window fighters Charter
shot ground-fleets $H --window fleets Charter
shot ground-lore $H --window lore Charter
shot ground-unexplored $H --window ground Brushfield
shot ground-harvested $H --strike Brushfield --window ground Brushfield
shot infra-ships $H --window infra Charter
shot infra-facilities $H --window infra Charter --click Facilities
shot infra-busy $H --window infra Charter --click "Build Decree" --days 60
shot sector $H --sector Charter_Sector
# four narrow windows fit side by side at 1440; your own planet's Infrastructure window is two columns wide, so it is not one of them
shot four $H --window ground Charter --window fleet Charter --window ground Brushfield --window infra Brushfield
