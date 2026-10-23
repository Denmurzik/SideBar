# M6 — Bluetooth-транспорт для заблокированного/невидимого по Wi-Fi устройства: план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Отправка с плитки Quick Share работает и тогда, когда своё устройство видно только по Bluetooth-рекламе (`FEF3`, заблокированный экран, режим «Ваши устройства»): плитка показывает его с меткой «своё», отправка идёт по Bluetooth Classic RFCOMM, а для файлов ≥ 1 МБ сессия сама переключается на Wi-Fi LAN, когда получатель это предлагает.

**Architecture:** Протокол (UKEY2/PairedKey/Introduction, `SendSession`/`NearbyConnection`/`CertificateIdentity`) уже не меняется в части, работающей по TCP; он уже рассчитан на произвольный `System.IO.Stream` (`SendSession.SendFilesAsync(Stream, ...)`), так что Bluetooth — это **новый транспорт**, а не новый протокол. Источник MAC-адреса — не живая BLE-реклама (в low power её там нет), а расшифрованные метаданные сертификата (поле 4), как уже проверено вручную в спайке `spikes/ble` (эксперимент E3, `docs/research/ble-locked-phone.md`). BLE-реклама (`FEF3`) используется только для **обнаружения** (какой сертификат телефон использует сейчас) и доверенного `EndpointInfo` для `PairedKeyEncryption` — ровно так же, как сегодня для этого служит mDNS TXT-запись. Bandwidth upgrade поднимается внутри уже установленного зашифрованного канала: `SecureChannel` (D2D keys) не завязан на транспорт — у него свой счётчик последовательности внутри `DeviceToDeviceMessage`, поэтому смена `Stream` под тем же `SecureChannel` ничего не ломает на уровне крипто (подтверждено чтением `src/SideBar.Nearby/Crypto/SecureChannel.cs`); точный кадровый протокол самого переключения — по результатам исследования `BandwidthUpgradeNegotiationFrame` (Task 3).

**Tech Stack:** C# / .NET 10, `net10.0-windows10.0.26100.0` (WinRT: `Windows.Devices.Bluetooth`, `Windows.Devices.Bluetooth.Rfcomm`, `Windows.Devices.Bluetooth.Advertisement`, `Windows.Networking.Sockets`), xUnit.

**Spec:** Задание пользователя 2026-10-23 (сессия BLE, 4 пункта: транспорт в продукт, плитка, bandwidth upgrade, ветка `m6` + `.buildlock`); `docs/research/ble-locked-phone.md` (§2.2–2.4, E1–E3 — проверено на Pixel 9); `spikes/ble/src/{Rfcomm.cs,Program.cs}` и `spikes/nearby/SideBar.Nearby/Session/OfficialCertificates.cs` (рабочий прототип транспорта и вывода MAC, уже проверен на устройстве); `docs/PLAN.md` §3 (архитектура `SideBar.Nearby`/`SideBar.App`).

## Global Constraints

- Ветка `m6` от свежего `main`; перед любой `dotnet build/test` — лок `C:/D/den4ik/SideBar/.buildlock` (`spikes/TEAM-RULES.md`: атомарный `mkdir`, подождать и повторить, если уже занят; `rm -rf` после) и проверка свободной ОЗУ (`powershell -NoProfile -Command "(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB"`, < 3 ГБ — подождать).
- AOT-safe, trim-safe (`IsAotCompatible=true`, `TreatWarningsAsErrors=true` в `Directory.Build.props`); WinRT-проекции (COM) совместимы по умолчанию, но каждая новая WinRT-зависимость проверяется `dotnet build` с 0 предупреждений.
- Секреты: MAC-адрес устройства никогда не печатается и не логируется (как в `spikes/ble`: белый список логов — тип кадра, размеры, enum); `NearbyLog` — единственный канал логов протокола, менять его формат не нужно.
- Коммиты без `Co-Authored-By`-трейлеров; пуш веток в GitHub без доп. вопроса (сами ветки, не прямой пуш в `main` — в `main` только через PR/мердж тимлида после ревью).
- Отправитель (роль SENDER = наш ПК) уже рекламирует `WifiLan` как допустимый upgrade-medium в `OfflineFrames.ConnectionRequest` (`src/SideBar.Nearby/Session/Frames.cs:28`) независимо от транспорта подключения — это не меняется и не добавляется заново.
- Приём (ReceiveSession, слушающий RFCOMM-сервер на ПК) **не в объёме этого плана** — только отправка с ПК на телефон по Bluetooth, как прямо указано в задании.

