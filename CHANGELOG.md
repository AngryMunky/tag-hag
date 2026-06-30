# Changelog

All notable changes to **The Tag Hag** are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this
project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Each tagged
release (`vX.Y.Z`) ships a self-contained `TheTagHag.exe` on the
[Releases](https://github.com/AngryMunky/tag-hag/releases) page.

## [Unreleased]
- Docs: README refreshed to the v2.9 feature set; this CHANGELOG added.

## [2.9.0] — 2026-06-30 — "Collections, Realized"
### Added
- **Find similar by tags** — pick an image and surface others that share its prompt tags, ranked by a tunable similarity slider (default 70%).
- **Auto-seeded collections** — the first scan of a source folder builds a matching collection tree and files images into it; re-scans add new images without disturbing your edits.
- **Collection breadcrumb** (`📂 › Animals › Cats`) with an **Include sub-collections** toggle in the gallery.
- **Auto-seed Potions** (Folders menu) — brews a Potion for each of your 25 most-used tags; **Reset Auto-Potions** clears only the auto-generated ones.
### Changed
- **Library Optimization now defaults to organizing by the collection tree** (source-folder layout becomes the opt-out).
- Database schema v5 → v6 (additive only): `folder_key` on collections, `is_auto_seeded` on potions.

## [2.8.4] — 2026-06-30
### Fixed
- Drag-to-collection now fires reliably (rewired to per-row drop handling).
- Library Optimization shows live progress and a working Cancel button.

## [2.8.3] — 2026-06-30
### Changed
- Empty-state cauldron illustration blends into the page (backdrop color-match).

## [2.8.2] — 2026-06-30
### Fixed
- Sidebar scope buttons (All / Favorites / Optimized / Untagged) switch the grid again — a v2.7.2 regression left a dead `#cnt-dupes` reference that threw before the grid could refresh.

## [2.8.1] — 2026-06-29
### Changed
- Cauldron empty-state art moved to the center panel.
- **Favorite** is now a true bulk toggle across a selection.
- Added a clear (✕) button to the search bar.
### Fixed
- Hardened selection-state sync so File-menu actions never desync from the selection.

## [2.8.0] — 2026-06-29 — "Clean Shell"
### Changed
- Retired **The Bog** and **Unsorted** scopes, the **Tags ▾** dropdown, the density buttons, and the version callout.
- Rebuilt the window chrome: WinForms ToolStrip → MenuStrip.

## [2.7.2] — 2026-06-28
### Changed
- Removed the Duplicates entry from the sidebar.

## [2.7.1] — 2026-06-28
### Fixed
- More forgiving WD14 tag-CSV lookup; corrected the Auto-Tagger button label.

## [2.7.0] — 2026-06-28 — "WD14 Smart Tagger"
### Added
- **WD14 auto-tagging** via a bundled, CPU-only ONNX model (no GPU, no setup).

## [2.6.0] — 2026-06-28 — "Tags & Toolbar"
### Added
- Tags dropdown and a WinForms ⋯ (more-actions) menu.

## [2.5.0] — 2026-06-28 — "The Hag, Simplified"
### Changed
- Streamlined the interface and trimmed redundant controls.

## [2.4.0] — 2026-06-28 — "Potions"
### Added
- **Potions** 🧪 — save any search as a named, reusable filter recallable from the sidebar.

## [2.3.0] — 2026-06-27 — "Collections, Curated"
### Added
- **Nested collections** — a curated logical hierarchy.
- Drag images directly into collections.
- **Consolidate by collection tree** — write the collection hierarchy to disk at a chosen location.

## [2.2.0] — 2026-06-27
### Added
- Drag images onto sidebar folders to move them on disk.
- Cancelable 0–100% scan-progress overlay.
- **Ctrl+A** selects the entire current view (not just the loaded page).

## [2.1.1] — 2026-06-26
### Added
- Clickable GitHub link in the About dialog.
### Changed
- Release asset is named `TheTagHag.exe` (no version suffix).

## [2.1.0] — 2026-06-26 — "Library Optimization"
### Added
- **Library Optimization** — resample large images into a managed store and reclaim disk by recycling the originals.
- GitHub Actions release pipeline: pushing a `v*` tag builds and publishes the exe automatically.

## [2.0.0] — 2026-06-25
### Added
- **Favorites**, per-image **Notes**, and **manual tags** that blend into prompt-tag search.
- First-generation **Collections**.
- **Auto-Tag** (suggest-only) from visually-similar neighbours.

## [1.1.0] — 2026-06-23
### Added
- Initial public release: recursive multi-folder **scan**; A1111 / ComfyUI / EXIF / sidecar **metadata** reading; **tag search** with autocomplete; virtualized **gallery** + thumbnail cache; **Inspector** + lightbox; bulk **Copy / Move / Delete**; and **Find Duplicates** (perceptual hash).
- **Dark Magic Pro** visual redesign; project split into a public mirror + a private working repo.

[Unreleased]: https://github.com/AngryMunky/tag-hag/compare/v2.9.0...HEAD
[2.9.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.9.0
[2.8.4]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.8.4
[2.8.3]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.8.3
[2.8.2]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.8.2
[2.8.1]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.8.1
[2.8.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.8.0
[2.7.2]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.7.2
[2.7.1]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.7.1
[2.7.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.7.0
[2.6.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.6.0
[2.5.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.5.0
[2.4.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.4.0
[2.3.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.3.0
[2.2.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.2.0
[2.1.1]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.1.1
[2.1.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.1.0
[2.0.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v2.0.0
[1.1.0]: https://github.com/AngryMunky/tag-hag/releases/tag/v1.1.0
