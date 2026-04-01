#pragma warning(disable: 4100) // unreferenced formal parameter
#pragma warning(disable: 4996) // strncpy

#include <windows.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <assert.h>
#include <fstream>

#include "ts3_functions.h"
#include "plugin_definitions.h"
#include "teamspeak/public_definitions.h"
#include "teamspeak/public_errors.h"

#include "pipe_client.h"

#define PLUGIN_API_VERSION 26

static struct TS3Functions ts3Functions;

static void PluginLog(const std::string& msg)
{
    char path[MAX_PATH];
    GetTempPathA(MAX_PATH, path);
    std::string logPath = std::string(path) + "TS3ScreenSharePlugin.log";
    std::ofstream f(logPath, std::ios::app);
    f << msg << "\n";
}

#include <windows.h>

// ── Menu helper ───────────────────────────────────────────────────────────────

static struct PluginMenuItem* createMenuItem(enum PluginMenuType type, int id,
                                             const char* text, const char* icon)
{
    struct PluginMenuItem* item =
        (struct PluginMenuItem*)malloc(sizeof(struct PluginMenuItem));
    item->type = type;
    item->id   = id;
    strncpy(item->text, text, PLUGIN_MENU_BUFSZ);
    strncpy(item->icon, icon, PLUGIN_MENU_BUFSZ);
    return item;
}

#define BEGIN_CREATE_MENUS(x)                                       \
    const size_t _menu_sz = (x) + 1;                               \
    size_t _menu_n = 0;                                             \
    *menuItems = (struct PluginMenuItem**)malloc(                   \
        sizeof(struct PluginMenuItem*) * _menu_sz);

#define CREATE_MENU_ITEM(a, b, c, d) \
    (*menuItems)[_menu_n++] = createMenuItem(a, b, c, d);

#define END_CREATE_MENUS        \
    (*menuItems)[_menu_n] = NULL;

// ── Menu IDs ─────────────────────────────────────────────────────────────────
#define MENU_ID_WATCH_STREAM        1
#define MENU_ID_START_STREAM        2
#define MENU_ID_STOP_STREAM         3
#define MENU_ID_SELF_START_STREAM   4
#define MENU_ID_SELF_STOP_STREAM    5

// ── Stream state (updated by app via pipe) ────────────────────────────────────
static bool g_streaming = false;

// ── Notification thread ───────────────────────────────────────────────────────
static HANDLE g_notifyEvent  = NULL;
static HANDLE g_stopEvent    = NULL;
static HANDLE g_notifyThread = NULL;
static uint64 g_activeServer = 0;

static std::string WideToUtf8(const std::wstring& w)
{
    int len = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string result(len - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, result.data(), len, nullptr, nullptr);
    return result;
}

static std::string FindNotificationSound()
{
    // 1. Custom sound next to plugin DLL
    wchar_t dllPath[MAX_PATH] = {};
    HMODULE hMod = nullptr;
    GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&FindNotificationSound), &hMod);
    GetModuleFileNameW(hMod, dllPath, MAX_PATH);
    std::wstring pluginDir(dllPath);
    auto s = pluginDir.rfind(L'\\');
    if (s != std::wstring::npos) pluginDir = pluginDir.substr(0, s);

    // 2. TS3 built-in fallback sounds
    wchar_t exePath[MAX_PATH] = {};
    GetModuleFileNameW(NULL, exePath, MAX_PATH);
    std::wstring ts3Dir(exePath);
    auto s2 = ts3Dir.rfind(L'\\');
    if (s2 != std::wstring::npos) ts3Dir = ts3Dir.substr(0, s2);

    const std::wstring candidates[] = {
        pluginDir + L"\\TS3ScreenShareNotification.wav",
        ts3Dir    + L"\\sound\\default\\message_received.wav",
        ts3Dir    + L"\\sound\\default\\up.wav",
    };
    for (const auto& p : candidates)
        if (GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES)
            return WideToUtf8(p);
    return {};
}

