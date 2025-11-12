#!/bin/bash

# PostgreSQL 17 Setup Script for Test Platform
# Story: 1.1 Database Schema Migration System
# Task: 1 - Database Setup and Configuration
# Subtask: 1.1 - Install and configure PostgreSQL 17

set -e  # Exit on error

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Configuration variables
DB_NAME="${DB_NAME:-test_platform}"
DB_USER="${DB_USER:-test_platform_user}"
DB_PASSWORD="${DB_PASSWORD:-$(openssl rand -base64 32)}"
DB_HOST="${DB_HOST:-localhost}"
DB_PORT="${DB_PORT:-5432}"
PG_VERSION="17"

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

# Function to check if PostgreSQL is installed
check_postgresql_installed() {
    if command -v psql &> /dev/null; then
        local version=$(psql --version | awk '{print $3}' | sed 's/\..*//g')
        if [ "$version" == "$PG_VERSION" ]; then
            print_info "PostgreSQL $PG_VERSION is already installed"
            return 0
        else
            print_warn "PostgreSQL $version is installed, but version $PG_VERSION is required"
            return 1
        fi
    else
        return 1
    fi
}

# Function to install PostgreSQL on Debian/Ubuntu
install_postgresql_debian() {
    print_info "Installing PostgreSQL $PG_VERSION on Debian/Ubuntu..."

    # Add PostgreSQL APT repository
    sudo sh -c 'echo "deb http://apt.postgresql.org/pub/repos/apt $(lsb_release -cs)-pgdg main" > /etc/apt/sources.list.d/pgdg.list'
    wget --quiet -O - https://www.postgresql.org/media/keys/ACCC4CF8.asc | sudo apt-key add -
    sudo apt-get update

    # Install PostgreSQL
    sudo apt-get install -y postgresql-$PG_VERSION postgresql-client-$PG_VERSION postgresql-contrib-$PG_VERSION

    print_info "PostgreSQL $PG_VERSION installed successfully"
}

# Function to install PostgreSQL on macOS
install_postgresql_macos() {
    print_info "Installing PostgreSQL $PG_VERSION on macOS..."

    if ! command -v brew &> /dev/null; then
        print_error "Homebrew is not installed. Please install Homebrew first."
        exit 1
    fi

    brew install postgresql@$PG_VERSION
    brew services start postgresql@$PG_VERSION

    # Add to PATH
    echo 'export PATH="/opt/homebrew/opt/postgresql@17/bin:$PATH"' >> ~/.zshrc
    export PATH="/opt/homebrew/opt/postgresql@17/bin:$PATH"

    print_info "PostgreSQL $PG_VERSION installed successfully"
}

# Function to configure PostgreSQL
configure_postgresql() {
    print_info "Configuring PostgreSQL..."

    # Find PostgreSQL configuration directory
    local config_dir=""
    if [[ "$(detect_os)" == "debian" ]]; then
        config_dir="/etc/postgresql/$PG_VERSION/main"
    elif [[ "$(detect_os)" == "macos" ]]; then
        config_dir="/opt/homebrew/var/postgresql@$PG_VERSION"
    fi

    if [ -z "$config_dir" ] || [ ! -d "$config_dir" ]; then
        print_warn "Could not find PostgreSQL configuration directory. Skipping configuration."
        return
    fi

    # Backup original configuration
    if [ -f "$config_dir/postgresql.conf" ]; then
        sudo cp "$config_dir/postgresql.conf" "$config_dir/postgresql.conf.backup.$(date +%Y%m%d%H%M%S)"

        # Update configuration
        print_info "Updating PostgreSQL configuration..."
        sudo sed -i.bak -E "s/^#?max_connections = .*/max_connections = 200/" "$config_dir/postgresql.conf" 2>/dev/null || \
        sudo sed -i '' -E "s/^#?max_connections = .*/max_connections = 200/" "$config_dir/postgresql.conf"

        sudo sed -i.bak -E "s/^#?shared_buffers = .*/shared_buffers = 256MB/" "$config_dir/postgresql.conf" 2>/dev/null || \
        sudo sed -i '' -E "s/^#?shared_buffers = .*/shared_buffers = 256MB/" "$config_dir/postgresql.conf"

        sudo sed -i.bak -E "s/^#?effective_cache_size = .*/effective_cache_size = 1GB/" "$config_dir/postgresql.conf" 2>/dev/null || \
        sudo sed -i '' -E "s/^#?effective_cache_size = .*/effective_cache_size = 1GB/" "$config_dir/postgresql.conf"

        # Enable logging for development
        sudo sed -i.bak -E "s/^#?log_statement = .*/log_statement = 'all'/" "$config_dir/postgresql.conf" 2>/dev/null || \
        sudo sed -i '' -E "s/^#?log_statement = .*/log_statement = 'all'/" "$config_dir/postgresql.conf"
    fi

    # Restart PostgreSQL to apply changes
    if [[ "$(detect_os)" == "debian" ]]; then
        sudo systemctl restart postgresql
    elif [[ "$(detect_os)" == "macos" ]]; then
        brew services restart postgresql@$PG_VERSION
    fi

    print_info "PostgreSQL configuration updated"
}

