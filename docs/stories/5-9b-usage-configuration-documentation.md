# Story 5.9b: Usage & Configuration Documentation

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Drafted  
**MVP Priority**: Critical (MVP Essential)  
**Estimated Complexity**: Medium  
**Target Implementation**: Sprint 5 (MVP Critical)

## Story

As a **Tamma operator**,
I want comprehensive usage and configuration documentation,
so that I can configure AI providers, Git platforms, and operate Tamma effectively.

## Acceptance Criteria

### CLI Command Reference

- [ ] **All `tamma` commands documented** with syntax, options, and practical examples
- [ ] **Command help system documented** with `tamma --help` and `tamma <command> --help` usage
- [ ] **Command examples provided** for common workflows and use cases
- [ ] **Command exit codes documented** for scripting and automation

### Configuration File Reference

- [ ] **All configuration options documented** with descriptions, default values, and validation rules
- [ ] **Configuration file structure documented** with hierarchical organization and inheritance
- [ ] **Configuration examples provided** for different deployment scenarios
- [ ] **Configuration validation documented** with error messages and troubleshooting

### AI Provider Setup Guides

- [ ] **Anthropic Claude setup documented** with API key generation, model selection, and configuration
- [ ] **OpenAI setup documented** with API key setup, model options, and rate limit considerations
- [ ] **GitHub Copilot setup documented** with authentication, model selection, and integration steps
- [ ] **Local LLM setup documented** with Ollama, LM Studio, and custom model configuration

### Git Platform Setup Guides

- [ ] **GitHub setup documented** with token scopes, webhook configuration, and repository permissions
- [ ] **GitLab setup documented** with access tokens, webhook setup, and project configuration
- [ ] **Platform-specific considerations documented** for API limits, rate limiting, and best practices

### Deployment Modes Documentation

- [ ] **CLI mode documented** with interactive usage, command-line options, and session management
- [ ] **Service mode documented** with background daemon configuration, log management, and process control
- [ ] **Web mode documented** with REST API usage, authentication, and web interface access
- [ ] **Worker mode documented** with CI/CD integration, batch processing, and automation

### Webhook Configuration

- [ ] **GitHub webhook setup documented** with URL configuration, secret management, and event filtering
- [ ] **GitLab webhook setup documented** with webhook endpoints, token configuration, and event selection
- [ ] **Webhook security documented** with signature verification, SSL requirements, and access control
- [ ] **Webhook troubleshooting documented** with common issues and debugging steps

### Environment Variables

- [ ] **All environment variables documented** with names, purposes, and example values
- [ ] **Environment variable precedence documented** with override rules and inheritance
- [ ] **Security considerations documented** for sensitive variables and secret management
- [ ] **Container environment variables documented** for Docker and Kubernetes deployments

## Tasks / Subtasks

- [ ] **Task 1: Create CLI command reference** (AC: 1, 2, 3, 4)
  - [ ] Document all available commands with syntax and options
  - [ ] Create usage examples for each command
  - [ ] Document help system and command discovery
  - [ ] Include exit codes and scripting examples

- [ ] **Task 2: Create configuration file reference** (AC: 5, 6, 7, 8)
  - [ ] Document all configuration sections and options
  - [ ] Provide configuration file examples for different scenarios
  - [ ] Document validation rules and error handling
  - [ ] Include configuration inheritance and precedence

- [ ] **Task 3: Create AI provider setup guides** (AC: 9, 10, 11, 12)
  - [ ] Write Anthropic Claude setup guide with API key generation
  - [ ] Create OpenAI setup documentation with model selection
  - [ ] Document GitHub Copilot integration steps
  - [ ] Write local LLM setup guide for Ollama and LM Studio

- [ ] **Task 4: Create Git platform setup guides** (AC: 13, 14, 15)
  - [ ] Document GitHub token setup and required scopes
  - [ ] Create GitLab access token configuration guide
  - [ ] Include platform-specific best practices and limitations

- [ ] **Task 5: Document deployment modes** (AC: 16, 17, 18, 19)
  - [ ] Write CLI mode usage guide with examples
  - [ ] Document service mode configuration and management
  - [ ] Create web mode API usage documentation
  - [ ] Write worker mode CI/CD integration guide

