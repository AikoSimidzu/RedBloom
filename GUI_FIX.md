# Исправление блокировки выполнения команд при запуске GUI-приложений

## Проблема
При запуске GUI-приложений через AI-агента (например, `notepad.exe`, `calc.exe`, `code.exe`) выполнение команд блокировалось до закрытия окна запущенного приложения. Это происходило потому, что `CommandRunner` ждал завершения процесса (`WaitForExitAsync`), что правильно для консольных утилит, но неправильно для GUI-приложений.

## Решение
Добавлена логика детектирования GUI-приложений в `CommandRunner.cs`:

### Основные изменения:

1. **Метод `IsGuiApplication(string command)`**
   - Детектирует, является ли команда запуском GUI-приложения
   - Проверяет список известных GUI-приложений (notepad, calc, code, devenv, браузеры и т.д.)
   - Исключает команды с перенаправлением вывода (`>`, `|`), так как они должны захватываться
   - Распознает команды с `start`, которые явно предназначены для GUI

2. **Метод `ExtractExecutableName(string command)`**
   - Извлекает имя исполняемого файла из командной строки
   - Корректно обрабатывает пути в кавычках
   - Убирает путь, оставляя только имя файла

3. **Метод `LaunchGuiApplicationAsync(string command, ...)`**
   - Запускает GUI-приложение асинхронно без ожидания закрытия
   - Ждет 500мс для детектирования немедленных ошибок (файл не найден и т.д.)
   - Возвращает успешный результат сразу после запуска

### Поток выполнения:

```
ExecuteAsync()
    ↓
IsGuiApplication() → true
    ↓
LaunchGuiApplicationAsync()
    ↓
Process.Start()
    ↓
Task.Delay(500ms) - проверка на ошибки
    ↓
Возврат "Application launched successfully."
```

### Поддерживаемые GUI-приложения:
- `notepad.exe` / `notepad`
- `mspaint.exe`
- `calc.exe`
- `explorer.exe`
- `code.exe` / `code` (VS Code)
- `devenv.exe` (Visual Studio)
- `winword.exe` (Word)
- `excel.exe`
- `powerpnt.exe` (PowerPoint)
- `chrome.exe`
- `firefox.exe`
- `msedge.exe`
- `iexplore.exe`

### Примеры использования:

**Блокирующие команды (консольные):**
```
dir
git status
dotnet build
node script.js > output.txt
```

**Неблокирующие команды (GUI):**
```
notepad file.txt
code .
calc
start chrome https://example.com
"C:\Program Files\Visual Studio Code\Code.exe" .
```

## Файлы изменены:
- `C:\Users\aikos\source\repos\RedBloom\RedBloom\Services\Ai\CommandRunner.cs`

## Тестирование:
Проект успешно скомпилирован без ошибок и предупреждений.
