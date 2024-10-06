# Use the official image as a parent image.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env

# Set the working directory.
WORKDIR /app

# Copy csproj and restore dependencies
COPY *.csproj ./
RUN dotnet restore

# Copy the rest of the working directory contents into the container at /app.
COPY . ./

# Build the application.
RUN dotnet publish -c Release -o out

# Use ASP.NET Core runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0

# Set the working directory.
WORKDIR /app

# Copy the build output from the build image
COPY --from=build-env /app/out .

# Expose port 443 for the application.
EXPOSE 443

# Define the command to run your app using `dotnet`
ENTRYPOINT ["dotnet", "SimpleL7Proxy.dll", "--urls", "http://*:443"]