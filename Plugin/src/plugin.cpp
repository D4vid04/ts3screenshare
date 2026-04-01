#pragma warning(disable: 4100) // unreferenced formal parameter
#pragma warning(disable: 4996) // strncpy

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <assert.h>

#include "ts3_functions.h"
#include "plugin_definitions.h"
#include "teamspeak/public_definitions.h"
#include "teamspeak/public_errors.h"

#include "pipe_client.h"

// ── Menu helper (mirrors SDK sample) ─────────────────────────────────────────
static struct PluginMenuItem* createMenuItem(enum PluginMenuType type, int id,
                                             const char* text, const char* icon)
{
    auto* item = static_cast<struct PluginMenuItem*>(malloc(sizeof(struct PluginMenuItem)));
    item->type = type;
    item->id   = id;
    strncpy(item->text, text, PLUGIN_MENU_BUFSZ);
    strncpy(item->icon, icon, PLUGIN_MENU_BUFSZ);
    return item;
}

#define BEGIN_CREATE_MENUS(x)                                   \
    const size_t _menu_sz = (x) + 1;                           \
    size_t _menu_n = 0;                                         \
    *menuItems = (struct PluginMenuItem**)malloc(               \
        sizeof(struct PluginMenuItem*) * _menu_sz);

#define CREATE_MENU_ITEM(a, b, c, d) (*menuItems)[_menu_n++] = createMenuItem(a, b, c, d);

#define END_CREATE_MENUS                        \
    (*menuItems)[_menu_n++] = nullptr;          \
    assert(_menu_n == _menu_sz);

#define PLUGIN_API_VERSION 26

static struct TS3Functions ts3Functions;

// ── Menu IDs ─────────────────────────────────────────────────────────────────
#define MENU_ID_WATCH_STREAM 1

// ── Plugin metadata ───────────────────────────────────────────────────────────

const char* ts3plugin_name()        { return "TS3ScreenShare"; }
const char* ts3plugin_version()     { return "1.0.0"; }
int         ts3plugin_apiVersion()  { return PLUGIN_API_VERSION; }
const char* ts3plugin_author()      { return "D4vid04"; }
const char* ts3plugin_description() {
    return "Integrates TS3ScreenShare with TeamSpeak 3.\n\n"
           "Features:\n"
           "- Auto-launches TS3ScreenShare when joining a server that has a relay URL\n"
           "  in its welcome message (format: [TS3SS] wss://your-relay:5000)\n"
           "- Right-click any user -> \"Watch TS3 Stream\" to open their stream";
}

void ts3plugin_setFunctionPointers(const struct TS3Functions funcs) {
    ts3Functions = funcs;
}

// ── Init / Shutdown ───────────────────────────────────────────────────────────

int  ts3plugin_init()     { return 0; } // 0 = success
void ts3plugin_shutdown() {}

// ── Context menus ─────────────────────────────────────────────────────────────

void ts3plugin_initMenus(struct PluginMenuItem*** menuItems, char** menuIcon)
{
    BEGIN_CREATE_MENUS(1);
    CREATE_MENU_ITEM(PLUGIN_MENU_TYPE_CLIENT, MENU_ID_WATCH_STREAM, "Watch TS3 Stream", "");
    END_CREATE_MENUS;

    *menuIcon = (char*)malloc(1);
    **menuIcon = '\0';
}

void ts3plugin_onMenuItemEvent(uint64 scHandlerID, enum PluginMenuType type,
                                int menuItemID, uint64 selectedItemID)
{
    if (type != PLUGIN_MENU_TYPE_CLIENT || menuItemID != MENU_ID_WATCH_STREAM) return;

    // Get the selected client's nickname
    char* nickname = nullptr;
    unsigned int err = ts3Functions.getClientVariableAsString(
        scHandlerID, (anyID)selectedItemID, CLIENT_NICKNAME, &nickname);

    if (err != ERROR_ok || !nickname) return;

    std::string cmd = std::string("WATCH_USER:") + nickname;
    ts3Functions.freeMemory(nickname);

    if (!PipeClient::Send(cmd))
    {
        // App not running — launch it (user can then connect and watch manually)
        PipeClient::LaunchApp();
    }
}

// ── Server connect → auto-launch ─────────────────────────────────────────────

static void CheckServerRelayUrl(uint64 scHandlerID)
{
    char* welcomeMsg = nullptr;
    unsigned int err = ts3Functions.getServerVariableAsString(
        scHandlerID, VIRTUALSERVER_WELCOMEMESSAGE, &welcomeMsg);

    if (err != ERROR_ok || !welcomeMsg) return;

    std::string msg(welcomeMsg);
    ts3Functions.freeMemory(welcomeMsg);

    // Find [TS3SS] marker anywhere in the welcome message
    const std::string marker = "[TS3SS]";
    auto pos = msg.find(marker);
    if (pos == std::string::npos) return;

    // Extract URL — first token after the marker
    std::string rest = msg.substr(pos + marker.size());
    auto start = rest.find_first_not_of(" \t\r\n");
    if (start == std::string::npos) return;
    rest = rest.substr(start);
    auto end = rest.find_first_of(" \t\r\n");
    std::string relayUrl = (end != std::string::npos) ? rest.substr(0, end) : rest;

    if (relayUrl.empty()) return;

    if (PipeClient::IsAppRunning())
    {
        PipeClient::Send("RELAY:" + relayUrl);
        PipeClient::Send("FOCUS");
    }
    else
    {
        PipeClient::LaunchApp(relayUrl);
    }
}

