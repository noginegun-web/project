#include "../include/sdk.h"
#include "../include/scum_sdk.h"
#include <windows.h>
#include <psapi.h>
#include <vector>

namespace ScumOxygen {

HANDLE MemoryReader::s_ProcessHandle = nullptr;
DWORD MemoryReader::s_ProcessId = 0;

bool MemoryReader::Initialize() {
    // Для DLL внутри процесса используем GetCurrentProcess
    s_ProcessId = GetCurrentProcessId();
    s_ProcessHandle = GetCurrentProcess();
    
    printf("[ScumOxygen] MemoryReader initialized for PID: %lu\n", s_ProcessId);
    return s_ProcessHandle != nullptr;
}

void MemoryReader::Shutdown() {
    s_ProcessHandle = nullptr;
}

bool MemoryReader::ReadMemory(uintptr_t address, void* buffer, size_t size) {
    if (!buffer || size == 0) return false;
    
    // Внутри процесса можно читать напрямую
    __try {
        memcpy(buffer, (void*)address, size);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

std::string MemoryReader::ReadString(uintptr_t address, size_t maxLength) {
    if (!address) return "";
    
    std::vector<char> buffer(maxLength + 1, 0);
    if (ReadMemory(address, buffer.data(), maxLength)) {
        buffer[maxLength] = '\0';
        return std::string(buffer.data());
    }
    
    return "";
}

uintptr_t MemoryReader::GetModuleBase(const char* moduleName) {
    if (!moduleName || !moduleName[0]) {
        // Возвращаем базу основного модуля
        return (uintptr_t)GetModuleHandleA(nullptr);
    }
    return (uintptr_t)GetModuleHandleA(moduleName);
}

uintptr_t MemoryReader::FindPattern(const char* module, const char* pattern, const char* mask) {
    uintptr_t base = GetModuleBase(module);
    if (!base) return 0;
    
    MODULEINFO modInfo = {};
    HMODULE hMod = module ? GetModuleHandleA(module) : GetModuleHandleA(nullptr);
    if (!GetModuleInformation(GetCurrentProcess(), hMod, &modInfo, sizeof(modInfo))) {
        return 0;
    }
    
    return FindPattern(base, modInfo.SizeOfImage, pattern, mask);
}

uintptr_t MemoryReader::FindPattern(uintptr_t start, size_t length, const char* pattern, const char* mask) {
    size_t patternLen = strlen(mask);
    if (patternLen == 0 || length < patternLen) {
        return 0;
    }
    
    for (size_t i = 0; i + patternLen <= length; i++) {
        bool found = true;
        
        for (size_t j = 0; j < patternLen; j++) {
            if (mask[j] == 'x' && *(char*)(start + i + j) != pattern[j]) {
                found = false;
                break;
            }
        }
        
        if (found) {
            return start + i;
        }
    }
    
    return 0;
}

// === GWorld Finding ===

struct FMemoryImageResult {
    uintptr_t address;
    bool found;
};

uintptr_t MemoryReader::FindGWorld() {
    const uintptr_t moduleBase = GetModuleBase(nullptr);
    if (!moduleBase) {
        return 0;
    }

    struct CandidatePattern {
        const char* pattern;
        const char* mask;
        size_t ripOffset;
        size_t instructionSize;
    };

    static const CandidatePattern candidates[] = {
        // Common UE4 client pattern.
        { "\x48\x8B\x05\x00\x00\x00\x00\x48\x85\xC0\x74\x00\x48\x8B\x40\x20", "xxx????xxxx?xxxx", 3, 7 },
        // Common UE4 client pattern with trailing return.
        { "\x48\x8B\x05\x00\x00\x00\x00\x48\x85\xC0\x74\x00\x48\x8B\x40\x20\xC3", "xxx????xxxx?xxxxx", 3, 7 },
        // Dedicated-server variant found in SCUMServer.exe 1.2.1.1.
        { "\x48\x8B\x1D\x00\x00\x00\x00\x48\x85\xDB\x74\x00\x41\xB0\x01", "xxx????xxxx?xxx", 3, 7 }
    };

    for (const auto& candidate : candidates) {
        uintptr_t addr = FindPattern(nullptr, candidate.pattern, candidate.mask);
        if (!addr) {
            continue;
        }

        int32_t offset = *(int32_t*)(addr + candidate.ripOffset);
        uintptr_t gworldAddr = addr + candidate.instructionSize + offset;
        if (gworldAddr) {
            return gworldAddr;
        }
    }

    // Dedicated server fallback for SCUM 1.2.1.1 / app 3792580.
    // Derived from the live SCUMServer.exe instruction:
    //   48 8B 1D 7C 25 8A 06 48 85 DB 74 3B 41 B0 01
    // which resolves to RVA 0x0719F8B0.
    return moduleBase + 0x0719F8B0;

}

} // namespace ScumOxygen