## Review Focus

1. Телефон рядом с несколькими валидными сейчас сертификатами (ротация раз в сутки, окно перекрытия ±30 мин, как уже наблюдалось в E3 — там было 3 валидных `Pixel 9` одновременно) — выбранный по BLE-рекламе должен быть именно текущим, не любым совпавшим по имени. Тест — Task 2.
2. Устройство видно и по mDNS, и по BLE одновременно (экран то блокируется, то нет) — в плитке должна быть ровно одна иконка на устройство, без дублей и без мигания при переключении источника. Тест — Task 2.
3. BLE-реклама пропала (экран разблокировали, точно так же пропадёт `FEF3`, если выключить Bluetooth) — тайл должен исчезать по таймауту “не видели N секунд”, а не висеть вечно; повторная реклама не должна создавать вторую иконку. Тест — Task 2.
4. RFCOMM-сервис не отвечает (адаптер выключен, устройство ушло, сервис не нашёлся) — `Choose` должен завершиться `Failed`, а не зависнуть и не бросить необработанное исключение на UI-поток. Тест — Task 1 (через фейковый коннектор) и Task 2.
5. Получатель предлагает upgrade на Wi-Fi LAN, когда файлы суммарно < 1 МБ, или вообще не предлагает для больших — обработчик не должен сам требовать апгрейд, только реагировать на то, что реально пришло; таймаут на `UPGRADE_PATH_AVAILABLE`, если TCP до указанного IP:порта недоступен (файрвол/другая подсеть), не должен ронять всю передачу — откат на старый (Bluetooth) канал. Тест — Task 3.

---

### Task 1: Bluetooth RFCOMM-транспорт и вывод MAC из сертификата (`SideBar.Nearby`)

**Files:**
- Create: `src/SideBar.Nearby/Transport/BluetoothTransport.cs` (порт `spikes/ble/src/Rfcomm.cs`: `RfcommClient.ConnectAsync`, `DuplexSocketStream`).
- Modify: `src/SideBar.Nearby/Identity/CertificateIdentity.cs` — добавить `PeerDescriptor` и `TryDescribePeer`.
- Modify: `tests/SideBar.Nearby.Tests/CertFixtures.cs` — `NewDevice` получает необязательный параметр `byte[]? bluetoothMac = null`, при наличии кодирует и поле 4 в `EncryptedMetadata` (сейчас кодируется только поле 1).
- Test: `tests/SideBar.Nearby.Tests/CertificateIdentityTests.cs`.

**Interfaces — Consumes:** `EndpointInfo` (`src/SideBar.Nearby/Wire/EndpointInfo.cs`, не меняется); `PublicCertificateData`, `FindPeer`-эквивалентный поиск уже существует внутри `CertificateIdentity` (приватный `FindPeer(EndpointInfo)`).

**Interfaces — Produces (для Task 2):**
- `public sealed record PeerDescriptor(byte[] SecretId, string? DeviceName, byte[]? BluetoothMac);` в `SideBar.Nearby.Identity`.
- `public PeerDescriptor? CertificateIdentity.TryDescribePeer(EndpointInfo info)` — null, если реклама не опознана; `BluetoothMac` — null, если поле 4 отсутствует или не равно 6 байтам (не у всех сертификатов оно есть).
- `public static class BluetoothTransport` в `SideBar.Nearby.Transport`:
  - `public static readonly Guid NearbySharingServiceId` = `a82efa21-ae5c-3dde-9bbc-f16da7b16c5a` (посчитан и задокументирован в `docs/research/ble-locked-phone.md` §2.3).
  - `public static Task<Stream> ConnectAsync(byte[] mac6, CancellationToken ct)` — бросает `IOException` с человекочитаемым сообщением на любой сбой (адрес не резолвится, сервис не найден, `StreamSocket.ConnectAsync` падает); ничего не возвращает null, только исключение или готовый `Stream`.