- [ ] **Task 6: Create webhook configuration guide** (AC: 20, 21, 22, 23)
  - [ ] Document GitHub webhook setup and configuration
  - [ ] Create GitLab webhook setup instructions
  - [ ] Include webhook security best practices
  - [ ] Add webhook troubleshooting and debugging guide

- [ ] **Task 7: Document environment variables** (AC: 24, 25, 26, 27)
  - [ ] Create comprehensive environment variable reference
  - [ ] Document variable precedence and override behavior
  - [ ] Include security considerations for sensitive variables
  - [ ] Add container-specific environment variable examples

## Dev Notes

### Current System State

From `docs/stories/1.3-provider-configuration-management.md`:

- Multi-provider configuration system with unified interface
- Provider-specific configuration options and validation
- Environment variable override capabilities

From `docs/stories/1.7-git-platform-configuration-management.md`:

- Multi-platform Git configuration with platform-specific settings
- Token management and authentication configuration
- Webhook setup and event filtering capabilities

From `docs/stories/1.5-2-cli-mode-enhancement.md`:

- Enhanced CLI with interactive mode and command structure
- Help system and command discovery features
- Session management and configuration persistence

From `docs/stories/1.5-6-webhook-integration.md`:

- Webhook endpoint configuration and management
- Event filtering and routing capabilities
- Security features including signature verification

From `docs/stories/1.8-hybrid-orchestrator-worker-architecture-design.md`:

- Multiple deployment modes: CLI, service, web, worker
- Mode-specific configuration and operational considerations
- Process management and monitoring capabilities

### Documentation Structure

```markdown
# Usage & Configuration Guide

## Quick Start

- Basic setup and first run
- Common configuration patterns
- Example workflows

## CLI Reference

### Core Commands

- tamma run - Start autonomous development
- tamma init - Initialize configuration
- tamma config - Manage configuration
- tamma start - Start service mode
- tamma stop - Stop service mode
- tamma status - Check system status

### Utility Commands

- tamma --help - Show help
- tamma version - Show version
- tamma doctor - Health check
- tamma logs - View logs
- tamma config --validate - Validate configuration

## Configuration Reference

### File Structure

- ~/.tamma/config.yaml
- Environment-specific configs
- Configuration inheritance

### Core Settings

- mode, log_level, database
- providers, platforms
- security, performance

### Provider Configuration

- AI provider settings
- Git platform settings
- Authentication and tokens

## Deployment Modes

### CLI Mode

- Interactive usage
- Command-line options
- Session management

### Service Mode

- Background daemon
- Process management
- Log rotation

### Web Mode

- REST API usage
- Authentication
- Web interface

### Worker Mode

- CI/CD integration
- Batch processing
- Automation

## Provider Setup

### AI Providers

- Anthropic Claude
- OpenAI
- GitHub Copilot
- Local LLMs

### Git Platforms

- GitHub
- GitLab
- Authentication
- Webhooks

## Webhook Configuration

### GitHub Webhooks

- Setup and configuration
- Event filtering
- Security

### GitLab Webhooks

- Setup and configuration
- Event filtering
- Security

## Environment Variables

### Core Variables

- TAMMA_MODE, DATABASE_URL
- LOG_LEVEL, CONFIG_PATH

### Provider Variables

- ANTHROPIC_API_KEY
- OPENAI_API_KEY
- GITHUB_TOKEN
- GITLAB_TOKEN

### Security Variables

- JWT_SECRET, WEBHOOK_SECRET
- Encryption keys and certificates
```

### Technical Implementation

#### 1. CLI Command Reference

