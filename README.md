# Encrypted File Server

End-to-end encrypted file storage with a Blazor WebAssembly UI, REST API, and built-in FTP server. Files are encrypted at rest on remote FTP backends — the server never stores plaintext.

## Features

- **Multi-provider encryption** — AES-256-CTR (streaming), AES-256-GCM (chunked AEAD), or ChaCha20-Poly1305 (chunked AEAD), selectable per data source
- **Data sources** — named storage buckets, each backed by a configurable FTP server
- **Folder hierarchy** — virtual directories with breadcrumb navigation, create/delete/move
- **Dual frontend access** — HTTP file server (`/browse/`) and built-in FTP server (port 2121)
- **Anonymous access** — optionally allow anonymous read-only access per data source, per frontend
- **User management** — registration, login (JWT), admin impersonation
- **Streaming crypto** — constant ~128 KB memory per stream regardless of file size
- **Web UI** — Blazor WASM with Havit Blazor components

## Quick Start

```bash
# Docker
docker build -t encrypted-file-server .
docker run -p 5000:5000 -p 2121:2121 \
  -e DATABASE_URL="postgres://user:pass@host:5432/dbname" \
  -e Jwt__Key="your-secret-key-min-32-chars-long" \
  encrypted-file-server

# Local
dotnet run --project Api
```

## Stack

.NET 10 · Blazor WASM · PostgreSQL · FluentFTP · FubarDev.FtpServer · Havit Blazor
