# Dependencies

| Component | Version | License | Use |
| --- | --- | --- | --- |
| .NET | 10.0 (LTS), TFM `net10.0-windows10.0.19041.0` | MIT | runtime, shipped self-contained; Windows SDK projection for OCR |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | 11.3.14 | MIT | user interface, tray icon |
| Avalonia.Diagnostics | 11.3.14 | MIT | Debug builds only |
| SkiaSharp (transitive through Avalonia, no own reference) | as shipped with Avalonia | MIT (+ Skia BSD-3-Clause) | editor drawing, export (PNG/JPEG), thumbnails |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | view models, commands |
| NAudio.Wasapi (+ NAudio.Core) | 2.2.1 | MIT | system sound and microphone for videos |
| Material Design Icons | 7.4.47 | Apache-2.0 | icons (paths copied into `Theme/Icons.axaml`) |
| xUnit, Microsoft.NET.Test.Sdk | 2.9.3, 17.12.0 | Apache-2.0, MIT | tests, not shipped |
| Inno Setup | 6.3+ | Inno Setup license | installer, not shipped |

## Not shipped

| Component | How it gets onto the PC | License |
| --- | --- | --- |
| FFmpeg (BtbN/FFmpeg-Builds, `ffmpeg-n8.1-latest-win64-lgpl-8.1.zip`) | downloaded by the user after the license notice, SHA-256 checked, per user in `%LocalAppData%\SnapNova\tools\ffmpeg` – or an existing `ffmpeg.exe` | LGPL 2.1+ (GPL build: GPL 3+) |

## Windows APIs (no package)

GDI `BitBlt` with `CAPTUREBLT` (screen freeze), `EnumWindows` + `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` (window snapping), `RegisterHotKey` (hotkeys), clipboard (`CF_DIB` + `PNG`), `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` (recording frame), DXGI output enumeration (ddagrab output index), job objects (FFmpeg kill-on-close), `HKCU\…\Run` (autostart), `Windows.Media.Ocr` (text recognition, offline), UI Automation (element snapping), `SendInput` (automatic scrolling), layered windows (cursor highlight and click effects), clipboard history exclusion formats, process loopback (`ActivateAudioInterfaceAsync`, sound of one program for additional recordings), Windows recycle bin (`Microsoft.VisualBasic.FileIO`, part of .NET).

## Online services (opt-in)

| Service | When | Data |
| --- | --- | --- |
| `github.com/BtbN/FFmpeg-Builds` | FFmpeg download after consent | IP address, user agent |
| `api.github.com/repos/complicatiion/SnapNova/releases/latest` | update check, only when switched on (at most daily) | IP address, user agent |
