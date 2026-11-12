# syntax=docker/dockerfile:1.7
# MECHWARE Worker (outbox relay, stock expiry, search indexer, email). Build from the repo root:
#   docker build -f infra/docker/worker.Dockerfile -t mechware-worker .
# One-off commands run in the same image: `seed-catalog`, `rebuild-search` (docker run ... mechware-worker seed-catalog).

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# RID restricts native assets (runtimes/ is ~600 MB otherwise). Use linux-arm64 on ARM hosts.
ARG RID=linux-x64

COPY backend/src/Mechware.Domain/Mechware.Domain.csproj                 backend/src/Mechware.Domain/
COPY backend/src/Mechware.Application/Mechware.Application.csproj       backend/src/Mechware.Application/
COPY backend/src/Mechware.Infrastructure/Mechware.Infrastructure.csproj backend/src/Mechware.Infrastructure/
COPY backend/src/Mechware.Worker/Mechware.Worker.csproj                 backend/src/Mechware.Worker/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore backend/src/Mechware.Worker/Mechware.Worker.csproj -r $RID

COPY backend/src backend/src
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish backend/src/Mechware.Worker/Mechware.Worker.csproj -c Release --no-restore -r $RID --no-self-contained -o /app/publish \
      -p:UseAppHost=false -p:DebugType=none

# aspnet (not plain runtime) image: Infrastructure uses ASP.NET Core types.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble AS runtime
WORKDIR /app
ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
COPY --from=build /app/publish .
USER 1654
# The Worker exposes no HTTP port, so there is no HEALTHCHECK; the orchestrator watches the process and restarts it.
ENTRYPOINT ["dotnet", "Mechware.Worker.dll"]
