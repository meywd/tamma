# Implementation Plan: Task 1 - Database Setup and Configuration

**Story**: 1.1 Database Schema Migration System  
**Task**: 1 - Database Setup and Configuration  
**Acceptance Criteria**: #1 - PostgreSQL 17 database with JSONB support installed and configured

## Overview

Set up PostgreSQL 17 with proper configuration for the test platform, including JSONB support and connection pooling.

## Implementation Steps

### Subtask 1.1: Install and configure PostgreSQL 17

**Objective**: Install PostgreSQL 17 and configure it for development use

**Steps**:

1. Install PostgreSQL 17 using package manager

   ```bash
   # Ubuntu/Debian
   sudo apt update
   sudo apt install postgresql-17 postgresql-client-17

   # macOS
   brew install postgresql@17
   brew services start postgresql@17
   ```

2. Initialize database cluster if needed

   ```bash
   sudo pg_createcluster 17 main --start
   ```

3. Configure PostgreSQL settings
   - Edit `/etc/postgresql/17/main/postgresql.conf`
   - Set `max_connections = 200`
   - Set `shared_buffers = 256MB`
   - Set `effective_cache_size = 1GB`
   - Enable `log_statement = 'all'` for development

4. Create test platform database
   ```sql
   CREATE DATABASE test_platform;
   CREATE USER test_platform_user WITH PASSWORD 'secure_password';
   GRANT ALL PRIVILEGES ON DATABASE test_platform TO test_platform_user;
   ```

**Validation**:

- PostgreSQL service is running
- Can connect to database
- Version is 17.x

### Subtask 1.2: Enable JSONB extension and verify functionality

**Objective**: Ensure JSONB support is available and working

**Steps**:

1. Connect to test_platform database
2. Enable JSONB extension (usually enabled by default in PostgreSQL 17)

   ```sql
   CREATE EXTENSION IF NOT EXISTS btree_gin;
   CREATE EXTENSION IF NOT EXISTS btree_gist;
   ```

3. Test JSONB functionality

   ```sql
   -- Create test table
   CREATE TABLE test_jsonb (
       id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
       data JSONB NOT NULL,
       created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
   );

   -- Insert test data
   INSERT INTO test_jsonb (data) VALUES
   ('{"name": "test", "value": 123, "tags": ["a", "b"]}'),
   ('{"config": {"enabled": true, "threshold": 0.5}}');

   -- Test JSONB queries
   SELECT data->>'name' as name FROM test_jsonb WHERE data ? 'name';
   SELECT * FROM test_jsonb WHERE data->'config'->>'enabled' = 'true';
   ```

4. Create JSONB indexes for performance testing
   ```sql
   CREATE INDEX idx_test_jsonb_data ON test_jsonb USING GIN (data);
   ```

**Validation**:

- JSONB operations work correctly
- Indexes are created successfully
- Query performance is acceptable

### Subtask 1.3: Configure connection pooling with PgBouncer

**Objective**: Set up PgBouncer for efficient connection management

**Steps**:

1. Install PgBouncer

   ```bash
   # Ubuntu/Debian
   sudo apt install pgbouncer

   # macOS
   brew install pgbouncer
   ```

2. Configure PgBouncer (`/etc/pgbouncer/pgbouncer.ini`)

   ```ini
   [databases]
   test_platform = host=localhost port=5432 dbname=test_platform

   [pgbouncer]
   listen_port = 6432
   listen_addr = 127.0.0.1
   auth_type = md5
   auth_file = /etc/pgbouncer/userlist.txt
   logfile = /var/log/pgbouncer/pgbouncer.log
   pidfile = /var/run/pgbouncer/pgbouncer.pid
   admin_users = postgres
   stats_users = stats, postgres

   # Connection pooling settings
   pool_mode = transaction
   max_client_conn = 200
   default_pool_size = 20
   min_pool_size = 5
   reserve_pool_size = 5
   reserve_pool_timeout = 5
   max_db_connections = 50
   max_user_connections = 50

   # Timeouts
   server_reset_query = DISCARD ALL
   server_check_delay = 30
   server_check_query = select 1
   server_lifetime = 3600
   server_idle_timeout = 600
   ```

3. Create userlist.txt for PgBouncer authentication

   ```bash
   sudo echo '"test_platform_user" "secure_password"' > /etc/pgbouncer/userlist.txt
   sudo chmod 600 /etc/pgbouncer/userlist.txt
   ```

4. Start PgBouncer service

   ```bash
   sudo systemctl enable pgbouncer
   sudo systemctl start pgbouncer
   ```

5. Test PgBouncer connection
   ```bash
   psql -h localhost -p 6432 -U test_platform_user -d test_platform
   ```

**Validation**:

- PgBouncer is running and listening on port 6432
- Can connect through PgBouncer to PostgreSQL
- Connection pooling statistics are available

## Files to Create

1. `database/setup/postgresql-setup.sh` - Installation and configuration script
2. `database/setup/pgbouncer-config.ini` - PgBouncer configuration template
3. `database/setup/test-jsonb.sql` - JSONB functionality test script
4. `config/database.json` - Database connection configuration

## Dependencies

- PostgreSQL 17
- PgBouncer
- Administrative privileges for installation

## Testing

1. Unit tests for database connection
2. Integration tests for JSONB operations
3. Performance tests for connection pooling
4. Validation scripts for setup verification

## Notes

- Use environment variables for sensitive configuration
- Document all configuration changes
- Consider Docker setup for development consistency
- Backup configuration files before modification
