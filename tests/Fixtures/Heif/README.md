# Synthetic HEIC fixtures

Generated from owned RGB quadrants and synthetic EXIF via `python3 tools/generate-heif-fixtures.py` (macOS ImageIO/sips). No personal or vendor photographs.

Colours: red/green above blue/yellow, with an off-centre black marker. Lossy HEVC means tests use colour tolerances. `quadrants-rotated` has orientation 6 (90 degrees clockwise), expected displayed size 180x320. Capture date is 2026-09-28 12:34:56; `quadrants-no-date` has none. The large grid is 8064x6048 (48,771,072 pixels).

These outputs are committed so Windows tests need no encoder or installed HEIF extension. Re-encoding can change hashes across OS versions; update hashes deliberately.

| File | Bytes | SHA256 |
|---|---:|---|
| quadrants-48mp.heic | 27183 | `dd45821e3fb22c344e50b43cdf6bc6187085a6917088a8f0d8692182f70971fe` |
| quadrants-no-date.heic | 693 | `2637f5b2fd94d744250e9cb4888020511e451d3cd7eb808cafa8bd2b3c58872a` |
| quadrants-rotated.heic | 1270 | `6946deb252e76a01bce348d7381db0934831d032ca355b96b92fa38a27edacd0` |
| quadrants.heic | 1270 | `a6aa41d8bc755de8f9c2786fe465a6cf07779782d222cc025833f3befbea4560` |
