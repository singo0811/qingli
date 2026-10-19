; TidyWin installer. Build with installer\build.ps1 (it publishes first and passes the version and the folder).
; Installs for the current user by default (no administrator prompt); the person can choose "for all users" in the dialog.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{6D2B2C0E-3F1A-4C5B-9E52-7A4B1D6E8F30}
AppName=TidyWin
AppVersion={#AppVersion}
AppVerName=TidyWin {#AppVersion}
AppPublisher=TidyWin
DefaultDirName={autopf}\TidyWin
DefaultGroupName=TidyWin
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=TidyWin-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=TidyWin
UninstallDisplayIcon={app}\TidyWin.exe
; An update runs this installer over a running copy: ask it to close. It is started again by the last [Run] line below
; (Windows' own restart of closed programs only works for programs that registered for it, and ours does not).
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName=TidyWin
MinVersion=10.0.17763

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
en.DesktopIcon=Create a desktop shortcut
zh.DesktopIcon=创建桌面快捷方式
en.LaunchApp=Start TidyWin
zh.LaunchApp=启动 TidyWin

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TidyWin"; Filename: "{app}\TidyWin.exe"
Name: "{autodesktop}\TidyWin"; Filename: "{app}\TidyWin.exe"; Tasks: desktopicon

[Run]
; The ordinary wizard offers a "start TidyWin" tick box; a silent run (which is what an update is) just starts it.
Filename: "{app}\TidyWin.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\TidyWin.exe"; Flags: nowait skipifnotsilent runasoriginaluser

[UninstallDelete]
; Downloaded update installers are ours to remove. Settings and the undo records stay, so a reinstall picks up where it left off.
Type: filesandordirs; Name: "{localappdata}\TidyWin\update"
