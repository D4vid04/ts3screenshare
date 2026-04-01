#define AppName "TS3ScreenShare"
#define AppVersion "1.0.0"
#define AppPublisher "D4vid04"
#define AppURL "https://github.com/D4vid04/ts3screenshare"
#define AppExeName "TS3ScreenShare.exe"
#define PublishDir "..\publish"
#define PluginDll "..\Plugin\build\Release\TS3ScreenSharePlugin.dll"
#define NotificationWav "TS3ScreenShareNotification.wav"

[Setup]
AppId={{B3A2C1D4-7E5F-4A8B-9C2D-1E3F5A7B9C0D}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=TS3ScreenShare-Setup-v{#AppVersion}
SetupIconFile=..\Assets\logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "ts3plugin"; Description: "Install TeamSpeak 3 plugin (auto-launch on server join)"; GroupDescription: "TeamSpeak 3 Integration:"; Flags: checkedonce

[Files]
; Main application
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

; TS3 plugin DLL — installed to %APPDATA%\TS3Client\plugins\
Source: "{#PluginDll}"; DestDir: "{userappdata}\TS3Client\plugins"; \
    Flags: ignoreversion; Tasks: ts3plugin

; Notification sound for the TS3 plugin
Source: "{#NotificationWav}"; DestDir: "{userappdata}\TS3Client\plugins"; \
    Flags: ignoreversion; Tasks: ts3plugin

[Registry]
; Store install dir so the TS3 plugin can find the exe via registry
Root: HKCU; Subkey: "SOFTWARE\TS3ScreenShare"; ValueType: string; \
    ValueName: "InstallDir"; ValueData: "{app}"; Flags: uninsdeletekey

[Icons]
Name: "{group}\{#AppName}";    Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\TS3ScreenShare"
