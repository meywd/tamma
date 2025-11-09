# Implementation Plan: Task 2 - Multi-Tenancy Data Isolation

**Story**: 1.3 Organization Management & Multi-Tenancy  
**Task**: 2 - Multi-Tenancy Data Isolation  
**Acceptance Criteria**: #4 - Organization-scoped data isolation in all queries

## Overview

Implement comprehensive multi-tenancy with Row-Level Security (RLS), tenant context middleware, and organization-scoped query filtering.

## Implementation Steps

### Subtask 2.1: Implement Row-Level Security (RLS) policies

**Objective**: Create PostgreSQL RLS policies for tenant data isolation

**Migration File**: `database/migrations/20240101000007_enable_rls_policies.ts`

```typescript
import { Knex } from 'knex';

export async function up(knex: Knex): Promise<void> {
  // Enable RLS on all tenant-scoped tables
  await knex.raw('ALTER TABLE organizations ENABLE ROW LEVEL SECURITY');
  await knex.raw('ALTER TABLE users ENABLE ROW LEVEL SECURITY');
  await knex.raw('ALTER TABLE user_organizations ENABLE ROW LEVEL SECURITY');
  await knex.raw('ALTER TABLE events ENABLE ROW LEVEL SECURITY');
  await knex.raw('ALTER TABLE api_keys ENABLE ROW LEVEL SECURITY');

  // Organizations table policies
  await knex.raw(`
    CREATE POLICY org_isolation_policy ON organizations
    FOR ALL
    TO authenticated_user
    USING (
      id IN (
        SELECT organization_id 
        FROM user_organizations 
        WHERE user_id = current_setting('app.current_user_id', true)::uuid 
        AND status = 'active'
      )
    )
    WITH CHECK (
      id IN (
        SELECT organization_id 
        FROM user_organizations 
        WHERE user_id = current_setting('app.current_user_id', true)::uuid 
        AND status = 'active'
      )
    )
  `);

  // Users table policies
  await knex.raw(`
    CREATE POLICY user_isolation_policy ON users
    FOR SELECT
    TO authenticated_user
    USING (
      id = current_setting('app.current_user_id', true)::uuid
      OR id IN (
        SELECT user_id 
        FROM user_organizations 
        WHERE organization_id = current_setting('app.current_organization_id', true)::uuid 
        AND status = 'active'
      )
    )
  `);

  await knex.raw(`
    CREATE POLICY user_self_update_policy ON users
    FOR UPDATE
    TO authenticated_user
    USING (
      id = current_setting('app.current_user_id', true)::uuid
    )
    WITH CHECK (
      id = current_setting('app.current_user_id', true)::uuid
    )
  `);

  // User organizations table policies
  await knex.raw(`
    CREATE POLICY user_org_isolation_policy ON user_organizations
    FOR ALL
    TO authenticated_user
    USING (
      user_id = current_setting('app.current_user_id', true)::uuid
      OR organization_id = current_setting('app.current_organization_id', true)::uuid
    )
    WITH CHECK (
      user_id = current_setting('app.current_user_id', true)::uuid
      OR organization_id = current_setting('app.current_organization_id', true)::uuid
    )
  `);

  // Events table policies
  await knex.raw(`
    CREATE POLICY events_isolation_policy ON events
    FOR SELECT
    TO authenticated_user
    USING (
      user_id = current_setting('app.current_user_id', true)::uuid
      OR organization_id = current_setting('app.current_organization_id', true)::uuid
      OR (user_id IS NULL AND organization_id IS NULL) -- System events
    )
  `);

  await knex.raw(`
    CREATE POLICY events_insert_policy ON events
    FOR INSERT
    TO authenticated_user
    WITH CHECK (
      user_id = current_setting('app.current_user_id', true)::uuid
      OR organization_id = current_setting('app.current_organization_id', true)::uuid
    )
  `);

  // API keys table policies
  await knex.raw(`
    CREATE POLICY api_keys_isolation_policy ON api_keys
    FOR ALL
    TO authenticated_user
    USING (
      user_id = current_setting('app.current_user_id', true)::uuid
      OR (
        organization_id = current_setting('app.current_organization_id', true)::uuid
        AND current_setting('app.current_user_role', true) IN ('admin', 'owner')
      )
    )
    WITH CHECK (
      user_id = current_setting('app.current_user_id', true)::uuid
      OR (
        organization_id = current_setting('app.current_organization_id', true)::uuid
        AND current_setting('app.current_user_role', true) IN ('admin', 'owner')
      )
    )
  `);

  // Create custom role for authenticated users
  await knex.raw(`
    DO $$
    BEGIN
      IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'authenticated_user') THEN
        CREATE ROLE authenticated_user;
      END IF;
    END
    $$;
  `);

  // Grant usage to authenticated users
  await knex.raw('GRANT USAGE ON SCHEMA public TO authenticated_user');
  await knex.raw('GRANT SELECT ON ALL TABLES IN SCHEMA public TO authenticated_user');
  await knex.raw('GRANT INSERT ON ALL TABLES IN SCHEMA public TO authenticated_user');
  await knex.raw('GRANT UPDATE ON ALL TABLES IN SCHEMA public TO authenticated_user');
  await knex.raw('GRANT DELETE ON ALL TABLES IN SCHEMA public TO authenticated_user');
  await knex.raw('GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO authenticated_user');
}

export async function down(knex: Knex): Promise<void> {
  // Drop all policies
  await knex.raw('DROP POLICY IF EXISTS org_isolation_policy ON organizations');
  await knex.raw('DROP POLICY IF EXISTS user_isolation_policy ON users');
  await knex.raw('DROP POLICY IF EXISTS user_self_update_policy ON users');
  await knex.raw('DROP POLICY IF EXISTS user_org_isolation_policy ON user_organizations');
  await knex.raw('DROP POLICY IF EXISTS events_isolation_policy ON events');
  await knex.raw('DROP POLICY IF EXISTS events_insert_policy ON events');
  await knex.raw('DROP POLICY IF EXISTS api_keys_isolation_policy ON api_keys');

  // Disable RLS
  await knex.raw('ALTER TABLE organizations DISABLE ROW LEVEL SECURITY');
  await knex.raw('ALTER TABLE users DISABLE ROW LEVEL SECURITY');
  await knex.raw('ALTER TABLE user_organizations DISABLE ROW LEVEL SECURITY');
  await knex.raw('ALTER TABLE events DISABLE ROW LEVEL SECURITY');
  await knex.raw('ALTER TABLE api_keys DISABLE ROW LEVEL SECURITY');

  // Drop role
  await knex.raw('DROP ROLE IF EXISTS authenticated_user');
}
```

