# Tag Hag — test fixtures

Drop **real** sample files here to build and verify the metadata parsers (tickets T3–T5).
~3–5 files per folder is plenty to start. Synthetic strings won't catch encoding quirks — use real exports.
Nothing in this folder ships inside the exe.

## Folders
- `a1111-png/` — PNGs from Automatic1111 / SD WebUI (embedded `parameters` text). Include at least one with a long negative prompt + LoRA tags.
- `comfyui-png/` — **MOST IMPORTANT.** PNGs from ComfyUI (embedded `prompt`/`workflow` JSON). Include several *different* graphs: a basic KSampler, one with multiple CLIPTextEncode nodes, one with an upscaler/refiner, and one using custom nodes — so positive/negative disambiguation gets exercised.
- `exif-jpeg-webp/` — JPEG and WebP files carrying A1111 metadata in EXIF UserComment / ImageDescription.
- `sidecar-txt/` — an image PLUS a matching `<basename>.txt` param dump (image itself has no embedded metadata).
- `edge-cases/` — optional: truncated/corrupt files, images with no metadata at all, unusually large ComfyUI graphs.
