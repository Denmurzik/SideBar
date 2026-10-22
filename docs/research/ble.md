# S5 — BLE на Windows (Fast Initiation / «пробуждение»)

Дата: 2026-10-22. Код: `spikes/ble/` (`net10.0-windows10.0.26100.0`, WinRT). Сценарий с телефоном — у тимлида.

## Итог

- **Поправка к HANDOFF/PLAN**: Fast Initiation — это service UUID **`0xFE2C`** (UUID Fast Pair), а не `0xFEF3`. `0xFEF3` — рекламa Nearby Connections (Copresence, `ble_utils.cc: kCopresenceServiceUuidMsb = 0x0000FEF300001000`); её ловит `BleMedium` Windows-реализации (`windows/ble.cc`, секция 0x16 Service Data). Подтверждено: `sharing/internal/api/fast_init_ble_beacon.h` (`kFastInitServiceUuid = {0xfe,0x2c}`), `rquickshare/.../hdl/ble.rs` (фильтр `0000fe2c-...`, «кто-то рядом делится» -> будим приёмник).
- Адаптер этой машины (Intel Wireless Bluetooth): LE, central+peripheral роли, **extended advertising поддержан**, `MaxAdvertisementDataLength=160`, offload поддержан (проверено `Ble info`).
- **Watcher работает**: за 15 с видно 576 реклам; секция `0x16` — 91; с UUID `0xFE2C` — 31 (в т.ч. extended), `0xFEF3` — 4 (проверено).
- **Publisher принимает Service Data (0x16)**: `BluetoothLEAdvertisementPublisher` с секцией `0x16`/`FE2C` перешёл `Created -> Waiting -> Started` без ошибок, `Stopped` по `Stop()` (проверено 3 с). Документация MS перечисляет 0x16 среди «зарезервированных», но исключение даёт только установка свойств `Flags`/`LocalName`; открытый код google/nearby (`windows/ble.cc`, `DataSections().Append(0x16...)`) делает то же — на практике работает.
- **Не проверено**: что телефон реагирует на нашу публикацию (нужен Pixel); что именно шлёт Android; формат полезной нагрузки — по гипотезе.

## 1. Что шлёт Android (по референсам)

Fast Initiation (`fast_init_ble_beacon.h`, V1): AD Service Data (0x16), UUID `FE2C` (little endian `2C FE`), далее:
`FC 12 8E` (model id, фиксирован) + 1 байт `[version(3) | type(3) | uwb(1) | sender_cert(1)]` + 1 байт `adjusted_tx_power`; в расширенной форме (`kAdvertiseDataTotalSize = 26`) далее могут идти UWB-метаданные/адрес, salt(1), secret_id_hash(8). Типы: `kNotify = 0` (телефон показывает уведомление получателю), `kSilent = 1` (тихий режим, для устройств того же аккаунта). Точная раскладка расширенной части — не прослежена (реализация менеджера на Windows у google/nearby закрыта: в репозитории только интерфейс `FastInitiationManager`).
Наблюдение в эфире: `FE2C`-реклам с телом 11–14 байт (начало `00 ..`) и 24 байта в extended — это другие устройства/форматы (не `FC128E`); для распознавания Fast Init фильтровать по префиксу `FC 12 8E` после UUID (гипотеза).

## 2. Что можно на Windows

| Задача | Возможно? | Как |
|---|---|---|
| Ловить Fast Init телефона (`FE2C`, тело `FC128E..`) | да (проверено: watcher видит `FE2C`) | `BluetoothLEAdvertisementWatcher`, `ScanningMode=Active`, `AllowExtendedAdvertisements=true`, разбор секции 0x16 (как в `windows/ble.cc`) |
| Публиковать `FE2C` Service Data | да (Start/Started без ошибок) | `BluetoothLEAdvertisementPublisher` с секцией 0x16; Flags/LocalName задать нельзя; `UseExtendedAdvertisement=false` для совместимости (legacy 31 байт: flags добавляет сама ОС) |
| Реклама Nearby (`FEF3`) для обнаружения без Wi-Fi LAN | теоретически (extended, до 160 Б) | не нужна в v1 (Wi-Fi LAN/mDNS) |
| `IsAnonymous` | доступно с 19041; скрывает адрес устройства в заголовке — для Fast Init не нужно | |

Ограничения WinRT: «best effort» политика ОС, нельзя задать интервал; Flags/LocalName/TxPower свойствами запрещены; зарезервированные секции в доке (в т.ч. 0x16) формально «не разрешены», но принимаются; на других адаптерах поведение может отличаться (нет extended -> только legacy 31 Б, `IsExtendedAdvertisingSupported` проверять).

## 3. Применение в SideBar

