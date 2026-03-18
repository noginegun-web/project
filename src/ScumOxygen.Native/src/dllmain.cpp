#include "../include/sdk.h"
#include <windows.h>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>

using namespace std::chrono_literals;
using namespace ScumOxygen;

namespace
{
    HMODULE g_hModule = nullptr;
    std::atomic_bool g_Running = false;
    std::thread g_MainThread;
    std::mutex g_LogMutex;
    std::once_flag g_GWorldLogOnce;
    std::once_flag g_GWorldFoundLogOnce;

    struct TArrayHeader
    {
        uintptr_t Data;
        int32_t Count;
        int32_t Max;
    };

    struct FVectorMem
    {
        float X;
        float Y;
        float Z;
    };

    struct FStringMem
    {
        uintptr_t Data;
        int32_t Count;
        int32_t Max;
    };

    struct NativePlayerSnapshot
    {
        std::string Name;
        int32_t PlayerId = 0;
        double X = 0.0;
        double Y = 0.0;
        double Z = 0.0;
        uintptr_t ItemPtr = 0;
    };

    constexpr uintptr_t kUWorldGameStateOffset = 0x120;
    constexpr uintptr_t kGameStatePlayerArrayOffset = 0x238;
    constexpr uintptr_t kPlayerStatePlayerIdOffset = 0x224;
    constexpr uintptr_t kPlayerStatePawnPrivateOffset = 0x280;
    constexpr uintptr_t kPlayerStatePlayerNamePrivateOffset = 0x300;
    constexpr uintptr_t kActorRootComponentOffset = 0x130;
    constexpr uintptr_t kSceneRelativeLocationOffset = 0x11C;
    constexpr uintptr_t kPrisonerItemInHandsOffset = 0x18A8;

