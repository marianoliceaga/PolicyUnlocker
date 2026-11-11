; Inno Setup 6 script for PolicyUnlocker.
; Normally compiled by scripts/release.ps1, which passes the defines below.
; Manual compile example:
;   ISCC.exe /DAppVersion=1.1.0 /DSourceDir=..\artifacts\publish /DOutputDir=..\artifacts /DRepoRoot=.. PolicyUnlocker.iss

#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef RepoRoot
  #define RepoRoot ".."
#endif

[Setup]
; Keep this AppId constant across releases so upgrades replace the previous install.
AppId={{00AE2B19-0070-4F6D-9450-24DF4E5CBD31}
AppName=PolicyUnlocker
AppVersion={#AppVersion}
AppVerName=PolicyUnlocker {#AppVersion}
AppPublisher=Mariano Liceaga
DefaultDirName={autopf}\PolicyUnlocker
DefaultGroupName=PolicyUnlocker
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=PolicyUnlocker-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\PolicyUnlocker.exe
UninstallDisplayName=PolicyUnlocker
WizardStyle=modern
LicenseFile={#RepoRoot}\LICENSE
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\PolicyUnlocker.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PolicyUnlocker"; Filename: "{app}\PolicyUnlocker.exe"
Name: "{group}\{cm:UninstallProgram,PolicyUnlocker}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PolicyUnlocker"; Filename: "{app}\PolicyUnlocker.exe"; Tasks: desktopicon
