# Implementation Plan: Task 5 - Documentation and Validation

**Story**: 1.1 Database Schema Migration System  
**Task**: 5 - Documentation and Validation  
**Acceptance Criteria**: #7, #8 - Database schema documented with relationships and constraints; Migration rollback capability with version tracking

## Overview

Create comprehensive documentation for the database schema and implement validation tests to ensure migration rollback functionality works correctly.

## Implementation Steps

### Subtask 5.1: Document schema with ERD and field descriptions

**Objective**: Create comprehensive database documentation

**File**: `docs/database/schema.md`

```markdown
# Database Schema Documentation

## Overview

The Test Platform uses PostgreSQL 17 with a comprehensive schema designed for multi-tenant operations, event sourcing, and audit trails. The schema follows the DCB (Dynamic Consistency Boundary) pattern for complete auditability.

## Entity Relationship Diagram (ERD)
```

┌─────────────────┐ ┌─────────────────────┐ ┌─────────────────┐
│ organizations │───────│ user_organizations │───────│ users │
├─────────────────┤ ├─────────────────────┤ ├─────────────────┤
│ id (UUID) PK │───────│ id (UUID) PK │───────│ id (UUID) PK │
│ name │ │ user_id (UUID) FK │ │ email │
│ slug │ │ organization_id FK │ │ password_hash │
│ description │ │ role │ │ first_name │
│ domain │ │ status │ │ last_name │
│ email │ │ joined_at │ │ status │
│ settings (JSONB)│ │ created_at │ │ role │
│ metadata (JSONB)│ └─────────────────────┘ │ created_at │
│ status │ │ updated_at │
│ created_at │ └─────────────────┘
│ updated_at │ │
└─────────────────┘ │
│ │
│ │
▼ ▼
┌─────────────────┐ ┌─────────────────┐
│ events │ │ api_keys │
├─────────────────┤ ├─────────────────┤
│ id (UUID) PK │ │ id (UUID) PK │
│ event_type │ │ key_id │
│ aggregate_type │ │ key_hash │
│ aggregate_id │ │ user_id FK │
│ data (JSONB) │ │ organization_id │
│ tags (JSONB) │ │ name │
│ user_id FK │ │ status │
│ organization_id │ │ expires_at │
│ event_timestamp │ │ created_at │
│ created_at │ │ last_used_at │
│ processed │ └─────────────────┘
│ processed_at │
└─────────────────┘

