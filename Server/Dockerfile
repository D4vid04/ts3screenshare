FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 5174

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Server/TS3ScreenShare.Server.csproj", "Server/"]
COPY ["Shared/TS3ScreenShare.Shared.csproj", "Shared/"]
RUN dotnet restore "Server/TS3ScreenShare.Server.csproj"
COPY . .
RUN dotnet publish "Server/TS3ScreenShare.Server.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TS3ScreenShare.Server.dll"]
