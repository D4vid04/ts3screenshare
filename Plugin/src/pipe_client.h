#pragma once
#include <string>

// Communicates with the running TS3ScreenShare WPF app via named pipe.
//
// Commands (plugin → app):
//   RELAY:<url>        — set relay server URL in the app
//   WATCH_USER:<name>  — start watching the stream of that TS3 username
//   FOCUS              — bring the app window to the foreground
class PipeClient
{
public:
    static constexpr const char* PIPE_NAME = "\\\\.\\pipe\\TS3ScreenShare";

    // Send a single-line command. Returns true if the app received it.
    static bool Send(const std::string& command);

    // Returns true if the app is currently running (pipe exists).
    static bool IsAppRunning();

    // Launch TS3ScreenShare.exe, optionally passing a relay URL.
    static void LaunchApp(const std::string& relayUrl = "");

private:
    static std::wstring FindAppExe();
};
