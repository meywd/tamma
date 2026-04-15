import type { IProvidersConfig } from '@tamma/shared';

/** Auth method for user registration. */
export type AuthMethod = 'email' | 'github' | 'both';

/** Represents a user in the Tamma SaaS platform. */
export interface User {
  id: string;
  githubId: number | null;
  githubLogin: string;
  email: string | null;
  role: 'owner' | 'admin' | 'member';
  tenantId: string | null;
  settings: IProvidersConfig;
  lastActiveAt: string | null;
  createdAt: string;
  updatedAt: string;
  /** Password hash (null for GitHub-only users). */
  passwordHash: string | null;
  /** Whether the user's email has been verified. */
  emailVerified: boolean;
  /** How the user registered. */
  authMethod: AuthMethod;
  /** SHA-256 hash of email verification token. */
  emailVerificationTokenHash: string | null;
  /** When the email verification token expires. */
  emailVerificationExpiresAt: string | null;
}

/** Links a user to a GitHub App installation with a role. */
export interface UserInstallation {
  userId: string;
  installationId: number;
  role: 'owner' | 'admin' | 'member';
  createdAt: string;
}

/** Input type for upsertUser — settings, tenantId, and lastActiveAt are optional/auto-managed. */
export type UpsertUserInput = Omit<User, 'id' | 'createdAt' | 'updatedAt' | 'settings' | 'tenantId' | 'lastActiveAt' | 'passwordHash' | 'emailVerified' | 'authMethod' | 'emailVerificationTokenHash' | 'emailVerificationExpiresAt'> & {
  settings?: IProvidersConfig;
  tenantId?: string | null;
};

/** Input for creating a user with email+password registration. */
export interface CreateEmailUserInput {
  email: string;
  name: string;
  passwordHash: string;
  emailVerificationTokenHash: string;
  emailVerificationExpiresAt: string;
}

/** Options for listing users with pagination. */
export interface ListUsersOptions {
  limit: number;
  offset: number;
  role?: 'owner' | 'admin' | 'member';
}

/** Paginated result for listUsers. */
export interface ListUsersResult {
  users: User[];
  total: number;
}

/** Interface for user persistence. */
export interface IUserStore {
  upsertUser(user: UpsertUserInput): Promise<User>;
  getUser(id: string): Promise<User | null>;
  getUserByGithubId(githubId: number): Promise<User | null>;
  linkUserToInstallation(userId: string, installationId: number, role: 'owner' | 'admin' | 'member'): Promise<void>;
  getUserInstallations(userId: string): Promise<UserInstallation[]>;
  getUserSettings(userId: string): Promise<IProvidersConfig>;
  updateUserSettings(userId: string, settings: IProvidersConfig): Promise<IProvidersConfig>;

  /** List users with pagination, excluding soft-deleted. */
  listUsers(options: ListUsersOptions): Promise<ListUsersResult>;

  /** Update a user's role. */
  updateUserRole(id: string, role: 'owner' | 'admin' | 'member'): Promise<User>;

  /** Soft-delete a user by setting deleted_at. */
  deleteUser(id: string): Promise<void>;

  /** Update last_active_at timestamp. */
  updateLastActive(id: string): Promise<void>;

  /** Remove all installation links for a user (e.g. on soft delete). */
  unlinkAllInstallations(userId: string): Promise<void>;

  // --- Story 18-1: Email auth methods ---

  /** Create a user with email+password registration. */
  createEmailUser(input: CreateEmailUserInput): Promise<User>;

  /** Get a user by email (case-insensitive). */
  getUserByEmail(email: string): Promise<User | null>;

  /** Set email verified to true and clear verification token. */
  setEmailVerified(userId: string): Promise<void>;

  /** Update the email verification token for a user. */
  updateVerificationToken(userId: string, tokenHash: string, expiresAt: string): Promise<void>;

  /** Update the user's password hash. */
  updatePasswordHash(userId: string, passwordHash: string): Promise<void>;

