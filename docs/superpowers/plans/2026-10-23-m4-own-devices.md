# M4 — «Свои устройства»: план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ПК обменивается файлами, текстом и ссылками со «своими» устройствами аккаунта Google (Pixel 9) без подтверждения, используя сертификаты официального приложения Quick Share; при нечитаемых сертификатах — честная деградация в режим «Все».

**Architecture:** Сертификатная идентичность из спайка (`spikes/nearby/SideBar.Nearby/Session/{CertificateIdentity,OfficialCertificates}.cs`, проверено вживую в обе стороны: `PairedKeyResult SUCCESS`, приём без кнопки) переносится в `src/SideBar.Nearby/Identity/`, ридер leveldb/protobuf — из `spikes/certs/src` в `src/SideBar.Nearby/Certs/` (читать в память, без временной копии). Сессии сообщают `Verified`; автоприём — только при `Verified`. Видимость расширяется до `Hidden | OwnDevices | Everyone`.

**Spec:** `docs/PLAN.md` (§2 «Видимость», «Свои устройства», «Деградация», «Входящие»), `docs/research/certs.md`, `docs/research/nearby-protocol.md` (модель PairedKey: хэш — секретом серта пира, пир — по рекламе, `optional_signed_data`, префиксы 0x01/0x02), `docs/research/review-2026-10-22.md` (#2, #7, #8, #9, #10, #23), ledgers M1–M3.

## Global Constraints

- Всё из M1–M3 (AOT, без активации окон, логи — белый список, коммиты без трейлеров + push, сборки/UI по одной под `.buildlock`/`.uilock`).
- Секреты (`preferences.json`, leveldb): только чтение, только в памяти (`FileShare.ReadWrite | FileShare.Delete`), никаких копий на диск, никаких значений в логах/исключениях/Detail (#8); имена/email/full_name/account_name из метаданных не выводятся никогда; имя устройства (`device_name`) — можно показывать в UI.
- Загрузка сертификатов: единый `TryLoad` с перехватом всех исключений, причина — enum (`Missing`, `Unreadable`, `FormatChanged`, `NoValidCertificate`), 3 повтора по 200 мс на гонку с compaction (#9); при неуспехе — режим «Свои устройства» недоступен, видимость «Свои» автоматически заменяется на «Скрыто» с одним уведомлением «Сертификаты Quick Share недоступны — работает только режим „Все“».
- Перезагрузка сертификатов: по таймеру до ближайшего `not_after` выбранного приватного серта и при изменении файлов (`FileSystemWatcher` на `preferences.json` и каталог leveldb, debounce 2 с); после перезагрузки — перерегистрация mDNS с новой солью (#10, #23).
- Реклама в «Своих устройствах»: endpoint info без имени (`Hidden`), имя берётся получателем из метаданных сертификата (#7); соль — новая при каждой (пере)регистрации, не из `consumed_salts` (#23); `consumed_salts` не пишем.
- Проверка пира: `IncomingTransfer.Verified == true` только если наш `VerifyPairedKey` вернул `Success` в этой сессии **до** Introduction (#2); `VerifiedName` — `device_name` из сертификата пира; автоприём и «своё» имя в уведомлении — только при `Verified`; в режиме «Свои устройства» неверифицированные входящие отклоняются без вопроса.
- Видимость: `Hidden` | `OwnDevices` (по умолчанию, если сертификаты читаются) | `Everyone` (на 10 мин, затем возврат к предыдущему); переключатель — в меню трея (радиогруппа из трёх пунктов); Public/Unknown сеть — не слушать ни в каком режиме.
- Отправка: при наличии сертификатов `send` всегда использует `CertificateIdentity` (свои устройства видят ПК как своё); устройство в плитке помечается «своё», если `Identify(endpointInfo)` нашёл его сертификат.
- Пока работает официальный `nearby_share.exe` (любой экземпляр) — свой стек на паузе (не рекламируем и не слушаем; отправка из плитки показывает «Закройте официальное приложение Quick Share»). Детект — по процессу, опрос раз в 5 с (M5 добавит цикл обновления сертификатов).

## Review Focus

1. Повреждённый/заблокированный `preferences.json` или leveldb в момент чтения → ни падения, ни частичной идентичности; через повтор — успех, иначе деградация с одним уведомлением. Тест — Task 1.
2. Чужое устройство в «Своих устройствах» с подделанным именем «Pixel 9» → входящий отклонён без вопроса, в UI не показан как «своё». Тест — Task 2.
3. Сертификат истёк, пока SideBar работает → перезагрузка по таймеру, реклама с новым сертификатом, без перезапуска. Тест — Task 1 (таймер с инжектируемыми часами).
4. Пользователь сам открыл официальный Quick Share → наш стек на паузе, после закрытия — возобновление. Тест — Task 4.
5. В сети одновременно «своё» и чужое устройство → в плитке оба, «своё» помечено, отправка на чужое идёт с PIN. Тест — Task 3 (модель плитки).

---

### Task 1: Сертификаты в продукте (Nearby.Certs + Identity)

**Files:** Create `src/SideBar.Nearby/Certs/{LevelDbReader,Snappy,ProtoReader,CertificateModel}.cs` (порт `spikes/certs/src`, вход — словарь путь→байты, без временных копий), `src/SideBar.Nearby/Identity/{CertificateIdentity,OfficialCertificateStore}.cs` (порт из спайка), `tests/SideBar.Nearby.Tests/{CertificateIdentityTests,OfficialCertificateStoreTests}.cs` (порт синтетических тестов спайка + новые).

**Interfaces — Produces:** `enum CertLoadFailure { Missing, Unreadable, FormatChanged, NoValidCertificate }`; `sealed class OfficialCertificateStore(string sharingDir, Func<DateTimeOffset> now)` — `bool TryLoad(out CertificateIdentity? identity, out CertLoadFailure? failure)`, `DateTimeOffset? NextExpiry`, `event Action? FilesChanged` (watcher + debounce); `CertificateIdentity : IShareIdentity` — как в спайке + `string? TryGetPeerName(EndpointInfo)`, `bool Identify(EndpointInfo)`; endpoint info в «Своих» — `Hidden`.

- [ ] **Step 1: Тесты (синтетические ключи):** перенесённые из спайка (7+); `TryLoad_garbage_preferences_is_FormatChanged`; `TryLoad_locked_file_retries_then_succeeds`; `TryLoad_no_valid_cert_reports_NoValidCertificate`; `Endpoint_info_in_own_devices_has_no_name`; `New_salt_each_CreateEndpointInfo_and_not_in_consumed`; `NextExpiry_is_min_not_after_of_selected_certs`.
- [ ] **Step 2–4:** FAIL → порт + доработки → PASS. **Step 5:** живой `identity-check` в CLI (печатает только сводку) на этой машине — SELF_SHARE выбран, peers ≥ 1. Коммит + push.

### Task 2: Verified и автоприём в сессиях

**Files:** Modify `src/SideBar.Nearby/Session/{ReceiveSession,SendSession,TransferTypes}.cs`; Test `tests/SideBar.Nearby.Tests/VerifiedTests.cs`.

**Interfaces — Produces:** `IncomingTransfer.Verified`, `IncomingTransfer.VerifiedName`; `SessionOptions.RejectUnverified` (true в режиме «Свои»); `SendSession` возвращает `PeerVerified` в `TransferResult`.

- [ ] **Step 1: Тесты (loopback с двумя `CertificateIdentity` на синтетических сертах):** `Own_device_is_verified_before_introduction`; `Introduction_before_paired_key_is_not_verified`; `Forged_signature_is_not_verified`; `Name_like_own_device_but_unknown_cert_is_not_verified`; `RejectUnverified_rejects_without_asking`; `Verified_name_comes_from_certificate_not_endpoint_name`.
- [ ] **Step 2–4:** FAIL → реализовать → PASS. **Step 5:** Коммит + push.

### Task 3: Приложение: режимы видимости, автоприём, «своё» в плитке

**Files:** Modify `src/SideBar.Core/Nearby/VisibilityModel.cs` (+ тесты), `src/SideBar.Core/Nearby/ShareTileModel.cs` (+ тесты: `DeviceView.IsOwn`), `src/SideBar.App/Nearby/{NearbyService,ReceiveHost,Notifications}.cs`, `src/SideBar.App/Tray/TrayIcon.cs`, `Strings.cs`.

- [ ] **Step 1: Тесты Core:** `Visibility_default_is_own_devices_when_certs_available`; `Everyone_returns_to_previous_after_10_min`; `Own_devices_unavailable_falls_back_to_hidden`; `Tile_marks_own_devices`.
- [ ] **Step 2:** Реализовать: трей — радиогруппа «Скрыто / Свои устройства / Все (10 мин)»; `ReceiveHost` — `RejectUnverified` в «Своих», автоприём при `Verified`, уведомление с `VerifiedName`; `NearbyService` — `CertificateIdentity` для отправки при наличии сертов; перезагрузка по `NextExpiry`/`FilesChanged` → перерегистрация рекламы; деградация с одним уведомлением.
- [ ] **Step 3:** Debug/AOT 0 warnings; loopback e2e с двумя идентичностями невозможен на живых сертах (один ПК) — e2e: CLI `send --self` → приложение в «Своих» с синтетическими сертами через `SIDEBAR_CERTS_DIR` override (DEBUG), автоприём без уведомления-вопроса. Коммит + push.

### Task 4: Пауза при работающем официальном приложении

**Files:** Create `src/SideBar.App/Nearby/OfficialAppWatcher.cs`; Modify `ReceiveHost`, `NearbyService`, `ShareTileModel` (+ тест состояния «Официальное приложение открыто»).

- [ ] **Step 1:** Тест Core: `Tile_shows_official_app_running_hint`.
- [ ] **Step 2:** `OfficialAppWatcher` — опрос `Process.GetProcessesByName("nearby_share")` раз в 5 с, событие `RunningChanged`; `ReceiveHost` останавливает рекламу/слушатель, `NearbyService` блокирует отправку с подсказкой.
- [ ] **Step 3:** Проверка: запуск фиктивного процесса с именем `nearby_share.exe` (копия `notepad.exe` во временную папку) → пауза в логе; закрытие → возобновление. Коммит + push.
