FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY . .
RUN dotnet restore

RUN dotnet publish Api/Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache krb5-libs

COPY --from=build /app/publish .

EXPOSE 5000 2121 2222 50000-50100

ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Api.dll"]
