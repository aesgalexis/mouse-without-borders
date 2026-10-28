#define AppName "Mouse, with out borders"
#ifndef AppVersion
  #define AppVersion "0.1.13"
#endif

[Setup]
AppId={{BAA10551-2B65-4E83-B102-1F184AE6C673}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Mouse, with out borders
AppPublisherURL=https://github.com/aesgalexis/mouse-without-borders
AppSupportURL=https://github.com/aesgalexis/mouse-without-borders/issues
DefaultDirName={localappdata}\Programs\MouseWithOutBorders
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\app-{#AppVersion}.ico
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
OutputDir=..\dist\installer
OutputBaseFilename=MouseWithOutBorders-{#AppVersion}-Setup
SetupIconFile=..\MouseWithOutBorders\assets\app.ico
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
; Shortcut creation is opt-in on every run, including upgrades. Reusing a past
; task selection would recreate a shortcut that the user has since deleted.
UsePreviousTasks=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\app\MouseWithOutBorders.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\MouseWithOutBorders\assets\app.ico"; DestDir: "{app}"; DestName: "app-{#AppVersion}.ico"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\MouseWithOutBorders.exe"; IconFilename: "{app}\app-{#AppVersion}.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\MouseWithOutBorders.exe"; IconFilename: "{app}\app-{#AppVersion}.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\MouseWithOutBorders.exe"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent; Check: not IsAppUpdate
Filename: "{app}\MouseWithOutBorders.exe"; Flags: nowait; Check: IsAppUpdate

[Code]
procedure RefreshDesktopIcons;
var
  Entry: TFindRec;
  Shell, Shortcut: Variant;
  ShortcutPath, AppPath: String;
begin
  AppPath := ExpandConstant('{app}\MouseWithOutBorders.exe');
  Shell := CreateOleObject('WScript.Shell');
  if FindFirst(ExpandConstant('{autodesktop}\*.lnk'), Entry) then
  begin
    try
      repeat
        try
          ShortcutPath := ExpandConstant('{autodesktop}\') + Entry.Name;
          Shortcut := Shell.CreateShortcut(ShortcutPath);
          if CompareText(Shortcut.TargetPath, AppPath) = 0 then
          begin
            Shortcut.IconLocation := ExpandConstant('{app}\app-{#AppVersion}.ico') + ',0';
            Shortcut.Save;
            Log('Updated desktop shortcut icon: ' + Entry.Name);
          end;
        except
          Log('Could not refresh desktop shortcut: ' + GetExceptionMessage);
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RefreshDesktopIcons;
end;

function IsAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:MWBUPDATE|0}') = '1';
end;

function RegisterWindowMessage(MessageName: String): Cardinal;
  external 'RegisterWindowMessageW@user32.dll stdcall';
function GetTopWindow(Parent: HWND): HWND;
  external 'GetTopWindow@user32.dll stdcall';
function GetWindow(Window: HWND; Command: Cardinal): HWND;
  external 'GetWindow@user32.dll stdcall';
function GetWindowThreadProcessId(Window: HWND; var ProcessId: Cardinal): Cardinal;
  external 'GetWindowThreadProcessId@user32.dll stdcall';
function GetCurrentProcessId: Cardinal;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function ProcessIdToSessionId(ProcessId: Cardinal; var SessionId: Cardinal): Boolean;
  external 'ProcessIdToSessionId@kernel32.dll stdcall';
function OpenProcess(Access: Cardinal; InheritHandle: Boolean; ProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function GetExitCodeProcess(Process: THandle; var ExitCode: Cardinal): Boolean;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var
  TerminatedInstanceFound: Boolean;

function IsLiveProcess(ProcessId: Cardinal): Boolean;
var
  Handle: THandle;
  ExitCode: Cardinal;
begin
  Result := True; { Fail closed when process state cannot be inspected. }
  Handle := OpenProcess($1000, False, ProcessId); { QUERY_LIMITED_INFORMATION }
  if Handle = 0 then
    exit;
  try
    if GetExitCodeProcess(Handle, ExitCode) then
      Result := ExitCode = 259; { STILL_ACTIVE }
  finally
    CloseHandle(Handle);
  end;
end;

procedure RequestMwbExit(ProcessId: Cardinal);
var
  Window, NextWindow: HWND;
  WindowProcessId, ExitMessage: Cardinal;
begin
  ExitMessage := RegisterWindowMessage('MouseWithOutBorders.ExitForUpdate.79DBA1BD');
  if ExitMessage = 0 then
    exit;

  { Include hidden windows: closing to the notification area is not exiting. }
  Window := GetTopWindow(0);
  while Window <> 0 do
  begin
    NextWindow := GetWindow(Window, 2); { GW_HWNDNEXT }
    WindowProcessId := 0;
    GetWindowThreadProcessId(Window, WindowProcessId);
    if WindowProcessId = ProcessId then
      PostMessage(Window, ExitMessage, 0, 0);
    Window := NextWindow;
  end;
end;

function IsTargetProcess(Process: Variant; AppPath: String): Boolean;
begin
  Result := False;
  if not VarIsNull(Process.ExecutablePath) then
    if CompareText(Process.ExecutablePath, AppPath) = 0 then
    begin
      Result := IsLiveProcess(Process.ProcessId);
      if not Result then
        TerminatedInstanceFound := True;
    end;
end;

function CountTargetProcesses(Services: Variant; Query, AppPath: String): Integer;
var
  Processes, Process: Variant;
  Index: Integer;
begin
  Result := 0;
  Processes := Services.ExecQuery(Query);
  for Index := 0 to Processes.Count - 1 do
  begin
    Process := Processes.ItemIndex(Index);
    if IsTargetProcess(Process, AppPath) then
      Result := Result + 1;
  end;
end;

function CloseRunningMwb: String;
var
  Locator, Services, Processes, Process: Variant;
  AppPath, Query: String;
  SessionId: Cardinal;
  Index, Attempt, TerminateResult: Integer;
begin
  Result := '';
  if not CheckForMutexes('Local\MouseWithOutBorders-79DBA1BD') then
    exit;
  try
    if not ProcessIdToSessionId(GetCurrentProcessId, SessionId) then
      RaiseException('Cannot determine the current Windows session.');
    AppPath := ExpandConstant('{app}\MouseWithOutBorders.exe');
    Query := 'SELECT * FROM Win32_Process WHERE Name = ''MouseWithOutBorders.exe''' +
      ' AND SessionId = ' + IntToStr(SessionId);
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery(Query);
    for Index := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(Index);
      if IsTargetProcess(Process, AppPath) then
      begin
        Log('Requesting MWB exit before replacing its files.');
        RequestMwbExit(Process.ProcessId);
      end;
    end;

    for Attempt := 1 to 30 do
    begin
      if CountTargetProcesses(Services, Query, AppPath) = 0 then
        exit;
      Sleep(100);
    end;

    { Older versions do not understand the exit message. Scope the fallback to
      this installation and Windows session, rather than killing by image name. }
    Processes := Services.ExecQuery(Query);
    for Index := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(Index);
      if IsTargetProcess(Process, AppPath) then
      begin
        Log('Closing an older or unresponsive MWB process.');
        TerminateResult := Process.Terminate(0);
        if TerminateResult <> 0 then
          Log(Format('MWB termination returned %d.', [TerminateResult]));
      end;
    end;

    for Attempt := 1 to 30 do
    begin
      if CountTargetProcesses(Services, Query, AppPath) = 0 then
        exit;
      Sleep(100);
    end;
    Result := 'MWB could not be closed. Exit it from the notification area and try again.';
  except
    Log('Cannot close MWB: ' + GetExceptionMessage);
    Result := 'Cannot close MWB automatically: ' + GetExceptionMessage;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  AppPath, RetiredPath: String;
begin
  TerminatedInstanceFound := False;
  Result := CloseRunningMwb;
  { Windows may retain an image mapping while terminated kernel threads drain.
    Move that image aside so Setup can write the new EXE without a reboot. }
  if (Result = '') and TerminatedInstanceFound then
  begin
    AppPath := ExpandConstant('{app}\MouseWithOutBorders.exe');
    RetiredPath := ExpandConstant('{app}\MouseWithOutBorders-retired-{#AppVersion}.exe');
    if FileExists(AppPath) then
    begin
      if FileExists(RetiredPath) then
        DeleteFile(RetiredPath);
      if not RenameFile(AppPath, RetiredPath) then
        Result := 'The previous application image is still locked by Windows. Restart Windows once, then retry this update.';
    end;
  end;
end;

function InitializeUninstall: Boolean;
var
  ErrorMessage: String;
begin
  ErrorMessage := CloseRunningMwb;
  Result := ErrorMessage = '';
  if not Result then
    SuppressibleMsgBox(ErrorMessage, mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'MouseWithOutBorders');
end;
