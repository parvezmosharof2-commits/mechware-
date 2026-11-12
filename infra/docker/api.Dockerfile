# syntax=docker/dockerfile:1.7
# MECHWARE API. Build from the repo root:  docker build -f infra/docker/api.Dockerfile -t mechware-api .
# Runtime image is the full (non-chiseled) Ubuntu one on purpose: it ships ICU (the code formats bn-BD) and tzdata, and has
# bash for the HEALTHCHECK. Money.cs uses CultureInfo("bn-BD"), so invariant globalization must stay OFF.

FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# RID restricts native assets (runtimes/ is ~600 MB otherwise). Use linux-arm64 on ARM hosts.
ARG RID=linux-x64

# Restore layer: project files only, so code edits do not invalidate the package cache.
COPY backend/src/Mechware.Domain/Mechware.Domain.csproj                 backend/src/Mechware.Domain/
COPY backend/src/Mechware.Application/Mechware.Application.csproj       backend/src/Mechware.Application/
COPY backend/src/Mechware.Infrastructure/Mechware.Infrastructure.csproj backend/src/Mechware.Infrastructure/
COPY backend/src/Mechware.Payment/Mechware.Payment.csproj               backend/src/Mechware.Payment/
COPY backend/src/Mechware.Api/Mechware.Api.csproj                       backend/src/Mechware.Api/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore backend/src/Mechware.Api/Mechware.Api.csproj -r $RID

COPY backend/src backend/src
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish backend/src/Mechware.Api/Mechware.Api.csproj -c Release --no-restore -r $RID --no-self-contained -o /app/publish \
      -p:UseAppHost=false -p:DebugType=none -p:GenerateDocumentationFile=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    Documents__LocalPath=/data/documents
COPY --from=build /app/publish .
# /data/documents is the only writable path besides /tmp: mount a volume there (see docker-compose.staging.yml).
RUN mkdir -p /data/documents && chown -R app:app /data
USER 1654
EXPOSE 8080
VOLUME ["/data/documents"]
# bash /dev/tcp probe: the image has no curl/wget. Liveness only; /health/ready also needs Postgres and Redis.
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=5 \
  CMD ["bash","-c","exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/live HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "Mechware.Api.dll"]
