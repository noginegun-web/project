#include "../include/sdk.h"
#include "../include/scum_sdk.h"
#include <windows.h>
#include <vector>
#include <map>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>

namespace ScumOxygen {

static constexpr uintptr_t kUWorldGameStateOffset = 0x120;
static constexpr uintptr_t kGameStatePlayerArrayOffset = 0x238;
static constexpr uintptr_t kControllerPlayerStateOffset = 0x228;
static constexpr uintptr_t kControllerPawnOffset = 0x250;
static constexpr uintptr_t kControllerRpcChannelOffset = 0x678;
static constexpr uintptr_t kControllerUserFakeNameOffset = 0x6F0;
static constexpr uintptr_t kPlayerStatePlayerIdOffset = 0x224;
static constexpr uintptr_t kPlayerStatePawnPrivateOffset = 0x280;
static constexpr uintptr_t kPlayerStatePlayerNamePrivateOffset = 0x300;
static constexpr uintptr_t kPawnControllerOffset = 0x258;
static constexpr uintptr_t kPrisonerUserIdOffset = 0x0ED0;
static constexpr uintptr_t kPrisonerServerUserProfileIdOffset = 0x0EE0;
static constexpr uintptr_t kPrisonerUserProfileNameOffset = 0x0EE8;
static constexpr uintptr_t kPrisonerUserFakeNameOffset = 0x0EF8;

namespace
{
    struct TArrayHeader
    {
        uintptr_t Data;
        int32_t Count;
        int32_t Max;
    };

    struct FStringMem
    {
        uintptr_t Data;
        int32_t Count;
        int32_t Max;
    };

    struct NativeChatContext
    {
        std::string Name;
        std::string ProfileName;
        std::string FakeName;
        std::string SteamId;
        int64_t DatabaseId = 0;
        int32_t PlayerId = 0;
    };

    using ProcessEventFn = void(__fastcall*)(UObject* object, UObject* function, void* params);

    std::atomic_bool s_ProcessEventHooked = false;
    std::once_flag s_ProcessEventPatternMissingLogOnce;
    std::once_flag s_ProcessEventWaitingLogOnce;
    std::once_flag s_ProcessEventHookedLogOnce;
    ProcessEventFn o_ProcessEvent = nullptr;
    uintptr_t s_ProcessEventAddress = 0;
    uintptr_t s_HookedVtable = 0;
    size_t s_HookedSlot = static_cast<size_t>(-1);
    std::mutex s_LogMutex;

    constexpr int32_t kChatServerBroadcastNameIndex = 0x434E3;
    constexpr int32_t kChatServerAdminNameIndex = 0x434F4;

    std::wstring GetModuleDirectory()
    {
        HMODULE module = nullptr;
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&GetModuleDirectory),
            &module);

