#!/bin/bash

# PgBouncer Setup Script for Test Platform
# Story: 1.1 Database Schema Migration System
# Task: 1 - Database Setup and Configuration
# Subtask: 1.3 - Configure connection pooling with PgBouncer

set -e  # Exit on error

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Function to print colored messages
print_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

print_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Load database configuration if available
if [ -f ".env.database" ]; then
    source .env.database
    print_info "Loaded database configuration from .env.database"
fi

# Configuration variables
DB_NAME="${DB_NAME:-test_platform}"
DB_USER="${DB_USER:-test_platform_user}"
DB_PASSWORD="${DB_PASSWORD:-}"
DB_HOST="${DB_HOST:-localhost}"
DB_PORT="${DB_PORT:-5432}"
PGBOUNCER_PORT="${PGBOUNCER_PORT:-6432}"
PGBOUNCER_CONFIG_DIR="${PGBOUNCER_CONFIG_DIR:-/etc/pgbouncer}"

# Function to detect OS
detect_os() {
    if [[ "$OSTYPE" == "linux-gnu"* ]]; then
        if [ -f /etc/debian_version ]; then
            echo "debian"
        elif [ -f /etc/redhat-release ]; then
            echo "redhat"
        else
            echo "linux"
        fi
    elif [[ "$OSTYPE" == "darwin"* ]]; then
        echo "macos"
    else
        echo "unknown"
    fi
}

# Function to check if PgBouncer is installed
check_pgbouncer_installed() {
    if command -v pgbouncer &> /dev/null; then
        print_info "PgBouncer is already installed"
        pgbouncer -V
        return 0
    else
        return 1
    fi
}

# Function to install PgBouncer on Debian/Ubuntu
install_pgbouncer_debian() {
    print_info "Installing PgBouncer on Debian/Ubuntu..."

    sudo apt-get update
    sudo apt-get install -y pgbouncer

    # Stop PgBouncer service after installation (we'll configure it first)
    sudo systemctl stop pgbouncer || true

    print_info "PgBouncer installed successfully"
}

# Function to install PgBouncer on macOS
install_pgbouncer_macos() {
    print_info "Installing PgBouncer on macOS..."

    if ! command -v brew &> /dev/null; then
        print_error "Homebrew is not installed. Please install Homebrew first."
        exit 1
    fi

    brew install pgbouncer

    # Create directories for macOS
    PGBOUNCER_CONFIG_DIR="/opt/homebrew/etc"
    mkdir -p /opt/homebrew/var/log

    print_info "PgBouncer installed successfully"
}

# Function to generate MD5 password hash
generate_md5_password() {
    local user=$1
    local password=$2
    echo -n "md5$(echo -n "${password}${user}" | md5sum | cut -d' ' -f1)"
}

# Function to configure PgBouncer
configure_pgbouncer() {
    print_info "Configuring PgBouncer..."

    # Check if password is set
    if [ -z "$DB_PASSWORD" ]; then
        print_error "Database password not found. Please run postgresql-setup.sh first or set DB_PASSWORD."
        exit 1
    fi

    # Create log directory
    sudo mkdir -p /var/log/pgbouncer
    sudo mkdir -p /var/run/pgbouncer

    # Set proper permissions based on OS
    if [[ "$(detect_os)" == "debian" ]]; then
        sudo chown postgres:postgres /var/log/pgbouncer
        sudo chown postgres:postgres /var/run/pgbouncer
    fi

    # Backup existing configuration if it exists
    if [ -f "$PGBOUNCER_CONFIG_DIR/pgbouncer.ini" ]; then
        sudo cp "$PGBOUNCER_CONFIG_DIR/pgbouncer.ini" "$PGBOUNCER_CONFIG_DIR/pgbouncer.ini.backup.$(date +%Y%m%d%H%M%S)"
    fi

    # Copy our configuration template
    sudo cp pgbouncer-config.ini "$PGBOUNCER_CONFIG_DIR/pgbouncer.ini"

    # Update database connection in configuration
    sudo sed -i.bak "s/test_platform = .*/test_platform = host=$DB_HOST port=$DB_PORT dbname=$DB_NAME/" "$PGBOUNCER_CONFIG_DIR/pgbouncer.ini" 2>/dev/null || \
    sudo sed -i '' "s/test_platform = .*/test_platform = host=$DB_HOST port=$DB_PORT dbname=$DB_NAME/" "$PGBOUNCER_CONFIG_DIR/pgbouncer.ini"

    # Create userlist.txt with MD5 hashed password
    local md5_password=$(generate_md5_password "$DB_USER" "$DB_PASSWORD")
    echo "\"$DB_USER\" \"$md5_password\"" | sudo tee "$PGBOUNCER_CONFIG_DIR/userlist.txt" > /dev/null

    # Also add postgres user if we have the password
    if [ ! -z "$POSTGRES_PASSWORD" ]; then
        local postgres_md5=$(generate_md5_password "postgres" "$POSTGRES_PASSWORD")
        echo "\"postgres\" \"$postgres_md5\"" | sudo tee -a "$PGBOUNCER_CONFIG_DIR/userlist.txt" > /dev/null
    fi

    # Set proper permissions on userlist.txt
    sudo chmod 600 "$PGBOUNCER_CONFIG_DIR/userlist.txt"
    if [[ "$(detect_os)" == "debian" ]]; then
        sudo chown postgres:postgres "$PGBOUNCER_CONFIG_DIR/userlist.txt"
    fi

    print_info "PgBouncer configuration completed"
}

