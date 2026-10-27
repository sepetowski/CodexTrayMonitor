[Setup]
AppId={{1DDA879D-C5F4-4B03-97DE-C72122C17719}
AppName=Codex Tray Monitor
AppVersion=1.0.0
AppPublisher=Codex Tray Monitor contributors
DefaultDirName={localappdata}\Programs\CodexTrayMonitor
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=CodexTrayMonitor-Setup
UninstallDisplayIcon={app}\CodexTrayMonitor.exe
SetupIconFile=..\src\CodexTrayMonitor\Assets\CodexTrayMonitor.ico
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
AppMutex=Local\CodexTrayMonitor.Installation
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Files]
Source: "..\artifacts\publish\win-x64\CodexTrayMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\licenses\H.NotifyIcon-2.4.1.txt"; DestDir: "{app}"; DestName: "H.NotifyIcon-LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Codex Tray Monitor"; Filename: "{app}\CodexTrayMonitor.exe"; WorkingDir: "{app}"; IconFilename: "{app}\CodexTrayMonitor.exe"; AppUserModelID: "CodexTrayMonitor.Desktop"

[Run]
Filename: "{app}\CodexTrayMonitor.exe"; Description: "Launch Codex Tray Monitor"; Flags: nowait postinstall skipifsilent unchecked