void ts3plugin_onConnectStatusChangeEvent(uint64 scHandlerID, int newStatus,
                                           unsigned int errorNumber)
{
    if (newStatus != STATUS_CONNECTION_ESTABLISHED) return;
    // Server variables are available once the connection is fully established
    ts3Functions.requestServerVariables(scHandlerID);
}

// Called after requestServerVariables completes — server data is now in cache
void ts3plugin_onServerUpdatedEvent(uint64 scHandlerID)
{
    CheckServerRelayUrl(scHandlerID);
}

// ── Required stubs ────────────────────────────────────────────────────────────

void ts3plugin_freeMemory(void* data) { free(data); }

int  ts3plugin_offersConfigure()                                                { return PLUGIN_OFFERS_NO_CONFIGURE; }
void ts3plugin_configure(void* handle, void* qParentWidget)                     {}
void ts3plugin_registerPluginID(const char* id)                                 {}
const char* ts3plugin_commandKeyword()                                          { return nullptr; }
int  ts3plugin_processCommand(uint64 scHandlerID, const char* command)          { return 0; }
void ts3plugin_currentServerConnectionChanged(uint64 scHandlerID)               {}
const char* ts3plugin_infoTitle()                                               { return nullptr; }
void ts3plugin_infoData(uint64 scHandlerID, uint64 id,
                        enum PluginItemType type, char** data)                  {}
void ts3plugin_initHotkeys(struct PluginHotkey*** hotkeys)                      {}
int  ts3plugin_onServerErrorEvent(uint64 scHandlerID, const char* errorMessage,
                                   unsigned int error, const char* returnCode,
                                   const char* extraMessage)                    { return 0; }
int  ts3plugin_onTextMessageEvent(uint64 scHandlerID, anyID targetMode,
                                   anyID toID, anyID fromID,
                                   const char* fromName,
                                   const char* fromUniqueIdentifier,
                                   const char* message, int ffIgnored)         { return 0; }
void ts3plugin_onTalkStatusChangeEvent(uint64 scHandlerID, int status,
                                        int isReceivedWhisper, anyID clientID) {}
void ts3plugin_onConnectionInfoEvent(uint64 scHandlerID, anyID clientID)       {}
void ts3plugin_onClientMoveEvent(uint64 scHandlerID, anyID clientID,
                                  uint64 oldChannelID, uint64 newChannelID,
                                  int visibility, const char* moveMessage)     {}
void ts3plugin_onClientMoveTimeoutEvent(uint64 scHandlerID, anyID clientID,
                                         uint64 oldChannelID, uint64 newChannelID,
                                         int visibility,
                                         const char* timeoutMessage)           {}
void ts3plugin_onClientMoveMovedEvent(uint64 scHandlerID, anyID clientID,
                                       uint64 oldChannelID, uint64 newChannelID,
                                       int visibility, anyID moverID,
                                       const char* moverName,
                                       const char* moverUniqueIdentifier,
                                       const char* moveMessage)                {}
void ts3plugin_onClientKickFromChannelEvent(uint64 scHandlerID, anyID clientID,
                                             uint64 oldChannelID,
                                             uint64 newChannelID, int visibility,
                                             anyID kickerID,
                                             const char* kickerName,
                                             const char* kickerUniqueIdentifier,
                                             const char* kickMessage)          {}
void ts3plugin_onClientKickFromServerEvent(uint64 scHandlerID, anyID clientID,
                                            uint64 oldChannelID,
                                            uint64 newChannelID, int visibility,
                                            anyID kickerID,
                                            const char* kickerName,
                                            const char* kickerUniqueIdentifier,
                                            const char* kickMessage)           {}
void ts3plugin_onClientIDsEvent(uint64 scHandlerID,
                                 const char* uniqueClientIdentifier,
                                 anyID clientID,
                                 const char* clientName)                       {}
void ts3plugin_onClientIDsFinishedEvent(uint64 scHandlerID)                    {}
void ts3plugin_onServerStopEvent(uint64 scHandlerID,
                                  const char* shutdownMessage)                 {}
void ts3plugin_onUserLoggingMessageEvent(const char* logMessage, int logLevel,
                                          const char* logChannel, uint64 logID,
                                          const char* logTime,
                                          const char* completeLogString)       {}
void ts3plugin_onClientPokeEvent(uint64 scHandlerID, anyID fromClientID,
                                  const char* pokerName,
                                  const char* pokerUniqueIdentity,
                                  const char* message, int ffIgnored)          {}
void ts3plugin_onClientSelfVariableUpdateEvent(uint64 scHandlerID, int flag,
                                                const char* oldValue,
                                                const char* newValue)          {}
void ts3plugin_onChannelSubscribeEvent(uint64 scHandlerID,
                                        uint64 channelID)                      {}
void ts3plugin_onChannelSubscribeFinishedEvent(uint64 scHandlerID)             {}
void ts3plugin_onChannelUnsubscribeEvent(uint64 scHandlerID,
                                          uint64 channelID)                    {}
void ts3plugin_onChannelUnsubscribeFinishedEvent(uint64 scHandlerID)           {}
void ts3plugin_onSoundDeviceListChangedEvent(const char* modeID,
                                              int playOrCap)                   {}
void ts3plugin_onPlaybackShutdownCompleteEvent(uint64 scHandlerID)             {}
