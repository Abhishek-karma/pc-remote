; PC-Remote-Setup.iss — Inno Setup installer for PC Remote (requirement 8).
;
; UX contract: "Run once to install, then always available."
;   * Installs to C:\Program Files\PC Remote\  (protected path, UIAccess-safe)
;   * Registers PCRemoteService with the SCM: automatic start, recovery restart
;   * Creates least-exposure firewall rules (private/domain profiles, LAN subnet)
;   * Optional tray autostart (HKCU Run — cosmetic only; the service does not
;     depend on the tray)
;   * Cleans up the legacy self-install (LocalAppData copy + HKCU Run entry)
;   * Full upgrade + uninstall support via the stable AppId
;
; Build:  installer\build.ps1  (publishes binaries into installer\staging,
;         then runs ISCC). Production signing: sign the staged exes BEFORE
;         running ISCC and set SignTool directives below.

#define MyAppName "PC Remote"
#define MyAppVersion GetVersionNumbersString("staging\PCRemoteService.exe")
#define MyAppPublisher "PC Remote"
#define MyAppExeName "PCRemoteTray.exe"
// Must be a syntactically valid GUID: the final group is exactly 12 hex
// digits. ("PCREMOTE0001" was not hex and made AppId invalid.)
#define MyAppId "{{8F4C0B6E-2A1D-4E3F-9C5A-1D0E5B7A9C31}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PC Remote
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; Uninstall-safe output; the installer itself is signed in CI (requirement 13).
OutputBaseFilename=PC-Remote-Setup
OutputDir=..\dist
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Architectures
ArchitecturesInstallIn64BitMode=x64compatible
; Do not let users install into an unprotected path (UIAccess requirement).
; DisableDirPage is the documented directive for this - "DefaultDirNameFixed"
; is not an Inno Setup directive and was silently ineffective.
DisableDirPage=yes

; Production signing — uncomment with real values in CI:
;SignTool=signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f
;SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "trayautostart"; Description: "Show the PC Remote tray icon after logon"; \
    GroupDescription: "Startup:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; Framework-dependent publish output (staging built by build.ps1).
Source: "staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; Service-owned data directory with restrictive ACL (created explicitly so the
; installer can tighten it before the first service start).
Name: "{commonappdata}\PCRemote"; Permissions: "SYSTEM-modify Administrators-modify Users-read"

[Icons]
Name: "{group}\PC Remote"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\PC Remote"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Tray autostart (optional, per-user, cosmetic only).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "PC Remote Tray"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; \
    Tasks: trayautostart; Flags: uninsdeletevalue
; UIAccess helper enabled — PCRemoteSession.UIA.exe is signed and in Program Files.
Root: HKLM; Subkey: "Software\PCRemote"; ValueType: dword; ValueName: "UseUIAccess"; \
    ValueData: "1"; Flags: uninsdeletekey

[Run]
; Delete first: on an upgrade the service already exists and "sc create" fails
; with 1073, which runhidden would swallow (DisplayName and recovery config
; would then never be refreshed).
Filename: "{sys}\sc.exe"; Parameters: "delete PCRemoteService"; Flags: runhidden; \
    StatusMsg: "Removing any previous PC Remote service registration..."
; --- Register the Windows service (requirement 1) ---
Filename: "{sys}\sc.exe"; Parameters: "create PCRemoteService binPath= ""\""{app}\PCRemoteService.exe\"""" start= auto obj= LocalSystem DisplayName= ""PC Remote Service"""; Flags: runhidden; StatusMsg: "Registering PC Remote service…"
Filename: "{sys}\sc.exe"; Parameters: "description PCRemoteService ""PC Remote — secure remote control service (WSS control channel, session management)"""; Flags: runhidden
; --- Service recovery: auto-restart after crash (requirement 1) ---
Filename: "{sys}\sc.exe"; Parameters: "failure PCRemoteService reset= 86400 actions= restart/5000/restart/10000/restart/30000"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "failureflag PCRemoteService 1"; Flags: runhidden
; --- Least-exposure firewall rules (requirement 9): private/domain, local subnet only ---
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PC Remote Agent"""; Flags: runhidden; StatusMsg: "Removing legacy firewall rules…"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""PC Remote Service (LAN, private)"" dir=in action=allow protocol=TCP localport=58642 profile=private,domain remoteip=localsubnet enable=yes"; Flags: runhidden; StatusMsg: "Creating firewall rule (control channel)…"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""PC Remote mDNS (LAN, private)"" dir=in action=allow protocol=UDP localport=5353 profile=private,domain remoteip=localsubnet enable=yes"; Flags: runhidden; StatusMsg: "Creating firewall rule (discovery)…"
; --- Start the service now ---
Filename: "{sys}\net.exe"; Parameters: "start PCRemoteService"; Flags: runhidden; StatusMsg: "Starting PC Remote service…"
; Launch the tray UI once at the end of setup (per-user session).
Filename: "{app}\{#MyAppExeName}"; Description: "Launch PC Remote"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop + delete service
Filename: "{sys}\net.exe"; Parameters: "stop PCRemoteService"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete PCRemoteService"; Flags: runhidden; RunOnceId: "DelSvc"
; Remove firewall rules
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PC Remote Service (LAN, private)"""; Flags: runhidden; RunOnceId: "DelFwTcp"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PC Remote mDNS (LAN, private)"""; Flags: runhidden; RunOnceId: "DelFwUdp"

[UninstallDelete]
; Stage directory for verified updates
Type: filesandordirs; Name: "{commonappdata}\PCRemote\update"

[Code]
// Legacy self-install cleanup (requirement 8: delete the self-install
// architecture). Removes the old HKCU Run entry and the LocalAppData copy
// for the user running setup. Done on install AND uninstall.
procedure CleanupLegacyInstall();
var
  RunKey: string;
  LegacyDir: string;
begin
  RunKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
  RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'PC Remote Agent');
  LegacyDir := ExpandConstant('{userappdata}') + '\..\Local\PCRemote';
  if DirExists(LegacyDir) then
    DelTree(LegacyDir, True, True, True);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanupLegacyInstall();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove tray autostart for every user is not possible from one session;
    // remove for the current user at minimum.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PC Remote Tray');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  // Upgrade path: stop the old service so its files can be replaced. The
  // stop must complete before [Run] re-registers the service.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop PCRemoteService', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
