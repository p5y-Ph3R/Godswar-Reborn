# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY GodswarServer.sln ./
COPY src/Godswar.Server/Godswar.Server.csproj src/Godswar.Server/
RUN dotnet restore src/Godswar.Server/Godswar.Server.csproj

COPY . .
RUN dotnet publish src/Godswar.Server/Godswar.Server.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./
COPY appsettings.docker.json ./appsettings.json

EXPOSE 5999 7000

ENTRYPOINT ["dotnet", "Godswar.Server.dll", "appsettings.json"]
