# M6 — Bluetooth Classic для своих устройств: план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Собственный телефон пользователя (видимость «Ваши устройства»), заблокированный или без Wi-Fi LAN, появляется в плитке Quick Share как «своё» и принимает файлы по Bluetooth Classic RFCOMM; файлы ≥ 1 МБ после приёма переключаются на Wi-Fi LAN, если приёмник это предлагает.

**Architecture:** Протокол (UKEY2/PairedKey/Introduction, `SendSession`/`NearbyConnection`) уже транспорт-агностичен — принимает произвольный `System.IO.Stream`. Три слоя добавляются поверх него: (1) транспорт — WinRT RFCOMM-клиент без сопряжения в `SideBar.Nearby`, портированный из подтверждённого на Pixel 9 спайка `spikes/ble/src/Rfcomm.cs`; (2) обнаружение — BLE-сканер `FEF3`, опознающий только свои сертификаты (presence-сигнал), MAC и данные для рукопожатия берутся из сертификата, а не из рекламы (доказано в E3, `docs/research/ble-locked-phone.md`); (3) bandwidth upgrade — реактивная обработка кадра `BandwidthUpgradeNegotiationFrame` от приёмника, переключение `NearbyConnection` на новый Wi-Fi LAN TCP-поток при сохранении того же `SecureChannel`.

**Tech Stack:** C# / .NET 10, `net10.0-windows10.0.26100.0` (WinRT: `Windows.Devices.Bluetooth`, `Windows.Devices.Bluetooth.Rfcomm`, `Windows.Networking.Sockets`), Google.Protobuf (кадры уже сгенерированы из `Protos/offline_wire_formats.proto`), xUnit.

**Spec:** `docs/research/ble-locked-phone.md` (разделы 2–4, 6 E1–E3, §7 код спайка); для bandwidth upgrade — исходники `C:/D/den4ik/refs/nearby/connections/implementation/{bwu_manager.cc,base_pcp_handler.cc}`, `.../proto/offline_wire_formats.proto` (см. Task 3 Constraints — конкретные находки ниже).

## Global Constraints

- Ветка `m6-bluetooth` от свежего `main` (создана). Коммиты — без Co-Authored-By трейлера, пушить сразу после коммита (без вопроса). Сборки/тесты — по одной под `C:/D/den4ik/SideBar/.buildlock` (`mkdir` атомарно, `rm -rf` после; проверить свободную RAM ≥ 3 ГБ перед `dotnet build/test`; при нехватке — подождать или дробить прогон).
- TFM уже глобальный (`Directory.Build.props`): `net10.0-windows10.0.26100.0`, `TreatWarningsAsErrors=true` — 0 предупреждений на каждом шаге коммита.
- RFCOMM: сервис `a82efa21-ae5c-3dde-9bbc-f16da7b16c5a` (`UUID.nameUUIDFromBytes("NearbySharing")`), подключение **без сопряжения** (`BluetoothCacheMode.Uncached`, `SocketProtectionLevel.PlainSocket`) — проверено на реальном заблокированном Pixel 9 (E3).
- MAC и данные для `RemoteEndpointInfo` Bluetooth-цели строятся из сертификата (`EncryptedMetadata` поля 1/4, расшифрованные ключом поля 7), не из живой BLE-рекламы — подтверждено в E3 (§2.3 «discovery необязателен»). Живая BLE-реклама (`FEF3`) используется только как presence-сигнал «телефон сейчас рядом», не как источник данных для рукопожатия.
- BLE-сканирование ограничено своими сертификатами (`CertificateIdentity.Identify`); анонимные/чужие устройства по BLE — вне скоупа M6.
- Секреты (MAC, ключи, salt) никогда не логировать — то же правило, что в `spikes/ble` и во всём проекте (`NearbyLog` — только тип кадра/размер/enum).
- Bandwidth upgrade: только медиум `WIFI_LAN`; мы никогда не инициируем upgrade сами, только реагируем на предложение приёмника (симметрично роли «кто принял входящее соединение» в Connections — см. Task 3).

