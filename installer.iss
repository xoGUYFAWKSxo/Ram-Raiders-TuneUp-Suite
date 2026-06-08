; =====================================================================
; Inno Setup Compiler Script for Ram Raiders TuneUp Suite
; =====================================================================
;
; BUILD PREREQUISITE — populate publish\ReleaseSuite BEFORE compiling this
; script. Publishing TuneUpUI also brings in the referenced TuneUpEngine.exe,
; so both executables land in the same folder (which is what the UI expects
; at runtime — it looks for TuneUpEngine.exe next to TuneUpUI.exe).
;
;   Framework-dependent (small; target PC needs the .NET 11 Desktop Runtime x64):
;     dotnet publish "TuneUpUI\TuneUpUI.csproj" -c Release -o "publish\ReleaseSuite"
;
;   Self-contained (larger; no runtime prerequisite on the target PC):
;     dotnet publish "TuneUpUI\TuneUpUI.csproj" -c Release -r win-x64 ^
;            --self-contained -o "publish\ReleaseSuite"
;
; The InitializeSetup check below warns if the .NET 11 Desktop Runtime is
; missing — relevant only for the framework-dependent publish.
; =====================================================================

#define MyAppName "Ram Raiders TuneUp Suite"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Ram Raiders"
#define MyAppExeName "TuneUpUI.exe"

[Setup]
; Unique GUID for the app uninstaller footprint
AppId={{C7892D95-31AB-48A6-8E3B-3DE24765660D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; The app is x64 .NET only: refuse 32-bit Windows and install into the real
; 64-bit Program Files (this also makes {commonpf} resolve to 64-bit below).
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; Setup file output configurations
OutputDir=publish\Installer
OutputBaseFilename=RamRaidersTuneUpSuite_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
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
Source: "publish\ReleaseSuite\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Launch the GUI upon successful installation completion
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
{ Returns True if a Microsoft.WindowsDesktop.App 11.x shared framework is present.
  Because ArchitecturesInstallIn64BitMode=x64 is set, {commonpf} resolves to the
  64-bit Program Files, where the x64 .NET runtime lives. }
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

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNetDesktop11Installed() then
  begin
    { Fail open: warn the user but let them proceed (e.g. for a self-contained
      build, or if detection misses a non-standard runtime location). }
    if MsgBox('Ram Raiders TuneUp Suite requires the .NET 11 Desktop Runtime (x64),'
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
