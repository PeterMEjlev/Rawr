#define MyAppName "RAWR"
#define MyAppPublisher "RAWR"
#define MyAppExeName "RAWR.exe"

#define MyAppVersion GetEnv("RAWR_VERSION")
#if MyAppVersion == ""
  #define MyAppVersion "0.1.0"
#endif

#define PublishDir GetEnv("RAWR_PUBLISH_DIR")
#if PublishDir == ""
  #define PublishDir "..\artifacts\publish\RAWR-win-x64"
#endif

#define InstallerOutputDir GetEnv("RAWR_INSTALLER_OUTPUT_DIR")
#if InstallerOutputDir == ""
  #define InstallerOutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{D283B9C6-C426-4D80-8AAE-42B1EB54EEB7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename=RAWR-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\Rawr.App\Assets\rawr.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Installer
VersionInfoProductName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
