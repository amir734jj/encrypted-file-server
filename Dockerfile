FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY . .
RUN dotnet restore

RUN dotnet publish Api/Api.csproj -c Release -o /app/publish
RUN dotnet publish UI/UI.csproj -c Release -o /app/ui-publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache curl krb5-libs

COPY --from=build /app/publish .
COPY --from=build /app/ui-publish/wwwroot ./wwwroot
RUN mkdir -p /app/storage

EXPOSE 5000 2121 2222 50000-50100

ENV ASPNETCORE_ENVIRONMENT=Production
ENV Storage__BasePath=/app/storage

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s \
    CMD curl -f "http://localhost:$(echo ${ASPNETCORE_URLS:-http://*:${PORT:-5000}} | grep -oE '[0-9]+$')/api/health" || exit 1

ENTRYPOINT ["dotnet", "Api.dll"]
