# Encrypted File Server

An encrypted file server where users can register, login, and manage encrypted file storage through data sources.

## Architecture

- **Shared** - DTOs, Refit API contracts, shared models
- **Api** - ASP.NET Core API with Identity, EF Core + PostgreSQL, AES-256-CBC stream encryption, built-in FTP server
- **UI** - Blazor WebAssembly with Havit Blazor components

## Features

- **User Authentication** - Register/Login with ASP.NET Identity + JWT
- **Data Sources** - Organize files into named buckets
- **Stream Encryption** - AES-256-CBC encryption with per-file IV, streamed on read/write
- **FTP Server** - Built-in FTP server (port 2121) with virtual encrypted filesystem
- **HTTP Downloads** - Stream-decrypted file downloads via REST API
- **Web UI** - Havit Blazor dashboard for managing data sources and files

## Encryption

- Each user gets a 256-bit random master key on registration
- Files are encrypted with AES-256-CBC using the user's master key
- Each file has a unique random IV stored in the database
- Decryption is streamed — files are never fully decrypted on disk
- Master key is stored in the database (zero-knowledge mode planned for future)

## Getting Started

### Prerequisites
- .NET 10 SDK
- PostgreSQL

### Run Locally
```bash
# Update connection string in Api/appsettings.json
# Then:
cd Api
dotnet ef migrations add InitialCreate
dotnet run
```

### FTP Access
Connect with any FTP client:
- **Host**: localhost
- **Port**: 2121
- **Username**: your email
- **Password**: your password
- Directory structure: `/{DataSourceName}/files...`

### Docker
```bash
docker build -t encrypted-file-server .
docker run -p 5000:5000 -p 2121:2121 \
  -e DATABASE_URL="postgres://user:pass@host:5432/dbname" \
  -e Jwt__Key="your-secret-key-min-32-chars-long" \
  -v efserver-storage:/app/storage \
  encrypted-file-server
```
