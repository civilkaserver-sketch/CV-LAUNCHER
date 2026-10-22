# AGENTS.md — launcher/ (CV Launcher, C#)

## Что это

Windows-лаунчер: один `Launcher.cs` (~262 КБ) содержит весь бэкенд, `ui.html`
(~210 КБ) — весь фронтенд. Оба файла очень большие: перед правкой составь план
и найди точное место (`grep` по имени метода/endpoint), не переписывай целиком.

## Ключевые факты

- Сборка: `build.bat` (или `beta\build_beta.bat`) через `csc.exe` .NET Framework 4.x.
  `ui.html`, `news.json`, `manifest.json`, `icon.ico`, `assets/*` **вшиваются как
  ресурсы** — после правки UI обязательно пересобирать exe.
- `CV Launcher.exe` и `CV Launcher Beta.exe` лежат в git — после сборки они
  меняются, это ожидаемо.
- Локальный HTTP-API: метод `HandleApi`, endpoint'ы перечислены в
  `../docs/PROJECT_MAP.md` §7. Добавляешь endpoint — правь и `Launcher.cs`,
  и вызов из `ui.html`.
- Репозиторий модпака качается из константы `RepoZip` (сейчас
  `civilkaserver-sketch/Civilka-version`). Распакованный модпак — `../version_repo/`.
- `version.sha` и `CurrentLauncherSha` в коде — авто-обновление лаунчера.

## Осторожно с

- Не менять `RepoZip`/манифест, не обновив `version_repo/` — иначе игроки
  получат рассинхрон.
- `beta/` — отдельная независимая сборка; правки в корне её не затрагивают.
- `launcher.log` (2 МБ) и `launcher.log.old` (5 МБ) — не читать целиком, только
  `Select-String`/`grep` по нужной строке.
- Кодировка: русский текст в `Launcher.cs`/`ui.html` — только UTF-8.

## Проверка

`build.bat` завершился с `[SUCCESS]` и в корне обновился `CV Launcher.exe`.