```bash
# Core Commands

## tamma run - Start autonomous development
tamma run [options] [issue-url]

Options:
  --mode <mode>          Deployment mode: cli, service, web, worker (default: cli)
  --config <path>         Configuration file path (default: ~/.tamma/config.yaml)
  --log-level <level>     Log level: debug, info, warn, error (default: info)
  --dry-run              Simulate execution without making changes
  --interactive           Interactive mode with approval checkpoints
  --issue <url>          Specific issue to process
  --repo <url>           Repository to work on
  --provider <name>       AI provider to use
  --platform <name>       Git platform to use

Examples:
  # Start interactive mode
  tamma run --interactive

  # Process specific issue
  tamma run --issue https://github.com/user/repo/issues/123

  # Dry run simulation
  tamma run --dry-run --issue https://github.com/user/repo/issues/123

  # Use specific provider and platform
  tamma run --provider anthropic --platform github

## tamma init - Initialize configuration
tamma init [options]

Options:
  --interactive           Interactive configuration wizard
  --template <name>      Use predefined template
  --force               Overwrite existing configuration
  --defaults            Use default values for all options

Examples:
  # Interactive setup
  tamma init --interactive

  # Use template
  tamma init --template production

  # Quick setup with defaults
  tamma init --defaults

## tamma config - Manage configuration
tamma config <action> [key] [value]

Actions:
  get <key>            Get configuration value
  set <key> <value>    Set configuration value
  delete <key>          Delete configuration value
  list                  List all configuration
  validate              Validate configuration
  reset                 Reset to defaults

Examples:
  # Get configuration value
  tamma config get providers.anthropic.model

  # Set configuration value
  tamma config set providers.anthropic.model claude-3-5-sonnet-20241022

  # List all configuration
  tamma config list

  # Validate configuration
  tamma config validate

## tamma start - Start service mode
tamma start [options]

Options:
  --mode <mode>          Service mode: service, web, worker
  --config <path>         Configuration file path
  --daemon               Run as daemon (background)
  --pid-file <path>      PID file path
  --user <user>          Run as specified user
  --group <group>        Run as specified group

Examples:
  # Start service daemon
  tamma start --mode service --daemon

  # Start web server
  tamma start --mode web --port 3000

  # Start worker for CI/CD
  tamma start --mode worker --batch

## tamma status - Check system status
tamma status [options]

Options:
  --format <format>      Output format: json, yaml, table (default: table)
  --verbose              Show detailed status
  --health-check          Run comprehensive health check

Examples:
  # Basic status
  tamma status

  # JSON output for scripting
  tamma status --format json

  # Detailed health check
  tamma status --verbose --health-check

## tamma logs - View logs
tamma logs [options]

Options:
  --follow               Follow log output (tail -f)
  --lines <number>       Number of lines to show (default: 50)
  --level <level>        Filter by log level
  --since <time>        Show logs since time
  --grep <pattern>       Filter by pattern
  --format <format>      Output format: json, pretty (default: pretty)

Examples:
  # Follow logs
  tamma logs --follow

  # Show last 100 lines
  tamma logs --lines 100

  # Filter by error level
  tamma logs --level error

  # Search for specific pattern
  tamma logs --grep "authentication"

## tamma doctor - Health check and diagnostics
tamma doctor [options]

Options:
  --fix                  Attempt to fix issues automatically
  --verbose              Show detailed diagnostic information
  --check <component>     Check specific component only

Examples:
  # Run health check
  tamma doctor

  # Auto-fix issues
  tamma doctor --fix

  # Check specific component
  tamma doctor --check database
```

#### 2. Configuration File Reference

