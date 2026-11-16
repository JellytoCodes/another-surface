#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir is required
#endif

[Setup]
AppId={{6C2E6AF4-2043-4A70-B54B-50D6B8A6826F}
AppName=Another Surface
AppVersion={#AppVersion}
AppPublisher=JellytoCodes
AppPublisherURL=https://github.com/JellytoCodes/another-surface
DefaultDirName={localappdata}\Programs\Another Surface
DefaultGroupName=Another Surface
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#InstallerOutputDir}
OutputBaseFilename=AnotherSurface-{#AppVersion}-Setup-x64
SetupIconFile=..\src\AnotherSurface.App\Assets\Brand\another-surface-app.ico
UninstallDisplayIcon={app}\AnotherSurface.exe
AppMutex=Local\Vellume.Desktop.SingleInstance
SetupMutex=Local\AnotherSurface.Setup
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Another Surface"; Filename: "{app}\AnotherSurface.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Another Surface"; Filename: "{app}\AnotherSurface.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AnotherSurface.exe"; Description: "{cm:LaunchProgram,Another Surface}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  { Keep the media library and settings. Only remove autostart owned by this installation. }
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Another Surface', Command) then
      if CompareText(Command, ExpandConstant('"{app}\AnotherSurface.exe" --windows-startup')) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Another Surface');
end;
