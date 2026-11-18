# Privacy

SnapNova is **offline and local**:

- **No telemetry, no account, no cloud, no ads.** SnapNova does not send usage data anywhere.
- **No external resources in the app.** Fonts and icons are embedded; nothing is loaded from CDNs.
- **Captures stay on your PC**, in the folders you choose (default `Pictures\SnapNova` and `Videos\SnapNova`). Nothing is uploaded.
- **Text recognition (OCR)** uses the offline OCR engine built into Windows; recognized text is stored only inside the `.snova` file.
- **Network connections** occur only
  - when you agree to the **FFmpeg download** for video recording (*Accept* or *Settings › Video › Install*): SnapNova fetches `checksums.sha256` and the FFmpeg ZIP from `github.com/BtbN/FFmpeg-Builds/releases`. GitHub receives your IP address and the user agent `SnapNova/<version>` (GitHub's privacy statement applies);
  - after you confirm the **update check** once (*Check now* or the automatic check in *Settings › Updates*, both off until then): at most once a day SnapNova requests `api.github.com/repos/complicatiion/SnapNova/releases/latest`; a download happens only when you click *Download*. GitHub receives your IP address and the user agent `SnapNova/<version>`;
  - when you open a link (project page, website, license pages) in your browser.

Video recording runs completely offline. Screen content is grabbed by FFmpeg on your PC and written to your capture folder. System sound and microphone are recorded only when switched on in *Settings › Video* (microphone off by default).

## Data stored by SnapNova

| Location | Content |
| --- | --- |
| `%LocalAppData%\SnapNova\settings.json` (portable: `data\settings.json`) | settings (language, theme, hotkeys, capture folder, video options, window state) |
| image and video folders (default `Pictures\SnapNova`, `Videos\SnapNova`) | your captures: `.snova` (original image, annotations, recognized text, origin: time, program name and window title of the captured window, screen area), exported PNG/JPEG/WebP, MP4/MKV videos, `.cursor.json` (mouse track of a recording for *Follow the mouse*) |
| `.snapnova-trash` in each capture folder | deleted captures until restored or removed (default after 7 days); with *Windows recycle bin* chosen, local captures go to the Windows recycle bin instead |
| `%LocalAppData%\SnapNova\updates` | downloaded update (only after your click) |
| `%LocalAppData%\SnapNova\thumbs` | preview images of videos for the gallery |
| `%LocalAppData%\SnapNova\tools\ffmpeg` | downloaded FFmpeg (only after consent), `ffmpeg.json` with the date of your consent, version, source and SHA-256 |
| `%LocalAppData%\SnapNova\temp` | segments and sound of a running recording – removed when the recording is finished |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` → `SnapNova` | only if *Start with Windows* is on (on terminal servers the setup may write one machine-wide entry in HKLM instead) |
| `HKCU\Software\Classes\.snova`, `SnapNova.Capture` | file type `.snova` → SnapNova, only when no working association exists |
| `HKCU\Control Panel\Keyboard` → `PrintScreenKeyForSnippingEnabled` | only when you click *Use for SnapNova* at the Print key setting |

Uninstalling asks whether settings, thumbnails and FFmpeg should be deleted. **Your captures are never deleted by the uninstaller.**

*Settings › Reset SnapNova* deletes the settings, caches and temporary files in `%LocalAppData%\SnapNova` (FFmpeg only if chosen) and removes the per-user autostart entry; the defaults apply at the next start. Captures are deleted only with *Also delete all captures* plus an explicit confirmation – and then only files SnapNova created (`.snova`, `SnapNova_*` images/videos, mouse tracks, `.snapnova-trash`).

## Clipboard

With *Copy screenshots to the clipboard* (on by default) every screenshot is placed on the Windows clipboard. *Keep out of clipboard history* (Settings › Capture) marks SnapNova's clipboard data so that Windows does not keep it in the clipboard history (Win+V) or sync it to the cloud clipboard.