```yaml
# ~/.tamma/config.yaml - Complete configuration reference

# Core Tamma Settings
mode: 'cli' # Deployment mode: cli, service, web, worker
log_level: 'info' # Log level: debug, info, warn, error
log_format: 'json' # Log format: json, pretty
config_version: '1.0' # Configuration file version

# Database Configuration
database:
  url: 'postgresql://localhost:5432/tamma'
  pool_size: 10 # Connection pool size
  timeout: 30000 # Connection timeout (ms)
  ssl: false # SSL connection
  max_connections: 20 # Max concurrent connections

# AI Provider Configuration
providers:
  # Anthropic Claude
  anthropic:
    enabled: true
    api_key: '${ANTHROPIC_API_KEY}'
    model: 'claude-3-5-sonnet-20241022'
    max_tokens: 100000
    temperature: 0.7
    timeout: 60000 # Request timeout (ms)
    retry_attempts: 3
    retry_delay: 1000 # Retry delay (ms)

  # OpenAI
  openai:
    enabled: false
    api_key: '${OPENAI_API_KEY}'
    model: 'gpt-4'
    max_tokens: 4000
    temperature: 0.7
    timeout: 60000
    retry_attempts: 3
    retry_delay: 1000

  # GitHub Copilot
  github_copilot:
    enabled: false
    token: '${GITHUB_COPILOT_TOKEN}'
    model: 'copilot'
    timeout: 60000
    retry_attempts: 3

  # Local LLM (Ollama)
  ollama:
    enabled: false
    base_url: 'http://localhost:11434'
    model: 'llama2'
    timeout: 120000 # Longer timeout for local models
    retry_attempts: 1 # No retry for local models

# Git Platform Configuration
platforms:
  # GitHub
  github:
    enabled: true
    token: '${GITHUB_TOKEN}'
    api_url: 'https://api.github.com'
    webhook_url: 'https://your-domain.com/webhooks/github'
    webhook_secret: '${GITHUB_WEBHOOK_SECRET}'
    default_branch: 'main'
    timeout: 30000
    rate_limit_rpm: 5000 # Rate limit per minute

  # GitLab
  gitlab:
    enabled: false
    token: '${GITLAB_TOKEN}'
    api_url: 'https://gitlab.com/api/v4'
    webhook_url: 'https://your-domain.com/webhooks/gitlab'
    webhook_secret: '${GITLAB_WEBHOOK_SECRET}'
    default_branch: 'main'
    timeout: 30000
    rate_limit_rpm: 3000

# Web Server Configuration (for web mode)
web:
  host: '0.0.0.0'
  port: 3000
  cors_origins: ['http://localhost:3000', 'https://your-domain.com']
  session_secret: '${TAMMA_SESSION_SECRET}'
  session_timeout: 3600000 # 1 hour in ms
  max_file_size: 10485760 # 10MB
  upload_timeout: 30000 # 30 seconds

# Service Configuration (for service mode)
service:
  user: 'tamma'
  group: 'tamma'
  working_directory: '/var/lib/tamma'
  log_directory: '/var/log/tamma'
  pid_file: '/var/run/tamma/tamma.pid'
  max_memory: '1G'
  max_cpu: '2'
  restart_policy: 'always'
  health_check_interval: 30000 # 30 seconds

# Worker Configuration (for worker mode)
worker:
  batch_size: 10 # Issues per batch
  poll_interval: 60000 # 1 minute
  max_concurrent_jobs: 5
  timeout: 3600000 # 1 hour per job
  retry_attempts: 3
  retry_delay: 60000 # 1 minute

# Security Configuration
security:
  jwt_secret: '${TAMMA_JWT_SECRET}'
  encryption_key: '${TAMMA_ENCRYPTION_KEY}'
  session_timeout: 3600000 # 1 hour
  max_login_attempts: 5
  lockout_duration: 900000 # 15 minutes
  rate_limit:
    enabled: true
    requests_per_minute: 100
    burst_size: 20

# Metrics and Monitoring
metrics:
  enabled: true
  endpoint: '/metrics'
  port: 9090
  collection_interval: 15000 # 15 seconds
  retention_days: 30

# Notification Configuration
notifications:
  email:
    enabled: false
    smtp_host: '${SMTP_HOST}'
    smtp_port: 587
    username: '${SMTP_USERNAME}'
    password: '${SMTP_PASSWORD}'
    from_address: 'noreply@your-domain.com'

  slack:
    enabled: false
    webhook_url: '${SLACK_WEBHOOK_URL}'
    channel: '#tamma-alerts'

  discord:
    enabled: false
    webhook_url: '${DISCORD_WEBHOOK_URL}'

# Development Configuration
development:
  debug_mode: false
  mock_apis: false
  verbose_logging: false
  enable_profiling: false
  hot_reload: false
```

#### 3. Environment Variables Reference

