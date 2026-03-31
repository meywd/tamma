# =============================================================================
# Tamma Studio — Multi-stage Dockerfile
#
# Build context: apps/tamma-elsa/src  (same level as Tamma.Studio/ directory)
#
# Stage 1: dotnet SDK — restore, build, publish Blazor WASM
# Stage 2: nginx:alpine — serve static files (~30-40MB final image)
# =============================================================================

# ---------------------------------------------------------------------------
# Stage 1: Build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy only the csproj for layer caching on restore
COPY Tamma.Studio/Tamma.Studio.csproj ./Tamma.Studio/

# Restore NuGet packages (cached unless csproj changes)
RUN dotnet restore Tamma.Studio/Tamma.Studio.csproj

# Copy full source
COPY Tamma.Studio/ ./Tamma.Studio/

# Publish in Release mode — produces wwwroot/ with all WASM assets
WORKDIR /src/Tamma.Studio
RUN dotnet publish Tamma.Studio.csproj \
    -c Release \
    -o /app/publish \
    /p:BlazorEnableCompression=true

# ---------------------------------------------------------------------------
# Stage 2: Runtime (nginx serving static files)
# ---------------------------------------------------------------------------
FROM nginx:1.27-alpine AS final

# Remove default nginx site
RUN rm -rf /usr/share/nginx/html/*

# Copy published Blazor WASM assets
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html

# Copy nginx configuration
COPY Tamma.Studio/nginx.conf /etc/nginx/conf.d/default.conf

# Copy entrypoint script for env var injection
COPY Tamma.Studio/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# Expose port 80 (nginx default)
EXPOSE 80

# Entrypoint: inject env vars, then start nginx
ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["nginx", "-g", "daemon off;"]
