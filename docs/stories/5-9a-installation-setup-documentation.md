# Story 5.9a: Installation & Setup Documentation

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Drafted  
**MVP Priority**: Critical (MVP Essential)  
**Estimated Complexity**: Medium  
**Target Implementation**: Sprint 5 (MVP Critical)

## Story

As a **developer adopting Tamma**,
I want clear installation and setup documentation,
so that I can quickly install Tamma via npm, Docker, or binaries and complete first-time configuration.

## Acceptance Criteria

### Installation Methods Documentation

- [ ] **Installation via npm documented** with `npm install -g @tamma/cli`, prerequisites, and troubleshooting steps
- [ ] **Installation via Docker documented** with `docker run` commands, Docker Compose setup, and volume mount configurations
- [ ] **Installation via binaries documented** with download links, extraction instructions, and PATH setup for Windows/macOS/Linux
- [ ] **Installation via source build documented** with git clone, dependency installation, and build instructions

### Prerequisites and Requirements

- [ ] **System prerequisites documented** including Node.js 22+, npm 10+, PostgreSQL 17+, and optional Redis
- [ ] **API key requirements documented** for Anthropic Claude, GitHub/GitLab tokens, and other providers
- [ ] **Hardware requirements documented** including minimum RAM, CPU, and disk space recommendations
- [ ] **Network requirements documented** including outbound internet access and firewall considerations

### Initial Configuration and Setup

- [ ] **First-time configuration wizard documented** with `tamma init` command walkthrough and interactive setup
- [ ] **Configuration file structure documented** with `~/.tamma/config.yaml` examples and all available options
- [ ] **Environment variable mapping documented** for containerized and CI/CD deployments
- [ ] **Database setup documented** including PostgreSQL installation, user creation, and migration steps

### Service Mode Setup

- [ ] **Service mode setup documented** for systemd (Linux), Windows Service, and launchd (macOS)
- [ ] **Background daemon configuration documented** with log rotation, restart policies, and monitoring
- [ ] **Multi-user setup documented** including permissions, user accounts, and access control
- [ ] **Production deployment considerations documented** including security hardening and performance tuning

### Troubleshooting and Common Issues

- [ ] **Common installation errors documented** with solutions for Node.js version mismatches, permission errors, and dependency conflicts
- [ ] **Database connection issues documented** with troubleshooting steps for PostgreSQL connection failures and authentication problems
- [ ] **API key and authentication issues documented** with validation steps and common token problems
- [ ] **Platform-specific issues documented** for Windows, macOS, and Linux compatibility problems

## Tasks / Subtasks

- [ ] **Task 1: Create npm installation guide** (AC: 1, 2)
  - [ ] Write npm package installation instructions with global flag
  - [ ] Document Node.js and npm version requirements
  - [ ] Include verification steps to confirm successful installation
  - [ ] Add troubleshooting for npm permission and version conflicts

- [ ] **Task 2: Create Docker installation guide** (AC: 1, 3)
  - [ ] Write Docker Hub pull instructions and image tags
  - [ ] Create Docker Compose example with PostgreSQL service
  - [ ] Document volume mounts for configuration and data persistence
  - [ ] Include network and port configuration examples

- [ ] **Task 3: Create binary installation guide** (AC: 1, 3)
  - [ ] Document GitHub Releases download process for each platform
  - [ ] Write extraction and installation steps for Windows (.exe), macOS (.dmg/.pkg), Linux (.deb/.rpm/.tar.gz)
  - [ ] Include PATH setup instructions for each operating system
  - [ ] Add signature verification and security considerations

- [ ] **Task 4: Create source build guide** (AC: 1, 4)
  - [ ] Document git clone and repository setup
  - [ ] Write pnpm installation and dependency management
  - [ ] Include build commands and output locations
  - [ ] Add development environment setup for contributors

- [ ] **Task 5: Document prerequisites and requirements** (AC: 2, 3)
  - [ ] Create comprehensive prerequisites checklist
  - [ ] Document API key generation for each supported provider
  - [ ] Include hardware and network requirement specifications
  - [ ] Add optional dependencies and their benefits

- [ ] **Task 6: Create configuration wizard documentation** (AC: 4, 5)
  - [ ] Document `tamma init` interactive setup process
  - [ ] Include configuration file examples with comments
  - [ ] Map environment variables to configuration options
  - [ ] Add validation and testing of configuration

- [ ] **Task 7: Document service mode setup** (AC: 6, 7)
  - [ ] Write systemd service file and setup instructions
  - [ ] Document Windows Service installation and management
  - [ ] Include macOS launchd agent configuration
  - [ ] Add log rotation and monitoring setup

