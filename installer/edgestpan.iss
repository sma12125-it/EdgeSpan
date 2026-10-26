; EdgeSpan Windows installer. Compile with Inno Setup 6 on a Windows machine
; after scripts/publish-windows.sh has produced publish\win-x64\EdgeSpan.exe.

#define MyAppName "EdgeSpan"
; Pass the version from Directory.Build.props, e.g.
;   iscc /DMyAppVersion=0.0.0 installer\edgestpan.iss  (use scripts/version.sh windows)
#ifndef MyAppVersion
  #error Pass /DMyAppVersion=x.y.z (the <Version> in Directory.Build.props)
#endif
#define MyAppPublisher "EdgeSpan"
#define MyAppExeName "EdgeSpan.exe"

[Setup]
AppId={{8F4B6E2A-1C0D-4A55-9E11-ED6E57A00001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\EdgeSpan
DefaultGroupName=EdgeSpan
DisableProgramGroupPage=yes
OutputDir=..\publish
OutputBaseFilename=EdgeSpan-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
    UninstallDisplayIcon={app}\{#MyAppExeName}
    SetupIconFile=..\src\EdgeSpan.Windows\Assets\EdgeSpan.ico
PrivilegesRequired=lowest

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fa"; MessagesFile: "compiler:Languages\Farsi.isl"

[Tasks]
Name: "startup"; Description: "Start EdgeSpan when I sign in"; GroupDescription: "Startup"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\EdgeSpan"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\EdgeSpan"; Filename: "{app}\{#MyAppExeName}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "EdgeSpan"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch EdgeSpan"; Flags: nowait postinstall skipifsilent
