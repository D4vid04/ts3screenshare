#include "pipe_client.h"
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <fstream>
#include <string>

static void PipeLog(const std::string& msg)
{
    char path[MAX_PATH];
    GetTempPathA(MAX_PATH, path);
    std::string logPath = std::string(path) + "TS3ScreenSharePlugin.log";
    std::ofstream f(logPath, std::ios::app);
    f << "[pipe] " << msg << "\n";
}

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
    // Retry briefly in case the server is restarting after a previous connection
    for (int attempt = 0; attempt < 3; ++attempt)
    {
        HANDLE pipe = CreateFileA(
            PIPE_NAME,
            GENERIC_WRITE,
            0, nullptr,
            OPEN_EXISTING,
            0, nullptr);

        if (pipe != INVALID_HANDLE_VALUE)
        {
            std::string msg = command + "\n";
            DWORD written = 0;
            bool ok = WriteFile(pipe, msg.c_str(), static_cast<DWORD>(msg.size()), &written, nullptr);
            CloseHandle(pipe);
            return ok;
        }

        if (GetLastError() != ERROR_PIPE_BUSY) break;
        WaitNamedPipeA(PIPE_NAME, 500);
    }
    return false;
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
    // 1. Registry — written by the installer (most reliable)
    const wchar_t* regKeys[] = {
        L"SOFTWARE\\TS3ScreenShare",
        L"SOFTWARE\\WOW6432Node\\TS3ScreenShare",
    };
    for (const wchar_t* key : regKeys)
    {
        HKEY hk = nullptr;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, key, 0, KEY_READ, &hk) == ERROR_SUCCESS ||
            RegOpenKeyExW(HKEY_LOCAL_MACHINE, key, 0, KEY_READ, &hk) == ERROR_SUCCESS)
        {
            wchar_t installDir[MAX_PATH] = {};
            DWORD sz = sizeof(installDir);
            DWORD type = 0;
            if (RegQueryValueExW(hk, L"InstallDir", nullptr, &type,
                                 reinterpret_cast<LPBYTE>(installDir), &sz) == ERROR_SUCCESS
                && (type == REG_SZ || type == REG_EXPAND_SZ))
            {
                RegCloseKey(hk);
                std::wstring exePath = std::wstring(installDir) + L"\\TS3ScreenShare.exe";
                if (GetFileAttributesW(exePath.c_str()) != INVALID_FILE_ATTRIBUTES)
                    return exePath;
            }
            else RegCloseKey(hk);
        }
    }

    // 2. Same directory as the plugin DLL (users sometimes put app next to TS3)
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

    // 3. %LOCALAPPDATA%\Programs\TS3ScreenShare  (default Inno Setup install location)
    wchar_t localAppData[MAX_PATH] = {};
    SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localAppData);

    std::wstring pluginParent2 = pluginsDir.substr(0, pluginsDir.rfind(L'\\') != std::wstring::npos ? pluginsDir.rfind(L'\\') : pluginsDir.size());

    const std::wstring candidates[] = {
        ts3Dir          + L"\\TS3ScreenShare\\TS3ScreenShare.exe",
        std::wstring(localAppData) + L"\\Programs\\TS3ScreenShare\\TS3ScreenShare.exe",
        std::wstring(localAppData) + L"\\TS3ScreenShare\\TS3ScreenShare.exe",
        pluginParent2   + L"\\TS3ScreenShare\\publish\\TS3ScreenShare.exe",
        pluginParent2   + L"\\publish\\TS3ScreenShare.exe",
    };

    for (const auto& path : candidates)
        if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
            return path;

    return {};
}

void PipeClient::LaunchApp(const std::string& relayUrl)
{
    std::wstring exePath = FindAppExe();
    if (exePath.empty()) { PipeLog("FindAppExe returned empty - exe not found"); return; }

    // Convert exePath to UTF-8 for logging
    int len = WideCharToMultiByte(CP_UTF8, 0, exePath.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string exePathStr(len - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, exePath.c_str(), -1, exePathStr.data(), len, nullptr, nullptr);
    PipeLog("FindAppExe found: " + exePathStr);

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
    BOOL ok = ShellExecuteExW(&sei);
    PipeLog(std::string("ShellExecuteExW result: ") + (ok ? "OK" : "FAIL") +
            " lastErr=" + std::to_string(GetLastError()));
    if (sei.hProcess) CloseHandle(sei.hProcess);
}
