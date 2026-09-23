; 知债：星穹学途（Knowledge Debt: Astral Path）· Windows 安装程序
; 桌面端 = WebView2 内嵌与 Web 完全一致的 UI
; Inno Setup 7

#define MyAppName "知债：星穹学途"
#define MyAppNameEn "Knowledge Debt: Astral Path"
#define MyAppVersion "1.4.0"
#define MyAppPublisher "知债：星穹学途四人团队"
#define MyAppURL "https://github.com/ClassTechStar/-Knowledge-Debt-Astral-Path"
#define MyAppExeName "AstralPath.Desktop.exe"
#define SetupRoot "AstralPath-Setup"

[Setup]
; 新 GUID + 新安装目录，与 1.3.0 安装包并存、互不覆盖
AppId={{B7E4D2C1-5A6F-48E9-9C3D-14A2B8F0E771}
AppName={#MyAppName}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}.0
VersionInfoDescription=知债：星穹学途 桌面应用（与 Web 端一致）
VersionInfoProductName=知债：星穹学途
VersionInfoCompany={#MyAppPublisher}
DefaultDirName={autopf}\AstralPath-1.4
DefaultGroupName={#MyAppName} 1.4
DisableProgramGroupPage=yes
OutputDir=dist
OutputBaseFilename=AstralPath-Setup-{#MyAppVersion}-Desktop
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\bin\{#MyAppExeName}
SetupIconFile=
InfoBeforeFile={#SetupRoot}\docs\README-安装说明.md
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："
Name: "webview2"; Description: "安装 Microsoft Edge WebView2 运行时（桌面 UI 依赖，Win10 必需）"; GroupDescription: "系统依赖："

[Files]
Source: "{#SetupRoot}\bin\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SetupRoot}\api\*"; DestDir: "{app}\api"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SetupRoot}\web\*"; DestDir: "{app}\web"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SetupRoot}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SetupRoot}\MicrosoftEdgeWebView2Setup.exe"; DestDir: "{tmp}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\bin\{#MyAppExeName}"; WorkingDir: "{app}\bin"; Comment: "与 Web 端一致的桌面应用"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\bin\{#MyAppExeName}"; WorkingDir: "{app}\bin"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebView2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 WebView2 运行时…"; Flags: skipifdoesntexist runhidden waituntilterminated; Tasks: webview2
Filename: "{app}\bin\{#MyAppExeName}"; Description: "启动 知债：星穹学途"; WorkingDir: "{app}\bin"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
Type: filesandordirs; Name: "{app}\api\materials-uploads"
Type: filesandordirs; Name: "{localappdata}\AstralPath\WebView2"
