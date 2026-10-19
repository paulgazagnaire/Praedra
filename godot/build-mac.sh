#!/usr/bin/env bash
# Builds the signed Mac zip: Godot export (its own signing is off, see README "Mac signing"),
# then an ad-hoc signature with rcodesign, then a zip that keeps permissions.
# Usage: godot/build-mac.sh [output.zip]     (run from anywhere; needs GODOT pointing at the 4.7.1 mono editor)
# rcodesign (apple-codesign 0.29.0, approved by Paul 2026-10-13) is downloaded once to ~/.cache/rcodesign if RCODESIGN is not set.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
out="$(realpath -m "${1:-$here/../build/mac/Praedra-mac.zip}")"
GODOT="${GODOT:-/tmp/godot/ed/Godot_v4.7.1-stable_mono_linux_x86_64/Godot_v4.7.1-stable_mono_linux.x86_64}"
RCV=0.29.0
if [ -z "${RCODESIGN:-}" ]; then
  RCODESIGN="$HOME/.cache/rcodesign/apple-codesign-$RCV-x86_64-unknown-linux-musl/rcodesign"
  if [ ! -x "$RCODESIGN" ]; then
    mkdir -p "$HOME/.cache/rcodesign"
    curl -fsSL "https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F$RCV/apple-codesign-$RCV-x86_64-unknown-linux-musl.tar.gz" \
      | tar xz -C "$HOME/.cache/rcodesign"
  fi
fi
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$(dirname "$out")"
(cd "$here" && dotnet build -v q RebellionViewer.csproj -p:RestoreAdditionalProjectSources=/tmp/godot/nupkgs >/dev/null \
  && "$GODOT" --headless --path . --import >/dev/null 2>&1 \
  && "$GODOT" --headless --path . --export-release macOS "$work/unsigned.zip" >/dev/null 2>&1)
(cd "$work" && unzip -q unsigned.zip)
"$RCODESIGN" sign --entitlements-xml-file "$here/mac-entitlements.plist" "$work/Praedra.app" >/dev/null
rm -f "$out"
(cd "$work" && zip -qry "$out.tmp" Praedra.app)
mv "$out.tmp" "$out"
echo "signed Mac build: $out"
