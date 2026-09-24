; 知债：星穹学途 · 单体版（无微服务）安装包
; 与旧版并存：新 AppId + 新安装目录
#define MyAppName "知债：星穹学途"
#define MyAppNameEn "Knowledge Debt: Astral Path"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "知债：星穹学途四人团队"
#define MyAppURL "https://github.com/ClassTechStar/-Knowledge-Debt-Astral-Path"
#define MyAppExeName "AstralPath.Monolith.exe"

[Setup]
AppId={{E1B2C3D4-8F9A-4B5C-6D7E-112233445566}
AppName={#MyAppName}（单体版）
AppVerName={#MyAppName} {#MyAppVersion} 单体版
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}.0
VersionInfoDescription=知债：星穹学途 单体版（无微服务）
VersionInfoProductName=知债：星穹学途 单体版
VersionInfoCompany={#MyAppPublisher}
DefaultDirName={autopf}\AstralPath-Monolith
DefaultGroupName={#MyAppName} 单体版
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
UninstallDisplayName={#MyAppName} 单体版
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
Name: "{group}\{#MyAppName} 单体版"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#MyAppName} 单体版"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName} 单体版"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动知债单体版"; Flags: nowait postinstall skipifsilent
