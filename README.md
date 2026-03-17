# ScumOxygen

Клон плагина **Oxygen** для SCUM Dedicated Server v1.2.1.1 (февраль 2026).

## ⚠️ Важно: Статус проекта

**Рабочая основа** — C# компоненты готовы, C++ DLL требует финальной настройки смещений под твою конкретную версию.

## 📋 Что есть на компьютере

- ✅ **SCUM клиент**: `E:\SteamLibrary\steamapps\common\SCUM`
- ✅ **UE4SS установлен**: `E:\SteamLibrary\steamapps\common\SCUM\SCUM\Binaries\Win64\`
- ✅ **Дампы классов**: `CXXHeaderDump/`, `UHTHeaderDump/`
- ✅ **Mappings**: `Mappings.usmap` для FModel
- ✅ **Версия игры**: 1.2.1.1.106289 (февраль 2026)

## 🔧 Файлы для сервера

После сборки (`dotnet build -c Release` и CMake для C++):

### Обязательные:
```
ScumOxygen/
├── ScumOxygen.Cli.exe              # Главный исполняемый файл
├── ScumOxygen.Core.dll
├── ScumOxygen.Rcon.dll             # RCON клиент
├── ScumOxygen.Scripting.dll        # Компилятор скриптов
├── ScumOxygen.Native.dll           # Нативная DLL для инжекции ⚠️ ТРЕБУЕТ СБОРКИ
├── ScumOxygen.Injector.exe         # Инжектор
├── appsettings.json                # Конфиг (создать)
└── Scripts/                        # Папка со скриптами
```

### Конфиг `appsettings.json`:
```json
{
  "Rcon": {
    "Host": "127.0.0.1",
    "Port": 8881,
    "Password": "твой_пароль_из_Steam"
  },
  "Scripting": {
    "ScriptsDirectory": "./Scripts",
    "AutoReload": true,
    "TimeoutSeconds": 30
  }
}
```

## 🚀 Запуск

### 1. Подготовка сервера
```bash
# В Steam Launch Options для SCUM Dedicated Server:
-fileopenlog
```

### 2. Инжекция DLL (после запуска сервера)
```bash
ScumOxygen.Injector.exe SCUMServer.exe ScumOxygen.Native.dll
```

### 3. Запуск менеджера
```bash
ScumOxygen.Cli.exe
# или с параметрами:
ScumOxygen.Cli.exe 127.0.0.1 8881 admin
```

## 🔨 Сборка C++ DLL (Visual Studio 2022)

```bash
cd C:\temp\ScumOxygen\src\ScumOxygen.Native
mkdir build && cd build
cmake .. -A x64 -DCMAKE_BUILD_TYPE=Release
cmake --build . --config Release

# Результат:
# - build/Release/ScumOxygen.Native.dll
# - build/Release/ScumOxygen.Injector.exe
```

## ⚠️ Настройка смещений (КРИТИЧНО)

Данные из UE4SS дампов SCUM v1.2.1.1:

### Классы и размеры:
| Класс | Размер | Базовый класс |
|-------|--------|---------------|
| `APrisoner` | 0x2F28 (~12KB) | `ACharacter` |
| `AConZGameState` | 0x8D0 | `AGameState` |
| `AConZPlayerController` | 0x9F8 | `APlayerController` |

### Ключевые смещения (нужно подтвердить через Cheat Engine):
- `AController::PlayerState` — ~0x2E0
- `APlayerState::PlayerNamePrivate` — ~0x308
- `APlayerState::PlayerId` — ~0x318
- `AConZGameState::PlayerArray` — ~0x318 (TArray<APlayerState*>)

### Найти GWorld:
1. Запусти SCUM (клиент) с `-fileopenlog`
2. Инжектируй UE4SS (уже установлен)
3. Открой консоль UE4SS (~ или F10)
4. Введи: `obj dump`
5. Найди `GWorld` в выводе

## 📚 Структура проекта

```
C:\temp\ScumOxygen
├── src/
│   ├── ScumOxygen.Core/           # Модели (PlayerInfo, ServerStatus)
│   ├── ScumOxygen.Rcon/           # RCON с System.IO.Pipelines
│   │   ├── Protocol/              # RCON пакеты
│   │   ├── Parsers/               # listplayers, status
│   │   └── Pool/                  # Пул соединений
│   ├── ScumOxygen.Scripting/      # Roslyn + AssemblyLoadContext
│   │   ├── ScriptCompiler.cs
│   │   ├── ScriptEngine.cs
│   │   └── ScriptLoadContext.cs
│   ├── ScumOxygen.Cli/            # Консольное приложение
│   ├── ScumOxygen.Tests/          # xUnit тесты
│   └── ScumOxygen.Native/         # C++ DLL
│       ├── include/
│       │   ├── sdk.h              # Базовый SDK
│       │   └── scum_sdk.h         # SCUM специфичные структуры
│       ├── src/
│       │   ├── dllmain.cpp        # Точка входа DLL
│       │   ├── hooks.cpp          # Хуки игровых событий
│       │   ├── memory.cpp         # Чтение памяти
│       │   ├── bridge.cpp         # IPC через Named Pipe
│       │   └── ue4_sdk.cpp        # (устарел, использовать scum_sdk.h)
│       ├── injector.cpp           # Инжектор
│       └── CMakeLists.txt
└── ScumOxygen.sln
```

## 🎮 API для скриптов

Пример скрипта `Scripts/welcome.csx`:
```csharp
using System;
using ScumOxygen.Core.Interfaces;

public class WelcomeScript
{
    public void OnPlayerConnected(IPlayer player)
    {
        Console.WriteLine($"Welcome {player.CharacterName}!");
    }
    
    public async Task Execute(IServer server)
    {
        await server.AnnounceAsync("Server managed by ScumOxygen!");
    }
}
```

## 🔍 Отладка

### Логи консоли (DLL):
```
[ScumOxygen] DLL attached
[ScumOxygen] MemoryReader initialized for PID: 12345
[ScumOxygen] Found PostLogin at: 0x7FF... (если паттерн найден)
```

### Если паттерны не найдены:
1. Запусти сервер с `-fileopenlog`
2. Подключись через x64dbg или Cheat Engine
3. Найди сигнатуры функций `PostLogin`, `Logout`
4. Обнови паттерны в `hooks.cpp`

## ⚖️ Легальность

- ✅ Личный сервер с `-fileopenlog` (EAC отключен)
- ✅ Некоммерческое использование
- ❌ Публичные сервера с включенным EAC

## 📝 TODO для финализации

1. [ ] Собрать C++ DLL через CMake
2. [ ] Найти актуальные смещения GWorld через CE
3. [ ] Подтвердить паттерны функций (PostLogin, Logout)
4. [ ] Протестировать инжекцию
5. [ ] Настроить веб-панель (Blazor) — опционально

## 💾 Бэкапы

Проект находится в: `C:\temp\ScumOxygen`

Скопируй всю папку перед экспериментами с C++ кодом.

---
**Готов помочь** с настройкой смещений через Cheat Engine если запустишь сервер и дампнешь память.