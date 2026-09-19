FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY src/Shared/Shared.csproj src/Shared/
COPY src/Shared-parser/Shared-parser.csproj src/Shared-parser/
COPY src/SimpleL7Proxy/SimpleL7Proxy.csproj src/SimpleL7Proxy/
COPY src/CompanionApp/CompanionApp.csproj src/CompanionApp/

RUN dotnet restore src/CompanionApp/CompanionApp.csproj

COPY src/Shared/ src/Shared/
COPY src/Shared-parser/ src/Shared-parser/
COPY src/SimpleL7Proxy/ src/SimpleL7Proxy/
COPY src/HealthProbe/Constants.cs src/HealthProbe/
COPY deployment/interactive/deploy.parameters.example.sh deployment/
COPY src/CompanionApp/ src/CompanionApp/

RUN dotnet publish src/CompanionApp/CompanionApp.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .

RUN mkdir -p /app/data /app/.keys \
    && chown app:app /app/data /app/.keys

USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "CompanionApp.dll"]
