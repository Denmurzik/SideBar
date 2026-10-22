# SideBar

Выезжающая с правого края экрана панель для Windows 11 (WinUI 3, NativeAOT):

- **Quick Share** — отправка и приём файлов с Android-устройствами (своя реализация протокола);
- **временная стопка** — промежуточный буфер для drag-and-drop между приложениями.

Статус: M1 (каркас) готов; дальше M2–M7, см. [`docs/PLAN.md`](docs/PLAN.md) и [`HANDOFF.md`](HANDOFF.md).

Неофициальный проект, не связан с Google. «Quick Share» — товарный знак Google LLC.

## Сборка

Требования:

- .NET SDK 10.0.402 (версия закреплена в `global.json`);
- Visual Studio Build Tools с компонентом «Разработка классических приложений на C++» — нужен для NativeAOT.

Тесты (собранный exe запускается напрямую: `dotnet test` с xunit.v3 на .NET 10 даёт «Handshake failures»):

```
dotnet build tests/SideBar.Core.Tests -c Release
tests/SideBar.Core.Tests/bin/Release/net10.0-windows10.0.26100.0/SideBar.Core.Tests.exe
```

Публикация (NativeAOT, результат — `artifacts/app`). Из git-bash перед публикацией добавьте в `PATH` каталог с `vswhere`:
`export PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"`.

```
dotnet publish src/SideBar.App -c Release -p:Platform=x64 -o artifacts/app
```

## Структура

- `src/SideBar.Core` — библиотека с логикой без UI;
- `src/SideBar.App` — приложение WinUI 3 (unpackaged, NativeAOT);
- `tests/` — тесты (`SideBar.Core.Tests`);
- `spikes/` — исследовательский код, не часть продукта.

## Лицензия

[GPL-3.0](LICENSE)
