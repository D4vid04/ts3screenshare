namespace TS3ScreenShare.Models;

public sealed class AppSettings
{
    public string ApiKey { get; set; } = "";
    public string RelayUrl { get; set; } = "wss://0.0.0.0:5000";
    public bool NotificationSound { get; set; } = true;
    public bool NotificationChat { get; set; } = true;
}
