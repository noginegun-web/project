#include "../include/sdk.h"
#include "../include/scum_sdk.h"
#include <windows.h>
#include <vector>
#include <map>

namespace ScumOxygen {

static constexpr uintptr_t kControllerPlayerStateOffset = 0x228;

// === Hook Engine ===

struct HookEntry {
    void* target;
    void* detour;
    void* trampoline;
    uint8_t originalBytes[14];
    size_t length;
};

static std::map<void*, HookEntry> s_Hooks;

bool HookManager::Initialize() {
    return true;
}

void HookManager::Shutdown() {
    for (auto& [target, entry] : s_Hooks) {
        Unhook(target);
    }
    s_Hooks.clear();
}

bool HookManager::Hook(void* target, void* detour, void** original) {
    if (!target || !detour) return false;
    
    void* trampoline = VirtualAlloc(nullptr, 32, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
    if (!trampoline) return false;
    
    const size_t hookLen = 14;
    
    HookEntry entry;
    entry.target = target;
    entry.detour = detour;
    entry.trampoline = trampoline;
    entry.length = hookLen;
    
    memcpy(entry.originalBytes, target, hookLen);
    memcpy(trampoline, target, hookLen);
    
    uint8_t* trampCode = (uint8_t*)trampoline + hookLen;
    trampCode[0] = 0xFF;
    trampCode[1] = 0x25;
    *(uint32_t*)(trampCode + 2) = 0;
    *(uint64_t*)(trampCode + 6) = (uint64_t)target + hookLen;
    
    DWORD oldProtect;
    if (!VirtualProtect(target, hookLen, PAGE_EXECUTE_READWRITE, &oldProtect)) {
        VirtualFree(trampoline, 0, MEM_RELEASE);
        return false;
    }
    
    uint8_t* code = (uint8_t*)target;
    code[0] = 0xFF;
    code[1] = 0x25;
    *(uint32_t*)(code + 2) = 0;
    *(uint64_t*)(code + 6) = (uint64_t)detour;
    
    VirtualProtect(target, hookLen, oldProtect, &oldProtect);
    
    s_Hooks[target] = entry;
    
    if (original) {
        *original = trampoline;
    }
    
    return true;
}

bool HookManager::Unhook(void* target) {
    auto it = s_Hooks.find(target);
    if (it == s_Hooks.end()) return false;
    
    HookEntry& entry = it->second;
    
    DWORD oldProtect;
    if (VirtualProtect(target, entry.length, PAGE_EXECUTE_READWRITE, &oldProtect)) {
        memcpy(target, entry.originalBytes, entry.length);
        VirtualProtect(target, entry.length, oldProtect, &oldProtect);
    }
    
    VirtualFree(entry.trampoline, 0, MEM_RELEASE);
    s_Hooks.erase(it);
    return true;
}

// === SCUM Game Hooks ===

// Original function pointers
void* o_PostLogin = nullptr;
void* o_Logout = nullptr;
void* o_BroadcastChat = nullptr;

// Detour functions
void __fastcall hk_PostLogin(void* gameMode, void* playerController) {
    // AConZPlayerController* controller = (AConZPlayerController*)playerController;
    // Читаем данные игрока
    
    if (playerController) {
        APlayerState** ppState = (APlayerState**)((uintptr_t)playerController + kControllerPlayerStateOffset);
        if (ppState && *ppState) {
            APlayerState* state = *ppState;
            std::string name = state->PlayerNamePrivate.ToString();
            printf("[ScumOxygen] Player joined: %s (ID: %d)\n", name.c_str(), state->PlayerId);
            
            // Отправляем в .NET bridge
            Bridge::SendPlayerJoin(std::to_string(state->PlayerId).c_str(), name.c_str());
        }
    }
    
    if (o_PostLogin) {
        ((void(__fastcall*)(void*, void*))o_PostLogin)(gameMode, playerController);
    }
}

void __fastcall hk_Logout(void* gameMode, void* playerController) {
    if (playerController) {
        APlayerState** ppState = (APlayerState**)((uintptr_t)playerController + kControllerPlayerStateOffset);
        if (ppState && *ppState) {
            APlayerState* state = *ppState;
            printf("[ScumOxygen] Player left: %s\n", state->PlayerNamePrivate.ToString().c_str());
            Bridge::SendPlayerLeave(std::to_string(state->PlayerId).c_str());
        }
    }
    
    if (o_Logout) {
        ((void(__fastcall*)(void*, void*))o_Logout)(gameMode, playerController);
    }
}

void __fastcall hk_BroadcastChat(void* gameState, void* playerState, void* message, uint8_t chatType) {
    if (playerState && message) {
        FString* msg = (FString*)message;
        APlayerState* state = (APlayerState*)playerState;
        
        std::string text = msg->ToString();
        std::string player = state->PlayerNamePrivate.ToString();
        
        printf("[ScumOxygen] Chat [%s]: %s\n", player.c_str(), text.c_str());
        Bridge::SendChatMessage(std::to_string(state->PlayerId).c_str(), text.c_str());
    }
    
    if (o_BroadcastChat) {
        ((void(__fastcall*)(void*, void*, void*, uint8_t))o_BroadcastChat)(gameState, playerState, message, chatType);
    }
}

// === Pattern Scans for SCUM 1.2.1.1 ===

bool HookManager::HookPlayerLogin() {
    // AGameMode::PostLogin signature
    // 48 89 74 24 ? 57 48 83 EC 20 48 8B F1 48 8B DA 48 8B 8E ? ? ? ?
    uintptr_t addr = MemoryReader::FindPattern(
        nullptr,  // Current process
        "\x48\x89\x74\x24\x00\x57\x48\x83\xEC\x20\x48\x8B\xF1",
        "xxxx?xxxxxxxxxx");
    
    if (!addr) {
        printf("[ScumOxygen] Pattern not found: PostLogin\n");
        return false;
    }
    
    printf("[ScumOxygen] Found PostLogin at: 0x%p\n", (void*)addr);
    return Hook((void*)addr, hk_PostLogin, &o_PostLogin);
}

bool HookManager::HookPlayerLogout() {
    // AGameMode::Logout signature
    // 40 53 48 83 EC 20 48 8B D9 48 8B 8B ? ? ? ? 48 85 C9 74 ? 48 8B 01
    uintptr_t addr = MemoryReader::FindPattern(
        nullptr,
        "\x40\x53\x48\x83\xEC\x20\x48\x8B\xD9\x48\x8B\x8B",
        "xxxxxxxxxxxx");
    
    if (!addr) {
        printf("[ScumOxygen] Pattern not found: Logout\n");
        return false;
    }
    
    printf("[ScumOxygen] Found Logout at: 0x%p\n", (void*)addr);
    return Hook((void*)addr, hk_Logout, &o_Logout);
}

bool HookManager::HookChatMessage() {
    // AConZGameState::BroadcastChatMessage signature
    // Сложно найти статическую сигнатуру, ищем через строку "[Global]" или "[Local]"
    
    // Альтернатива - искать через GNames и находить функцию по имени
    printf("[ScumOxygen] Chat hook requires manual address for this version\n");
    return false;
}

bool HookManager::HookProcessEvent() {
    // UObject::ProcessEvent - универсальный хук для всех Blueprint событий
    // 40 55 56 57 41 54 41 55 41 56 41 57 48 81 EC 80 00 00 00 48 8D 6C 24 ?
    
    uintptr_t addr = MemoryReader::FindPattern(
        nullptr,
        "\x40\x55\x56\x57\x41\x54\x41\x55\x41\x56\x41\x57\x48\x81\xEC",
        "xxxxxxxxxxxxxxx");
    
    if (!addr) {
        printf("[ScumOxygen] Pattern not found: ProcessEvent\n");
        return false;
    }
    
    printf("[ScumOxygen] Found ProcessEvent at: 0x%p\n", (void*)addr);
    // TODO: Implement generic ProcessEvent detour
    return true;
}

} // namespace ScumOxygen
