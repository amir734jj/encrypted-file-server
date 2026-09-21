FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false

COPY . .
RUN dotnet restore

RUN dotnet publish Api/Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache curl krb5-libs icu-libs icu-data-full

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV ASPNETCORE_URLS=http://+:3000

COPY --from=build /app/publish .

EXPOSE 3000 2121 2222 50000 50001 50002 50003 50004

ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:3000/api/health || exit 1

ENTRYPOINT ["dotnet", "Api.dll"]
