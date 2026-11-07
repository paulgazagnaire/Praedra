#!/usr/bin/env bash
# Installs the newest downloaded Praedra build and clears macOS's "downloaded from the internet" flag, so it opens with no
# Privacy & Security prompt. Usage (Terminal, after downloading the new build):  bash ~/Downloads/update-praedra.sh
# Takes the newest Praedra-mac*.zip in ~/Downloads (or a Praedra.app that Safari already unzipped there), installs it as
# ~/Applications/Praedra.app (no password needed), and opens it. Saves are kept: they live outside the app.
set -euo pipefail
dl="$HOME/Downloads"; dest="$HOME/Applications"; app="$dest/Praedra.app"
src="$(ls -td "$dl"/Praedra-mac*.zip "$dl"/Praedra.app "$dl"/Praedra-mac*/Praedra.app 2>/dev/null | head -1 || true)"
if [ -z "$src" ]; then echo "No Praedra-mac.zip or Praedra.app in $dl. Download the build first."; exit 1; fi
echo "Installing from: $src"
pkill -x Praedra 2>/dev/null || true
mkdir -p "$dest"; rm -rf "$app"
if [[ "$src" == *.zip ]]; then
  tmp="$(mktemp -d)"; ditto -x -k "$src" "$tmp"
  mv "$tmp/Praedra.app" "$app"; rm -rf "$tmp"
else
  ditto "$src" "$app"
fi
xattr -dr com.apple.quarantine "$app" 2>/dev/null || true
echo "Installed $app"
open "$app"
