#define MyAppName "AI Frontier"
#define MyAppPublisher "AI Frontier contributors"
#define MyAppURL "https://github.com/why30263-bot/ai-frontier"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist"
#endif

[Setup]
AppId={{A8F4E179-3D47-4C8E-AE63-F32E208883E0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL + "/issues"}
DefaultDirName={localappdata}\Programs\AIFrontier
DefaultGroupName={#MyAppName}
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=AIFrontier-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#PublishDir}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\AIFrontier.exe
CloseApplications=yes
RestartApplications=yes
Uninstallable=yes
CreateUninstallRegKey=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AI Frontier"; Filename: "{app}\AIFrontier.exe"
Name: "{autodesktop}\AI Frontier"; Filename: "{app}\AIFrontier.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"; Flags: checkedonce

[Run]
Filename: "{app}\AIFrontier.exe"; Description: "启动 AI Frontier"; Flags: nowait postinstall skipifsilent
