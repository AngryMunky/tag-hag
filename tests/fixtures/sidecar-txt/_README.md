# sidecar-txt fixtures

The user does not generate sidecar `.txt` files, so `sample_sidecar_synthetic.txt` is **synthetic** test data
in the standard A1111 `parameters` shape (positive / `Negative prompt:` / settings line).

How to test the sidecar fallback (ticket T4):
1. Take an image that has **no embedded metadata** (e.g. a plain screenshot, or strip metadata from a copy).
2. Place it here named so its basename matches a `.txt` (e.g. `myimage.png` + `myimage.txt`).
3. The reader's resolution order is embedded → EXIF → **sidecar**, so only a metadata-free image
   will actually exercise the sidecar branch (otherwise embedded/EXIF wins first).

`sample_sidecar_synthetic.txt` alone is enough to unit-test the A1111 text parser path.
Real sidecar files can replace this whenever available.
