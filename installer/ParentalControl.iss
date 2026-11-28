; ParentalControl Inno Setup Installer
; Requires Inno Setup 6.2+ (https://jrsoftware.org/isdownload.php)

#define MyAppName      "Parental Control"
#define MyAppVersion   "1.5.0"
#define MyAppPublisher "ParentalControl"
#define MyAppExeName   "ParentalControl.Admin.exe"
#define ServiceExeName "ParentalControl.Service.exe"
#define ServiceName    "ParentalControl.Service"
#define AgentExeName   "ParentalControl.Agent.exe"
#define ProbeExeName   "ParentalControl.Probe.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={commonpf}\ParentalControl
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=ParentalControlSetup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\admin\{#MyAppExeName}
SetupIconFile=..\icon.ico
CloseApplications=force
; request-window D7: Log() writes the result of the WebView2 Runtime bootstrapper. Without this, Log() writes nothing.
; The log is "Setup Log <date> #<n>.txt" in the temp folder of the user, or the file of /LOG= (the updater uses it).
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Service files
Source: "..\publish\service\*"; DestDir: "{app}\service"; Flags: ignoreversion recursesubdirs createallsubdirs

; Admin app files
Source: "..\publish\admin\*"; DestDir: "{app}\admin"; Flags: ignoreversion recursesubdirs createallsubdirs
; Agent files
Source: "..\publish\agent\*"; DestDir: "{app}\agent"; Flags: ignoreversion recursesubdirs createallsubdirs
; The Session Probe (session-probe-and-agent-channel, design D2). The service starts it as SYSTEM in the session of the
; child. Only administrators can change {app}\probe.
Source: "..\publish\probe\*"; DestDir: "{app}\probe"; Flags: ignoreversion recursesubdirs createallsubdirs
; request-window D7: the Evergreen WebView2 Runtime bootstrapper (build.ps1 downloads it and checks its signature).
; Not installed: [Code] extracts it only if the runtime is missing, and runs it after the service start.
Source: "..\publish\redist\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Registry]
; session-probe-and-agent-channel D9: the service is the only process that starts the Session Agent. Delete the Run value
; of an older install (up to 1.5.0), also on an update. An agent that the Run value starts has no channel and exits.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ParentalControlAgent"; Flags: deletevalue uninsdeletevalue
; The service writes the state of the data folder here (require-admin-sign-in). Remove it at uninstall.
Root: HKLM; Subkey: "Software\ParentalControl"; Flags: uninsdeletekey dontcreatekey

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\admin\{#MyAppExeName}"; \
    IconFilename: "{app}\admin\{#MyAppExeName}"; Comment: "Launch Parental Control Admin"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\admin\{#MyAppExeName}"; \
    IconFilename: "{app}\admin\{#MyAppExeName}"; Comment: "Launch Parental Control Admin"

[Run]
Filename: "{app}\admin\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; Stop the service first: it starts the agent and the Session Probe, and it ends each probe at its stop. "net stop" waits
; until the service is stopped. Then end each agent and probe that is left, and delete the service before files are removed.
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; \
    RunOnceId: "StopService"; Flags: runhidden waituntilterminated
Filename: "taskkill"; Parameters: "/F /IM {#AgentExeName}"; \
    RunOnceId: "StopAgent"; Flags: runhidden waituntilterminated
Filename: "taskkill"; Parameters: "/F /IM {#ProbeExeName}"; \
    RunOnceId: "StopProbe"; Flags: runhidden waituntilterminated
Filename: "sc"; Parameters: "delete {#ServiceName}"; \
    RunOnceId: "DeleteService"; Flags: runhidden waituntilterminated

[UninstallDelete]
; Remove desktop shortcut
Type: files; Name: "{commondesktop}\{#MyAppName}.lnk"
; The data folder (logs, database) is deleted in [Code] (CurUninstallStepChanged), only after a check that it is a
; real folder that SYSTEM or Administrators owns (require-admin-sign-in, S4 of the second review).
; Remove entire install directory (Program Files\ParentalControl)
Type: filesandordirs; Name: "{app}"

[Code]
function ServiceExists(): Boolean;
var
    ResultCode: Integer;
begin
    Result := Exec('sc', ExpandConstant('query {#ServiceName}'),
                   '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
              and (ResultCode = 0);
end;

procedure StopService();
var
    ResultCode: Integer;
begin
    Exec('sc', ExpandConstant('stop {#ServiceName}'),
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
end;

procedure UpdateService();
var
    ResultCode: Integer;
    BinPath: String;
begin
    BinPath := ExpandConstant('"{app}\service\{#ServiceExeName}"');
    Exec('sc', ExpandConstant('config {#ServiceName}')
         + ' binPath= ' + BinPath
         + ' start= auto',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc', ExpandConstant('failure {#ServiceName}')
         + ' reset= 86400 actions= restart/10000/restart/30000/restart/60000',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // require-admin-sign-in SF2: run the restart actions also when the service reports a stop with an error code.
    Exec('sc', ExpandConstant('failureflag {#ServiceName} 1'),
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CreateService();
var
    ResultCode: Integer;
    BinPath: String;
begin
    BinPath := ExpandConstant('"{app}\service\{#ServiceExeName}"');

    Exec('sc', ExpandConstant('create {#ServiceName}')
         + ' binPath= ' + BinPath
         + ' start= auto'
         + ' obj= LocalSystem'
         + ' DisplayName= "Parental Control Service"',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Set description
    Exec('sc', ExpandConstant('description {#ServiceName}')
         + ' "Monitors and enforces parental control time limits."',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // Configure service to restart on failure (10s, 30s, 60s)
    Exec('sc', ExpandConstant('failure {#ServiceName}')
         + ' reset= 86400 actions= restart/10000/restart/30000/restart/60000',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    // require-admin-sign-in SF2: run the restart actions also when the service reports a stop with an error code.
    Exec('sc', ExpandConstant('failureflag {#ServiceName} 1'),
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure StartService();
var
    ResultCode: Integer;
    Retries: Integer;
    Started: Boolean;
begin
    Started := False;
    Sleep(2000);  // Let SCM finish registering the service

    for Retries := 1 to 3 do
    begin
        Log(Format('StartService: attempt %d of 3', [Retries]));
        if Exec('sc', ExpandConstant('start {#ServiceName}'),
                '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        begin
            if (ResultCode = 0) or (ResultCode = 1056) then  // 1056 = already running
            begin
                Log('StartService: service started successfully');
                Started := True;
                Break;
            end;
            Log(Format('StartService: sc start returned %d', [ResultCode]));
        end
        else
            Log('StartService: Exec call itself failed');

        if Retries < 3 then
            Sleep(1000 + Retries * 1000);  // Escalating: 2s, 3s
    end;

    if not Started then
        MsgBox('The Parental Control service could not be started automatically. '
               + 'It will start after the next reboot, or you can start it manually '
               + 'from the Services console (services.msc).',
               mbInformation, MB_OK);
end;

// request-window D7: the machine-wide Evergreen WebView2 Runtime. The setup runs in 64-bit mode, so a plain HKLM reads
// the 64-bit view; the key is in the 32-bit view (WOW6432Node). Present: pv is not empty and not 0.0.0.0.
const
    WebView2ClientKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
    WebView2Bootstrapper = 'MicrosoftEdgeWebview2Setup.exe';

function IsWebView2RuntimeInstalled(): Boolean;
var
    Version: String;
begin
    Result := RegQueryStringValue(HKLM32, WebView2ClientKey, 'pv', Version)
              and (Version <> '') and (Version <> '0.0.0.0');
    if Result then
        Log('WebView2 Runtime: version ' + Version)
    else
        Log('WebView2 Runtime: not installed');
end;

// Runs after StartService(), so the limits work while the bootstrapper downloads the runtime. A failure (for example
// offline) does not stop the setup; the log has the result and the exit code, and the service writes a warning event
// 15 minutes after its start if the runtime is still missing.
procedure InstallWebView2RuntimeIfMissing();
var
    ResultCode: Integer;
begin
    if IsWebView2RuntimeInstalled() then
        Exit;
    try
        ExtractTemporaryFile(WebView2Bootstrapper);
    except
        Log('WebView2 Runtime: the bootstrapper could not be extracted: ' + GetExceptionMessage());
        Exit;
    end;
    Log('WebView2 Runtime: running the bootstrapper /silent /install');
    if Exec(ExpandConstant('{tmp}\' + WebView2Bootstrapper), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
        Log(Format('WebView2 Runtime: the bootstrapper ended with exit code %d', [ResultCode]))
    else
        Log(Format('WebView2 Runtime: the bootstrapper did not start: error %d (%s)', [ResultCode, SysErrorMessage(ResultCode)]));
    if not IsWebView2RuntimeInstalled() then
        Log('WebView2 Runtime: still missing; the Request Window shows its fallback text');
end;

procedure StopRunningProcesses();
var
    ResultCode: Integer;
begin
    Exec('taskkill', ExpandConstant('/F /T /IM {#AgentExeName}'),
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('taskkill', ExpandConstant('/F /T /IM {#MyAppExeName}'),
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
end;

// The service ends each Session Probe at its stop (design D2). This ends a probe that is left, so that the setup can
// replace the files in {app}\probe. Call it after StopService().
procedure StopProbes();
var
    ResultCode: Integer;
begin
    Exec('taskkill', ExpandConstant('/F /IM {#ProbeExeName}'),
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
    StopRunningProcesses();
    if ServiceExists() then
        StopService();
    StopProbes();
    Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
    if CurStep = ssInstall then
    begin
        StopRunningProcesses();
        if ServiceExists() then
            StopService();
        StopProbes();
    end
    else if CurStep = ssPostInstall then
    begin
        // After files are copied: create if new or update if existing, then start
        if not ServiceExists() then
            CreateService()
        else
            UpdateService();
        StartService();
        // Not a [Run] entry: Inno Setup runs those before ssPostInstall, while the service is still stopped.
        InstallWebView2RuntimeIfMissing();
    end;
end;

// The service can make a new data folder with a random name (require-admin-sign-in D14) and stores its location in
// HKLM\Software\ParentalControl\DataFolderPath. Read it before the uninstall removes the key, and delete the folder
// after the service is gone. Only a name of the form {commonappdata}\ParentalControl-<32 hex digits> is deleted.
// S4: the uninstaller deletes a data folder (the default one or a random one) only if it is a real folder, not a
// junction or a symbolic link, and SYSTEM or Administrators owns it. A standard user can make a folder or a link with
// the name before the install; the uninstaller then leaves it alone.
var
    RelocatedDataFolder: String;

function GetFileAttributesW(lpFileName: String): Cardinal;
    external 'GetFileAttributesW@kernel32.dll stdcall';

function IsServiceDataFolder(Path: String): Boolean;
var
    Prefix: String;
    I: Integer;
    C: Char;
begin
    Prefix := ExpandConstant('{commonappdata}\ParentalControl-');
    Result := (Length(Path) = Length(Prefix) + 32) and (CompareText(Copy(Path, 1, Length(Prefix)), Prefix) = 0);
    if Result then
        for I := Length(Prefix) + 1 to Length(Path) do
        begin
            C := Path[I];
            if not (((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'f')) or ((C >= 'A') and (C <= 'F'))) then
                Result := False;
        end;
end;

// True only if Path is a real folder (no reparse point) and its owner is SYSTEM (S-1-5-18) or Administrators
// (S-1-5-32-544). Each error gives False: the folder then stays.
function IsSafeToDelete(Path: String): Boolean;
var
    Attributes: Cardinal;
    ResultCode: Integer;
    Command: String;
begin
    Result := False;
    Attributes := GetFileAttributesW(Path);
    if Attributes = $FFFFFFFF then
        Exit; // missing
    if (Attributes and $10) = 0 then
    begin
        Log('Uninstall: ' + Path + ' is not a folder. It stays.');
        Exit;
    end;
    if (Attributes and $400) <> 0 then
    begin
        Log('Uninstall: ' + Path + ' is a junction or a symbolic link. It stays.');
        Exit;
    end;
    if Pos('''', Path) > 0 then
        Exit;
    Command := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
        '$p=''' + Path + '''; $i=Get-Item -LiteralPath $p -Force -ErrorAction Stop; ' +
        'if ((($i.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or (-not $i.PSIsContainer)) { exit 3 }; ' +
        '$o=(Get-Acl -LiteralPath $p -ErrorAction Stop).GetOwner([Security.Principal.SecurityIdentifier]).Value; ' +
        'if (($o -eq ''S-1-5-18'') -or ($o -eq ''S-1-5-32-544'')) { exit 0 } else { exit 4 }"';
    if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
       and (ResultCode = 0) then
        Result := True
    else
        Log(Format('Uninstall: the owner check of %s failed (code %d). It stays.', [Path, ResultCode]));
end;

procedure DeleteDataFolder(Path: String);
begin
    if (Path <> '') and IsSafeToDelete(Path) then
        DelTree(Path, True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
    Value: String;
begin
    if CurUninstallStep = usUninstall then
    begin
        RelocatedDataFolder := '';
        if RegQueryStringValue(HKLM64, 'Software\ParentalControl', 'DataFolderPath', Value) and IsServiceDataFolder(Value) then
            RelocatedDataFolder := Value;
    end
    else if CurUninstallStep = usPostUninstall then
    begin
        DeleteDataFolder(ExpandConstant('{commonappdata}\ParentalControl'));
        DeleteDataFolder(RelocatedDataFolder);
    end;
end;
