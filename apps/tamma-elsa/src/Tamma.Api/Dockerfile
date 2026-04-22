# Tamma API Dockerfile
# Multi-stage build for optimized production image

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files
COPY ["Tamma.Api/Tamma.Api.csproj", "Tamma.Api/"]
COPY ["Tamma.Core/Tamma.Core.csproj", "Tamma.Core/"]
COPY ["Tamma.Data/Tamma.Data.csproj", "Tamma.Data/"]
COPY ["Tamma.Activities/Tamma.Activities.csproj", "Tamma.Activities/"]

# Restore dependencies
RUN dotnet restore "Tamma.Api/Tamma.Api.csproj"

# Copy source code
COPY . .

# Build the project
WORKDIR "/src/Tamma.Api"
RUN dotnet build "Tamma.Api.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Tamma.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN groupadd -r tamma && useradd -r -g tamma tamma

# Copy published files
COPY --from=publish /app/publish .

# Create logs directory
RUN mkdir -p /app/logs && chown -R tamma:tamma /app

# Switch to non-root user
USER tamma

# Expose port
EXPOSE 3100

# Health check
HEALTHCHECK --interval=15s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:3100/api/health || exit 1

# Set environment variables
ENV ASPNETCORE_URLS=http://+:3100
ENV ASPNETCORE_ENVIRONMENT=Production

# Entry point
ENTRYPOINT ["dotnet", "Tamma.Api.dll"]