### Subtask 2.2: Create tenant context middleware

**Objective**: Implement middleware to inject tenant context into database sessions

**File**: `src/middleware/tenant-context.ts`

```typescript
import { Request, Response, NextFunction } from 'express';
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface TenantContext {
  userId: string;
  organizationId?: string;
  role?: string;
  permissions?: string[];
}

export class TenantContextMiddleware {
  // Main middleware to set tenant context
  static setTenantContext() {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const user = req.user;

        if (!user) {
          return next();
        }

        // Set user context
        await db.raw('SET app.current_user_id = ?', [user.sub]);

        // Set organization context if available
        if (user.organizationId) {
          await db.raw('SET app.current_organization_id = ?', [user.organizationId]);
        }

        // Set user role if available
        if (user.role) {
          await db.raw('SET app.current_user_role = ?', [user.role]);
        }

        // Set session identifier for audit
        const sessionId = req.sessionID || req.headers['x-session-id'] || 'anonymous';
        await db.raw('SET app.current_session_id = ?', [sessionId]);

        // Set request context
        await db.raw('SET app.current_ip_address = ?', [req.ip]);
        await db.raw('SET app.current_user_agent = ?', [req.get('User-Agent')]);

        logger.debug('Tenant context set', {
          userId: user.sub,
          organizationId: user.organizationId,
          role: user.role,
        });

        next();
      } catch (error) {
        logger.error('Failed to set tenant context', { error });
        next();
      }
    };
  }

  // Middleware to require organization context
  static requireOrganization() {
    return (req: Request, res: Response, next: NextFunction): void => {
      if (!req.user?.organizationId) {
        throw new ApiError(400, 'Organization context is required');
      }
      next();
    };
  }

  // Middleware to switch organization context
  static switchOrganization() {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const { organizationId } = req.body;
        const userId = req.user!.sub;

        if (!organizationId) {
          throw new ApiError(400, 'Organization ID is required');
        }

        // Verify user is member of organization
        const membership = await db('user_organizations')
          .where('user_id', userId)
          .where('organization_id', organizationId)
          .where('status', 'active')
          .first();

        if (!membership) {
          throw new ApiError(403, 'You are not a member of this organization');
        }

        // Update organization context
        await db.raw('SET app.current_organization_id = ?', [organizationId]);
        await db.raw('SET app.current_user_role = ?', [membership.role]);

        // Update request user object
        req.user.organizationId = organizationId;
        req.user.role = membership.role;

        logger.info('Organization context switched', {
          userId,
          organizationId,
          role: membership.role,
        });

        res.json({
          message: 'Organization context switched successfully',
          organizationId,
          role: membership.role,
        });
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
          });
        } else {
          logger.error('Failed to switch organization context', { error });
          res.status(500).json({ error: 'Internal server error' });
        }
      }
    };
  }

  // Middleware to clear tenant context
  static clearTenantContext() {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        await db.raw('RESET app.current_user_id');
        await db.raw('RESET app.current_organization_id');
        await db.raw('RESET app.current_user_role');
        await db.raw('RESET app.current_session_id');
        await db.raw('RESET app.current_ip_address');
        await db.raw('RESET app.current_user_agent');

        logger.debug('Tenant context cleared');

        next();
      } catch (error) {
        logger.error('Failed to clear tenant context', { error });
        next();
      }
    };
  }

  // Helper to get current tenant context
  static async getCurrentContext(): Promise<TenantContext | null> {
    try {
      const result = await db.raw(`
        SELECT 
          current_setting('app.current_user_id', true) as user_id,
          current_setting('app.current_organization_id', true) as organization_id,
          current_setting('app.current_user_role', true) as user_role,
          current_setting('app.current_session_id', true) as session_id
      `);

      const row = result.rows[0];

      if (!row.user_id) {
        return null;
      }

      return {
        userId: row.user_id,
        organizationId: row.organization_id || undefined,
        role: row.user_role || undefined,
      };
    } catch (error) {
      logger.error('Failed to get current tenant context', { error });
      return null;
    }
  }

  // Helper to check if user has access to organization
  static async checkOrganizationAccess(userId: string, organizationId: string): Promise<boolean> {
    try {
      const membership = await db('user_organizations')
        .where('user_id', userId)
        .where('organization_id', organizationId)
        .where('status', 'active')
        .first();

      return !!membership;
    } catch (error) {
      logger.error('Failed to check organization access', { error });
      return false;
    }
  }

  // Helper to get user's organizations
  static async getUserOrganizations(userId: string): Promise<
    Array<{
      id: string;
      name: string;
      role: string;
    }>
  > {
    try {
      return await db('organizations')
        .join('user_organizations', 'organizations.id', 'user_organizations.organization_id')
        .where('user_organizations.user_id', userId)
        .where('user_organizations.status', 'active')
        .where('organizations.status', 'active')
        .select(['organizations.id', 'organizations.name', 'user_organizations.role']);
    } catch (error) {
      logger.error('Failed to get user organizations', { error });
      return [];
    }
  }
}
```

