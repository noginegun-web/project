#include "../include/sdk.h"
#include "../include/scum_sdk.h"
#include <windows.h>
#include <vector>
#include <map>
#include <atomic>
#include <chrono>
#include <cctype>
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
    constexpr uintptr_t kFUObjectArrayObjObjectsOffset = 0x10;

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

    struct FUObjectItemMem
    {
        uintptr_t Object;
        uint8_t Pad[0x10];
    };

    struct TUObjectArrayMem
    {
        uintptr_t Objects;
        uint8_t Pad8[0x8];
        int32_t MaxElements;
        int32_t NumElements;
        int32_t MaxChunks;
        int32_t NumChunks;
    };

    using ProcessEventFn = void(__fastcall*)(UObject* object, UObject* function, void* params);
    struct FStringParam
    {
        wchar_t* Data;
        int32_t Count;
        int32_t Max;
    };

    struct AdminCommandParams
    {
        FStringParam commandText;
    };

    struct StaticAdminCommandParams
    {
        UObject* WorldContextObject;
        FStringParam commandText;
    };

    struct MiscBroadcastChatParams
    {
        UObject* WorldContextObject;
        FStringParam text;
        uint8_t chatType;
        uint8_t Pad11[0x7]{};
    };

    struct MiscSendChatLineParams
    {
        UObject* PlayerController;
        FStringParam text;
        uint8_t chatType;
        bool shouldCopyToClientClipboard;
        uint8_t Pad12[0x6]{};
    };

    struct UFieldRaw
    {
        UObject Object;
        UFieldRaw* Next;
    };

    struct UStructRaw
    {
        UFieldRaw Field;
        uint8_t Pad30[0x10];
        UStructRaw* SuperStruct;
        UFieldRaw* Children;
        void* ChildProperties;
        int32_t Size;
        int16_t MinAlignment;
        uint8_t Pad5E[0x52];
    };

    struct UClassRaw
    {
        UStructRaw Struct;
        uint8_t PadB0[0x20];
        uint64_t CastFlags;
        uint8_t PadD8[0x40];
        UObject* ClassDefaultObject;
    };

    std::atomic_bool s_ProcessEventHooked = false;
    std::once_flag s_ProcessEventPatternMissingLogOnce;
    std::once_flag s_ProcessEventWaitingLogOnce;
    std::once_flag s_ProcessEventHookedLogOnce;
    std::once_flag s_GObjectsMissingLogOnce;
    std::once_flag s_GObjectsProbeLogOnce;
    ProcessEventFn o_ProcessEvent = nullptr;
    uintptr_t s_ProcessEventAddress = 0;
    uintptr_t s_HookedVtable = 0;
    size_t s_HookedSlot = static_cast<size_t>(-1);
    constexpr size_t kProcessEventVtableIndex = 0x44;
    std::atomic_uintptr_t s_LastLiveRpcChannel = 0;
    std::atomic_uintptr_t s_AdminCommandFunction = 0;
    std::atomic_uintptr_t s_BroadcastChatFunction = 0;
    std::atomic_uintptr_t s_MiscStaticsClass = 0;
    std::atomic_uintptr_t s_MiscStaticsDefaultObject = 0;
    std::atomic_uintptr_t s_MiscStaticsAdminFunction = 0;
    std::atomic_uintptr_t s_MiscStaticsSendChatLineFunction = 0;
    std::atomic_uintptr_t s_MiscStaticsBroadcastChatLineFunction = 0;
    std::mutex s_LogMutex;
    constexpr uint64_t kClassCastFlagClass = 0x0000000000000020ULL;
    constexpr uint64_t kClassCastFlagFunction = 0x0000000000080000ULL;
    constexpr int32_t kNameIdxMiscStatics = 0xAF5C8;
    constexpr int32_t kNameIdxPlayerRpcChannel = 0xB0541;
    constexpr int32_t kNameIdxBroadcastChatLine = 0xAF5CF;
    constexpr int32_t kNameIdxSendChatLineToPlayer = 0xAF667;
    constexpr int32_t kNameIdxTestProcessAdminCommand = 0xAF682;
    constexpr int32_t kNameIdxChatServerBroadcastChatMessage = 0x432B8;
    constexpr int32_t kNameIdxChatServerProcessAdminCommand = 0x432C9;
    constexpr uint8_t kChatTypeDefault = 0;
    constexpr uint8_t kChatTypeGlobal = 2;
    constexpr uint8_t kChatTypeAdmin = 4;
    constexpr uint8_t kChatTypeServerMessage = 6;
    constexpr uint8_t kChatTypeError = 7;

    bool BuildChatContextFromRpcChannel(uintptr_t rpcChannel, NativeChatContext& ctx);
    bool GetProcessEventInvoker(uintptr_t objectPtr, ProcessEventFn& invoker);
    bool ResolveWorldObject(uintptr_t& worldObject);
    uintptr_t ResolveControllerFromRpcChannel(uintptr_t rpcChannel);
    std::wstring Utf8ToWideLocal(const std::string& input);

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

    std::string FNameToStringLocal(const FName& name)
    {
        using AppendStringFn = void(__fastcall*)(const FName*, FStringParam&);

        static AppendStringFn appendString = nullptr;
        if (!appendString)
        {
            const auto appendStringAddress = MemoryReader::FindFNameToString();
            if (!appendStringAddress)
                return {};

            appendString = reinterpret_cast<AppendStringFn>(appendStringAddress);
        }

        wchar_t buffer[1024]{};
        FStringParam temp{};
        temp.Data = buffer;
        temp.Count = 0;
        temp.Max = static_cast<int32_t>(std::size(buffer));

        appendString(&name, temp);

        auto text = WideToUtf8(std::wstring(buffer));
        const auto slash = text.rfind('/');
        if (slash != std::string::npos)
            text = text.substr(slash + 1);
        const auto colon = text.rfind(':');
        if (colon != std::string::npos)
            text = text.substr(colon + 1);
        const auto dot = text.rfind('.');
        if (dot != std::string::npos)
            text = text.substr(dot + 1);
        const auto space = text.rfind(' ');
        if (space != std::string::npos)
            text = text.substr(space + 1);
        return text;
    }

    std::string GetObjectNameLocal(const UObject* object)
    {
        if (!object)
            return {};

        return FNameToStringLocal(object->NamePrivate);
    }

    bool MatchesKnownNameIndex(const UObject* object, const char* objectName)
    {
        if (!object || !objectName)
            return false;

        const auto comparisonIndex = object->NamePrivate.ComparisonIndex;
        if (strcmp(objectName, "MiscStatics") == 0)
            return comparisonIndex == kNameIdxMiscStatics;
        if (strcmp(objectName, "PlayerRpcChannel") == 0)
            return comparisonIndex == kNameIdxPlayerRpcChannel;
        if (strcmp(objectName, "BroadcastChatLine") == 0)
            return comparisonIndex == kNameIdxBroadcastChatLine;
        if (strcmp(objectName, "SendChatLineToPlayer") == 0)
            return comparisonIndex == kNameIdxSendChatLineToPlayer;
        if (strcmp(objectName, "Test_ProcessAdminCommand") == 0)
            return comparisonIndex == kNameIdxTestProcessAdminCommand;
        if (strcmp(objectName, "Chat_Server_BroadcastChatMessage") == 0)
            return comparisonIndex == kNameIdxChatServerBroadcastChatMessage;
        if (strcmp(objectName, "Chat_Server_ProcessAdminCommand") == 0)
            return comparisonIndex == kNameIdxChatServerProcessAdminCommand;
        return false;
    }

    bool HasTypeFlag(const UObject* object, uint64_t requiredFlag)
    {
        if (!object || !object->ClassPrivate)
            return false;

        const auto* klass = reinterpret_cast<const UClassRaw*>(object->ClassPrivate);
        return (klass->CastFlags & requiredFlag) != 0;
    }

    bool ResolveObjectByName(const std::string& objectName, uint64_t requiredType, uintptr_t& objectPtr)
    {
        objectPtr = 0;

        const auto gobjectsAddress = MemoryReader::FindGUObjectArray();
        if (!gobjectsAddress)
        {
            std::call_once(s_GObjectsMissingLogOnce, []()
            {
                LogHookLine("ResolveObjectByName: GUObjectArray was not resolved for this SCUMServer build.");
            });
            return false;
        }

        TUObjectArrayMem objectArray{};
        if (!MemoryReader::ReadMemory(gobjectsAddress + kFUObjectArrayObjObjectsOffset, &objectArray, sizeof(objectArray)))
            return false;

        std::call_once(s_GObjectsProbeLogOnce, [&]()
        {
            std::ostringstream ss;
            ss << "GUObjectArray probe: addr=0x" << std::hex << gobjectsAddress
               << " objObjects=0x" << (gobjectsAddress + kFUObjectArrayObjObjectsOffset)
               << " objects=0x" << objectArray.Objects
               << " maxElements=" << std::dec << objectArray.MaxElements
               << " numElements=" << objectArray.NumElements
               << " maxChunks=" << objectArray.MaxChunks
               << " numChunks=" << objectArray.NumChunks;
            LogHookLine(ss.str());
        });

        if (!objectArray.Objects || objectArray.NumElements <= 0 || objectArray.NumChunks <= 0)
            return false;

        constexpr int32_t elementsPerChunk = 0x10000;
        for (int32_t chunkIndex = 0; chunkIndex < objectArray.NumChunks; ++chunkIndex)
        {
            uintptr_t chunkPtr = 0;
            if (!ReadPointer(objectArray.Objects + (static_cast<uintptr_t>(chunkIndex) * sizeof(uintptr_t)), chunkPtr) || !chunkPtr)
                continue;

            const int32_t chunkBaseIndex = chunkIndex * elementsPerChunk;
            const int32_t chunkRemaining = objectArray.NumElements - chunkBaseIndex;
            if (chunkRemaining <= 0)
                break;

            const int32_t chunkCount = (chunkRemaining < elementsPerChunk) ? chunkRemaining : elementsPerChunk;
            for (int32_t inChunkIndex = 0; inChunkIndex < chunkCount; ++inChunkIndex)
            {
                FUObjectItemMem item{};
                const uintptr_t itemAddress = chunkPtr + (static_cast<uintptr_t>(inChunkIndex) * sizeof(FUObjectItemMem));
                if (!MemoryReader::ReadMemory(itemAddress, &item, sizeof(item)) || !item.Object)
                    continue;

                const auto* object = reinterpret_cast<const UObject*>(item.Object);
                if (requiredType && !HasTypeFlag(object, requiredType))
                    continue;

                if (!MatchesKnownNameIndex(object, objectName.c_str()) && GetObjectNameLocal(object) != objectName)
                    continue;

                objectPtr = item.Object;
                return true;
            }
        }

        return false;
    }

    bool ResolveFunctionByGlobalScan(const char* ownerClassName, const char* funcName, uintptr_t& functionPtr)
    {
        functionPtr = 0;

        const auto gobjectsAddress = MemoryReader::FindGUObjectArray();
        if (!gobjectsAddress)
            return false;

        TUObjectArrayMem objectArray{};
        if (!MemoryReader::ReadMemory(gobjectsAddress + kFUObjectArrayObjObjectsOffset, &objectArray, sizeof(objectArray)))
            return false;

        if (!objectArray.Objects || objectArray.NumElements <= 0 || objectArray.NumChunks <= 0)
            return false;

        constexpr int32_t elementsPerChunk = 0x10000;
        for (int32_t chunkIndex = 0; chunkIndex < objectArray.NumChunks; ++chunkIndex)
        {
            uintptr_t chunkPtr = 0;
            if (!ReadPointer(objectArray.Objects + (static_cast<uintptr_t>(chunkIndex) * sizeof(uintptr_t)), chunkPtr) || !chunkPtr)
                continue;

            const int32_t chunkBaseIndex = chunkIndex * elementsPerChunk;
            const int32_t chunkRemaining = objectArray.NumElements - chunkBaseIndex;
            if (chunkRemaining <= 0)
                break;

            const int32_t chunkCount = (chunkRemaining < elementsPerChunk) ? chunkRemaining : elementsPerChunk;
            for (int32_t inChunkIndex = 0; inChunkIndex < chunkCount; ++inChunkIndex)
            {
                FUObjectItemMem item{};
                const uintptr_t itemAddress = chunkPtr + (static_cast<uintptr_t>(inChunkIndex) * sizeof(FUObjectItemMem));
                if (!MemoryReader::ReadMemory(itemAddress, &item, sizeof(item)) || !item.Object)
                    continue;

                const auto* object = reinterpret_cast<const UObject*>(item.Object);
                if (!MatchesKnownNameIndex(object, funcName) && GetObjectNameLocal(object) != funcName)
                    continue;

                const auto* outer = reinterpret_cast<const UObject*>(object->OuterPrivate);
                if (!outer)
                    continue;

                if (!MatchesKnownNameIndex(outer, ownerClassName) && GetObjectNameLocal(outer) != ownerClassName)
                    continue;

                functionPtr = item.Object;
                return true;
            }
        }

        return false;
    }

    bool ResolveFunctionByNameAnyOwner(const char* funcName, uintptr_t& functionPtr, uintptr_t& ownerPtr)
    {
        functionPtr = 0;
        ownerPtr = 0;

        const auto gobjectsAddress = MemoryReader::FindGUObjectArray();
        if (!gobjectsAddress)
            return false;

        TUObjectArrayMem objectArray{};
        if (!MemoryReader::ReadMemory(gobjectsAddress + kFUObjectArrayObjObjectsOffset, &objectArray, sizeof(objectArray)))
            return false;

        if (!objectArray.Objects || objectArray.NumElements <= 0 || objectArray.NumChunks <= 0)
            return false;

        constexpr int32_t elementsPerChunk = 0x10000;
        for (int32_t chunkIndex = 0; chunkIndex < objectArray.NumChunks; ++chunkIndex)
        {
            uintptr_t chunkPtr = 0;
            if (!ReadPointer(objectArray.Objects + (static_cast<uintptr_t>(chunkIndex) * sizeof(uintptr_t)), chunkPtr) || !chunkPtr)
                continue;

            const int32_t chunkBaseIndex = chunkIndex * elementsPerChunk;
            const int32_t chunkRemaining = objectArray.NumElements - chunkBaseIndex;
            if (chunkRemaining <= 0)
                break;

            const int32_t chunkCount = (chunkRemaining < elementsPerChunk) ? chunkRemaining : elementsPerChunk;
            for (int32_t inChunkIndex = 0; inChunkIndex < chunkCount; ++inChunkIndex)
            {
                FUObjectItemMem item{};
                const uintptr_t itemAddress = chunkPtr + (static_cast<uintptr_t>(inChunkIndex) * sizeof(FUObjectItemMem));
                if (!MemoryReader::ReadMemory(itemAddress, &item, sizeof(item)) || !item.Object)
                    continue;

                const auto* object = reinterpret_cast<const UObject*>(item.Object);
                if (!HasTypeFlag(object, kClassCastFlagFunction))
                    continue;

                if (!MatchesKnownNameIndex(object, funcName) && GetObjectNameLocal(object) != funcName)
                    continue;

                const auto* outer = reinterpret_cast<const UObject*>(object->OuterPrivate);
                functionPtr = item.Object;
                ownerPtr = reinterpret_cast<uintptr_t>(outer);
                return true;
            }
        }

        return false;
    }

    std::string TrimCopy(std::string value)
    {
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.front())))
            value.erase(value.begin());
        while (!value.empty() && std::isspace(static_cast<unsigned char>(value.back())))
            value.pop_back();
        return value;
    }

    bool LooksLikeRpcChannel(UObject* object)
    {
        if (!object)
            return false;

        NativeChatContext ctx{};
        return BuildChatContextFromRpcChannel(reinterpret_cast<uintptr_t>(object), ctx);
    }

    bool IsLikelyChatPayload(UObject* object, void* params, std::string& message, uint8_t& rawChannel)
    {
        message.clear();
        rawChannel = 0;

        if (!object || !params)
            return false;

        if (!LooksLikeRpcChannel(object))
            return false;

        if (!ReadFStringUtf8(reinterpret_cast<uintptr_t>(params), message, 512))
            return false;

        message = TrimCopy(message);
        if (message.empty() || message.size() > 400)
            return false;

        MemoryReader::ReadMemory(reinterpret_cast<uintptr_t>(params) + 0x10, &rawChannel, sizeof(rawChannel));
        if (rawChannel > kChatTypeError)
            rawChannel = kChatTypeDefault;

        bool hasVisible = false;
        for (char ch : message)
        {
            const auto uch = static_cast<unsigned char>(ch);
            if (uch >= 0x20 && ch != '\r' && ch != '\n')
            {
                hasVisible = true;
                break;
            }
        }

        return hasVisible;
    }

    uintptr_t ResolveControllerByDatabaseId(int64_t databaseId)
    {
        if (databaseId <= 0)
            return 0;

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

            int64_t currentDatabaseId = 0;
            if (!ReadInt64(pawn + kPrisonerServerUserProfileIdOffset, currentDatabaseId) || currentDatabaseId != databaseId)
                continue;

            uintptr_t controller = 0;
            if (ReadPointer(pawn + kPawnControllerOffset, controller) && controller)
                return controller;
        }

        return 0;
    }

    bool EnsureMiscStaticsBase(uintptr_t& invokeTarget)
    {
        invokeTarget = 0;

        auto miscClassPtr = s_MiscStaticsClass.load();
        auto miscDefaultObjectPtr = s_MiscStaticsDefaultObject.load();

        if (!miscClassPtr)
        {
            ResolveObjectByName("MiscStatics", 0, miscClassPtr);
            if (!miscClassPtr)
            {
                uintptr_t resolvedFn = 0;
                uintptr_t ownerPtr = 0;
                const char* fallbackNames[] = {
                    "BroadcastChatLine",
                    "SendChatLineToPlayer",
                    "Test_ProcessAdminCommand"
                };

                for (const auto* fallbackName : fallbackNames)
                {
                    if (ResolveFunctionByNameAnyOwner(fallbackName, resolvedFn, ownerPtr) && ownerPtr)
                    {
                        miscClassPtr = ownerPtr;

                        std::ostringstream ss;
                        ss << "Resolved MiscStatics base via owner fallback. source=" << fallbackName
                           << " owner=0x" << std::hex << ownerPtr
                           << " fn=0x" << resolvedFn;
                        LogHookLine(ss.str());
                        break;
                    }
                }
            }

            if (miscClassPtr)
            {
                s_MiscStaticsClass = miscClassPtr;
                const auto* klass = reinterpret_cast<const UClassRaw*>(miscClassPtr);
                miscDefaultObjectPtr = reinterpret_cast<uintptr_t>(klass->ClassDefaultObject);
                if (miscDefaultObjectPtr)
                    s_MiscStaticsDefaultObject = miscDefaultObjectPtr;
            }
        }

        invokeTarget = miscDefaultObjectPtr ? miscDefaultObjectPtr : miscClassPtr;
        return invokeTarget != 0;
    }

    bool EnsureMiscStaticsFunction(const char* functionName, std::atomic_uintptr_t& cache, uintptr_t& functionPtr)
    {
        functionPtr = cache.load();
        if (functionPtr)
            return true;

        uintptr_t resolved = 0;
        if (!ResolveFunctionByGlobalScan("MiscStatics", functionName, resolved))
        {
            uintptr_t ownerPtr = 0;
            if (!ResolveFunctionByNameAnyOwner(functionName, resolved, ownerPtr))
                return false;

            if (ownerPtr)
            {
                s_MiscStaticsClass = ownerPtr;
                const auto* klass = reinterpret_cast<const UClassRaw*>(ownerPtr);
                const auto cdo = reinterpret_cast<uintptr_t>(klass->ClassDefaultObject);
                if (cdo)
                    s_MiscStaticsDefaultObject = cdo;
            }

            std::ostringstream ss;
            ss << "Resolved MiscStatics fallback by function name. fn=" << functionName
               << " owner=0x" << std::hex << ownerPtr
               << " resolved=0x" << resolved;
            LogHookLine(ss.str());
        }

        cache = resolved;
        functionPtr = resolved;
        return true;
    }

    bool ExecuteMiscBroadcastChat(const std::string& rawText, uint8_t chatType)
    {
        auto text = TrimCopy(rawText);
        if (text.empty())
            return false;

        uintptr_t invokeTarget = 0;
        if (!EnsureMiscStaticsBase(invokeTarget))
            return false;

        uintptr_t functionPtr = 0;
        if (!EnsureMiscStaticsFunction("BroadcastChatLine", s_MiscStaticsBroadcastChatLineFunction, functionPtr))
            return false;

        uintptr_t worldObject = 0;
        if (!ResolveWorldObject(worldObject) || !worldObject)
            return false;

        ProcessEventFn processEvent = nullptr;
        if (!GetProcessEventInvoker(invokeTarget, processEvent) || !processEvent)
            return false;

        auto wideText = Utf8ToWideLocal(text);
        if (wideText.empty())
            return false;

        std::vector<wchar_t> chars(wideText.begin(), wideText.end());
        chars.push_back(L'\0');

        MiscBroadcastChatParams params{};
        params.WorldContextObject = reinterpret_cast<UObject*>(worldObject);
        params.text.Data = chars.data();
        params.text.Count = static_cast<int32_t>(chars.size());
        params.text.Max = static_cast<int32_t>(chars.size());
        params.chatType = chatType;

        processEvent(
            reinterpret_cast<UObject*>(invokeTarget),
            reinterpret_cast<UObject*>(functionPtr),
            &params);

        std::ostringstream ss;
        ss << "CMD(misc-broadcast) -> " << text
           << " world=0x" << std::hex << worldObject
           << " fn=0x" << functionPtr
           << " chatType=" << std::dec << static_cast<int>(chatType);
        LogHookLine(ss.str());
        return true;
    }

    bool ExecuteMiscSendChatLine(int64_t databaseId, const std::string& rawText, uint8_t chatType)
    {
        auto text = TrimCopy(rawText);
        if (databaseId <= 0 || text.empty())
            return false;

        auto controller = ResolveControllerByDatabaseId(databaseId);
        if (!controller)
        {
            const auto rpcChannel = s_LastLiveRpcChannel.load();
            if (rpcChannel)
            {
                NativeChatContext ctx{};
                if (BuildChatContextFromRpcChannel(rpcChannel, ctx) && ctx.DatabaseId == databaseId)
                {
                    controller = ResolveControllerFromRpcChannel(rpcChannel);
                    if (controller)
                    {
                        std::ostringstream ss;
                        ss << "Resolved controller from live rpc fallback. dbId=" << std::dec << databaseId
                           << " rpc=0x" << std::hex << rpcChannel
                           << " controller=0x" << controller;
                        LogHookLine(ss.str());
                    }
                }
            }
        }

        if (!controller)
        {
            std::ostringstream ss;
            ss << "ExecuteMiscSendChatLine failed: controller not found for dbId=" << std::dec << databaseId
               << " text=" << text;
            LogHookLine(ss.str());
            return false;
        }

        uintptr_t invokeTarget = 0;
        if (!EnsureMiscStaticsBase(invokeTarget))
        {
            std::ostringstream ss;
            ss << "ExecuteMiscSendChatLine failed: MiscStatics base unavailable for dbId=" << std::dec << databaseId;
            LogHookLine(ss.str());
            return false;
        }

        uintptr_t functionPtr = 0;
        if (!EnsureMiscStaticsFunction("SendChatLineToPlayer", s_MiscStaticsSendChatLineFunction, functionPtr))
        {
            std::ostringstream ss;
            ss << "ExecuteMiscSendChatLine failed: SendChatLineToPlayer unresolved for dbId=" << std::dec << databaseId;
            LogHookLine(ss.str());
            return false;
        }

        ProcessEventFn processEvent = nullptr;
        if (!GetProcessEventInvoker(invokeTarget, processEvent) || !processEvent)
        {
            std::ostringstream ss;
            ss << "ExecuteMiscSendChatLine failed: ProcessEvent invoker unavailable for dbId=" << std::dec << databaseId;
            LogHookLine(ss.str());
            return false;
        }

        auto wideText = Utf8ToWideLocal(text);
        if (wideText.empty())
        {
            std::ostringstream ss;
            ss << "ExecuteMiscSendChatLine failed: Utf8ToWide conversion failed for dbId=" << std::dec << databaseId;
            LogHookLine(ss.str());
            return false;
        }

        std::vector<wchar_t> chars(wideText.begin(), wideText.end());
        chars.push_back(L'\0');

        MiscSendChatLineParams params{};
        params.PlayerController = reinterpret_cast<UObject*>(controller);
        params.text.Data = chars.data();
        params.text.Count = static_cast<int32_t>(chars.size());
        params.text.Max = static_cast<int32_t>(chars.size());
        params.chatType = chatType;
        params.shouldCopyToClientClipboard = false;

        processEvent(
            reinterpret_cast<UObject*>(invokeTarget),
            reinterpret_cast<UObject*>(functionPtr),
            &params);

        std::ostringstream ss;
        ss << "CMD(misc-sendchat) -> dbId=" << std::dec << databaseId
           << " text=" << text
           << " controller=0x" << std::hex << controller
           << " fn=0x" << functionPtr
           << " chatType=" << std::dec << static_cast<int>(chatType);
        LogHookLine(ss.str());
        return true;
    }

    bool TryExecuteStructuredCommand(const std::string& normalizedCommand)
    {
        auto command = TrimCopy(normalizedCommand);
        if (command.empty())
            return false;

        while (!command.empty() && command.front() == '#')
            command.erase(command.begin());
        command = TrimCopy(command);
        if (command.empty())
            return false;

        const auto split = command.find(' ');
        const auto verb = split == std::string::npos ? command : command.substr(0, split);
        auto rest = split == std::string::npos ? std::string{} : TrimCopy(command.substr(split + 1));

        if (_stricmp(verb.c_str(), "Announce") == 0 || _stricmp(verb.c_str(), "Broadcast") == 0)
        {
            return ExecuteMiscBroadcastChat(rest, kChatTypeServerMessage);
        }

        if (_stricmp(verb.c_str(), "SendNotification") == 0)
        {
            std::istringstream parser(rest);
            int notificationType = 1;
            long long databaseId = 0;
            if (!(parser >> notificationType >> databaseId))
                return false;

            std::string text;
            const auto quoted = rest.find('"');
            if (quoted != std::string::npos)
            {
                text = rest.substr(quoted);
            }
            else
            {
                const auto position = parser.tellg();
                if (position < 0)
                    return false;

                text = rest.substr(static_cast<size_t>(position));
            }

            text = TrimCopy(text);
            if (!text.empty() && text.front() == '"')
            {
                text.erase(text.begin());
                if (!text.empty() && text.back() == '"')
                    text.pop_back();
            }

            uint8_t chatType = kChatTypeServerMessage;
            if (notificationType == 2)
                chatType = kChatTypeError;
            else if (notificationType == 4)
                chatType = kChatTypeAdmin;

            return ExecuteMiscSendChatLine(databaseId, text, chatType);
        }

        return false;
    }

    uintptr_t ResolveFunctionFromClass(UObject* ownerObject, const char* className, const char* funcName)
    {
        if (!ownerObject || !ownerObject->ClassPrivate || !className || !funcName)
            return 0;

        for (auto* current = reinterpret_cast<UStructRaw*>(ownerObject->ClassPrivate); current; current = current->SuperStruct)
        {
            const auto* currentObject = reinterpret_cast<UObject*>(current);
            if (!MatchesKnownNameIndex(currentObject, className) && GetObjectNameLocal(currentObject) != className)
                continue;

            for (auto* field = current->Children; field; field = field->Next)
            {
                const auto* fieldObject = reinterpret_cast<UObject*>(field);
                if (MatchesKnownNameIndex(fieldObject, funcName) || GetObjectNameLocal(fieldObject) == funcName)
                    return reinterpret_cast<uintptr_t>(fieldObject);
            }
        }

        uintptr_t globalFunction = 0;
        if (ResolveFunctionByGlobalScan(className, funcName, globalFunction))
            return globalFunction;

        return 0;
    }

    bool IsNamedFunction(UObject* function, const char* ownerClassName, const char* functionName)
    {
        if (!function || !ownerClassName || !functionName)
            return false;

        if (!MatchesKnownNameIndex(function, functionName) && GetObjectNameLocal(function) != functionName)
            return false;

        const auto* outer = reinterpret_cast<const UObject*>(function->OuterPrivate);
        if (!outer)
            return false;

        return MatchesKnownNameIndex(outer, ownerClassName) || GetObjectNameLocal(outer) == ownerClassName;
    }

    bool ResolveWorldObject(uintptr_t& worldObject)
    {
        worldObject = 0;

        const auto gworldPtrAddress = MemoryReader::FindGWorld();
        if (!gworldPtrAddress)
            return false;

        return ReadPointer(gworldPtrAddress, worldObject) && worldObject;
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

    bool ResolveFirstLiveRpcChannel(uintptr_t& rpcChannel)
    {
        rpcChannel = 0;

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
            return false;

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
                return true;
            }
        }

        return false;
    }

    std::wstring Utf8ToWideLocal(const std::string& input)
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

    bool GetProcessEventInvoker(uintptr_t objectPtr, ProcessEventFn& invoker)
    {
        invoker = o_ProcessEvent;
        if (invoker)
            return true;

        uintptr_t vtable = 0;
        if (!ReadPointer(objectPtr, vtable) || !vtable)
            return false;

        uintptr_t slotValue = 0;
        if (!ReadPointer(vtable + (kProcessEventVtableIndex * sizeof(uintptr_t)), slotValue) || !slotValue)
            return false;

        invoker = reinterpret_cast<ProcessEventFn>(slotValue);
        return true;
    }

    bool TryExecuteAdminCommandInternal(const std::string& rawCommand)
    {
        auto command = rawCommand;
        while (!command.empty() && std::isspace(static_cast<unsigned char>(command.front())))
            command.erase(command.begin());
        while (!command.empty() && std::isspace(static_cast<unsigned char>(command.back())))
            command.pop_back();
        while (!command.empty() && command.front() == '#')
            command.erase(command.begin());

        if (command.empty())
            return false;

        if (TryExecuteStructuredCommand(command))
            return true;

        auto wideCommand = Utf8ToWideLocal(command);
        if (wideCommand.empty())
            return false;

        std::vector<wchar_t> chars(wideCommand.begin(), wideCommand.end());
        chars.push_back(L'\0');

        AdminCommandParams params{};
        params.commandText.Data = chars.data();
        params.commandText.Count = static_cast<int32_t>(chars.size());
        params.commandText.Max = static_cast<int32_t>(chars.size());

        auto miscFunctionPtr = s_MiscStaticsAdminFunction.load();
        auto miscClassPtr = s_MiscStaticsClass.load();
        auto miscDefaultObjectPtr = s_MiscStaticsDefaultObject.load();

        if (!miscClassPtr)
        {
            ResolveObjectByName("MiscStatics", kClassCastFlagClass, miscClassPtr);
            if (miscClassPtr)
            {
                s_MiscStaticsClass = miscClassPtr;
                const auto* klass = reinterpret_cast<const UClassRaw*>(miscClassPtr);
                miscDefaultObjectPtr = reinterpret_cast<uintptr_t>(klass->ClassDefaultObject);
                if (miscDefaultObjectPtr)
                    s_MiscStaticsDefaultObject = miscDefaultObjectPtr;
            }
        }

        if (!miscFunctionPtr && miscClassPtr)
        {
            miscFunctionPtr = ResolveFunctionFromClass(reinterpret_cast<UObject*>(miscClassPtr), "MiscStatics", "Test_ProcessAdminCommand");
            if (miscFunctionPtr)
                s_MiscStaticsAdminFunction = miscFunctionPtr;
        }

        if (!miscFunctionPtr)
        {
            uintptr_t ownerPtr = 0;
            uintptr_t resolvedFn = 0;
            if (ResolveFunctionByNameAnyOwner("Test_ProcessAdminCommand", resolvedFn, ownerPtr))
            {
                miscFunctionPtr = resolvedFn;
                s_MiscStaticsAdminFunction = resolvedFn;

                if (!miscClassPtr && ownerPtr)
                {
                    miscClassPtr = ownerPtr;
                    s_MiscStaticsClass = ownerPtr;
                    const auto* klass = reinterpret_cast<const UClassRaw*>(ownerPtr);
                    miscDefaultObjectPtr = reinterpret_cast<uintptr_t>(klass->ClassDefaultObject);
                    if (miscDefaultObjectPtr)
                        s_MiscStaticsDefaultObject = miscDefaultObjectPtr;
                }

                std::ostringstream ss;
                ss << "Resolved static admin fallback by function name. owner=0x" << std::hex << ownerPtr
                   << " fn=0x" << resolvedFn;
                LogHookLine(ss.str());
            }
        }

        const auto miscInvokeTarget = miscDefaultObjectPtr ? miscDefaultObjectPtr : miscClassPtr;

        if (!miscClassPtr || !miscFunctionPtr)
        {
            std::ostringstream ss;
            ss << "Static admin path unresolved: class=0x" << std::hex << miscClassPtr
               << " target=0x" << miscInvokeTarget
               << " fn=0x" << miscFunctionPtr;
            LogHookLine(ss.str());
        }

        uintptr_t worldObject = 0;
        if (miscInvokeTarget && miscFunctionPtr && ResolveWorldObject(worldObject))
        {
            ProcessEventFn processEvent = nullptr;
            if (GetProcessEventInvoker(miscInvokeTarget, processEvent) && processEvent)
            {
                StaticAdminCommandParams staticParams{};
                staticParams.WorldContextObject = reinterpret_cast<UObject*>(worldObject);
                staticParams.commandText = params.commandText;

                processEvent(
                    reinterpret_cast<UObject*>(miscInvokeTarget),
                    reinterpret_cast<UObject*>(miscFunctionPtr),
                    &staticParams);

                std::ostringstream ss;
                ss << "CMD(process-event/static) -> " << command
                   << " world=0x" << std::hex << worldObject
                   << " target=0x" << miscInvokeTarget
                   << " fn=0x" << miscFunctionPtr;
                LogHookLine(ss.str());
                return true;
            }
        }

        auto rpcChannel = s_LastLiveRpcChannel.load();
        if (!rpcChannel && !ResolveFirstLiveRpcChannel(rpcChannel))
        {
            LogHookLine("Direct admin command skipped: no live UPlayerRpcChannel and static admin path unavailable.");
            return false;
        }

        auto functionPtr = s_AdminCommandFunction.load();
        if (!functionPtr)
        {
            functionPtr = ResolveFunctionFromClass(reinterpret_cast<UObject*>(rpcChannel), "PlayerRpcChannel", "Chat_Server_ProcessAdminCommand");
            if (functionPtr)
            {
                s_AdminCommandFunction = functionPtr;
            }
            else
            {
                LogHookLine("Direct admin command skipped: Chat_Server_ProcessAdminCommand UFunction unavailable.");
                return false;
            }
        }

        ProcessEventFn processEvent = nullptr;
        if (!GetProcessEventInvoker(rpcChannel, processEvent) || !processEvent)
        {
            LogHookLine("Direct admin command skipped: ProcessEvent invoker unavailable.");
            return false;
        }

        processEvent(
            reinterpret_cast<UObject*>(rpcChannel),
            reinterpret_cast<UObject*>(functionPtr),
            &params);

        std::ostringstream ss;
        ss << "CMD(process-event/rpc) -> " << command
           << " rpc=0x" << std::hex << rpcChannel
           << " fn=0x" << functionPtr;
        LogHookLine(ss.str());
        return true;
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

        std::string message;
        int32_t chatType = 0;
        bool isChatEvent = false;
        uint8_t heuristicChannel = 0;

        if (s_BroadcastChatFunction.load() == reinterpret_cast<uintptr_t>(function)
            || IsNamedFunction(function, "PlayerRpcChannel", "Chat_Server_BroadcastChatMessage"))
        {
            s_BroadcastChatFunction = reinterpret_cast<uintptr_t>(function);
            s_LastLiveRpcChannel = reinterpret_cast<uintptr_t>(object);
            if (!ReadFStringUtf8(reinterpret_cast<uintptr_t>(params), message))
                return;

            uint8_t rawChannel = 0;
            MemoryReader::ReadMemory(reinterpret_cast<uintptr_t>(params) + 0x10, &rawChannel, sizeof(rawChannel));
            chatType = static_cast<int32_t>(rawChannel);
            isChatEvent = true;
        }
        else if (s_AdminCommandFunction.load() == reinterpret_cast<uintptr_t>(function)
            || IsNamedFunction(function, "PlayerRpcChannel", "Chat_Server_ProcessAdminCommand"))
        {
            s_AdminCommandFunction = reinterpret_cast<uintptr_t>(function);
            s_LastLiveRpcChannel = reinterpret_cast<uintptr_t>(object);
            if (!ReadFStringUtf8(reinterpret_cast<uintptr_t>(params), message))
                return;

            chatType = 4;
            isChatEvent = true;
        }

        if (!isChatEvent && IsLikelyChatPayload(object, params, message, heuristicChannel))
        {
            s_LastLiveRpcChannel = reinterpret_cast<uintptr_t>(object);

            const auto functionPtr = reinterpret_cast<uintptr_t>(function);
            if (!message.empty() && (message.front() == '/' || message.front() == '!' || message.front() == '#'))
            {
                s_BroadcastChatFunction = functionPtr;
                chatType = static_cast<int32_t>(heuristicChannel);
                isChatEvent = true;

                std::ostringstream ss;
                ss << "Auto-learned chat rpc function from live payload. fn=0x"
                   << std::hex << functionPtr
                   << " channel=" << std::dec << static_cast<int>(heuristicChannel)
                   << " text=" << message;
                LogHookLine(ss.str());
            }
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
    LogHookLine("ProcessEvent hook disabled in safe mode; using non-hook chat/event paths.");
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

    const auto slot = kProcessEventVtableIndex;

    DWORD oldProtect = 0;
    auto* slotPtr = reinterpret_cast<uintptr_t*>(vtable + (slot * sizeof(uintptr_t)));
    uintptr_t slotValue = 0;
    if (!ReadPointer(reinterpret_cast<uintptr_t>(slotPtr), slotValue) || !slotValue)
    {
        LogHookLine("ProcessEvent vtable slot is empty/unreadable.");
        return false;
    }

    if (slotValue == reinterpret_cast<uintptr_t>(&hk_ProcessEvent))
    {
        s_HookedVtable = vtable;
        s_HookedSlot = slot;
        s_ProcessEventHooked = true;
        return true;
    }

    if (!VirtualProtect(slotPtr, sizeof(uintptr_t), PAGE_EXECUTE_READWRITE, &oldProtect))
    {
        LogHookLine("VirtualProtect failed while patching UPlayerRpcChannel vtable.");
        return false;
    }

    o_ProcessEvent = reinterpret_cast<ProcessEventFn>(slotValue);
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

bool HookManager::ExecuteAdminCommandDirect(const char* commandText)
{
    if (!commandText || !commandText[0])
        return false;

    return TryExecuteAdminCommandInternal(commandText);
}

} // namespace ScumOxygen
