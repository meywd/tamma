# Implementation Plan: Task 3 - Core Schema Creation

**Story**: 1.1 Database Schema Migration System  
**Task**: 3 - Core Schema Creation  
**Acceptance Criteria**: #3, #4 - Core tables created: organizations, users, user_organizations, events, api_keys; Event sourcing table (events) with DCB pattern implementation

## Overview

Create the core database schema with all essential tables for the test platform, implementing the DCB (Dynamic Consistency Boundary) event sourcing pattern.

## Implementation Steps

### Subtask 3.1: Create organizations table with UUID primary key

**Objective**: Create organizations table for multi-tenant support

**Migration File**: `database/migrations/20240101000001_create_organizations.ts`

```typescript
import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('organizations', (table) => {
    // Primary key
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Organization details
    table.string('name', 255).notNullable();
    table.string('slug', 100).unique().notNullable(); // URL-friendly identifier
    table.text('description').nullable();
    table.string('domain', 255).nullable(); // For SSO integration

    // Contact information
    table.string('email', 255).nullable();
    table.string('phone', 50).nullable();
    table.string('website', 500).nullable();

    // Address
    table.string('address_line1', 255).nullable();
    table.string('address_line2', 255).nullable();
    table.string('city', 100).nullable();
    table.string('state', 100).nullable();
    table.string('country', 100).nullable();
    table.string('postal_code', 20).nullable();

    // Configuration (JSONB for flexibility)
    table.jsonb('settings').defaultTo('{}');
    table.jsonb('metadata').defaultTo('{}');

    // Status and lifecycle
    table.enum('status', ['active', 'inactive', 'suspended', 'deleted']).defaultTo('active');
    table.string('subscription_tier', 50).defaultTo('free');
    table.timestamp('subscription_expires_at').nullable();

    // Audit fields
    table.timestamp('created_at').defaultTo(knex.fn.now());
    table.timestamp('updated_at').defaultTo(knex.fn.now());
    table.uuid('created_by').nullable().references('id').inTable('users');
    table.uuid('updated_by').nullable().references('id').inTable('users');

    // Indexes
    table.index(['slug']);
    table.index(['status']);
    table.index(['subscription_tier']);
    table.index(['created_at']);

    // Constraints
    table.check('name IS NOT NULL AND length(trim(name)) > 0');
    table.check("slug IS NOT NULL AND slug ~ '^[a-z0-9-]+$';");
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('organizations');
}
```

### Subtask 3.2: Create users table with authentication fields

**Objective**: Create users table with authentication and profile information

**Migration File**: `database/migrations/20240101000002_create_users.ts`

```typescript
import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('users', (table) => {
    // Primary key
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Authentication fields
    table.string('email', 255).unique().notNullable();
    table.string('password_hash', 255).notNullable(); // bcrypt hash
    table.string('password_salt', 255).notNullable();
    table.string('password_reset_token', 255).nullable().unique();
    table.timestamp('password_reset_expires_at').nullable();
    table.string('email_verification_token', 255).nullable().unique();
    table.timestamp('email_verified_at').nullable();

    // Profile information
    table.string('first_name', 100).nullable();
    table.string('last_name', 100).nullable();
    table.string('username', 100).unique().nullable();
    table.text('bio').nullable();
    table.string('avatar_url', 500).nullable();

    // Preferences and settings
    table.jsonb('preferences').defaultTo('{}');
    table.jsonb('settings').defaultTo('{}');
    table.string('timezone', 50).defaultTo('UTC');
    table.string('language', 10).defaultTo('en');

    // Status and roles
    table.enum('status', ['active', 'inactive', 'suspended', 'deleted']).defaultTo('active');
    table.enum('role', ['user', 'admin', 'super_admin']).defaultTo('user');
    table.boolean('email_verified').defaultTo(false);
    table.boolean('mfa_enabled').defaultTo(false);
    table.string('mfa_secret', 255).nullable();

    // Security
    table.string('last_login_ip', 45).nullable(); // IPv6 compatible
    table.string('current_login_ip', 45).nullable();
    table.timestamp('last_login_at').nullable();
    table.timestamp('current_login_at').nullable();
    table.integer('failed_login_attempts').defaultTo(0);
    table.timestamp('locked_until').nullable();

    // Audit fields
    table.timestamp('created_at').defaultTo(knex.fn.now());
    table.timestamp('updated_at').defaultTo(knex.fn.now());
    table.uuid('created_by').nullable().references('id').inTable('users');
    table.uuid('updated_by').nullable().references('id').inTable('users');

    // Indexes
    table.index(['email']);
    table.index(['username']);
    table.index(['status']);
    table.index(['role']);
    table.index(['created_at']);
    table.index(['last_login_at']);

    // Constraints
    table.check(
      "email IS NOT NULL AND email ~ '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}$'"
    );
    table.check('password_hash IS NOT NULL AND length(password_hash) > 0');
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('users');
}
```

