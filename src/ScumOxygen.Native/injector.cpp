#include <windows.h>
#include <stdio.h>
#include <string>
#include <tlhelp32.h>

// Простой инжектор DLL для ScumOxygen
// Запуск: ScumOxygen.Injector.exe <PID или ИмяПроцесса> <путь к DLL>

DWORD GetProcessIdByName(const char* processName) {
    DWORD pid = 0;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    
    if (snapshot != INVALID_HANDLE_VALUE) {
        PROCESSENTRY32 pe;
        pe.dwSize = sizeof(PROCESSENTRY32);
        
        if (Process32First(snapshot, &pe)) {
            do {
                if (_stricmp(pe.szExeFile, processName) == 0) {
                    pid = pe.th32ProcessID;
                    break;
                }
            } while (Process32Next(snapshot, &pe));
        }
        
        CloseHandle(snapshot);
    }
    
    return pid;
}

bool InjectDLL(DWORD processId, const char* dllPath) {
    // Открываем процесс
    HANDLE hProcess = OpenProcess(PROCESS_ALL_ACCESS, FALSE, processId);
    if (!hProcess) {
        printf("[-] Failed to open process (PID: %lu). Error: %lu\n", processId, GetLastError());
        return false;
    }
    
    // Выделяем память для пути к DLL
    size_t pathLen = strlen(dllPath) + 1;
    LPVOID remoteMemory = VirtualAllocEx(hProcess, nullptr, pathLen, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remoteMemory) {
        printf("[-] Failed to allocate memory. Error: %lu\n", GetLastError());
        CloseHandle(hProcess);
        return false;
    }
    
    // Пишем путь к DLL в процесс
    if (!WriteProcessMemory(hProcess, remoteMemory, dllPath, pathLen, nullptr)) {
        printf("[-] Failed to write memory. Error: %lu\n", GetLastError());
        VirtualFreeEx(hProcess, remoteMemory, 0, MEM_RELEASE);
        CloseHandle(hProcess);
        return false;
    }
    
    // Получаем адрес LoadLibraryA
    HMODULE hKernel32 = GetModuleHandleA("kernel32.dll");
    LPTHREAD_START_ROUTINE loadLibraryAddr = (LPTHREAD_START_ROUTINE)GetProcAddress(hKernel32, "LoadLibraryA");
    
    // Создаем удаленный поток для загрузки DLL
    HANDLE hThread = CreateRemoteThread(hProcess, nullptr, 0, loadLibraryAddr, remoteMemory, 0, nullptr);
    if (!hThread) {
        printf("[-] Failed to create remote thread. Error: %lu\n", GetLastError());
        VirtualFreeEx(hProcess, remoteMemory, 0, MEM_RELEASE);
        CloseHandle(hProcess);
        return false;
    }
    
    // Ждем завершения
    WaitForSingleObject(hThread, INFINITE);
    
    // Получаем результат
    DWORD exitCode = 0;
    GetExitCodeThread(hThread, &exitCode);
    
    // Очистка
    VirtualFreeEx(hProcess, remoteMemory, 0, MEM_RELEASE);
    CloseHandle(hThread);
    CloseHandle(hProcess);
    
    if (exitCode == 0) {
        printf("[-] LoadLibrary failed in target process\n");
        return false;
    }
    
    printf("[+] DLL injected successfully at address: 0x%p\n", (void*)exitCode);
    return true;
}

int main(int argc, char* argv[]) {
    printf("ScumOxygen DLL Injector\n");
    printf("========================\n\n");
    
    if (argc != 3) {
        printf("Usage: %s <PID or ProcessName> <DLL Path>\n", argv[0]);
        printf("Example: %s SCUMServer.exe ScumOxygen.Native.dll\n", argv[0]);
        printf("Example: %s 1234 C:\\\\Path\\\\To\\\\ScumOxygen.Native.dll\n", argv[0]);
        return 1;
    }
    
    DWORD pid = 0;
    
    // Проверяем, передан PID или имя процесса
    if (atoi(argv[1]) != 0) {
        pid = atoi(argv[1]);
        printf("[*] Using PID: %lu\n", pid);
    } else {
        printf("[*] Looking for process: %s\n", argv[1]);
        pid = GetProcessIdByName(argv[1]);
        if (pid == 0) {
            printf("[-] Process not found: %s\n", argv[1]);
            return 1;
        }
        printf("[+] Found process with PID: %lu\n", pid);
    }
    
    // Проверяем существование DLL
    DWORD attribs = GetFileAttributesA(argv[2]);
    if (attribs == INVALID_FILE_ATTRIBUTES || (attribs & FILE_ATTRIBUTE_DIRECTORY)) {
        printf("[-] DLL not found: %s\n", argv[2]);
        return 1;
    }
    
    // Получаем полный путь
    char fullPath[MAX_PATH];
    if (!GetFullPathNameA(argv[2], MAX_PATH, fullPath, nullptr)) {
        printf("[-] Failed to resolve full path\n");
        return 1;
    }
    
    printf("[*] Target DLL: %s\n", fullPath);
    printf("[*] Injecting...\n\n");
    
    if (InjectDLL(pid, fullPath)) {
        printf("\n[+] Success!\n");
        return 0;
    } else {
        printf("\n[-] Injection failed!\n");
        return 1;
    }
}
