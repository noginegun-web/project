#pragma once

// ======== SCUM v1.2.1.1 (2026) UE4 SDK ========
// Сгенерировано на основе UE4SS дампов

#include <windows.h>
#include <string>
#include <vector>

namespace ScumOxygen {

// === Базовые структуры UE4 ===

template<typename T>
struct TArray {
    T* Data;
    int32_t Count;
    int32_t Max;
    
    T& operator[](int32_t i) { return Data[i]; }
    int32_t Num() const { return Count; }
    bool IsValid() const { return Data != nullptr && Count > 0; }
};

struct FString {
    TArray<wchar_t> Data;
    
    std::wstring ToWString() const {
        if (!IsValid()) return L"";
        return std::wstring(Data.Data);
    }
    
    std::string ToString() const {
        std::wstring ws = ToWString();
        if (ws.empty()) return "";
        int len = WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), -1, nullptr, 0, nullptr, nullptr);
        std::string s(len, 0);
        WideCharToMultiByte(CP_UTF8, 0, ws.c_str(), -1, &s[0], len, nullptr, nullptr);
        s.pop_back();
        return s;
    }
    
    bool IsValid() const { return Data.IsValid(); }
};

struct FName {
    int32_t ComparisonIndex;
    int32_t Number;
};

struct FVector {
    float X, Y, Z;
};

struct FRotator {
    float Pitch, Yaw, Roll;
};

// === Иерархия классов SCUM ===

// UObject (размер: 0x28)
struct UObject {
    void** VTable;                    // 0x00
    int32_t ObjectFlags;              // 0x08
    int32_t InternalIndex;            // 0x0C
    void* ClassPrivate;               // 0x10 (UClass*)
    FName NamePrivate;                // 0x18
    void* OuterPrivate;               // 0x20 (UObject*)
};

// AActor : UObject (размер: ~0x2B0)
struct AActor : UObject {
    char pad_28[0x288];               // 0x28 - 0x2B0
    // ... поля актора
};

// AController : AActor (размер: ~0x2E0)
struct AController : AActor {
    char pad_2B0[0x30];               // 0x2B0
    void* Pawn;                       // 0x2E0 (APawn*)
};

// APlayerController : AController (размер: ~0x4C0)
struct APlayerController : AController {
    char pad_2E8[0x1D8];              // 0x2E8 - 0x4C0
    void* Player;                     // 0x4C0 (UPlayer*)
    void* AcknowledgedPawn;           // 0x4C8 (APawn*)
    void* MyHUD;                      // 0x4D0 (AHUD*)
    void* PlayerCameraManager;        // 0x4D8 (APlayerCameraManager*)
};

// APlayerState : AActor (размер: ~0x350)
struct APlayerState : AActor {
    char pad_2B0[0x48];               // 0x2B0
    float Score;                      // 0x2F8
    float DamageDealt;                // 0x2FC
    int32_t Ping;                     // 0x300
    char pad_304[0x4];                // 0x304
    FString PlayerNamePrivate;        // 0x308
    int32_t PlayerId;                 // 0x318
    char pad_31C[0x4];                // 0x31C
    void* CurrPawn;                   // 0x320 (APawn*)
    void* PrevPawn;                   // 0x328 (APawn*)
    // ...
};

// APawn : AActor (размер: ~0x310)
struct APawn : AActor {
    char pad_2B0[0x20];               // 0x2B0
    void* Controller;                 // 0x2D0 (AController*)
    char pad_2D8[0x38];               // 0x2D8
};

// ACharacter : APawn (размер: ~0x680)
struct ACharacter : APawn {
    char pad_310[0x370];              // 0x310 - 0x680
    void* Mesh;                       // 0x680 (USkeletalMeshComponent*)
};

// APrisoner : ACharacter (размер: ~0x2F28 из дампов UE4SS)
struct APrisoner : ACharacter {
    char pad_688[0x28A0];             // 0x688 - 0x2F28
    
    // Ключевые поля (смещения нужно подтвердить через CE):
    // FString PlayerName;            // ~0x8D0
    // FVector Location;              // через RootComponent
    // float Health;                  // ~0x1A00 (из метаболизма)
    // float Stamina;                 // ~0x1A04
    // int32_t FamePoints;            // ~0x1A10
};

// AConZGameStateBase : AGameStateBase
struct AConZGameStateBase : AActor {
    char pad_2B0[0x68];               // 0x2B0
    TArray<void*> PlayerArray;        // 0x318 (TArray<APlayerState*>)
    // ...
};

// AConZGameState : AConZGameStateBase (размер: ~0x8D0 из дампов)
struct AConZGameState : AConZGameStateBase {
    char pad_328[0x5A8];              // 0x328 - 0x8D0
    // Поля специфичные для SCUM
    float ServerTime;                 // ~0x400
    int32_t OnlinePlayers;            // ~0x404
    // ...
};

// UWorld (размер: ~0x190)
struct UWorld {
    char pad_00[0x30];                // UObject header
    char pad_30[0x8];                 // 0x30
    void* PersistentLevel;            // 0x38 (ULevel*)
    char pad_40[0x128];               // 0x40
    AConZGameState* GameState;        // 0x168
    char pad_170[0x20];               // 0x170
};

// === Функции SDK ===

inline UWorld** GetGWorld() {
    // Ищем через сигнатуру в SCUMServer.exe
    // Паттерн: 48 8B 05 ? ? ? ? 48 85 C0 74 ? 48 8B 40 20 C3
    uintptr_t base = (uintptr_t)GetModuleHandleA(nullptr);
    if (!base) return nullptr;
    
    // TODO: Найти реальный адрес GWorld через сигнатурный поиск
    // Смещение меняется с каждой версией игры
    static UWorld** gworld = nullptr;
    if (!gworld) {
        // Пример поиска - нужно подтвердить для текущей версии
        auto pattern = "\x48\x8B\x05\x00\x00\x00\x00\x48\x85\xC0\x74\x00\x48\x8B\x40\x20\xC3";
        auto mask = "xxx????xxx?xxxxxx";
        // ... поиск
    }
    
    return gworld;
}

inline std::vector<APlayerState*> GetOnlinePlayers() {
    std::vector<APlayerState*> result;
    
    auto** gworld = GetGWorld();
    if (!gworld || !*gworld) return result;
    
    UWorld* world = *gworld;
    if (!world->GameState) return result;
    
    auto& playerArray = world->GameState->PlayerArray;
    for (int32_t i = 0; i < playerArray.Num(); i++) {
        if (playerArray.Data[i]) {
            result.push_back((APlayerState*)playerArray.Data[i]);
        }
    }
    
    return result;
}

} // namespace ScumOxygen
