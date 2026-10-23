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
AppMutex=Local\SideBar.SingleInstance
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

[Run]
; Сначала удаляем ВСЕ входящие правила для нашего exe, в том числе Block, которые Windows создаёт при «Отмене» в запросе брандмауэра
; (Block сильнее Allow). Затем правило по имени (старая установка в другой папке). Ошибка «правил нет» игнорируется. Потом добавляем: дубликаты не копятся.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all dir=in program=""{app}\SideBar.exe"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FwRule}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#FwRule}"" dir=in action=allow program=""{app}\SideBar.exe"" protocol=TCP profile=private,domain"; Flags: runhidden waituntilterminated
Filename: "{app}\SideBar.exe"; Description: "{cm:LaunchProgram,SideBar}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=all dir=in program=""{app}\SideBar.exe"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFwRulesByProgram"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FwRule}"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFwRule"

[Code]
{ Значение автозапуска создаёт приложение; удаляем его при деинсталляции (для пользователя, запустившего деинсталлятор). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SideBar');
end;
