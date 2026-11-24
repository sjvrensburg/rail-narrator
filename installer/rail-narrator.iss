; Rail Narrator's Windows installer (Inno Setup 6). Built by packaging/build-windows.sh when
; Inno Setup's ISCC is available, from the folder that script stages, which it passes as
; /DSOURCE_DIR=<folder>, with /DAPP_VERSION=<version>. Per-user by default, so no administrator
; rights are needed; an all-users install can be chosen in the dialogue.
#ifndef APP_VERSION
  #define APP_VERSION "0.0.0"
#endif
#ifndef SOURCE_DIR
  #error Pass /DSOURCE_DIR=<the staged win-x64 folder>.
#endif
#ifndef OUTPUT_DIR
  #define OUTPUT_DIR "output"
#endif
#define MyAppName "Rail Narrator"
#define MyAppURL "https://github.com/sjvrensburg/rail-narrator"
#define MyAppExeName "rail-narrator.exe"

[Setup]
; Never change AppId: it is how an upgrade finds the installed copy.
AppId={{DDF06607-843C-42C1-A600-6609E5591105}
AppName={#MyAppName}
AppVersion={#APP_VERSION}
AppVerName={#MyAppName} {#APP_VERSION}
AppPublisher=Stefan van Rensburg
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile={#SOURCE_DIR}\LICENSE
OutputDir={#OUTPUT_DIR}
OutputBaseFilename=rail-narrator-setup-x64
SetupIconFile=..\packaging\rail-narrator.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; The application must not be running while its files are replaced.
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "pdfassoc"; Description: "Offer Rail Narrator in ""Open with"" for PDF files"; GroupDescription: "File associations:"

[Files]
Source: "{#SOURCE_DIR}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; "Open with" only: the reader's default PDF program is left as it is.
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "RailNarrator.PDF"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\RailNarrator.PDF"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\RailNarrator.PDF\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: pdfassoc
Root: HKA; Subkey: "Software\Classes\RailNarrator.PDF\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: pdfassoc

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