        wchar_t buffer[MAX_PATH] = { 0 };
        GetModuleFileNameW(module ? module : GetModuleHandleW(nullptr), buffer, MAX_PATH);
        std::wstring path = buffer;
        const auto pos = path.find_last_of(L"\\/");
        return pos == std::wstring::npos ? path : path.substr(0, pos);
    }

    std::wstring JoinPath(const std::wstring& left, const std::wstring& right)
    {
        if (left.empty())
            return right;
        if (left.back() == L'\\' || left.back() == L'/')
            return left + right;
        return left + L"\\" + right;
    }

    std::string WideToUtf8(const std::wstring& input)
    {
        if (input.empty())
            return {};

        const auto len = WideCharToMultiByte(CP_UTF8, 0, input.c_str(), -1, nullptr, 0, nullptr, nullptr);
        if (len <= 1)
            return {};

        std::string output(static_cast<size_t>(len), '\0');
        WideCharToMultiByte(CP_UTF8, 0, input.c_str(), -1, output.data(), len, nullptr, nullptr);
        if (!output.empty() && output.back() == '\0')
            output.pop_back();
        return output;
    }

    void LogHookLine(const std::string& line)
    {
        std::lock_guard<std::mutex> lock(s_LogMutex);
        const auto baseDir = GetModuleDirectory();
        const auto oxygenRoot = JoinPath(baseDir, L"oxygen");
        const auto logsDir = JoinPath(oxygenRoot, L"logs");
        CreateDirectoryW(oxygenRoot.c_str(), nullptr);
        CreateDirectoryW(logsDir.c_str(), nullptr);

        const auto logPath = JoinPath(logsDir, L"native.log");
        std::ofstream out(logPath, std::ios::app);
        if (out.is_open())
        {
            out << "[ScumOxygen.Native] " << line << "\n";
        }
    }

    std::string JsonEscape(const std::string& value)
    {
        std::string out;
        out.reserve(value.size() + 16);

        for (char ch : value)
        {
            switch (ch)
            {
            case '\\': out += "\\\\"; break;
            case '"': out += "\\\""; break;
            case '\r': out += "\\r"; break;
            case '\n': out += "\\n"; break;
            case '\t': out += "\\t"; break;
            default:
                if (static_cast<unsigned char>(ch) < 0x20)
                {
                    char tmp[7];
                    sprintf_s(tmp, "\\u%04x", static_cast<unsigned char>(ch));
                    out += tmp;
                }
                else
                {
                    out += ch;
                }
                break;
            }
        }

        return out;
    }

    bool ReadPointer(uintptr_t address, uintptr_t& value)
    {
        return MemoryReader::ReadMemory(address, &value, sizeof(value));
    }

    bool ReadInt32(uintptr_t address, int32_t& value)
    {
        return MemoryReader::ReadMemory(address, &value, sizeof(value));
    }

    bool ReadInt64(uintptr_t address, int64_t& value)
    {
        return MemoryReader::ReadMemory(address, &value, sizeof(value));
    }

    bool ReadFStringUtf8(uintptr_t address, std::string& value, int32_t maxLen = 256)
    {
        value.clear();

        FStringMem str{};
        if (!MemoryReader::ReadMemory(address, &str, sizeof(str)))
            return false;

        if (str.Count <= 0 || str.Count > maxLen || str.Data == 0)
            return false;

        std::vector<wchar_t> chars(static_cast<size_t>(str.Count) + 1, L'\0');
        const auto bytes = static_cast<size_t>(str.Count) * sizeof(wchar_t);
        if (!MemoryReader::ReadMemory(str.Data, chars.data(), bytes))
            return false;

        value = WideToUtf8(std::wstring(chars.data()));
        return !value.empty();
    }

    uintptr_t ResolveControllerFromRpcChannel(uintptr_t rpcChannel)
    {
        uintptr_t outer = 0;
        if (ReadPointer(rpcChannel + offsetof(UObject, OuterPrivate), outer) && outer)
        {
            uintptr_t outerRpc = 0;
            if (ReadPointer(outer + kControllerRpcChannelOffset, outerRpc) && outerRpc == rpcChannel)
                return outer;
        }

        const auto gworldPtrAddress = MemoryReader::FindGWorld();
        if (!gworldPtrAddress)
            return 0;

        uintptr_t world = 0;
        if (!ReadPointer(gworldPtrAddress, world) || !world)
            return 0;

        uintptr_t gameState = 0;
        if (!ReadPointer(world + kUWorldGameStateOffset, gameState) || !gameState)
            return 0;

        TArrayHeader playerArray{};
        if (!MemoryReader::ReadMemory(gameState + kGameStatePlayerArrayOffset, &playerArray, sizeof(playerArray)))
            return 0;

        if (playerArray.Count <= 0 || playerArray.Count > 256 || playerArray.Data == 0)
            return 0;

        for (int32_t i = 0; i < playerArray.Count; ++i)
        {
            uintptr_t playerState = 0;
            if (!ReadPointer(playerArray.Data + (static_cast<uintptr_t>(i) * sizeof(uintptr_t)), playerState) || !playerState)
                continue;

            uintptr_t pawn = 0;
            if (!ReadPointer(playerState + kPlayerStatePawnPrivateOffset, pawn) || !pawn)
                continue;

            uintptr_t controller = 0;
            if (!ReadPointer(pawn + kPawnControllerOffset, controller) || !controller)
                continue;

            uintptr_t candidateRpc = 0;
            if (ReadPointer(controller + kControllerRpcChannelOffset, candidateRpc) && candidateRpc == rpcChannel)
                return controller;
        }

        return 0;
    }

    bool BuildChatContextFromController(uintptr_t controller, NativeChatContext& ctx)
    {
        if (!controller)
            return false;

        uintptr_t playerState = 0;
        ReadPointer(controller + kControllerPlayerStateOffset, playerState);

        uintptr_t pawn = 0;
        if (!ReadPointer(controller + kControllerPawnOffset, pawn) || !pawn)
        {
            if (playerState)
            {
                ReadPointer(playerState + kPlayerStatePawnPrivateOffset, pawn);
            }
        }

        if (playerState)
        {
            ReadInt32(playerState + kPlayerStatePlayerIdOffset, ctx.PlayerId);
            ReadFStringUtf8(playerState + kPlayerStatePlayerNamePrivateOffset, ctx.Name);
        }

        if (pawn)
        {
            ReadFStringUtf8(pawn + kPrisonerUserIdOffset, ctx.SteamId);
            ReadInt64(pawn + kPrisonerServerUserProfileIdOffset, ctx.DatabaseId);
            ReadFStringUtf8(pawn + kPrisonerUserProfileNameOffset, ctx.ProfileName);
            ReadFStringUtf8(pawn + kPrisonerUserFakeNameOffset, ctx.FakeName);
        }

        if (ctx.FakeName.empty())
        {
            ReadFStringUtf8(controller + kControllerUserFakeNameOffset, ctx.FakeName);
        }

        if (!ctx.ProfileName.empty())
        {
            ctx.Name = ctx.ProfileName;
        }

        return !ctx.Name.empty();
    }

    bool BuildChatContextFromRpcChannel(uintptr_t rpcChannel, NativeChatContext& ctx)
    {
        const auto controller = ResolveControllerFromRpcChannel(rpcChannel);
        return BuildChatContextFromController(controller, ctx);
    }

    std::string BuildChatJson(const NativeChatContext& ctx, const std::string& message, int chatType)
    {
        std::ostringstream ss;
        ss << "{\"name\":\"" << JsonEscape(ctx.Name)
           << "\",\"profileName\":\"" << JsonEscape(ctx.ProfileName)
           << "\",\"fakeName\":\"" << JsonEscape(ctx.FakeName)
           << "\",\"playerId\":" << ctx.PlayerId
           << ",\"databaseId\":" << ctx.DatabaseId
           << ",\"steamId\":\"" << JsonEscape(ctx.SteamId)
           << "\",\"message\":\"" << JsonEscape(message)
           << "\",\"chatType\":" << chatType
           << "}";
        return ss.str();
    }

    void TryHandleChatProcessEvent(UObject* object, UObject* function, void* params)
    {
        if (!object || !function || !params)
            return;

        FName functionName{};
        if (!MemoryReader::ReadMemory(reinterpret_cast<uintptr_t>(function) + offsetof(UObject, NamePrivate), &functionName, sizeof(functionName)))
            return;

        std::string message;
        int32_t chatType = 0;
        bool isChatEvent = false;

        if (functionName.ComparisonIndex == kChatServerBroadcastNameIndex)
        {
            if (!ReadFStringUtf8(reinterpret_cast<uintptr_t>(params), message))
                return;

            uint8_t rawChannel = 0;
            MemoryReader::ReadMemory(reinterpret_cast<uintptr_t>(params) + 0x10, &rawChannel, sizeof(rawChannel));
            chatType = static_cast<int32_t>(rawChannel);
            isChatEvent = true;
        }
        else if (functionName.ComparisonIndex == kChatServerAdminNameIndex)
        {
            if (!ReadFStringUtf8(reinterpret_cast<uintptr_t>(params), message))
                return;

            chatType = 4;
            isChatEvent = true;
        }

        if (!isChatEvent || message.empty())
            return;

        NativeChatContext ctx{};
        if (!BuildChatContextFromRpcChannel(reinterpret_cast<uintptr_t>(object), ctx))
            return;

        Bridge::SendEvent("CHAT_MESSAGE", BuildChatJson(ctx, message, chatType).c_str());

        std::ostringstream log;
        log << "[ProcessEvent] chat hook player=" << ctx.Name
            << " steamId=" << ctx.SteamId
            << " dbId=" << ctx.DatabaseId
            << " type=" << chatType
            << " msg=" << message;
        LogHookLine(log.str());
    }

    void __fastcall hk_ProcessEvent(UObject* object, UObject* function, void* params)
    {
        TryHandleChatProcessEvent(object, function, params);

        if (o_ProcessEvent)
        {
            o_ProcessEvent(object, function, params);
        }
    }
}

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
    HookProcessEvent();
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
            Bridge::SendPlayerJoin(state->PlayerId, name.c_str());
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
            Bridge::SendPlayerLeave(state->PlayerId, state->PlayerNamePrivate.ToString().c_str());
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
        Bridge::SendChatMessage(state->PlayerId, player.c_str(), text.c_str(), chatType);
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
    if (s_ProcessEventHooked)
        return true;

    if (!s_ProcessEventAddress)
    {
        s_ProcessEventAddress = MemoryReader::FindPattern(
            nullptr,
            "\x40\x55\x56\x57\x41\x54\x41\x55\x41\x56\x41\x57\x48\x81\xEC",
            "xxxxxxxxxxxxxxx");

        if (!s_ProcessEventAddress)
        {
            std::call_once(s_ProcessEventPatternMissingLogOnce, []()
            {
                LogHookLine("ProcessEvent pattern not found yet.");
            });
            return false;
        }
    }

    const auto gworldPtrAddress = MemoryReader::FindGWorld();
    if (!gworldPtrAddress)
        return false;

    uintptr_t world = 0;
    if (!ReadPointer(gworldPtrAddress, world) || !world)
        return false;

    uintptr_t gameState = 0;
    if (!ReadPointer(world + kUWorldGameStateOffset, gameState) || !gameState)
        return false;

    TArrayHeader playerArray{};
    if (!MemoryReader::ReadMemory(gameState + kGameStatePlayerArrayOffset, &playerArray, sizeof(playerArray)))
        return false;

    if (playerArray.Count <= 0 || playerArray.Count > 256 || playerArray.Data == 0)
    {
        std::call_once(s_ProcessEventWaitingLogOnce, []()
        {
            LogHookLine("ProcessEvent hook waiting for first live player/rpc channel.");
        });
        return false;
    }

    uintptr_t rpcChannel = 0;
    for (int32_t i = 0; i < playerArray.Count; ++i)
    {
        uintptr_t playerState = 0;
        if (!ReadPointer(playerArray.Data + (static_cast<uintptr_t>(i) * sizeof(uintptr_t)), playerState) || !playerState)
            continue;

        uintptr_t pawn = 0;
        if (!ReadPointer(playerState + kPlayerStatePawnPrivateOffset, pawn) || !pawn)
            continue;

        uintptr_t controller = 0;
        if (!ReadPointer(pawn + kPawnControllerOffset, controller) || !controller)
            continue;

        uintptr_t candidateRpc = 0;
        if (ReadPointer(controller + kControllerRpcChannelOffset, candidateRpc) && candidateRpc)
        {
            rpcChannel = candidateRpc;
            break;
        }
    }

    if (!rpcChannel)
        return false;

    uintptr_t vtable = 0;
    if (!ReadPointer(rpcChannel, vtable) || !vtable)
        return false;

    size_t slot = static_cast<size_t>(-1);
    for (size_t i = 0; i < 256; ++i)
    {
        uintptr_t candidate = 0;
        if (!ReadPointer(vtable + (i * sizeof(uintptr_t)), candidate))
            continue;

        if (candidate == s_ProcessEventAddress)
        {
            slot = i;
            break;
        }
    }

    if (slot == static_cast<size_t>(-1))
    {
        LogHookLine("ProcessEvent address not found inside UPlayerRpcChannel vtable.");
        return false;
    }

    DWORD oldProtect = 0;
    auto* slotPtr = reinterpret_cast<uintptr_t*>(vtable + (slot * sizeof(uintptr_t)));
    if (!VirtualProtect(slotPtr, sizeof(uintptr_t), PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        LogHookLine("VirtualProtect failed while patching UPlayerRpcChannel vtable.");
        return false;
    }

    o_ProcessEvent = reinterpret_cast<ProcessEventFn>(*slotPtr);
    *slotPtr = reinterpret_cast<uintptr_t>(&hk_ProcessEvent);
    VirtualProtect(slotPtr, sizeof(uintptr_t), oldProtect, &oldProtect);

    s_HookedVtable = vtable;
    s_HookedSlot = slot;
    s_ProcessEventHooked = true;

    std::call_once(s_ProcessEventHookedLogOnce, [vtable, slot]()
    {
        std::ostringstream ss;
        ss << "Installed UPlayerRpcChannel::ProcessEvent vtable hook. vtable=0x"
           << std::hex << vtable << " slot=" << std::dec << slot
           << " target=0x" << std::hex << s_ProcessEventAddress;
        LogHookLine(ss.str());
    });

    return true;
}

} // namespace ScumOxygen
