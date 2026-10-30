"""Build index.html from portrait-maker.src.html: inline the sample photo and the person-finder model.

python3 build.py <sample.jpg> <selfie_segmenter.tflite>
The engine file vision_wasm_internal.wasm (from npm @mediapipe/tasks-vision@0.10.14, wasm/) is published next to
index.html, not inlined; see README.md.
"""
import base64, sys
src, sample, model = 'portrait-maker.src.html', sys.argv[1], sys.argv[2]
b64 = lambda p: base64.b64encode(open(p, 'rb').read()).decode()
html = open(src).read().replace('%%SAMPLE%%', b64(sample)).replace('%%MODEL%%', b64(model))
open('index.html', 'w').write(html)
print(len(html), 'bytes')
