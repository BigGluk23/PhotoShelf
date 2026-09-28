# PhotoShelf HEIC worker

`PhotoShelf.HeifWorker.exe`, `heif.dll` (libheif 1.23.5) and `libde265.dll`
(libde265 1.1.3) form an offline HEIC/HEIF decoder. Windows imaging codecs are
not used. The two LGPL libraries remain replaceable shared DLLs; the MSVC C/C++
runtime is statically linked so no redistributable installation is required.

## Build

From the repository root, in PowerShell 7 on Windows with Visual Studio C++ x64
Build Tools, CMake and Ninja installed:

```powershell
./tools/build-heif-codec.ps1
```

The script finds the installed Visual Studio via `vswhere`; it does not require
a particular Visual Studio year. It verifies both checked-in archives against
`vendor/sources.json`, builds the decoder-only libraries and helper, inspects
imports for accidentally introduced VC++ runtime DLLs, and assembles
`artifacts/heif-codec/win-x64`. These files must be distributed together.

The hashes were verified against the GitHub release asset SHA-256 digests on
2026-09-28. libheif 1.23.5 contains the 2026-09-21 security release fixes;
libde265 1.1.3 includes the 1.1.2 security fixes and high-bit-depth corrections.
NuGet audit does not audit these native sources; recheck upstream advisories
when updating them. The archives are unmodified official release sources.

The release package includes both complete archives, their manifest, this
helper's source, CMake configuration, the PowerShell build recipe and licenses.
To reconstruct the repository layout from those files:

1. Put the helper `main.cpp`, `CMakeLists.txt`, `README.md`, `NOTICE.txt` in
   `native/heif-worker/`.
2. Put the two archives and `sources.json` in `native/heif-worker/vendor/`.
3. Put `build-heif-codec.ps1` in `tools/` and the two COPYING files in
   `docs/licenses/`, retaining their names.
4. Run the build command above. Compatible modified DLLs can replace the
   distributed DLLs directly; the helper does not enforce binary signatures or
   hashes on installed libraries.

The CMake projects are also portable for local decoder tests. Build/install
libde265 first with shared libraries and all command-line tools disabled; then
build/install libheif with the exact codec flags in the PowerShell script and
`CMAKE_PREFIX_PATH` pointing to that install prefix; finally configure this
worker with the same prefix. On macOS/Linux omit the MSVC `/guard:cf` flags and
use the platform's normal shared-library runtime lookup path.

## Pipe protocol

```text
PhotoShelf.HeifWorker.exe --max-dimension 256
stdin: one complete HEIC/HEIF file, raw bytes, terminated by EOF
stdout: "PSH1", uint32-LE width, uint32-LE height, uint32-LE stride,
        followed by exactly height rows of stride bytes (RGBA8, straight alpha)
```

`stride` is exactly `width * 4`. The maximum dimension argument accepts 32–4096.
The primary image is decoded and reduced while preserving aspect ratio, without
upsampling. HEIF crop/rotation/mirroring are applied once by libheif. Pixel row
bounds come from the decoded channel and its actual stride, not header claims.
`--version` reports actual linked-library versions, without reading media.
Failures return exit code 2 and a short constant code on stderr. No input path is
accepted or printed. The parent must discard incomplete output/nonzero exits.

## Bounds and lifetime

- Input: at most 128 MiB, from a pipe, never written back to disk.
- Declared and decoded source: at most 64,000,000 pixels (including 48 MP photos).
- Decoded output: at most 4096 × 4096 × 4 bytes.
- Explicit libheif context limits: 768 MiB total native-accounted memory,
  256 MiB single block, 1024 tiles, 4096 items/children, 4 MiB color profile.
- Only libde265 HEVC decoding is compiled in; no dynamic plugins, encoders,
  experimental features, compressed headers or unrelated image decoders.
- Up to two codec threads; parallel tile decoding is disabled.
- Parent attaches a 1 GiB Windows Job Object and applies its wall-clock timeout
  before sending input. Process termination closes actual media readers before
  the parent's operation completes. The library's own accounting is not an OS
  hard memory limit, and the process boundary is not a permissions sandbox.

The worker clears `LIBHEIF_SECURITY_LIMITS` and explicitly initializes every
public security-limit field so inherited environment settings cannot disable
the bounds. Original content is never re-encoded; only disposable preview
pixels leave this process.

## Color and format scope

The output is an 8-bit SDR preview. libheif performs NCLX color conversion to
sRGB and converts high-bit-depth input to 8-bit. This is not full HDR/gain-map
rendering; ICC-only profiles do not receive a separate ICC transformation in
this worker. Auxiliary depth/gain-map images and additional still frames are
not rendered. Live Photo MOV/AAE companions are retained by PhotoShelf's file
organization layer; this decoder does not apply AAE edits or decode videos.
Unsupported media fails visibly without changing the original.