- **Получение (телефон -> ПК)**: watcher по `FE2C`/`FC128E` как триггер: телефон открыл «Поделиться» -> ПК (в режиме видимости, если разрешено) включает mDNS-рекламу/слушающий сокет и показывает peek панели. Аналог `rquickshare BleListener` (фильтр не чаще раза в 30 с).
- **Отправка (ПК -> телефон)**: публикация Fast Init (`notify`) на время отправки заставляет телефон показать «устройство рядом хочет поделиться»; для self-share, вероятно, нужен `silent` + `sender_cert`/secret_id_hash (раскладка не установлена -> зависит от S4).

## 4. Спайк

`dotnet spikes/ble/bin/Debug/net10.0-windows10.0.26100.0/Ble.dll info | watch [сек] | publish [сек] [опции]`. Печатает только счётчики, типы секций и форму `FE2C`-данных; адреса и имена не выводятся. Для пакетов с телом `FC128E...` в `watch` печатается тело целиком (salt и hash эфемерные, идентификаторов нет) + RSSI.

### Раскладка Fast Initiation V1 (уточнено, 24 байта после UUID `2C FE`)

Источник: `sharing/internal/api/fast_init_ble_beacon.h` (google/nearby) + `rquickshare/core_lib/src/hdl/blea.rs`, где зашит байтовый дамп настоящего Android-отправителя; совпадает с наблюдением в эфире (`len=24 head=FC128E0142`, расширенная неподключаемая реклама):

| Смещение | Размер | Поле |
|---|---|---|
| 0 | 3 | model id `FC 12 8E` |
| 3 | 1 | `version(3) | type(3) | uwb(1) | sender_cert(1)`: `0x01` = V1, notify, без UWB, sender_cert=1 |
| 4 | 1 | adjusted tx power (`0x42` у Android) |
| 5 | 1 | uwb metadata (0) |
| 6 | 8 | uwb address (нули) |
| 14 | 1 | salt |
| 15 | 8 | secret_id_hash (у Android — хэш сертификата отправителя; у rquickshare — зашитые 8 байт) |
| 23 | 1 | флаги `require_bt_advertising | self_only_advertising` (0) |

Предыдущая гипотеза (`FC128E <type<<2> 00`, 7 байт) была неверной — не хватало длины и положения битов (type в битах 4..2, sender_cert в бите 0). rquickshare будит Android именно рассылкой этих 24 байт (`Broadcast`, не подключаемая) — «устройство рядом делится» -> Android включает mDNS-службу (README rquickshare: без этого телефон светится нерегулярно).

### Опции publish

| Опция | Смысл |
|---|---|
| `--static` | дословный дамп rquickshare (проверенный на практике вариант), остальное игнорируется |
| `--type notify|silent` | тип (биты 4..2): 0 / 1 |
| `--cert 0|1` | sender_cert_supported (бит 0), по умолчанию 1 |
| `--tx HEX` | adjusted tx power, по умолчанию `42` |
| `--hash random|selfshare` | secret_id_hash: случайный или **гипотеза** HKDF-SHA256(ikm=salt, salt=secret_key SELF_SHARE-серта, 8 Б) (в открытых исходниках не описано) |
| `--ext` / (по умолчанию legacy) | расширенная или обычная реклама (наблюдаемый Android — extended) |
| `--raw HEX` | произвольное тело после UUID |

## 5. Сценарий проверки с телефоном (Pixel 9)

Критерий успеха: телефон, который сам ничего не рекламирует (экран «Получить» НЕ открыт), после нашего beacon начинает отвечать по mDNS, и `nearby discover` его находит; либо появляется уведомление «устройство рядом делится».
Команды (два окна PowerShell, из `C:\D\den4ik\SideBar\spikes`):
1. Контроль без beacon: `nearby\SideBar.Nearby.Cli\bin\Debug\net10.0-windows10.0.26100.0\nearby.exe discover --timeout 15` — телефон (экран включён, разблокирован, Quick Share видимость «Все») обычно не найден.
2. Окно A: `dotnet ble\bin\Debug\net10.0-windows10.0.26100.0\Ble.dll publish 60 --static`. Окно B (через 3-5 с): `nearby\SideBar.Nearby.Cli\bin\Debug\net10.0-windows10.0.26100.0\nearby.exe discover --timeout 30`. Смотреть: появился ли Pixel 9; реакция телефона (уведомление).
3. Если нет: варианты (по 60 с, discover параллельно): `publish 60 --ext` (extended как у Android), `publish 60 --type silent --ext`, `publish 60 --ext --hash selfshare` (для «Ваших устройств»: `nearby discover --self`), `publish 60 --legacy --cert 0`.
4. Для сверки: `Ble watch 60` при открытом на телефоне «Поделиться -> Quick Share» печатает тела `FE2C` телефона целиком (`body=...`): наши варианты можно сравнить побайтно (`salt`/`hash` эфемерные).
