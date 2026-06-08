; =====================================================================
; Inno Setup Compiler Script for Ram Raiders TuneUp Suite
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
; Copy all consolidated portable binaries to destination
Source: "publish\ReleaseSuite\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Launch the GUI upon successful installation completion
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
