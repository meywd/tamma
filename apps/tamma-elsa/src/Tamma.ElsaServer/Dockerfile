# Tamma ELSA Server Dockerfile
# Multi-stage build for optimized production image
# Build context: apps/tamma-elsa/ (includes src/ and workflows/)

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files for restore
COPY ["src/Tamma.ElsaServer/Tamma.ElsaServer.csproj", "Tamma.ElsaServer/"]
COPY ["src/Tamma.Activities/Tamma.Activities.csproj", "Tamma.Activities/"]
COPY ["src/Tamma.Core/Tamma.Core.csproj", "Tamma.Core/"]
COPY ["src/Tamma.Data/Tamma.Data.csproj", "Tamma.Data/"]

# Restore dependencies
RUN dotnet restore "Tamma.ElsaServer/Tamma.ElsaServer.csproj"

# Copy source code
COPY src/ .

# Build the project
WORKDIR "/src/Tamma.ElsaServer"
RUN dotnet build "Tamma.ElsaServer.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "Tamma.ElsaServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*
# Story 28-5 — to enable Backup:DeletionBackup (pre-drop tenant pg_dump),
# add postgresql-client so pg_dump resolves on PATH, e.g.:
#   RUN apt-get update && apt-get install -y curl postgresql-client \
#       && rm -rf /var/lib/apt/lists/*
# Left out by default to keep the image lean (the feature ships OFF).

# Create non-root user for security
RUN groupadd -r tamma && useradd -r -g tamma tamma

# Copy published files
COPY --from=publish /app/publish .

# Copy workflow JSON files for seeding at startup
COPY ["workflows/", "/app/workflows/"]

# Create directories for activities and storage
RUN mkdir -p /app/activities /app/storage && \
    chown -R tamma:tamma /app

# Switch to non-root user
USER tamma

# Expose port
EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

# Set environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

# Entry point
ENTRYPOINT ["dotnet", "Tamma.ElsaServer.dll"]