### Subtask 3.3: Create user_organizations join table

**Objective**: Create many-to-many relationship between users and organizations

**Migration File**: `database/migrations/20240101000003_create_user_organizations.ts`

```typescript
import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('user_organizations', (table) => {
    // Primary key
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Foreign keys
    table.uuid('user_id').notNullable().references('id').inTable('users').onDelete('CASCADE');
    table
      .uuid('organization_id')
      .notNullable()
      .references('id')
      .inTable('organizations')
      .onDelete('CASCADE');

    // Role and permissions within organization
    table.enum('role', ['member', 'admin', 'owner']).defaultTo('member');
    table.jsonb('permissions').defaultTo('{}');

    // Status and invitation
    table.enum('status', ['active', 'pending', 'invited', 'left']).defaultTo('pending');
    table.string('invitation_token', 255).nullable().unique();
    table.timestamp('invitation_expires_at').nullable();
    table.uuid('invited_by').nullable().references('id').inTable('users');

    // Membership details
    table.string('job_title', 255).nullable();
    table.string('department', 255).nullable();
    table.jsonb('metadata').defaultTo('{}');

    // Audit fields
    table.timestamp('joined_at').nullable();
    table.timestamp('left_at').nullable();
    table.timestamp('created_at').defaultTo(knex.fn.now());
    table.timestamp('updated_at').defaultTo(knex.fn.now());

    // Indexes
    table.index(['user_id']);
    table.index(['organization_id']);
    table.index(['status']);
    table.index(['role']);
    table.unique(['user_id', 'organization_id']); // Prevent duplicate memberships

    // Constraints
    table.check('user_id IS NOT NULL');
    table.check('organization_id IS NOT NULL');
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('user_organizations');
}
```

### Subtask 3.4: Create events table for DCB event sourcing

**Objective**: Create events table implementing DCB pattern for audit trail

**Migration File**: `database/migrations/20240101000004_create_events.ts`