### Subtask 2.3: Add organization-scoped query filtering

**Objective**: Create query builder with automatic tenant filtering

**File**: `src/services/tenant-query-service.ts`

```typescript
import { Knex } from 'knex';
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface TenantQueryOptions {
  organizationId?: string;
  userId?: string;
  includeSystemData?: boolean;
  bypassRls?: boolean;
}

export class TenantQueryService {
  // Create tenant-aware query builder
  static createQuery(table: string, options: TenantQueryOptions = {}): Knex.QueryBuilder {
    let query = db(table);

    // If bypassing RLS (for system operations)
    if (options.bypassRls) {
      query = query.withSchema('public');
    }

    // Add organization filtering if specified
    if (options.organizationId && this.isTenantTable(table)) {
      query = query.where('organization_id', options.organizationId);
    }

    // Add user filtering if specified
    if (options.userId && this.isUserScopedTable(table)) {
      query = query.where('user_id', options.userId);
    }

    return query;
  }

  // Create tenant-aware count query
  static createCountQuery(table: string, options: TenantQueryOptions = {}): Knex.QueryBuilder {
    let query = db(table).count('* as count');

    if (options.bypassRls) {
      query = query.withSchema('public');
    }

    if (options.organizationId && this.isTenantTable(table)) {
      query = query.where('organization_id', options.organizationId);
    }

    if (options.userId && this.isUserScopedTable(table)) {
      query = query.where('user_id', options.userId);
    }

    return query;
  }

  // Execute query with tenant context
  static async executeWithTenant<T>(
    queryFn: () => Knex.QueryBuilder,
    tenantContext: {
      userId: string;
      organizationId?: string;
      role?: string;
    }
  ): Promise<T[]> {
    try {
      // Set tenant context for this query
      await db.raw('SET LOCAL app.current_user_id = ?', [tenantContext.userId]);

      if (tenantContext.organizationId) {
        await db.raw('SET LOCAL app.current_organization_id = ?', [tenantContext.organizationId]);
      }

      if (tenantContext.role) {
        await db.raw('SET LOCAL app.current_user_role = ?', [tenantContext.role]);
      }

      // Execute query
      const query = queryFn();
      const result = await query;

      return result;
    } catch (error) {
      logger.error('Failed to execute query with tenant context', { error });
      throw new ApiError(500, 'Query execution failed');
    }
  }

  // Check if table is tenant-scoped
  private static isTenantTable(table: string): boolean {
    const tenantTables = [
      'organizations',
      'user_organizations',
      'events',
      'api_keys',
      'benchmarks',
      'benchmark_results',
      'projects',
      'datasets',
      'reports',
    ];

    return tenantTables.includes(table);
  }

  // Check if table is user-scoped
  private static isUserScopedTable(table: string): boolean {
    const userTables = ['users', 'api_keys', 'user_sessions', 'user_preferences'];

    return userTables.includes(table);
  }

  // Create organization-scoped subquery
  static createOrganizationSubquery(
    table: string,
    alias: string,
    organizationId: string
  ): Knex.QueryBuilder {
    return db(table).where('organization_id', organizationId).as(alias);
  }

  // Create user-scoped subquery
  static createUserSubquery(table: string, alias: string, userId: string): Knex.QueryBuilder {
    return db(table).where('user_id', userId).as(alias);
  }

  // Get tenant-aware statistics
  static async getTenantStatistics(
    organizationId: string,
    userId?: string
  ): Promise<{
    totalUsers: number;
    totalProjects: number;
    totalBenchmarks: number;
    totalApiCalls: number;
    storageUsed: number;
  }> {
    try {
      const queries = [
        this.createCountQuery('user_organizations', { organizationId }),
        this.createCountQuery('projects', { organizationId }),
        this.createCountQuery('benchmarks', { organizationId }),
        this.createCountQuery('api_keys', { organizationId }),
      ];

      const [usersResult, projectsResult, benchmarksResult, apiKeysResult] = await Promise.all(
        queries.map((q) => q.first())
      );

      // Get storage usage (would need to query actual storage service)
      const storageUsed = await this.getStorageUsage(organizationId);

      return {
        totalUsers: parseInt(usersResult?.count || '0'),
        totalProjects: parseInt(projectsResult?.count || '0'),
        totalBenchmarks: parseInt(benchmarksResult?.count || '0'),
        totalApiCalls: parseInt(apiKeysResult?.count || '0'),
        storageUsed,
      };
    } catch (error) {
      logger.error('Failed to get tenant statistics', { error, organizationId });
      throw new ApiError(500, 'Failed to get statistics');
    }
  }

  // Validate tenant access
  static async validateTenantAccess(
    userId: string,
    organizationId: string,
    resourceType: string,
    resourceId?: string
  ): Promise<boolean> {
    try {
      // Check if user is member of organization
      const membership = await db('user_organizations')
        .where('user_id', userId)
        .where('organization_id', organizationId)
        .where('status', 'active')
        .first();

      if (!membership) {
        return false;
      }

      // If no specific resource, organization membership is sufficient
      if (!resourceId) {
        return true;
      }

      // Check specific resource access
      return await this.checkResourceAccess(
        userId,
        organizationId,
        resourceType,
        resourceId,
        membership.role
      );
    } catch (error) {
      logger.error('Failed to validate tenant access', { error });
      return false;
    }
  }

  private static async checkResourceAccess(
    userId: string,
    organizationId: string,
    resourceType: string,
    resourceId: string,
    userRole: string
  ): Promise<boolean> {
    switch (resourceType) {
      case 'benchmark':
        return await this.checkBenchmarkAccess(userId, organizationId, resourceId, userRole);
      case 'project':
        return await this.checkProjectAccess(userId, organizationId, resourceId, userRole);
      case 'api_key':
        return await this.checkApiKeyAccess(userId, organizationId, resourceId, userRole);
      default:
        return true; // Unknown resource type, allow by default
    }
  }

  private static async checkBenchmarkAccess(
    userId: string,
    organizationId: string,
    benchmarkId: string,
    userRole: string
  ): Promise<boolean> {
    const benchmark = await db('benchmarks')
      .where('id', benchmarkId)
      .where('organization_id', organizationId)
      .first();

    if (!benchmark) {
      return false;
    }

    // Owner can always access
    if (benchmark.created_by === userId) {
      return true;
    }

    // Admins and owners can access all benchmarks
    return ['admin', 'owner'].includes(userRole);
  }

  private static async checkProjectAccess(
    userId: string,
    organizationId: string,
    projectId: string,
    userRole: string
  ): Promise<boolean> {
    const project = await db('projects')
      .where('id', projectId)
      .where('organization_id', organizationId)
      .first();

    if (!project) {
      return false;
    }

    // Owner can always access
    if (project.created_by === userId) {
      return true;
    }

    // Check project membership
    const membership = await db('project_members')
      .where('project_id', projectId)
      .where('user_id', userId)
      .first();

    return !!membership || ['admin', 'owner'].includes(userRole);
  }

  private static async checkApiKeyAccess(
    userId: string,
    organizationId: string,
    apiKeyId: string,
    userRole: string
  ): Promise<boolean> {
    const apiKey = await db('api_keys')
      .where('id', apiKeyId)
      .where('organization_id', organizationId)
      .first();

    if (!apiKey) {
      return false;
    }

    // Owner can always access
    if (apiKey.user_id === userId) {
      return true;
    }

    // Admins and owners can manage organization API keys
    return ['admin', 'owner'].includes(userRole);
  }

  private static async getStorageUsage(organizationId: string): Promise<number> {
    // This would integrate with your storage service
    // For now, return mock data
    return 1024 * 1024 * 100; // 100MB
  }
}
```

