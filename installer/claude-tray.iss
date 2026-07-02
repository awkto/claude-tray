; Inno Setup script for claude-tray — per-user install, no admin required.
; Built by .github/workflows/release.yml with:
;   ISCC.exe /DAppVersion=x.y.z /DPublishDir=<abs path to publish\win-x64> claude-tray.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif

[Setup]
AppId={{B7E5D1A4-2F3C-4E8A-9D16-C0FFEE7A2026}
AppName=claude-tray
AppVersion={#AppVersion}
AppPublisher=awkto
AppPublisherURL=https://github.com/awkto/claude-tray
DefaultDirName={localappdata}\Programs\claude-tray
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=claude-tray-setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
UninstallDisplayIcon={app}\claude-tray.exe
WizardStyle=modern

[Tasks]
Name: "startup"; Description: "Start claude-tray when you sign in to Windows"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{userprograms}\claude-tray"; Filename: "{app}\claude-tray.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "claude-tray"; ValueData: """{app}\claude-tray.exe"""; Tasks: startup; \
  Flags: uninsdeletevalue

[Run]
Filename: "{app}\claude-tray.exe"; Description: "Launch claude-tray"; \
  Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /im claude-tray.exe /f"; \
  Flags: runhidden; RunOnceId: "KillTray"
