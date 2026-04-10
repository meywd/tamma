/** Represents a GitHub App installation stored in the database. */
export interface GitHubInstallation {
  installationId: number;
  accountLogin: string;
  accountType: 'User' | 'Organization';
  appId: number;
  permissions: Record<string, string>;
  suspendedAt: string | null;
  apiKeyHash: string | null;
  apiKeyPrefix: string | null;
  apiKeyEncrypted: string | null;
  tenantId: string;
  createdAt: string;
  updatedAt: string;
}

/** Represents a repository linked to an installation. */
export interface GitHubInstallationRepo {
  installationId: number;
  repoId: number;
  owner: string;
  name: string;
  fullName: string;
  isActive: boolean;
}

/** Input type for upsertInstallation — tenantId is optional (defaults to DEFAULT_TENANT_ID). */
export type UpsertInstallationInput = Omit<GitHubInstallation, 'createdAt' | 'updatedAt' | 'tenantId'> & {
  tenantId?: string;
};

/** Interface for installation persistence. */
export interface IGitHubInstallationStore {
  upsertInstallation(installation: UpsertInstallationInput): Promise<void>;
  removeInstallation(installationId: number): Promise<void>;
  getInstallation(installationId: number): Promise<GitHubInstallation | null>;
  listInstallations(): Promise<GitHubInstallation[]>;
  listActiveInstallations(): Promise<GitHubInstallation[]>;
  setRepos(installationId: number, repos: Omit<GitHubInstallationRepo, 'installationId' | 'isActive'>[]): Promise<void>;
  addRepos(installationId: number, repos: Omit<GitHubInstallationRepo, 'installationId' | 'isActive'>[]): Promise<void>;
  removeRepos(installationId: number, repoIds: number[]): Promise<void>;
  listRepos(installationId: number): Promise<GitHubInstallationRepo[]>;
  listAllActiveRepos(): Promise<GitHubInstallationRepo[]>;
  suspendInstallation(installationId: number): Promise<void>;
  unsuspendInstallation(installationId: number): Promise<void>;
  updateApiKeyHash(installationId: number, hash: string, prefix: string, encrypted?: string): Promise<void>;
  findByApiKeyHash(hash: string): Promise<GitHubInstallation | null>;
}

/** Default tenant ID for CLI/self-hosted mode. */
const DEFAULT_TENANT_ID = '00000000-0000-0000-0000-000000000000';

/** In-memory implementation for testing and development. */
export class InMemoryInstallationStore implements IGitHubInstallationStore {
  private installations = new Map<number, GitHubInstallation>();
  private repos = new Map<number, GitHubInstallationRepo[]>();

  async upsertInstallation(installation: UpsertInstallationInput): Promise<void> {
    const now = new Date().toISOString();
    const existing = this.installations.get(installation.installationId);
    this.installations.set(installation.installationId, {
      ...installation,
      tenantId: installation.tenantId ?? DEFAULT_TENANT_ID,
      createdAt: existing?.createdAt ?? now,
      updatedAt: now,
    });
  }

  async removeInstallation(installationId: number): Promise<void> {
    this.installations.delete(installationId);
    this.repos.delete(installationId);
  }

  async getInstallation(installationId: number): Promise<GitHubInstallation | null> {
    return this.installations.get(installationId) ?? null;
  }

  async listInstallations(): Promise<GitHubInstallation[]> {
    return [...this.installations.values()];
  }

  async listActiveInstallations(): Promise<GitHubInstallation[]> {
    return [...this.installations.values()].filter((i) => i.suspendedAt === null);
  }

  async setRepos(installationId: number, repos: Omit<GitHubInstallationRepo, 'installationId' | 'isActive'>[]): Promise<void> {
    this.repos.set(
      installationId,
      repos.map((r) => ({ ...r, installationId, isActive: true })),
    );
  }

  async addRepos(installationId: number, repos: Omit<GitHubInstallationRepo, 'installationId' | 'isActive'>[]): Promise<void> {
    const existing = this.repos.get(installationId) ?? [];
    const newRepos = repos.map((r) => ({ ...r, installationId, isActive: true }));
    this.repos.set(installationId, [...existing, ...newRepos]);
  }

  async removeRepos(installationId: number, repoIds: number[]): Promise<void> {
    const existing = this.repos.get(installationId) ?? [];
    this.repos.set(
      installationId,
      existing.filter((r) => !repoIds.includes(r.repoId)),
    );
  }

  async listRepos(installationId: number): Promise<GitHubInstallationRepo[]> {
    return this.repos.get(installationId) ?? [];
  }

  async listAllActiveRepos(): Promise<GitHubInstallationRepo[]> {
    const activeInstallations = await this.listActiveInstallations();
    const activeIds = new Set(activeInstallations.map((i) => i.installationId));
    const allRepos: GitHubInstallationRepo[] = [];
    for (const [installationId, repos] of this.repos) {
      if (activeIds.has(installationId)) {
        allRepos.push(...repos.filter((r) => r.isActive));
      }
    }
    return allRepos;
  }

  async suspendInstallation(installationId: number): Promise<void> {
    const existing = this.installations.get(installationId);
    if (existing) {
      existing.suspendedAt = new Date().toISOString();
      existing.updatedAt = new Date().toISOString();
    }
  }

  async unsuspendInstallation(installationId: number): Promise<void> {
    const existing = this.installations.get(installationId);
    if (existing) {
      existing.suspendedAt = null;
      existing.updatedAt = new Date().toISOString();
    }
  }

  async updateApiKeyHash(installationId: number, hash: string, prefix: string, encrypted?: string): Promise<void> {
    const existing = this.installations.get(installationId);
    if (existing) {
      existing.apiKeyHash = hash;
      existing.apiKeyPrefix = prefix;
      existing.apiKeyEncrypted = encrypted ?? null;
      existing.updatedAt = new Date().toISOString();
    }
  }

  async findByApiKeyHash(hash: string): Promise<GitHubInstallation | null> {
    for (const installation of this.installations.values()) {
      if (installation.apiKeyHash === hash) {
        return installation;
      }
    }
    return null;
  }
}
