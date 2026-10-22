# S6 — управление `nearby_share.exe`

Дата: 2026-10-22. Код: `spikes/official/` (C#, `net10.0-windows`, P/Invoke, без зависимостей).
Версия приложения: MSIX `NearbyShare` 1.0.2472.1, сборка бинарников 2026-01-13.

## Итог

Цикл «запуск без видимых окон -> `PublishDevice succeeded` -> закрытие -> сертификаты обновлены» отработал **без ручных действий и без `taskkill`** (2 прогона). Ключевые факты:

- запуск с `STARTUPINFO.wShowWindow = SW_HIDE` — **ни одного видимого окна** (проверено перечислением окон процесса), `PublishDevice succeeded` через ~1.3 с, весь цикл с закрытием ~3.5 с;
- `WM_CLOSE` главному окну **не завершает процесс** — окно прячется (в трей), процесс живёт (проверено: 20 с ожидания, процесс остался);
- **штатное завершение — `WM_QUIT` в поток окна** (`PostThreadMessage` потоку, владеющему окном `Google.Windows.NearbyShare`): процесс выходит за ~2 с, в логе пишется `[[ Exiting Nearby Share for Windows ]]`, leveldb и `preferences.json` целы (проверено разбором ридером из S3: 13 записей, все проверки сошлись, id в `expiration_dict` == ключам БД). Это не принудительное завершение: цикл сообщений заканчивается, выполняется обычный выход из `main`;
- автозапуска через `startupTask`/Run нет; есть задача Планировщика «Quick Share Relaunch» (см. ниже).

## 1. Как устроен автозапуск (проверено, read-only; ничего не отключалось)

| Механизм | Результат проверки |
|---|---|
| MSIX `AppxManifest.xml` | **Нет** `windows.startupTask`. Есть `windows.shareTarget` (`nearby_share.exe`) и `windows.fileExplorerContextMenus` + COM `nearby_share_context_menu.dll` (CLSID `AD54D7CA-052D-4730-B0F2-2BA34D3438DC`), приложение `NearbyShareContextMenu` -> `nearby_share_launcher.exe`. Приложение `AppListEntry="none"` (нет плитки в Пуске). |
| `HKCU/HKLM\...\CurrentVersion\Run` (+WOW6432) | Записей Quick Share нет. |
| `HKCU\Software\Google\NearbyShare`, `HKLM\...` | Ключей нет (нужны для `log_level` и `auto_app_start`; сейчас используются значения по умолчанию). |
| Планировщик заданий | **`\Google\Quick Share Relaunch`**: триггер — по времени, повтор каждые **15 мин**; действие `C:\Program Files\Google\NearbyShare\nearby_share_launcher.exe --relaunch_if_crashed`; запуск от пользователя, `RunLevel=Limited`, состояние `Ready`, последний результат 0. |

`nearby_share_launcher.exe` (по строкам бинарника, **гипотеза по строкам, поведение не прослежено до конца**): читает флаг `nearby_sharing.is_auto_app_start_enabled` (локальный/roaming proto-store `nearby_sharing_preferences.pb`; при отсутствии реестрового значения по умолчанию `true`), смотрит `nearby_share.pid`; сообщение: «auto app start flag is disabled — Skip starting the app and exit the launcher». Имя аргумента `--relaunch_if_crashed` и наблюдение (задача сработала в 21:15, приложение не запущено, пользователь его не открывал) говорят, что launcher перезапускает приложение **только после аварии** (pid-файл есть, процесса нет), а не просто «если не запущено».
- Файл `nearby_sharing_preferences.pb` (6 байт): `08 01 10 01 18 01` — три булевых поля = true (какое из них autostart — не установлено).
- `nearby_share.exe` в `strings` содержит `GetIsAutoAppStartEnabledOmaha`: флаг автозапуска, судя по имени, также приходит из Google Update (Omaha).

### Как выключить штатно (описано, не выполнялось)
1. Предпочтительно — настройка в самом приложении Quick Share («Запускать при входе»), она меняет `nearby_sharing.is_auto_app_start_enabled`. Требует открыть окно приложения (мы его запускаем кратковременно; интерактивно — действие пользователя).
2. Планировщик: `Disable-ScheduledTask -TaskPath '\Google\' -TaskName 'Quick Share Relaunch'` (или `schtasks /Change /TN "\Google\Quick Share Relaunch" /DISABLE`). Задача принадлежит пользователю, права админа, скорее всего, не нужны. Побочный эффект: нет перезапуска после аварий — для нас это желательно.
3. Run-ключа и startupTask отключать не нужно — их нет. Возможный будущий автозапуск через Omaha/обновление задачу может пересоздать — проверять при каждом старте SideBar (read-only, `Get-ScheduledTask`/COM `ITaskService`).

Важно для сосуществования: поскольку launcher может поднять приложение после аварии, SideBar обязан **завершать приложение штатно (WM_QUIT)**, а не `TerminateProcess` — иначе, если остался pid-файл, launcher через <=15 мин запустит приложение снова (гипотеза, не проверялось). При этом уже сейчас ни одна из наших остановок не привела к перезапуску: после обоих прогонов процесс не появлялся (проверено на тике 21:30).

## 2. Эксперимент

Условия: процесс не запущен (проверено перед стартом); перед первым прогоном сделана копия leveldb и prefs во `%TEMP%` (удалена после проверки). Команда: `dotnet spikes/official/bin/Debug/net10.0-windows/Official.dll cycle <normal|minnoactive|minimized|hidden> [publishTimeoutSec] [closeTimeoutSec]` (отказывается работать, если `nearby_share` уже запущен).

| Прогон | Режим запуска | Окна | PublishDevice | Закрытие |
|---|---|---|---|---|
| 1 | `SW_SHOWMINNOACTIVE` | через ~0.4 с создано скрытое окно `Google.Windows.NearbyShare`, к 1.0 с оно **видимо и свёрнуто** (мигает кнопка на панели задач) | 2.4 с | `WM_CLOSE`: процесс остался, окно скрыто (трей). Затем `WM_QUIT`: вышел < 15 с |
| 2 | `SW_HIDE` | окно создано, **не показывалось** (`visible=False` на всём протяжении) | 1.3 с | `WM_QUIT`: вышел за 2.2 с, маркер «Exiting» в логе |

Окна процесса: `Google.Windows.NearbyShare` (Flutter), `MSCTFIME UI`, `IME` (служебные).
`SW_HIDE` годится как основной режим. Остаётся иконка в трее на время работы (не проверялось, видна ли она на 3 с; Windows 11 скрывает новые иконки в переполнении).

### Целостность после (проверено)
- Каталог leveldb: после каждого запуска ротируются `MANIFEST-N`, `*.log`/`*.ldb`, обновляются `CURRENT`, `LOG`, `LOG.old` — штатная работа leveldb (compaction при открытии). Ридер из S3 открывает БД: 13 записей (было 15 — истёкшие удалены), все контрольные суммы протокола (tag, GCM, key==secret_id) сошлись.
- `preferences.json` и `preferences_bak.json` перезаписаны приложением (размер ±10 Б), JSON валиден; `private_certificate_list` — по-прежнему 6, id==SHA256(secret) 6/6.
- Сертификаты **обновились**: до — 2 из 6 приватных валидны, 24 ч назад кончились окна; после — валидны 2 (запас ~47.6 ч), запас вперёд ~192 ч; публичные других устройств: запас вырос с ~77 ч до ~149 ч.
- Файл флагов `nearby_sharing_flags.dat` перезаписан (тот же размер).

## 3. Лог

- Каждый запуск создаёт `Logs\nearby_share_<ГГГГ-ММ-ДД-ччммсс>.log` (UTC по времени записей — локальное время смещено, сверено) и жёсткую ссылку `nearby_share.log` на актуальный. Поэтому контроллер запоминает набор логов и длины до запуска и ищет маркер в любых файлах `nearby_share*.log`, читая с сохранённого смещения (`FileShare.ReadWrite|Delete`).
- Маркеры: `PublishDevice succeeded` (строка `operator(): PublishDevice succeeded. contact_removed: false`, при запуске бывает дважды), `PublishDevice: uploaded 3 self share credentials and 3 contacts credentials`, `We are the only instance, command line:`, `[[ Exiting Nearby Share for Windows ]]`.
- Лог содержит токены/идентификаторы: контроллер **не выводит содержимое**, только булево «маркер найден».

## 4. Прототип контроллера (`spikes/official/src/OfficialAppController.cs`)

`IsRunning/FindProcesses` (по имени `nearby_share`), `Launch(ShowMode, args)` (`CreateProcessW` + `STARTF_USESHOWWINDOW`; `args` — пути файлов для варианта A из HANDOFF), `SnapshotLogs` + `WaitForLogAsync(marker, snapshot, timeout)`, `WindowsOf(pid)`, `QuitMessageLoopAsync(timeout)` (WM_QUIT), `CloseSoftlyAsync` (WM_CLOSE — только скрывает окно, оставлен для диагностики). Таймауты везде явные; при провале закрытия контроллер **не убивает**, возвращает `false`.

## 5. Риски и не проверено

- Если пользователь запустит приложение сам, пока SideBar его завершает/ждёт: обязателен детект «процесс запущен до нас» (PID не наш) — такой процесс не трогаем. Различать запущенный нами экземпляр по PID из `Launch`.
- Гонка двух запусков: приложение single-instance (мьютекс); второй запуск шлёт `WM_COPYDATA` первому — нельзя запускать «для обновления» при чужом запущенном экземпляре (аргументы, e.g. пути файлов, попадут в его очередь).
- `WM_QUIT` — не документированный способ выхода; при смене версии (Flutter runner) может перестать работать; fallback: ожидание -> `TerminateProcess` только как последнее средство (не использовался; повреждение leveldb маловероятно, но не проверялось).
- Не проверено: поведение при сбое сети (нет `PublishDevice succeeded` -> таймаут 60 с -> закрытие); поведение при первом запуске после выхода из аккаунта; видна ли иконка в трее; влияние на `Another instance` при двойном запуске; антиабуз (частота: 2 запуска за ~2 мин за сегодня — не наблюдаем реакции).
- Тик `Quick Share Relaunch` в 21:30 (после обоих наших штатных выходов) отработал с результатом 0, приложение **не** перезапущено (проверено).