# Function to start PgBouncer service
start_pgbouncer() {
    print_info "Starting PgBouncer service..."

    if [[ "$(detect_os)" == "debian" ]]; then
        # Enable and start systemd service
        sudo systemctl enable pgbouncer
        sudo systemctl start pgbouncer

        # Check status
        if sudo systemctl is-active --quiet pgbouncer; then
            print_info "PgBouncer service started successfully"
        else
            print_error "Failed to start PgBouncer service"
            sudo systemctl status pgbouncer --no-pager || true
            return 1
        fi
    elif [[ "$(detect_os)" == "macos" ]]; then
        # Create LaunchAgent for macOS
        cat > ~/Library/LaunchAgents/pgbouncer.plist <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>pgbouncer</string>
    <key>ProgramArguments</key>
    <array>
        <string>/opt/homebrew/bin/pgbouncer</string>
        <string>/opt/homebrew/etc/pgbouncer.ini</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardErrorPath</key>
    <string>/opt/homebrew/var/log/pgbouncer_error.log</string>
    <key>StandardOutPath</key>
    <string>/opt/homebrew/var/log/pgbouncer.log</string>
</dict>
</plist>
EOF
        launchctl load ~/Library/LaunchAgents/pgbouncer.plist
        launchctl start pgbouncer

        print_info "PgBouncer service started successfully"
    else
        # Manual start for other systems
        pgbouncer -d "$PGBOUNCER_CONFIG_DIR/pgbouncer.ini"
        print_info "PgBouncer started in daemon mode"
    fi
}

# Function to test PgBouncer connection
test_pgbouncer_connection() {
    print_info "Testing PgBouncer connection..."

    # Wait for PgBouncer to be ready
    sleep 2

    # Test connection through PgBouncer
    export PGPASSWORD=$DB_PASSWORD
    if psql -h localhost -p $PGBOUNCER_PORT -U $DB_USER -d $DB_NAME -c "SELECT 'PgBouncer connection successful' as status;" 2>/dev/null; then
        print_info "Successfully connected to database through PgBouncer"
    else
        print_error "Failed to connect through PgBouncer"
        print_info "Checking PgBouncer logs..."
        if [ -f "/var/log/pgbouncer/pgbouncer.log" ]; then
            sudo tail -20 /var/log/pgbouncer/pgbouncer.log
        fi
        return 1
    fi
    unset PGPASSWORD

    # Show PgBouncer statistics (requires admin access)
    print_info "PgBouncer pool statistics:"
    export PGPASSWORD=$DB_PASSWORD
    psql -h localhost -p $PGBOUNCER_PORT -U $DB_USER -d pgbouncer -c "SHOW POOLS;" 2>/dev/null || \
    print_warn "Could not retrieve pool statistics (admin access required)"
    unset PGPASSWORD
}

# Function to create systemd service file for better control
create_systemd_service() {
    if [[ "$(detect_os)" != "debian" ]]; then
        return
    fi

    print_info "Creating systemd service file..."

    sudo tee /etc/systemd/system/pgbouncer.service > /dev/null <<EOF
[Unit]
Description=PgBouncer connection pooler
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=notify
User=postgres
Group=postgres
ExecStart=/usr/sbin/pgbouncer /etc/pgbouncer/pgbouncer.ini
ExecReload=/bin/kill -HUP \$MAINPID
KillSignal=SIGTERM
Restart=on-failure
RestartSec=10s

# Security settings
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=/var/log/pgbouncer /var/run/pgbouncer

[Install]
WantedBy=multi-user.target
EOF

    sudo systemctl daemon-reload
    print_info "Systemd service file created"
}

# Function to show connection details
show_connection_details() {
    print_info "PgBouncer Setup Complete!"
    echo ""
    echo "====================================="
    echo "Connection Details:"
    echo "====================================="
    echo "Direct PostgreSQL connection:"
    echo "  Host: $DB_HOST"
    echo "  Port: $DB_PORT"
    echo "  Database: $DB_NAME"
    echo "  User: $DB_USER"
    echo ""
    echo "PgBouncer pooled connection:"
    echo "  Host: localhost"
    echo "  Port: $PGBOUNCER_PORT"
    echo "  Database: $DB_NAME"
    echo "  User: $DB_USER"
    echo ""
    echo "Connection string (through PgBouncer):"
    echo "  postgresql://$DB_USER:<password>@localhost:$PGBOUNCER_PORT/$DB_NAME"
    echo ""
    echo "Configuration files:"
    echo "  PgBouncer config: $PGBOUNCER_CONFIG_DIR/pgbouncer.ini"
    echo "  User list: $PGBOUNCER_CONFIG_DIR/userlist.txt"
    echo "  Log file: /var/log/pgbouncer/pgbouncer.log"
    echo "====================================="
}

# Main script execution
main() {
    print_info "Starting PgBouncer setup for Test Platform"

    local os=$(detect_os)
    print_info "Detected OS: $os"

    # Check if PgBouncer is already installed
    if ! check_pgbouncer_installed; then
        # Install PgBouncer based on OS
        case $os in
            debian)
                install_pgbouncer_debian
                ;;
            macos)
                install_pgbouncer_macos
                ;;
            *)
                print_error "Unsupported operating system: $os"
                print_info "Please install PgBouncer manually"
                exit 1
                ;;
        esac
    fi

    # Configure PgBouncer
    configure_pgbouncer

    # Create systemd service if on Linux
    if [[ "$os" == "debian" ]]; then
        create_systemd_service
    fi

    # Start PgBouncer service
    start_pgbouncer

    # Test connection
    if test_pgbouncer_connection; then
        show_connection_details
    else
        print_error "PgBouncer setup completed but connection test failed"
        print_info "Please check the configuration and logs"
        exit 1
    fi
}

# Run main function
main "$@"