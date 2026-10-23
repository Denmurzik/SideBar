# M6 — отправка на свой телефон с погасшим/заблокированным экраном

Дата: 2026-10-23. Исследование по исходникам (google/nearby `c80c38e` от 2026-10-22, Chromium `main`), справке Google и community-реализациям. Код: `spikes/ble/` (варианты `publish` и `watch --identify`; собрано, не запускалось). С телефоном ничего не запускалось, BLE не публиковался.

## Итог

- **Вердикт: (C) — не проверено, но есть конкретный рецепт с сильным обоснованием, близкий к (A).** Fast Initiation (`FE2C`) для заблокированного телефона почти наверняка бесполезен, сколько вариантов ни перебирай. Но разбудить телефон и не нужно: в режиме «Ваши устройства» заблокированный телефон **сам** остаётся доступным, только не по mDNS. Он шлёт BLE-рекламу Nearby (`FEF3`, low power) и **слушает Bluetooth Classic RFCOMM**. Официальный отправитель подключается по RFCOMM к MAC-адресу из **сертификата** телефона, а не из рекламы.
- Google прямо обещает такое поведение: «Your devices: If your screen is off, your device is still visible to your other devices with the same Google Account» ([справка Android](https://support.google.com/android/answer/9286773?hl=en)). Для «Контактов» и «Все» нужен включённый и разблокированный экран. На Chromebook: «If the screen is off or locked, the device is automatically in "Your devices" visibility» ([справка Chromebook](https://support.google.com/chromebook/answer/10751738?hl=en)).
- **Почему наш beacon не сработал** (подтверждено исходниками для Windows/ChromeOS, для Android — по аналогии и по наблюдению). Fast Initiation — это сигнал «рядом кто-то делится», рассчитанный на экран, который включён. Приёмник при заблокированном экране сканирование Fast Init останавливает. Отправитель при заблокированном экране его не шлёт.
- **Рецепт (гипотеза; E1, E2, E5 проверяют её косвенно, транспорт — E3, в спайк не добавлен):**
  1. MAC Bluetooth телефона взять из расшифрованных метаданных его публичного сертификата (поле 4).
  2. Открыть RFCOMM-сокет без сопряжения, сервис `a82efa21-ae5c-3dde-9bbc-f16da7b16c5a` (= `UUID.nameUUIDFromBytes("NearbySharing")`).
  3. Провести тот же протокол, что по TCP: ConnectionRequest -> UKEY2 -> PairedKey -> Introduction. `secret_id_hash` обязан совпасть с **текущим SELF_SHARE-сертификатом телефона**, иначе получатель в режиме «не Все» вернёт `kFail`.
  4. Файлы до 1 МБ идут прямо по Bluetooth. Для больших файлов телефон сам предложит bandwidth upgrade (например, Wi-Fi LAN: его IP и порт), и нам нужно сыграть роль отвечающей стороны.
- **На Windows это реализуемо без драйверов:** WinRT `BluetoothDevice.FromBluetoothAddressAsync`, `GetRfcommServicesForIdAsync(..., Uncached)`, `StreamSocket.ConnectAsync`. Ровно так делает открытая Windows-реализация google/nearby. BLE L2CAP и GATT-сокет на Windows не нужны: в google/nearby для Windows `BleSocket::Connect()` — заглушка, `return false`.

## 1. Что говорит Google (вопрос 1)

| Источник | Что сказано |
|---|---|
| [Android Help 9286773](https://support.google.com/android/answer/9286773?hl=en) | «Your devices: If your screen is off, your device is still visible to your other devices with the same Google Account.» «Contacts: While your screen is on and unlocked…». «To receive content from someone else, make sure your screen is unlocked.» Bluetooth должен быть включён на обоих устройствах. |
| [Chromebook Help 10751738](https://support.google.com/chromebook/answer/10751738?hl=en) | «If the screen is off or locked, the device is automatically in "Your devices" visibility.» «If your Chromebook is locked, you'll still receive the file being transferred.» |
| [Android–Windows Help 13801258](https://support.google.com/android/answer/13801258?hl=en) | Свои устройства принимают автоматически; Bluetooth нужен на обоих устройствах; без BLE Extended Advertising на ПК «other people can't find and share with you on certain networks». |

Вывод: сценарий «ПК -> свой заблокированный телефон» Google поддерживает официально, но только для видимости «Ваши устройства» и только для отправителя того же аккаунта. Для отправителя «Все» он невозможен в принципе: при заблокированном экране телефон рекламирует SELF_SHARE-сертификат, а анонимного отправителя отвергает (см. §2.4).
Форумных подтверждений «Windows -> заблокированный Pixel работает» найти не удалось; это первый эксперимент (E1).

## 2. Механизм по исходникам (вопрос 2)

### 2.1 Fast Initiation (`FE2C`) — не механизм пробуждения заблокированного

- Раскладка: `sharing/internal/api/fast_init_ble_beacon.h` (`kFastInitServiceUuid {0xfe,0x2c}`, `kFastInitModelId {0xfc,0x12,0x8e}`, `FastInitType {kNotify=0, kSilent=1}`, `kAdvertiseDataTotalSize = 26`). Байт 23 — флаги `require_bt_advertising | self_only_advertising`. Порядок битов не задокументирован; в спайке добавлен `--flags HEX`.
- **Отправитель Windows** (`sharing/nearby_sharing_service_impl.cc`). `StartFastInitiationAdvertising()`, стр. 1620–1635, всегда шлёт `kSilent`. `InvalidateFastInitiationAdvertising()`, стр. 1961–2008, **останавливает beacon при `is_screen_locked_`**, без foreground send surface и во время передачи. Реализация `FastInitiationManager` (байты, salt, `secret_id_hash`) **в открытом коде отсутствует**: в репозитории только интерфейс `sharing/internal/api/fast_initiation_manager.h` и фейки. Алгоритм `secret_id_hash` (8 Б) и salt (1 Б) из открытых исходников **не восстановить**. Наш `--hash selfshare` — по-прежнему догадка.
- **Отправитель ChromeOS** (`chrome/browser/nearby_sharing/fast_initiation/fast_initiation_advertiser.cc`, `RegisterAdvertisement` и `GenerateFastInitV1Metadata`). Короткая форма из 5 байт: `FC 12 8E`, `(version<<5)|(type<<2)` (биты uwb и sender_cert пустые), `-kAdjustedTxPower = 66 (0x42)`. Плюс `FE2C` в **списке service UUID** (AD 0x03), тип `ADVERTISEMENT_TYPE_BROADCAST`. Тип: `kNotify` (`nearby_sharing_service_impl.cc` Chromium, стр. 1648, TODO «kNotify or kSilent»).
- **Приёмник ChromeOS** (`fast_initiation_scanner.cc`, `StartScanning`). Аппаратный фильтр по Service Data с шаблоном `2C FE FC 12 8E` (UUID в обратном порядке + model id), `Range::kNear` (то есть важен RSSI: близко), таймауты found 1 с / lost 7 с. В `nearby_sharing_service_impl.cc` Chromium (`InvalidateFastInitiationScanning`, около стр. 2445): «Stopping background scanning because the screen is locked». Салт и хэш приёмник ChromeOS **не проверяет**: фильтр идёт только по префиксу.
- Android (GMS) закрыт. Наблюдаемое (rquickshare README): beacon лишь заставляет Android «показаться» в mDNS, пока экран включён, и то нерегулярно. Логика приёмника — «уведомление/переход в high power, когда рядом кто-то делится», а это имеет смысл только при включённом экране.

Вывод: перебор вариантов `FE2C` (notify/silent, хэш, extended) не решает задачу «заблокированный экран». Fast Init стоит проверить только как ускоритель для **включённого** экрана (E4).

### 2.2 Что делает заблокированный телефон-приёмник

`sharing/nearby_sharing_service_impl.cc`, `InvalidateAdvertisingState()`, стр. 2012–2110:
- при заблокированном экране реклама разрешена только при `supports_advertising_on_lock_screen`. На Windows это флаг Backup + sync bindings, на Android это включённые «Ваши устройства» (по справке);
- `if (is_screen_locked_ && supports_advertising_on_lock_screen) visibility = DEVICE_VISIBILITY_SELF_SHARE;` — реклама переключается на **SELF_SHARE-сертификат**;
- без foreground receive surface `power_level = kLowPower`;
- `advertising_on_screen_locked_ = is_screen_locked_` передаётся в `PairedKeyVerificationRunner` (§2.4).

`sharing/nearby_connections_manager_impl.cc`, `StartAdvertising`, стр. 168–258:
- `MediumSelection(bluetooth = is_high_power, ble = true, …)`, `enable_bluetooth_listening = use_ble = true`;
- если `endpoint_info.size() <= kMinimumAdvertisementSize` (1+2+14 = 17 Б, то есть без имени, как у SELF_SHARE), используется **fast advertisement** на UUID `0000fef3-0000-1000-8000-00805f9b34fb`.

`connections/implementation/p2p_cluster_pcp_handler.cc`:
- `ShouldAdvertiseBluetoothMacOverBle()` (стр. 119) возвращает true только при high power: в low power **MAC в BLE-рекламе нет**;
- `StartBleAdvertising` (около стр. 2290–2400): при `enable_bluetooth_listening` поднимается `bluetooth_medium_.StartAcceptingConnections(service_id)` — **RFCOMM-сервер работает и в low power**;
- Wi-Fi LAN (mDNS) в десктопном C++ и в low power разрешён: `StartAdvertisingImpl` (стр. 177+) проверяет только `allowed.wifi_lan`; запрет при `low_power` есть лишь в `UpdateAdvertisingOptionsImpl` (стр. 1618, 1671). Android (закрытый Java-стек) ведёт себя иначе: в фоне и при погасшем экране mDNS пропадает (наблюдение S5, README rquickshare/NearDrop). Поэтому то, что mDNS выключен именно политикой low power, — вывод по наблюдению, а не по коду.

Формат fast advertisement (`connections/implementation/ble_advertisement.h`): `[version(3)|pcp(5)] [endpoint_id 4] [endpoint_info_size 1] [endpoint_info ≤17] [uwb_size][uwb][extra]`, без service_id_hash и без MAC. Windows-сканер ловит его в секции 0x16 с UUID `FEF3` (`internal/platform/implementation/windows/ble.cc`, `InitializeWithServiceFilter` и обработчик около стр. 1100–1210).

### 2.3 Как официальный отправитель подключается к нему

1. Discovery по BLE находит fast advertisement `FEF3`. Endpoint info расшифровывается перебором публичных сертификатов (как у нас для mDNS).
2. `sharing/outgoing_share_session.cc`: `GetBluetoothMacAddressForShareTarget` (стр. 120) вызывает `GetBluetoothMacAddressFromCertificate` (`sharing/nearby_sharing_util.cc`, стр. 48). Это **`unencrypted_metadata().bluetooth_mac_address()` расшифрованного публичного сертификата** (6 Б; в наших данных это поле 4 `EncryptedMetadata`, у 9/9 чужих сертификатов, см. `certs.md`).
3. `NearbyConnectionsManagerImpl::Connect` (стр. 343–420): `allowed_mediums.bluetooth = true`, `remote_bluetooth_mac_address` передаётся в `RequestConnection`.
4. `connections/implementation/base_pcp_handler.cc`, `AppendRemoteBluetoothMacAddressEndpoint` (стр. 2364–2405), добавляет к найденному по BLE endpoint'у Bluetooth-endpoint с этим MAC. Подключение идёт по RFCOMM: `mediums/bluetooth/bluetooth_classic.cc`, `Connect` -> `ConnectToService(device, GenerateUuidFromString(service_id))`, UUID = `Uuid(data)` = MD5, версия 3 (`internal/platform/uuid.cc`, стр. 113), для `"NearbySharing"` это **`a82efa21-ae5c-3dde-9bbc-f16da7b16c5a`** (посчитано).
5. Windows-реализация RFCOMM (`internal/platform/implementation/windows/bluetooth_classic_medium.cc`, `ConnectToService`, стр. 159; `bluetooth_classic_device.cc`, `GetRfcommServiceForIdAsync`, `BluetoothCacheMode::Uncached`; `bluetooth_classic_socket.cc`, `InternalConnect`, `StreamSocket.ConnectAsync(ConnectionHostName, ConnectionServiceName)`). **Сопряжение не требуется**, есть только проверка доступа `HaveAccess(device_id)`.
6. Upgrade: `sharing/incoming_share_session.cc`, `TryUpgradeBandwidth` (стр. 472). Его инициирует **получатель** после accept, если сумма файлов ≥ `kAttachmentsSizeThresholdOverHighQualityMedium = 1000000` (`sharing/constants.h:55`). Для Wi-Fi LAN получатель шлёт `BANDWIDTH_UPGRADE_NEGOTIATION / UPGRADE_PATH_AVAILABLE` со своим `ip_address` + `wifi_port` (`connections/implementation/mediums/wifi_lan_bwu_handler.cc`). Отправитель подключается туда по TCP и продолжает в новом канале. Набор medium'ов берётся из пересечения с нашим `ConnectionRequest.mediums`.

Следствие для нас: BLE-discovery **не обязателен**. MAC и сертификат телефона у нас уже есть (leveldb). Discovery нужен только для того, чтобы точно знать, каким сертификатом телефон рекламирует себя сейчас (§2.4).

### 2.4 Проверка PairedKey при заблокированном экране — ловушка

`sharing/paired_key_verification_runner.cc`:
- `VerifyAuthTokenHashWithPrivateCertificates` (стр. 300–331): при `screen_locked_advertising` первым проверяется **SELF_SHARE** private cert. Сравнивается `HashAuthenticationTokenWithPrivateCertificate(visibility, token)`, то есть `HKDF(token, salt = secret_key ТЕКУЩЕГО валидного приватного серта)`, 6 Б;
- `OnReadPairedKeyEncryptionFrame` (стр. 136–155): если хэш не совпал (`kUnable`), а соединение входящее и видимость не EVERYONE, то **`kFail`** («Incoming connection with non-everyone visibility cannot verify public certificate. Treating as kFail»).

Значит, отправитель обязан вычислить `secret_id_hash` с публичным сертификатом, который соответствует **текущему SELF_SHARE-сертификату телефона**. Сейчас `CertificateIdentity.CreatePairedKeyEncryption` выбирает серт пира по его рекламе (`Identify(remote)`); без рекламы он шлёт случайные 6 байт, и телефон ответит FAIL. Варианты:
- (a) поймать fast advertisement `FEF3` заблокированного телефона и опознать сертификат (E2);
- (b) без BLE взять серт Pixel, чьё окно `[start, end)` содержит «сейчас». При неоднозначности (несколько валидных) пробовать по очереди: каждая попытка — новое RFCOMM-соединение.
Подпись телефона мы проверим перебором его публичных сертов: подпись сходится ровно с одним.

### 2.5 Прочее (BLE v2, DCT, Presence, облако)

- **DCT** (`connections/implementation/mediums/advertisements/dct_advertisement.h`) — реклама с именем устройства и L2CAP PSM (`DctAdvertisement::Create(service_id, device_name, psm, dedup)`). Создаётся только при наличии имени в endpoint info (видимость «Все») и `client->IsDctEnabled()`. Для SELF_SHARE не применяется; для Windows бесполезна, LE L2CAP CoC в WinRT нет.
- **BLE v2 / GATT**: обычная (не fast) реклама больше 27 Б отдаётся через GATT-характеристику, а в эфир идёт только заголовок. SELF_SHARE-реклама укладывается в fast-формат, GATT не нужен. WinRT GATT-клиент есть, если понадобится.
- **Nearby Presence**: в `sharing/certificates/nearby_share_certificate_manager_impl.cc` есть только `QuerySharedCredentials` / `PerVisibilitySharedCredentials` — канал доставки **тех же** публичных сертификатов (`SharedCredential::DATA_TYPE_PUBLIC_CERTIFICATE`). Отдельной BLE-идентичности Presence в Quick Share нет. Поле 11 в наших публичных сертах — вероятно, оттуда (не разбиралось).
- **Облачный/FCM-«wake»**: в открытом коде нет. Есть WebRTC-medium (`ShouldEnableWebRtc`, Tachyon) для *передачи* через интернет, но для `kLowPower`-рекламы он отключён (`ShouldUseInternet` возвращает false при `PowerLevel::kLowPower`, `nearby_connections_manager_impl.cc`, стр. 65–73). Пути «разбудить через сервер Google» не найдено.

## 3. Community-реализации (вопрос 3)

| Проект | Отправка на заблокированный телефон | Детали |
|---|---|---|
| rquickshare (`core_lib/src/hdl/blea.rs`) | нет | Только Wi-Fi LAN. Шлёт `FE2C` с дампом Android-отправителя (`bluer::adv::Type::Broadcast`, только service data), чтобы Android включил mDNS. README: «Android doesn't broadcast its mDNS service all the time»; телефон «appears and disappears randomly». Про заблокированный экран ничего. |
| NearDrop | нет | README: «Wi-Fi LAN only… Bluetooth needs further reverse engineering»; «Android listens for specific BLE broadcasts to automatically become visible, and macOS doesn't allow apps to send them». Обходной путь — QR-код. |
| Прочие (Packet и др. на `rqs_lib`) | нет | Наследуют ограничения rquickshare. |

Ни одна известная открытая реализация не поддерживает Bluetooth-транспорт Quick Share, и ни одна не заявляет отправку на заблокированный телефон.

## 4. Что рекламировать / куда подключаться (вопрос 4)

- **Для заблокированного телефона рекламировать ничего не нужно.** Нужно подключиться: RFCOMM, MAC из сертификата, UUID `a82efa21-ae5c-3dde-9bbc-f16da7b16c5a`, insecure (без сопряжения). Дальше тот же кадровый протокол (4 байта длины + OfflineFrame; `BaseEndpointChannel` один для всех medium'ов).
- **Fast Init для включённого экрана** (по ChromeOS-приёмнику): фильтр по Service Data `2C FE FC 12 8E…` без проверки хэша, то есть открытый, notify-тип; плюс RSSI «near». Android-фильтр неизвестен. Если он сделан через `ScanFilter.setServiceUuid`, нужен ещё AD 0x03 со списком UUID (как у ChromeOS). Для этого в спайке есть `--uuidlist` и `--short`. Интервал и длительность WinRT не позволяет задавать («best effort»).
- **Фильтрация по аккаунту** в Fast Init (`secret_id_hash`, `self_only_advertising`) существует в формате, но алгоритм закрыт. Для нашей задачи она не нужна.

## 5. Windows: возможности и ограничения (вопрос 5)

| Что | Можно? | Как / почему |
|---|---|---|
| Service Data 0x16 в `BluetoothLEAdvertisementPublisher` | да (проверено в S5) | Хотя [документация](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.advertisement.bluetoothleadvertisementpublisher) перечисляет 0x16 среди «system-reserved», исключение бросают только свойства `Flags`/`LocalName`. |
| Список UUID 0x02/0x03 | не проверено | Тоже в списке reserved. `--uuidlist` пробует `adv.ServiceUuids.Add(FE2C)` и печатает исключение, если оно будет. |
| Extended advertising | да на этом адаптере (`IsExtendedAdvertisingSupported`, MaxAdvertisementDataLength=160) | `UseExtendedAdvertisement` (19041+). Интервал, connectable, длительность не задаются. |
| Сканирование `FEF3` | да | `BluetoothLEAdvertisementWatcher` (как в S5; так же делает `windows/ble.cc`). |
| GATT-клиент | да | WinRT `GattDeviceService`; для SELF_SHARE fast-рекламы не нужен. |
| BLE L2CAP CoC / GATT-сокет Nearby | **нет** | В WinRT нет API для LE L2CAP CoC. В google/nearby для Windows `BleSocket::Connect()` возвращает `false`. |
| RFCOMM-клиент к несопряжённому устройству | да (так делает google/nearby для Windows) | `BluetoothDevice.FromBluetoothAddressAsync(mac)`, затем `GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(a82efa21-…), BluetoothCacheMode.Uncached)`, затем `StreamSocket.ConnectAsync(svc.ConnectionHostName, svc.ConnectionServiceName)`. Возможен запрос согласия на доступ к устройству (`DeviceAccessInformation`); на этой машине не проверено (см. E3). |
| Скорость по RFCOMM | ограничена | BT Classic даёт порядка сотен КБ/с (оценка, не измерялось). Поэтому для больших файлов нужен BWU (E5). |

## 6. Эксперименты с Pixel 9 (по порядку)

Подготовка один раз:
- на Pixel: Настройки -> Google -> Quick Share -> «Кто может делиться с вами» = **«Ваши устройства»**; Bluetooth и Wi-Fi включены; тот же аккаунт, что у официального приложения на ПК; Pixel лежит в 1–2 м от ПК;
- на ПК: Bluetooth включён, ПК и Pixel в одной Wi-Fi-сети; PowerShell, `cd C:\D\den4ik\SideBar\spikes`;
- «заблокировать» в шагах ниже значит: нажать кнопку питания (экран погас, телефон заблокирован) и не трогать телефон ≥ 30 с;
- SideBar и официальное приложение одновременно не запускать. В каждом эксперименте сказано, что должно быть запущено.

**E1. Контроль: официальное Quick Share for Windows -> заблокированный Pixel.** Кода не нужно.
1. Закрыть SideBar (трей -> Выход). Запустить «Quick Share» из меню Пуск.
2. Pixel: заблокировать.
3. ПК: Проводник -> файл ~300 КБ -> ПКМ -> «Отправить с помощью Quick Share» -> выбрать Pixel.
4. Повторить с файлом ~20 МБ.
5. Закрыть официальное приложение (трей -> Выход).
Смотреть: виден ли Pixel в списке и через сколько секунд; дошёл ли файл, пока экран не включался (после разблокировки проверить в «Файлах»); время передачи 20 МБ. Логи `%LOCALAPPDATA%\Google\Nearby\Sharing\Logs` не открывать.
Решение: если Pixel не виден даже официальному приложению, вердикт (B), дальше не идём.

**E2. Что рекламирует заблокированный Pixel.** Только приём BLE, ничего не публикуется.
1. Pixel: заблокировать.
2. ПК: `dotnet ble\bin\Debug\net10.0-windows10.0.26100.0\Ble.dll watch 60 --identify`
3. Pixel: разблокировать, выключить Bluetooth, снова заблокировать. ПК: та же команда ещё раз.
4. Pixel: включить Bluetooth обратно.
Команда печатает список публичных сертов (`peer cert #N: device=… validNow=… hoursLeft=…`), затем строки `FEF3 xK: len=… <тип рекламы> own-account=yes|no cert=#N device=… offset=… rssi=[..] first=…s` и итог `FEF3 distinct bodies=… matched=…`.
Смотреть:
- есть ли в прогоне шага 2 строки `own-account=yes` с device = Pixel 9 и исчезают ли они в прогоне шага 3;
- какой `cert=#N` совпал: это сертификат, которым Pixel сейчас рекламирует себя;
- тип рекламы и `len`.
Ожидание по исходникам: при заблокированном экране `own-account=yes` есть, но `nearby\SideBar.Nearby.Cli\bin\Debug\net10.0-windows10.0.26100.0\nearby.exe discover --self --timeout 30` Pixel не находит (нет mDNS). Если `FEF3` есть, а `matched=0`, значит раскладка тела другая; сравнить `len` с §2.2.

**E3. Транспорт к заблокированному Pixel (Bluetooth Classic).** В спайк не добавлено.
Механизм описан в §2.3 и §2.4 по открытому коду google/nearby. Нужна ли отдельная доработка, решает тимлид. Эксперимент должен показать:
- найден ли сервис Nearby на Pixel без сопряжения;
- открылось ли соединение;
- ответил ли Pixel на начало рукопожатия;
- результат PairedKey;
- принят ли файл < 1 МБ, пока экран заблокирован.

Про сопряжение (вопрос тимлида):
- Windows-реализация google/nearby (`bluetooth_classic_medium.cc`, `ConnectToService`) подключается к **несопряжённому** устройству через WinRT. Она получает устройство по адресу, ищет RFCOMM-сервис с `BluetoothCacheMode::Uncached` и проверяет только `DeviceAccessInformation` (`HaveAccess`). Pairing она не запрашивает.
- Android-сторона Nearby, по всей видимости, слушает insecure RFCOMM, тоже без pairing.
- На нашей машине это **не проверено**. Документация Microsoft ([Bluetooth RFCOMM](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/send-or-receive-files-with-rfcomm)) описывает только сценарий с сопряжённым устройством. Поведение для несопряжённого устройства и возможный запрос согласия в ней не описаны.
- Путь через Winsock `AF_BTH` google/nearby не использует. Подходит ли он для несопряжённого Pixel, по источникам не установлено.

**E4. Калибровка Fast Init при ВКЛЮЧЁННОМ экране.** Публикация BLE, варианты уже в спайке.
1. Pixel: разблокирован, на главном экране, Quick Share НЕ открыт.
2. Для каждого варианта по очереди: окно A — `dotnet ble\bin\Debug\net10.0-windows10.0.26100.0\Ble.dll publish 60 <вариант>`; окно B через 5 с — `nearby\SideBar.Nearby.Cli\bin\Debug\net10.0-windows10.0.26100.0\nearby.exe discover --self --timeout 40`; между вариантами пауза 30 с.
3. Варианты: `--static`; `--short --uuidlist`; `--short`; `--ext --type silent`; `--ext --flags 40`; `--ext --flags 80`.
4. Лучший вариант повторить с заблокированным Pixel.
Смотреть: найден ли Pixel в discover и через сколько секунд; было ли на Pixel уведомление «Устройство поблизости делится». Если `--uuidlist` печатает `ServiceUuids.Add threw` или `Start threw`, значит WinRT запрещает 0x03; это тоже результат.
Ожидание: при включённом экране часть вариантов срабатывает, при заблокированном реакции нет (§2.1).

**E5. Скорость официального пути.** Кода не нужно, расширение E1.
1. Запустить официальное приложение, Pixel заблокировать. Параллельно запустить `dotnet ble\bin\Debug\net10.0-windows10.0.26100.0\Ble.dll watch 90 --identify`. Официальное приложение может занять адаптер: если watcher остановится, будет строка `watcher stopped`.
2. Отправить файл ~100 МБ, засечь время.
Смотреть: скорость в МБ/с. Порядка 0,1–0,2 МБ/с — передача осталась на Bluetooth; единицы и десятки МБ/с — был upgrade на Wi-Fi. От этого зависит, нужен ли в M6 ответчик bandwidth upgrade (§2.3, п. 6).

## 7. Изменения спайка

`spikes/ble/src/Program.cs`:
- `publish --short` — 5-байтовая форма ChromeOS (`FC 12 8E | type<<2 | tx`);
- `publish --uuidlist` — дополнительно `FE2C` в `ServiceUuids` (AD 0x03, как у ChromeOS), исключение печатается;
- `publish --flags HEX` — последний байт 24-байтовой формы (`require_bt_advertising | self_only_advertising`). Порядок битов неизвестен, пробовать `80` и `40`;
- `watch --identify` — опознание тел `FEF3` по публичным сертам. Перебираются все смещения, с которых может начинаться 17-байтовый endpoint info. Печатаются счётчики, длина, смещение, RSSI, время и device_name совпавшего серта.

`spikes/ble/src/PeerCerts.cs` — загрузка сертов только в память. Файлы читаются с `FileShare.ReadWrite | Delete`, leveldb разбирается продуктовым `LevelDbReader` (подключён ссылкой из `src/SideBar.Nearby/Certs`), на диск ничего не пишется. Из метаданных читается только device_name.
Собрано (0 предупреждений), не запускалось.

## 8. Риски и не проверено

- Android-сторона (GMS) закрыта. Перенос поведения десктопного C++ (BLE + RFCOMM listening в фоне, SELF_SHARE на lock screen) на Android — гипотеза; её подкрепляют справка Google и наблюдение «mDNS пропадает».
- MAC в сертификате может не совпасть с текущим адресом Pixel (рандомизация BT Classic не применяется, но не проверено).
- WinRT может запросить у пользователя согласие на доступ к Bluetooth-устройству при первом RFCOMM.
- Неверный SELF_SHARE-серт телефона даёт FAIL. Как Pixel реагирует на серию FAIL (антиабуз, блок-лист) — неизвестно; попытки делать с паузами.
- Автоприём на заблокированном экране для «своих» обещан только справкой Chromebook; на Android его проверяет E1.
