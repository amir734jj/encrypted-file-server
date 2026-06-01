# Encrypted File Server

End-to-end encrypted file storage with a Blazor WebAssembly UI, REST API, built-in FTP server, and SFTP server. Files are encrypted at rest on remote FTP backends — the server never stores plaintext.

## Features

- **Multi-provider encryption** — AES-256-CTR (streaming), AES-256-GCM (chunked AEAD), or ChaCha20-Poly1305 (chunked AEAD), selectable per data source
- **Data sources** — named storage buckets, each backed by a configurable FTP server
- **Folder hierarchy** — virtual directories with breadcrumb navigation, create/delete/move
- **Triple frontend access** — HTTP (`/browse/`), FTP (port 2121), and SFTP (port 2222)
- **Anonymous access** — optionally allow anonymous read-only access per data source, per frontend
- **User management** — registration, login (JWT), admin impersonation
- **Streaming crypto** — constant ~128 KB memory per stream regardless of file size
- **Web UI** — Blazor WASM with Havit Blazor components

## Quick Start

```bash
# Docker
docker build -t encrypted-file-server .
docker run -p 3000:3000 -p 2121:2121 -p 2222:2222 -p 50000-50004:50000-50004 \
  -e DATABASE_URL="postgres://user:pass@host:5432/dbname" \
  -e Jwt__Key="your-secret-key-min-32-chars-long" \
  encrypted-file-server

# Local
dotnet run --project Api
```

## Ports

| Port | Protocol | Description |
|------|----------|-------------|
| 3000 | HTTP | Web UI and REST API |
| 2121 | FTP | FTP server (active + passive) |
| 2222 | SSH | SFTP server |
| 50000–50004 | TCP | FTP passive data ports |

## Connecting

### Web UI

Open `http://localhost:3000` in your browser.

### FTP

```bash
ftp localhost 2121
# or with a GUI client (FileZilla, WinSCP) pointing to port 2121
```

### SFTP

```bash
sftp -P 2222 user@localhost
# or with a GUI client (FileZilla, WinSCP) using SFTP protocol on port 2222
```

Anonymous access can be enabled per data source in the admin settings.

## Configuration

Key settings in `appsettings.json`:

```jsonc
{
  "Ftp": {
    "Port": 2121,              // FTP control port
    "PasvMinPort": 50000,      // Passive mode port range start
    "PasvMaxPort": 50004,      // Passive mode port range end
    "PublicHostname": "your-domain.com"  // Public hostname for PASV responses
  }
}
```

The SFTP server runs on port 2222 and generates an RSA host key on first start (persisted to `ssh_host_key.pem`).

## Stack

.NET 10 · Blazor WASM · PostgreSQL · FluentFTP · FubarDev.FtpServer · FxSsh · Havit Blazor