- [ ] **Task 8: Create troubleshooting guide** (AC: 8, 9, 10, 11)
  - [ ] Compile common installation errors and solutions
  - [ ] Document database connection troubleshooting steps
  - [ ] Include API key validation and authentication debugging
  - [ ] Add platform-specific compatibility notes and workarounds

## Dev Notes

### Current System State

From `docs/stories/1.5-5-docker-packaging.md`:

- Docker multi-stage builds create optimized images for production deployment
- Container images include all dependencies and runtime requirements

From `docs/stories/1.5-8-npm-package-publishing.md`:

- npm package published as `@tamma/cli` with global installation support
- Package includes CLI entry points and dependency management

From `docs/stories/1.5-9-binary-releases-installers.md`:

- GitHub Actions create platform-specific binaries for Windows, macOS, and Linux
- Release artifacts include executable installers and compressed archives

From `docs/stories/5-1-structured-logging-implementation.md`:

- Structured logging provides debug capabilities for troubleshooting installation issues
- Log levels and correlation IDs aid in problem diagnosis

From `docs/stories/5-2-metrics-collection-infrastructure.md`:

- Health check endpoints available for monitoring service status
- Metrics endpoints help validate successful deployment

### Documentation Structure

```markdown
# Installation & Setup Guide

## Quick Start

- One-command installation for each method
- Verification steps to confirm successful setup

## Installation Methods

### 1. npm Installation

- Global package installation
- Prerequisites and version requirements
- Verification commands

### 2. Docker Installation

- Docker Hub images and tags
- Docker Compose examples
- Volume and network configuration

### 3. Binary Installation

- Platform-specific downloads
- Installation steps for each OS
- PATH configuration

### 4. Source Build

- Development setup
- Build from source
- Contributing guidelines

## Prerequisites

- System requirements
- API key setup
- Database requirements
- Network considerations

## Configuration

- Initial setup wizard
- Configuration file reference
- Environment variables
- Validation and testing

## Service Mode

- Background daemon setup
- Systemd configuration
- Windows Service setup
- macOS launchd setup

## Troubleshooting

- Common installation errors
- Database connection issues
- Authentication problems
- Platform-specific issues
```

### Technical Implementation

#### 1. Installation Scripts and Examples

```bash
#!/bin/bash
# scripts/install.sh - Universal installation script

set -e

# Detect operating system
OS="$(uname -s)"
ARCH="$(uname -m)"

echo "Installing Tamma for $OS ($ARCH)..."

# Check prerequisites
check_prerequisites() {
    echo "Checking prerequisites..."

    # Check Node.js
    if ! command -v node &> /dev/null; then
        echo "Error: Node.js 22+ is required but not installed."
        echo "Please install Node.js from https://nodejs.org/"
        exit 1
    fi

    NODE_VERSION=$(node -v | cut -d'v' -f2 | cut -d'.' -f1)
    if [ "$NODE_VERSION" -lt 22 ]; then
        echo "Error: Node.js 22+ is required. Current version: $(node -v)"
        exit 1
    fi

    # Check npm
    if ! command -v npm &> /dev/null; then
        echo "Error: npm is required but not installed."
        exit 1
    fi

    echo "✓ Prerequisites check passed"
}

# Install via npm
install_npm() {
    echo "Installing via npm..."
    npm install -g @tamma/cli

    # Verify installation
    if command -v tamma &> /dev/null; then
        echo "✓ Tamma installed successfully"
        tamma --version
    else
        echo "✗ Installation failed. Please check your npm configuration."
        exit 1
    fi
}

# Install via Docker
install_docker() {
    echo "Installing via Docker..."

    if ! command -v docker &> /dev/null; then
        echo "Error: Docker is required but not installed."
        echo "Please install Docker from https://docker.com/"
        exit 1
    fi

    docker pull tamma/tamma:latest
    echo "✓ Docker image pulled successfully"

    # Create docker-compose.yml example
    cat > docker-compose.yml << 'EOF'
version: '3.8'
services:
  tamma:
    image: tamma/tamma:latest
    ports:
      - "3000:3000"
    environment:
      - TAMMA_MODE=web
      - DATABASE_URL=postgresql://tamma:password@postgres:5432/tamma
    volumes:
      - ./config:/app/config
      - ./logs:/app/logs
    depends_on:
      - postgres

  postgres:
    image: postgres:17
    environment:
      - POSTGRES_DB=tamma
      - POSTGRES_USER=tamma
      - POSTGRES_PASSWORD=password
    volumes:
      - postgres_data:/var/lib/postgresql/data
    ports:
      - "5432:5432"

volumes:
  postgres_data:
EOF

    echo "✓ docker-compose.yml created"
    echo "Run 'docker-compose up -d' to start Tamma"
}

# Main installation flow
main() {
    check_prerequisites

    echo "Choose installation method:"
    echo "1) npm (recommended for most users)"
    echo "2) Docker (for containerized deployment)"
    echo "3) Binary (for specific platforms)"
    echo "4) Source (for developers)"

    read -p "Enter choice [1-4]: " choice

    case $choice in
        1) install_npm ;;
        2) install_docker ;;
        3) echo "Please visit https://github.com/meywd/tamma/releases for binary downloads" ;;
        4) echo "Please see documentation for build from source instructions" ;;
        *) echo "Invalid choice" && exit 1 ;;
    esac
}

main "$@"
```

