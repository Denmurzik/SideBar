# Quick Share по Wi-Fi LAN: что реализовано в спайке S1/S2

Код: `spikes/nearby/` (`SideBar.Nearby` — библиотека, `SideBar.Nearby.Cli` — консоль `nearby.exe`, `SideBar.Nearby.Tests`).
Режим только «Все» (EVERYONE), медиум только Wi-Fi LAN. Сертификаты («Свои устройства») не делались — точка расширения `IShareIdentity`.

## Статус проверки

| Что | Статус |
|---|---|
| Юнит-тесты (62 шт.: framing, endpoint info, mDNS-имя, PIN, HKDF RFC 5869, UKEY2 in-memory, SecureChannel, сессии send<->receive по loopback TCP) | проверено, 62/62 |
| mDNS: `DnsServiceRegister` + `DnsServiceBrowse` + `DnsServiceResolve` на этой машине (своя же реклама находится) | проверено |
| Полный цикл send -> receive между двумя процессами `nearby.exe` через реальный mDNS + TCP, файл 300 КБ побайтно совпал, PIN совпал на обеих сторонах | проверено |
| Отправка ПК -> Pixel 9 в режиме «Все» (PIN совпал, файл принят) | проверено на устройстве |
| Приём Pixel 9 -> ПК в режиме «Все» (PNG 237 КБ целиком) | проверено на устройстве |
| Режим «Свои устройства» (`--self`): отправка и приём с Pixel 9, PairedKey SUCCESS/SUCCESS, файл 2 МБ целый | проверено на устройстве |
| NativeAOT-публикация библиотеки | не проверено (код без рефлексии, P/Invoke через `LibraryImport`, колбэки `UnmanagedCallersOnly`, protobuf только сгенерированный) |

## Схема как реализовано

Роли: получатель = сервер (TCP listener + mDNS-реклама), отправитель = клиент (browse + connect).

1. **mDNS**: тип `_FC9F5ED42C8A._tcp.local`. Имя экземпляра = base64url без паддинга от 10 байт: `0x23`, 4 символа endpoint id, `FC 9F 5E`, `00 00`. TXT `n` = base64url(endpoint info). Windows API: `dnsapi.dll` (`DnsServiceBrowse/Resolve/Register/DeRegister`), P/Invoke без сторонних библиотек.
2. **Endpoint info**: байт `version(3)|hidden(1)|type(3)|reserved(1)`, 16 байт (salt + зашифрованный ключ метаданных; в EVERYONE — случайные), имя (1 байт длины + UTF-8, если не hidden), опциональные TLV (1 = QR, 2 = vendor).
3. **TCP-фрейминг**: 4 байта big-endian длины + protobuf. Лимит кадра 5 МиБ.
4. **Рукопожатие (открытым текстом)**: ConnectionRequest (OfflineFrame, `endpoint_info`, `mediums=[WIFI_LAN]`) и UKEY2 ClientInit -> ServerInit -> ClientFinish -> ConnectionResponse(ACCEPT, OsInfo=WINDOWS) в обе стороны.
5. **UKEY2**: шифр P256_SHA512, next protocol `AES_256_CBC-HMAC_SHA256`. ClientInit содержит commitment = SHA-512(ClientFinish); сервер сверяет. Ключи: ECDH P-256, `shared = SHA-256(raw ECDH)`, `HKDF-SHA256(salt="UKEY2 v1 auth"/"UKEY2 v1 next", ikm=shared, info=ClientInit_raw || ServerInit_raw, 32)` -> auth string и next secret. Координаты X/Y в `EcP256PublicKey` — знаковые big-endian как у Java `BigInteger.toByteArray` (бывает 33 байта с ведущим нулём или меньше 32).
6. **D2D-ключи**: `d2d_client/server = HKDF(salt=SHA256("D2D"), ikm=next, info="client"/"server")`; затем для каждого `ENC:2` и `SIG:1` с salt `SHA256("SecureMessage")`. Клиент шифрует/подписывает client-ключами, расшифровывает server-ключами; сервер наоборот.
7. **SecureMessage**: `HeaderAndBody{Header{HMAC_SHA256, AES_256_CBC, iv(16), public_metadata=GcmMetadata{DEVICE_TO_DEVICE_MESSAGE, v1}}, body=AES-CBC-PKCS7(DeviceToDeviceMessage{seq, OfflineFrame})}` + HMAC-SHA256. Независимые счётчики seq с 1, проверяются строго.
8. **PIN**: Chromium-алгоритм по auth string (знаковые байты, база 31, модуль 9973, abs, 4 цифры).
9. **Payload layer**: Quick Share-кадры идут как BYTES-payload: полный кадр (`LAST_CHUNK=0`) + пустой чанк с `LAST_CHUNK`. Файл — FILE-payload чанками по 512 КиБ + пустой последний чанк.
10. **Quick Share-кадры**: PairedKeyEncryption (случайные 6 + 72 байта) в обе стороны -> PairedKeyResult(UNABLE) в обе стороны -> Introduction (отправитель) -> Response ACCEPT/REJECT (получатель) -> файлы -> Disconnection.
11. **KeepAlive**: исходящий каждые 10 с после включения шифрования.

