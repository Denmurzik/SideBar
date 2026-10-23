# M3 — Quick Share в режиме «Все»: план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Верхняя плитка карточки отправляет файлы на Android-устройства поблизости (Quick Share, режим «Все», Wi-Fi LAN), а ПК принимает файлы с телефона в «Загрузки» с подтверждением по PIN.

**Architecture:** Протокол переносится из `spikes/nearby/SideBar.Nearby` (проверен вживую на Pixel 9 в обе стороны) в продуктовую библиотеку `src/SideBar.Nearby` без UI, с тестами (`tests/SideBar.Nearby.Tests`), и укрепляется по ревью исследования (#1, #3–#6, #11, #18, #24). Сертификатный режим «Свои устройства» — M4 (в M3 переносится только интерфейс `IShareIdentity` и `AnonymousIdentity`). Приложение получает `NearbyService` (UI-фасад), состояния плитки Quick Share, уведомления Windows (App SDK `AppNotificationManager`) и пункт видимости в трее.

**Tech Stack:** как M1/M2 + Google.Protobuf 3.36.2 / Grpc.Tools 2.84.0 (codegen, как в спайке), Win32 DNS-SD (`DnsServiceBrowse/Resolve/Register`), `Microsoft.Windows.AppNotifications`.

**Spec:** `docs/PLAN.md` (§1 «Верх — Quick Share», §2 «Входящие», «Видимость», «Медиумы v1»), `docs/research/nearby-protocol.md`, `docs/research/review-2026-10-22.md` (#1, #3, #4, #5, #6, #11, #18, #24), M1/M2 ledgers в `docs/superpowers/ledgers/`.

## Global Constraints

- Всё из Global Constraints M1 и M2 (AOT-safe interop, без активации окон, коммиты без трейлеров + push, сборки по одной под `.buildlock`, тесты — запуском exe).
- Протокол: поведение, проверенное на Pixel 9, не меняется (S1/S2): `FileMetadata.id` обязателен; отправитель не закрывает сокет до `Disconnection` получателя; KeepAlive каждые 10 с; чанки 512 КиБ; финальный пустой чанк `LAST_CHUNK`.
- Устойчивость (#1, #3, #4, #24): `PayloadTransfer` с `CONTROL` обрабатывается (`PAYLOAD_CANCELED` → отмена передачи, прочее — игнор); отсутствие обязательного подсообщения → `InvalidDataException`; таймауты: рукопожатие 15 с, тишина от пира 30 с, решение пользователя 60 с (→ Reject); до 3 параллельных входящих сессий; BYTES-payload ≤ 64 КиБ и ≤ 8 одновременно; файл: `Written + len ≤ Size`, `Size ≥ 0`, ≤ 100 файлов в одном Introduction, свободное место проверяется до Accept; повтор Introduction или дубль payload id → протокольная ошибка; отправитель открывает файлы внутри try, `FileShare.ReadWrite`, читает входящие во время отправки и шлёт Cancel при отмене пользователем.
- Приём файлов (#5, #6): запись в `<имя>.part` рядом с целью → атомарный rename по завершении; любой неуспех удаляет `.part`; имена — `SideBar.Core.Shelf.FileNaming.Sanitize` (единая санитизация), уникальность «(n)» с `CreateNew` в цикле; Mark-of-the-Web (`:Zone.Identifier`, `ZoneId=3`, `HostUrl=about:internet`); папка — «Загрузки» через `SHGetKnownFolderPath(FOLDERID_Downloads)`.
- Логи протокола (#11): только белый список — тип кадра, размеры, enum-поля, результат; никаких имён файлов, текста, ssid/паролей, ключей; `ToString()`/JsonFormatter protobuf-сообщений запрещены вне `#if DEBUG` в CLI.
- Сеть (#18): mDNS-реклама и TCP-слушатель — только на IPv4 интерфейса с маршрутом по умолчанию; в публичной сети (`INetworkListManager`, категория Public) и в режиме «Скрыто» — не слушать и не рекламировать; правило брандмауэра — в M7 (установщик), на машине пользователя не менять.
- Видимость (M3): `Hidden` (по умолчанию) и `Everyone` на 10 минут (как Android), включается в меню трея «Видимость для всех — 10 мин»; по истечении — `Hidden`. Отправка работает при любой видимости.
- UI плитки Quick Share: в покое — логотип; после drop файлов на плитку (или переноса элементов стопки на неё — свой drag на плитку QS = отправка, элементы остаются в стопке) — поиск, найденные устройства иконками (до 4, имя под иконкой, тип устройства — глиф Phone `U+E8EA` / Laptop `U+E7F8` / Tablet `U+E70A`), клик по иконке — отправка, прогресс — кольцо вокруг иконки, итог — галочка/крестик 1.5 с, затем покой; клик вне карточки или 60 с без выбора — отмена; карточка не прячется, пока идёт выбор или передача.
- Входящие (PLAN §2): от чужих (M3 — все) — уведомление Windows «<устройство> хочет отправить N файл(ов) · PIN 1234» с кнопками «Принять»/«Отклонить»; карточка кратко выглядывает (Peek на 1.5 с) при входящем запросе; по завершении — уведомление «Получено N файл(ов)» с кнопками «Открыть» (один файл) / «Показать в папке»; отказ/ошибка — уведомление с причиной без путей.
- Строки UI — в `Strings` (русские).

## Review Focus

1. Телефон отменяет передачу посреди большого файла → сессия закрывается без исключения, `.part` удалён, UI показывает «Отменено». Тест — Task 2 (loopback с `CONTROL PAYLOAD_CANCELED`).
2. Пир подключился и молчит → через 15 с/30 с сессия закрыта, следующие приёмы не блокированы. Тест — Task 2.
3. Имя файла от телефона `..\..\evil.exe` / `CON.txt` / bidi → файл в «Загрузках» с безопасным именем и Mark-of-the-Web. Тест — Task 3.
4. ПК в публичной Wi-Fi → ничего не слушает и не рекламирует даже при «Все». Тест — Task 4 (модель видимости с инжектируемой категорией сети).
5. Пользователь бросил файлы на плитку, но телефона не видно (Quick Share на телефоне закрыт) → через 60 с отмена с понятным текстом «Устройства не найдены — откройте Quick Share на телефоне». Тест — Task 5 (состояния плитки, unit на модели состояний).

---

### Task 1: Перенос библиотеки протокола в продукт

**Files:** Create `src/SideBar.Nearby/**` (из `spikes/nearby/SideBar.Nearby`: Wire, Crypto, Discovery, Session без `CertificateIdentity`/`OfficialCertificates`, Protos), `tests/SideBar.Nearby.Tests/**` (из спайка без тестов сертификатов), `tools/SideBar.Nearby.Cli/**` (из спайка, без `--self/--diag`); Modify `SideBar.slnx`, `.github/workflows/ci.yml` (собрать и запустить `SideBar.Nearby.Tests.exe`).

**Interfaces — Produces:** пространство имён `SideBar.Nearby.*` с теми же публичными типами, что в спайке (`QuickShareDiscovery`, `QuickShareAdvertiser`, `SendSession`, `ReceiveSession`, `SessionOptions`, `IShareIdentity`, `AnonymousIdentity`, `EndpointInfo`, `Pin`, `TransferTypes`), плюс `NearbyLog` — делегат белого списка (`Action<NearbyLogEvent>`; `NearbyLogEvent(string Kind, long? Size, string? Enum)`), заменяющий трассировку.

- [ ] **Step 1:** Перенести код и тесты (атрибуция Apache-2.0/Unlicense/GPL-3.0 в шапках портированных файлов сохраняется); удалить JSON-дампы кадров и `Diagnose` из библиотеки; CLI печатает только белый список (DEBUG может печатать больше).
- [ ] **Step 2:** `dotnet build tests/SideBar.Nearby.Tests -m:1` + запуск exe — все перенесённые тесты (≥ 60) зелёные; локальный e2e `tools/SideBar.Nearby.Cli receive` ↔ `send` по loopback — файл побайтно совпадает.
- [ ] **Step 3:** CI собирает и запускает оба тест-exe. Коммит `feat(nearby): port the Quick Share protocol library` + push, CI зелёный.

### Task 2: Устойчивость сессий

**Files:** Modify `src/SideBar.Nearby/Session/{NearbyConnection,SendSession,ReceiveSession,Frames}.cs`; Test `tests/SideBar.Nearby.Tests/RobustnessTests.cs`.

**Interfaces — Produces:** `SessionOptions` получает `HandshakeTimeout = 15 s`, `IdleTimeout = 30 s`, `DecisionTimeout = 60 s`, `MaxFilesPerIntroduction = 100`, `MaxBytesPayload = 64 KiB`, `MaxConcurrentBytesPayloads = 8`; `TransferResult` получает `CancelledByPeer`/`TimedOut`/`ProtocolError`; `ReceiveSession` отдаёт `IncomingTransfer.Verified` (в M3 всегда `false`) — задел для M4 (#2).

- [ ] **Step 1: Тесты (loopback, с поддельным пиром на уровне кадров):** `Peer_cancel_mid_file_ends_session_cleanly`; `Control_ack_frames_are_ignored`; `Missing_submessage_is_protocol_error_not_crash`; `Silent_peer_times_out_after_idle_timeout` (тайм-ауты в тесте укорочены); `Handshake_timeout`; `User_decision_timeout_rejects`; `Bytes_payload_over_limit_is_rejected`; `File_chunk_beyond_declared_size_is_rejected`; `Duplicate_introduction_is_protocol_error`; `Too_many_files_rejected`; `Sender_reads_peer_cancel_during_send`; `Sender_open_failure_reports_and_leaks_nothing`.
- [ ] **Step 2–4:** FAIL → реализовать → PASS (полный exe).
- [ ] **Step 5:** Коммит `fix(nearby): timeouts, limits, cancel and protocol-error handling` + push.

### Task 3: Безопасная запись принятых файлов

**Files:** Create `src/SideBar.Nearby/Session/ReceivedFileWriter.cs`; Modify `ReceiveSession.cs`; Modify `src/SideBar.Nearby/SideBar.Nearby.csproj` (ссылка на `SideBar.Core` ради `FileNaming.Sanitize`); Test `tests/SideBar.Nearby.Tests/ReceivedFileWriterTests.cs`.

**Interfaces — Produces:** `sealed class ReceivedFileWriter(string directory)` — `Stream Begin(string rawName, long size, out string finalPathPreview)`, `string Commit()` (rename `.part` → уникальное итоговое имя, MotW), `void Abort()` (удалить `.part`); `static string DownloadsFolder()` (`SHGetKnownFolderPath`).

- [ ] **Step 1: Тесты:** `Traversal_names_stay_in_directory` (`..\..\a.txt`, `C:\x\a.txt`, `a/b.txt`); `Reserved_and_bidi_names_are_sanitized`; `Commit_is_atomic_and_unique` (две параллельные записи `a.txt` → `a.txt`, `a (2).txt`); `Abort_removes_part`; `Zone_identifier_written` (`:Zone.Identifier` содержит `ZoneId=3`); `Free_space_check_rejects_too_large` (через инжектируемый `Func<long>`).
- [ ] **Step 2–4:** FAIL → реализовать → PASS.
- [ ] **Step 5:** Коммит `fix(nearby): safe received-file writing (part+rename, sanitize, MotW)` + push.

### Task 4: Модель видимости и привязка к сети

**Files:** Create `src/SideBar.Core/Nearby/VisibilityModel.cs`, `src/SideBar.Nearby/Discovery/NetworkInfo.cs`; Modify `QuickShareAdvertiser.cs`; Test `tests/SideBar.Core.Tests/VisibilityModelTests.cs`.

**Interfaces — Produces:** `enum Visibility { Hidden, Everyone }`; `enum NetworkCategory { Unknown, Public, Private, Domain }`; `sealed class VisibilityModel` — `Visibility Current`, `DateTimeOffset? EveryoneUntil`, `void EnableEveryone(DateTimeOffset now)` (10 мин), `void Tick(DateTimeOffset now)`, `bool ShouldListen(NetworkCategory c)` (`Everyone` и не `Public`), `event Action? Changed`. `static class NetworkInfo { (IPAddress Address, int InterfaceIndex)? DefaultRouteInterface(); NetworkCategory CategoryOf(int interfaceIndex); }` (`INetworkListManager` через `[GeneratedComInterface]`, при ошибке — `Unknown` ⇒ не слушать).

- [ ] **Step 1: Тесты (Core):** `Default_is_hidden`; `Everyone_expires_after_10_minutes`; `Public_network_never_listens`; `Unknown_network_never_listens`; `Private_and_domain_listen_only_when_everyone`.
- [ ] **Step 2–4:** FAIL → реализовать (TCP-слушатель и `DnsServiceRegister` — на адресе/индексе `DefaultRouteInterface`) → PASS.
- [ ] **Step 5:** Коммит `feat(nearby): visibility model and LAN-only binding` + push.

### Task 5: Отправка из плитки Quick Share (App)

**Files:** Create `src/SideBar.Core/Nearby/ShareTileModel.cs` (+ тесты `tests/SideBar.Core.Tests/ShareTileModelTests.cs`), `src/SideBar.App/Nearby/NearbyService.cs`, `src/SideBar.App/Nearby/ShareTileView.xaml(.cs)`; Modify `CardWindow.xaml(.cs)`, `App.xaml.cs`, `src/SideBar.App/Shelf/OleDropTarget.cs` (drop на плитку QS: файлы — отправка; свой drag стопки на плитку QS — отправка, элементы остаются), `tools/SideBar.DragDriver` (сценарий: drop на плитку QS → состояние «поиск», отмена по клику вне).

**Interfaces — Produces:** `enum ShareTileState { Idle, Searching, Choosing, Sending, Done, Failed }`; `sealed class ShareTileModel` — `State`, `IReadOnlyList<DeviceView> Devices` (≤ 4), `void BeginShare(IReadOnlyList<string> paths, DateTimeOffset now)`, `void DeviceFound(DeviceView d)`, `void DeviceLost(string id)`, `void Choose(string id)`, `void Progress(double fraction)`, `void Finished(bool ok, string? reason)`, `void Cancel()`, `void Tick(DateTimeOffset now)` (60 с без выбора → Failed «Устройства не найдены — откройте Quick Share на телефоне»; Done/Failed → Idle через 1.5 с), `bool KeepsCardOpen`. `sealed record DeviceView(string Id, string Name, DeviceKind Kind)`. `NearbyService` — `void StartShare(paths)`, `void Cancel()`, события в UI-поток.
- Карточка не прячется, пока `ShareTileModel.KeepsCardOpen` (передать в `EdgeStateMachine` тем же механизмом, что веер: `SetKeepOpenRect` на весь прямоугольник карточки или отдельный флаг — на выбор реализатора, с тестом).

- [ ] **Step 1: Тесты модели:** `Drop_starts_searching`; `Found_devices_capped_at_4`; `Choose_starts_sending`; `No_device_for_60s_fails_with_hint`; `Done_returns_to_idle_after_1_5s`; `Cancel_from_any_state_returns_idle`; `KeepsCardOpen_true_while_searching_choosing_sending`.
- [ ] **Step 2–4:** FAIL → модель → PASS; затем `NearbyService` + `ShareTileView` (иконки, кольцо прогресса — `ProgressRing` не годится (неопределённый), рисовать дугу `Path` по fraction), drop на плитку.
- [ ] **Step 5:** Debug/AOT 0 warnings; DragDriver: drop на плитку QS → «поиск», клик вне → Idle; loopback e2e: `tools/SideBar.Nearby.Cli receive` на этой машине виден в плитке как устройство → выбор → файл принят побайтно (Cli слушает на loopback-адаптере — допускается флаг `--include-local` в discovery только в DEBUG-сборке приложения).
- [ ] **Step 6:** Коммит `feat(app): send from the Quick Share tile` + push.

### Task 6: Приём файлов, уведомления, видимость в трее (App)

**Files:** Create `src/SideBar.App/Nearby/ReceiveHost.cs`, `src/SideBar.App/Nearby/Notifications.cs`; Modify `App.xaml.cs`, `src/SideBar.App/Tray/TrayIcon.cs`, `Strings.cs`, `SideBar.App.csproj` (App SDK AppNotifications).

**Interfaces — Consumes:** `VisibilityModel`, `NetworkInfo`, `ReceiveSession`, `ReceivedFileWriter` (Tasks 2–4). **Produces:** `ReceiveHost` — запускает/останавливает рекламу и слушатель по `VisibilityModel.ShouldListen(NetworkInfo.CategoryOf(...))` (перепроверка при смене сети — `NetworkChange.NetworkAddressChanged` + таймер 30 с), до 3 сессий, решение пользователя через `Notifications.AskAccept(sender, count, pin) → Task<bool>` (кнопки уведомления; таймаут 60 с → отклонить), сохранение в «Загрузки», итоговое уведомление `Notifications.Received(paths)`; Peek карточки на 1.5 с при входящем запросе. Трей: пункт «Видимость для всех — 10 мин» (галочка, пока активно; повторный клик — выключить).

- [ ] **Step 1:** Реализовать. AppNotificationManager в unpackaged-приложении: `AppNotificationManager.Default.Register()` при старте, `Unregister()` при выходе; обработчик `NotificationInvoked` маршалит в UI-поток.
- [ ] **Step 2:** Debug/AOT 0 warnings. Loopback e2e: `tools/SideBar.Nearby.Cli send` на этой машине → уведомление с PIN (проверка через лог приложения, нажатие кнопки — через `--auto-accept` DEBUG-флаг приложения для автотеста) → файл в «Загрузках» (тестовая папка через `SIDEBAR_DOWNLOADS_DIR` override), MotW записан; при видимости Hidden — Cli не находит ПК.
- [ ] **Step 3:** Коммит `feat(app): receive with notifications and timed visibility` + push.
