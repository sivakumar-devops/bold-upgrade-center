# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY BoldBI.UpgradeCenter.csproj ./
RUN dotnet restore BoldBI.UpgradeCenter.csproj

COPY . ./
RUN dotnet publish BoldBI.UpgradeCenter.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    DOTNET_RUNNING_IN_CONTAINER=true \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        nodejs \
        npm \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

RUN npm ci --include=dev --ignore-scripts \
    && npx playwright install --with-deps chromium \
    && npm cache clean --force \
    && groupadd --system --gid 10001 appgroup \
    && useradd --system --uid 10001 --gid appgroup --home-dir /app --shell /usr/sbin/nologin appuser \
    && mkdir -p /app/App_Data/DataProtectionKeys /tmp/boldbi-upgrade-center /ms-playwright \
    && chown -R appuser:appgroup /app /tmp/boldbi-upgrade-center /ms-playwright

USER appuser

EXPOSE 8080

ENTRYPOINT ["dotnet", "BoldBI.UpgradeCenter.dll"]
