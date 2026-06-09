; Swoosh installer (Inno Setup). Built in CI, once per architecture:
;
;   ISCC /DAppVersion=<version> /DArch=x64|arm64 /DSourceDir=<publish dir> /O<out> installer\Swoosh.iss
;
; Installs the self-contained tray app + WinUI settings app into Program Files,
; adds a Start Menu shortcut, and registers a clean uninstaller. Launch-at-login
; stays app-owned (the tray app reconciles the HKCU Run key from its setting); the
; uninstaller removes that Run value so it never dangles after removal.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-" + Arch
#endif

[Setup]
AppId={{BD8314B2-7533-4C33-8CDB-605EA803A3D9}
AppName=Swoosh
AppVersion={#AppVersion}
AppPublisher=Bradley Wyatt
AppPublisherURL=https://github.com/bwya77/swoosh
AppSupportURL=https://github.com/bwya77/swoosh/issues
AppUpdatesURL=https://github.com/bwya77/swoosh/releases
DefaultDirName={autopf}\Swoosh
DefaultGroupName=Swoosh
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\Swoosh.exe
UninstallDisplayName=Swoosh
SetupIconFile=..\Assets\swoosh.ico
OutputBaseFilename=SwooshSetup-{#AppVersion}-win-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
; Close a running Swoosh before replacing files so the in-app update can replace the
; locked executables. Relaunch is handled explicitly in [Run] (including silent installs),
; not via Restart Manager, which does not reliably restart the app after a silent update.
CloseApplications=yes
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Swoosh"; Filename: "{app}\Swoosh.exe"
Name: "{autodesktop}\Swoosh"; Filename: "{app}\Swoosh.exe"; Tasks: desktopicon

[Tasks]
Name: "startupwithwindows"; Description: "Start Swoosh when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
; Launch after install. When "Start with Windows" was ticked, pass --enable-startup so
; the app persists the LaunchAtLogin setting and registers the Run key itself (the app
; stays the single owner of that key). runasoriginaluser drops admin so the tray app and
; its per-user settings run as the actual user, not the elevated installer account.
; shellexec is REQUIRED because Swoosh.exe is a UIAccess app (uiAccess="true" in its
; manifest, so it can control elevated windows). Windows refuses to start a UIAccess
; binary via CreateProcess/CreateProcessAsUser (ERROR_ELEVATION_REQUIRED / code 740) unless
; the caller holds SeTcbPrivilege; it must be launched through ShellExecuteEx instead, which
; is the same path the Start menu and a double-click use. runasoriginaluser keeps that
; shell-launch at the user's normal (medium) integrity rather than the installer's admin.
Filename: "{app}\Swoosh.exe"; Parameters: "--enable-startup"; Description: "Launch Swoosh"; Tasks: startupwithwindows; Flags: nowait postinstall skipifsilent runasoriginaluser shellexec
Filename: "{app}\Swoosh.exe"; Description: "Launch Swoosh"; Tasks: not startupwithwindows; Flags: nowait postinstall skipifsilent runasoriginaluser shellexec
; Silent in-app update path: the postinstall checkbox above is skipped under /SILENT and
; /VERYSILENT, so relaunch Swoosh explicitly here. The app reconciles its own launch-at-login
; from settings on startup, so no --enable-startup is needed. runasoriginaluser returns to the
; invoking user since the installer runs elevated; shellexec is required for the UIAccess
; binary (see above).
Filename: "{app}\Swoosh.exe"; Flags: nowait runasoriginaluser shellexec; Check: WizardSilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Remove the app-owned "Start with Windows" entry so it doesn't point at a
  // deleted executable after uninstall. User settings under %APPDATA%\Swoosh are
  // intentionally left in place.
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Swoosh');
end;
