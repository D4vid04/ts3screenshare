# TS3ScreenShare

Screen sharing for TeamSpeak 3. Share your screen with people in the same TeamSpeak channel — no third-party services required.

## How it works

1. You connect the client to your TeamSpeak server and a relay server
2. The relay server verifies your identity via TS3 ServerQuery (challenge via away message)
3. Once authenticated, you can stream your screen to everyone in your channel
4. Streams are scoped to channels — you only see streams from people in your current channel

## Client

### Installation

Download `TS3ScreenShare-Setup-v1.0.0.exe` from [Releases](https://github.com/D4vid04/ts3screenshare/releases) and run it.

During installation you can optionally install the **TeamSpeak 3 plugin**, which adds:
- Right-click menu on yourself → Start / Stop stream
- Global plugin menu → Start / Stop stream
- Notification sound (played through TS3 audio) when someone in your channel starts streaming
- Auto-launch of the app when you join a TS3 server

### Requirements
- Windows 10/11 x64
- TeamSpeak 3 client (for the plugin)

### Usage

1. Enter your TeamSpeak ServerQuery API key (optional, needed for some TS3 server setups)
2. Enter the relay server URL (e.g. `https://relay.yourdomain.com`)
3. Click **Connect**
4. Once connected, click **Start stream** to share your screen

If the TS3 plugin is installed, the app launches and connects automatically when you join a TS3 server.

## TS3 Plugin

The plugin is installed together with the client (optional step in the installer). It provides:

- **Start/Stop stream** in the right-click menu on your own client entry
- **Start/Stop stream** in the global Plugins menu
- **Notification sound** when someone in your current channel starts streaming (played via TS3 audio, respects TS3 volume settings)
- **Auto-launch** — the app is opened automatically when you connect to a TS3 server

The plugin communicates with the app via a named pipe (`\\.\pipe\TS3ScreenShare`).

## Relay Server

The relay server is distributed as a Docker image. It requires a running TeamSpeak 3 server with ServerQuery access.

### Quick start

```yaml
services:
  relay:
    image: d4vid04/ts3screenshare-server:latest
    restart: unless-stopped
    ports:
      - "5174:5174"
    # NOTE: The server runs on plain HTTP. HTTPS must be handled by a reverse proxy
    # in front of this container (e.g. Nginx, Caddy, Traefik, or Cloudflare Tunnel).
    environment:
      - TS3ServerQuery__Host=localhost
      - TS3ServerQuery__Port=10011
      - TS3ServerQuery__Username=serveradmin
      - TS3ServerQuery__Password=ECZvJs5L
```

> The server will refuse to start if ServerQuery credentials are missing or incorrect.

### Configuration

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `TS3ServerQuery__Host` | Yes | `localhost` | TS3 server hostname or IP |
| `TS3ServerQuery__Port` | Yes | `10011` | TS3 ServerQuery port |
| `TS3ServerQuery__Username` | Yes | `serveradmin` | ServerQuery username |
| `TS3ServerQuery__Password` | Yes | — | ServerQuery password |
| `TS3ServerQuery__VirtualServerId` | No | `1` | Virtual server ID |

#### Access control

| Variable | Description |
|----------|-------------|
| `TS3ServerQuery__ConnectionAllowedGroupIds__0` | Whitelist — only these server groups can connect. Empty = everyone. |
| `TS3ServerQuery__ConnectionBlockedGroupIds__0` | Blacklist — these server groups cannot connect. |
| `TS3ServerQuery__StreamingAllowedGroupIds__0` | Only these server groups can start a stream. Empty = everyone. |
| `TS3ServerQuery__StreamingAllowedChannelIds__0` | Only these channels allow streaming. Empty = all channels. |
| `TS3ServerQuery__StreamingBlockedChannelIds__0` | Streaming is blocked in these channels. Takes priority over allowed list. |

Use `__0`, `__1`, `__2`, ... to specify multiple values.

### TS3 ServerQuery whitelist

If the relay server is on the same machine as TS3, add `127.0.0.1` to the ServerQuery IP allowlist to prevent flood protection from blocking rapid connections:

```
# /var/ts3server/query_ip_allowlist.txt
127.0.0.1
::1
```

Restart the TS3 server after editing the file.

### Reverse proxy

The relay server listens on plain HTTP. You must place a reverse proxy in front of it to provide HTTPS. The client requires a secure connection.

**Cloudflare Tunnel**
```
Service: http://localhost:5174
```

**Caddy**
```
relay.yourdomain.com {
    reverse_proxy localhost:5174
}
```

**Nginx**
```nginx
server {
    listen 443 ssl;
    server_name relay.yourdomain.com;

    ssl_certificate     /path/to/cert.pem;
    ssl_certificate_key /path/to/key.pem;

    location / {
        proxy_pass http://localhost:5174;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
    }
}
```

### Updating

```bash
docker compose pull
docker compose up -d
```

## Security

- All clients must authenticate via TS3 challenge-response before accessing any stream
- The server verifies identity by checking the client's TS3 away message via ServerQuery
- Streams are visible only to clients in the same TS3 channel
- The server periodically checks that connected clients are still on the TS3 server and disconnects those who have left
- The server shuts down on startup if ServerQuery is unreachable or credentials are invalid
