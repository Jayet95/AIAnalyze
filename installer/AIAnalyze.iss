; AIAnalyze setup wizard (Inno Setup 6). Build it with scripts\build-installer.ps1, which prepares
; build\app and passes the version. Compiling by hand:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=0.5.0 installer\AIAnalyze.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "AIAnalyze"
#define MyServiceName "AIAnalyze"
#define MyUiUrl "http://127.0.0.1:5280/"

[Setup]
; Never change AppId: it links upgrades and the uninstaller to earlier installs.
AppId={{6F3D2C41-8B7A-4E59-9C1D-2A7E5B4F9A10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=AIAnalyze
AppComments=Local PC health assistant
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=AIAnalyze-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=privacy.txt
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\AIAnalyze.exe
CloseApplications=no
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} setup

[Messages]
WelcomeLabel2=This installs [name/ver], a local assistant that checks your PC's health, watches temperatures and game performance, and explains problems in plain words.%n%nIt runs in the background as a Windows service and opens in your browser at 127.0.0.1:5280. Nothing leaves your PC unless you choose to share or ask the assistant.

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
; build\app holds the self-contained app (no .NET needed), the UI in wwwroot, and optionally PresentMon.
Source: "..\build\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[INI]
; Shortcuts to the local UI (a web page served by the service).
Filename: "{autoprograms}\{#MyAppName}.url"; Section: "InternetShortcut"; Key: "URL"; String: "{#MyUiUrl}"
Filename: "{autodesktop}\{#MyAppName}.url"; Section: "InternetShortcut"; Key: "URL"; String: "{#MyUiUrl}"; Tasks: desktopicon

[UninstallDelete]
Type: files; Name: "{autoprograms}\{#MyAppName}.url"
Type: files; Name: "{autodesktop}\{#MyAppName}.url"

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\AIAnalyze.exe"" start= auto DisplayName= ""AIAnalyze PC Diagnostics"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing the AIAnalyze service..."
Filename: "{sys}\sc.exe"; Parameters: "description {#MyServiceName} ""Checks PC health, samples CPU and GPU sensors, records FPS, and explains problems. UI: {#MyUiUrl}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting AIAnalyze..."
; Give the service a few seconds to start before opening the UI.
Filename: "{cmd}"; Parameters: "/c timeout /t 4 /nobreak >nul & start """" ""{#MyUiUrl}"""; Description: "Open AIAnalyze"; Flags: postinstall nowait runhidden skipifsilent

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{cmd}"; Parameters: "/c timeout /t 3 /nobreak >nul"; Flags: runhidden waituntilterminated; RunOnceId: "WaitForStop"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
const
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\AIAnalyze';
  PawnIOKey = 'SYSTEM\CurrentControlSet\Services\PawnIO';

procedure RunHidden(const FileName, Params: String);
var
  ResultCode: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ On upgrade, stop and remove the running service first so its files can be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if RegKeyExists(HKLM, ServiceKey) then
  begin
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'stop AIAnalyze');
    Sleep(3000);
    RunHidden(ExpandConstant('{sys}\sc.exe'), 'delete AIAnalyze');
    Sleep(1000);
  end;
end;

{ CPU temperature, power and motherboard fans need the signed PawnIO driver, which is not bundled. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) and (not RegKeyExists(HKLM, PawnIOKey)) then
    MsgBox('AIAnalyze is installed.' + #13#10#13#10 +
      'For CPU temperature, CPU power and motherboard fan readings, install the free, signed PawnIO driver from pawnio.eu, then restart the PC. ' +
      'Everything else works without it.', mbInformation, MB_OK);
end;

{ Collected data is kept unless the user asks to remove it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\AIAnalyze');
    if DirExists(DataDir) and (not UninstallSilent) then
      if MsgBox('Also delete the data AIAnalyze collected on this PC (health history, game sessions, settings, sign-in)?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