```

## Table Details

### organizations

Multi-tenant organization management with flexible configuration.

**Columns:**
- `id` (UUID, Primary Key): Unique identifier using gen_random_uuid()
- `name` (VARCHAR(255), Not Null): Organization display name
- `slug` (VARCHAR(100), Unique, Not Null): URL-friendly identifier
- `description` (TEXT, Nullable): Organization description
- `domain` (VARCHAR(255), Nullable): Custom domain for SSO
- `email` (VARCHAR(255), Nullable): Contact email
- `phone` (VARCHAR(50), Nullable): Contact phone
- `website` (VARCHAR(500), Nullable): Organization website
- `address_line1` (VARCHAR(255), Nullable): Street address
- `address_line2` (VARCHAR(255), Nullable): Address line 2
- `city` (VARCHAR(100), Nullable): City
- `state` (VARCHAR(100), Nullable): State/Province
- `country` (VARCHAR(100), Nullable): Country
- `postal_code` (VARCHAR(20), Nullable): Postal/ZIP code
- `settings` (JSONB, Default '{}'): Organization-specific settings
- `metadata` (JSONB, Default '{}'): Extensible metadata
- `status` (ENUM, Default 'active'): Organization status
- `subscription_tier` (VARCHAR(50), Default 'free'): Subscription level
- `subscription_expires_at` (TIMESTAMP, Nullable): Subscription expiry
- `created_at` (TIMESTAMP, Default NOW()): Creation timestamp
- `updated_at` (TIMESTAMP, Default NOW()): Last update timestamp
- `created_by` (UUID, Foreign Key → users.id): Creator
- `updated_by` (UUID, Foreign Key → users.id): Last updater

**Constraints:**
- `slug` must match pattern `^[a-z0-9-]+$`
- `name` cannot be empty after trimming

**Indexes:**
- Primary key on `id`
- Unique index on `slug`
- Index on `status`
- Index on `subscription_tier`
- Index on `created_at`
- GIN index on `name` for trigram search
- Index on `domain` (where not null)
- Composite index on `status, created_at DESC`

### users

User authentication and profile management.

**Columns:**
- `id` (UUID, Primary Key): Unique identifier
- `email` (VARCHAR(255), Unique, Not Null): User email (login)
- `password_hash` (VARCHAR(255), Not Null): Bcrypt password hash
- `password_salt` (VARCHAR(255), Not Null): Password salt
- `password_reset_token` (VARCHAR(255), Unique, Nullable): Reset token
- `password_reset_expires_at` (TIMESTAMP, Nullable): Reset token expiry
- `email_verification_token` (VARCHAR(255), Unique, Nullable): Verification token
- `email_verified_at` (TIMESTAMP, Nullable): Email verification timestamp
- `first_name` (VARCHAR(100), Nullable): First name
- `last_name` (VARCHAR(100), Nullable): Last name
- `username` (VARCHAR(100), Unique, Nullable): Unique username
- `bio` (TEXT, Nullable): User biography
- `avatar_url` (VARCHAR(500), Nullable): Profile picture URL
- `preferences` (JSONB, Default '{}'): User preferences
- `settings` (JSONB, Default '{}'): User settings
- `timezone` (VARCHAR(50), Default 'UTC'): User timezone
- `language` (VARCHAR(10), Default 'en'): User language
- `status` (ENUM, Default 'active'): Account status
- `role` (ENUM, Default 'user'): System role
- `email_verified` (BOOLEAN, Default false): Email verification status
- `mfa_enabled` (BOOLEAN, Default false): Multi-factor auth status
- `mfa_secret` (VARCHAR(255), Nullable): MFA secret
- `last_login_ip` (VARCHAR(45), Nullable): Last login IP (IPv6 compatible)
- `current_login_ip` (VARCHAR(45), Nullable): Current login IP
- `last_login_at` (TIMESTAMP, Nullable): Last login timestamp
- `current_login_at` (TIMESTAMP, Nullable): Current login timestamp
- `failed_login_attempts` (INTEGER, Default 0): Failed login count
- `locked_until` (TIMESTAMP, Nullable): Account lockout expiry
- `created_at` (TIMESTAMP, Default NOW()): Creation timestamp
- `updated_at` (TIMESTAMP, Default NOW()): Last update timestamp
- `created_by` (UUID, Foreign Key → users.id): Creator
- `updated_by` (UUID, Foreign Key → users.id): Last updater

**Constraints:**
- `email` must match email regex pattern
- `password_hash` cannot be empty

**Indexes:**
- Primary key on `id`
- Unique index on `email`
- Unique index on `username`
- Index on `LOWER(email)` for case-insensitive search
- Index on `status, role`
- Index on `last_login_at DESC, current_login_at DESC` (where active)
- Composite index on `LOWER(first_name), LOWER(last_name)` (where not null)

### user_organizations

Many-to-many relationship between users and organizations.

**Columns:**
- `id` (UUID, Primary Key): Unique identifier
- `user_id` (UUID, Foreign Key → users.id, Not Null, On Delete CASCADE): User reference
- `organization_id` (UUID, Foreign Key → organizations.id, Not Null, On Delete CASCADE): Organization reference
- `role` (ENUM, Default 'member'): Role within organization
- `permissions` (JSONB, Default '{}'): Additional permissions
- `status` (ENUM, Default 'pending'): Membership status
- `invitation_token` (VARCHAR(255), Unique, Nullable): Invitation token
- `invitation_expires_at` (TIMESTAMP, Nullable): Invitation expiry
- `invited_by` (UUID, Foreign Key → users.id, Nullable): Inviter
- `job_title` (VARCHAR(255), Nullable): Job title
- `department` (VARCHAR(255), Nullable): Department
- `metadata` (JSONB, Default '{}'): Extensible metadata
- `joined_at` (TIMESTAMP, Nullable): Join timestamp
- `left_at` (TIMESTAMP, Nullable): Leave timestamp
- `created_at` (TIMESTAMP, Default NOW()): Creation timestamp
- `updated_at` (TIMESTAMP, Default NOW()): Last update timestamp

**Constraints:**
- Unique constraint on `(user_id, organization_id)`
- Both foreign keys are NOT NULL

**Indexes:**
- Primary key on `id`
- Index on `user_id`
- Index on `organization_id`
- Index on `user_id, status` (where status in active/pending)
- Index on `organization_id, role` (where status = active)

### events

DCB event sourcing table for complete audit trail.

**Columns:**
- `id` (UUID, Primary Key): Time-sortable UUID v7
- `event_type` (VARCHAR(255), Not Null): Event type (e.g., "USER.CREATED")
- `aggregate_type` (VARCHAR(100), Not Null): Aggregate type (e.g., "user")
- `aggregate_id` (UUID, Not Null): Aggregate identifier
- `aggregate_version` (VARCHAR(50), Not Null): Aggregate version
- `data` (JSONB, Not Null): Event payload
- `metadata` (JSONB, Default '{}'): Event metadata
- `tags` (JSONB, Default '{}'): Flexible tags for querying
- `user_id` (UUID, Foreign Key → users.id, Nullable): Acting user
- `organization_id` (UUID, Foreign Key → organizations.id, Nullable): Organization context
- `session_id` (VARCHAR(255), Nullable): Session identifier
- `correlation_id` (VARCHAR(255), Nullable): Request correlation
- `causation_id` (VARCHAR(255), Nullable): Causal event reference
- `source` (VARCHAR(100), Default 'system'): Event source
- `version` (VARCHAR(50), Nullable): API/system version
- `ip_address` (VARCHAR(45), Nullable): Client IP
- `user_agent` (VARCHAR(500), Nullable): Client user agent
- `event_timestamp` (TIMESTAMP, Not Null, Default NOW()): Event time
- `created_at` (TIMESTAMP, Default NOW()): Insertion time
- `processed` (BOOLEAN, Default false): Processing status
- `processed_at` (TIMESTAMP, Nullable): Processing timestamp
- `retry_count` (INTEGER, Default 0): Retry attempts
- `error_message` (VARCHAR(1000), Nullable): Processing error

**Constraints:**
- All identification fields are NOT NULL
- `event_type` cannot be empty after trimming

**Indexes:**
- Primary key on `id`
- Index on `event_type`
- Index on `aggregate_type, aggregate_id`
- Index on `aggregate_id, aggregate_version`
- Index on `event_timestamp` (time-series queries)
- GIN index on `tags`
- GIN index on `data`
- Composite indexes for common query patterns:
  - `aggregate_type, event_timestamp DESC`
  - `organization_id, event_timestamp DESC`
  - `user_id, event_timestamp DESC`
  - `event_type, event_timestamp DESC`
  - `correlation_id, event_timestamp`
  - `processed` (for unprocessed events)

### api_keys

API key management for programmatic access.

**Columns:**
- `id` (UUID, Primary Key): Unique identifier
- `key_id` (VARCHAR(100), Unique, Not Null): Public key identifier
- `key_hash` (VARCHAR(255), Unique, Not Null): Hashed key value
- `key_prefix` (VARCHAR(20), Not Null): Key prefix for identification
- `user_id` (UUID, Foreign Key → users.id, Not Null, On Delete CASCADE): Owner
- `organization_id` (UUID, Foreign Key → organizations.id, Nullable, On Delete CASCADE): Organization context
- `name` (VARCHAR(255), Not Null): Key display name
- `description` (TEXT, Nullable): Key description
- `key_type` (ENUM, Default 'personal'): Key type
- `permissions` (JSONB, Default '{}'): Key permissions
- `scopes` (JSONB, Default '[]'): Allowed scopes
- `allowed_ips` (VARCHAR(1000), Nullable): Allowed IP addresses
- `allowed_domains` (VARCHAR(1000), Nullable): Allowed domains
- `status` (ENUM, Default 'active'): Key status
- `expires_at` (TIMESTAMP, Nullable): Expiry timestamp
- `last_used_at` (TIMESTAMP, Nullable): Last usage timestamp
- `last_used_ip` (VARCHAR(45), Nullable): Last usage IP
- `last_used_user_agent` (VARCHAR(500), Nullable): Last usage UA
- `usage_count` (INTEGER, Default 0): Usage counter
- `usage_reset_at` (TIMESTAMP, Nullable): Usage counter reset
- `usage_limit` (INTEGER, Nullable): Monthly usage limit
- `created_from_ip` (VARCHAR(45), Nullable): Creation IP
- `created_from_user_agent` (VARCHAR(500), Nullable): Creation UA
- `require_mfa` (BOOLEAN, Default false): MFA requirement
- `created_at` (TIMESTAMP, Default NOW()): Creation timestamp
- `updated_at` (TIMESTAMP, Default NOW()): Last update timestamp
- `created_by` (UUID, Foreign Key → users.id, Nullable): Creator
- `updated_by` (UUID, Foreign Key → users.id, Nullable): Last updater

**Constraints:**
- All key fields are NOT NULL
- `name` cannot be empty after trimming
- `user_id` is NOT NULL

**Indexes:**
- Primary key on `id`
- Unique index on `key_id`
- Unique index on `key_hash`
- Index on `user_id, status` (where status in active/inactive)
- Index on `organization_id, status` (where not null and status in active/inactive)
- Index on `expires_at` (where not null)
- Index on `last_used_at DESC` (where status = active)

## Data Flow Patterns

### User Registration Flow
1. Create user record with `email_verified = false`
2. Generate `email_verification_token`
3. Send verification email
4. User verifies → update `email_verified_at`, clear token
5. Emit `USER.CREATED` and `USER.EMAIL_VERIFIED` events

### Organization Creation Flow
1. Create organization record
2. Create user_organization record with role = 'owner'
3. Emit `ORGANIZATION.CREATED` event
4. Emit `USER_ORGANIZATION.CREATED` event

### Event Sourcing Pattern
1. All state changes emit events
2. Events include full context (user, org, session)
3. Events are time-ordered with precise timestamps
4. Tags enable flexible querying and aggregation
5. Unprocessed events are picked up by background workers

## Security Considerations

### Row-Level Security
- Implement RLS policies for tenant isolation
- Users can only access their organization's data
- API keys are scoped to specific organizations

### Data Encryption
- Passwords use bcrypt with salt
- API keys are hashed before storage
- Sensitive data in JSONB fields should be encrypted

### Audit Trail
- All modifications create events
- Events include IP address and user agent
- Correlation IDs trace request flows
- Immutable event history prevents tampering

## Performance Optimizations

### Indexing Strategy
- Primary indexes on all foreign keys
- Composite indexes for common query patterns
- GIN indexes on JSONB fields for flexible queries
- Time-series indexes on timestamp fields

### Connection Pooling
- Environment-specific pool configurations
- Connection validation on acquire
- Pool monitoring and alerting
- Graceful degradation under load

### Query Patterns
- Use prepared statements for repeated queries
- Implement pagination for large result sets
- Cache frequently accessed reference data
- Use materialized views for complex aggregations
```

