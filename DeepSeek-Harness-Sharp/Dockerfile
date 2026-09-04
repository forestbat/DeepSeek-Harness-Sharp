FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj", "DeepSeek-Harness-Sharp/"]
RUN dotnet restore "DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj"
COPY . .
WORKDIR "/src/DeepSeek-Harness-Sharp"
RUN dotnet build "./DeepSeek-Harness-Sharp.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./DeepSeek-Harness-Sharp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "DeepSeek-Harness-Sharp.dll"]
