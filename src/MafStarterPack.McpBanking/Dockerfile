# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

COPY . /source

WORKDIR /source

RUN dotnet publish -c Release -o /app --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

WORKDIR /app

COPY --from=build /app .

USER $APP_UID

ENTRYPOINT ["dotnet", "MafStarterPack.McpBanking.dll"]
