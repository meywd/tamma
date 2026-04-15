import type pg from 'pg';
import type { IProvidersConfig } from '@tamma/shared';
import type { IUserStore, User, UserInstallation, UpsertUserInput, ListUsersOptions, ListUsersResult, CreateEmailUserInput, AuthMethod } from './user-store.js';

/** PostgreSQL-backed user store. */
export class PgUserStore implements IUserStore {
  constructor(private readonly pool: pg.Pool) {}

  async upsertUser(user: UpsertUserInput): Promise<User> {
    // Note: settings is only set on INSERT (new user). On conflict (existing user),
    // settings are preserved — use updateUserSettings() to change them.
    const settings = user.settings ?? { providers: {} };
    const tenantId = user.tenantId ?? null;
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO users (github_id, github_login, email, role, settings, tenant_id, email_verified, auth_method)
       VALUES ($1, $2, $3, $4, $5, $6, true, 'github')
       ON CONFLICT (github_id)
       DO UPDATE SET github_login = $2, email = COALESCE($3, users.email), updated_at = NOW()
       RETURNING *`,
      [user.githubId, user.githubLogin, user.email, user.role, JSON.stringify(settings), tenantId],
    );
    return this.mapUser(result.rows[0]!);
  }

  async getUser(id: string): Promise<User | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM users WHERE id = $1',
      [id],
    );
    if (result.rows.length === 0) return null;
    return this.mapUser(result.rows[0]!);
  }

  async getUserByGithubId(githubId: number): Promise<User | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM users WHERE github_id = $1',
      [githubId],
    );
    if (result.rows.length === 0) return null;
    return this.mapUser(result.rows[0]!);
  }

  async linkUserToInstallation(userId: string, installationId: number, role: 'owner' | 'admin' | 'member'): Promise<void> {
    await this.pool.query(
      `INSERT INTO user_installations (user_id, installation_id, role)
       VALUES ($1, $2, $3)
       ON CONFLICT (user_id, installation_id)
       DO UPDATE SET role = $3`,
      [userId, installationId, role],
    );
  }

  async getUserInstallations(userId: string): Promise<UserInstallation[]> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM user_installations WHERE user_id = $1',
      [userId],
    );
    return result.rows.map((r) => ({
      userId: String(r['user_id']),
      installationId: Number(r['installation_id']),
      role: String(r['role']) as 'owner' | 'admin' | 'member',
      createdAt: String(r['created_at']),
    }));
  }

  async getUserSettings(userId: string): Promise<IProvidersConfig> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT settings FROM users WHERE id = $1',
      [userId],
    );
    if (result.rows.length === 0) {
      return { providers: {} };
    }
    const settings = result.rows[0]!['settings'];
    if (settings && typeof settings === 'object') {
      return settings as IProvidersConfig;
    }
    return { providers: {} };
  }

  async updateUserSettings(userId: string, settings: IProvidersConfig): Promise<IProvidersConfig> {
    // Validation is owned by ConfigService — the store trusts its caller.
    const result = await this.pool.query<Record<string, unknown>>(
      'UPDATE users SET settings = $1, updated_at = NOW() WHERE id = $2 RETURNING settings',
      [JSON.stringify(settings), userId],
    );
    if (result.rows.length === 0) {
      throw new Error(`User not found: ${userId}`);
    }
    return result.rows[0]!['settings'] as IProvidersConfig;
  }

  async listUsers(options: ListUsersOptions): Promise<ListUsersResult> {
    const conditions = ['deleted_at IS NULL'];
    const params: unknown[] = [];
    let paramIndex = 1;

    if (options.role !== undefined) {
      conditions.push(`role = $${paramIndex}`);
      params.push(options.role);
      paramIndex++;
    }

    const where = conditions.join(' AND ');

    const countResult = await this.pool.query<Record<string, unknown>>(
      `SELECT COUNT(*)::int AS total FROM users WHERE ${where}`,
      params,
    );
    const total = Number(countResult.rows[0]!['total']);

    const dataResult = await this.pool.query<Record<string, unknown>>(
      `SELECT * FROM users WHERE ${where} ORDER BY created_at DESC LIMIT $${paramIndex} OFFSET $${paramIndex + 1}`,
      [...params, options.limit, options.offset],
    );

    const users = dataResult.rows.map((r) => this.mapUser(r));
    return { users, total };
  }

  async updateUserRole(id: string, role: 'owner' | 'admin' | 'member'): Promise<User> {
    const result = await this.pool.query<Record<string, unknown>>(
      'UPDATE users SET role = $1, updated_at = NOW() WHERE id = $2 AND deleted_at IS NULL RETURNING *',
      [role, id],
    );
    if (result.rows.length === 0) {
      throw new Error(`User not found: ${id}`);
    }
    return this.mapUser(result.rows[0]!);
  }

  async deleteUser(id: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE users SET deleted_at = NOW(), updated_at = NOW() WHERE id = $1 AND deleted_at IS NULL',
      [id],
    );
    if (result.rowCount === 0) {
      throw new Error(`User not found: ${id}`);
    }
  }

  async updateLastActive(id: string): Promise<void> {
    await this.pool.query(
      'UPDATE users SET last_active_at = NOW() WHERE id = $1 AND deleted_at IS NULL',
      [id],
    );
  }

  async unlinkAllInstallations(userId: string): Promise<void> {
    await this.pool.query(
      'DELETE FROM user_installations WHERE user_id = $1',
      [userId],
    );
  }

  // --- Story 18-1: Email auth methods ---

  async createEmailUser(input: CreateEmailUserInput): Promise<User> {
    const normalizedEmail = input.email.toLowerCase().trim();
    const result = await this.pool.query<Record<string, unknown>>(
      `INSERT INTO users (github_id, github_login, email, role, settings, password_hash, email_verified, auth_method, email_verification_token_hash, email_verification_expires_at)
       VALUES (NULL, $1, $2, 'member', '{"providers":{}}', $3, false, 'email', $4, $5)
       RETURNING *`,
      [input.name, normalizedEmail, input.passwordHash, input.emailVerificationTokenHash, input.emailVerificationExpiresAt],
    );
    return this.mapUser(result.rows[0]!);
  }

  async getUserByEmail(email: string): Promise<User | null> {
    const normalized = email.toLowerCase().trim();
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM users WHERE LOWER(email) = $1 AND deleted_at IS NULL',
      [normalized],
    );
    if (result.rows.length === 0) return null;
    return this.mapUser(result.rows[0]!);
  }

  async setEmailVerified(userId: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE users SET email_verified = true, email_verification_token_hash = NULL, email_verification_expires_at = NULL, updated_at = NOW() WHERE id = $1',
      [userId],
    );
    if (result.rowCount === 0) {
      throw new Error(`User not found: ${userId}`);
    }
  }

  async updateVerificationToken(userId: string, tokenHash: string, expiresAt: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE users SET email_verification_token_hash = $1, email_verification_expires_at = $2, updated_at = NOW() WHERE id = $3',
      [tokenHash, expiresAt, userId],
    );
    if (result.rowCount === 0) {
      throw new Error(`User not found: ${userId}`);
    }
  }

  async updatePasswordHash(userId: string, passwordHash: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE users SET password_hash = $1, updated_at = NOW() WHERE id = $2',
      [passwordHash, userId],
    );
    if (result.rowCount === 0) {
      throw new Error(`User not found: ${userId}`);
    }
  }

  async updateActiveTenant(userId: string, tenantId: string | null): Promise<void> {
    const result = await this.pool.query(
      'UPDATE users SET tenant_id = $1, updated_at = NOW() WHERE id = $2',
      [tenantId, userId],
    );
    if (result.rowCount === 0) {
      throw new Error(`User not found: ${userId}`);
    }
  }

  async updateAuthMethod(userId: string, authMethod: AuthMethod): Promise<void> {
    const result = await this.pool.query(
      'UPDATE users SET auth_method = $1, updated_at = NOW() WHERE id = $2',
      [authMethod, userId],
    );
    if (result.rowCount === 0) {
      throw new Error(`User not found: ${userId}`);
    }
  }

  async setGithubId(userId: string, githubId: number, githubLogin: string): Promise<void> {
    const result = await this.pool.query(
      'UPDATE users SET github_id = $1, github_login = $2, updated_at = NOW() WHERE id = $3',
      [githubId, githubLogin, userId],
    );
    if (result.rowCount === 0) {
      throw new Error(`User not found: ${userId}`);
    }
  }

  private mapUser(row: Record<string, unknown>): User {
    const rawSettings = row['settings'];
    const settings: IProvidersConfig = rawSettings && typeof rawSettings === 'object'
      ? rawSettings as IProvidersConfig
      : { providers: {} };

    return {
      id: String(row['id']),
      githubId: row['github_id'] !== null && row['github_id'] !== undefined ? Number(row['github_id']) : null,
      githubLogin: String(row['github_login'] ?? ''),
      email: row['email'] !== null && row['email'] !== undefined ? String(row['email']) : null,
      role: String(row['role']) as 'owner' | 'admin' | 'member',
      tenantId: row['tenant_id'] !== null && row['tenant_id'] !== undefined ? String(row['tenant_id']) : null,
      settings,
      lastActiveAt: row['last_active_at'] !== null && row['last_active_at'] !== undefined
        ? String(row['last_active_at'])
        : null,
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
      passwordHash: row['password_hash'] !== null && row['password_hash'] !== undefined
        ? String(row['password_hash'])
        : null,
      emailVerified: Boolean(row['email_verified']),
      authMethod: (String(row['auth_method'] ?? 'github')) as AuthMethod,
      emailVerificationTokenHash: row['email_verification_token_hash'] !== null && row['email_verification_token_hash'] !== undefined
        ? String(row['email_verification_token_hash'])
        : null,
      emailVerificationExpiresAt: row['email_verification_expires_at'] !== null && row['email_verification_expires_at'] !== undefined
        ? String(row['email_verification_expires_at'])
        : null,
    };
  }
}
