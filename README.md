# QaAgent — автономний QA-інженер (Semantic Kernel + Ollama)

ШІ-агент, що сам аналізує Swagger/OpenAPI цільового API, генерує C#-тести
(Playwright + NUnit), емпірично визначає матрицю доступу, самовідновлює тести
після змін API (self-healing) і проактивно звітує (файл / Telegram / Email).

LLM: локальна **Ollama** `qwen2.5-coder:7b` через **Semantic Kernel**.
Платформа: **.NET 10**.

## Архітектура (проєкти)

| Проєкт | Призначення |
|---|---|
| `QaAgent.Core` | Доменні моделі: `ApiSpec`, `EndpointSpec`, `TestRun`, `RunReport` |
| `QaAgent.Llm` | Semantic Kernel ↔ Ollama (`LlmClient`) |
| `QaAgent.Swagger` | Парсинг OpenAPI, snapshot, diff (+ детекція перейменувань) |
| `QaAgent.Probing` | Auth-probing — емпірична матриця доступу |
| `QaAgent.Generation` | Генерація сценаріїв (LLM-дані + детерміновані правила) → C# |
| `QaAgent.Execution` | Запуск `dotnet test` + парсинг TRX |
| `QaAgent.Healing` | Self-healing зламаних тестів |
| `QaAgent.Reporting` | Звіти: Markdown/HTML, Telegram, Email |
| `QaAgent.Cli` | Точка входу (команди) |
| `generated/ApiTests` | Тест-проєкт, куди агент пише згенеровані тести |

## Команди CLI

```bash
dotnet run --project src/QaAgent.Cli -- <команда> [swaggerUrl]
```

| Команда | Дія |
|---|---|
| `smoke` | Перевірка зв'язку з Ollama |
| `analyze` | Парсинг схеми + diff зі знімком |
| `probe` | Auth-probing → матриця доступу у знімок |
| `generate` | Генерація тестів зі знімка (перезапис) |
| `run` | Запуск тестів + парсинг результату |
| `heal` | Self-healing після зміни API |
| `agent` | Повний цикл: probe → generate → run → heal → report |

`swaggerUrl` за замовчуванням: `http://localhost:5234/swagger/v1/swagger.json`.

## Налаштування (змінні середовища)

Жодних секретів у коді — лише через env.

| Змінна | Опис |
|---|---|
| `QA_OLLAMA_MODEL` | Модель Ollama (деф. `deepseek-coder-v2:latest`) |
| `QA_OLLAMA_ENDPOINT` | URL Ollama (деф. `http://localhost:11434`) |
| `API_BASE_URL` | База цільового API для тестів (деф. `http://localhost:5234`) |
| `TELEGRAM_BOT_TOKEN` | Токен бота (Telegram-звіти) |
| `TELEGRAM_CHAT_ID` | Chat ID для повідомлень |
| `QA_SMTP_HOST` / `QA_SMTP_PORT` | SMTP-сервер (Email-звіти) |
| `QA_SMTP_USER` / `QA_SMTP_PASS` | Логін/пароль SMTP |
| `QA_MAIL_FROM` / `QA_MAIL_TO` | Адреси відправника/отримувача |
| `QA_ADMIN_EMAIL` / `QA_ADMIN_PASSWORD` | Креди admin для positive-тестів Admin-ендпоінтів |

> Telegram/Email вмикаються автоматично, щойно задано відповідні змінні; інакше — пропускаються.

## Швидкий старт

```bash
# 1. Підняти цільове API (OnlineLibrary) у Development
# 2. Повний цикл:
dotnet run --project src/QaAgent.Cli -- agent
# Звіти: artifacts/reports/latest.md (+ .html)
```

## CI/CD (GitHub Actions)

Workflow: `.github/workflows/qa.yml` — запускає повний цикл `agent` за розкладом/пушем,
заливає звіт як артефакт і завалює джобу при реальних падіннях (гейт якості).

⚠️ **Потрібен self-hosted раннер** з Ollama + моделлю та доступом до цільового API
(GitHub-hosted не підходить: немає Ollama/GPU).

Налаштування self-hosted раннера:
1. Встанови Ollama і витягни модель: `ollama pull deepseek-coder-v2`.
2. Зареєструй раннер у репозиторії (Settings → Actions → Runners).
3. Repo **Variables**: `API_BASE_URL`, `QA_OLLAMA_MODEL`, `QA_API_PROJECT` (для seed).
4. Repo **Secrets**: `TELEGRAM_BOT_TOKEN`, `TELEGRAM_CHAT_ID`, `QA_SMTP_*`, `QA_MAIL_*`, `QA_ADMIN_PASSWORD`.

Артефакт `qa-report` міститиме Markdown/HTML-звіт і TRX.

> Що комітимо в репо: `artifacts/schema-snapshot.json` (baseline для diff/self-healing)
> та `generated/ApiTests/Generated/*.cs` (тести — основа для самовідновлення).
> Звіти й TRX (`artifacts/reports`, `artifacts/test-results`) — у `.gitignore`.
