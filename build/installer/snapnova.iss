; SnapNova installer (Inno Setup 6.3+).
;
; Build:
;   Easiest: build the solution in Visual Studio with configuration "Release" – SnapNova.App publishes itself to
;   artifacts\publish\SnapNova (self-contained, with the .NET runtime) and runs this script with ISCC (Inno Setup 6).
;   Manually:
;   1. Publish the app:  powershell -NoProfile -ExecutionPolicy Bypass -File .\build\publish.ps1 -SkipInstaller
;   2. ISCC /DAppVersion=0.0.7 /DSourceDir=..\..\artifacts\publish\SnapNova /DOutputDir=..\..\artifacts snapnova.iss
;   build\publish.ps1 does both steps (tests, publish, portable ZIP, setup, SHA-256).
;
; Install mode:
;   Default is a system-wide installation (Program Files, all users, admin rights). The first wizard page offers
;   "Install for me only" as the second option (%LocalAppData%\Programs, no admin rights). Silent: /ALLUSERS or /CURRENTUSER.
;   {autopf}, {autoprograms} and the registry root HKA follow the chosen mode automatically.
;
; Per-user options (autostart, start in tray, minimize to tray) are written as setup defaults to
;   HKA\Software\sksdesign\SnapNova\Setup
; SnapNova applies them once per Windows user at the first start (autostart = HKCU Run, switchable in the app without
; admin rights). The installing user gets them right away (--apply-setup-defaults, run as the original user).
;
; Terminal server / multi-session Windows (RD Session Host, Windows 10/11 Enterprise multi-session, AVD):
;   Install system-wide. The task "Start for every user at sign-in" writes ONE machine-wide Run entry
;   (HKLM\...\Run, "SnapNova.exe --autostart"); each user can still switch autostart off in SnapNova (the app then ends
;   at once at sign-in). Settings, thumbnails and FFmpeg stay per user (%LocalAppData%\SnapNova); the app is single
;   instance per session (Local\ mutex, pipe per user and session), hotkeys and the tray icon work per session.
;   On a multi-session system this task is preselected. Silent: /TASKS="autostartall,..." .
;
; FFmpeg is NOT part of this setup. SnapNova downloads it only after the user accepted the license notice (opt-in).
;
; English is the default language; German is offered in the language dialog.
; Wizard images (SnapNova logo, no text, 100–250 % DPI) are in images\.
; Targets: Windows 10 2004 (build 19041) or later, Windows 11, 64-bit.

#ifndef AppVersion
  #define AppVersion "0.0.7"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\SnapNova"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define AppName "SnapNova"
#define AppExe "SnapNova.exe"
#define ProjectExt ".snova"
#define ProgId "SnapNova.Capture"
#define RegBase "Software\sksdesign\SnapNova"

; The app must be published self-contained (the .NET runtime is part of the setup, nothing to install on the target PC).
#if !FileExists(AddBackslash(SourceDir) + "coreclr.dll") && !FileExists(AddBackslash(SourcePath) + AddBackslash(SourceDir) + "coreclr.dll")
  #error SnapNova is not published self-contained (coreclr.dll missing in SourceDir). Build Release in Visual Studio or run build\publish.ps1.
#endif