```bash
# Core Environment Variables
export TAMMA_MODE="cli"                    # Deployment mode
export TAMMA_LOG_LEVEL="info"               # Log level
export TAMMA_LOG_FORMAT="json"              # Log format
export TAMMA_CONFIG_PATH="/path/to/config"  # Config file path
export TAMMA_DATA_DIR="/path/to/data"        # Data directory

# Database
export DATABASE_URL="postgresql://user:pass@host:5432/db"
export DATABASE_POOL_SIZE="10"
export DATABASE_TIMEOUT="30000"

# AI Provider API Keys
export ANTHROPIC_API_KEY="your-anthropic-api-key"
export OPENAI_API_KEY="your-openai-api-key"
export GITHUB_COPILOT_TOKEN="your-copilot-token"

# Git Platform Tokens
export GITHUB_TOKEN="your-github-token"
export GITLAB_TOKEN="your-gitlab-token"
export GITHUB_WEBHOOK_SECRET="your-webhook-secret"
export GITLAB_WEBHOOK_SECRET="your-gitlab-webhook-secret"

# Security
export TAMMA_JWT_SECRET="your-jwt-secret"
export TAMMA_ENCRYPTION_KEY="your-encryption-key"
export TAMMA_SESSION_SECRET="your-session-secret"

# Web Server
export TAMMA_WEB_HOST="0.0.0.0"
export TAMMA_WEB_PORT="3000"
export TAMMA_CORS_ORIGINS="http://localhost:3000"

# Service Mode
export TAMMA_SERVICE_USER="tamma"
export TAMMA_SERVICE_GROUP="tamma"
export TAMMA_PID_FILE="/var/run/tamma/tamma.pid"

# Metrics
export TAMMA_METRICS_ENABLED="true"
export TAMMA_METRICS_PORT="9090"
export TAMMA_METRICS_ENDPOINT="/metrics"

# Notifications
export SMTP_HOST="smtp.gmail.com"
export SMTP_PORT="587"
export SMTP_USERNAME="your-email@gmail.com"
export SMTP_PASSWORD="your-app-password"
export SLACK_WEBHOOK_URL="https://hooks.slack.com/..."
export DISCORD_WEBHOOK_URL="https://discord.com/api/webhooks/..."

# Development
export TAMMA_DEBUG="false"
export TAMMA_MOCK_APIS="false"
export TAMMA_VERBOSE_LOGGING="false"
```

#### 4. Provider Setup Examples

