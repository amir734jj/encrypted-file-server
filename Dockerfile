# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY EncryptedFileServer.slnx .
COPY Shared/Shared.csproj Shared/
COPY Api/Api.csproj Api/
COPY UI/UI.csproj UI/

# Restore
RUN dotnet restore

# Copy everything and build
COPY . .
RUN dotnet publish Api/Api.csproj -c Release -o /app/publish

# UI publish (Blazor WASM)
RUN dotnet publish UI/UI.csproj -c Release -o /app/ui-publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install curl for healthchecks
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Copy published API
COPY --from=build /app/publish .

# Copy Blazor WASM output into wwwroot
COPY --from=build /app/ui-publish/wwwroot ./wwwroot

# Create storage directory
RUN mkdir -p /app/storage

# Expose HTTP + FTP ports
EXPOSE 5000
EXPOSE 2121

# FTP passive mode ports (if needed)
EXPOSE 50000-50100

ENV ASPNETCORE_ENVIRONMENT=Production
ENV PORT=5000
ENV Storage__BasePath=/app/storage

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
    CMD curl -f http://localhost:5000/api/health || exit 1

ENTRYPOINT ["dotnet", "Api.dll"]

ENTRYPOINT ["dotnet", "Api.dll"]