## Отличия референсов

- **NearDrop** — только приём (macOS), PROTOCOL.md лучший по описанию; из него взяты формат endpoint info, salt-константы, порядок кадров.
- **rquickshare** (GPL, код не копировался, использован как сверка поведения) — обе роли. Его `RemoteDeviceInfo::serialize` пишет тип устройства как `(type << 1) & 0b111`, что обрезает типы >3 и не ставит версию; мы пишем `(version<<5)|(hidden<<4)|(type<<1)` по описанию NearDrop. Для отправителя rquickshare не проверяет ConnectionResponse полноценно; мы проверяем ACCEPT.
- **google/nearby** — источник .proto; полноценный Nearby Connections (медиумы, bandwidth upgrade) здесь не нужен.
- `.proto` скопированы из rquickshare (`core_lib/src/proto_src`), в каждый добавлена только строка `option csharp_namespace` (`SideBar.Nearby.Protocol.{Connections,SecureGcm,SecureMsg,Sharing}`), чтобы не конфликтовать с пространством `SideBar.Nearby`.

## Грабли

- **Знаковые координаты EC**: ошибка в паддинге ломает ~1% рукопожатий (случайно), поэтому в тестах 200 случайных ключей.
- **`ReadSharing` и конец файла**: отправитель шлёт Disconnection сразу за последним чанком; получатель должен проверять «все файлы получены» внутри цикла чтения чанков, иначе Disconnection трактуется как отмена (была такая ошибка, исправлена).
- **Async + unsafe**: C# не позволяет `await` в `unsafe`-контексте — нативные вызовы вынесены в синхронные методы.
- **`DnsServiceResolve` возвращает один IPv4-адрес** (`ip4Address`), причём не обязательно с LAN-адаптера (на этой машине вернулся адрес виртуального адаптера 172.19.x.x). При нескольких адаптерах стоит пробовать подключение ко всем локальным подсетям или резолвить A-записи отдельно (кандидат на доработку в M3).
- **Android может не увидеть службу, зарегистрированную до начала поиска** (по rquickshare) — получатель перерегистрируется каждые 30 с (`--reannounce`). Отдельно проверить: нужен ли BLE-триггер, чтобы телефон сам рекламировал mDNS при отправке на ПК (S5).
- **xUnit v3 + .NET 10**: `dotnet test` через Microsoft.Testing.Platform даёт `Handshake failures` (в `global.json` включён MTP-раннер); надёжно запускать собранный `SideBar.Nearby.Tests.exe` напрямую.
- Имя файла от отправителя недоверенное: берётся только последний сегмент пути, недопустимые символы заменяются, существующие файлы не перезаписываются (`name (1).ext`). Зарезервированные имена Windows (CON, NUL) не обрабатываются.
- `payload_id`/`id` файлов случайные int64; при приёме payload без анонса в Introduction отвергается.

- **`FileMetadata.id` обязателен** (случайный int64, уникальный): без него Pixel 9 обрывал соединение ещё до accept. Ни NearDrop, ни rquickshare его не ставят, но с Android 2026 без него не работает (проверено на устройстве; точная причина не выяснена, возможно совпало с другой ошибкой, но с id работает).
- **Отправитель не закрывает сокет раньше получателя**: после последнего чанка ждать `Disconnection` от получателя (до ~5 с), только потом слать свой и дождаться FIN. Ранний close с непрочитанными входящими данными = TCP RST, телефон показывает «Не удалось передать файл», хотя у нас `Completed`.
- `--trace` в CLI печатает типы кадров и поля сообщений (без ключей и содержимого) — основной инструмент разбора расхождений с телефоном.

- **PairedKey по google/nearby**: `secret_id_hash = HKDF(token, salt = secret_key ПУБЛИЧНОГО серта ПИРА)`, `signed_data` — подпись СВОИМ приватным сертом (prefix 0x01 отправитель / 0x02 получатель), серт пира ищется по рекламе (не по хэшу), `optional_signed_data` — подпись предыдущей видимостью. Прежняя модель «ищем серт по хэшу» давала UNABLE в обе стороны.

## Не сделано (осознанно)

Приём текста/ссылок/Wi-Fi-данных, bandwidth upgrade, QR-код, отмена из UI, проверка свободного места, IPv6, режим «Свои устройства».
