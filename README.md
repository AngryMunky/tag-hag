<p align="center">
  <img src="Resources/logo-lockup.png" alt="Tag Hag" width="420" />
</p>

<h1 align="center">The Tag Hag</h1>
<p align="center"><em>Tame the AI image hoard.</em></p>

A portable, single-file **Windows desktop app** for managing a large local library of AI‑generated images. Point it at your folders and it scans them, reads the embedded **A1111 / ComfyUI / EXIF / sidecar** generation metadata, indexes everything into a local **SQLite + FTS5** database, and lets you **search, sort, and sift thousands of images by their prompt tags** — then copy / move / archive / delete them into an export tree. Built to stay smooth at **100k+ images** (verified at ~28,600).

> Local‑first, single‑user, portable. No cloud, no account, no telemetry.

## Features

- **Scan** multiple source folders recursively; incremental re‑scans skip unchanged files and prune deleted ones.
- **Read generation metadata** from A1111 `parameters`, ComfyUI prompt graphs, JPEG/WebP EXIF, and `.txt` sidecars.
- **Search** by prompt tags with type‑ahead autocomplete (the "prompt matrix"): comma = AND, `"quotes"` = exact phrase.
- **Gallery** with virtualized infinite scroll and a lazy 512px WebP **thumbnail cache** for fast load at scale.
- **Inspector panel** + lightbox showing positive / negative prompt, checkpoint, LoRAs, sampler / steps / CFG / seed, with copy buttons.
- **Curation**: multi‑select, bulk **Copy / Move / Archive (the Bog) / Delete** (Recycle Bin, recoverable) into a confined export tree.
- **Optimize**: metadata‑preserving downsample (export copies by default, opt‑in in‑place behind a strong confirm).
- **Civitai mode**: browse the live feed (period / sort / NSFW / min‑likes / *Followed only*), react inline, and pick images to import into the library.
- **Dark Magic Pro** look: a 3‑pane shell with a witch‑hat brand, Cinzel + Inter type, and the hag‑tag app icon.

## Build & run

Requires the **.NET 8 SDK** (Windows). The shipped exe is self‑contained — end users need nothing installed.

```sh
# Debug build
dotnet build TheTagHag.csproj -c Debug

# Release: one self-contained, single-file win-x64 exe → publish\TheTagHag.exe
dotnet publish TheTagHag.csproj -c Release -o publish
```

> The running app holds `publish\TheTagHag.exe` — close it before re‑publishing or the build fails on a file lock.

## Tests

The engine has a headless self‑test harness (the GUI is verified in‑app). Each prints `RESULT: PASS/FAIL`:

```sh
TheTagHag.exe --selftest          # SQLite + FTS5 smoke test
TheTagHag.exe --selftest-scan  tests\fixtures
TheTagHag.exe --selftest-ui    tests\fixtures
# …also: -db -png -meta -comfy -fileops -optimize -settings -harvest -react
```

Regenerate the app icon from the brand mark:

```sh
TheTagHag.exe --makeicon Resources\app.ico design\ui-redesign\assets\logo-mark.png
```

## Tech stack

C# / .NET 8 · WinForms + WebView2 (HTML gallery) · SQLite + FTS5 (`Microsoft.Data.Sqlite`, WAL) · SixLabors.ImageSharp · DPAPI‑encrypted API key · single‑file self‑contained win‑x64 publish.

## License

[MIT](LICENSE) © Angry Munky
