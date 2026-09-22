# Multi-stage Dockerfile for the MetricsServer standalone service
# Builds a .NET 10.0 in-memory rollup metrics server that listens on port 9100

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env

WORKDIR /app

# Copy the project file first so restore layers cache
COPY MetricsServer/MetricsServer.csproj ./MetricsServer/

WORKDIR /app/MetricsServer
RUN dotnet restore

# Copy the rest of the source code
WORKDIR /app
COPY MetricsServer/ ./MetricsServer/

WORKDIR /app/MetricsServer
RUN dotnet publish -c Release -o /app/out

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

# curl is required by the HEALTHCHECK below; the aspnet image does not ship it
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build-env /app/out .

# Expose the metrics server port
EXPOSE 9100

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=
ENV METRICSSERVER_PORT=9100
ENV DOTNET_gcServer=1

# Set APPINSIGHTS_CONNECTIONSTRING to enable Application Insights telemetry

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:9100/health || exit 1

ENTRYPOINT ["dotnet", "MetricsServer.dll"]