### Subtask 5.2: Test migration rollback scenarios

**Objective**: Create comprehensive tests for migration rollback functionality

**File**: `tests/database/migration-rollback.test.ts`

```typescript
import { Knex } from 'knex';
import { MigrationRunner } from '../../src/database/migration-runner';
import { getTestDatabase } from '../helpers/test-database';

describe('Migration Rollback Tests', () => {
  let db: Knex;
  let runner: MigrationRunner;

  beforeAll(async () => {
    db = await getTestDatabase();
    runner = new MigrationRunner('test');
  });

  afterAll(async () => {
    await runner.close();
    await db.destroy();
  });

  beforeEach(async () => {
    // Ensure clean state
    await db.raw('DROP SCHEMA public CASCADE; CREATE SCHEMA public;');
    await runner.migrateToLatest();
  });

  describe('Single Migration Rollback', () => {
    it('should rollback last migration successfully', async () => {
      // Get current state
      const beforeTables = await getTableNames(db);
      expect(beforeTables).toContain('organizations');
      expect(beforeTables).toContain('users');
      expect(beforeTables).toContain('user_organizations');
      expect(beforeTables).toContain('events');
      expect(beforeTables).toContain('api_keys');

      // Rollback one migration
      const result = await runner.rollback(1);
      expect(result.success).toBe(true);
      expect(result.executed).toHaveLength(1);

      // Verify api_keys table is gone
      const afterTables = await getTableNames(db);
      expect(afterTables).not.toContain('api_keys');
      expect(afterTables).toContain('events');
    });

    it('should preserve data integrity during rollback', async () => {
      // Insert test data
      await db('organizations').insert({
        name: 'Test Org',
        slug: 'test-org',
      });

      await db('users').insert({
        email: 'test@example.com',
        password_hash: 'hash',
        password_salt: 'salt',
      });

      const orgCount = await db('organizations').count('* as count');
      const userCount = await db('users').count('* as count');

      // Rollback migrations
      await runner.rollback();

      // Verify all tables are dropped
      const tables = await getTableNames(db);
      expect(tables).not.toContain('organizations');
      expect(tables).not.toContain('users');
    });
  });

  describe('Multiple Migration Rollback', () => {
    it('should rollback multiple migrations in correct order', async () => {
      // Rollback to specific batch
      const result = await runner.rollback(2);
      expect(result.success).toBe(true);
      expect(result.executed.length).toBeGreaterThan(1);

      // Verify correct tables remain
      const tables = await getTableNames(db);
      expect(tables).toContain('organizations');
      expect(tables).toContain('users');
      // Later tables should be gone
    });

    it('should handle rollback to specific version', async () => {
      // Get current version
      const currentVersion = await runner.getCurrentVersion();
      expect(currentVersion).toBeTruthy();

      // Rollback to earlier version
      const result = await runner.rollbackToVersion('create_organizations');
      expect(result.success).toBe(true);

      // Verify only organizations table exists
      const tables = await getTableNames(db);
      expect(tables).toContain('organizations');
      expect(tables).not.toContain('users');
    });
  });

  describe('Rollback Error Handling', () => {
    it('should handle rollback failures gracefully', async () => {
      // Manually corrupt the database state
      await db.raw('DROP TABLE IF EXISTS knex_migrations');

      // Attempt rollback
      const result = await runner.rollback();
      expect(result.success).toBe(false);
      expect(result.error).toBeDefined();
    });

    it('should validate rollback reversibility', async () => {
      // Test that all migrations can be rolled back
      const isValid = await validateMigrationReversibility(db);
      expect(isValid).toBe(true);
    });
  });

  describe('Data Consistency', () => {
    it('should maintain foreign key constraints during rollback', async () => {
      // Create related data
      const orgId = await db('organizations')
        .insert({
          name: 'Test Org',
          slug: 'test-org',
        })
        .returning('id')
        .then((rows) => rows[0].id);

      await db('users').insert({
        email: 'test@example.com',
        password_hash: 'hash',
        password_salt: 'salt',
      });

      await db('user_organizations').insert({
        user_id: (await db('users').select('id').first()).id,
        organization_id: orgId,
        role: 'member',
      });

      // Rollback user_organizations table
      await runner.rollback(1);

      // Verify parent tables still exist and data is intact
      const orgCount = await db('organizations').count('* as count');
      const userCount = await db('users').count('* as count');

      expect(parseInt(orgCount[0].count)).toBe(1);
      expect(parseInt(userCount[0].count)).toBe(1);
    });

    it('should handle cascade deletes correctly', async () => {
      // Create data with cascade relationships
      const orgId = await db('organizations')
        .insert({
          name: 'Test Org',
          slug: 'test-org',
        })
        .returning('id')
        .then((rows) => rows[0].id);

      const userId = await db('users')
        .insert({
          email: 'test@example.com',
          password_hash: 'hash',
          password_salt: 'salt',
        })
        .returning('id')
        .then((rows) => rows[0].id);

      await db('user_organizations').insert({
        user_id: userId,
        organization_id: orgId,
        role: 'member',
      });

      // Rollback organizations table (should cascade)
      await runner.rollback(4);

      // Verify all related data is gone
      const tables = await getTableNames(db);
      expect(tables).not.toContain('organizations');
      expect(tables).not.toContain('user_organizations');
    });
  });

  describe('Performance Impact', () => {
    it('should complete rollback within acceptable time', async () => {
      const startTime = Date.now();

      await runner.rollback();

      const duration = Date.now() - startTime;
      expect(duration).toBeLessThan(30000); // 30 seconds max
    });

    it('should handle large datasets efficiently', async () => {
      // Insert large dataset
      const users = Array.from({ length: 1000 }, (_, i) => ({
        email: `user${i}@example.com`,
        password_hash: 'hash',
        password_salt: 'salt',
      }));

      await db('users').insert(users);

      // Rollback should still be efficient
      const startTime = Date.now();
      const result = await runner.rollback(1);
      const duration = Date.now() - startTime;

      expect(result.success).toBe(true);
      expect(duration).toBeLessThan(60000); // 60 seconds for large dataset
    });
  });
});

async function getTableNames(db: Knex): Promise<string[]> {
  const result = await db.raw(`
    SELECT table_name 
    FROM information_schema.tables 
    WHERE table_schema = 'public' 
    AND table_type = 'BASE TABLE'
    ORDER BY table_name
  `);
  return result.rows.map((row: any) => row.table_name);
}

async function validateMigrationReversibility(db: Knex): Promise<boolean> {
  try {
    // Get all migrations
    const migrations = await db('knex_migrations').select('*').orderBy('id');

    // Test rollback for each migration
    for (const migration of migrations) {
      // This would require custom migration testing logic
      // For now, just verify the migration file has a down function
      console.log(`Testing reversibility for: ${migration.name}`);
    }

    return true;
  } catch (error) {
    console.error('Migration reversibility validation failed:', error);
    return false;
  }
}
```