    std::wstring GetModuleDirectory()
    {
        wchar_t buffer[MAX_PATH] = { 0 };
        GetModuleFileNameW(g_hModule ? g_hModule : GetModuleHandleW(nullptr), buffer, MAX_PATH);
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

    std::wstring Utf8ToWide(const std::string& input)
    {
        if (input.empty())
            return {};

        const auto len = MultiByteToWideChar(CP_UTF8, 0, input.c_str(), -1, nullptr, 0);
        if (len <= 1)
            return {};

        std::wstring output(static_cast<size_t>(len), L'\0');
        MultiByteToWideChar(CP_UTF8, 0, input.c_str(), -1, output.data(), len);
        if (!output.empty() && output.back() == L'\0')
            output.pop_back();
        return output;
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

    void EnsureRuntimeFolders()
    {
        const auto baseDir = GetModuleDirectory();
        const auto oxygenRoot = JoinPath(baseDir, L"oxygen");
        const auto logsDir = JoinPath(oxygenRoot, L"logs");

        CreateDirectoryW(oxygenRoot.c_str(), nullptr);
        CreateDirectoryW(logsDir.c_str(), nullptr);
    }

    void LogLine(const std::string& line)
    {
        std::lock_guard<std::mutex> lock(g_LogMutex);
        EnsureRuntimeFolders();

        const auto baseDir = GetModuleDirectory();
        const auto logPath = JoinPath(JoinPath(baseDir, L"oxygen"), L"logs\\native.log");

        std::ofstream out(logPath, std::ios::app);
        if (out.is_open())
        {
            out << "[ScumOxygen.Native] " << line << "\n";
        }
    }

    void AppendServerCommand(const std::string& command)
    {
        if (command.empty())
            return;

        std::lock_guard<std::mutex> lock(g_LogMutex);
        EnsureRuntimeFolders();

        const auto baseDir = GetModuleDirectory();
        const auto commandPath = JoinPath(JoinPath(baseDir, L"oxygen"), L"commands.txt");
        std::ofstream out(commandPath, std::ios::app);
        if (out.is_open())
        {
            out << command << "\n";
        }
    }

    bool TrySendConsoleCommand(const std::string& command)
    {
        if (command.empty())
            return false;

        const auto hInput = GetStdHandle(STD_INPUT_HANDLE);
        if (hInput == nullptr || hInput == INVALID_HANDLE_VALUE)
            return false;

        DWORD mode = 0;
        if (!GetConsoleMode(hInput, &mode))
            return false;

        const auto text = Utf8ToWide(command + "\r\n");
        if (text.empty())
            return false;

        std::vector<INPUT_RECORD> records;
        records.reserve(text.size() * 2);

        for (wchar_t ch : text)
        {
            INPUT_RECORD down{};
            down.EventType = KEY_EVENT;
            down.Event.KeyEvent.bKeyDown = TRUE;
            down.Event.KeyEvent.wRepeatCount = 1;
            down.Event.KeyEvent.wVirtualKeyCode = (ch == L'\r') ? VK_RETURN : 0;
            down.Event.KeyEvent.uChar.UnicodeChar = ch;
            records.push_back(down);

            INPUT_RECORD up = down;
            up.Event.KeyEvent.bKeyDown = FALSE;
            records.push_back(up);
        }

        DWORD written = 0;
        if (!WriteConsoleInputW(hInput, records.data(), static_cast<DWORD>(records.size()), &written))
            return false;

        return written > 0;
    }

    bool ReadPointer(uintptr_t address, uintptr_t& value)
    {
        return MemoryReader::ReadMemory(address, &value, sizeof(value));
    }

    bool ReadInt32(uintptr_t address, int32_t& value)
    {
        return MemoryReader::ReadMemory(address, &value, sizeof(value));
    }

    bool ReadVector(uintptr_t address, double& x, double& y, double& z)
    {
        FVectorMem vec{};
        if (!MemoryReader::ReadMemory(address, &vec, sizeof(vec)))
            return false;

        x = static_cast<double>(vec.X);
        y = static_cast<double>(vec.Y);
        z = static_cast<double>(vec.Z);
        return std::isfinite(x) && std::isfinite(y) && std::isfinite(z);
    }

    bool ReadFStringUtf8(uintptr_t address, std::string& value)
    {
        value.clear();

        FStringMem str{};
        if (!MemoryReader::ReadMemory(address, &str, sizeof(str)))
            return false;

        if (str.Count <= 0 || str.Count > 128 || str.Data == 0)
            return false;

        std::vector<wchar_t> chars(static_cast<size_t>(str.Count) + 1, L'\0');
        const auto bytes = static_cast<size_t>(str.Count) * sizeof(wchar_t);
        if (!MemoryReader::ReadMemory(str.Data, chars.data(), bytes))
            return false;

        value = WideToUtf8(std::wstring(chars.data()));
        return !value.empty();
    }

    uintptr_t ResolveGWorldPointerAddress()
    {
        const auto addr = MemoryReader::FindGWorld();
        if (!addr)
        {
            std::call_once(g_GWorldLogOnce, []()
            {
                LogLine("GWorld pattern not found yet.");
            });
        }
        else
        {
            std::call_once(g_GWorldFoundLogOnce, [addr]()
            {
                std::ostringstream ss;
                ss << "GWorld pointer address resolved: 0x" << std::hex << addr;
                LogLine(ss.str());
            });
        }
        return addr;
    }

    std::string BuildJoinLeaveJson(const NativePlayerSnapshot& player)
    {
        std::ostringstream ss;
        ss << "{\"name\":\"" << JsonEscape(player.Name) << "\",\"playerId\":" << player.PlayerId << "}";
        return ss.str();
    }

    std::string BuildSnapshotJson(const NativePlayerSnapshot& player)
    {
        std::ostringstream ss;
        ss.setf(std::ios::fixed);
        ss.precision(2);
        ss << "{\"name\":\"" << JsonEscape(player.Name)
           << "\",\"playerId\":" << player.PlayerId
           << ",\"x\":" << player.X
           << ",\"y\":" << player.Y
           << ",\"z\":" << player.Z
           << ",\"itemInHands\":\"\"}";
        return ss.str();
    }

    bool EnumeratePlayers(std::vector<NativePlayerSnapshot>& players)
    {
        players.clear();

        const auto gworldPtrAddress = ResolveGWorldPointerAddress();
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
            return true;

        for (int32_t i = 0; i < playerArray.Count; ++i)
        {
            uintptr_t playerState = 0;
            if (!ReadPointer(playerArray.Data + (static_cast<uintptr_t>(i) * sizeof(uintptr_t)), playerState) || !playerState)
                continue;

            NativePlayerSnapshot snapshot{};
            if (!ReadFStringUtf8(playerState + kPlayerStatePlayerNamePrivateOffset, snapshot.Name) || snapshot.Name.empty())
                continue;

            ReadInt32(playerState + kPlayerStatePlayerIdOffset, snapshot.PlayerId);

            uintptr_t pawn = 0;
            if (ReadPointer(playerState + kPlayerStatePawnPrivateOffset, pawn) && pawn)
            {
                uintptr_t rootComponent = 0;
                if (ReadPointer(pawn + kActorRootComponentOffset, rootComponent) && rootComponent)
                {
                    ReadVector(rootComponent + kSceneRelativeLocationOffset, snapshot.X, snapshot.Y, snapshot.Z);
                }

                ReadPointer(pawn + kPrisonerItemInHandsOffset, snapshot.ItemPtr);
            }

            players.push_back(snapshot);
        }

        return true;
    }

    void PublishPlayerState()
    {
        static std::unordered_map<std::string, NativePlayerSnapshot> lastPlayers;

        std::vector<NativePlayerSnapshot> currentPlayers;
        if (!EnumeratePlayers(currentPlayers))
            return;

        std::unordered_map<std::string, NativePlayerSnapshot> currentMap;
        currentMap.reserve(currentPlayers.size());

        for (const auto& player : currentPlayers)
        {
            const auto key = player.Name;
            currentMap[key] = player;

            if (lastPlayers.find(key) == lastPlayers.end())
            {
                Bridge::SendEvent("PLAYER_JOIN", BuildJoinLeaveJson(player).c_str());
            }

            Bridge::SendEvent("PLAYER_SNAPSHOT", BuildSnapshotJson(player).c_str());
        }

        for (const auto& [key, player] : lastPlayers)
        {
            if (currentMap.find(key) == currentMap.end())
            {
                Bridge::SendEvent("PLAYER_LEAVE", BuildJoinLeaveJson(player).c_str());
            }
        }

        lastPlayers = std::move(currentMap);
    }

    void OnPipeCommand(const char* type, const char* data)
    {
        if (!type)
            return;

        const std::string commandType = type;
        const std::string payload = data ? std::string(data) : std::string();

        if (_stricmp(commandType.c_str(), "CMD") == 0)
        {
            if (TrySendConsoleCommand(payload))
            {
                LogLine(std::string("CMD(console) -> ") + payload);
            }
            else
            {
                AppendServerCommand(payload);
                LogLine(std::string("CMD(file-fallback) -> ") + payload);
            }
            return;
        }

        if (_stricmp(commandType.c_str(), "RAW") == 0)
        {
            if (TrySendConsoleCommand(payload))
            {
                LogLine(std::string("RAW(console) -> ") + payload);
            }
            else
            {
                AppendServerCommand(payload);
                LogLine(std::string("RAW(file-fallback) -> ") + payload);
            }
            return;
        }

        AppendServerCommand(commandType + (payload.empty() ? "" : (" " + payload)));
    }

    void MainLoop()
    {
        std::this_thread::sleep_for(5s);
        EnsureRuntimeFolders();
        LogLine("Native runtime starting.");

        if (!MemoryReader::Initialize())
        {
            LogLine("MemoryReader initialization failed.");
            return;
        }

        if (!Bridge::Initialize())
        {
            LogLine("Named pipe bridge initialization failed.");
            MemoryReader::Shutdown();
            return;
        }

        Bridge::SetCommandCallback(OnPipeCommand);
        HookManager::Initialize();
        LogLine("Native bridge ready. Polling live server state.");

        while (g_Running)
        {
            PublishPlayerState();
            std::this_thread::sleep_for(1000ms);
        }

        HookManager::Shutdown();
        Bridge::Shutdown();
        MemoryReader::Shutdown();
        LogLine("Native runtime stopped.");
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(hModule);
        g_hModule = hModule;
        g_Running = true;
        g_MainThread = std::thread(MainLoop);
        break;

    case DLL_PROCESS_DETACH:
        g_Running = false;
        if (g_MainThread.joinable())
        {
            g_MainThread.join();
        }
        break;
    }

    return TRUE;
}

extern "C" __declspec(dllexport) void ScumOxygen_Test()
{
    MessageBoxA(nullptr, "ScumOxygen.Native loaded successfully.", "ScumOxygen.Native", MB_OK);
}

extern "C" __declspec(dllexport) const char* ScumOxygen_Version()
{
    return "1.1.0";
}
