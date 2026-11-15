# Licenses

## SnapNova

SnapNova is released under the **MIT License** – see [LICENSE.md](LICENSE.md).
Copyright © 2026 Sven Klocke (*sksdesign | complicatiion*).

| Document | Content |
| --- | --- |
| [LICENSE.md](LICENSE.md) | MIT License of SnapNova |
| [COPYRIGHT.md](COPYRIGHT.md) | Copyright and trademark notes |
| [LEGAL-NOTICE.md](LEGAL-NOTICE.md) | Disclaimer, limitation of liability, recording of other people |
| [PRIVACY.md](PRIVACY.md) | Privacy notes (offline, no telemetry) |

## Third-party components

Components shipped with SnapNova keep their own copyrights and licenses. Full license texts are in [third-party/](third-party/).

| Component | Version | License | Shipped as | File |
| --- | --- | --- | --- | --- |
| .NET Runtime | 10.0 | MIT | self-contained runtime | [third-party/DOTNET.md](third-party/DOTNET.md) |
| Avalonia UI (incl. Fluent theme, Inter font package) | 11.3.14 | MIT | library | [third-party/AVALONIA.md](third-party/AVALONIA.md) |
| SkiaSharp / HarfBuzzSharp (via Avalonia) | as shipped with Avalonia | MIT (+ Skia BSD-3-Clause, HarfBuzz MIT-style) | native libraries | [third-party/SKIASHARP.md](third-party/SKIASHARP.md) |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | library | [third-party/COMMUNITYTOOLKIT-MVVM.md](third-party/COMMUNITYTOOLKIT-MVVM.md) |
| NAudio (NAudio.Wasapi, NAudio.Core) | 2.2.1 | MIT | library | [third-party/NAUDIO.md](third-party/NAUDIO.md) |
| Material Design Icons (Pictogrammers) | 7.4.47 | Apache-2.0 | vector icons in the UI | [third-party/MATERIAL-DESIGN-ICONS.md](third-party/MATERIAL-DESIGN-ICONS.md) |
| Inter (font) | 4.x | SIL OFL 1.1 | embedded font | [third-party/INTER-FONT.md](third-party/INTER-FONT.md) |

Shared license texts: [Apache License 2.0](third-party/LICENSE-APACHE-2.0.md) · [SIL Open Font License 1.1](third-party/LICENSE-OFL-1.1.md) · [MIT](third-party/LICENSE-MIT.md) · [BSD 3-Clause](third-party/LICENSE-BSD-3-CLAUSE.md)

Build-time only (not shipped): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), Inno Setup (own license, installer only).

**Not shipped, downloaded only with the user's consent: FFmpeg.** SnapNova never bundles FFmpeg. Before the first video recording it shows a license notice; only after the user agreed, it downloads an official build from BtbN/FFmpeg-Builds (GitHub), verifies the SHA-256 and stores it per user together with FFmpeg's own license. SnapNova runs it only as a separate program. Details: [third-party/FFMPEG.md](third-party/FFMPEG.md).
