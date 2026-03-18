#include "../include/sdk.h"
#include <windows.h>
#include <string>
#include <queue>
#include <mutex>
#include <thread>
#include <atomic>
#include <condition_variable>

namespace ScumOxygen {

// Именованный pipe для связи с .NET
static HANDLE s_Pipe = INVALID_HANDLE_VALUE;
static std::thread s_ReaderThread;
static std::thread s_WriterThread;
static bool s_Running = false;
static CommandCallback s_CommandCallback = nullptr;
static std::atomic_bool s_IsConnected = false;

// Очередь событий
static std::queue<std::string> s_EventQueue;
static std::mutex s_QueueMutex;
static std::condition_variable s_QueueCv;

static void QueueEvent(std::string message)
{
    if (message.empty())
        return;

    {
        std::lock_guard<std::mutex> lock(s_QueueMutex);
        s_EventQueue.push(std::move(message));
    }

    s_QueueCv.notify_one();
}

static void FlushQueuedEvents()
{
    while (s_Running)
    {
        std::unique_lock<std::mutex> lock(s_QueueMutex);
        s_QueueCv.wait(lock, []()
        {
            return !s_Running || !s_EventQueue.empty();
        });

        if (!s_Running)
            return;

        if (!s_IsConnected.load() || s_Pipe == INVALID_HANDLE_VALUE)
        {
            lock.unlock();
            Sleep(25);
            continue;
        }

        auto message = std::move(s_EventQueue.front());
        s_EventQueue.pop();
        lock.unlock();

        DWORD bytesWritten = 0;
        if (!WriteFile(s_Pipe, message.c_str(), static_cast<DWORD>(message.length()), &bytesWritten, nullptr))
        {
            const auto error = GetLastError();
            if (error == ERROR_BROKEN_PIPE || error == ERROR_NO_DATA)
            {
                s_IsConnected = false;
            }
        }
    }
}

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
    s_IsConnected = false;

    s_WriterThread = std::thread([]()
    {
        FlushQueuedEvents();
    });
    
    // Запускаем поток чтения команд от .NET
    s_ReaderThread = std::thread([]() {
        char buffer[4096];
        DWORD bytesRead;
        
        // Ждем подключения .NET клиента
        printf("[ScumOxygen] Waiting for .NET bridge connection...\n");
        if (ConnectNamedPipe(s_Pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
            printf("[ScumOxygen] .NET bridge connected\n");
            s_IsConnected = true;
            s_QueueCv.notify_all();
            
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
                        s_IsConnected = false;
                        DisconnectNamedPipe(s_Pipe);
                        
                        // Ждем переподключения
                        if (ConnectNamedPipe(s_Pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
                            printf("[ScumOxygen] .NET bridge reconnected\n");
                            s_IsConnected = true;
                            s_QueueCv.notify_all();
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
    s_IsConnected = false;
    s_QueueCv.notify_all();
    
    if (s_Pipe != INVALID_HANDLE_VALUE) {
        CloseHandle(s_Pipe);
        s_Pipe = INVALID_HANDLE_VALUE;
    }
    
    if (s_ReaderThread.joinable()) {
        s_ReaderThread.join();
    }

    if (s_WriterThread.joinable()) {
        s_WriterThread.join();
    }
}

void Bridge::SendEvent(const char* eventType, const char* data) {
    if (s_Pipe == INVALID_HANDLE_VALUE) return;

    QueueEvent(std::string(eventType ? eventType : "") + "|" + (data ? data : ""));
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
