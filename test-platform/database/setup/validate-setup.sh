#!/bin/bash

# Database Setup Validation Script
# Story: 1.1 Database Schema Migration System
# Task: 1 - Database Setup and Configuration
# Purpose: Validate that all components are correctly installed and configured

set -e  # Exit on error

# Color codes for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Results tracking
TOTAL_CHECKS=0
PASSED_CHECKS=0
FAILED_CHECKS=0
WARNINGS=0

# Load configuration if available
if [ -f ".env.database" ]; then
    source .env.database
fi

# Configuration variables
DB_NAME="${DB_NAME:-test_platform}"
DB_USER="${DB_USER:-test_platform_user}"
DB_PASSWORD="${DB_PASSWORD:-}"
DB_HOST="${DB_HOST:-localhost}"
DB_PORT="${DB_PORT:-5432}"
PGBOUNCER_PORT="${PGBOUNCER_PORT:-6432}"
REQUIRED_PG_VERSION="17"

# Function to print colored messages
print_header() {
    echo ""
    echo -e "${BLUE}========================================${NC}"
    echo -e "${BLUE}$1${NC}"
    echo -e "${BLUE}========================================${NC}"
}

print_section() {
    echo ""
    echo -e "${YELLOW}--- $1 ---${NC}"
}

print_check() {
    echo -n "  ✓ Checking $1... "
}

print_pass() {
    echo -e "${GREEN}PASSED${NC}"
    ((PASSED_CHECKS++))
    ((TOTAL_CHECKS++))
}

print_fail() {
    echo -e "${RED}FAILED${NC}"
    if [ ! -z "$1" ]; then
        echo -e "    ${RED}Error: $1${NC}"
    fi
    ((FAILED_CHECKS++))
    ((TOTAL_CHECKS++))
}

print_warn() {
    echo -e "${YELLOW}WARNING${NC}"
    if [ ! -z "$1" ]; then
        echo -e "    ${YELLOW}Warning: $1${NC}"
    fi
    ((WARNINGS++))
}

