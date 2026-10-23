# M5 — обновление сертификатов через официальное приложение: план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Раз в сутки SideBar незаметно запускает официальный `nearby_share.exe`, ждёт обновления сертификатов и штатно закрывает его — «Свои устройства» продолжают работать неделями без ручных действий.

**Architecture:** Контроллер из спайка `spikes/official/src` (проверено: `SW_HIDE` без окон, `PublishDevice succeeded` за ~1.3 с, выход `WM_QUIT` потоку окна, leveldb цела) переносится в `src/SideBar.App/Official/`, планировщик решения «пора ли» — в Core (`RefreshScheduler`, чистая логика с тестами).

**Spec:** `docs/PLAN.md` §2 «Обновление сертификатов», «Сосуществование», `docs/research/official-app.md`, `docs/research/review-2026-10-22.md` #12, M4 plan (пауза стека при работающем приложении).

## Global Constraints

- Всё из M1–M4.
- Когда: если с последнего успешного обновления прошло ≥ 20 ч — при простое пользователя ≥ 5 мин (`GetLastInputInfo`); если > 48 ч — сразу; плюс пункт трея «Обновить сертификаты сейчас». Время последнего успеха — в `%LocalAppData%\SideBar\state.json` (source-gen JSON).
- Запуск только если `nearby_share.exe` не запущен (любой экземпляр пользователя не трогаем). `CreateProcessW` + `STARTF_USESHOWWINDOW` + `SW_HIDE`; запоминаем PID.
- Ожидание маркера `PublishDevice succeeded` в `Logs\nearby_share*.log` (чтение с сохранённого смещения, `FileShare.ReadWrite | Delete`, содержимое лога не логируем) — таймаут 60 с.
- Закрытие (#12): `WM_QUIT` только потоку окна `Google.Windows.NearbyShare`, принадлежащего **нашему** PID, и только если окна процесса невидимы и в логе после снимка нет `Received WM_COPYDATA` / `command line:` (значит, пользователь не начал им пользоваться); иначе — не трогать и отложить. `TerminateProcess` не используется никогда; если процесс не вышел за 15 с — оставить, повторить попытку закрытия через 2 мин (до 3 раз), затем оставить пользователю.
- Пока цикл идёт, свой стек на паузе (M4 `OfficialAppWatcher`), после — перезагрузка сертификатов (M4 `OfficialCertificateStore`) и перерегистрация рекламы.
- Отказы (нет приложения, таймаут маркера, не вышел) — в лог (тип/код), без уведомлений, кроме: > 72 ч без успешного обновления → одно уведомление «Не удаётся обновить сертификаты Quick Share — откройте официальное приложение Quick Share один раз».

## Review Focus

1. Пользователь сам открыл Quick Share, пока идёт наш цикл → мы его не закрываем, цикл засчитан как отложенный. Тест — Task 2 (fake process/log).
2. Нет установленного Quick Share → ни ошибок, ни уведомлений, кроме 72-часового. Тест — Task 1.
3. Ноутбук спал 3 дня → после пробуждения цикл сразу (> 48 ч), не ждёт простоя. Тест — Task 1.
4. Лог ротирован во время ожидания (новый `nearby_share_<ts>.log`) → маркер находится. Тест — Task 2.
5. Запущенный нами процесс завис и не выходит по `WM_QUIT` → не убиваем, 3 попытки, затем оставляем. Тест — Task 2.

---

### Task 1: RefreshScheduler (Core)

**Files:** Create `src/SideBar.Core/Official/RefreshScheduler.cs`, `src/SideBar.Core/Official/AppState.cs` (+ JSON context); Test `tests/SideBar.Core.Tests/RefreshSchedulerTests.cs`.

**Interfaces — Produces:** `enum RefreshDecision { Wait, RunWhenIdle, RunNow }`; `sealed class RefreshScheduler` — `RefreshDecision Decide(DateTimeOffset now, DateTimeOffset? lastSuccess, TimeSpan idle, bool officialInstalled, bool officialRunning)`; `bool ShouldWarn(DateTimeOffset now, DateTimeOffset? lastSuccess)` (> 72 ч, один раз за период); `AppState` load/save (atomic, как `ShelfStore`).

- [ ] **Step 1: Тесты:** `Under_20h_waits`; `Over_20h_runs_only_when_idle_5min`; `Over_48h_runs_now`; `Never_refreshed_runs_now`; `Not_installed_waits_and_never_throws`; `Official_running_waits`; `Warn_after_72h_once`; `State_round_trips_and_corrupt_is_empty`.
- [ ] **Step 2–4:** FAIL → реализовать → PASS. Коммит + push.

### Task 2: OfficialAppController и цикл (App)

**Files:** Create `src/SideBar.App/Official/{OfficialAppController,Native,RefreshService}.cs` (порт `spikes/official/src`); Modify `App.xaml.cs`, `Tray/TrayIcon.cs` («Обновить сертификаты сейчас»), `Strings.cs`; `tools/SideBar.OfficialSim/` — фиктивный «nearby_share.exe» для тестов (создаёт окно класса `Google.Windows.NearbyShare`, пишет маркер в лог, режимы: нормальный / зависший / «пользователь открыл»).

**Interfaces — Consumes:** `RefreshScheduler`, `AppState` (Task 1), M4 `OfficialAppWatcher`, `OfficialCertificateStore`. **Produces:** `RefreshService` — таймер 5 мин → `Decide` → цикл; `Task<RefreshOutcome> RunOnceAsync()` (`Succeeded | NotInstalled | AlreadyRunning | MarkerTimeout | UserTookOver | DidNotExit`).

- [ ] **Step 1:** Реализовать; путь к exe и каталог данных переопределяются через `SIDEBAR_OFFICIAL_EXE` / `SIDEBAR_SHARING_DIR` (для симулятора).
- [ ] **Step 2:** Проверки на симуляторе: успех (маркер → WM_QUIT → выход), ротация лога, «пользователь открыл» (WM_COPYDATA в логе → не закрываем), зависший (3 попытки, не убиваем), уже запущен (не запускаем). Debug/AOT 0 warnings.
- [ ] **Step 3:** Один живой цикл с настоящим `nearby_share.exe` на этой машине, только если он не запущен (как в S6): маркер получен, процесс вышел, сертификаты перечитаны. Коммит + push.