static DWORD WINAPI NotifyThreadProc(LPVOID)
{
    g_notifyEvent = CreateEventA(NULL, FALSE, FALSE, "Local\\TS3ScreenShare_Notify");
    if (!g_notifyEvent) return 0;

    std::string soundPath = FindNotificationSound();
    PluginLog("Notification sound: " + (soundPath.empty() ? "(not found)" : soundPath));

    HANDLE handles[2] = { g_notifyEvent, g_stopEvent };
    while (true)
    {
        DWORD result = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
        if (result != WAIT_OBJECT_0) break; // stop event or error
        if (!soundPath.empty() && g_activeServer != 0)
            ts3Functions.playWaveFile(g_activeServer, soundPath.c_str());
    }
    CloseHandle(g_notifyEvent);
    g_notifyEvent = NULL;
    return 0;
}

// ── Relay URL detection ───────────────────────────────────────────────────────

static void CheckServerRelayUrl(uint64 scHandlerID)
{
    PluginLog("CheckServerRelayUrl called");

    char* welcomeMsg = nullptr;
    unsigned int err = ts3Functions.getServerVariableAsString(
        scHandlerID, VIRTUALSERVER_WELCOMEMESSAGE, &welcomeMsg);

    if (err != ERROR_ok || !welcomeMsg)
    {
        PluginLog("getServerVariableAsString failed, err=" + std::to_string(err)
                  + " msg=" + (welcomeMsg ? welcomeMsg : "null"));
        return;
    }

    std::string msg(welcomeMsg);
    ts3Functions.freeMemory(welcomeMsg);
    PluginLog("Welcome message: " + msg);

    const std::string marker = "[TS3SS]";
    auto pos = msg.find(marker);
    if (pos == std::string::npos)
    {
        PluginLog("Marker [TS3SS] not found in welcome message");
        return;
    }

    std::string rest = msg.substr(pos + marker.size());
    auto start = rest.find_first_not_of(" \t\r\n");
    if (start == std::string::npos) return;
    rest = rest.substr(start);
    auto end = rest.find_first_of(" \t\r\n");
    std::string relayUrl = (end != std::string::npos) ? rest.substr(0, end) : rest;
    if (relayUrl.empty()) return;

    PluginLog("Relay URL found: " + relayUrl);

    bool appRunning = PipeClient::IsAppRunning();
    PluginLog("App running: " + std::to_string(appRunning));

    if (appRunning)
    {
        bool sent = PipeClient::Send("RELAY:" + relayUrl);
        PluginLog("Pipe send result: " + std::to_string(sent));
    }
    else
    {
        PluginLog("Launching app with relay: " + relayUrl);
        PipeClient::LaunchApp(relayUrl);
    }
}

// ── Exported plugin API ───────────────────────────────────────────────────────

