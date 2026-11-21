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
Name: "startup"; Description: "Start AIAnalyze with Windows (icon next to the clock, alerts as notifications)"; GroupDescription: "Startup:"

[Files]
; build\app holds the self-contained app (no .NET needed), the UI in wwwroot, and optionally PresentMon.
Source: "..\build\app\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; Earlier versions put browser shortcuts (.url) in these places.
Type: files; Name: "{autoprograms}\{#MyAppName}.url"
Type: files; Name: "{autodesktop}\{#MyAppName}.url"

[Icons]
; The AIAnalyze window app (WebView2) with its tray icon. The service keeps running without it.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\desktop\AIAnalyze.Desktop.exe"; Comment: "Your PC's health, temperatures and games"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\desktop\AIAnalyze.Desktop.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""{app}\AIAnalyze.exe"" start= auto DisplayName= ""AIAnalyze PC Diagnostics"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing the AIAnalyze service..."
Filename: "{sys}\sc.exe"; Parameters: "description {#MyServiceName} ""Checks PC health, samples CPU and GPU sensors, records FPS, and explains problems. UI: {#MyUiUrl}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting AIAnalyze..."
; Register "start with Windows" for the user who ran setup (not the elevated admin account).
Filename: "{app}\desktop\AIAnalyze.Desktop.exe"; Parameters: "--enable-startup --background"; Flags: runasoriginaluser nowait; Tasks: startup
; Open the window. The app waits for the service and shows a message if it isn't answering yet.
Filename: "{app}\desktop\AIAnalyze.Desktop.exe"; Description: "Open AIAnalyze"; Flags: postinstall nowait runasoriginaluser skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM AIAnalyze.Desktop.exe /F"; Flags: runhidden waituntilterminated; RunOnceId: "CloseWindowApp"
Filename: "{sys}\sc.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{cmd}"; Parameters: "/c timeout /t 3 /nobreak >nul"; Flags: runhidden waituntilterminated; RunOnceId: "WaitForStop"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[Code]
const
  ServiceKey = 'SYSTEM\CurrentControlSet\Services\AIAnalyze';
  PawnIOKey = 'SYSTEM\CurrentControlSet\Services\PawnIO';
  WebView2Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure RunHidden(const FileName, Params: String);
var
  ResultCode: Integer;
begin
  Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ On upgrade, close the window app and stop and remove the running service first so files can be replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM AIAnalyze.Desktop.exe /F');
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
  if (CurStep = ssPostInstall) and (not WizardSilent) then
  begin
    { The window uses WebView2 (part of Windows 11 and most Windows 10 PCs). Without it, AIAnalyze still works in the browser. }
    if not RegKeyExists(HKLM, WebView2Key) then
      MsgBox('The AIAnalyze window needs Microsoft Edge WebView2, which is missing on this PC.' + #13#10#13#10 +
        'Install the free "Evergreen" WebView2 Runtime from Microsoft. Until then, open AIAnalyze in your browser at {#MyUiUrl}.',
        mbInformation, MB_OK);
    if not RegKeyExists(HKLM, PawnIOKey) then
      MsgBox('AIAnalyze is installed.' + #13#10#13#10 +
        'For CPU temperature, CPU power and motherboard fan readings, install the free, signed PawnIO driver from pawnio.eu, then restart the PC. ' +
        'Everything else works without it.', mbInformation, MB_OK);
  end;
end;

{ Collected data is kept unless the user asks to remove it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Remove "start with Windows" for the account running the uninstaller; entries for other users point to a missing file and are ignored by Windows. }
    RegDeleteValue(HKCU, RunKey, 'AIAnalyze');
    DataDir := ExpandConstant('{commonappdata}\AIAnalyze');
    if DirExists(DataDir) and (not UninstallSilent) then
      if MsgBox('Also delete the data AIAnalyze collected on this PC (health history, game sessions, settings, sign-in)?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
