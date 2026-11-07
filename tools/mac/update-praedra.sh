#!/usr/bin/env bash
# Installs the newest downloaded Praedra build and clears macOS's "downloaded from the internet" flag, so it opens with no
# Privacy & Security prompt. Usage (Terminal, after downloading the new build):  bash ~/Downloads/update-praedra.sh
# Takes the newest Praedra-mac*.zip in ~/Downloads (or a Praedra*.app that Safari already unzipped there), installs it as
# ~/Applications/Praedra.app (no password needed), and opens it. Saves are kept: they live outside the app.
set -euo pipefail
dl="$HOME/Downloads"; dest="$HOME/Applications"; app="$dest/Praedra.app"
if pgrep -x Praedra >/dev/null 2>&1; then echo "Praedra is running. Save, quit it, then run this again."; exit 1; fi
src="$(ls -td "$dl"/Praedra-mac*.zip "$dl"/Praedra*.app "$dl"/Praedra-mac*/Praedra*.app 2>/dev/null | head -1 || true)"
if [ -z "$src" ]; then echo "No Praedra-mac.zip or Praedra.app in $dl. Download the build first."; exit 1; fi
echo "Installing from: $src ($(date -r "$src" '+%Y-%m-%d %H:%M'))"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
if [[ "$src" == *.zip ]]; then ditto -x -k "$src" "$tmp"; else ditto "$src" "$tmp/Praedra.app"; fi
if [ ! -d "$tmp/Praedra.app/Contents" ]; then echo "That download has no Praedra.app inside; your installed game was left alone."; exit 1; fi
xattr -dr com.apple.quarantine "$tmp/Praedra.app" 2>/dev/null || true
mkdir -p "$dest"; rm -rf "$app"; mv "$tmp/Praedra.app" "$app"   # the old app goes only once the new one is ready
echo "Installed $app"
open "$app"