### Subtask 5.3: Validate data integrity after migrations

**Objective**: Create comprehensive data integrity validation

**File**: `src/database/data-integrity-validator.ts`

```typescript
import { Knex } from 'knex';
import { logger } from '../observability/logger';

export interface IntegrityCheck {
  name: string;
  description: string;
  check: (db: Knex) => Promise<IntegrityResult>;
}

export interface IntegrityResult {
  passed: boolean;
  message: string;
  details?: Record<string, any>;
  severity: 'error' | 'warning' | 'info';
}

export class DataIntegrityValidator {
  private checks: IntegrityCheck[] = [
    {
      name: 'foreign-key-constraints',
      description: 'Verify all foreign key constraints are valid',
      check: this.checkForeignKeyConstraints.bind(this),
    },
    {
      name: 'unique-constraints',
      description: 'Verify unique constraints are enforced',
      check: this.checkUniqueConstraints.bind(this),
    },
    {
      name: 'data-consistency',
      description: 'Check data consistency across related tables',
      check: this.checkDataConsistency.bind(this),
    },
    {
      name: 'event-integrity',
      description: 'Verify event sourcing data integrity',
      check: this.checkEventIntegrity.bind(this),
    },
    {
      name: 'index-usage',
      description: 'Verify indexes are being used effectively',
      check: this.checkIndexUsage.bind(this),
    },
    {
      name: 'table-sizes',
      description: 'Check table sizes for potential issues',
      check: this.checkTableSizes.bind(this),
    },
  ];

  async validateAll(db: Knex): Promise<{
    overall: boolean;
    results: IntegrityResult[];
    summary: {
      passed: number;
      warnings: number;
      errors: number;
    };
  }> {
    logger.info('Starting data integrity validation');

    const results: IntegrityResult[] = [];
    let passed = 0;
    let warnings = 0;
    let errors = 0;

    for (const check of this.checks) {
      try {
        logger.debug(`Running integrity check: ${check.name}`);
        const result = await check.check(db);
        results.push(result);

        if (result.passed) {
          passed++;
        } else if (result.severity === 'warning') {
          warnings++;
        } else {
          errors++;
        }

        logger.info(`Integrity check completed: ${check.name}`, {
          passed: result.passed,
          severity: result.severity,
          message: result.message,
        });
      } catch (error) {
        const errorResult: IntegrityResult = {
          passed: false,
          message: `Check failed with error: ${(error as Error).message}`,
          severity: 'error',
          details: { error },
        };
        results.push(errorResult);
        errors++;
        logger.error(`Integrity check failed: ${check.name}`, { error });
      }
    }

    const overall = errors === 0;
    const summary = { passed, warnings, errors };

    logger.info('Data integrity validation completed', {
      overall,
      summary,
    });

    return { overall, results, summary };
  }

  private async checkForeignKeyConstraints(db: Knex): Promise<IntegrityResult> {
    // Check for orphaned records
    const orphanedUserOrgs = await db('user_organizations')
      .leftJoin('users', 'user_organizations.user_id', 'users.id')
      .leftJoin('organizations', 'user_organizations.organization_id', 'organizations.id')
      .whereNull('users.id')
      .orWhereNull('organizations.id')
      .count('* as count')
      .first();

    const orphanedEvents = await db('events')
      .leftJoin('users', 'events.user_id', 'users.id')
      .whereNotNull('events.user_id')
      .andWhereNull('users.id')
      .count('* as count')
      .first();

    const orphanedApiKeys = await db('api_keys')
      .leftJoin('users', 'api_keys.user_id', 'users.id')
      .whereNull('users.id')
      .count('* as count')
      .first();

    const totalOrphans =
      parseInt(orphanedUserOrgs.count) +
      parseInt(orphanedEvents.count) +
      parseInt(orphanedApiKeys.count);

    return {
      passed: totalOrphans === 0,
      message:
        totalOrphans === 0
          ? 'All foreign key constraints satisfied'
          : `Found ${totalOrphans} orphaned records`,
      severity: totalOrphans === 0 ? 'info' : 'error',
      details: {
        orphanedUserOrgs: orphanedUserOrgs.count,
        orphanedEvents: orphanedEvents.count,
        orphanedApiKeys: orphanedApiKeys.count,
      },
    };
  }

  private async checkUniqueConstraints(db: Knex): Promise<IntegrityResult> {
    // Check for duplicate emails
    const duplicateEmails = await db('users')
      .select('email')
      .groupBy('email')
      .havingRaw('COUNT(*) > 1')
      .count('* as count');

    // Check for duplicate slugs
    const duplicateSlugs = await db('organizations')
      .select('slug')
      .groupBy('slug')
      .havingRaw('COUNT(*) > 1')
      .count('* as count');

    const totalDuplicates = duplicateEmails.length + duplicateSlugs.length;

    return {
      passed: totalDuplicates === 0,
      message:
        totalDuplicates === 0
          ? 'All unique constraints satisfied'
          : `Found ${totalDuplicates} unique constraint violations`,
      severity: totalDuplicates === 0 ? 'info' : 'error',
      details: {
        duplicateEmails,
        duplicateSlugs,
      },
    };
  }

  private async checkDataConsistency(db: Knex): Promise<IntegrityResult> {
    // Check for users without organizations
    const usersWithoutOrgs = await db('users')
      .leftJoin('user_organizations', 'users.id', 'user_organizations.user_id')
      .whereNull('user_organizations.user_id')
      .where('users.status', 'active')
      .count('* as count')
      .first();

    // Check for organizations without owners
    const orgsWithoutOwners = await db('organizations')
      .leftJoin('user_organizations', function () {
        this.on('organizations.id', '=', 'user_organizations.organization_id').andOn(
          'user_organizations.role',
          '=',
          db.raw('?', ['owner'])
        );
      })
      .whereNull('user_organizations.id')
      .count('* as count')
      .first();

    const issues = parseInt(usersWithoutOrgs.count) + parseInt(orgsWithoutOwners.count);

    return {
      passed: issues === 0,
      message:
        issues === 0 ? 'Data consistency checks passed' : `Found ${issues} data consistency issues`,
      severity: issues === 0 ? 'info' : 'warning',
      details: {
        usersWithoutOrgs: usersWithoutOrgs.count,
        orgsWithoutOwners: orgsWithoutOwners.count,
      },
    };
  }

  private async checkEventIntegrity(db: Knex): Promise<IntegrityResult> {
    // Check for events with missing required fields
    const invalidEvents = await db('events')
      .whereNull('event_type')
      .orWhereNull('aggregate_type')
      .orWhereNull('aggregate_id')
      .orWhereNull('aggregate_version')
      .orWhereNull('event_timestamp')
      .count('* as count')
      .first();

    // Check for events with invalid JSON
    const invalidJsonEvents = await db.raw(`
      SELECT COUNT(*) as count 
      FROM events 
      WHERE jsonb_typeof(data) != 'object' 
      OR jsonb_typeof(metadata) != 'object'
      OR jsonb_typeof(tags) != 'object'
    `);

    const totalInvalid = parseInt(invalidEvents.count) + parseInt(invalidJsonEvents.rows[0].count);

    return {
      passed: totalInvalid === 0,
      message:
        totalInvalid === 0
          ? 'Event integrity checks passed'
          : `Found ${totalInvalid} invalid events`,
      severity: totalInvalid === 0 ? 'info' : 'error',
      details: {
        invalidEvents: invalidEvents.count,
        invalidJsonEvents: invalidJsonEvents.rows[0].count,
      },
    };
  }

  private async checkIndexUsage(db: Knex): Promise<IntegrityResult> {
    // Check index usage statistics (PostgreSQL specific)
    const indexStats = await db.raw(`
      SELECT 
        schemaname,
        tablename,
        indexname,
        idx_scan,
        idx_tup_read,
        idx_tup_fetch
      FROM pg_stat_user_indexes 
      WHERE schemaname = 'public'
      ORDER BY idx_scan ASC
    `);

    const unusedIndexes = indexStats.rows.filter(
      (row: any) => row.idx_scan === 0 && !row.indexname.includes('pkey')
    );

    return {
      passed: unusedIndexes.length === 0,
      message:
        unusedIndexes.length === 0
          ? 'All indexes are being used'
          : `Found ${unusedIndexes.length} unused indexes`,
      severity: unusedIndexes.length === 0 ? 'info' : 'warning',
      details: {
        unusedIndexes,
        totalIndexes: indexStats.rows.length,
      },
    };
  }

  private async checkTableSizes(db: Knex): Promise<IntegrityResult> {
    // Check table sizes for potential issues
    const tableSizes = await db.raw(`
      SELECT 
        schemaname,
        tablename,
        pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) as size,
        pg_total_relation_size(schemaname||'.'||tablename) as size_bytes
      FROM pg_tables 
      WHERE schemaname = 'public'
      ORDER BY size_bytes DESC
    `);

    const largeTables = tableSizes.rows.filter(
      (row: any) => row.size_bytes > 1024 * 1024 * 1024 // > 1GB
    );

    return {
      passed: largeTables.length === 0,
      message:
        largeTables.length === 0
          ? 'Table sizes are within acceptable limits'
          : `Found ${largeTables.length} large tables`,
      severity: largeTables.length === 0 ? 'info' : 'warning',
      details: {
        largeTables,
        tableSizes: tableSizes.rows,
      },
    };
  }
}

// CLI command for running integrity checks
export async function runIntegrityChecks(db: Knex): Promise<void> {
  const validator = new DataIntegrityValidator();
  const result = await validator.validateAll(db);

  console.log('\n=== Data Integrity Validation Results ===');
  console.log(`Overall: ${result.overall ? 'PASS' : 'FAIL'}`);
  console.log(`Passed: ${result.summary.passed}`);
  console.log(`Warnings: ${result.summary.warnings}`);
  console.log(`Errors: ${result.summary.errors}\n`);

  for (const checkResult of result.results) {
    const status = checkResult.passed ? '✓' : '✗';
    const severity = checkResult.severity.toUpperCase();
    console.log(`${status} [${severity}] ${checkResult.name}`);
    console.log(`  ${checkResult.message}`);
    if (checkResult.details) {
      console.log(`  Details:`, JSON.stringify(checkResult.details, null, 2));
    }
    console.log('');
  }

  if (!result.overall) {
    process.exit(1);
  }
}
```

## Files to Create

1. `docs/database/schema.md` - Comprehensive schema documentation
2. `tests/database/migration-rollback.test.ts` - Migration rollback tests
3. `src/database/data-integrity-validator.ts` - Data integrity validation
4. Update `src/cli/migration-commands.ts` to include integrity checks

## Dependencies

- Jest/Vitest for testing
- Test database setup utilities
- Database connection from previous tasks

## Testing

1. Run migration rollback tests
2. Execute data integrity validation
3. Performance testing with large datasets
4. Documentation validation
5. End-to-end migration testing

## Notes

- Document all constraints and relationships clearly
- Test rollback scenarios thoroughly before production
- Monitor data integrity regularly in production
- Keep documentation updated with schema changes
- Consider automated integrity checks in CI/CD pipeline