print_info() {
    echo -e "    ${GREEN}ℹ${NC} $1"
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

# ============================================
# PostgreSQL Validation
# ============================================
validate_postgresql() {
    print_section "PostgreSQL Installation"

    # Check if PostgreSQL is installed
    print_check "PostgreSQL installation"
    if command -v psql &> /dev/null; then
        print_pass
    else
        print_fail "PostgreSQL client not found in PATH"
        return 1
    fi

    # Check PostgreSQL version
    print_check "PostgreSQL version"
    local version=$(psql --version | awk '{print $3}' | sed 's/\..*//g')
    if [ "$version" == "$REQUIRED_PG_VERSION" ]; then
        print_pass
        print_info "Version: $(psql --version | awk '{print $3}')"
    else
        print_fail "Expected version $REQUIRED_PG_VERSION, found $version"
    fi

    # Check if PostgreSQL service is running
    print_check "PostgreSQL service status"
    local os=$(detect_os)
    if [[ "$os" == "debian" ]]; then
        if systemctl is-active --quiet postgresql; then
            print_pass
        else
            print_fail "PostgreSQL service is not running"
        fi
    elif [[ "$os" == "macos" ]]; then
        if pgrep -x "postgres" > /dev/null; then
            print_pass
        else
            print_fail "PostgreSQL process not found"
        fi
    else
        print_warn "Cannot determine service status on this OS"
    fi

    # Check PostgreSQL port
    print_check "PostgreSQL port $DB_PORT"
    if nc -z localhost $DB_PORT 2>/dev/null; then
        print_pass
    else
        print_fail "Port $DB_PORT is not accessible"
    fi

    # Check database existence
    print_check "Database '$DB_NAME' existence"
    export PGPASSWORD=$DB_PASSWORD
    if psql -h $DB_HOST -p $DB_PORT -U $DB_USER -lqt 2>/dev/null | cut -d \| -f 1 | grep -qw $DB_NAME; then
        print_pass
    else
        print_fail "Database '$DB_NAME' does not exist"
    fi
    unset PGPASSWORD

    # Check database connection
    print_check "Database connection"
    export PGPASSWORD=$DB_PASSWORD
    if psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -c "SELECT 1;" &>/dev/null; then
        print_pass
    else
        print_fail "Cannot connect to database"
    fi
    unset PGPASSWORD

    # Check user privileges
    print_check "User privileges"
    export PGPASSWORD=$DB_PASSWORD
    local has_privileges=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SELECT has_database_privilege('$DB_USER', '$DB_NAME', 'CREATE');" 2>/dev/null | tr -d ' ')
    if [ "$has_privileges" == "t" ]; then
        print_pass
    else
        print_warn "User '$DB_USER' may have limited privileges"
    fi
    unset PGPASSWORD
}

# ============================================
# JSONB Support Validation
# ============================================
validate_jsonb() {
    print_section "JSONB Support"

    # Check JSONB data type support
    print_check "JSONB data type support"
    export PGPASSWORD=$DB_PASSWORD
    if psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -c "SELECT '{}' ::jsonb;" &>/dev/null; then
        print_pass
    else
        print_fail "JSONB data type not supported"
    fi
    unset PGPASSWORD

    # Check required extensions
    print_check "btree_gin extension"
    export PGPASSWORD=$DB_PASSWORD
    local ext_exists=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SELECT COUNT(*) FROM pg_extension WHERE extname = 'btree_gin';" 2>/dev/null | tr -d ' ')
    if [ "$ext_exists" == "1" ]; then
        print_pass
    else
        print_warn "btree_gin extension not installed (run CREATE EXTENSION btree_gin;)"
    fi
    unset PGPASSWORD

    print_check "btree_gist extension"
    export PGPASSWORD=$DB_PASSWORD
    ext_exists=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SELECT COUNT(*) FROM pg_extension WHERE extname = 'btree_gist';" 2>/dev/null | tr -d ' ')
    if [ "$ext_exists" == "1" ]; then
        print_pass
    else
        print_warn "btree_gist extension not installed (run CREATE EXTENSION btree_gist;)"
    fi
    unset PGPASSWORD

    # Test JSONB operations
    print_check "JSONB operations"
    export PGPASSWORD=$DB_PASSWORD
    if psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -c "SELECT '{\"test\": \"value\"}'::jsonb->>'test';" &>/dev/null; then
        print_pass
    else
        print_fail "JSONB operations not working"
    fi
    unset PGPASSWORD

    # Check if test tables exist
    print_check "Test JSONB tables"
    export PGPASSWORD=$DB_PASSWORD
    local table_exists=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SELECT COUNT(*) FROM information_schema.tables WHERE table_name IN ('test_jsonb', 'test_jsonb_performance');" 2>/dev/null | tr -d ' ')
    if [ "$table_exists" -gt "0" ]; then
        print_pass
        print_info "Found $table_exists test table(s)"
    else
        print_warn "Test tables not found (run test-jsonb.sql to create them)"
    fi
    unset PGPASSWORD
}

# ============================================
# PgBouncer Validation
# ============================================
validate_pgbouncer() {
    print_section "PgBouncer Configuration"

    # Check if PgBouncer is installed
    print_check "PgBouncer installation"
    if command -v pgbouncer &> /dev/null; then
        print_pass
        print_info "Version: $(pgbouncer -V 2>&1 | head -1)"
    else
        print_warn "PgBouncer not installed"
        return 0
    fi

    # Check PgBouncer configuration file
    print_check "PgBouncer configuration file"
    local config_locations=("/etc/pgbouncer/pgbouncer.ini" "/opt/homebrew/etc/pgbouncer.ini" "/usr/local/etc/pgbouncer.ini")
    local config_found=false
    for config in "${config_locations[@]}"; do
        if [ -f "$config" ]; then
            config_found=true
            print_pass
            print_info "Configuration at: $config"
            break
        fi
    done
    if [ "$config_found" == "false" ]; then
        print_warn "PgBouncer configuration not found in standard locations"
    fi

    # Check PgBouncer service
    print_check "PgBouncer service status"
    local os=$(detect_os)
    if [[ "$os" == "debian" ]]; then
        if systemctl is-active --quiet pgbouncer 2>/dev/null; then
            print_pass
        else
            print_warn "PgBouncer service is not running"
        fi
    elif [[ "$os" == "macos" ]]; then
        if pgrep -f "pgbouncer" > /dev/null; then
            print_pass
        else
            print_warn "PgBouncer process not found"
        fi
    else
        if pgrep -f "pgbouncer" > /dev/null; then
            print_pass
        else
            print_warn "PgBouncer process not found"
        fi
    fi

    # Check PgBouncer port
    print_check "PgBouncer port $PGBOUNCER_PORT"
    if nc -z localhost $PGBOUNCER_PORT 2>/dev/null; then
        print_pass
    else
        print_warn "Port $PGBOUNCER_PORT is not accessible"
    fi

    # Test PgBouncer connection
    print_check "PgBouncer connection"
    export PGPASSWORD=$DB_PASSWORD
    if psql -h localhost -p $PGBOUNCER_PORT -U $DB_USER -d $DB_NAME -c "SELECT 1;" &>/dev/null; then
        print_pass
    else
        print_warn "Cannot connect through PgBouncer"
    fi
    unset PGPASSWORD
}

# ============================================
# Configuration Files Validation
# ============================================
validate_configuration() {
    print_section "Configuration Files"

    # Check database configuration file
    print_check "database.json configuration"
    if [ -f "config/database.json" ]; then
        print_pass
        # Validate JSON syntax
        if command -v jq &> /dev/null; then
            if jq empty config/database.json 2>/dev/null; then
                print_info "JSON syntax is valid"
            else
                print_warn "JSON syntax error in database.json"
            fi
        fi
    else
        print_fail "config/database.json not found"
    fi

    # Check environment file
    print_check "Environment configuration (.env.database)"
    if [ -f ".env.database" ]; then
        print_pass
        # Check file permissions
        local perms=$(stat -c %a .env.database 2>/dev/null || stat -f %A .env.database 2>/dev/null)
        if [ "$perms" == "600" ]; then
            print_info "File permissions are secure (600)"
        else
            print_warn "File permissions are $perms (should be 600 for security)"
        fi
    else
        print_warn ".env.database not found (run postgresql-setup.sh to generate)"
    fi

    # Check for setup scripts
    print_check "Setup scripts"
    local scripts_found=0
    local expected_scripts=("postgresql-setup.sh" "pgbouncer-setup.sh" "test-jsonb.sql" "validate-setup.sh")
    for script in "${expected_scripts[@]}"; do
        if [ -f "database/setup/$script" ]; then
            ((scripts_found++))
        fi
    done
    if [ $scripts_found -eq ${#expected_scripts[@]} ]; then
        print_pass
        print_info "All ${#expected_scripts[@]} setup scripts found"
    else
        print_warn "Found $scripts_found of ${#expected_scripts[@]} expected scripts"
    fi
}

# ============================================
# Performance Checks
# ============================================
validate_performance() {
    print_section "Performance Configuration"

    # Check PostgreSQL settings
    export PGPASSWORD=$DB_PASSWORD

    print_check "max_connections setting"
    local max_conn=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SHOW max_connections;" 2>/dev/null | tr -d ' ')
    if [ ! -z "$max_conn" ] && [ "$max_conn" -ge "200" ]; then
        print_pass
        print_info "max_connections = $max_conn"
    else
        print_warn "max_connections = $max_conn (recommended: >= 200)"
    fi

    print_check "shared_buffers setting"
    local shared_buf=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SHOW shared_buffers;" 2>/dev/null | tr -d ' ')
    if [ ! -z "$shared_buf" ]; then
        print_pass
        print_info "shared_buffers = $shared_buf"
    else
        print_warn "Could not retrieve shared_buffers setting"
    fi

    print_check "Database size"
    local db_size=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SELECT pg_size_pretty(pg_database_size('$DB_NAME'));" 2>/dev/null | tr -d ' ')
    if [ ! -z "$db_size" ]; then
        print_pass
        print_info "Database size: $db_size"
    else
        print_warn "Could not retrieve database size"
    fi

    unset PGPASSWORD
}

# ============================================
# Security Checks
# ============================================
validate_security() {
    print_section "Security Configuration"

    # Check password encryption
    print_check "Password encryption method"
    export PGPASSWORD=$DB_PASSWORD
    local pwd_encryption=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SHOW password_encryption;" 2>/dev/null | tr -d ' ')
    if [ "$pwd_encryption" == "scram-sha-256" ] || [ "$pwd_encryption" == "md5" ]; then
        print_pass
        print_info "Using $pwd_encryption encryption"
    else
        print_warn "Password encryption: $pwd_encryption"
    fi
    unset PGPASSWORD

    # Check SSL configuration
    print_check "SSL configuration"
    export PGPASSWORD=$DB_PASSWORD
    local ssl_status=$(psql -h $DB_HOST -p $DB_PORT -U $DB_USER -d $DB_NAME -t -c "SHOW ssl;" 2>/dev/null | tr -d ' ')
    if [ "$ssl_status" == "on" ]; then
        print_pass
        print_info "SSL is enabled"
    else
        print_warn "SSL is disabled (recommended for production)"
    fi
    unset PGPASSWORD

    # Check for default passwords
    print_check "Default password usage"
    if [ "$DB_PASSWORD" == "secure_password" ] || [ "$DB_PASSWORD" == "password" ] || [ "$DB_PASSWORD" == "admin" ]; then
        print_warn "Using a weak or default password"
    else
        print_pass
    fi
}

# ============================================
# Summary Report
# ============================================
print_summary() {
    print_header "Validation Summary"

    echo ""
    echo -e "Total Checks: $TOTAL_CHECKS"
    echo -e "${GREEN}Passed: $PASSED_CHECKS${NC}"
    echo -e "${YELLOW}Warnings: $WARNINGS${NC}"
    echo -e "${RED}Failed: $FAILED_CHECKS${NC}"

    echo ""
    if [ $FAILED_CHECKS -eq 0 ]; then
        echo -e "${GREEN}✓ All critical checks passed!${NC}"
        echo -e "${GREEN}The database setup is ready for use.${NC}"
    else
        echo -e "${RED}✗ Some checks failed.${NC}"
        echo -e "${RED}Please review the errors above and run the setup scripts.${NC}"
    fi

    if [ $WARNINGS -gt 0 ]; then
        echo ""
        echo -e "${YELLOW}⚠ There are $WARNINGS warnings that should be reviewed.${NC}"
    fi

    echo ""
    echo "Next steps:"
    if [ $FAILED_CHECKS -gt 0 ]; then
        echo "  1. Run ./postgresql-setup.sh to set up PostgreSQL"
        echo "  2. Run psql -f test-jsonb.sql to test JSONB functionality"
        echo "  3. Run ./pgbouncer-setup.sh to configure connection pooling"
        echo "  4. Run this validation script again to confirm setup"
    else
        echo "  1. Review any warnings above"
        echo "  2. Update application configuration with database credentials"
        echo "  3. Begin implementing migration system (Task 2)"
    fi
}

# ============================================
# Main Execution
# ============================================
main() {
    print_header "Test Platform Database Setup Validation"
    echo "Story: 1.1 Database Schema Migration System"
    echo "Task: 1 - Database Setup and Configuration"
    echo "Date: $(date)"
    echo "System: $(uname -s) $(uname -r)"

    # Run all validations
    validate_postgresql
    validate_jsonb
    validate_pgbouncer
    validate_configuration
    validate_performance
    validate_security

    # Print summary
    print_summary

    # Exit with appropriate code
    if [ $FAILED_CHECKS -gt 0 ]; then
        exit 1
    else
        exit 0
    fi
}

# Run main function
main "$@"