#### 2. Configuration File Template

```yaml
# ~/.tamma/config.yaml - Tamma configuration template

# Tamma Settings
mode: 'cli' # cli, service, web, worker
log_level: 'info' # debug, info, warn, error
log_format: 'json' # json, pretty

# Database Configuration
database:
  url: 'postgresql://localhost:5432/tamma'
  pool_size: 10
  timeout: 30000

# AI Provider Configuration
providers:
  anthropic:
    api_key: '${ANTHROPIC_API_KEY}'
    model: 'claude-3-5-sonnet-20241022'
    max_tokens: 100000
    timeout: 60000

  openai:
    api_key: '${OPENAI_API_KEY}'
    model: 'gpt-4'
    max_tokens: 4000
    timeout: 60000

# Git Platform Configuration
platforms:
  github:
    token: '${GITHUB_TOKEN}'
    api_url: 'https://api.github.com'
    webhook_secret: '${GITHUB_WEBHOOK_SECRET}'

  gitlab:
    token: '${GITLAB_TOKEN}'
    api_url: 'https://gitlab.com/api/v4'
    webhook_secret: '${GITLAB_WEBHOOK_SECRET}'

# Service Mode Configuration
service:
  user: 'tamma'
  group: 'tamma'
  working_directory: '/var/lib/tamma'
  log_directory: '/var/log/tamma'
  pid_file: '/var/run/tamma/tamma.pid'

# Web Server Configuration
web:
  host: '0.0.0.0'
  port: 3000
  cors_origins: ['http://localhost:3000']
  session_secret: '${TAMMA_SESSION_SECRET}'

# Metrics and Monitoring
metrics:
  enabled: true
  endpoint: '/metrics'
  port: 9090

# Security Settings
security:
  jwt_secret: '${TAMMA_JWT_SECRET}'
  session_timeout: 3600000 # 1 hour
  rate_limit:
    enabled: true
    requests_per_minute: 100
```

#### 3. Docker Compose Examples

```yaml
# docker-compose.yml - Production deployment
version: '3.8'

services:
  tamma:
    image: tamma/tamma:v0.1.0-alpha
    container_name: tamma
    restart: unless-stopped
    ports:
      - '3000:3000' # Web interface
      - '9090:9090' # Metrics endpoint
    environment:
      - TAMMA_MODE=web
      - TAMMA_LOG_LEVEL=info
      - DATABASE_URL=postgresql://tamma:${POSTGRES_PASSWORD}@postgres:5432/tamma
      - ANTHROPIC_API_KEY=${ANTHROPIC_API_KEY}
      - GITHUB_TOKEN=${GITHUB_TOKEN}
      - TAMMA_JWT_SECRET=${TAMMA_JWT_SECRET}
    volumes:
      - tamma_config:/app/config
      - tamma_logs:/app/logs
      - tamma_workspace:/app/workspace
    depends_on:
      postgres:
        condition: service_healthy
    healthcheck:
      test: ['CMD', 'curl', '-f', 'http://localhost:3000/health']
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 40s

  postgres:
    image: postgres:17-alpine
    container_name: tamma-postgres
    restart: unless-stopped
    environment:
      - POSTGRES_DB=tamma
      - POSTGRES_USER=tamma
      - POSTGRES_PASSWORD=${POSTGRES_PASSWORD}
    volumes:
      - postgres_data:/var/lib/postgresql/data
      - ./init-scripts:/docker-entrypoint-initdb.d
    ports:
      - '5432:5432'
    healthcheck:
      test: ['CMD-SHELL', 'pg_isready -U tamma']
      interval: 10s
      timeout: 5s
      retries: 5

  redis:
    image: redis:7-alpine
    container_name: tamma-redis
    restart: unless-stopped
    ports:
      - '6379:6379'
    volumes:
      - redis_data:/data
    healthcheck:
      test: ['CMD', 'redis-cli', 'ping']
      interval: 10s
      timeout: 3s
      retries: 3

volumes:
  postgres_data:
  redis_data:
  tamma_config:
  tamma_logs:
  tamma_workspace:

networks:
  default:
    name: tamma-network
```

