# FFmpeg (not bundled)

SnapNova does **not** contain, ship or modify FFmpeg.

For video recording, SnapNova offers to download an official FFmpeg Windows build from
**BtbN/FFmpeg-Builds** (https://github.com/BtbN/FFmpeg-Builds) – only after the user has read and accepted a license notice.
The download is verified against the SHA-256 checksum published with the release and stored per user in
`%LocalAppData%\SnapNova\tools\ffmpeg`, together with FFmpeg's own `LICENSE.txt`.
Alternatively the user can select an existing `ffmpeg.exe`.

SnapNova runs FFmpeg exclusively as a separate program (`ffmpeg.exe`) with command-line arguments. It does not link to
FFmpeg libraries.

| Variant | License | Notes |
| --- | --- | --- |
| LGPL build (default) | GNU LGPL 2.1 or later | no GPL-only libraries (no x264/x265) |
| GPL build (optional, Settings › Video) | GNU GPL 3 or later | includes x264/x265 |

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project. Source code: https://ffmpeg.org/download.html
