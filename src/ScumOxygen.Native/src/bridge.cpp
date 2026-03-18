#include "../include/sdk.h"
#include <windows.h>
#include <string>
#include <queue>
#include <mutex>

namespace ScumOxygen {

// Именованный pipe для связи с .NET
static HANDLE s_Pipe = INVALID_HANDLE_VALUE;
static std::thread s_ReaderThread;
static bool s_Running = false;
static CommandCallback s_CommandCallback = nullptr;

// Очередь событий
static std::queue<std::string> s_EventQueue;
static std::mutex s_QueueMutex;

bool Bridge::Initialize() {
    // Создаем именованный pipe
    s_Pipe = CreateNamedPipeA(
        "\\\\.\\pipe\\ScumOxygen",
        PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT,
        1, // Max instances
        65536, // Out buffer
        65536, // In buffer
        0, // Timeout
        nullptr // Security
    );
    
    if (s_Pipe == INVALID_HANDLE_VALUE) {
        printf("[ScumOxygen] Failed to create named pipe\n");
        return false;
    }
    
    s_Running = true;
    
    // Запускаем поток чтения команд от .NET
    s_ReaderThread = std::thread([]() {
        char buffer[4096];
        DWORD bytesRead;
        
        // Ждем подключения .NET клиента
        printf("[ScumOxygen] Waiting for .NET bridge connection...\n");
        if (ConnectNamedPipe(s_Pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
            printf("[ScumOxygen] .NET bridge connected\n");
            
            while (s_Running) {
                if (ReadFile(s_Pipe, buffer, sizeof(buffer) - 1, &bytesRead, nullptr)) {
                    buffer[bytesRead] = '\0';
                    
                    // Парсим команду: TYPE|DATA
                    char* separator = strchr(buffer, '|');
                    if (separator) {
                        *separator = '\0';
                        const char* type = buffer;
                        const char* data = separator + 1;
                        
                        if (s_CommandCallback) {
                            s_CommandCallback(type, data);
                        }
                    }
                } else {
                    // Клиент отключился
                    if (GetLastError() == ERROR_BROKEN_PIPE) {
                        printf("[ScumOxygen] .NET bridge disconnected\n");
                        DisconnectNamedPipe(s_Pipe);
                        
                        // Ждем переподключения
                        if (ConnectNamedPipe(s_Pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
                            printf("[ScumOxygen] .NET bridge reconnected\n");
                        }
                    }
                }
            }
        }
    });
    
    return true;
}

void Bridge::Shutdown() {
    s_Running = false;
    
    if (s_Pipe != INVALID_HANDLE_VALUE) {
        CloseHandle(s_Pipe);
        s_Pipe = INVALID_HANDLE_VALUE;
    }
    
    if (s_ReaderThread.joinable()) {
        s_ReaderThread.join();
    }
}

void Bridge::SendEvent(const char* eventType, const char* data) {
    if (s_Pipe == INVALID_HANDLE_VALUE) return;
    
    std::string message = std::string(eventType) + "|" + data;
    DWORD bytesWritten;
    
    WriteFile(s_Pipe, message.c_str(), (DWORD)message.length(), &bytesWritten, nullptr);
}

static std::string EscapeJson(const char* value) {
    std::string out;
    const std::string input = value ? value : "";
    out.reserve(input.size() + 16);

    for (const char ch : input) {
        switch (ch) {
        case '\\': out += "\\\\"; break;
        case '"': out += "\\\""; break;
        case '\r': out += "\\r"; break;
        case '\n': out += "\\n"; break;
        case '\t': out += "\\t"; break;
        default:
            if (static_cast<unsigned char>(ch) < 0x20) {
                char tmp[7];
                snprintf(tmp, sizeof(tmp), "\\u%04x", static_cast<unsigned char>(ch));
                out += tmp;
            } else {
                out += ch;
            }
            break;
        }
    }

    return out;
}

void Bridge::SendPlayerJoin(int playerId, const char* playerName, const char* steamId) {
    const auto safeName = EscapeJson(playerName);
    const auto safeSteamId = EscapeJson(steamId);
    char buffer[1400];
    snprintf(
        buffer,
        sizeof(buffer),
        "{\"playerId\":%d,\"name\":\"%s\",\"steamId\":\"%s\"}",
        playerId,
        safeName.c_str(),
        safeSteamId.c_str());
    SendEvent("PLAYER_JOIN", buffer);
}

void Bridge::SendPlayerLeave(int playerId, const char* playerName, const char* steamId) {
    const auto safeName = EscapeJson(playerName);
    const auto safeSteamId = EscapeJson(steamId);
    char buffer[1400];
    snprintf(
        buffer,
        sizeof(buffer),
        "{\"playerId\":%d,\"name\":\"%s\",\"steamId\":\"%s\"}",
        playerId,
        safeName.c_str(),
        safeSteamId.c_str());
    SendEvent("PLAYER_LEAVE", buffer);
}

void Bridge::SendChatMessage(int playerId, const char* playerName, const char* message, int chatType, const char* steamId) {
    const auto safeName = EscapeJson(playerName);
    const auto safeMessage = EscapeJson(message);
    const auto safeSteamId = EscapeJson(steamId);
    char buffer[3072];
    snprintf(
        buffer,
        sizeof(buffer),
        "{\"playerId\":%d,\"name\":\"%s\",\"steamId\":\"%s\",\"message\":\"%s\",\"chatType\":%d}",
        playerId,
        safeName.c_str(),
        safeSteamId.c_str(),
        safeMessage.c_str(),
        chatType);
    SendEvent("CHAT_MESSAGE", buffer);
}

void Bridge::SetCommandCallback(CommandCallback callback) {
    s_CommandCallback = callback;
}

} // namespace ScumOxygen
