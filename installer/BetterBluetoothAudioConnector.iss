#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "Better Bluetooth Audio Connector"
#define AppPublisher "liubaiqing"
#define AppUrl "https://github.com/liubaiqing/BetterBluetoothAudioConnector"
#define AppExeName "Better Bluetooth Audio Connector.exe"
#define PublishDir "..\BetterBluetoothAudioConnector\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{B4AE5674-0B19-4E54-A43F-5421DCE095B7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=BetterBluetoothAudioConnector-Setup-{#AppVersion}-x64
SetupIconFile=..\BetterBluetoothAudioConnector\Assets\BetterBluetoothAudioConnector.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
WizardResizable=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,.\Languages\ChineseSimplifiedOverrides.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
