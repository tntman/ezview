# ezview

A fast, minimal media viewer for Windows: images, video/audio, image sequences and 3D scenes in one clean, dark window.

Built with C# / .NET 9 and Avalonia, on top of [libmpv](https://mpv.io), [ImageMagick](https://imagemagick.org) (Magick.NET) and [Assimp](https://www.assimp.org), plus its own Alembic and USD readers.

## What it opens

| | |
|---|---|
| **Images** | jpg, png, webp, heic, avif, jxl, tiff, psd, exr, hdr, dds, tga, svg, camera RAW and more (200+ via ImageMagick) |
| **Video / audio** | everything mpv plays: mkv, mp4, HEVC, AV1, ProRes, webm, gif, mp3, flac … with hardware decoding |
| **Image sequences** | `shot.1001.exr`, `plate_0001.dpx` … play as a clip with real frame numbers |
| **3D** | obj, fbx, gltf/glb, stl, ply, dae, blend, 3ds, Quake md2/md3/mdl/pk3, Doom 3 md5 … (Assimp) |
| **3D animation** | Alembic (`.abc`), USD (`.usda` / `.usdc` / `.usdz`, with references, payloads, variants and point instancing), FBX skeletons |

## Features

- Chromeless dark window, frosted top bar and player bar that fade out while you watch
- Player bar with timeline, frame numbers, loop (off / file / queue) and volume; frame stepping with , . and 5 s jumps with ← →
- Queue panel (Q), info panel with full metadata and EXIF (I), right-click menu
- 3D: Houdini-style navigation (LMB tumble, MMB pan, RMB dolly), wireframe (W), grid (G), textures on/off (T), skeleton (B), outliner with visibility and solo (O)
- Can register itself for all supported file types (`ezview --register`)

## Building

```bash
dotnet build
```

Video needs **libmpv**, which isn't in the repo (it's ~120 MB). Download a `mpv-dev-x86_64-*.7z` build from
[shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases) and put `libmpv-2.dll` in a `native/` folder next to `ezview.csproj`. The build copies it to the output automatically.

A release build:

```bash
dotnet publish -c Release -o publish
```

## Command line

```bash
ezview <file>          # open a file (its folder becomes the queue)
ezview --register      # add ezview to "Open with" for every supported type
ezview --unregister    # remove that again
ezview --selftest      # sequence grouping + 3D animation format checks
```

## Keys

| Key | Action |
|---|---|
| ← / → | Previous / next file (images, 3D); one frame back / forward (video, animation) |
| Shift+← / → , J / L, Ctrl+← / → | Jump 5 s, 10 s, 60 s |
| Space / K | Play / pause |
| 0–9 | Jump to 0–90 % (video) |
| [ / ] / Backspace | Slower / faster / normal speed |
| PgUp / PgDn | Previous / next file |
| R | Loop mode |
| Q / I / O | Queue / info / outliner |
| F, double-click | Fullscreen |
| 0 / H | Fit / reset view |

## Third-party

Nunito Sans is included under the SIL Open Font License (`Assets/Fonts/OFL.txt`).
