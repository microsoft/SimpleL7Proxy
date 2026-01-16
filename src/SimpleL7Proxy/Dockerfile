# Use the official image as a parent image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env

# Set the working directory.
WORKDIR /app

# Copy the Shared project first
COPY Shared/Shared.csproj ./Shared/
# Copy SimpleL7Proxy project file
COPY SimpleL7Proxy/SimpleL7Proxy.csproj ./SimpleL7Proxy/

# Restore dependencies for SimpleL7Proxy (which will also restore Shared)
WORKDIR /app/SimpleL7Proxy
RUN dotnet restore

# Copy the rest of the source code
WORKDIR /app
COPY Shared/ ./Shared/
COPY SimpleL7Proxy/ ./SimpleL7Proxy/

# Build the application from SimpleL7Proxy directory
WORKDIR /app/SimpleL7Proxy
RUN dotnet publish -c Release -o /app/out

# Use ASP.NET Core runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Set the working directory.
WORKDIR /app

# Copy the build output from the build image
COPY --from=build-env /app/out .

# Copy config.json to the working directory
COPY SimpleL7Proxy/config.json .
COPY SimpleL7Proxy/auth.json .

# Expose port 443 for the application.
EXPOSE 443
EXPOSE 9000

# Define the command to run your app using `dotnet`
ENTRYPOINT ["dotnet", "SimpleL7Proxy.dll", "--urls", "http://*:443"]