# Function to create database and user
create_database() {
    print_info "Creating database and user..."

    # Wait for PostgreSQL to be ready
    local retries=30
    while [ $retries -gt 0 ]; do
        if sudo -u postgres psql -c "SELECT 1" &>/dev/null; then
            break
        fi
        print_info "Waiting for PostgreSQL to be ready..."
        sleep 2
        retries=$((retries - 1))
    done

    if [ $retries -eq 0 ]; then
        print_error "PostgreSQL is not responding"
        exit 1
    fi

    # Create database and user
    sudo -u postgres psql <<EOF
-- Check if database exists
SELECT 'CREATE DATABASE $DB_NAME'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$DB_NAME')\gexec

-- Check if user exists
DO \$\$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_user WHERE usename = '$DB_USER') THEN
        CREATE USER $DB_USER WITH PASSWORD '$DB_PASSWORD';
    ELSE
        ALTER USER $DB_USER WITH PASSWORD '$DB_PASSWORD';
    END IF;
END\$\$;

-- Grant privileges
GRANT ALL PRIVILEGES ON DATABASE $DB_NAME TO $DB_USER;

-- Connect to the database and grant schema privileges
\c $DB_NAME
GRANT ALL ON SCHEMA public TO $DB_USER;
EOF

    print_info "Database '$DB_NAME' and user '$DB_USER' created successfully"

    # Save connection details to environment file
    cat > .env.database <<EOF
# PostgreSQL Connection Configuration
# Generated on $(date)
DB_NAME=$DB_NAME
DB_USER=$DB_USER
DB_PASSWORD=$DB_PASSWORD
DB_HOST=$DB_HOST
DB_PORT=$DB_PORT
DATABASE_URL=postgresql://$DB_USER:$DB_PASSWORD@$DB_HOST:$DB_PORT/$DB_NAME
EOF
    chmod 600 .env.database
    print_info "Database credentials saved to .env.database (keep this secure!)"
}

# Function to validate PostgreSQL installation
validate_installation() {
    print_info "Validating PostgreSQL installation..."

    # Check PostgreSQL version
    local version=$(psql --version | awk '{print $3}')
    print_info "PostgreSQL version: $version"

    # Check if service is running
    if [[ "$(detect_os)" == "debian" ]]; then
        if systemctl is-active --quiet postgresql; then
            print_info "PostgreSQL service is running"
        else
            print_error "PostgreSQL service is not running"
            return 1
        fi
    elif [[ "$(detect_os)" == "macos" ]]; then
        if brew services list | grep -q "postgresql@$PG_VERSION.*started"; then
            print_info "PostgreSQL service is running"
        else
            print_error "PostgreSQL service is not running"
            return 1
        fi
    fi

    # Test database connection
    export PGPASSWORD=$DB_PASSWORD
    if psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -c "SELECT version();" &>/dev/null; then
        print_info "Database connection successful"
    else
        print_error "Failed to connect to database"
        return 1
    fi
    unset PGPASSWORD

    print_info "PostgreSQL installation validated successfully"
}

# Main script execution
main() {
    print_info "Starting PostgreSQL $PG_VERSION setup for Test Platform"

    local os=$(detect_os)
    print_info "Detected OS: $os"

    # Check if PostgreSQL is already installed
    if ! check_postgresql_installed; then
        # Install PostgreSQL based on OS
        case $os in
            debian)
                install_postgresql_debian
                ;;
            macos)
                install_postgresql_macos
                ;;
            *)
                print_error "Unsupported operating system: $os"
                print_info "Please install PostgreSQL $PG_VERSION manually"
                exit 1
                ;;
        esac
    fi

    # Configure PostgreSQL
    configure_postgresql

    # Create database and user
    create_database

    # Validate installation
    if validate_installation; then
        print_info "PostgreSQL setup completed successfully!"
        print_info "Connection details saved in .env.database"
        echo ""
        print_info "Next steps:"
        print_info "  1. Run test-jsonb.sql to verify JSONB functionality"
        print_info "  2. Configure PgBouncer for connection pooling"
        print_info "  3. Update application configuration with database credentials"
    else
        print_error "PostgreSQL setup validation failed"
        exit 1
    fi
}

# Run main function
main "$@"