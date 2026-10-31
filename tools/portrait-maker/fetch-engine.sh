#!/bin/sh
# Fetch the MediaPipe engine (vision_wasm_internal.wasm) next to index.html.
# It is 9.4 MB, so it stays out of git (.gitignore) and is published beside the page.
# Version must match the jsDelivr import in portrait-maker.src.html.
set -eu
VER=0.10.14
URL="https://registry.npmjs.org/@mediapipe/tasks-vision/-/tasks-vision-$VER.tgz"
cd "$(dirname "$0")"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fsSL "$URL" -o "$tmp/pkg.tgz"
tar -xzf "$tmp/pkg.tgz" -C "$tmp" package/wasm/vision_wasm_internal.wasm
mv "$tmp/package/wasm/vision_wasm_internal.wasm" .
ls -l vision_wasm_internal.wasm   # expect 9423986 bytes
