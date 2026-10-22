# UI-спайки (S7, S10, S8)

Код: `spikes/ui/`. Окружение: Windows 11 (10.0.26300), .NET SDK 10.0.402, VS Build Tools 2026, 15.5 ГБ RAM.

## S7 — WinUI 3 + NativeAOT, фоновая память

Проект `spikes/ui/S7Panel` (unpackaged, `WindowsAppSDKSelfContained`, `PublishAot`), окно без рамки, always-on-top,
`IsShownInSwitchers=false`, у правого края `DisplayArea.Primary.WorkArea`; режимы show/hide (`AppWindow.Hide()` +
`EmptyWorkingSet` + `SetProcessWorkingSetSize(-1,-1)`). Google.Protobuf 3.36.2 + сгенерированный `Ping`
(string/int32/repeated bytes/map), на старте сериализация и разбор туда-обратно.
Замер: `spikes/ui/measure.ps1 -Dir <папка публикации> -Label <имя>` (приложение управляется именованными событиями
`S7Panel_show|hide|exit`, время первого кадра пишет в `s7.log`).

**Проверено (результат запуска):**

| Метрика | NativeAOT | JIT (self-contained, без trim) |
|---|---|---|
| Размер публикации | **85.6 МБ** (150 файлов; S7Panel.exe 5.7 МБ) | 170.4 МБ |
| Protobuf: round-trip | ok, 22 байта, без IL-предупреждений при публикации | ok |
| После старта (6 с): private bytes / WS / private WS | 84.0 / 139.0 / 64.1 МБ | 85.6 / 154.2 / 65.1 МБ |
| Скрыто +2 с после обрезки: WS / private WS | 8.0 / **3.0 МБ** | 10.9 / 3.3 МБ |
| Скрыто +10 с: WS / private WS | 8.6 / 3.5 МБ | 14.5 / 4.8 МБ |
| Скрыто +60 с: WS / private WS | 14.4 / 7.6 МБ | 23.6 / 8.9 МБ |
| После показа (+3 с): WS / private WS | 31.3 / 12.4 МБ (2-й, 3-й цикл: ~21 / 8–9 МБ) | 44.0 / 15.1 МБ (далее ~28 / 9 МБ) |
| Показ после обрезки → первый `CompositionTarget.Rendering` (3 цикла) | 12.9 / 4.3 / 15.7 мс | 11.1 / 16.5 / 10.4 мс |

Выводы:
- NativeAOT работает с WinUI 3 (WinAppSDK 2.x, split-пакеты) и Google.Protobuf без предупреждений и падений. Критерий S7 (<100 мс на показ) выполнен с большим запасом; цель «единицы МБ private WS в простое» — выполнена: **~3 МБ сразу после обрезки, ~4–8 МБ через 10–60 с**.
- Private bytes (commit) обрезкой не уменьшаются (~84–89 МБ у обоих) — это зарезервированные/закоммиченные страницы, не ОЗУ. В диспетчере задач важен WS/private WS.
- В скрытом состоянии WS медленно растёт (3 → 7.6 МБ за минуту у AOT): что-то в процессе периодически трогает страницы (таймеры XAML/Composition, WinRT-потоки). В продакшене — повторять обрезку по таймеру (например раз в 30–60 с, пока панель скрыта).
- AOT выигрывает у JIT: ×2 по размеру, ~20–35 % по WS после показа. Старт-WS (~140 МБ) одинаков — это нативный стек WinUI.
- Оговорка по «первому кадру»: `Rendering` срабатывает в начале кадра, реальный present может быть чуть позже (+1 кадр). Порядок величины верен.
- Не проверено: холодный старт процесса до первого кадра, поведение при нескольких мониторах/DPI.

Подводные камни сборки (зафиксировать в CI):
1. NativeAOT-линковка из git-bash: `vswhere.exe` не в PATH → `export PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"`. MSVC-линкер ILCompiler находит сам.
2. Grpc.Tools в этом проекте не запускал `Protobuf_Compile` перед компиляцией (WinUI-цели) — добавлен явный `<Target Name="GenProto" BeforeTargets="CoreCompile;MarkupCompilePass1" DependsOnTargets="Protobuf_Compile" />`.
3. Мета-пакет `Microsoft.WindowsAppSDK` тянет AI/ML-зависимости; используем split-пакеты Base/Foundation/WinUI/InteractiveExperiences.
4. Команды: `dotnet publish -c Release -p:Platform=x64 -o out-aot -m:1` (AOT) и с `-p:Aot=false -o out-jit` (JIT).
