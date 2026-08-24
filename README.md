<div align="center">

# 🌺 RedBloom

**A Windows terminal that thinks** — real local shells and SSH, AI agents that can actually *do* things, projects, and extensions, all in one dark, themeable window.

**Терминал, который думает** — настоящие локальные оболочки и SSH, ИИ-агенты, которые реально *работают*, проекты и расширения — всё в одном тёмном настраиваемом окне.

[**English**](#english) · [**Русский**](#русский)

Built with C# / WPF on .NET 10 · WebView2 · SSH.NET · Anthropic & OpenAI-compatible APIs

</div>

---

## English

### Overview

RedBloom is a custom terminal emulator for Windows that fuses three things people usually keep in separate apps:

- a **real terminal** — local shells and SSH sessions in tabs and split panes;
- **AI agents** that live in the same window and can run commands, edit files, drive apps and reach your remote machines with your permission;
- a **workspace** layer — Projects that group chats, rooms and code, and an Extensions system for small purpose-built tools.

It’s a single dark, fully themeable window with a live-wallpaper background, English/Russian UI, and everything stored locally on your machine.

> Windows-only. UI panels are rendered as HTML inside WebView2; the shell plumbing is native (ConPTY) and the SSH stack is SSH.NET.

### Highlights

- 🖥️ Tabbed, split-pane terminal with local shells (cmd, PowerShell, pwsh, WSL, custom profiles) and SSH.
- 🤖 AI agents with **tools**: run commands (with approval), read/write/edit files, control windows/mouse/keyboard, generate images, ask other agents — and **ask you** questions with preset answers.
- 🔐 Agents reach remote machines through RedBloom’s **own** SSH client — external `ssh`/`plink` is intercepted and routed through it, so a password login never hangs and never lands on a command line.
- 🗂️ **Projects**: group chats/rooms/files, a relationship graph, VS/GitHub/local sources, activity monitoring, and one-click GitHub publish.
- 🧩 **Extensions**: HTML/JS modules with a sandboxed host bridge. Ships with an **ESP32 firmware** extension (edit → compile → flash → serial monitor).
- 🎨 Themes, per-surface opacity, custom window chrome, and a live wallpaper as the terminal background.

### Features

#### Terminal & SSH
- Tabs and recursive split panes with draggable, animated splitters.
- Local shells through **ConPTY**: cmd, Windows PowerShell, pwsh, WSL, or any custom profile.
- SSH through **SSH.NET**: saved sessions, `known_hosts` with SHA-256 fingerprint prompts, and local/remote/dynamic (SOCKS) port forwarding.
- Paste an existing `ssh …` command line and RedBloom turns it into a saved session.
- Session passwords and key passphrases are stored **DPAPI-encrypted** (per Windows user), never in plain text.

#### AI agents
- Providers: **Anthropic**, any **OpenAI-compatible** endpoint, **Gemini** (via its OpenAI-compatible API), the **Claude Code CLI**, **local models** (Ollama / GGUF), and local **image generation** (stable-diffusion.cpp).
- Per-chat model switching, custom system prompts, avatars, name colours, and a roleplay mode.
- Streamed replies with reasoning folded away, a live token gauge, and automatic context compaction.
- **Auto-titles** — after the first exchange a short, model-written title replaces the placeholder.
- **Export** a chat to Markdown (`/export`); inside a project it saves straight into the project folder.
- **Rooms** — multi-agent conversations where several agents talk, with turn-taking and mentions.

#### Agent tools
Each tool is offered only when its permission is on, and command execution is put to you for approval:

| Tool | What it does |
|------|--------------|
| `run_command` | Runs a shell command locally (or on an attached SSH host), with approval and optional elevation. |
| `read/write/edit/list` | Precise file operations — no quoting or truncation traps. |
| `manage_window` | Launch, screenshot, focus and close apps — so an agent can *see* and test a GUI. |
| `control_mouse` / `type_keys` | Drive an app the agent can see in a screenshot. |
| `generate_image` | Draw a picture with the local image model. |
| `ask_agent` | Hand a request to another configured agent (e.g. an image model). |
| `manage_tasks` | Keep a shared task list and the agent’s own notebook. |
| `ask_user` | **Ask you a question** with preset answers *and* a free-text box — the assistant checks a decision instead of guessing. |

- **Built-in SSH for agents.** Attach a saved session and the tools act on the remote machine over one live SSH.NET connection (persistent working directory, base64 file transfer). External `ssh`/`plink` a model tries to run is intercepted and routed here — no TTY hangs, no password on the command line, no secret sent to the model.
- **Agent tunnels.** Point an agent at a loopback-only service (`127.0.0.1:8080`) on a remote box; RedBloom raises the SSH forward automatically while a chat needs it.

#### Projects
- Group chats, rooms and files into a workspace (a real folder on disk).
- Create chats (via a **model picker**) and rooms directly inside a project.
- A **relationship graph** — nodes and edges for chats/rooms/files/sources/notes, custom colours, and **export as an image**.
- **Sources**: link Visual Studio solutions, GitHub repositories (clone/publish), and local folders — with live file-watching.
- **Monitoring**: stats, a complexity score, connection analysis, a code-by-language chart, LOC and git status.
- **GitHub**: sign in with **OAuth device flow** (no manual token), then publish/update a project to a repo — everything, or project data only (connections, notes, chats/rooms).
- **Project context**: a chat opened inside a project works out of the project folder and gets an orientation preamble (description, `PROJECT.md`, sources, a shallow file tree), so agents don’t need it re-explained.

#### Extensions
- An extension is a folder with a `manifest.json` and an HTML entry page, hosted in WebView2.
- A small, **declared** host bridge: run only the programs the manifest lists (streamed, cancellable), read/write files **confined to the extension’s data folder**, watch folders for changes, and — through the native picker only — reach extra folders the user authorises.
- Ships with **ESP32 Firmware** (built on `arduino-cli`): a C++ editor with IntelliSense-style hints, compile / upload / serial monitor, a VS-style file browser, “build to bin”, and a resizable output pane.
- See [`RedBloom/Assets/EXTENSIONS.md`](RedBloom/Assets/EXTENSIONS.md) for the extension API.

#### Appearance & UX
- Theme colours, per-surface opacity, fonts, Mica/Acrylic backdrop and custom window chrome.
- **Live wallpaper** as the terminal background, including Wallpaper Engine capture.
- Tray icon, Explorer context-menu integration, and full **English / Russian** localization.

### How it works

RedBloom is a WPF shell that hosts most of its rich surfaces as HTML inside **WebView2**, talking to the C# host over a small `postMessage` protocol.

- **Terminal** — `Controls/TerminalView` hosts **xterm.js**; the bytes come from an `ITerminalBackend`: `ConPtyBackend` for local shells (P/Invoke over ConPTY) or `SshBackend` (SSH.NET). Panes live in a recursive `SplitContainer`.
- **Chat & rooms** — `Controls/AgentChatView` / `RoomChatView` host `Assets/chat.html`. A turn is streamed from an `IAgentTransport` (one per wire format: Anthropic SDK, OpenAI-compatible HTTP, Claude CLI process, local runners). When the model calls a tool, the transport hands it to an `IAgentToolHost` — the chat view — which runs the approved action and returns the result.
- **Projects** — `Controls/ProjectHomeView` and `ProjectGraphView` host `Assets/project.html` / `graph.html`; a single WebView2 fixed under the tab strip scrolls its own content to avoid airspace overlap. Data lives in `%APPDATA%\RedBloom\projects` plus a project folder on disk.
- **Extensions** — `Controls/ExtensionView` maps two virtual hosts (shared vendor libs + the extension’s own files) and exposes the sandboxed bridge described above.
- **Live wallpaper** — a native in-process **D3D11 `Present` hook** (`native/RedBloomHook`) injected into the wallpaper process copies its back buffer into a shared texture; RedBloom does the downscale and read-back on its own device, so it never stalls the desktop.

Everything is stored locally; secrets are DPAPI-encrypted for the current Windows user.

### Requirements

- **Windows 10/11** (x64).
- **.NET 10 SDK**.
- **WebView2 Runtime** (present on current Windows installs).
- Visual Studio 2022 with the **C++ toolchain** — only to build the native wallpaper-capture hook.
- Optional, for specific features: `git` (project clone/publish), `arduino-cli` (ESP32 extension), a local model runner (Ollama / llama.cpp).

### Build & run

```bash
# the app
dotnet build RedBloom/RedBloom.csproj -c Release

# the native wallpaper-capture hook (x64 only, built separately)
powershell -ExecutionPolicy Bypass -File native\RedBloomHook\build.ps1
```

The hook build locates the toolchain with `vswhere`, calls `vcvars64.bat`, and produces `native\RedBloomHook\bin\RedBloomHook.dll`, which ships next to the executable.

For GitHub sign-in, register a personal **OAuth App** (with *Device Flow* enabled) and set its **Client ID** — either as `EmbeddedClientId` in `Services/GitHubClient.cs`, or in `%APPDATA%\RedBloom\github.clientid`. No client secret is needed or stored.

### Where your data lives

Everything is under `%APPDATA%\RedBloom`:

| Path | Contents |
|------|----------|
| `settings.json` | theme, colours, fonts, backdrop, language, and your agents |
| `sessions.json` | saved SSH sessions and port forwards |
| `known_hosts.json` | accepted host keys (SHA-256 fingerprints) |
| `chats\*.json` | one file per chat |
| `projects\*` | project definitions; project files live in a folder you choose |
| `github.token` | GitHub token, **DPAPI-encrypted** |
| `extensions\` · `extensions-data\` | user extensions and their sandboxed data |

Passwords, key passphrases and API keys are never written in plain text — they’re wrapped with DPAPI (`CurrentUser` scope), readable only by the same Windows user on the same machine.

### Security

An agent runs commands with **your** privileges and can request elevation. Commands are shown for approval before they run. An agent endpoint has, in effect, shell access to your machine — point it only at services you trust. Secrets attached to a chat (e.g. a session password) are sent to the endpoint only when you explicitly choose to include them.

### Status

Personal project, developed on `master`. No test suite or release pipeline yet.

---

## Русский

### Обзор

RedBloom — это собственный терминал для Windows, который объединяет три вещи, обычно живущие в разных приложениях:

- **настоящий терминал** — локальные оболочки и SSH-сессии во вкладках и сплит-панелях;
- **ИИ-агентов**, которые живут в том же окне и с твоего разрешения могут выполнять команды, править файлы, управлять приложениями и работать на удалённых машинах;
- слой **рабочего пространства** — Проекты, объединяющие чаты, комнаты и код, и систему Расширений для небольших специализированных инструментов.

Это одно тёмное, полностью настраиваемое окно с живыми обоями в фоне, интерфейсом на русском/английском, и всеми данными, которые хранятся локально.

> Только Windows. Панели интерфейса рендерятся как HTML внутри WebView2; терминал — нативный (ConPTY), SSH — на SSH.NET.

### Коротко о главном

- 🖥️ Терминал со вкладками и сплитами: локальные оболочки (cmd, PowerShell, pwsh, WSL, свои профили) и SSH.
- 🤖 ИИ-агенты с **инструментами**: выполнять команды (с подтверждением), читать/писать/править файлы, управлять окнами/мышью/клавиатурой, рисовать картинки, обращаться к другим агентам — и **задавать тебе вопросы** с готовыми вариантами.
- 🔐 К удалённым машинам агенты ходят через **встроенный** SSH-клиент RedBloom — внешние `ssh`/`plink` перехватываются и идут через него, поэтому вход по паролю не зависает и пароль не попадает в командную строку.
- 🗂️ **Проекты**: объединяют чаты/комнаты/файлы, дерево связей, источники VS/GitHub/локальные, мониторинг активности и публикацию на GitHub в один клик.
- 🧩 **Расширения**: HTML/JS-модули с песочницей. В комплекте — расширение **прошивок ESP32** (редактор → сборка → заливка → монитор порта).
- 🎨 Темы, прозрачность поверхностей, своё оформление окна и живые обои как фон терминала.

### Возможности

#### Терминал и SSH
- Вкладки и рекурсивные сплит-панели с перетаскиваемыми анимированными разделителями.
- Локальные оболочки через **ConPTY**: cmd, Windows PowerShell, pwsh, WSL или любой свой профиль.
- SSH через **SSH.NET**: сохранённые сессии, `known_hosts` с отпечатком SHA-256, проброс портов (локальный/удалённый/динамический SOCKS).
- Вставь готовую команду `ssh …` — RedBloom превратит её в сохранённую сессию.
- Пароли и парольные фразы ключей хранятся **зашифрованными через DPAPI** (для текущего пользователя Windows), никогда в открытом виде.

#### ИИ-агенты
- Провайдеры: **Anthropic**, любой **OpenAI-совместимый** эндпоинт, **Gemini** (через его OpenAI-совместимый API), **Claude Code CLI**, **локальные модели** (Ollama / GGUF) и локальная **генерация изображений** (stable-diffusion.cpp).
- Смена модели на уровне чата, свои системные промпты, аватары, цвета имени, режим ролеплея.
- Потоковые ответы (рассуждения свёрнуты), живой счётчик токенов, авто-уплотнение контекста.
- **Авто-заголовки** — после первого обмена короткое название пишет модель.
- **Экспорт** чата в Markdown (`/export`); внутри проекта сохраняется прямо в папку проекта.
- **Комнаты** — беседы нескольких агентов сразу, с очерёдностью и упоминаниями.

#### Инструменты агента
Каждый инструмент доступен только при включённом праве, а выполнение команд выносится тебе на подтверждение:

| Инструмент | Что делает |
|------------|-----------|
| `run_command` | Выполняет команду локально (или на прикреплённом SSH-хосте), с подтверждением и по желанию с повышением прав. |
| `read/write/edit/list` | Точные операции с файлами — без ловушек экранирования и обрезки. |
| `manage_window` | Запуск, скриншот, фокус и закрытие приложений — чтобы агент *видел* и тестировал GUI. |
| `control_mouse` / `type_keys` | Управление приложением, которое агент видит на скриншоте. |
| `generate_image` | Рисует картинку локальной моделью. |
| `ask_agent` | Передаёт запрос другому настроенному агенту (например, image-модели). |
| `manage_tasks` | Ведёт общий список задач и личный блокнот агента. |
| `ask_user` | **Задаёт тебе вопрос** с готовыми вариантами *и* полем для своего ответа — ассистент уточняет решение, а не угадывает. |

- **Встроенный SSH для агентов.** Прикрепи сохранённую сессию — и инструменты работают на удалённой машине через одно живое соединение SSH.NET (рабочая папка сохраняется, файлы передаются через base64). Внешние `ssh`/`plink`, которые пытается запустить модель, перехватываются сюда — без зависаний на TTY, без пароля в командной строке и без утечки секрета к модели.
- **Туннели агента.** Укажи агенту сервис, слушающий только loopback (`127.0.0.1:8080`) на удалённой машине; RedBloom сам поднимет SSH-проброс, пока чат в нём нуждается.

#### Проекты
- Объединяют чаты, комнаты и файлы в рабочее пространство (реальную папку на диске).
- Создание чатов (через **выбор модели**) и комнат прямо внутри проекта.
- **Дерево связей** — узлы и связи для чатов/комнат/файлов/источников/заметок, свои цвета и **экспорт картинкой**.
- **Источники**: решения Visual Studio, репозитории GitHub (клон/публикация), локальные папки — со слежением за изменениями.
- **Мониторинг**: статистика, оценка сложности, анализ связей, диаграмма кода по языкам, LOC и статус git.
- **GitHub**: вход через **OAuth device flow** (без ручного токена), затем публикация/обновление проекта в репозиторий — всё целиком или только данные проекта (связи, заметки, чаты/комнаты).
- **Проектный контекст**: чат, открытый внутри проекта, работает из папки проекта и получает вводную (описание, `PROJECT.md`, источники, неглубокое дерево файлов) — не нужно объяснять заново.

#### Расширения
- Расширение — это папка с `manifest.json` и стартовой HTML-страницей, размещаемая в WebView2.
- Маленький, **заранее объявленный** мост к хосту: запускать только программы из манифеста (стриминг, отмена), читать/писать файлы **строго в песочнице расширения**, следить за папками и — только через нативный выбор — обращаться к папкам, которые авторизовал пользователь.
- В комплекте — **ESP32 Firmware** (на базе `arduino-cli`): редактор C++ с подсказками, сборка / заливка / монитор порта, файловый браузер как в VS, «собрать в bin» и изменяемая высота вывода.
- API расширений — в [`RedBloom/Assets/EXTENSIONS.md`](RedBloom/Assets/EXTENSIONS.md).

#### Оформление и удобство
- Цвета темы, прозрачность поверхностей, шрифты, подложка Mica/Acrylic и своё оформление окна.
- **Живые обои** как фон терминала, в том числе захват Wallpaper Engine.
- Иконка в трее, интеграция с контекстным меню проводника, полная локализация **русский / английский**.

### Как это работает

RedBloom — это WPF-оболочка, которая большинство «богатых» поверхностей размещает как HTML внутри **WebView2** и общается с C#-хостом по небольшому протоколу `postMessage`.

- **Терминал** — `Controls/TerminalView` размещает **xterm.js**; байты приходят от `ITerminalBackend`: `ConPtyBackend` для локальных оболочек (P/Invoke по ConPTY) или `SshBackend` (SSH.NET). Панели живут в рекурсивном `SplitContainer`.
- **Чат и комнаты** — `Controls/AgentChatView` / `RoomChatView` размещают `Assets/chat.html`. Ход потоково приходит от `IAgentTransport` (по одному на формат: Anthropic SDK, OpenAI-совместимый HTTP, процесс Claude CLI, локальные раннеры). Когда модель зовёт инструмент, транспорт передаёт его в `IAgentToolHost` — вью чата, — который выполняет подтверждённое действие и возвращает результат.
- **Проекты** — `Controls/ProjectHomeView` и `ProjectGraphView` размещают `Assets/project.html` / `graph.html`; один WebView2, закреплённый под лентой вкладок, скроллит своё содержимое сам, чтобы не было наложения (airspace). Данные — в `%APPDATA%\RedBloom\projects` плюс папка проекта на диске.
- **Расширения** — `Controls/ExtensionView` подключает два виртуальных хоста (общие библиотеки + файлы расширения) и открывает описанный выше мост-песочницу.
- **Живые обои** — нативный внутрипроцессный **хук `Present` D3D11** (`native/RedBloomHook`), внедряемый в процесс обоев, копирует его back buffer в общую текстуру; уменьшение и чтение RedBloom делает на своём устройстве, поэтому рабочий стол не подвисает.

Всё хранится локально; секреты зашифрованы через DPAPI для текущего пользователя Windows.

### Требования

- **Windows 10/11** (x64).
- **.NET 10 SDK**.
- **WebView2 Runtime** (есть на актуальных Windows).
- Visual Studio 2022 с **C++-тулчейном** — только для сборки нативного хука обоев.
- По желанию, для отдельных фич: `git` (клон/публикация проектов), `arduino-cli` (расширение ESP32), локальный раннер моделей (Ollama / llama.cpp).

### Сборка и запуск

```bash
# приложение
dotnet build RedBloom/RedBloom.csproj -c Release

# нативный хук захвата обоев (только x64, собирается отдельно)
powershell -ExecutionPolicy Bypass -File native\RedBloomHook\build.ps1
```

Сборка хука находит тулчейн через `vswhere`, вызывает `vcvars64.bat` и создаёт `native\RedBloomHook\bin\RedBloomHook.dll`, который кладётся рядом с исполняемым файлом.

Для входа в GitHub зарегистрируй **OAuth App** (с включённым *Device Flow*) и укажи его **Client ID** — либо в `EmbeddedClientId` в `Services/GitHubClient.cs`, либо в `%APPDATA%\RedBloom\github.clientid`. Client secret не нужен и нигде не хранится.

### Где хранятся данные

Всё — в `%APPDATA%\RedBloom`:

| Путь | Содержимое |
|------|-----------|
| `settings.json` | тема, цвета, шрифты, подложка, язык и твои агенты |
| `sessions.json` | сохранённые SSH-сессии и пробросы портов |
| `known_hosts.json` | принятые ключи хостов (отпечатки SHA-256) |
| `chats\*.json` | по файлу на чат |
| `projects\*` | описания проектов; файлы проекта — в выбранной тобой папке |
| `github.token` | токен GitHub, **зашифрован через DPAPI** |
| `extensions\` · `extensions-data\` | пользовательские расширения и их данные в песочнице |

Пароли, парольные фразы и API-ключи никогда не пишутся в открытом виде — они обёрнуты DPAPI (`CurrentUser`), доступны только тому же пользователю Windows на той же машине.

### Безопасность

Агент выполняет команды с **твоими** правами и может запросить повышение. Команды показываются на подтверждение перед запуском. По сути эндпоинт агента имеет доступ к оболочке твоей машины — направляй его только на сервисы, которым доверяешь. Секреты, прикреплённые к чату (например, пароль сессии), уходят на эндпоинт только если ты явно решишь их включить.

### Статус

Личный проект, разработка в ветке `master`. Тестов и релизного пайплайна пока нет.
