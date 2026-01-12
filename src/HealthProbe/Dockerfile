# Multi-stage Dockerfile for HealthProbe standalone service
# This builds a .NET 10.0 health probe server that runs on port 9000

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env

WORKDIR /app

# Copy the Shared project first (dependency)
COPY Shared/Shared.csproj ./Shared/
# Copy HealthProbe project file
COPY HealthProbe/HealthProbe.csproj ./HealthProbe/

# Restore dependencies for HealthProbe (which will also restore Shared)
WORKDIR /app/HealthProbe
RUN dotnet restore

# Copy the rest of the source code
WORKDIR /app
COPY Shared/ ./Shared/
COPY HealthProbe/ ./HealthProbe/

# Build and publish the application from HealthProbe directory
WORKDIR /app/HealthProbe
RUN dotnet publish -c Release -o /app/out

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

# Copy the build output from the build stage
COPY --from=build-env /app/out .

# Expose port 9000 for health probe endpoints
EXPOSE 9000

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=

# Health check - verify the service is responding
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:9000/health || exit 1

# Define the entry point to run the HealthProbe service
ENTRYPOINT ["dotnet", "HealthProbe.dll"]
