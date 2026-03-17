#pragma once

#include <windows.h>
#include <stdint.h>
#include <vector>
#include <string>

namespace ScumOxygen {

// Структуры FVector/FRotator в scum_sdk.h

// Интерфейс для чтения памяти процесса
class MemoryReader {
public:
    static bool Initialize();
    static void Shutdown();
    
    // Чтение памяти
    template<typename T>
    static T Read(uintptr_t address) {
        T value{};
        ReadMemory(address, &value, sizeof(T));
        return value;
    }
    
    static bool ReadMemory(uintptr_t address, void* buffer, size_t size);
    static std::string ReadString(uintptr_t address, size_t maxLength = 256);
    
    // Сканирование паттернов
    static uintptr_t FindPattern(const char* module, const char* pattern, const char* mask);
    static uintptr_t FindPattern(uintptr_t start, size_t length, const char* pattern, const char* mask);
    
    // Получение базового адреса модуля
    static uintptr_t GetModuleBase(const char* moduleName);
    
    // Поиск GWorld
    static uintptr_t FindGWorld();
    
private:
    static HANDLE s_ProcessHandle;
    static DWORD s_ProcessId;
};

// Интерфейс хуков
class HookManager {
public:
    static bool Initialize();
    static void Shutdown();
    
    // Установка хука на функцию
    static bool Hook(void* target, void* detour, void** original);
    static bool Unhook(void* target);
    
    // Хуки для UE4
    static bool HookProcessEvent();
    static bool HookPlayerLogin();
    static bool HookPlayerLogout();
    static bool HookChatMessage();
};

// Команды от .NET хоста
using CommandCallback = void(*)(const char* command, const char* args);

class Bridge {
public:
    static bool Initialize();
    static void Shutdown();
    
    // Отправка событий в .NET
    static void SendEvent(const char* eventType, const char* data);
    static void SendPlayerJoin(const char* steamId, const char* playerName);
    static void SendPlayerLeave(const char* steamId);
    static void SendChatMessage(const char* steamId, const char* message);
    
    // Установка callback для команд
    static void SetCommandCallback(CommandCallback callback);
};

} // namespace ScumOxygen