  /** Update the user's active tenant. */
  updateActiveTenant(userId: string, tenantId: string | null): Promise<void>;

  /** Update the user's auth method (e.g., when linking GitHub to email account). */
  updateAuthMethod(userId: string, authMethod: AuthMethod): Promise<void>;

  /** Set the user's GitHub ID and login (for account linking). */
  setGithubId(userId: string, githubId: number, githubLogin: string): Promise<void>;
}

/** Default empty provider settings. */
const DEFAULT_SETTINGS: IProvidersConfig = { providers: {} };

/** In-memory implementation for testing and development. */
export class InMemoryUserStore implements IUserStore {
  private users = new Map<string, User & { deletedAt?: string }>();
  private userInstallations = new Map<string, UserInstallation[]>();
  private nextId = 1;

  async upsertUser(user: UpsertUserInput): Promise<User> {
    // Check if user with this GitHub ID already exists
    if (user.githubId !== null) {
      for (const existing of this.users.values()) {
        if (existing.githubId === user.githubId) {
          existing.githubLogin = user.githubLogin;
          if (user.email !== null) {
            existing.email = user.email;
          }
          existing.updatedAt = new Date().toISOString();
          return existing;
        }
      }
    }

    const now = new Date().toISOString();
    const id = String(this.nextId++);
    const newUser: User & { deletedAt?: string } = {
      ...user,
      tenantId: user.tenantId ?? null,
      settings: user.settings ?? structuredClone(DEFAULT_SETTINGS),
      lastActiveAt: null,
      passwordHash: null,
      emailVerified: user.githubId !== null, // GitHub users are pre-verified
      authMethod: user.githubId !== null ? 'github' : 'email',
      emailVerificationTokenHash: null,
      emailVerificationExpiresAt: null,
      id,
      createdAt: now,
      updatedAt: now,
    };
    this.users.set(id, newUser);
    return newUser;
  }

  async getUser(id: string): Promise<User | null> {
    return this.users.get(id) ?? null;
  }

  async getUserByGithubId(githubId: number): Promise<User | null> {
    for (const user of this.users.values()) {
      if (user.githubId === githubId) return user;
    }
    return null;
  }

  async linkUserToInstallation(userId: string, installationId: number, role: 'owner' | 'admin' | 'member'): Promise<void> {
    const existing = this.userInstallations.get(userId) ?? [];
    const alreadyLinked = existing.find((ui) => ui.installationId === installationId);
    if (alreadyLinked) {
      alreadyLinked.role = role;
      return;
    }
    existing.push({
      userId,
      installationId,
      role,
      createdAt: new Date().toISOString(),
    });
    this.userInstallations.set(userId, existing);
  }

  async getUserInstallations(userId: string): Promise<UserInstallation[]> {
    return this.userInstallations.get(userId) ?? [];
  }

  async getUserSettings(userId: string): Promise<IProvidersConfig> {
    const user = this.users.get(userId);
    if (!user) return structuredClone(DEFAULT_SETTINGS);
    return structuredClone(user.settings);
  }

  async updateUserSettings(userId: string, settings: IProvidersConfig): Promise<IProvidersConfig> {
    const user = this.users.get(userId);
    if (!user) {
      throw new Error(`User not found: ${userId}`);
    }
    user.settings = structuredClone(settings);
    user.updatedAt = new Date().toISOString();
    return structuredClone(user.settings);
  }

  async listUsers(options: ListUsersOptions): Promise<ListUsersResult> {
    const allUsers = [...this.users.values()].filter((u) => !u.deletedAt);
    const filtered = options.role
      ? allUsers.filter((u) => u.role === options.role)
      : allUsers;
    const total = filtered.length;
    const users = filtered.slice(options.offset, options.offset + options.limit);
    return { users, total };
  }

  async updateUserRole(id: string, role: 'owner' | 'admin' | 'member'): Promise<User> {
    const user = this.users.get(id);
    if (!user || user.deletedAt) {
      throw new Error(`User not found: ${id}`);
    }
    user.role = role;
    user.updatedAt = new Date().toISOString();
    return user;
  }

