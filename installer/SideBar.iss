; Inno Setup 6. Сборка: iscc /DAppVersion=0.1.0 installer\SideBar.iss
; Исходник — artifacts\app (результат dotnet publish), результат — artifacts\installer.

#ifndef AppVersion
  #error AppVersion не задана: iscc /DAppVersion=X.Y.Z installer\SideBar.iss
#endif

#define AppName "SideBar"
#define FwRule "SideBar Quick Share"

[Setup]
AppId={{6F1B7C52-3A0E-4E5B-9D44-5B1D2C8A7E10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=SideBar
AppPublisherURL=https://github.com/Denmurzik/SideBar
DefaultDirName={autopf}\SideBar
DefaultGroupName=SideBar
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; HKCU трогаем осознанно: только удаление значения автозапуска при деинсталляции
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE
SetupIconFile=..\src\SideBar.App\Assets\SideBar.ico
UninstallDisplayIcon={app}\SideBar.exe
OutputDir=..\artifacts\installer
OutputBaseFilename=SideBar-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\SideBar"; Filename: "{app}\SideBar.exe"

; Значение автозапуска создаёт само приложение; здесь только удаление при деинсталляции.
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "SideBar"; ValueType: none; Flags: uninsdeletevalue dontcreatekey

[Run]
; Удаляем старое правило (ошибка, если его нет, игнорируется), затем добавляем: дубликаты не копятся.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FwRule}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FwRule}"" dir=in action=allow program=""{app}\SideBar.exe"" protocol=TCP profile=private"; Flags: runhidden waituntilterminated
Filename: "{app}\SideBar.exe"; Description: "{cm:LaunchProgram,SideBar}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FwRule}"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFwRule"
