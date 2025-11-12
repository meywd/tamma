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

export interface ValidationReport {
  overall: boolean;
  results: IntegrityResult[];
  summary: {
    passed: number;
    warnings: number;
    errors: number;
  };
  timestamp: string;
  duration: number;
}

/**
 * Data Integrity Validator
 * Performs comprehensive checks on database integrity, consistency, and performance
 */
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
    {
      name: 'required-fields',
      description: 'Verify required fields are not null',
      check: this.checkRequiredFields.bind(this),
    },
    {
      name: 'date-consistency',
      description: 'Check timestamp consistency',
      check: this.checkDateConsistency.bind(this),
    },
    {
      name: 'jsonb-structure',
      description: 'Validate JSONB field structures',
      check: this.checkJsonbStructure.bind(this),
    },
    {
      name: 'enum-values',
      description: 'Validate enum field values',
      check: this.checkEnumValues.bind(this),
    },
  ];

  /**
   * Run all integrity checks
   */
  async validateAll(db: Knex): Promise<ValidationReport> {
    const startTime = Date.now();
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
          details: { error: (error as Error).message, stack: (error as Error).stack },
        };
        results.push(errorResult);
        errors++;
        logger.error(`Integrity check failed: ${check.name}`, { error });
      }
    }

    const duration = Date.now() - startTime;
    const overall = errors === 0;
    const summary = { passed, warnings, errors };

    logger.info('Data integrity validation completed', {
      overall,
      summary,
      duration,
    });

    return {
      overall,
      results,
      summary,
      timestamp: new Date().toISOString(),
      duration,
    };
  }

  /**
   * Run specific integrity checks
   */
  async validate(db: Knex, checkNames: string[]): Promise<ValidationReport> {
    const startTime = Date.now();
    const filteredChecks = this.checks.filter(c => checkNames.includes(c.name));

    if (filteredChecks.length === 0) {
      throw new Error(`No valid checks found for: ${checkNames.join(', ')}`);
    }

    logger.info(`Running specific integrity checks: ${checkNames.join(', ')}`);

    const results: IntegrityResult[] = [];
    let passed = 0;
    let warnings = 0;
    let errors = 0;

    for (const check of filteredChecks) {
      try {
        const result = await check.check(db);
        results.push(result);

        if (result.passed) {
          passed++;
        } else if (result.severity === 'warning') {
          warnings++;
        } else {
          errors++;
        }
      } catch (error) {
        results.push({
          passed: false,
          message: `Check failed: ${(error as Error).message}`,
          severity: 'error',
        });
        errors++;
      }
    }

    const duration = Date.now() - startTime;

    return {
      overall: errors === 0,
      results,
      summary: { passed, warnings, errors },
      timestamp: new Date().toISOString(),
      duration,
    };
  }

  /**
   * Check foreign key constraints
   */
  private async checkForeignKeyConstraints(db: Knex): Promise<IntegrityResult> {
    const issues: any[] = [];

    // Check for orphaned user_organizations
    const userOrgsExists = await this.tableExists(db, 'user_organizations');
    if (userOrgsExists) {
      const orphanedUserOrgs = await db('user_organizations')
        .leftJoin('users', 'user_organizations.user_id', 'users.id')
        .leftJoin('organizations', 'user_organizations.organization_id', 'organizations.id')
        .whereNull('users.id')
        .orWhereNull('organizations.id')
        .select('user_organizations.id');

      if (orphanedUserOrgs.length > 0) {
        issues.push({
          table: 'user_organizations',
          orphaned: orphanedUserOrgs.length,
          ids: orphanedUserOrgs.map(r => r.id),
        });
      }
    }

    // Check for orphaned events
    const eventsExists = await this.tableExists(db, 'events');
    if (eventsExists) {
      const orphanedEvents = await db('events')
        .leftJoin('users', 'events.user_id', 'users.id')
        .whereNotNull('events.user_id')
        .whereNull('users.id')
        .select('events.id');

      if (orphanedEvents.length > 0) {
        issues.push({
          table: 'events',
          orphaned: orphanedEvents.length,
          ids: orphanedEvents.map((r: any) => r.id),
        });
      }
    }

    // Check for orphaned API keys
    const apiKeysExists = await this.tableExists(db, 'api_keys');
    if (apiKeysExists) {
      const orphanedApiKeys = await db('api_keys')
        .leftJoin('users', 'api_keys.user_id', 'users.id')
        .whereNull('users.id')
        .select('api_keys.id');

      if (orphanedApiKeys.length > 0) {
        issues.push({
          table: 'api_keys',
          orphaned: orphanedApiKeys.length,
          ids: orphanedApiKeys.map(r => r.id),
        });
      }
    }

    const totalOrphans = issues.reduce((sum, issue) => sum + issue.orphaned, 0);

    return {
      passed: totalOrphans === 0,
      message:
        totalOrphans === 0
          ? 'All foreign key constraints satisfied'
          : `Found ${totalOrphans} orphaned records across ${issues.length} tables`,
      severity: totalOrphans === 0 ? 'info' : 'error',
      details: { issues },
    };
  }

  /**
   * Check unique constraints
   */
  private async checkUniqueConstraints(db: Knex): Promise<IntegrityResult> {
    const duplicates: any[] = [];

    // Check for duplicate emails in users
    const usersExists = await this.tableExists(db, 'users');
    if (usersExists) {
      const duplicateEmails = await db('users')
        .select('email')
        .groupBy('email')
        .havingRaw('COUNT(*) > 1')
        .pluck('email');

      if (duplicateEmails.length > 0) {
        duplicates.push({
          table: 'users',
          column: 'email',
          values: duplicateEmails,
        });
      }
    }

    // Check for duplicate slugs in organizations
    const orgsExists = await this.tableExists(db, 'organizations');
    if (orgsExists) {
      const duplicateSlugs = await db('organizations')
        .select('slug')
        .groupBy('slug')
        .havingRaw('COUNT(*) > 1')
        .pluck('slug');

      if (duplicateSlugs.length > 0) {
        duplicates.push({
          table: 'organizations',
          column: 'slug',
          values: duplicateSlugs,
        });
      }
    }

    return {
      passed: duplicates.length === 0,
      message:
        duplicates.length === 0
          ? 'All unique constraints satisfied'
          : `Found ${duplicates.length} unique constraint violations`,
      severity: duplicates.length === 0 ? 'info' : 'error',
      details: { duplicates },
    };
  }

  /**
   * Check data consistency across tables
   */
  private async checkDataConsistency(db: Knex): Promise<IntegrityResult> {
    const issues: any[] = [];

    // Check for active users without organizations
    const usersExists = await this.tableExists(db, 'users');
    const userOrgsExists = await this.tableExists(db, 'user_organizations');

    if (usersExists && userOrgsExists) {
      const usersWithoutOrgs = await db('users')
        .leftJoin('user_organizations', 'users.id', 'user_organizations.user_id')
        .whereNull('user_organizations.user_id')
        .where('users.status', 'active')
        .pluck('users.email');

      if (usersWithoutOrgs.length > 0) {
        issues.push({
          type: 'users_without_organizations',
          count: usersWithoutOrgs.length,
          samples: usersWithoutOrgs.slice(0, 5),
        });
      }
    }

    // Check for organizations without owners
    const orgsExists = await this.tableExists(db, 'organizations');
    if (orgsExists && userOrgsExists) {
      const orgsWithoutOwners = await db('organizations')
        .leftJoin('user_organizations', function () {
          this.on('organizations.id', '=', 'user_organizations.organization_id').andOn(
            'user_organizations.role',
            '=',
            db.raw('?', ['owner'])
          );
        })
        .whereNull('user_organizations.id')
        .pluck('organizations.slug');

      if (orgsWithoutOwners.length > 0) {
        issues.push({
          type: 'organizations_without_owners',
          count: orgsWithoutOwners.length,
          samples: orgsWithoutOwners.slice(0, 5),
        });
      }
    }

    const severity = issues.length === 0 ? 'info' : issues.length > 5 ? 'error' : 'warning';

    return {
      passed: issues.length === 0,
      message:
        issues.length === 0
          ? 'Data consistency checks passed'
          : `Found ${issues.length} data consistency issues`,
      severity,
      details: { issues },
    };
  }

  /**
   * Check event integrity
   */
  private async checkEventIntegrity(db: Knex): Promise<IntegrityResult> {
    const eventsExists = await this.tableExists(db, 'events');
    if (!eventsExists) {
      return {
        passed: true,
        message: 'Events table does not exist yet',
        severity: 'info',
      };
    }

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
    let invalidJsonCount = 0;
    try {
      const invalidJsonEvents = await db.raw(`
        SELECT COUNT(*) as count
        FROM events
        WHERE jsonb_typeof(data) != 'object'
        OR jsonb_typeof(metadata) != 'object'
        OR jsonb_typeof(tags) != 'object'
      `);
      invalidJsonCount = parseInt(invalidJsonEvents.rows[0].count);
    } catch (error) {
      logger.debug('Could not check JSONB types', { error });
    }

    const totalInvalid = parseInt(invalidEvents?.count as string || '0') + invalidJsonCount;

    return {
      passed: totalInvalid === 0,
      message:
        totalInvalid === 0
          ? 'Event integrity checks passed'
          : `Found ${totalInvalid} invalid events`,
      severity: totalInvalid === 0 ? 'info' : 'error',
      details: {
        missingFields: invalidEvents?.count || 0,
        invalidJson: invalidJsonCount,
      },
    };
  }

  /**
   * Check index usage statistics
   */
  private async checkIndexUsage(db: Knex): Promise<IntegrityResult> {
    try {
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
        LIMIT 20
      `);

      const unusedIndexes = indexStats.rows.filter(
        (row: any) => row.idx_scan === '0' && !row.indexname.includes('pkey')
      );

      return {
        passed: unusedIndexes.length === 0,
        message:
          unusedIndexes.length === 0
            ? 'All indexes are being used'
            : `Found ${unusedIndexes.length} potentially unused indexes`,
        severity: unusedIndexes.length === 0 ? 'info' : 'warning',
        details: {
          unusedIndexes: unusedIndexes.map((idx: any) => ({
            table: idx.tablename,
            index: idx.indexname,
          })),
          totalIndexes: indexStats.rows.length,
        },
      };
    } catch (error) {
      return {
        passed: true,
        message: 'Index usage check not available',
        severity: 'info',
        details: { error: (error as Error).message },
      };
    }
  }

  /**
   * Check table sizes
   */
  private async checkTableSizes(db: Knex): Promise<IntegrityResult> {
    try {
      const tableSizes = await db.raw(`
        SELECT
          schemaname,
          tablename,
          pg_size_pretty(pg_total_relation_size(schemaname||'.'||tablename)) as size,
          pg_total_relation_size(schemaname||'.'||tablename) as size_bytes
        FROM pg_tables
        WHERE schemaname = 'public'
        ORDER BY size_bytes DESC
        LIMIT 10
      `);

      const largeTables = tableSizes.rows.filter(
        (row: any) => row.size_bytes > 1024 * 1024 * 1024 // > 1GB
      );

      return {
        passed: largeTables.length === 0,
        message:
          largeTables.length === 0
            ? 'Table sizes are within acceptable limits'
            : `Found ${largeTables.length} large tables (>1GB)`,
        severity: largeTables.length === 0 ? 'info' : 'warning',
        details: {
          largeTables: largeTables.map((t: any) => ({
            table: t.tablename,
            size: t.size,
          })),
          topTables: tableSizes.rows.slice(0, 5).map((t: any) => ({
            table: t.tablename,
            size: t.size,
          })),
        },
      };
    } catch (error) {
      return {
        passed: true,
        message: 'Table size check not available',
        severity: 'info',
        details: { error: (error as Error).message },
      };
    }
  }

  /**
   * Check required fields are not null
   */
  private async checkRequiredFields(db: Knex): Promise<IntegrityResult> {
    const issues: any[] = [];

    // Check organizations required fields
    const orgsExists = await this.tableExists(db, 'organizations');
    if (orgsExists) {
      const orgsWithNullName = await db('organizations')
        .whereNull('name')
        .orWhere('name', '')
        .count('* as count')
        .first();

      if (parseInt(orgsWithNullName?.count as string || '0') > 0) {
        issues.push({
          table: 'organizations',
          field: 'name',
          count: orgsWithNullName?.count,
        });
      }

      const orgsWithNullSlug = await db('organizations')
        .whereNull('slug')
        .orWhere('slug', '')
        .count('* as count')
        .first();

      if (parseInt(orgsWithNullSlug?.count as string || '0') > 0) {
        issues.push({
          table: 'organizations',
          field: 'slug',
          count: orgsWithNullSlug?.count,
        });
      }
    }

    // Check users required fields
    const usersExists = await this.tableExists(db, 'users');
    if (usersExists) {
      const usersWithNullEmail = await db('users')
        .whereNull('email')
        .orWhere('email', '')
        .count('* as count')
        .first();

      if (parseInt(usersWithNullEmail?.count as string || '0') > 0) {
        issues.push({
          table: 'users',
          field: 'email',
          count: usersWithNullEmail?.count,
        });
      }
    }

    return {
      passed: issues.length === 0,
      message:
        issues.length === 0
          ? 'All required fields have values'
          : `Found ${issues.length} required fields with null/empty values`,
      severity: issues.length === 0 ? 'info' : 'error',
      details: { issues },
    };
  }

  /**
   * Check timestamp consistency
   */
  private async checkDateConsistency(db: Knex): Promise<IntegrityResult> {
    const issues: any[] = [];

    // Check if updated_at is before created_at
    const tables = ['organizations', 'users', 'user_organizations', 'events', 'api_keys'];

    for (const table of tables) {
      const exists = await this.tableExists(db, table);
      if (!exists) continue;

      const hasCreatedAt = await this.columnExists(db, table, 'created_at');
      const hasUpdatedAt = await this.columnExists(db, table, 'updated_at');

      if (hasCreatedAt && hasUpdatedAt) {
        const invalidDates = await db(table)
          .whereRaw('updated_at < created_at')
          .count('* as count')
          .first();

        if (parseInt(invalidDates?.count as string || '0') > 0) {
          issues.push({
            table,
            issue: 'updated_at before created_at',
            count: invalidDates?.count,
          });
        }
      }
    }

    return {
      passed: issues.length === 0,
      message:
        issues.length === 0
          ? 'Timestamp consistency checks passed'
          : `Found ${issues.length} timestamp inconsistencies`,
      severity: issues.length === 0 ? 'info' : 'warning',
      details: { issues },
    };
  }

  /**
   * Check JSONB field structures
   */
  private async checkJsonbStructure(db: Knex): Promise<IntegrityResult> {
    const issues: any[] = [];

    try {
      // Check organizations settings/metadata are valid JSON objects
      const orgsExists = await this.tableExists(db, 'organizations');
      if (orgsExists) {
        const invalidJsonOrgs = await db.raw(`
          SELECT COUNT(*) as count
          FROM organizations
          WHERE jsonb_typeof(settings) != 'object'
          OR jsonb_typeof(metadata) != 'object'
        `);

        if (parseInt(invalidJsonOrgs.rows[0].count) > 0) {
          issues.push({
            table: 'organizations',
            fields: ['settings', 'metadata'],
            count: invalidJsonOrgs.rows[0].count,
          });
        }
      }
    } catch (error) {
      logger.debug('JSONB structure check not available', { error });
    }

    return {
      passed: issues.length === 0,
      message:
        issues.length === 0
          ? 'JSONB structures are valid'
          : `Found ${issues.length} invalid JSONB structures`,
      severity: issues.length === 0 ? 'info' : 'warning',
      details: { issues },
    };
  }

  /**
   * Check enum field values
   */
  private async checkEnumValues(db: Knex): Promise<IntegrityResult> {
    const issues: any[] = [];

    // Check organization status values
    const orgsExists = await this.tableExists(db, 'organizations');
    if (orgsExists) {
      const validStatuses = ['active', 'inactive', 'suspended', 'deleted'];
      const invalidStatusOrgs = await db('organizations')
        .whereNotIn('status', validStatuses)
        .count('* as count')
        .first();

      if (parseInt(invalidStatusOrgs?.count as string || '0') > 0) {
        issues.push({
          table: 'organizations',
          field: 'status',
          count: invalidStatusOrgs?.count,
        });
      }
    }

    // Check user status values
    const usersExists = await this.tableExists(db, 'users');
    if (usersExists && await this.columnExists(db, 'users', 'status')) {
      const validUserStatuses = ['active', 'inactive', 'suspended', 'deleted'];
      const invalidStatusUsers = await db('users')
        .whereNotIn('status', validUserStatuses)
        .count('* as count')
        .first();

      if (parseInt(invalidStatusUsers?.count as string || '0') > 0) {
        issues.push({
          table: 'users',
          field: 'status',
          count: invalidStatusUsers?.count,
        });
      }
    }

    return {
      passed: issues.length === 0,
      message:
        issues.length === 0
          ? 'All enum values are valid'
          : `Found ${issues.length} invalid enum values`,
      severity: issues.length === 0 ? 'info' : 'error',
      details: { issues },
    };
  }

  /**
   * Helper: Check if table exists
   */
  private async tableExists(db: Knex, tableName: string): Promise<boolean> {
    try {
      const result = await db.raw(`
        SELECT EXISTS (
          SELECT FROM information_schema.tables
          WHERE table_schema = 'public'
          AND table_name = ?
        )
      `, [tableName]);
      return result.rows[0].exists;
    } catch (error) {
      return false;
    }
  }

  /**
   * Helper: Check if column exists
   */
  private async columnExists(db: Knex, tableName: string, columnName: string): Promise<boolean> {
    try {
      const result = await db.raw(`
        SELECT EXISTS (
          SELECT FROM information_schema.columns
          WHERE table_schema = 'public'
          AND table_name = ?
          AND column_name = ?
        )
      `, [tableName, columnName]);
      return result.rows[0].exists;
    } catch (error) {
      return false;
    }
  }
}

/**
 * CLI command for running integrity checks
 */
export async function runIntegrityChecks(db: Knex, checks?: string[]): Promise<void> {
  const validator = new DataIntegrityValidator();
  const result = checks
    ? await validator.validate(db, checks)
    : await validator.validateAll(db);

  console.log('\n=== Data Integrity Validation Results ===');
  console.log(`Timestamp: ${result.timestamp}`);
  console.log(`Duration: ${result.duration}ms`);
  console.log(`Overall: ${result.overall ? 'PASS' : 'FAIL'}`);
  console.log(`Passed: ${result.summary.passed}`);
  console.log(`Warnings: ${result.summary.warnings}`);
  console.log(`Errors: ${result.summary.errors}\n`);

  for (const checkResult of result.results) {
    const status = checkResult.passed ? '✓' : '✗';
    const severity = checkResult.severity.toUpperCase();
    console.log(`${status} [${severity}] ${checkResult.message}`);
    if (checkResult.details && Object.keys(checkResult.details).length > 0) {
      console.log(`  Details:`, JSON.stringify(checkResult.details, null, 2));
    }
    console.log('');
  }

  if (!result.overall) {
    process.exit(1);
  }
}

/**
 * Generate integrity report
 */
export async function generateIntegrityReport(
  db: Knex,
  outputPath?: string
): Promise<ValidationReport> {
  const validator = new DataIntegrityValidator();
  const report = await validator.validateAll(db);

  if (outputPath) {
    const fs = await import('fs/promises');
    await fs.writeFile(outputPath, JSON.stringify(report, null, 2));
    logger.info(`Integrity report saved to: ${outputPath}`);
  }

  return report;
}