# Установщик

Inno Setup 6 (`iscc`). Скрипт — `SideBar.iss`, версия передаётся параметром.

Локальная сборка:

```
dotnet publish src/SideBar.App -c Release -p:Platform=x64 -o artifacts/app
iscc /DAppVersion=0.1.0 installer\SideBar.iss
```

Результат: `artifacts/installer/SideBar-<версия>-setup.exe`. В CI установщик собирается на каждом push,
релиз создаётся только на теге `v*`.

Установщик требует прав администратора (`{autopf}`, правило брандмауэра). Добавляет правило
«SideBar Quick Share» (TCP, входящие, только частные сети) и удаляет его при деинсталляции.
