using TS3ScreenShare.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 2 * 1024 * 1024; // 2 MB for video frames
});
builder.Services.Configure<TS3ServerQueryOptions>(
    builder.Configuration.GetSection(TS3ServerQueryOptions.Section));
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Services.AddSingleton<StreamRegistry>();
builder.Services.AddSingleton<TS3ServerQueryService>();

var app = builder.Build();

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
