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

`dotnet spikes/ble/bin/Debug/net10.0-windows10.0.26100.0/Ble.dll info | watch [сек] | publish [сек] [notify|silent]`. Печатает только счётчики, типы секций и форму `FE2C`-данных (длина и первые 5 байт после UUID: model id / метаданные, не идентификаторы устройств); адреса и имена не выводятся.
Полезная нагрузка publish — гипотеза: `2C FE FC 12 8E <meta> 00` (`meta = type<<2`).

## 5. Сценарий проверки с телефоном (Pixel 9) — для тимлида

1. Запустить `Ble watch 60`; на телефоне открыть «Поделиться» -> Quick Share (идёт сканирование получателей). Ожидаем рост счётчика `FE2C` со строкой `head=FC128E..`; записать `meta` и длину.
2. Запустить `Ble publish 30 notify`; смотреть, показал ли телефон уведомление/«устройство рядом делится» (на телефоне Quick Share включён, видимость «Контакты/Все»). Повторить с `silent`.
3. Результаты (какие строки `head`, реакция телефона) -> уточним раскладку метаданных.
