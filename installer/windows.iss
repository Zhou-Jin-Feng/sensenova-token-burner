#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef NumericVersion
  #error NumericVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

[Setup]
AppId={{64244ED3-DF89-49A6-922B-23D6B377A154}
AppName=SenseNova Token Burner
AppVersion={#AppVersion}
AppPublisher=SenseNova Token Burner contributors
AppPublisherURL=https://github.com/Zhou-Jin-Feng/sensenova-token-burner
DefaultDirName={localappdata}\Programs\SenseNova Token Burner
DefaultGroupName=SenseNova Token Burner
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
VersionInfoVersion={#NumericVersion}
UninstallDisplayIcon={app}\SenseNova.TokenBurner.exe
OutputDir={#OutputDir}
OutputBaseFilename=SenseNova.TokenBurner-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SenseNova Token Burner"; Filename: "{app}\SenseNova.TokenBurner.exe"
Name: "{autodesktop}\SenseNova Token Burner"; Filename: "{app}\SenseNova.TokenBurner.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SenseNova.TokenBurner.exe"; Description: "打开 SenseNova Token Burner"; Flags: nowait postinstall skipifsilent

; 用户配置与DPAPI凭据在LocalAppData/SenseNova.TokenBurner/User，卸载不删除。
; 不创建开机启动、服务、计划任务，不自动关闭既有进程。
