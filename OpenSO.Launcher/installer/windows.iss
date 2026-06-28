; OpenSO Launcher - Windows installer (Inno Setup 6).
; Built in CI:  ISCC /DAppVersion=0.1.0 /DPublishDir=<abs path to publish> installer\windows.iss
; Installs per-user to %LOCALAPPDATA% (PrivilegesRequired=lowest -> no UAC prompt). The launcher then
; installs the GAME itself to %LOCALAPPDATA%\OpenSO (see LauncherConfig.ResolvedInstallRoot).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
AppId={{8F3A1C2D-4B5E-4A6F-9C8D-1E2F3A4B5C6D}
AppName=OpenSO Launcher
AppVersion={#AppVersion}
AppPublisher=OpenSO
AppPublisherURL=https://openso.org
DefaultDirName={localappdata}\OpenSO Launcher
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=OpenSO-Launcher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile={#SourcePath}\..\Assets\openso.ico
UninstallDisplayIcon={app}\OpenSO.Launcher.exe

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\OpenSO Launcher"; Filename: "{app}\OpenSO.Launcher.exe"
Name: "{autodesktop}\OpenSO Launcher"; Filename: "{app}\OpenSO.Launcher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\OpenSO.Launcher.exe"; Description: "Launch OpenSO Launcher"; Flags: nowait postinstall skipifsilent