## Review Focus

1. Телефон ушёл из радиуса/выключил Bluetooth между кликом по плитке и подключением → `RfcommClient.ConnectAsync` бросает, путь ошибки такой же, как при пропаже TCP-устройства (`Strings.SendFailed`), плитка не виснет в «отправке». Тест — Task 1.
2. Один физический телефон виден одновременно по mDNS (экран включился) и по BLE (сертификат тот же) → показывается один тайл, mDNS в приоритете. Тест — Task 2.
3. У телефона несколько одновременно валидных self-share сертификатов (ротация, см. E3: «3 валидных Pixel 9») → `FindBluetoothTargets` не выдаёт дублирующихся тайлов для одного MAC. Тест — Task 1.
4. Приёмник предлагает upgrade не на `WIFI_LAN` (`WIFI_HOTSPOT`/`WIFI_DIRECT`/др.) или не прислал `client_introduction_ack`, когда мы его ждали → отправитель не ломается, продолжает на Bluetooth без апгрейда. Тест — Task 3.
5. TCP-подключение к `ip:port` из `UPGRADE_PATH_AVAILABLE` не удалось (файрвол/другая подсеть/таймаут) → сессия не виснет и не теряет уже отправленные байты, завершается на старом Bluetooth-канале с понятным результатом. Тест — Task 3.

---

### Task 1: Bluetooth-транспорт и цели в `SideBar.Nearby`

**Files:**
- Create: `src/SideBar.Nearby/Transport/BluetoothTransport.cs` (порт `spikes/ble/src/Rfcomm.cs`, без Console-вывода).
- Modify: `src/SideBar.Nearby/Identity/CertificateIdentity.cs` — расширить `PublicCertificateData`, добавить `BluetoothTarget` и `FindBluetoothTargets()`/`TryGetPeerKey()`.
- Modify: `src/SideBar.Nearby/Identity/OfficialCertificateStore.cs:329` — передать `c.MetadataEncryptionKey` в `PublicCertificateData`.
- Test: Modify `tests/SideBar.Nearby.Tests/CertFixtures.cs` (`NewDevice`: опциональный `byte[]? bluetoothMac = null`); Create `tests/SideBar.Nearby.Tests/CertificateIdentityBluetoothTests.cs`.

