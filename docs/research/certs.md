# S3 — формат сертификатов официального Quick Share

> Актуальные решения — `docs/PLAN.md`; требования к продукту по итогам ревью — `docs/research/review-2026-10-22.md`.
> Часть утверждений ниже (статусы «не проверено», размеры/вариант A в ui.md) устарела — источник истины PLAN.


Дата: 2026-10-22. Код: `spikes/certs/` (C#, `net10.0`, без NuGet-зависимостей). Парсер печатает только сводку (счётчики, длины, номера полей, булевы проверки).
Источник протокола — `google/nearby` (`sharing/certificates/*`, `sharing/proto/rpc_resources.proto`, `encrypted_metadata.proto`, `enums.proto`), Apache-2.0.

## Итог

Формат **совпадает с открытым google/nearby один в один**. Проверено на живых данных этой машины:

- свои приватные сертификаты читаются из `preferences.json` (открытый JSON, без DPAPI/шифрования);
- публичные сертификаты читаются из leveldb (значение = голый protobuf `PublicCertificate`);
- криптография воспроизводится на стандартном `System.Security.Cryptography` (NativeAOT-совместимо): `id == SHA256(secret_key)`, подпись/проверка ECDSA P-256 (DER), AES-CTR для metadata key, AES-GCM для метаданных, HMAC-тег ключа — все проверки сошлись.

## 1. `preferences.json`

Корень — плоский JSON-объект (14 ключей, ~6.6 КБ). Значимые:

| Ключ | Тип | Смысл |
|---|---|---|
| `nearby_sharing.private_certificate_list` | array[6] | свои приватные сертификаты (см. ниже) |
| `nearby_sharing.device_id` | string(10) | id устройства (не печатаем значение) |
| `nearby_sharing.background_visibility`, `..._fallback_visibility`, `..._expiration_seconds` | number | текущая видимость в UI приложения |
| `nearby_sharing.users` | object[3] | `current_user`, `app_info_1/2` — идентификаторы аккаунта (не использовать) |
| `nearby_sharing.scheduler.{upload_local_device_certificates,download_public_certificates,private_certificate_expiration,public_certificate_expiration,get_account_info}` | object{a,f,p,s,w} | состояние планировщиков (время последнего/следующего запуска) |
| `nearbyshare.public_certificate_expiration_dict` | object[15] | `base64url(secret_id)` -> срок; **ровно те же 15 id, что в leveldb** (проверено) |

### Элемент `private_certificate_list` (= `PrivateCertificateData`, `NearbySharePrivateCertificate::ToCertificateData`)

| Поле JSON | Кодирование | Размер | Значение |
|---|---|---|---|
| `visibility` | int | — | `DeviceVisibility`: **2 = SELF_SHARE, 3 = ALL_CONTACTS** (`4 = EVERYONE` отдельного серта не имеет — берётся ALL_CONTACTS) |
| `not_before`, `not_after` | int | — | Unix **наносекунды** (`absl::ToUnixNanos`); окно 72 ч |
| `key_pair` | base64url (web-safe, с `=`-паддингом или без) | 138 Б | PKCS#8 `PrivateKeyInfo`, EC P-256 |
| `secret_key` | base64url | 32 Б | AES-256 ключ сертификата |
| `metadata_encryption_key` | base64url | 14 Б | ключ метаданных |
| `id` | base64url | 32 Б | `SHA256(secret_key)` (проверено) |
| `unencrypted_metadata` | base64url | ~230 Б | protobuf `EncryptedMetadata` (расшифрованный вид): поля 1 device_name, 2 full_name, 3 icon_url, 4 bluetooth_mac(6 Б), 6 account_name, 8 vendor_id |
| `consumed_salts` | **hex**-строка | 4 hex-символа на соль | 2-байтовые соли, уже потраченные на `EncryptMetadataKey` (до 32768) |

Состав на этой машине: 6 сертификатов = 2 видимости x 3 (текущий + запас вперёд, `kNearbyShareNumPrivateCertificates = 3`). Сейчас валидны (окно содержит «сейчас») 2 из 6 — по одному на видимость; остальные прошлые/будущие. У SELF_SHARE-серта уже использовано 14 солей.

## 2. leveldb `NearbySharePublicCertificateDatabase`

- Стандартный LevelDB (CURRENT -> `MANIFEST-000028`, 3 `.ldb`, 1 пустой `.log`), блоки **без Snappy** (35 raw-блоков, 0 сжатых), но декомпрессор реализован на случай смены.
- **Ключ** = голые 32 байта `secret_id` (без префикса), равен `PublicCertificate.secret_id` во всех 15 записях.
- **Значение** = сериализованный `nearby.sharing.proto.PublicCertificate` без обёрток.
- 15 записей (+5 tombstone в истории), все `for_self_share = true`, `binding_id` пуст, все структурно валидны (`secret_id` 32, `secret_key` 32, `public_key` 91 Б = SPKI P-256, tag 32).
- Из них **6 — наши собственные** (id совпадают с id приватных сертификатов): сервер вернул их обратно как self-share. **9 — других устройств аккаунта (Pixel)**; валидны сейчас 3, самый поздний конец окна через ~77 ч.
- Поля `PublicCertificate`: 1 secret_id, 2 secret_key, 3 public_key (SPKI), 4/5 start/end (`Timestamp.seconds`; Java-время/1000), 7 metadata_encryption_key, 8 encrypted_metadata_bytes, 9 metadata_encryption_key_tag, 10 for_self_share. **Неожиданность**: поле 7 (`metadata_encryption_key`) присутствует во всех 15, хотя в `.proto` написано, что для чужих устройств оно не возвращается — для self-share сервер его отдаёт.
- **Недокументированное поле 11** (~596 Б, вложенное сообщение; поля 1,2,3,4,5,6,7,9,12,13,15,16,17,18,19) есть у 9 записей чужих устройств; в открытом proto оно `reserved 11`. Содержимое не разбирали; для расшифровки/подписи не нужно. Гипотеза: данные нового механизма Nearby Presence / identity credentials (в `NearbyShareCertificateManagerImpl` встречается `PerVisibilitySharedCredentials`).
- Расшифрованные метаданные чужих устройств: поля 1 device_name, 2 full_name, 3 icon_url, 4 bluetooth_mac, 6 account_name, 7 model_name, 8 vendor_id, 9 icon_token (все 9 расшифровались).

## 3. Как это использует google/nearby (для S4)

Константы (`constants.h`): валидность серта 72 ч; смещения границ `[0,2ч)`; допуск публичных 30 мин; salt 2 Б; metadata key 14 Б; tag 32 Б; secret_key 32 Б; id 32 Б; хэш токена 6 Б.

**Реклама (мы — отправитель/получатель, свой приватный серт).** `EncryptMetadataKey()`: выбрать неиспользованную 2-байтовую соль -> `iv = HKDF-SHA256(salt, no salt, no info, 16 Б)` -> `AES-256-CTR(secret_key, counter=iv)` шифрует 14-байтовый `metadata_encryption_key` -> пара (salt, 14 Б). Эта пара кладётся в endpoint info (`advertisement.cc`):
`byte0 = (version=0 <<5) | (hasDeviceName?0:1 <<4) | (deviceType <<1)`, затем salt(2) + encrypted_metadata_key(14), опционально `len + device_name`, TLV vendor_id (type 2, len 1), TLV capabilities (type 3). Для скрытого имени байт имени опускается — получатель берёт имя из метаданных сертификата. Использованную соль нужно сохранять в `consumed_salts` (иначе повторы); в read-only режиме достаточно случайных солей в памяти, но повтор с уже потраченной не страшен только если получатель не ведёт чёрный список — **гипотеза, не проверено**.

**Получатель находит нужный публичный серт** (`NearbyShareDecryptedPublicCertificate::DecryptPublicCertificate`): перебор всех публичных сертов в базе (индекса нет — id в рекламе не передаётся): для каждого `key = AES-CTR(cert.secret_key, salt, enc_key)`; принять, если `HMAC-SHA256(key=32x0x00, key)` == `cert.metadata_encryption_key_tag`. Нашёлся — расшифровать метаданные: `AES-256-GCM(key=HKDF(metadata_key,32), nonce=HKDF(cert.secret_key,12), без AAD, tag 16 Б в конце)`. Здесь 15 сертов, перебор тривиален. В self-share поле 7 даёт прямой метаключ (тест в спайке).

**Подпись в PairedKeyEncryption** (`paired_key_verification_runner.cc`): токен = UKEY2 auth token; `padded = prefix(1 байт) + token`, префикс `0x01` у отправителя, `0x02` у получателя (подписывает свой префикс, проверяет префикс противоположной стороны). Подпись — **ECDSA-SHA256, DER** (ASN.1 SEQUENCE; в .NET `DSASignatureFormat.Rfc3279DerSequence`) приватным ключом серта нужной видимости; проверка — публичным ключом (SPKI) найденного публичного серта. `secret_id_hash` = `HKDF-SHA256(ikm=token, salt=cert.secret_key, info=пусто, 6 Б)`. Если подписать нечем — шлётся 72 случайных байта. Приёмник дополнительно проверяет `secret_id_hash` против своих приватных сертов (видимости: текущая, при блокировке — SELF_SHARE, недавно сменённая).

**Выбор своего серта**: `GetValidPrivateCertificate(visibility)`: EVERYONE -> ALL_CONTACTS; берётся первый с `not_before <= now < not_after`.

## 4. Что проверено / не проверено

Проверено (запуск `dotnet spikes/certs/bin/Debug/net10.0/Certs.dll`):
- структура JSON и leveldb; чтение leveldb с нуля собственным ридером (MANIFEST -> живые файлы, .ldb, .log), совпадение 15/15 с `public_certificate_expiration_dict`;
- `id == SHA256(secret)` 6/6; PKCS#8 импортируется 6/6; self sign/verify DER; AES-CTR раундтрип metadata key;
- для 15/15 публичных: `tag == HMAC(0x00*32, field7)` и успешная AES-GCM-расшифровка метаданных (значит HKDF/GCM-схема воспроизведена верно).

Не проверено:
- расшифровка чужой рекламы реальным advert от телефона и проверка подписи телефона (нужен S4/живой обмен);
- что телефон примет подпись нашим ключом из `preferences.json` (ядро риска S4);
- поведение при записи (мы только читаем; `consumed_salts` не обновляем);
- смысл поля 11.

## 5. Практические выводы и риски

- **Свежесть**: окно серта 72 ч, у телефона новые серты приходят только через `PublishDevice`/`download_public_certificates` официального приложения. Сейчас у телефона валидны 3 серта, последний кончается через ~77 ч, у ПК запас ~121 ч. Подтверждает суточное обновление из PLAN (порог 48 ч разумен; с допуском 30 мин).
- **Чтение при работающем официальном приложении**: leveldb держит `LOCK`; читать надо копией файлов (в спайке — копия с `FileShare.ReadWrite` во временный каталог вне репозитория, `LOCK` пропускается) либо файлы напрямую с `FileShare.ReadWrite`. Окно гонки при compaction: копировать `CURRENT` + `MANIFEST` + все `.ldb`/`.log`, при ошибке разбора — повторить.
- **Смена формата**: версии нет; признаки смены: исключение разбора, `id != SHA256(secret)`, tag не сходится, не 32/14/91 длины, неожиданные номера полей. Критерий деградации -> режим «Все» (по PLAN). Поле 11 и поле 7 показывают, что Google уже добавляет поля — парсер должен игнорировать неизвестные.
- **Prefs**: файл пишется приложением; читаем с `FileShare.ReadWrite`, при обрыве JSON — повтор (есть `preferences_bak.json`).
- **Секреты**: ключи только в памяти; спайк ничего не пишет на диск кроме временной копии leveldb (удаляется в `finally`).
- **Выбор ридера leveldb для продукта**: собственный минимальный (`LevelDb.cs` ~170 строк + `Snappy.cs`) — без нативных зависимостей и без NuGet, NativeAOT-безопасен (нет рефлексии); NuGet-пакеты (`LevelDB.Standard`, `Snappier`+обёртки) тянут рефлексию/нативный код или не поддерживаются. Ограничение: только чтение, без проверки CRC, предполагает целую БД.
- Для продукта protobuf: либо `Google.Protobuf` (после S7 проверить AOT), либо этот минимальный wire-reader (`Proto.cs`) — для двух сообщений достаточно.