  async deleteUser(id: string): Promise<void> {
    const user = this.users.get(id);
    if (!user) {
      throw new Error(`User not found: ${id}`);
    }
    user.deletedAt = new Date().toISOString();
    user.updatedAt = new Date().toISOString();
  }

  async updateLastActive(id: string): Promise<void> {
    const user = this.users.get(id);
    if (!user || user.deletedAt) return;
    user.lastActiveAt = new Date().toISOString();
  }

  async unlinkAllInstallations(userId: string): Promise<void> {
    this.userInstallations.delete(userId);
  }

  // --- Story 18-1: Email auth methods ---

  async createEmailUser(input: CreateEmailUserInput): Promise<User> {
    // Check email uniqueness (case-insensitive)
    const normalizedEmail = input.email.toLowerCase().trim();
    for (const existing of this.users.values()) {
      if (existing.email?.toLowerCase() === normalizedEmail && !existing.deletedAt) {
        throw new Error('Email already exists');
      }
    }

    const now = new Date().toISOString();
    const id = String(this.nextId++);
    const newUser: User & { deletedAt?: string } = {
      id,
      githubId: null,
      githubLogin: '',
      email: normalizedEmail,
      role: 'member',
      tenantId: null,
      settings: structuredClone(DEFAULT_SETTINGS),
      lastActiveAt: null,
      passwordHash: input.passwordHash,
      emailVerified: false,
      authMethod: 'email',
      emailVerificationTokenHash: input.emailVerificationTokenHash,
      emailVerificationExpiresAt: input.emailVerificationExpiresAt,
      createdAt: now,
      updatedAt: now,
    };
    this.users.set(id, newUser);
    return newUser;
  }

  async getUserByEmail(email: string): Promise<User | null> {
    const normalized = email.toLowerCase().trim();
    for (const user of this.users.values()) {
      if (user.email?.toLowerCase() === normalized && !user.deletedAt) {
        return user;
      }
    }
    return null;
  }

  async setEmailVerified(userId: string): Promise<void> {
    const user = this.users.get(userId);
    if (!user) throw new Error(`User not found: ${userId}`);
    user.emailVerified = true;
    user.emailVerificationTokenHash = null;
    user.emailVerificationExpiresAt = null;
    user.updatedAt = new Date().toISOString();
  }

  async updateVerificationToken(userId: string, tokenHash: string, expiresAt: string): Promise<void> {
    const user = this.users.get(userId);
    if (!user) throw new Error(`User not found: ${userId}`);
    user.emailVerificationTokenHash = tokenHash;
    user.emailVerificationExpiresAt = expiresAt;
    user.updatedAt = new Date().toISOString();
  }

  async updatePasswordHash(userId: string, passwordHash: string): Promise<void> {
    const user = this.users.get(userId);
    if (!user) throw new Error(`User not found: ${userId}`);
    user.passwordHash = passwordHash;
    user.updatedAt = new Date().toISOString();
  }

  async updateActiveTenant(userId: string, tenantId: string | null): Promise<void> {
    const user = this.users.get(userId);
    if (!user) throw new Error(`User not found: ${userId}`);
    user.tenantId = tenantId;
    user.updatedAt = new Date().toISOString();
  }

  async updateAuthMethod(userId: string, authMethod: AuthMethod): Promise<void> {
    const user = this.users.get(userId);
    if (!user) throw new Error(`User not found: ${userId}`);
    user.authMethod = authMethod;
    user.updatedAt = new Date().toISOString();
  }

  async setGithubId(userId: string, githubId: number, githubLogin: string): Promise<void> {
    const user = this.users.get(userId);
    if (!user) throw new Error(`User not found: ${userId}`);
    user.githubId = githubId;
    user.githubLogin = githubLogin;
    user.updatedAt = new Date().toISOString();
  }
}