extern "C" {

__declspec(dllexport) const char* ts3plugin_name()       { return "TS3ScreenShare"; }
__declspec(dllexport) const char* ts3plugin_version()    { return "1.0.0"; }
__declspec(dllexport) int   ts3plugin_apiVersion()       { return PLUGIN_API_VERSION; }
__declspec(dllexport) const char* ts3plugin_author()     { return "D4vid04"; }
__declspec(dllexport) const char* ts3plugin_description() {
    return "Integrates TS3ScreenShare with TeamSpeak 3.\n\n"
           "- Auto-launches the app when joining a server that contains\n"
           "  [TS3SS] wss://your-relay:5000 in the welcome message\n"
           "- Right-click any user -> Watch TS3 Stream";
}

__declspec(dllexport) void ts3plugin_setFunctionPointers(const struct TS3Functions funcs) {
    ts3Functions = funcs;
}

__declspec(dllexport) int ts3plugin_init()
{
    g_stopEvent    = CreateEventA(NULL, TRUE, FALSE, NULL); // manual-reset, unnamed
    g_notifyThread = CreateThread(NULL, 0, NotifyThreadProc, NULL, 0, NULL);
    return 0;
}

__declspec(dllexport) void ts3plugin_shutdown()
{
    if (g_stopEvent)    { SetEvent(g_stopEvent); }
    if (g_notifyThread) { WaitForSingleObject(g_notifyThread, 2000); CloseHandle(g_notifyThread); g_notifyThread = NULL; }
    if (g_stopEvent)    { CloseHandle(g_stopEvent); g_stopEvent = NULL; }
}

__declspec(dllexport) void ts3plugin_freeMemory(void* data) { free(data); }

__declspec(dllexport) int ts3plugin_offersConfigure() {
    return PLUGIN_OFFERS_NO_CONFIGURE;
}

__declspec(dllexport) void ts3plugin_registerPluginID(const char* id) {}

// ── Context menus ─────────────────────────────────────────────────────────────

__declspec(dllexport) void ts3plugin_initMenus(
    struct PluginMenuItem*** menuItems, char** menuIcon)
{
    BEGIN_CREATE_MENUS(5);
    CREATE_MENU_ITEM(PLUGIN_MENU_TYPE_GLOBAL, MENU_ID_START_STREAM,      "Start TS3 Stream", "");
    CREATE_MENU_ITEM(PLUGIN_MENU_TYPE_GLOBAL, MENU_ID_STOP_STREAM,       "Stop TS3 Stream",  "");
    CREATE_MENU_ITEM(PLUGIN_MENU_TYPE_CLIENT, MENU_ID_WATCH_STREAM,      "Watch TS3 Stream", "");
    CREATE_MENU_ITEM(PLUGIN_MENU_TYPE_CLIENT, MENU_ID_SELF_START_STREAM, "Start TS3 Stream", "");
    CREATE_MENU_ITEM(PLUGIN_MENU_TYPE_CLIENT, MENU_ID_SELF_STOP_STREAM,  "Stop TS3 Stream",  "");
    END_CREATE_MENUS;

    *menuIcon = (char*)malloc(1);
    (*menuIcon)[0] = '\0';
}

__declspec(dllexport) void ts3plugin_onMenuItemEvent(
    uint64 scHandlerID, enum PluginMenuType type, int menuItemID, uint64 selectedItemID)
{
    if (type == PLUGIN_MENU_TYPE_GLOBAL)
    {
        if (menuItemID == MENU_ID_START_STREAM)
        {
            if (!g_streaming)
            {
                if (!PipeClient::Send("START_STREAM"))
                    PipeClient::LaunchApp();
                else
                    g_streaming = true;
            }
        }
        else if (menuItemID == MENU_ID_STOP_STREAM)
        {
            if (g_streaming && PipeClient::Send("STOP_STREAM"))
                g_streaming = false;
        }
        return;
    }

    if (type == PLUGIN_MENU_TYPE_CLIENT)
    {
        anyID myClientID = 0;
        ts3Functions.getClientID(scHandlerID, &myClientID);
        bool isSelf = ((anyID)selectedItemID == myClientID);

        if (menuItemID == MENU_ID_SELF_START_STREAM && isSelf)
        {
            if (!g_streaming)
            {
                if (!PipeClient::Send("START_STREAM"))
                    PipeClient::LaunchApp();
                else
                    g_streaming = true;
            }
            return;
        }

        if (menuItemID == MENU_ID_SELF_STOP_STREAM && isSelf)
        {
            if (g_streaming && PipeClient::Send("STOP_STREAM"))
                g_streaming = false;
            return;
        }

        if (menuItemID == MENU_ID_WATCH_STREAM && !isSelf)
        {
            char* nickname = nullptr;
            if (ts3Functions.getClientVariableAsString(
                    scHandlerID, (anyID)selectedItemID, CLIENT_NICKNAME, &nickname) != ERROR_ok
                || !nickname) return;

            std::string cmd = std::string("WATCH_USER:") + nickname;
            ts3Functions.freeMemory(nickname);

            if (!PipeClient::Send(cmd))
                PipeClient::LaunchApp();
        }
    }
}

// ── Server connect → auto-launch ─────────────────────────────────────────────

__declspec(dllexport) void ts3plugin_onConnectStatusChangeEvent(
    uint64 scHandlerID, int newStatus, unsigned int errorNumber)
{
    PluginLog("onConnectStatusChangeEvent status=" + std::to_string(newStatus));
    if (newStatus == STATUS_CONNECTION_ESTABLISHED)
        g_activeServer = scHandlerID;
    else if (newStatus == STATUS_DISCONNECTED && scHandlerID == g_activeServer)
        g_activeServer = 0;
    if (newStatus != STATUS_CONNECTION_ESTABLISHED) return;
    CheckServerRelayUrl(scHandlerID);
    ts3Functions.requestServerVariables(scHandlerID);
}

__declspec(dllexport) void ts3plugin_onServerUpdatedEvent(uint64 scHandlerID)
{
    PluginLog("onServerUpdatedEvent fired");
    CheckServerRelayUrl(scHandlerID);
}

} // extern "C"
