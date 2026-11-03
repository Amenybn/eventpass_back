# ---- Build : restauration, tests puis publication ----
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Restauration en cache tant que les .csproj ne changent pas
COPY src/EventPass.Api/EventPass.Api.csproj src/EventPass.Api/
COPY tests/EventPass.Api.Tests/EventPass.Api.Tests.csproj tests/EventPass.Api.Tests/
RUN dotnet restore tests/EventPass.Api.Tests/EventPass.Api.Tests.csproj

COPY . .
# L'image n'est produite que si les tests passent
RUN dotnet test tests/EventPass.Api.Tests/EventPass.Api.Tests.csproj -c Release --no-restore \
 && dotnet publish src/EventPass.Api/EventPass.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app
COPY --from=build /app .

# Dossier de la base SQLite (montez-y un volume pour conserver les données),
# inutilisé si PostgreSQL est configuré (DB_HOST ou DATABASE_URL)
RUN mkdir -p /app/data && chown 1654:1654 /app/data

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    SQLITE_PATH=/app/data/eventpass.db

# Utilisateur non-root numérique ("app" de l'image .NET) : compatible runAsNonRoot
USER 1654:1654
EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
  CMD wget -qO- "http://127.0.0.1:${PORT:-8080}/health" >/dev/null || exit 1

ENTRYPOINT ["dotnet", "EventPass.Api.dll"]