```typescript
import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('events', (table) => {
    // Primary key - UUID v7 for time-sortable IDs
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Event identification
    table.string('event_type', 255).notNullable(); // e.g., "USER.CREATED", "ORG.UPDATED"
    table.string('aggregate_type', 100).notNullable(); // e.g., "user", "organization"
    table.uuid('aggregate_id').notNullable(); // ID of the entity
    table.string('aggregate_version', 50).notNullable(); // Version of the aggregate

    // Event data
    table.jsonb('data').notNullable(); // Event payload
    table.jsonb('metadata').defaultTo('{}'); // Event metadata

    // DCB Pattern - Flexible tagging system
    table.jsonb('tags').defaultTo('{}'); // Flexible tags for querying

    // Context information
    table.uuid('user_id').nullable().references('id').inTable('users');
    table.uuid('organization_id').nullable().references('id').inTable('organizations');
    table.string('session_id', 255).nullable();
    table.string('correlation_id', 255).nullable(); // For tracing related events
    table.string('causation_id', 255).nullable(); // For event causality

    // System information
    table.string('source', 100).defaultTo('system'); // system, api, cli, webhook
    table.string('version', 50).nullable(); // API version or system version
    table.string('ip_address', 45).nullable();
    table.string('user_agent', 500).nullable();

    // Timestamps (DCB requires precise timing)
    table.timestamp('event_timestamp').notNullable().defaultTo(knex.fn.now());
    table.timestamp('created_at').defaultTo(knex.fn.now());

    // Processing information
    table.boolean('processed').defaultTo(false);
    table.timestamp('processed_at').nullable();
    table.integer('retry_count').defaultTo(0);
    table.string('error_message', 1000).nullable();

    // Indexes for performance
    table.index(['event_type']);
    table.index(['aggregate_type', 'aggregate_id']);
    table.index(['aggregate_id', 'aggregate_version']);
    table.index(['event_timestamp']); // Time-series queries
    table.index(['user_id']);
    table.index(['organization_id']);
    table.index(['correlation_id']);
    table.index(['causation_id']);
    table.index(['source']);
    table.index(['processed']);

    // GIN indexes for JSONB tags and data
    table.index(['tags'], { using: 'gin' });
    table.index(['data'], { using: 'gin' });

    // Composite indexes for common query patterns
    table.index(['aggregate_type', 'event_timestamp']);
    table.index(['organization_id', 'event_timestamp']);
    table.index(['user_id', 'event_timestamp']);

    // Constraints
    table.check('event_type IS NOT NULL AND length(trim(event_type)) > 0');
    table.check('aggregate_type IS NOT NULL AND length(trim(aggregate_type)) > 0');
    table.check('aggregate_id IS NOT NULL');
    table.check('aggregate_version IS NOT NULL');
    table.check('event_timestamp IS NOT NULL');
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('events');
}
```

### Subtask 3.5: Create api_keys table for authentication

**Objective**: Create API keys table for programmatic access

**Migration File**: `database/migrations/20240101000005_create_api_keys.ts`

```typescript
import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  return knex.schema.createTable('api_keys', (table) => {
    // Primary key
    table.uuid('id').primary().defaultTo(knex.raw('gen_random_uuid()'));

    // Key information
    table.string('key_id', 100).unique().notNullable(); // Public identifier
    table.string('key_hash', 255).unique().notNullable(); // Hashed key
    table.string('key_prefix', 20).notNullable(); // First few characters for identification

    // Ownership
    table.uuid('user_id').notNullable().references('id').inTable('users').onDelete('CASCADE');
    table
      .uuid('organization_id')
      .nullable()
      .references('id')
      .inTable('organizations')
      .onDelete('CASCADE');

    // Key details
    table.string('name', 255).notNullable();
    table.text('description').nullable();
    table.enum('key_type', ['personal', 'service', 'integration']).defaultTo('personal');

    // Permissions and scopes
    table.jsonb('permissions').defaultTo('{}');
    table.jsonb('scopes').defaultTo('[]'); // Array of allowed scopes
    table.string('allowed_ips', 1000).nullable(); // Comma-separated IP addresses
    table.string('allowed_domains', 1000).nullable(); // Comma-separated domains

    // Status and lifecycle
    table.enum('status', ['active', 'inactive', 'revoked', 'expired']).defaultTo('active');
    table.timestamp('expires_at').nullable();
    table.timestamp('last_used_at').nullable();
    table.string('last_used_ip', 45).nullable();
    table.string('last_used_user_agent', 500).nullable();

    // Usage tracking
    table.integer('usage_count').defaultTo(0);
    table.timestamp('usage_reset_at').nullable();
    table.integer('usage_limit').nullable(); // Monthly usage limit

    // Security
    table.string('created_from_ip', 45).nullable();
    table.string('created_from_user_agent', 500).nullable();
    table.boolean('require_mfa').defaultTo(false);

    // Audit fields
    table.timestamp('created_at').defaultTo(knex.fn.now());
    table.timestamp('updated_at').defaultTo(knex.fn.now());
    table.uuid('created_by').nullable().references('id').inTable('users');
    table.uuid('updated_by').nullable().references('id').inTable('users');

    // Indexes
    table.index(['key_id']);
    table.index(['key_hash']);
    table.index(['user_id']);
    table.index(['organization_id']);
    table.index(['status']);
    table.index(['key_type']);
    table.index(['expires_at']);
    table.index(['last_used_at']);
    table.index(['created_at']);

    // Constraints
    table.check('key_id IS NOT NULL AND length(trim(key_id)) > 0');
    table.check('key_hash IS NOT NULL AND length(key_hash) > 0');
    table.check('key_prefix IS NOT NULL AND length(key_prefix) > 0');
    table.check('name IS NOT NULL AND length(trim(name)) > 0');
    table.check('user_id IS NOT NULL');
  });
}

export async function down(knex: Knex): Promise<void> {
  return knex.schema.dropTableIfExists('api_keys');
}
```