[Setup]
AppId={{8F3B6C2E-5A71-4D9E-9C0B-7E2A41D6F5A3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=sksdesign | complicatiion
AppPublisherURL=https://sksdesign.de
AppSupportURL=https://github.com/complicatiion/SnapNova
AppCopyright=Copyright (C) 2026 Sven Klocke
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\..\licenses\LICENSE.md
OutputDir={#OutputDir}
OutputBaseFilename=snapnova_v{#AppVersion}_setup
SetupIconFile=..\..\src\SnapNova.App\Assets\snapnova.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardImageFile=images\wizard-100.bmp,images\wizard-125.bmp,images\wizard-150.bmp,images\wizard-175.bmp,images\wizard-200.bmp,images\wizard-225.bmp,images\wizard-250.bmp
WizardSmallImageFile=images\wizard-small-100.bmp,images\wizard-small-125.bmp,images\wizard-small-150.bmp,images\wizard-small-175.bmp,images\wizard-small-200.bmp,images\wizard-small-225.bmp,images\wizard-small-250.bmp
WizardImageStretch=no
; Windows 10 2004 or later: Windows.Graphics.Capture without border, WDA_EXCLUDEFROMCAPTURE for the recording frame
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; System-wide by default, "for me only" as the optional second choice (dialog + /ALLUSERS, /CURRENTUSER)
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UsedUserAreasWarning=no
; Terminal servers: no install-mode shadowing of per-user data (SnapNova keeps it per user by itself)
TerminalServicesAware=yes
; New file type: refresh Explorer icons and "Open with" after install/uninstall
ChangesAssociations=yes
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=yes
LanguageDetectionMethod=none

[Languages]
; First entry = default
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.GroupStartup=Startup and system tray:
de.GroupStartup=Start und System-Tray:
en.GroupShell=Windows integration:
de.GroupShell=Windows-Integration:
en.TaskAutostart=Start SnapNova with Windows
de.TaskAutostart=SnapNova mit Windows starten
en.TaskStartInTray=Start in the system tray (no window at startup)
de.TaskStartInTray=Im System-Tray starten (kein Fenster beim Start)
en.TaskMinimizeToTray=Minimize and close to the system tray
de.TaskMinimizeToTray=Beim Minimieren und Schließen in den System-Tray
en.TaskAssociate=Associate SnapNova capture files (.snova) with SnapNova
de.TaskAssociate=SnapNova-Aufnahmedateien (.snova) mit SnapNova verknüpfen
en.TaskOpenWith=Add SnapNova to "Open with" for images and videos (PNG, JPG, GIF, MP4) – does not change your default apps
de.TaskOpenWith=SnapNova zu „Öffnen mit“ für Bilder und Videos hinzufügen (PNG, JPG, GIF, MP4) – Standard-Apps bleiben unverändert
en.TaskAnimations=Smooth animations (page changes, panels, notifications)
de.TaskAnimations=Weiche Animationen (Seitenwechsel, Seitenleisten, Benachrichtigungen)
en.TaskAutostartAll=Start SnapNova for every user at sign-in (terminal server / multi-session)
de.TaskAutostartAll=SnapNova für jeden Benutzer bei der Anmeldung starten (Terminalserver / Multi-Session)
en.TaskDesktopIcon=Create a desktop shortcut
de.TaskDesktopIcon=Desktop-Verknüpfung erstellen
en.FileTypeName=SnapNova capture
de.FileTypeName=SnapNova-Aufnahme
en.PurgeQuestion=Also delete SnapNova data (settings, library index, thumbnails and the downloaded FFmpeg)?%n%nYour captures in the SnapNova pictures folder are kept.
de.PurgeQuestion=Auch SnapNova-Daten löschen (Einstellungen, Bibliotheksindex, Vorschaubilder und das heruntergeladene FFmpeg)?%n%nDeine Aufnahmen im SnapNova-Bilderordner bleiben erhalten.

[Tasks]
Name: "autostart"; Description: "{cm:TaskAutostart}"; GroupDescription: "{cm:GroupStartup}"
Name: "starttray"; Description: "{cm:TaskStartInTray}"; GroupDescription: "{cm:GroupStartup}"
Name: "minimizetray"; Description: "{cm:TaskMinimizeToTray}"; GroupDescription: "{cm:GroupStartup}"
Name: "autostartall"; Description: "{cm:TaskAutostartAll}"; GroupDescription: "{cm:GroupStartup}"; Flags: unchecked; Check: IsAdminInstallMode
Name: "animations"; Description: "{cm:TaskAnimations}"; GroupDescription: "{cm:GroupStartup}"
Name: "associate"; Description: "{cm:TaskAssociate}"; GroupDescription: "{cm:GroupShell}"
Name: "openwith"; Description: "{cm:TaskOpenWith}"; GroupDescription: "{cm:GroupShell}"; Flags: unchecked
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:GroupShell}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; ---- Setup defaults, applied once per Windows user by the app (HKA = HKLM system-wide, HKCU for "me only") ----
Root: HKA; Subkey: "{#RegBase}"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "{#RegBase}\Setup"; Flags: uninsdeletekey
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: string; ValueName: "InstallDir"; ValueData: "{app}"
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: string; ValueName: "Version"; ValueData: "{#AppVersion}"
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: dword; ValueName: "StartWithWindows"; ValueData: "1"; Tasks: autostart
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: dword; ValueName: "StartWithWindows"; ValueData: "0"; Tasks: not autostart
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: dword; ValueName: "StartInTray"; ValueData: "1"; Tasks: starttray
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: dword; ValueName: "StartInTray"; ValueData: "0"; Tasks: not starttray
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: dword; ValueName: "MinimizeToTray"; ValueData: "1"; Tasks: minimizetray
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: dword; ValueName: "MinimizeToTray"; ValueData: "0"; Tasks: not minimizetray
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: dword; ValueName: "Animations"; ValueData: "1"; Tasks: animations
Root: HKA; Subkey: "{#RegBase}\Setup"; ValueType: dword; ValueName: "Animations"; ValueData: "0"; Tasks: not animations

; ---- Machine-wide autostart for every user (terminal server); each user can switch it off in SnapNova ----
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostartall

; ---- Own file type .snova (capture + editable annotation layers) ----
Root: HKA; Subkey: "Software\Classes\{#ProjectExt}"; ValueType: string; ValueName: ""; ValueData: "{#ProgId}"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProjectExt}"; ValueType: string; ValueName: "Content Type"; ValueData: "application/x-snapnova"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProjectExt}\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "{cm:FileTypeName}"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: associate

; ---- Registered application (Default apps / Open with) ----
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: "{#ProjectExt}"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".png"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".jpg"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".gif"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""

; ---- Optional: "Open with" for common formats (never takes over the default app) ----
Root: HKA; Subkey: "Software\Classes\.png\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.jpg\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.gif\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith

[Run]
; Applies the setup defaults (autostart in HKCU\...\Run, tray options) for the installing user – as that user, not as admin.
Filename: "{app}\{#AppExe}"; Parameters: "--apply-setup-defaults"; Flags: runhidden waituntilterminated runasoriginaluser
; SnapNova runs without admin rights (manifest asInvoker), so it is always started as the original user.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Removes the autostart entry of the uninstalling user (other users' entries point to a missing file and are ignored by Windows).
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-autostart"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveAutostart"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\data"

[Code]
var
  TasksPreselected: Boolean;

{ Multi-session Windows: RD Session Host (server with the terminal server role) or Windows 10/11 Enterprise multi-session. }
function IsMultiSession(): Boolean;
var
  Edition: String;
  Version: TWindowsVersion;
  AppCompat: Cardinal;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'EditionID', Edition) then
    if (CompareText(Edition, 'ServerRdsh') = 0) or (CompareText(Edition, 'EnterpriseMultiSession') = 0) then
      Result := True;
  GetWindowsVersionEx(Version);
  if (not Result) and (Version.ProductType <> VER_NT_WORKSTATION) then
    if RegQueryDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Terminal Server', 'TSAppCompat', AppCompat) then
      Result := AppCompat <> 0;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { Preselect the machine-wide autostart once on terminal servers (the user can still untick it). }
  if (CurPageID = wpSelectTasks) and (not TasksPreselected) then
  begin
    TasksPreselected := True;
    if IsAdminInstallMode and IsMultiSession then
      WizardSelectTasks('autostartall');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  { FFmpeg runs in a job object of SnapNova (kill-on-close): ending SnapNova also ends a running recording process. }
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) and
     (MsgBox(CustomMessage('PurgeQuestion'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
  begin
    { Settings, library index, thumbnails, FFmpeg – never the captures themselves (Pictures\SnapNova) }
    DelTree(ExpandConstant('{localappdata}\SnapNova'), True, True, True);
    DelTree(ExpandConstant('{userappdata}\SnapNova'), True, True, True);
  end;
end;
