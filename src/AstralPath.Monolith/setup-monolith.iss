; 知债：星穹学途 · 单体版 2.1（无微服务）
#define MyAppName "知债：星穹学途"
#define MyAppVersion "2.1.0"
#define MyAppPublisher "知债：星穹学途四人团队"
#define MyAppURL "https://github.com/ClassTechStar/-Knowledge-Debt-Astral-Path"
#define MyAppExeName "AstralPath.Monolith.exe"

[Setup]
AppId={{F2B3C4D5-9G0A-4B1C-2D3E-445566778899}
AppName={#MyAppName}（单体版 2.1）
AppVerName={#MyAppName} {#MyAppVersion} 单体版
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoDescription=知债：星穹学途 单体版 2.1（无微服务）
DefaultDirName={autopf}\AstralPath-Monolith-2.1
DefaultGroupName={#MyAppName} 单体版 2.1
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=AstralPath-Monolith-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
UninstallDisplayName={#MyAppName} 单体版 2.1
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=Resources\astralpath-icon.ico
CloseApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName} 单体版 2.1"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName} 单体版 2.1"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动知债单体版 2.1"; Flags: nowait postinstall skipifsilent