**Interfaces — Produces:**
- `PublicCertificateData(byte[] SecretId, byte[] SecretKey, byte[] PublicKeySpki, byte[] KeyTag, byte[]? EncryptedMetadata = null, byte[]? MetadataEncryptionKey = null)` — новый 6-й позиционный параметр (поле 7 сертификата; самим self-share сертификатам он приходит открытым, как уже используется в `OfficialCertificateStore.PeerName`/`CertificateIdentity.DecryptMetadata`).
- `public sealed record BluetoothTarget(string Key, string? DeviceName, byte[] Mac, EndpointInfo RemoteInfo)` в `CertificateIdentity.cs`. `Key` = `Convert.ToHexString(SecretId)` — стабильный идентификатор устройства, одинаковый что при обнаружении через mDNS (`TryGetPeerKey`), что через BLE presence (Task 2), что из `FindBluetoothTargets` (Task 1) — на нём строится дедуп.
- `public IReadOnlyList<BluetoothTarget> FindBluetoothTargets()` на `CertificateIdentity` — для каждого `peers`-сертификата с непустым `MetadataEncryptionKey` и `EncryptedMetadata.Length >= 16`: расшифровать `DecryptMetadata(cert.SecretKey, cert.MetadataEncryptionKey, cert.EncryptedMetadata)` (существующий приватный метод — сделать `internal` при необходимости), прочитать поле 4 (`ProtoReader.Bytes(fields, 4)`, ожидается 6 байт — иначе сертификат пропускается, не ошибка) как MAC и поле 1 как имя (`DeviceName`, уже есть). Собрать свежий `RemoteInfo = new EndpointInfo { Metadata = [..salt(2 случайных байта), ..NearbyCrypto.Ctr(cert.SecretKey, salt, cert.MetadataEncryptionKey)] }` — тот же шифр, что в `CreateEndpointInfo`, но ключом *этого* сертификата, а не своего; он будет опознан обратно той же `FindPeer`, см. E3. Группировать результат по MAC (`Convert.ToHexString(mac)`), оставляя одну запись на MAC (Review Focus #3) — при нескольких валидных сертификатах с разными MAC (должно быть одно устройство → один MAC) взять первый.
- `public string? TryGetPeerKey(EndpointInfo info)` на `CertificateIdentity` — `FindPeer(info) is { } cert ? Convert.ToHexString(cert.SecretId) : null`, иначе `null`. Используется в Task 2 и для дедупа mDNS/BLE.
- `public static class RfcommClient` в `SideBar.Nearby.Transport`: `public static readonly Guid NearbySharingServiceId = Guid.Parse("a82efa21-ae5c-3dde-9bbc-f16da7b16c5a")`; `public static Task<Stream> ConnectAsync(byte[] mac6, CancellationToken ct)` — WinRT `BluetoothDevice.FromBluetoothAddressAsync` (MAC как big-endian `ulong`, старший байт первым) → `GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(NearbySharingServiceId), BluetoothCacheMode.Uncached)` → при `Error != Success` или 0 сервисов — `IOException` с коротким описанием (без MAC в тексте) → `new StreamSocket().ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName, SocketProtectionLevel.PlainSocket)` → при исключении оборачивать/пробрасывать как `IOException`, так, чтобы вызывающий код (Task 2) ловил его тем же `catch` путём, что и `TcpClient.ConnectAsync`. Возвращает готовый `Stream` (внутри — приватный `DuplexSocketStream`, как в спайке: `AsStreamForRead`/`AsStreamForWrite`, `Dispose` закрывает оба + сокет). Не печатать/не логировать MAC — тот же запрет, что в `spikes/ble`.

- [ ] **Step 1: Тест (дедуп по MAC и чтение поля 4):**

```csharp
[Fact]
public void FindBluetoothTargets_reads_mac_and_dedupes_same_mac()
{
    var mac = Convert.FromHexString("AABBCCDDEEFF");
    var device = CertFixtures.NewDevice("Pixel 9", bluetoothMac: mac);
    var identity = new CertificateIdentity(
        new PrivateCertificateData(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(14), /*any own key pair*/ ownPkcs8),
        [new PublicCertificateData(device.Public.SecretId, device.Public.SecretKey, device.Public.PublicKey,
            device.Public.KeyTag, device.Public.EncryptedMetadata, device.Public.MetadataEncryptionKey)]);

    var targets = identity.FindBluetoothTargets();

    Assert.Single(targets);
    Assert.Equal(mac, targets[0].Mac);
    Assert.Equal("Pixel 9", targets[0].DeviceName);
    Assert.True(identity.Identify(targets[0].RemoteInfo));
    Assert.Equal(targets[0].Key, identity.TryGetPeerKey(targets[0].RemoteInfo));
}
```

- [ ] **Step 2:** Запустить тест, убедиться, что он падает (`FindBluetoothTargets`/`TryGetPeerKey`/`bluetoothMac` ещё не существуют).
- [ ] **Step 3:** Реализовать правки `CertFixtures.NewDevice` (добавить поле 4 в protobuf-тело перед шифрованием — тег `(4 << 3) | 2 = 0x22`, длина, 6 байт MAC — только если `bluetoothMac != null`), `PublicCertificateData`, `FindBluetoothTargets`, `TryGetPeerKey`, правку `OfficialCertificateStore.cs:329`.
- [ ] **Step 4:** Прогнать под `.buildlock`: `dotnet test tests/SideBar.Nearby.Tests --filter CertificateIdentityBluetoothTests`. Ожидание: PASS, 0 предупреждений при полной сборке решения.
- [ ] **Step 5: Создать `BluetoothTransport.cs`** (сигнатуры выше). Автотеста нет (WinRT Bluetooth не эмулируется; как и `DnsSdNative`/`QuickShareBrowser`, проверяется на устройстве в Task 2).
- [ ] **Step 6:** Собрать решение под `.buildlock` (`dotnet build`, 0 warnings). Коммит + push в `m6-bluetooth`.

### Task 2: BLE-presence и плитка «своё» по Bluetooth

**Files:**
- Create: `src/SideBar.Nearby/Discovery/BleOwnDeviceWatcher.cs`.
- Modify: `src/SideBar.App/Nearby/NearbyService.cs` — вторая цель поиска, маршрутизация `Choose`/`SendAsync` на Bluetooth, дедуп с mDNS.

**Interfaces — Consumes:** Task 1 (`RfcommClient.ConnectAsync`, `CertificateIdentity.FindBluetoothTargets/TryGetPeerKey/Identify`), существующие `IdentityProvider`, `SendSession.SendFilesAsync`, `DeviceView(string Id, string Name, ShareDeviceKind Kind, bool IsOwn)`.

**Interfaces — Produces:**
- `public sealed record BlePresence(string Key, string? DeviceName)` и `public sealed class BleOwnDeviceWatcher(Func<CertificateIdentity?> identity) : IDisposable` в `SideBar.Nearby.Discovery` — `event Action<BlePresence>? Found`, `event Action<string /*Key*/>? Lost`, `void Start()`.
  Реализация: `BluetoothLEAdvertisementWatcher` (как в `spikes/ble` `watch`), фильтр на секцию Service Data `0x16` с UUID `0xFEF3`; для каждого тела рекламы и каждого смещения `o` в `0 .. body.Length - 17` (как доказанный в E2 перебор — точная раскладка обёртки не нужна) взять `new EndpointInfo { Metadata = body.Slice(o + 1, 16).ToArray() }`, вызвать `identity().Identify(candidate)`; при совпадении — `Key = identity()!.TryGetPeerKey(candidate)!`, `DeviceName` через уже существующий `TryGetPeerName`. Presence-таймер на Key: без повторной рекламы **15 с** (рекламный интервал по E2 — первый кадр на 3,1 с, видно многократно за 60 с) — `Lost(key)`.
- `NearbyService`: новое поле `Dictionary<string, CertificateIdentity.BluetoothTarget> _bleTargets`; `BleOwnDeviceWatcher? _ble`. В `StartShare`, рядом с `QuickShareBrowser`: если `_identity.Current is { } id`, запустить `_ble = new BleOwnDeviceWatcher(() => _identity.Current)`, подписать `Found`/`Lost`. В `OnBlePresenceFound(epoch, presence)`: если `Key` уже покрыт действующим mDNS-эндпоинтом (новый приватный `string? KeyOf(DiscoveredEndpoint e)` = `OwnIdentity(e)?.TryGetPeerKey(e.Info)`, сравнить по всем `_endpoints.Values`) — игнорировать (mDNS в приоритете, Review Focus #2); иначе — взять цель из `identity.FindBluetoothTargets()` по совпавшему `Key`, положить в `_bleTargets[key]`, поднять `DeviceFound` с `new DeviceView("ble:" + key, DisplayText.SanitizeName(presence.DeviceName, Strings.OwnDevice), ShareDeviceKind.Phone, IsOwn: true)`. В `OnFound` (mDNS) — если тот же `Key` уже показан через `_bleTargets`, убрать BLE-тайл (`DeviceLost("ble:" + key)`, удалить из `_bleTargets`) перед показом mDNS-варианта.
- `Choose(id)`: если `id` начинается с `"ble:"` — взять цель из `_bleTargets`, завершить поиск (остановить и `_browser`, и `_ble`), запустить `SendBluetoothAsync`; иначе — текущий путь без изменений.
- `SendBluetoothAsync(epoch, target, filesTask, ct)`: `var stream = await RfcommClient.ConnectAsync(target.Mac, ct)` под тем же `ConnectTimeout`, что и TCP; `var options = new SessionOptions(DeviceNames.Resolve(...), DeviceKind.Laptop, _identity.Current) { RemoteEndpointInfo = target.RemoteInfo, RequireVerifiedPeer = true }` (своё устройство — верификация обязательна, как для mDNS own); остальное — тот же `SendSession.SendFilesAsync` + маппинг `TransferResult.Status` на `Strings.*`, что в существующем `SendAsync`. Вынести общую часть (вызов `SendFilesAsync` + маппинг результата) в приватный helper, общий для TCP- и Bluetooth-пути, чтобы не дублировать строки 298–310 `NearbyService.cs`.

- [ ] **Step 1:** Реализовать `BleOwnDeviceWatcher` и правки `NearbyService` по спецификации выше.
- [ ] **Step 2:** Собрать решение под `.buildlock`, 0 предупреждений.
- [ ] **Step 3 (на устройстве, координирует тимлид с пользователем):** Pixel 9 заблокирован, тот же аккаунт. Запустить SideBar (не официальное приложение), сделать drop файла на плитку Quick Share. Смотреть: появляется тайл с подписью «своё» и иконкой телефона примерно через время первой живой рекламы BLE (по E2 ≈ 3–10 с); клик → файл доходит на заблокированный экран (как в E3, но уже из продукта, не спайка).
- [ ] **Step 4 (на устройстве):** Разблокировать Pixel на середине поиска (mDNS включается) → тайл не дублируется, устройство видно один раз (Review Focus #2).
- [ ] **Step 5:** Коммит + push в `m6-bluetooth`.

### Task 3: Bandwidth upgrade на Wi-Fi LAN для Bluetooth-сессий

**Files:**
- Modify: `src/SideBar.Nearby/Session/NearbyConnection.cs` — возможность заменить поток под тем же `SecureChannel`.
- Modify: `src/SideBar.Nearby/Session/SendSession.cs` — реакция на предложение апгрейда в фоновом читателе (`WatchPeerAsync`).
- Modify: `src/SideBar.Nearby/Session/TransferTypes.cs` — опциональный хук подключения для апгрейда (тестируемость без реального Wi-Fi).
- Test: Create `tests/SideBar.Nearby.Tests/BandwidthUpgradeTests.cs`.

**Constraints (из `C:/D/den4ik/refs/nearby`, Apache-2.0, Connections v1; наша сторона всегда «кто получил `UPGRADE_PATH_AVAILABLE`», симметрично коду для исходящей BWU-сессии в `bwu_manager.cc`):**
- Инициирует upgrade только сторона, принявшая входящее соединение (`base_pcp_handler.cc:2549-2550`, `pending_connection_info.is_incoming`) — у нас это всегда телефон (приёмник); мы, отправитель по Bluetooth, только реагируем и никогда не шлём `UPGRADE_PATH_AVAILABLE` сами.
- Кадр — существующий в проекте `Conn.BandwidthUpgradeNegotiationFrame` (`Protos/offline_wire_formats.proto`, уже сгенерирован, доп. proto-работа не нужна). `EventType`: `UpgradePathAvailable=1`, `LastWriteToPriorChannel=2`, `SafeToClosePriorChannel=3`, `ClientIntroduction=4`, `UpgradeFailure=5`, `ClientIntroductionAck=6`. `UpgradePathInfo.Medium.WifiLan=5`; `UpgradePathInfo.WifiLanSocket{IpAddress bytes, WifiPort int32}` (адрес — 4 или 16 байт, MSB-first; `AddressCandidates` игнорировать — не наш случай). `ClientIntroduction{EndpointId string, SupportsDisablingEncryption bool, LastEndpointId string}`; `ClientIntroductionAck{}`; `SafeToClosePriorChannel{StaFrequency int32}` (поле не используем).
- Последовательность на приёме `UPGRADE_PATH_AVAILABLE(WifiLan{ip,port})` на СТАРОМ (Bluetooth) канале (`bwu_manager.cc:820-964`, `RunUpgradeProtocol:753-817`):
  1. Поставить новые записи в очередь на СТАРЫЙ канал на пауза (ничего нового туда не писать после этого шага).
  2. Открыть TCP к `ip:port` (мы — клиент, телефон слушает).
  3. На НОВОМ сокете, **как есть, без шифрования** (хост ещё не знает, какому endpoint принадлежит это соединение) записать один plaintext-кадр `OfflineFrame{V1{Type=BandwidthUpgradeNegotiation, BandwidthUpgradeNegotiation{EventType=ClientIntroduction, ClientIntroduction{EndpointId=<наш endpoint id из ConnectionRequest>}}}}` (4-байтовая длина + тело, тот же `FrameIO`, что у `ConnectionRequest`). Если `upgrade_path_info.SupportsClientIntroductionAck` — прочитать один plaintext-кадр и ожидать `ClientIntroductionAck` (игнорировать прочее по типу, 5-секундный таймаут — дальше как обычный `IOException`).
  4. Записать на СТАРЫЙ (Bluetooth) канал, через тот же `SecureChannel`, что уже на нём включён, кадр `BandwidthUpgradeNegotiation{EventType=LastWriteToPriorChannel}` — это наш последний write туда.
  5. Дождаться на СТАРОМ канале (тот же читающий цикл, что уже слушает Cancel/Disconnection) кадра `BandwidthUpgradeNegotiation{EventType=LastWriteToPriorChannel}` **от телефона** → ответить на СТАРОМ канале `BandwidthUpgradeNegotiation{EventType=SafeToClosePriorChannel}`.
  6. Дождаться на СТАРОМ канале `BandwidthUpgradeNegotiation{EventType=SafeToClosePriorChannel}` от телефона → закрыть старый (Bluetooth) `Stream`, с этого момента все чтения/записи (`PayloadTransfer` и т. д.) идут по новому TCP-потоку.
  - Шаги 4–6 — симметричный «дренаж»: оба конца гарантированно получают всё, что было отправлено в старый канал, прежде чем он закрывается (комментарий `bwu_manager.cc:1336-1339`: Android-сокеты Bluetooth при закрытии бросают `IOException` на другом конце — закрывать можно только после явного подтверждения).
- **Крипто переживает переключение как есть.** `bwu_manager.cc:764-768`: тот же UKEY2-производный контекст (у нас — тот же объект `SecureChannel`, с тем же monotonic AES-GCM nonce/counter) используется и для старого, и для нового канала; счётчик не сбрасывается и новый handshake не нужен. Значит: `NearbyConnection` должен получить метод (например `internal void SwapStream(Stream newStream)` или аналог) который меняет `_stream`, не трогая `_channel` (`SecureChannel`), и safely останавливает/перезапускает keep-alive-цикл на новом потоке. Текущий конструктор `NearbyConnection(Stream stream, string role, SessionOptions options)` — `_stream` сделать заменяемым (не `readonly`), `_writeLock`/`_channel` не трогать.
- Хук для подключения к Wi-Fi LAN должен быть внедряемым (тестируемость без реальной сети): добавить в `SessionOptions` `Func<IPEndPoint, CancellationToken, Task<Stream>>? ConnectWifiLanUpgradeAsync { get; init; }`, по умолчанию `null` → `SendSession` использует реальный `new TcpClient(); await client.ConnectAsync(ep.Address, ep.Port, ct); return client.GetStream();`; тест подставляет коннектор на локальный `TcpListener` (как `Link` в `tests/SideBar.Nearby.Tests/SessionTests.cs`).
- Если `upgrade_path_info.Medium != WifiLan`, или TCP-подключение/`ClientIntroduction`-рукопожатие не удалось (таймаут/`IOException`) — просто проигнорировать апгрейд и продолжить передачу на текущем (Bluetooth) канале; **не** считать это ошибкой всей передачи (Review Focus #4, #5). Поймать это на уровне `WatchPeerAsync`/нового кода, не давая исключению прервать `SendAllAsync`.

- [ ] **Step 1: Тест — апгрейд на Wi-Fi LAN не теряет и не дублирует кадры:**

```csharp
[Fact]
public async Task SendSession_upgrades_to_wifi_lan_mid_transfer_without_losing_bytes()
{
    using var oldLink = await Link.OpenAsync(ct);          // имитирует Bluetooth-канал
    using var newListener = new TcpListener(IPAddress.Loopback, 0);
    newListener.Start();
    var options = new SessionOptions("PC") { ConnectWifiLanUpgradeAsync = (ep, ct) => /* подключиться к newListener через TcpClient */ };

    var sendTask = SendSession.SendFilesAsync(oldLink.Client, options, [testFilePath], ct: ct);
    // На стороне receiver (управляем oldLink.Server напрямую + принимаем newListener):
    // 1. довести протокол до encrypted-состояния (как FakeReceiver), затем accept;
    // 2. на oldLink.Server (через тот же SecureChannel) отправить BandwidthUpgradeNegotiation{UpgradePathAvailable, WifiLan{127.0.0.1, newListener.Port}};
    // 3. accept на newListener, прочитать plaintext ClientIntroduction, ответить ClientIntroductionAck;
    // 4. прочитать на oldLink.Server LastWriteToPriorChannel, ответить тем же, прочитать SafeToClosePriorChannel, ответить тем же;
    // 5. дальше читать PayloadTransfer с НОВОГО (newListener-accepted) сокета до последнего чанка.
    var result = await sendTask;
    Assert.Equal(TransferStatus.Completed, result.Status);
    // и: все байты файла пришли ровно один раз, в правильном порядке, по новому каналу.
}
```

(Полная реализация теста — завести небольшой `FakeUpgradingReceiver` рядом с `FakeReceiver` в `tests/SideBar.Nearby.Tests/SessionTests.cs`, управляющий обоими сокетами; тот же `SecureChannel.ForServer`, что и у `FakeReceiver.AcceptAsync`, применяется к `newListener`-соединению для продолжения шифрования.)

- [ ] **Step 2:** Запустить, убедиться, что падает (`ConnectWifiLanUpgradeAsync`, `SwapStream`, обработка `BandwidthUpgradeNegotiation` ещё не существуют).
- [ ] **Step 3:** Реализовать `NearbyConnection.SwapStream`, `SessionOptions.ConnectWifiLanUpgradeAsync`, обработку кадра в `SendSession` (в читающем цикле, который уже видит `Cancel`/`Disconnection` — расширить тем же способом тип распознаваемых кадров) по протоколу из Constraints.
- [ ] **Step 4:** Тест теста из Step 1 — PASS. Добавить тест на Review Focus #4/#5 (неподходящий медиум; TCP-подключение падает) — сессия заканчивается нормальным `TransferResult` на старом канале, не исключением.
- [ ] **Step 5:** Собрать решение под `.buildlock`, 0 предупреждений.
- [ ] **Step 6 (на устройстве, координирует тимлид с пользователем):** Отправить на заблокированный Pixel 9 по Bluetooth-плитке файл ≥ 2 МБ (аналог E5, но из продукта). Смотреть: скорость — единицы/десятки МБ/с (апгрейд состоялся) против 0,1–0,2 МБ/с (остался на Bluetooth); результат — `Completed`.
- [ ] **Step 7:** Коммит + push в `m6-bluetooth`. Обновить `docs/research/ble-locked-phone.md` (новый раздел «M6 в продукте») и `docs/PLAN.md` §6 (M6 — из «отложено» в статус реализации).