- [ ] **Step 1: Написать падающий тест на `TryDescribePeer`**

```csharp
[Fact]
public void TryDescribePeer_decodes_name_and_mac_from_field7_metadata_key()
{
    var mac = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC };
    var device = CertFixtures.NewDevice("Pixel 9", bluetoothMac: mac);
    var identity = new CertificateIdentity(
        new PrivateCertificateData(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(14), /*own key*/ ...),
        [new PublicCertificateData(device.Public.SecretId, device.Public.SecretKey, device.Public.PublicKey,
            device.Public.KeyTag, device.Public.EncryptedMetadata)]);
    var info = /* EndpointInfo, рекламирующее именно device.Public через CreateEndpointInfo-подобную соль */;

    var described = identity.TryDescribePeer(info);

    Assert.NotNull(described);
    Assert.Equal("Pixel 9", described!.DeviceName);
    Assert.Equal(mac, described.BluetoothMac);
}
```

Используй существующий паттерн `CertificateIdentityTests.Pair()`/`NewCert()` для построения пары приватный+публичный сертификат и живого `EndpointInfo` через `a.CreateEndpointInfo(...)` + `b.TryDescribePeer(decoded)`, а не вручную собранный `EndpointInfo` — так тест бьёт по тому же пути, что и реальная реклама.

- [ ] **Step 2: Прогнать тест, убедиться что падает**

Run: `dotnet test tests/SideBar.Nearby.Tests --filter TryDescribePeer_decodes_name_and_mac_from_field7_metadata_key`
Expected: FAIL (`TryDescribePeer` не существует) — взять `.buildlock` перед запуском, отпустить после.

- [ ] **Step 3: Реализовать `PeerDescriptor`/`TryDescribePeer` в `CertificateIdentity.cs`**

Переиспользовать существующий приватный `FindPeer(EndpointInfo)` и статический `DecryptMetadata`/`DeviceName`. Поле 4 (6 байт MAC) читается тем же лёгким протобуф-ридером, что и поле 1 (`ProtoReader.Bytes(fields, 4)`), без новой схемы.

- [ ] **Step 4: Прогнать тест и полный набор `CertificateIdentityTests`, убедиться, что всё проходит**

Run: `dotnet test tests/SideBar.Nearby.Tests --filter CertificateIdentityTests`
Expected: PASS, включая уже существующие тесты (расширение `NewDevice` не должно их задеть — параметр необязательный).

- [ ] **Step 5: Реализовать `BluetoothTransport` (порт `spikes/ble/src/Rfcomm.cs`)**

Один в один по уже проверенному на Pixel 9 коду спайка (`RfcommClient.ConnectAsync`, `DuplexSocketStream`), с той разницей, что `ConnectAsync` не возвращает `(Socket?, string Status)`, а либо успешный `Stream`, либо бросает `IOException`(сообщение = то, что в спайке шло в `Status`) — это нужно `NearbyService` (Task 2), которому проще ловить одно исключение, чем разбирать строку статуса. Никакой автоматический тест здесь не покрывает реальное соединение (нужен настоящий адаптер и телефон) — это и так уже проверено вручную (E3).

- [ ] **Step 6: Собрать весь солюшн, 0 предупреждений**

Run: `dotnet build SideBar.slnx` (под `.buildlock`, `-m:1`)
Expected: Build succeeded, 0 Warning(s), 0 Error(s).

- [ ] **Step 7: Коммит**

```bash
git add src/SideBar.Nearby/Transport/BluetoothTransport.cs src/SideBar.Nearby/Identity/CertificateIdentity.cs \
        tests/SideBar.Nearby.Tests/CertFixtures.cs tests/SideBar.Nearby.Tests/CertificateIdentityTests.cs
git commit -m "feat(nearby): Bluetooth RFCOMM transport and certificate-derived MAC lookup"
git push -u origin m6
```

