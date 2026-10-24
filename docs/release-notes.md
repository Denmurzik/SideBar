### Русский

**SideBar 0.1.0** — выезжающая с правого края экрана панель для Windows 11.

- **Полка** — временное хранилище для drag-and-drop: перетащите файлы на панель и достаньте их в другом приложении. На полке до 100 файлов (в стопке видно до 10), есть корзина.
- **Quick Share с Android** — отправка и приём файлов, текстов и ссылок.
  - Согласие на приём (с PIN) показывается прямо в карточке; тексты и ссылки с телефона попадают в буфер обмена.
  - Режимы видимости «Все» и «Свои устройства». «Свои устройства» принимают автоматически по сертификатам официального Quick Share для Windows (нужен установленный клиент, в который выполнен вход в тот же аккаунт Google); сертификаты обновляются раз в сутки.
  - На свои устройства можно отправлять даже при заблокированном экране телефона: по Bluetooth, большие файлы переключаются на Wi-Fi.
  - Пока работает официальное приложение Quick Share, приём в SideBar ставится на паузу.
  - Панель подпрыгивает при входящем запросе; отправка продолжается, даже если переключиться на другое окно.
- **Окно настроек** — пункт в меню значка в трее.
- **Русский и английский интерфейс.** «Авто» следует языку Windows; смена языка применяется после перезапуска SideBar.
- **Проверка обновлений.** Раз в сутки SideBar делает анонимный запрос к api.github.com с заголовком User-Agent `SideBar/<версия>`; никакие данные не отправляются. Проверку можно выключить в настройках.
- Только Windows 11.

**Установка и обновление.** Нужны права администратора. Перед обновлением закройте SideBar через значок в трее. Установщик добавляет правило брандмауэра «SideBar Quick Share» (TCP, входящие, только для `SideBar.exe`, профили «частная» и «доменная»); перед этим он удаляет все прежние входящие правила для этого `SideBar.exe`, в том числе запрещающие, созданные нажатием «Отмена» в запросе брандмауэра.

**Удаление.** Деинсталлятор удаляет программу, правило брандмауэра и запись автозапуска. Настройки и данные полки сохраняются: папка `%LOCALAPPDATA%\SideBar` и раздел реестра `HKCU\Software\SideBar`. Чтобы убрать их, удалите папку и раздел вручную.

**Для разработчиков.** Релиз-кандидаты публикуются только как GitHub pre-release: проверка обновлений игнорирует pre-release.

### English

**SideBar 0.1.0** — a panel that slides out from the right screen edge on Windows 11.

- **Shelf** — a temporary stack for drag-and-drop: drop files on the panel and pick them up in another app. The shelf holds up to 100 files (the stack shows up to 10) and has a trash.
- **Quick Share with Android** — send and receive files, texts and links.
  - Consent (with a PIN) is shown right in the card; texts and links from the phone go to the clipboard.
  - Visibility modes "Everyone" and "Your devices". "Your devices" auto-accepts using the certificates of the official Quick Share for Windows (the official app must be installed and signed in to the same Google account); the certificates are refreshed once a day.
  - You can send to your own phone even when its screen is locked: over Bluetooth, with large files switching to Wi-Fi.
  - While the official Quick Share app is running, receiving in SideBar is paused.
  - The panel bounces on an incoming request; a send keeps going if you switch to another window.
- **Settings window** — an entry in the tray icon menu.
- **Russian and English UI.** "Auto" follows the Windows language; a language change applies after restarting SideBar.
- **Update check.** Once a day SideBar makes an anonymous request to api.github.com with the User-Agent `SideBar/<version>`; no data is sent. You can turn it off in Settings.
- Windows 11 only.

**Install and upgrade.** Administrator rights are required. Close SideBar from the tray icon before upgrading. The installer adds a firewall rule "SideBar Quick Share" (TCP inbound, for `SideBar.exe` only, Private and Domain profiles); before that it removes all existing inbound rules for this `SideBar.exe`, including blocking rules created by pressing "Cancel" in the firewall prompt.

**Uninstall.** The uninstaller removes the program, the firewall rule and the autostart entry. Settings and shelf data are kept: the `%LOCALAPPDATA%\SideBar` folder and the `HKCU\Software\SideBar` registry key. Delete them manually to remove everything.

**For maintainers.** Release candidates are published only as GitHub pre-releases: the update check ignores pre-releases.
