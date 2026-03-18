#define WINVERAPI
#define VERPRODAPI
#include <windows.h>
#include <shlwapi.h>
#include <string>
#include <vector>
#include <mutex>
#include <fstream>
#include <algorithm>
#include <cstdio>
#include <cstring>

#include <coreclr_delegates.h>
#include <hostfxr.h>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "version.lib")

static HMODULE g_realVersion = nullptr;
static std::once_flag g_realOnce;
static std::once_flag g_hostOnce;
static HMODULE g_nativeModule = nullptr;

static bool DirectoryExists(const std::wstring& path);

static std::wstring GetRuntimeRoot(const std::wstring& baseDir)
{
    const auto preferred = baseDir + L"\\NeDjin";
    if (DirectoryExists(preferred))
        return preferred;

    const auto legacy = baseDir + L"\\ScumOxygen";
    if (DirectoryExists(legacy))
        return legacy;

    return preferred;
}

static void LogLine(const std::wstring& line)
{
    wchar_t path[MAX_PATH] = {0};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    std::wstring baseDir = path;
    const auto runtimeRoot = GetRuntimeRoot(baseDir);
    const auto oxygenDir = runtimeRoot + L"\\oxygen";
    std::wstring logDir = oxygenDir + L"\\logs";
    CreateDirectoryW(runtimeRoot.c_str(), nullptr);
    CreateDirectoryW(oxygenDir.c_str(), nullptr);
    CreateDirectoryW(logDir.c_str(), nullptr);
    std::wstring logPath = logDir + L"\\proxy.log";

    FILE* f = nullptr;
    _wfopen_s(&f, logPath.c_str(), L"a+, ccs=UTF-8");
    if (f)
    {
        fwprintf(f, L"%s\n", line.c_str());
        fclose(f);
    }
}

static void LogLineA(const std::string& line)
{
    wchar_t path[MAX_PATH] = {0};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    std::wstring baseDir = path;
    const auto runtimeRoot = GetRuntimeRoot(baseDir);
    const auto oxygenDir = runtimeRoot + L"\\oxygen";
    std::wstring logDir = oxygenDir + L"\\logs";
    CreateDirectoryW(runtimeRoot.c_str(), nullptr);
    CreateDirectoryW(oxygenDir.c_str(), nullptr);
    CreateDirectoryW(logDir.c_str(), nullptr);
    std::wstring logPath = logDir + L"\\proxy.log";

    FILE* f = nullptr;
    _wfopen_s(&f, logPath.c_str(), L"a+");
    if (f)
    {
        fwrite(line.c_str(), 1, line.size(), f);
        fwrite("\n", 1, 1, f);
        fclose(f);
    }
}

static void LoadRealVersion()
{
    std::call_once(g_realOnce, []() {
        wchar_t modPath[MAX_PATH] = {0};
        GetModuleFileNameW(nullptr, modPath, MAX_PATH);
        PathRemoveFileSpecW(modPath);
        std::wstring baseDir = modPath;
        std::wstring path = baseDir + L"\\version.real.dll";
        g_realVersion = LoadLibraryW(path.c_str());
        if (!g_realVersion)
        {
            // fallback to system copy if renamed file not found
            wchar_t sysdir[MAX_PATH] = {0};
            GetSystemDirectoryW(sysdir, MAX_PATH);
            std::wstring sysPath = sysdir;
            sysPath += L"\\version.dll";
            g_realVersion = LoadLibraryW(sysPath.c_str());
        }
    });
}

static std::wstring JoinPath(const std::wstring& left, const std::wstring& right)
{
    if (left.empty()) return right;
    if (left.back() == L'\\' || left.back() == L'/') return left + right;
    return left + L"\\" + right;
}

static bool DirectoryExists(const std::wstring& path)
{
    const DWORD attrs = GetFileAttributesW(path.c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY);
}

static bool FileExists(const std::wstring& path)
{
    const DWORD attrs = GetFileAttributesW(path.c_str());
    return attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY);
}

static std::wstring FindLatestSubdirectory(const std::wstring& root)
{
    WIN32_FIND_DATAW data{};
    const auto pattern = JoinPath(root, L"*");
    HANDLE handle = FindFirstFileW(pattern.c_str(), &data);
    if (handle == INVALID_HANDLE_VALUE)
        return L"";

    std::vector<std::wstring> dirs;
    do
    {
        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            continue;
        if (wcscmp(data.cFileName, L".") == 0 || wcscmp(data.cFileName, L"..") == 0)
            continue;
        dirs.emplace_back(data.cFileName);
    }
    while (FindNextFileW(handle, &data));

    FindClose(handle);
    if (dirs.empty())
        return L"";

    std::sort(dirs.begin(), dirs.end());
    return JoinPath(root, dirs.back());
}