## Additional Schema Files

### Create TypeScript interfaces (`src/models/database.types.ts`)

```typescript
export interface Organization {
  id: string;
  name: string;
  slug: string;
  description?: string;
  domain?: string;
  email?: string;
  phone?: string;
  website?: string;
  address_line1?: string;
  address_line2?: string;
  city?: string;
  state?: string;
  country?: string;
  postal_code?: string;
  settings: Record<string, any>;
  metadata: Record<string, any>;
  status: 'active' | 'inactive' | 'suspended' | 'deleted';
  subscription_tier: string;
  subscription_expires_at?: Date;
  created_at: Date;
  updated_at: Date;
  created_by?: string;
  updated_by?: string;
}

export interface User {
  id: string;
  email: string;
  password_hash: string;
  password_salt: string;
  password_reset_token?: string;
  password_reset_expires_at?: Date;
  email_verification_token?: string;
  email_verified_at?: Date;
  first_name?: string;
  last_name?: string;
  username?: string;
  bio?: string;
  avatar_url?: string;
  preferences: Record<string, any>;
  settings: Record<string, any>;
  timezone: string;
  language: string;
  status: 'active' | 'inactive' | 'suspended' | 'deleted';
  role: 'user' | 'admin' | 'super_admin';
  email_verified: boolean;
  mfa_enabled: boolean;
  mfa_secret?: string;
  last_login_ip?: string;
  current_login_ip?: string;
  last_login_at?: Date;
  current_login_at?: Date;
  failed_login_attempts: number;
  locked_until?: Date;
  created_at: Date;
  updated_at: Date;
  created_by?: string;
  updated_by?: string;
}

export interface Event {
  id: string;
  event_type: string;
  aggregate_type: string;
  aggregate_id: string;
  aggregate_version: string;
  data: Record<string, any>;
  metadata: Record<string, any>;
  tags: Record<string, any>;
  user_id?: string;
  organization_id?: string;
  session_id?: string;
  correlation_id?: string;
  causation_id?: string;
  source: string;
  version?: string;
  ip_address?: string;
  user_agent?: string;
  event_timestamp: Date;
  created_at: Date;
  processed: boolean;
  processed_at?: Date;
  retry_count: number;
  error_message?: string;
}
```

## Files to Create

1. `database/migrations/20240101000001_create_organizations.ts`
2. `database/migrations/20240101000002_create_users.ts`
3. `database/migrations/20240101000003_create_user_organizations.ts`
4. `database/migrations/20240101000004_create_events.ts`
5. `database/migrations/20240101000005_create_api_keys.ts`
6. `src/models/database.types.ts` - TypeScript interfaces

## Dependencies

- Knex.js migration framework
- PostgreSQL 17 with UUID extension
- Database connection from Task 2

## Testing

1. Unit tests for each migration
2. Integration tests with test database
3. Foreign key constraint validation
4. Index performance testing
5. DCB event pattern validation

## Notes

- All tables use UUID primary keys with gen_random_uuid()
- JSONB fields for flexible schema evolution
- Comprehensive indexing for query performance
- Row-level security policies will be added in Task 4
- Event table implements full DCB pattern with time-series optimization
