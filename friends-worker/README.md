# Civilization Friends Worker (Cloudflare Workers + Durable Objects SQLite)

Лёгкий 24/7 бэкенд списка друзей, статусов (`Играет в Civilization` / `В лаунчере` / `Оффлайн`), синхронизации скинов и 1-на-1 чата на бесплатном тарифе Cloudflare Workers (Durable Object с SQLite, 100 000 запросов/сутки).

## Как задеплоить (2 минуты)

1. Установи зависимости внутри `launcher/friends-worker`:
   ```powershell
   cd launcher\friends-worker
   npm install
   ```
2. Авторизуйся в своём бесплатном аккаунте Cloudflare (откроется браузер, вводить пароль в терминале не нужно):
   ```powershell
   npx wrangler login
   ```
3. Задеплой воркер:
   ```powershell
   npx wrangler deploy
   ```
4. В выводе появится URL вида `https://civilization-friends.<твой-субдомен>.workers.dev`.
   Вставь его в `Launcher.cs` в константу `FriendsBackendUrl` (либо передай через `%LOCALAPPDATA%\CivilizationLauncher\friends_url.txt` для быстрого теста без перекомпиляции) и пересобери лаунчер (`launcher\build.bat`).

## Локальный тест без деплоя

```powershell
cd launcher\friends-worker
npx wrangler dev --port 8787
```

Тогда в `%LOCALAPPDATA%\CivilizationLauncher\friends_url.txt` (или рядом с `Launcher.cs` в dev-режиме: `launcher\friends_url.txt`) можно прописать `http://127.0.0.1:8787` и протестировать синхронизацию локально.
