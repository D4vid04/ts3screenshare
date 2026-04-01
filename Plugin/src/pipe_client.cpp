#include "pipe_client.h"
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>

// ── Helpers ───────────────────────────────────────────────────────────────────

static std::wstring Utf8ToWide(const std::string& s)
{
    if (s.empty()) return {};
    int len = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    std::wstring result(len - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, result.data(), len);
    return result;
}

// ── PipeClient implementation ─────────────────────────────────────────────────

bool PipeClient::Send(const std::string& command)
{
    HANDLE pipe = CreateFileA(
        PIPE_NAME,
        GENERIC_WRITE,
        0, nullptr,
        OPEN_EXISTING,
        0, nullptr);

    if (pipe == INVALID_HANDLE_VALUE) return false;

    std::string msg = command + "\n";
    DWORD written = 0;
    bool ok = WriteFile(pipe, msg.c_str(), static_cast<DWORD>(msg.size()), &written, nullptr);
    CloseHandle(pipe);
    return ok;
}

bool PipeClient::IsAppRunning()
{
    HANDLE pipe = CreateFileA(
        PIPE_NAME,
        GENERIC_WRITE,
        0, nullptr,
        OPEN_EXISTING,
        0, nullptr);

    if (pipe != INVALID_HANDLE_VALUE)
    {
        CloseHandle(pipe);
        return true;
    }
    return false;
}

std::wstring PipeClient::FindAppExe()
{
    // 1. Same directory as the plugin DLL (users sometimes put app next to TS3)
    wchar_t dllPath[MAX_PATH] = {};
    HMODULE hMod = nullptr;
    GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&PipeClient::FindAppExe),
        &hMod);
    GetModuleFileNameW(hMod, dllPath, MAX_PATH);

    std::wstring pluginsDir(dllPath);
    auto slash = pluginsDir.rfind(L'\\');
    if (slash != std::wstring::npos) pluginsDir = pluginsDir.substr(0, slash); // → …\plugins

    auto parentSlash = pluginsDir.rfind(L'\\');
    std::wstring ts3Dir = (parentSlash != std::wstring::npos)
        ? pluginsDir.substr(0, parentSlash) // → …\TS3Client
        : pluginsDir;

    // 2. %LOCALAPPDATA%\Programs\TS3ScreenShare  (default Inno Setup install location)
    wchar_t localAppData[MAX_PATH] = {};
    SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localAppData);

    const std::wstring candidates[] = {
        ts3Dir          + L"\\TS3ScreenShare\\TS3ScreenShare.exe",
        std::wstring(localAppData) + L"\\Programs\\TS3ScreenShare\\TS3ScreenShare.exe",
        std::wstring(localAppData) + L"\\TS3ScreenShare\\TS3ScreenShare.exe",
    };

    for (const auto& path : candidates)
        if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
            return path;

    return {};
}

void PipeClient::LaunchApp(const std::string& relayUrl)
{
    std::wstring exePath = FindAppExe();
    if (exePath.empty()) return;

    std::wstring args;
    if (!relayUrl.empty())
        args = L"--relay " + Utf8ToWide(relayUrl);

    SHELLEXECUTEINFOW sei = {};
    sei.cbSize      = sizeof(sei);
    sei.fMask       = SEE_MASK_NOCLOSEPROCESS;
    sei.lpVerb      = L"open";
    sei.lpFile      = exePath.c_str();
    sei.lpParameters = args.empty() ? nullptr : args.c_str();
    sei.nShow       = SW_SHOW;
    ShellExecuteExW(&sei);
    if (sei.hProcess) CloseHandle(sei.hProcess);
}
