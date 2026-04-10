import type pg from 'pg';
import type {
  IGitHubInstallationStore,
  GitHubInstallation,
  GitHubInstallationRepo,
  UpsertInstallationInput,
} from './installation-store.js';

/** PostgreSQL-backed installation store. */
export class PgInstallationStore implements IGitHubInstallationStore {
  constructor(private readonly pool: pg.Pool) {}

  async upsertInstallation(installation: UpsertInstallationInput): Promise<void> {
    const tenantId = installation.tenantId ?? '00000000-0000-0000-0000-000000000000';
    await this.pool.query(
      `INSERT INTO github_installations (installation_id, account_login, account_type, app_id, permissions, suspended_at, api_key_hash, api_key_prefix, api_key_encrypted, tenant_id)
       VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
       ON CONFLICT (installation_id)
       DO UPDATE SET account_login = $2, account_type = $3, permissions = $5, suspended_at = $6,
                     api_key_hash = COALESCE($7, github_installations.api_key_hash),
                     api_key_prefix = COALESCE($8, github_installations.api_key_prefix),
                     api_key_encrypted = COALESCE($9, github_installations.api_key_encrypted),
                     tenant_id = $10,
                     updated_at = NOW()`,
      [
        installation.installationId,
        installation.accountLogin,
        installation.accountType,
        installation.appId,
        JSON.stringify(installation.permissions),
        installation.suspendedAt,
        installation.apiKeyHash,
        installation.apiKeyPrefix,
        installation.apiKeyEncrypted,
        tenantId,
      ],
    );
  }

  async removeInstallation(installationId: number): Promise<void> {
    await this.pool.query('DELETE FROM github_installations WHERE installation_id = $1', [installationId]);
  }

  async getInstallation(installationId: number): Promise<GitHubInstallation | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM github_installations WHERE installation_id = $1',
      [installationId],
    );
    if (result.rows.length === 0) return null;
    return this.mapInstallation(result.rows[0]!);
  }

  async listInstallations(): Promise<GitHubInstallation[]> {
    const result = await this.pool.query('SELECT * FROM github_installations ORDER BY created_at');
    return result.rows.map((r: Record<string, unknown>) => this.mapInstallation(r));
  }

  async listActiveInstallations(): Promise<GitHubInstallation[]> {
    const result = await this.pool.query(
      'SELECT * FROM github_installations WHERE suspended_at IS NULL ORDER BY created_at',
    );
    return result.rows.map((r: Record<string, unknown>) => this.mapInstallation(r));
  }

  async setRepos(
    installationId: number,
    repos: Omit<GitHubInstallationRepo, 'installationId' | 'isActive'>[],
  ): Promise<void> {
    const client = await this.pool.connect();
    try {
      await client.query('BEGIN');
      await client.query(
        'DELETE FROM github_installation_repos WHERE installation_id = $1',
        [installationId],
      );
      for (const repo of repos) {
        await client.query(
          `INSERT INTO github_installation_repos (installation_id, repo_id, owner, name, full_name)
           VALUES ($1, $2, $3, $4, $5)`,
          [installationId, repo.repoId, repo.owner, repo.name, repo.fullName],
        );
      }
      await client.query('COMMIT');
    } catch (err) {
      await client.query('ROLLBACK');
      throw err;
    } finally {
      client.release();
    }
  }

  async addRepos(
    installationId: number,
    repos: Omit<GitHubInstallationRepo, 'installationId' | 'isActive'>[],
  ): Promise<void> {
    for (const repo of repos) {
      await this.pool.query(
        `INSERT INTO github_installation_repos (installation_id, repo_id, owner, name, full_name)
         VALUES ($1, $2, $3, $4, $5)
         ON CONFLICT (installation_id, repo_id) DO UPDATE SET owner = $3, name = $4, full_name = $5, is_active = TRUE, updated_at = NOW()`,
        [installationId, repo.repoId, repo.owner, repo.name, repo.fullName],
      );
    }
  }

  async removeRepos(installationId: number, repoIds: number[]): Promise<void> {
    if (repoIds.length === 0) return;
    await this.pool.query(
      `DELETE FROM github_installation_repos WHERE installation_id = $1 AND repo_id = ANY($2::bigint[])`,
      [installationId, repoIds],
    );
  }

  async listRepos(installationId: number): Promise<GitHubInstallationRepo[]> {
    const result = await this.pool.query(
      'SELECT * FROM github_installation_repos WHERE installation_id = $1 ORDER BY full_name',
      [installationId],
    );
    return result.rows.map((r: Record<string, unknown>) => this.mapRepo(r));
  }

  async listAllActiveRepos(): Promise<GitHubInstallationRepo[]> {
    const result = await this.pool.query(
      `SELECT r.* FROM github_installation_repos r
       JOIN github_installations i ON r.installation_id = i.installation_id
       WHERE i.suspended_at IS NULL AND r.is_active = TRUE
       ORDER BY r.full_name`,
    );
    return result.rows.map((r: Record<string, unknown>) => this.mapRepo(r));
  }

  async suspendInstallation(installationId: number): Promise<void> {
    await this.pool.query(
      'UPDATE github_installations SET suspended_at = NOW(), updated_at = NOW() WHERE installation_id = $1',
      [installationId],
    );
  }

  async unsuspendInstallation(installationId: number): Promise<void> {
    await this.pool.query(
      'UPDATE github_installations SET suspended_at = NULL, updated_at = NOW() WHERE installation_id = $1',
      [installationId],
    );
  }

  async updateApiKeyHash(installationId: number, hash: string, prefix: string, encrypted?: string): Promise<void> {
    await this.pool.query(
      `UPDATE github_installations
       SET api_key_hash = $2, api_key_prefix = $3, api_key_encrypted = $4, updated_at = NOW()
       WHERE installation_id = $1`,
      [installationId, hash, prefix, encrypted ?? null],
    );
  }

  async findByApiKeyHash(hash: string): Promise<GitHubInstallation | null> {
    const result = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM github_installations WHERE api_key_hash = $1',
      [hash],
    );
    if (result.rows.length === 0) return null;
    return this.mapInstallation(result.rows[0]!);
  }

  private mapInstallation(row: Record<string, unknown>): GitHubInstallation {
    return {
      installationId: Number(row['installation_id']),
      accountLogin: String(row['account_login']),
      accountType: String(row['account_type']) as 'User' | 'Organization',
      appId: Number(row['app_id']),
      permissions: (row['permissions'] ?? {}) as Record<string, string>,
      suspendedAt: row['suspended_at'] !== null && row['suspended_at'] !== undefined ? String(row['suspended_at']) : null,
      apiKeyHash: row['api_key_hash'] !== null && row['api_key_hash'] !== undefined ? String(row['api_key_hash']) : null,
      apiKeyPrefix: row['api_key_prefix'] !== null && row['api_key_prefix'] !== undefined ? String(row['api_key_prefix']) : null,
      apiKeyEncrypted: row['api_key_encrypted'] !== null && row['api_key_encrypted'] !== undefined ? String(row['api_key_encrypted']) : null,
      tenantId: String(row['tenant_id'] ?? '00000000-0000-0000-0000-000000000000'),
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
    };
  }

  private mapRepo(row: Record<string, unknown>): GitHubInstallationRepo {
    return {
      installationId: Number(row['installation_id']),
      repoId: Number(row['repo_id']),
      owner: String(row['owner']),
      name: String(row['name']),
      fullName: String(row['full_name']),
      isActive: Boolean(row['is_active']),
    };
  }
}
