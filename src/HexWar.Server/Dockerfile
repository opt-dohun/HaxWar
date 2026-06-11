FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app

# 캐시 최적화를 위해 csproj 파일 복사 및 복원
COPY HexWar.sln ./
COPY src/HexWar.Server/HexWar.Server.csproj src/HexWar.Server/
COPY src/HexWar.Infrastructure/HexWar.Infrastructure.csproj src/HexWar.Infrastructure/
COPY src/HexWar.Application/HexWar.Application.csproj src/HexWar.Application/
COPY src/HexWar.Domain/HexWar.Domain.csproj src/HexWar.Domain/
COPY src/HexWar.Matchmaking/HexWar.Matchmaking.csproj src/HexWar.Matchmaking/
COPY src/HexWar.Client/HexWar.Client.csproj src/HexWar.Client/

RUN dotnet restore src/HexWar.Server/HexWar.Server.csproj

# 소스코드 전체 복사
COPY src/ src/

WORKDIR /app/src/HexWar.Server
RUN dotnet publish -c Release -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /publish .
EXPOSE 5051
ENV ASPNETCORE_URLS=http://+:5051
ENTRYPOINT ["dotnet", "HexWar.Server.dll"]
