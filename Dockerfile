# ─────────────────────────────────────────────────────────────
# CALM API — multi-stage build (.NET 8)
# Build context = repo root
# ─────────────────────────────────────────────────────────────

# ── Build stage ──
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore (copy csproj files first for layer caching)
COPY *.sln ./
COPY src/Domain/*.csproj          src/Domain/
COPY src/Application/*.csproj      src/Application/
COPY src/Infrastructure/*.csproj   src/Infrastructure/
COPY src/Api/*.csproj             src/Api/
RUN dotnet restore src/Api/CashLoanManagement.Api.csproj

# Copy everything and publish
COPY . .
RUN dotnet publish src/Api/CashLoanManagement.Api.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ── Runtime stage ──
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
# Host platforms (Render) inject PORT; Program.cs binds to it. 8080 is the local default.
EXPOSE 8080

ENTRYPOINT ["dotnet", "CashLoanManagement.Api.dll"]
