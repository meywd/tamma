# Database Schema Documentation

## Overview

The Test Platform uses PostgreSQL 17 with a comprehensive schema designed for multi-tenant operations, event sourcing, and audit trails. The schema follows the DCB (Dynamic Consistency Boundary) pattern for complete auditability.

## Entity Relationship Diagram (ERD)

```
┌─────────────────┐     ┌─────────────────────┐     ┌─────────────────┐
│  organizations  │────│  user_organizations  │────│      users       │
├─────────────────┤     ├─────────────────────┤     ├─────────────────┤
│ id (UUID) PK    │────│ id (UUID) PK         │────│ id (UUID) PK     │
│ name            │     │ user_id (UUID) FK    │     │ email            │
│ slug            │     │ organization_id FK   │     │ password_hash    │
│ description     │     │ role                 │     │ first_name       │
│ domain          │     │ status               │     │ last_name        │
│ email           │     │ joined_at            │     │ status           │
│ settings (JSONB)│     │ created_at           │     │ role             │
│ metadata (JSONB)│     └─────────────────────┘     │ created_at       │
│ status          │                                  │ updated_at       │
│ created_at      │                                  └─────────────────┘
│ updated_at      │                                          │
└─────────────────┘                                          │
        │                                                     │
        │                                                     │
        ▼                                                     ▼
┌─────────────────┐                                  ┌─────────────────┐
│     events      │                                  │    api_keys     │
├─────────────────┤                                  ├─────────────────┤
│ id (UUID) PK    │                                  │ id (UUID) PK    │
│ event_type      │                                  │ key_id          │
│ aggregate_type  │                                  │ key_hash        │
│ aggregate_id    │                                  │ user_id FK      │
│ data (JSONB)    │                                  │ organization_id │
│ tags (JSONB)    │                                  │ name            │
│ user_id FK      │                                  │ status          │
│ organization_id │                                  │ expires_at      │
│ event_timestamp │                                  │ created_at      │
│ created_at      │                                  │ last_used_at    │
│ processed       │                                  └─────────────────┘
│ processed_at    │
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

## Migration Management

### Migration Strategy
- Sequential versioned migrations
- Transactional DDL operations
- Rollback capability for each migration
- Automated integrity checks after migrations

### Version Tracking
- Knex migration table tracks applied migrations
- Each migration has up/down functions
- Batch and timestamp tracking
- Lock mechanism prevents concurrent migrations

## Data Integrity Rules

### Referential Integrity
- Cascade deletes for dependent records
- Foreign key constraints on all relationships
- Null checks for required fields
- Default values for optional fields

### Business Logic Constraints
- Email uniqueness across users
- Organization slug uniqueness
- Valid status transitions
- Timestamp consistency (created < updated)

### Data Validation
- Email format validation
- URL format for domains/websites
- JSONB structure validation
- Enum value constraints

## Monitoring and Maintenance

### Key Metrics
- Table sizes and growth rates
- Index usage statistics
- Query performance metrics
- Connection pool utilization

### Regular Maintenance Tasks
- Vacuum and analyze operations
- Index rebuilding
- Statistics updates
- Dead tuple cleanup

### Backup Strategy
- Point-in-time recovery enabled
- Daily automated backups
- Transaction log archiving
- Geo-replicated backup storage

## Future Considerations

### Scalability Planning
- Horizontal partitioning for large tables
- Read replica configuration
- Sharding strategy for multi-region
- Event stream partitioning

### Schema Evolution
- Backward compatible migrations
- Feature flag controlled changes
- Gradual column deprecation
- Zero-downtime migration patterns

### Performance Enhancements
- Query result caching
- Materialized view strategies
- Connection pooler optimization
- Index-only scan optimization