### Subtask 2.4: Implement Redis namespacing by organization

**Objective**: Create Redis key namespacing for multi-tenant data isolation

**File**: `src/services/tenant-redis-service.ts`

```typescript
import Redis from 'ioredis';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface TenantRedisOptions {
  organizationId?: string;
  userId?: string;
  ttl?: number;
}

export class TenantRedisService {
  private redis: Redis;
  private readonly DEFAULT_TTL = 3600; // 1 hour

  constructor(redisUrl?: string) {
    this.redis = new Redis(redisUrl || process.env.REDIS_URL);

    this.redis.on('error', (error) => {
      logger.error('Redis connection error', { error });
    });
  }

  // Create namespaced key
  private createKey(key: string, options: TenantRedisOptions): string {
    const parts = ['tp']; // Test Platform prefix

    if (options.organizationId) {
      parts.push('org', options.organizationId);
    }

    if (options.userId) {
      parts.push('user', options.userId);
    }

    parts.push(key);

    return parts.join(':');
  }

  // Set value with tenant namespace
  async set(key: string, value: any, options: TenantRedisOptions = {}): Promise<void> {
    try {
      const namespacedKey = this.createKey(key, options);
      const serializedValue = JSON.stringify(value);
      const ttl = options.ttl || this.DEFAULT_TTL;

      await this.redis.setex(namespacedKey, ttl, serializedValue);

      logger.debug('Redis value set with tenant namespace', {
        key: namespacedKey,
        organizationId: options.organizationId,
        userId: options.userId,
        ttl,
      });
    } catch (error) {
      logger.error('Failed to set Redis value', { error, key, options });
      throw new ApiError(500, 'Failed to cache data');
    }
  }

  // Get value with tenant namespace
  async get<T>(key: string, options: TenantRedisOptions = {}): Promise<T | null> {
    try {
      const namespacedKey = this.createKey(key, options);
      const value = await this.redis.get(namespacedKey);

      if (value === null) {
        return null;
      }

      return JSON.parse(value) as T;
    } catch (error) {
      logger.error('Failed to get Redis value', { error, key, options });
      return null;
    }
  }

  // Delete value with tenant namespace
  async del(key: string, options: TenantRedisOptions = {}): Promise<void> {
    try {
      const namespacedKey = this.createKey(key, options);
      await this.redis.del(namespacedKey);

      logger.debug('Redis value deleted with tenant namespace', {
        key: namespacedKey,
        organizationId: options.organizationId,
        userId: options.userId,
      });
    } catch (error) {
      logger.error('Failed to delete Redis value', { error, key, options });
      throw new ApiError(500, 'Failed to delete cached data');
    }
  }

  // Increment counter with tenant namespace
  async incr(key: string, options: TenantRedisOptions = {}): Promise<number> {
    try {
      const namespacedKey = this.createKey(key, options);
      return await this.redis.incr(namespacedKey);
    } catch (error) {
      logger.error('Failed to increment Redis value', { error, key, options });
      throw new ApiError(500, 'Failed to increment counter');
    }
  }

  // Add to sorted set with tenant namespace
  async zadd(
    key: string,
    score: number,
    member: string,
    options: TenantRedisOptions = {}
  ): Promise<void> {
    try {
      const namespacedKey = this.createKey(key, options);
      await this.redis.zadd(namespacedKey, score, member);

      if (options.ttl) {
        await this.redis.expire(namespacedKey, options.ttl);
      }
    } catch (error) {
      logger.error('Failed to add to Redis sorted set', { error, key, options });
      throw new ApiError(500, 'Failed to add to sorted set');
    }
  }

  // Get range from sorted set with tenant namespace
  async zrange(
    key: string,
    start: number,
    stop: number,
    options: TenantRedisOptions = {}
  ): Promise<string[]> {
    try {
      const namespacedKey = this.createKey(key, options);
      return await this.redis.zrange(namespacedKey, start, stop);
    } catch (error) {
      logger.error('Failed to get Redis sorted set range', { error, key, options });
      return [];
    }
  }

  // Add to list with tenant namespace
  async lpush(key: string, ...values: string[]): Promise<void> {
    try {
      const namespacedKey = this.createKey(key, {});
      await this.redis.lpush(namespacedKey, ...values);
    } catch (error) {
      logger.error('Failed to push to Redis list', { error, key });
      throw new ApiError(500, 'Failed to push to list');
    }
  }

  // Get from list with tenant namespace
  async lrange(
    key: string,
    start: number,
    stop: number,
    options: TenantRedisOptions = {}
  ): Promise<string[]> {
    try {
      const namespacedKey = this.createKey(key, options);
      return await this.redis.lrange(namespacedKey, start, stop);
    } catch (error) {
      logger.error('Failed to get Redis list range', { error, key, options });
      return [];
    }
  }

  // Set hash field with tenant namespace
  async hset(
    key: string,
    field: string,
    value: any,
    options: TenantRedisOptions = {}
  ): Promise<void> {
    try {
      const namespacedKey = this.createKey(key, options);
      const serializedValue = JSON.stringify(value);
      await this.redis.hset(namespacedKey, field, serializedValue);

      if (options.ttl) {
        await this.redis.expire(namespacedKey, options.ttl);
      }
    } catch (error) {
      logger.error('Failed to set Redis hash field', { error, key, field, options });
      throw new ApiError(500, 'Failed to set hash field');
    }
  }

  // Get hash field with tenant namespace
  async hget<T>(key: string, field: string, options: TenantRedisOptions = {}): Promise<T | null> {
    try {
      const namespacedKey = this.createKey(key, options);
      const value = await this.redis.hget(namespacedKey, field);

      if (value === null) {
        return null;
      }

      return JSON.parse(value) as T;
    } catch (error) {
      logger.error('Failed to get Redis hash field', { error, key, field, options });
      return null;
    }
  }

  // Get all hash fields with tenant namespace
  async hgetall(key: string, options: TenantRedisOptions = {}): Promise<Record<string, any>> {
    try {
      const namespacedKey = this.createKey(key, options);
      const hash = await this.redis.hgetall(namespacedKey);

      // Parse all JSON values
      const parsedHash: Record<string, any> = {};
      for (const [field, value] of Object.entries(hash)) {
        try {
          parsedHash[field] = JSON.parse(value);
        } catch {
          parsedHash[field] = value;
        }
      }

      return parsedHash;
    } catch (error) {
      logger.error('Failed to get Redis hash all', { error, key, options });
      return {};
    }
  }

  // Clear all data for organization
  async clearOrganizationData(organizationId: string): Promise<void> {
    try {
      const pattern = `tp:org:${organizationId}:*`;
      const keys = await this.redis.keys(pattern);

      if (keys.length > 0) {
        await this.redis.del(...keys);
        logger.info('Cleared organization data from Redis', {
          organizationId,
          deletedKeys: keys.length,
        });
      }
    } catch (error) {
      logger.error('Failed to clear organization data from Redis', { error, organizationId });
      throw new ApiError(500, 'Failed to clear organization data');
    }
  }

  // Clear all data for user
  async clearUserData(userId: string): Promise<void> {
    try {
      const patterns = [`tp:user:${userId}:*`, `tp:org:*:user:${userId}:*`];

      for (const pattern of patterns) {
        const keys = await this.redis.keys(pattern);
        if (keys.length > 0) {
          await this.redis.del(...keys);
        }
      }

      logger.info('Cleared user data from Redis', { userId });
    } catch (error) {
      logger.error('Failed to clear user data from Redis', { error, userId });
      throw new ApiError(500, 'Failed to clear user data');
    }
  }

  // Get tenant statistics from Redis
  async getTenantStats(organizationId: string): Promise<{
    activeUsers: number;
    apiCalls: number;
    cacheHitRate: number;
  }> {
    try {
      const keys = [
        `tp:org:${organizationId}:active_users`,
        `tp:org:${organizationId}:api_calls`,
        `tp:org:${organizationId}:cache_stats`,
      ];

      const values = await this.redis.mget(...keys);

      return {
        activeUsers: parseInt(values[0] || '0'),
        apiCalls: parseInt(values[1] || '0'),
        cacheHitRate: parseFloat(values[2] || '0'),
      };
    } catch (error) {
      logger.error('Failed to get tenant stats from Redis', { error, organizationId });
      return {
        activeUsers: 0,
        apiCalls: 0,
        cacheHitRate: 0,
      };
    }
  }

  // Close Redis connection
  async close(): Promise<void> {
    await this.redis.quit();
  }
}
```

## Files to Create

1. `database/migrations/20240101000007_enable_rls_policies.ts` - RLS policies
2. `src/middleware/tenant-context.ts` - Tenant context middleware
3. `src/services/tenant-query-service.ts` - Tenant-aware queries
4. `src/services/tenant-redis-service.ts` - Redis namespacing
5. Update existing services to use tenant-aware queries

## Dependencies

- PostgreSQL 17 with RLS support
- Redis for caching and session management
- Express.js for middleware
- Database connection for query execution

## Testing

1. RLS policy effectiveness tests
2. Tenant context middleware tests
3. Query isolation tests
4. Redis namespacing tests
5. Cross-tenant data leak tests

## Notes

- Test RLS policies thoroughly in development
- Use database transactions for complex multi-table operations
- Monitor RLS performance impact
- Implement proper error handling for tenant context failures
- Consider using connection pooling with tenant context
- Add comprehensive logging for tenant operations
