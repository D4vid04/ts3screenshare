using Microsoft.Extensions.Options;
using TS3ScreenShare.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 2 * 1024 * 1024; // 2 MB for video frames
});
builder.Services.Configure<TS3ServerQueryOptions>(
    builder.Configuration.GetSection(TS3ServerQueryOptions.Section));
builder.Services.AddSingleton<StreamRegistry>();
builder.Services.AddSingleton<TS3ServerQueryService>();
builder.Services.AddHostedService<TS3PresenceWatcher>();

var app = builder.Build();

// Verify ServerQuery connectivity at startup — shut down if configured but unreachable
var queryOpts = app.Services.GetRequiredService<IOptionsMonitor<TS3ServerQueryOptions>>().CurrentValue;
if (queryOpts.IsConfigured)
{
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    try
    {
        var ts3 = app.Services.GetRequiredService<TS3ServerQueryService>();
        await ts3.VerifyConnectionAsync();
        startupLogger.LogInformation("ServerQuery login verified at {Host}:{Port}", queryOpts.Host, queryOpts.Port);
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical("ServerQuery login failed at {Host}:{Port} — {Message}. Shutting down.", queryOpts.Host, queryOpts.Port, ex.Message);
        Environment.Exit(1);
    }
}
else
{
    app.Logger.LogCritical("ServerQuery is not configured (password missing). Shutting down.");
    Environment.Exit(1);
}

// Hub protection with a shared key (HubKey in appsettings / env variable)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/hub"))
    {
        var configuredKey = app.Configuration["HubKey"];
        if (!string.IsNullOrEmpty(configuredKey))
        {
            var provided = context.Request.Query["key"].ToString();
            if (provided != configuredKey)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }
    }
    await next();
});

app.MapHub<StreamHub>("/hub");

app.Run();
