; 知债：星穹学途（Knowledge Debt: Astral Path）· Windows 安装包
; Inno Setup 7 · 生成 AstralPath-Setup-1.3.0.exe

#define MyAppName "知债：星穹学途"
#define MyAppNameEn "Knowledge Debt: Astral Path"
#define MyAppVersion "1.3.0"
#define MyAppPublisher "知债：星穹学途四人团队"
#define MyAppExeName "AstralPath.Desktop.exe"
#define MyApiExeName "AstralPath.Api.exe"
#define SetupRoot "AstralPath-Setup"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF0123456789}
AppName={#MyAppName}
AppVerName={#MyAppName} {#MyAppVersion}（{#MyAppNameEn}）
AppPublisher={#MyAppPublisher}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\AstralPath
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename=AstralPath-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\bin\{#MyAppExeName}
DisableDirPage=no
InfoBeforeFile={#SetupRoot}\docs\README-安装说明.md

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："
Name: "webappicon"; Description: "创建 Web 演示台快捷方式"; GroupDescription: "附加图标："
Name: "associatepdf"; Description: "将 PDF 添加到「用知债打开」右键菜单（可选）"; GroupDescription: "文件关联："

[Files]
; 桌面端（Avalonia）
Source: "{#SetupRoot}\bin\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs
; 本地 API + 演示 Web
Source: "{#SetupRoot}\api\*"; DestDir: "{app}\api"; Flags: ignoreversion recursesubdirs createallsubdirs
; 离线演示页
Source: "{#SetupRoot}\web\*"; DestDir: "{app}\web"; Flags: ignoreversion recursesubdirs createallsubdirs
; 文档
Source: "{#SetupRoot}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
; 启动/卸载辅助脚本
Source: "{#SetupRoot}\启动-Web演示台.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SetupRoot}\启动-桌面客户端.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SetupRoot}\安装.bat"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName} · 桌面客户端"; Filename: "{app}\bin\{#MyAppExeName}"; Comment: "知债：星穹学途桌面端"
Name: "{group}\{#MyAppName} · Web 演示台"; Filename: "{app}\启动-Web演示台.bat"; Comment: "启动本地 API 并打开演示 UI（http://127.0.0.1:5190）"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\bin\{#MyAppExeName}"; Tasks: desktopicon
Name: "{autodesktop}\{#MyAppName} Web 演示台"; Filename: "{app}\启动-Web演示台.bat"; Tasks: webappicon

[Registry]
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\AstralPath"; ValueType: string; ValueData: "用知债：星穹学途打开"; Flags: uninsdeletekey; Tasks: associatepdf
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.pdf\shell\AstralPath\command"; ValueType: string; ValueData: """{app}\bin\{#MyAppExeName}"" ""%1"""; Tasks: associatepdf

[Run]
Filename: "{app}\启动-Web演示台.bat"; Description: "启动 Web 演示台（本地 API）"; Flags: nowait postinstall skipifsilent; Components: 
Filename: "{app}\bin\{#MyAppExeName}"; Description: "启动桌面客户端"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
Type: filesandordirs; Name: "{app}\api\materials-uploads"
Type: filesandordirs; Name: "{app}\logs"