---

### Task 2: BLE-обнаружение своих устройств и интеграция с плиткой (`SideBar.Nearby` + `SideBar.App`)

**Files:**
- Create: `src/SideBar.Nearby/Discovery/BluetoothOwnDeviceWatcher.cs`.
- Modify: `src/SideBar.App/Nearby/NearbyService.cs` — второй источник обнаружения, унификация `_endpoints`, де-дуп по `SecretId`, маршрутизация `Choose`/`SendAsync` по транспорту.

**Interfaces — Consumes:** Task 1: `CertificateIdentity.TryDescribePeer`, `BluetoothTransport.ConnectAsync`, `BluetoothTransport.NearbySharingServiceId`. Существующее: `EndpointInfo.TryDecode`, `IdentityProvider.Current` (`SideBar.Sharing`), `DeviceView`/`ShareDeviceKind` (`SideBar.Core.Nearby`, не меняются).

**Interfaces — Produces:**
- `public sealed record BluetoothEndpoint(string Id, string? DeviceName, byte[] Mac, EndpointInfo Info, DeviceKind Kind);` в `SideBar.Nearby.Discovery` — `Id` = нижний гекс `PeerDescriptor.SecretId` (стабилен между рекламами разных солей, в отличие от самого `EndpointInfo.Metadata`).
- `public sealed class BluetoothOwnDeviceWatcher(Func<CertificateIdentity?> identity) : IDisposable` — `event Action<BluetoothEndpoint>? Found; event Action<string>? Lost; void Start(); void Dispose();`. `Lost` поднимается не телом рекламы (его там нет), а таймаутом «не видели `StaleAfter` = 15 с» — проверяется раз в 5 с.

- [ ] **Step 1: Написать падающий тест на опознание и устаревание (без реального BLE, через внутренний метод разбора тела)**

Выдели из `BluetoothOwnDeviceWatcher` чистую статическую функцию `internal static BluetoothEndpoint? TryParseAdvertisement(ReadOnlySpan<byte> serviceData, CertificateIdentity identity)` (сканирует небольшое окно смещений, как в `spikes/ble/src/Program.cs`, `Match`, задокументированная гипотеза §2.2: обёртка 8 байт, `EndpointInfo` дальше) — она тестируется без WinRT:

```csharp
[Fact]
public void TryParseAdvertisement_finds_endpoint_info_behind_the_ble_v2_wrapper()
{
    var (identity, info) = /* как в Task 1: CertificateIdentity + EndpointInfo своего устройства */;
    byte[] wrapped = [/* 8 байт обёртки version/pcp/endpoint_id/size, как наблюдалось в E2 */, .. info.Encode()];

    var endpoint = BluetoothOwnDeviceWatcher.TryParseAdvertisement(wrapped, identity);

    Assert.NotNull(endpoint);
    Assert.Equal(Convert.ToHexString(/* ожидаемый SecretId */), endpoint!.Id);
}

[Fact]
public void TryParseAdvertisement_returns_null_for_a_stranger_certificate() { /* реклама не нашего устройства -> null, без исключений */ }
```

- [ ] **Step 2:** Запустить, убедиться что падает (метод не существует).

- [ ] **Step 3: Реализовать `TryParseAdvertisement` и обвязку `BluetoothOwnDeviceWatcher` (`BluetoothLEAdvertisementWatcher`, фильтр `FEF3`, таймер устаревания)**

`Start()` поднимает `BluetoothLEAdvertisementWatcher` аналогично `spikes/ble/src/Program.cs` (`watch`), но без консольного вывода и без `--identify`-перебора всех устройств подряд — сразу `identity()` (текущая идентичность из `Func`) и `TryParseAdvertisement`. При `identity() is null` (сертификаты недоступны) реклама просто игнорируется, без ошибок.

- [ ] **Step 4:** Прогнать тесты, убедиться что проходят.

- [ ] **Step 5: Переписать `NearbyService` на единый `ShareTarget`**