// Helper: get module directory
static std::wstring GetModuleDir(HMODULE mod)
{
    wchar_t path[MAX_PATH] = {0};
    GetModuleFileNameW(mod, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    return std::wstring(path);
}

// Hostfxr function pointers
static hostfxr_initialize_for_runtime_config_fn init_fptr = nullptr;
static hostfxr_get_runtime_delegate_fn get_delegate_fptr = nullptr;
static hostfxr_close_fn close_fptr = nullptr;
static hostfxr_set_error_writer_fn set_error_writer_fptr = nullptr;

static std::string FormatRc(const int rc)
{
    char buffer[64] = {};
    sprintf_s(buffer, " rc=%d (0x%08X)", rc, static_cast<unsigned int>(rc));
    return std::string(buffer);
}

static void HOSTFXR_CALLTYPE HostfxrErrorWriter(const char_t* message)
{
    if (message == nullptr)
        return;

    LogLine(std::wstring(L"[hostfxr] ") + message);
}

static bool LoadHostfxr(const std::wstring& managedDir)
{
    const auto dotnetRoot = JoinPath(managedDir, L"dotnet");
    const auto fxrRoot = JoinPath(dotnetRoot, L"host\\fxr");
    const auto fxrDir = FindLatestSubdirectory(fxrRoot);
    if (fxrDir.empty())
    {
        LogLine(L"[proxy] dotnet hostfxr directory not found: " + fxrRoot);
        return false;
    }

    const auto hostfxrPath = JoinPath(fxrDir, L"hostfxr.dll");
    if (!FileExists(hostfxrPath))
    {
        LogLine(L"[proxy] hostfxr.dll missing: " + hostfxrPath);
        return false;
    }

    SetEnvironmentVariableW(L"DOTNET_ROOT", dotnetRoot.c_str());
    SetEnvironmentVariableW(L"DOTNET_ROOT_X64", dotnetRoot.c_str());
    SetEnvironmentVariableW(L"DOTNET_MULTILEVEL_LOOKUP", L"0");

    LogLine(L"[proxy] DOTNET_ROOT=" + dotnetRoot);
    LogLine(L"[proxy] hostfxr=" + hostfxrPath);

    HMODULE hfxr = LoadLibraryExW(
        hostfxrPath.c_str(),
        nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!hfxr)
    {
        LogLine(L"[proxy] LoadLibraryEx(hostfxr) failed");
        return false;
    }

    init_fptr = (hostfxr_initialize_for_runtime_config_fn)GetProcAddress(hfxr, "hostfxr_initialize_for_runtime_config");
    get_delegate_fptr = (hostfxr_get_runtime_delegate_fn)GetProcAddress(hfxr, "hostfxr_get_runtime_delegate");
    close_fptr = (hostfxr_close_fn)GetProcAddress(hfxr, "hostfxr_close");
    set_error_writer_fptr = (hostfxr_set_error_writer_fn)GetProcAddress(hfxr, "hostfxr_set_error_writer");

    if (!init_fptr || !get_delegate_fptr || !close_fptr)
    {
        LogLine(L"[proxy] GetProcAddress hostfxr exports failed");
    }
    return (init_fptr && get_delegate_fptr && close_fptr);
}

static void LoadNativeBridge(const std::wstring& managedDir)
{
    if (g_nativeModule)
        return;

    const auto nativePath = JoinPath(managedDir, L"ScumOxygen.Native.dll");
    if (!FileExists(nativePath))
    {
        LogLine(L"[proxy] native bridge missing: " + nativePath);
        return;
    }

    g_nativeModule = LoadLibraryExW(
        nativePath.c_str(),
        nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);

    if (!g_nativeModule)
    {
        LogLine(L"[proxy] failed to load native bridge: " + nativePath);
        return;
    }

    LogLine(L"[proxy] native bridge loaded: " + nativePath);
}

static void StartManaged(HMODULE mod)
{
    std::call_once(g_hostOnce, [mod]() {
        LogLineA("[proxy] StartManaged");
        std::wstring baseDir = GetModuleDir(mod);
        std::wstring managedDir = GetRuntimeRoot(baseDir);
        LoadNativeBridge(managedDir);
        if (!LoadHostfxr(managedDir)) return;

        std::wstring runtimeConfig = managedDir + L"\\ScumOxygen.Bootstrap.runtimeconfig.json";
        std::wstring assemblyPath = managedDir + L"\\ScumOxygen.Bootstrap.dll";
        LogLine(L"[proxy] runtimeconfig: " + runtimeConfig);
        LogLine(L"[proxy] assembly: " + assemblyPath);

        if (set_error_writer_fptr)
        {
            set_error_writer_fptr(HostfxrErrorWriter);
        }

        LogLineA("[proxy] hostfxr_initialize_for_runtime_config...");
        hostfxr_handle cxt = nullptr;
        int rc = init_fptr(runtimeConfig.c_str(), nullptr, &cxt);
        if (rc != 0 || cxt == nullptr)
        {
            LogLineA(std::string("[proxy] hostfxr_initialize failed") + FormatRc(rc));
            return;
        }

        void* load_assembly_and_get_function_pointer = nullptr;
        rc = get_delegate_fptr(
            cxt,
            hdt_load_assembly_and_get_function_pointer,
            &load_assembly_and_get_function_pointer);

        if (rc != 0 || load_assembly_and_get_function_pointer == nullptr)
        {
            LogLineA(std::string("[proxy] get_delegate failed") + FormatRc(rc));
            close_fptr(cxt);
            return;
        }

        auto load_assembly = (load_assembly_and_get_function_pointer_fn)load_assembly_and_get_function_pointer;

        const char_t* typeName = L"ScumOxygen.Bootstrap.Plugin, ScumOxygen.Bootstrap";
        const char_t* methodName = L"InitializeNative";
        const char_t* delegateType = UNMANAGEDCALLERSONLY_METHOD;
        const char* launchArgs = "scum-server";

        void* managedFunc = nullptr;
        LogLineA("[proxy] load_assembly_and_get_function_pointer...");
        rc = load_assembly(
            assemblyPath.c_str(),
            typeName,
            methodName,
            delegateType,
            nullptr,
            &managedFunc);

        if (rc == 0 && managedFunc != nullptr)
        {
            LogLineA("[proxy] invoking managed entry...");
            auto init = (component_entry_point_fn)(managedFunc);
            const int initRc = init((void*)launchArgs, static_cast<int32_t>(strlen(launchArgs)));
            LogLineA(std::string("[proxy] managed entry returned") + FormatRc(initRc));
        }
        else
        {
            LogLineA(std::string("[proxy] load_assembly_and_get_function_pointer failed") + FormatRc(rc));
        }

        close_fptr(cxt);
    });
}

// Export helpers
#define DEFINE_EXPORT(ret, name, args, callargs) \
    extern "C" ret WINAPI name args { \
        LoadRealVersion(); \
        if (!g_realVersion) return (ret)0; \
        using Fn = ret (WINAPI*) args; \
        static Fn fn = (Fn)GetProcAddress(g_realVersion, #name); \
        if (!fn) return (ret)0; \
        return fn callargs; \
    }

DEFINE_EXPORT(BOOL, GetFileVersionInfoA, (LPCSTR a, DWORD b, DWORD c, LPVOID d), (a,b,c,d))
DEFINE_EXPORT(BOOL, GetFileVersionInfoW, (LPCWSTR a, DWORD b, DWORD c, LPVOID d), (a,b,c,d))
DEFINE_EXPORT(BOOL, GetFileVersionInfoExA, (DWORD a, LPCSTR b, DWORD c, DWORD d, LPVOID e), (a,b,c,d,e))
DEFINE_EXPORT(BOOL, GetFileVersionInfoExW, (DWORD a, LPCWSTR b, DWORD c, DWORD d, LPVOID e), (a,b,c,d,e))
DEFINE_EXPORT(DWORD, GetFileVersionInfoSizeA, (LPCSTR a, LPDWORD b), (a,b))
DEFINE_EXPORT(DWORD, GetFileVersionInfoSizeW, (LPCWSTR a, LPDWORD b), (a,b))
DEFINE_EXPORT(DWORD, GetFileVersionInfoSizeExA, (DWORD a, LPCSTR b, LPDWORD c), (a,b,c))
DEFINE_EXPORT(DWORD, GetFileVersionInfoSizeExW, (DWORD a, LPCWSTR b, LPDWORD c), (a,b,c))
DEFINE_EXPORT(DWORD, VerFindFileA, (DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPSTR e, PUINT f, LPSTR g, PUINT h), (a,b,c,d,e,f,g,h))
DEFINE_EXPORT(DWORD, VerFindFileW, (DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPWSTR e, PUINT f, LPWSTR g, PUINT h), (a,b,c,d,e,f,g,h))
DEFINE_EXPORT(DWORD, VerInstallFileA, (DWORD a, LPCSTR b, LPCSTR c, LPCSTR d, LPCSTR e, LPCSTR f, LPSTR g, PUINT h), (a,b,c,d,e,f,g,h))
DEFINE_EXPORT(DWORD, VerInstallFileW, (DWORD a, LPCWSTR b, LPCWSTR c, LPCWSTR d, LPCWSTR e, LPCWSTR f, LPWSTR g, PUINT h), (a,b,c,d,e,f,g,h))
DEFINE_EXPORT(DWORD, VerLanguageNameA, (DWORD a, LPSTR b, DWORD c), (a,b,c))
DEFINE_EXPORT(DWORD, VerLanguageNameW, (DWORD a, LPWSTR b, DWORD c), (a,b,c))
DEFINE_EXPORT(BOOL, VerQueryValueA, (LPCVOID a, LPCSTR b, LPVOID* c, PUINT d), (a,b,c,d))
DEFINE_EXPORT(BOOL, VerQueryValueW, (LPCVOID a, LPCWSTR b, LPVOID* c, PUINT d), (a,b,c,d))

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        LogLineA("[proxy] DllMain attach");
        CreateThread(nullptr, 0, [](LPVOID param) -> DWORD {
            StartManaged(reinterpret_cast<HMODULE>(param));
            return 0;
        }, hModule, 0, nullptr);
    }
    return TRUE;
}