```bash
#!/bin/bash
# scripts/setup-providers.sh - Provider setup automation

setup_anthropic() {
    echo "Setting up Anthropic Claude..."

    # Check if API key exists
    if [ -z "$ANTHROPIC_API_KEY" ]; then
        echo "Anthropic API key not found. Please set ANTHROPIC_API_KEY environment variable."
        echo "Get your API key from: https://console.anthropic.com/"
        return 1
    fi

    # Test API key
    echo "Testing Anthropic API key..."
    curl -s -X POST https://api.anthropic.com/v1/messages \
        -H "Content-Type: application/json" \
        -H "x-api-key: $ANTHROPIC_API_KEY" \
        -d '{
            "model": "claude-3-5-sonnet-20241022",
            "max_tokens": 10,
            "messages": [{"role": "user", "content": "test"}]
        }' > /dev/null

    if [ $? -eq 0 ]; then
        echo "✓ Anthropic API key is valid"

        # Update configuration
        tamma config set providers.anthropic.enabled true
        tamma config set providers.anthropic.api_key "\${ANTHROPIC_API_KEY}"
        tamma config set providers.anthropic.model claude-3-5-sonnet-20241022

        echo "✓ Anthropic provider configured"
    else
        echo "✗ Anthropic API key is invalid"
        return 1
    fi
}

setup_openai() {
    echo "Setting up OpenAI..."

    if [ -z "$OPENAI_API_KEY" ]; then
        echo "OpenAI API key not found. Please set OPENAI_API_KEY environment variable."
        echo "Get your API key from: https://platform.openai.com/api-keys"
        return 1
    fi

    # Test API key
    echo "Testing OpenAI API key..."
    curl -s -X POST https://api.openai.com/v1/chat/completions \
        -H "Content-Type: application/json" \
        -H "Authorization: Bearer $OPENAI_API_KEY" \
        -d '{
            "model": "gpt-4",
            "max_tokens": 10,
            "messages": [{"role": "user", "content": "test"}]
        }' > /dev/null

    if [ $? -eq 0 ]; then
        echo "✓ OpenAI API key is valid"

        # Update configuration
        tamma config set providers.openai.enabled true
        tamma config set providers.openai.api_key "\${OPENAI_API_KEY}"
        tamma config set providers.openai.model gpt-4

        echo "✓ OpenAI provider configured"
    else
        echo "✗ OpenAI API key is invalid"
        return 1
    fi
}

setup_github() {
    echo "Setting up GitHub..."

    if [ -z "$GITHUB_TOKEN" ]; then
        echo "GitHub token not found. Please set GITHUB_TOKEN environment variable."
        echo "Create a token at: https://github.com/settings/tokens"
        echo "Required scopes: repo, admin:repo_hook, read:org"
        return 1
    fi

    # Test token
    echo "Testing GitHub token..."
    curl -s -H "Authorization: token $GITHUB_TOKEN" \
        https://api.github.com/user > /dev/null

    if [ $? -eq 0 ]; then
        echo "✓ GitHub token is valid"

        # Update configuration
        tamma config set platforms.github.enabled true
        tamma config set platforms.github.token "\${GITHUB_TOKEN}"
        tamma config set platforms.github.api_url "https://api.github.com"

        echo "✓ GitHub platform configured"
    else
        echo "✗ GitHub token is invalid"
        return 1
    fi
}

setup_ollama() {
    echo "Setting up Ollama (local LLM)..."

    # Check if Ollama is running
    if ! curl -s http://localhost:11434/api/tags > /dev/null; then
        echo "Ollama is not running. Please start Ollama first."
        echo "Install Ollama from: https://ollama.ai/"
        return 1
    fi

    # Get available models
    echo "Available Ollama models:"
    curl -s http://localhost:11434/api/tags | jq -r '.models[].name'

    # Configure Ollama
    tamma config set providers.ollama.enabled true
    tamma config set providers.ollama.base_url "http://localhost:11434"

    read -p "Enter Ollama model name: " model
    if [ -n "$model" ]; then
        tamma config set providers.ollama.model "$model"
        echo "✓ Ollama configured with model: $model"
    fi
}

# Main setup flow
main() {
    echo "Tamma Provider Setup"
    echo "==================="
    echo ""

    echo "Available providers:"
    echo "1) Anthropic Claude"
    echo "2) OpenAI"
    echo "3) GitHub"
    echo "4) Ollama (Local LLM)"
    echo "5) All providers"
    echo ""

    read -p "Select provider to setup [1-5]: " choice

    case $choice in
        1) setup_anthropic ;;
        2) setup_openai ;;
        3) setup_github ;;
        4) setup_ollama ;;
        5)
            setup_anthropic && setup_openai && setup_github && setup_ollama
            ;;
        *)
            echo "Invalid choice"
            exit 1
            ;;
    esac

    echo ""
    echo "Setup complete! Run 'tamma config validate' to verify configuration."
}

main "$@"
```

### Project Structure Notes

- Documentation files in `docs/usage/` directory with clear organization
- CLI help system integrated with command structure
- Configuration validation and testing utilities
- Provider setup scripts for automation
- Example configurations for different deployment scenarios

### References

- [Source: docs/tech-spec-epic-5.md#Story-5.9b-Usage-Configuration-Documentation]
- [Source: docs/epics.md#Story-5.9b-Usage-Configuration-Documentation]
- [Source: docs/stories/1.3-provider-configuration-management.md]
- [Source: docs/stories/1.7-git-platform-configuration-management.md]
- [Source: docs/stories/1.5-2-cli-mode-enhancement.md]
- [Source: docs/stories/1.5-6-webhook-integration.md]

## Dev Agent Record

### Context Reference

<!-- Path(s) to story context XML will be added here by context workflow -->

### Agent Model Used

claude-3-5-sonnet-20241022

### Debug Log References

### Completion Notes List

### File List
