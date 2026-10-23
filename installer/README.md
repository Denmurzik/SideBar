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
«SideBar Quick Share» (TCP, входящие, профили «частная» и «доменная») и удаляет его при деинсталляции.
Перед добавлением (и при деинсталляции) удаляются все входящие правила для `{app}\SideBar.exe`
(`netsh ... delete rule name=all dir=in program=...`), в том числе Block-правила, созданные «Отменой» в запросе брандмауэра.
Релиз-кандидаты (`v0.2.0-rc1`) публикуйте только как GitHub pre-release.