```csharp
abstract record ShareTarget(string Id, EndpointInfo Info, DeviceKind Kind)
{
    public abstract Task<Stream> ConnectAsync(CancellationToken ct);
}
```
`TcpShareTarget`(оборачивает существующий `DiscoveredEndpoint`, подключается как сейчас через `TcpClient`) и `BluetoothShareTarget`(оборачивает `BluetoothEndpoint`, подключается через `BluetoothTransport.ConnectAsync`, `Id = "ble:" + Endpoint.Id`). `_endpoints` становится `Dictionary<string, ShareTarget>`. `OnFound`(mDNS) и новый `OnBleFound` пишут в один словарь под тем же `_gate`; `SendAsync` вызывает `target.ConnectAsync(ct)` вместо инлайнового `TcpClient`.

Де-дуп: держать `HashSet<string> _ownSecretIdsViaWifi` (гекс `SecretId`, получаемый через `identity.TryDescribePeer(endpoint.Info)` и для mDNS-эндпоинтов). `OnBleFound` не добавляет тайл (и убирает уже показанный), если тот же `SecretId` сейчас виден по mDNS; `OnFound`(mDNS), заметив устройство с известным `SecretId`, убирает ранее показанный `"ble:"+id`, если он был.

- [ ] **Step 6:** Собрать `SideBar.App` + `SideBar.Nearby` (под `.buildlock`), 0 предупреждений.

Run: `dotnet build SideBar.slnx`
Expected: Build succeeded, 0 Warning(s), 0 Error(s).

- [ ] **Step 7: Коммит**

```bash
git add src/SideBar.Nearby/Discovery/BluetoothOwnDeviceWatcher.cs src/SideBar.App/Nearby/NearbyService.cs \
        tests/SideBar.Nearby.Tests/
git commit -m "feat(nearby): discover own devices over BLE and route sends to them over Bluetooth"
git push origin m6
```

- [ ] **Step 8: Проверка на устройстве (пользователь, Pixel 9)**

Сценарий как E1/E2/E3 в `docs/research/ble-locked-phone.md`: заблокировать Pixel, перетащить файл < 1 МБ на плитку SideBar.App (не официальное приложение) — телефон должен появиться в плитке с меткой «своё» в пределах ~10 с, клик должен его принять на заблокированном экране. Отдельно проверить, что при разблокированном экране (виден и по mDNS, и по BLE) иконка одна, без дублей/мигания.

---

### Task 3: Bandwidth upgrade на Wi-Fi LAN для файлов ≥ 1 МБ (`SideBar.Nearby`)

**Статус: черновик, уточняется.** Кадры `BandwidthUpgradeNegotiationFrame` (`UPGRADE_PATH_AVAILABLE`, `CLIENT_INTRODUCTION`, `CLIENT_INTRODUCTION_ACK`, `LAST_WRITE_TO_PRIOR_CHANNEL`, `SAFE_TO_CLOSE_PRIOR_CHANNEL`, `UPGRADE_FAILURE`) уже есть в `src/SideBar.Nearby/Protos/offline_wire_formats.proto:185-290` и скомпилированы, но точная очерёдность (кто первым пишет в новый TCP-сокет, переживает ли `SecureChannel` смену потока без нового рукопожатия, как и когда закрывать старый RFCOMM-канал) уточняется по исходникам `google/nearby` (`connections/implementation/bwu_manager.cc`, `base_bwu_handler.cc`, `mediums/wifi_lan_bwu_handler.cc`, клон в `C:/D/den4ik/refs/nearby`). Эта секция будет дописана и самопроверена (Self-Review) отдельным обновлением плана, прежде чем её можно будет выполнять как задачу — пока не начинай Task 3 без этого обновления.

**Известно уже сейчас (из `SecureChannel.cs`):** `_sendSeq`/`_recvSeq` считаются внутри `DeviceToDeviceMessage`, а не на уровне транспорта — значит, подменить `Stream` под тем же `SecureChannel`-объектом безопасно с точки зрения крипто-счётчика; `NearbyConnection` сейчас хранит `_stream` в `readonly`-поле и такой подмены не умеет (понадобится не-readonly поле плюс метод подмены, дожидающийся текущей записи под `_writeLock`).