#### 4. Service Configuration Files

```ini
# /etc/systemd/system/tamma.service - systemd service file
[Unit]
Description=Tamma Autonomous Development Platform
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=simple
User=tamma
Group=tamma
WorkingDirectory=/var/lib/tamma
ExecStart=/usr/local/bin/tamma start --mode=service
ExecReload=/bin/kill -HUP $MAINPID
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal
SyslogIdentifier=tamma

# Security settings
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/tamma /var/log/tamma

# Environment
Environment=TAMMA_MODE=service
Environment=TAMMA_LOG_LEVEL=info
Environment=DATABASE_URL=postgresql://tamma:password@localhost:5432/tamma

[Install]
WantedBy=multi-user.target
```

```xml
<!-- tamma-service.xml - Windows Service configuration -->
<service>
  <id>tamma</id>
  <name>Tamma Autonomous Development Platform</name>
  <description>Tamma is an AI-powered autonomous development orchestration platform</description>
  <executable>C:\Program Files\Tamma\tamma.exe</executable>
  <arguments>start --mode=service</arguments>
  <logmode>rotate</logmode>
  <depend>postgresql-x64-17</depend>
  <startmode>Automatic</startmode>
  <waithint>10</waithint>

  <env name="TAMMA_MODE" value="service"/>
  <env name="TAMMA_LOG_LEVEL" value="info"/>
  <env name="DATABASE_URL" value="postgresql://tamma:password@localhost:5432/tamma"/>

  <serviceaccount>
    <domain>NT AUTHORITY\LocalService</domain>
    <user>LocalService</user>
    <password></password>
  </serviceaccount>

  <workingdirectory>C:\ProgramData\Tamma</workingdirectory>
  <logpath>C:\ProgramData\Tamma\logs</logpath>
</service>
```

```plist
<!-- com.tamma.daemon.plist - macOS launchd agent -->
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.tamma.daemon</string>

    <key>ProgramArguments</key>
    <array>
        <string>/usr/local/bin/tamma</string>
        <string>start</string>
        <string>--mode=service</string>
    </array>

    <key>WorkingDirectory</key>
    <string>/var/lib/tamma</string>

    <key>RunAtLoad</key>
    <true/>

    <key>KeepAlive</key>
    <true/>

    <key>StandardOutPath</key>
    <string>/var/log/tamma/tamma.log</string>

    <key>StandardErrorPath</key>
    <string>/var/log/tamma/tamma.error.log</string>

    <key>EnvironmentVariables</key>
    <dict>
        <key>TAMMA_MODE</key>
        <string>service</string>
        <key>TAMMA_LOG_LEVEL</key>
        <string>info</string>
        <key>DATABASE_URL</key>
        <string>postgresql://tamma:password@localhost:5432/tamma</string>
    </dict>

    <key>UserName</key>
    <string>tamma</string>

    <key>GroupName</key>
    <string>tamma</string>
</dict>
</plist>
```

### Project Structure Notes

- Documentation files in `docs/installation/` directory with clear organization
- Installation scripts in `scripts/` directory for automated setup
- Configuration templates in `config/templates/` with examples
- Service configuration files in `config/services/` for each platform
- Docker examples in `docker/` directory with compose files

### References

- [Source: docs/tech-spec-epic-5.md#Story-5.9a-Installation-Setup-Documentation]
- [Source: docs/epics.md#Story-5.9a-Installation-Setup-Documentation]
- [Source: docs/stories/1.5-5-docker-packaging.md]
- [Source: docs/stories/1.5-8-npm-package-publishing.md]
- [Source: docs/stories/1.5-9-binary-releases-installers.md]
- [Source: docs/architecture.md#Deployment-Modes]

## Dev Agent Record

### Context Reference

<!-- Path(s) to story context XML will be added here by context workflow -->

### Agent Model Used

claude-3-5-sonnet-20241022

### Debug Log References

### Completion Notes List

### File List
