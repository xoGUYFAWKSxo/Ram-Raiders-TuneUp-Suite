; =====================================================================
; Inno Setup Compiler Script for Galactic Tune-Up
; =====================================================================
;
; BUILD PREREQUISITE — populate publish\ReleaseSuite BEFORE compiling this
; script. Publishing TuneUpUI also brings in the referenced TuneUpEngine.exe,
; so both executables land in the same folder (which is what the UI expects
; at runtime — it looks for TuneUpEngine.exe next to TuneUpUI.exe).
;
;   Framework-dependent (small; target PC needs the .NET 11 Desktop Runtime x64):
;   Run build-installer.ps1, which publishes BOTH projects into publish\ReleaseSuite,
;   checks every engine dependency is present, then compiles this script. (Publishing
;   only TuneUpUI drops DLLs the engine needs, such as PerformanceCounter, because the
;   UI's Desktop runtime already contains them and the engine's runtime does not.)
;
;     dotnet publish "TuneUpUI\TuneUpUI.csproj" -c Release -o "publish\ReleaseSuite"
;     dotnet publish "TuneUpEngine\TuneUpEngine.csproj" -c Release -o "publish\ReleaseSuite"
;
;   Self-contained (larger; no runtime prerequisite on the target PC):
;     dotnet publish "TuneUpUI\TuneUpUI.csproj" -c Release -r win-x64 ^
;            --self-contained -o "publish\ReleaseSuite"
;
; The InitializeSetup check below warns if the .NET 11 Desktop Runtime is
; missing — relevant only for the framework-dependent publish.
; =====================================================================

#define MyAppName "Galactic Tune-Up"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "xoGUYFAWKSxo"
#define MyAppExeName "TuneUpUI.exe"
#define EngineExeName "TuneUpEngine.exe"
#define ServiceName "TuneUpEngineService"
; The name before 2026-10-16. Upgrades move out of its folders (see [InstallDelete]).
#define LegacyAppName "Ram Raiders TuneUp Suite"

[Setup]
; Unique GUID for the app uninstaller footprint
AppId={{C7892D95-31AB-48A6-8E3B-3DE24765660D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
; Upgrades from the old name install into the new folder, not the old one.
UsePreviousAppDir=no
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The app is x64 .NET only: refuse 32-bit Windows and install into the real
; 64-bit Program Files (this also makes {commonpf} resolve to 64-bit below).
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; Setup file output configurations
OutputDir=publish\Installer
OutputBaseFilename=GalacticTuneUp_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardImageFile=assets\wizard-large-100.bmp,assets\wizard-large-200.bmp
WizardSmallImageFile=assets\wizard-small-100.bmp,assets\wizard-small-200.bmp
SetupIconFile=TuneUpIcon.ico
; Force installer to prompt for administrative elevation on run
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Copy all consolidated portable binaries to destination. Debug symbols (*.pdb)
; are excluded — they are not needed by an installed release build.
Source: "publish\ReleaseSuite\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Upgrading from the old name: remove its program folder and shortcuts. The engine
; moves its ProgramData folder (the change journal) to the new name when it starts.
Type: filesandordirs; Name: "{commonpf}\{#LegacyAppName}"
Type: filesandordirs; Name: "{commonprograms}\{#LegacyAppName}"
Type: files; Name: "{commondesktop}\{#LegacyAppName}.lnk"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; ── Engine service ──────────────────────────────────────────────────────
; The engine runs as an automatically starting Windows service (LocalSystem), so
; the app no longer needs a UAC prompt on every launch or sign-in, and keeps
; working in the background after the window is closed. "create" fails harmlessly
; on an upgrade (the service already exists); "config" then refreshes its path.
Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""\""{app}\{#EngineExeName}\"" --service"" start= auto DisplayName= ""Galactic Tune-Up Engine"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing the TuneUp engine service..."
Filename: "{sys}\sc.exe"; Parameters: "config {#ServiceName} binPath= ""\""{app}\{#EngineExeName}\"" --service"" start= auto DisplayName= ""Galactic Tune-Up Engine"""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Galactic Tune-Up background engine (memory, ProBalance, Game Mode and system maintenance)."""; Flags: runhidden waituntilterminated
; Restart automatically if it ever stops unexpectedly.
Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000"; Flags: runhidden waituntilterminated
; Windows' default service permissions, plus "start" (RP) for interactive users
; (IU), so the UI can start the engine if it was stopped — without admin rights.
; Stopping or reconfiguring it still requires an administrator.
Filename: "{sys}\sc.exe"; Parameters: "sdset {#ServiceName} D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)"; Flags: runhidden waituntilterminated
Filename: "{sys}\net.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Starting the TuneUp engine..."

; Launch the GUI upon successful installation completion — as the user who ran
; setup, NOT elevated. (Setup itself runs as administrator; without
; runasoriginaluser the UI inherited that and wrote to the admin's settings.)
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Close every open TuneUp window first: a running TuneUpUI.exe keeps its files
; locked, so the program folder was left behind. Then stop the engine (net.exe
; waits until it has stopped) and end any engine process still running, so none
; outlives its deleted service holding the pipe (found in installer testing).
; Only then undo every system change in the change journal and remove the service.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#MyAppExeName}"; Flags: runhidden waituntilterminated; RunOnceId: "CloseUI"
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopEngine"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#EngineExeName}"; Flags: runhidden waituntilterminated; RunOnceId: "EndEngine"
Filename: "{app}\{#EngineExeName}"; Parameters: "--revert-all"; Flags: runhidden waituntilterminated; RunOnceId: "RevertAll"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[UninstallDelete]
; The change journal, engine settings and machine-wide backups (ProgramData).
Type: filesandordirs; Name: "{commonappdata}\GalacticTuneUp"
Type: filesandordirs; Name: "{commonappdata}\RamRaidersTuneUpSuite"

[Code]
(* Returns True if a Microsoft.WindowsDesktop.App 11.x shared framework is present.
   Because ArchitecturesInstallIn64BitMode=x64 is set, {commonpf} resolves to the
   64-bit Program Files, where the x64 .NET runtime lives. (A brace comment would end
   at the brace after "commonpf".) *)
function IsDotNetDesktop11Installed(): Boolean;
var
  FindRec: TFindRec;
  RuntimePath: String;
begin
  Result := False;
  RuntimePath := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(RuntimePath) then
  begin
    if FindFirst(RuntimePath + '\11.*', FindRec) then
    begin
      try
        Result := True;
      finally
        FindClose(FindRec);
      end;
    end;
  end;
end;

{ Before files are copied on an upgrade, stop the running engine service so its
  executable can be replaced. Harmless on a first install (no service yet). }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    { A window open from the old-name folder would keep it locked, so it couldn't be removed. }
    if DirExists(ExpandConstant('{commonpf}\{#LegacyAppName}')) then
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNetDesktop11Installed() then
  begin
    { Fail open: warn the user but let them proceed (e.g. for a self-contained
      build, or if detection misses a non-standard runtime location). }
    if MsgBox('Galactic Tune-Up requires the .NET 11 Desktop Runtime (x64),'
              + #13#10 + 'which does not appear to be installed on this PC.'
              + #13#10 + #13#10
              + 'Download it from:' + #13#10
              + 'https://dotnet.microsoft.com/download/dotnet/11.0'
              + #13#10 + '(choose ".NET Desktop Runtime", x64).'
              + #13#10 + #13#10
              + 'Continue with installation anyway?',
              mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;
