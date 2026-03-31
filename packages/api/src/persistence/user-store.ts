import type { IProvidersConfig } from '@tamma/shared';

/** Represents a user in the Tamma SaaS platform. */
export interface User {
  id: string;
  githubId: number;
  githubLogin: string;
  email: string | null;
  role: 'owner' | 'admin' | 'member';
  settings: IProvidersConfig;
  createdAt: string;
  updatedAt: string;
}

/** Links a user to a GitHub App installation with a role. */
export interface UserInstallation {
  userId: string;
  installationId: number;
  role: 'owner' | 'admin' | 'member';
  createdAt: string;
}

/** Input type for upsertUser — settings is optional (defaults to empty). */
export type UpsertUserInput = Omit<User, 'id' | 'createdAt' | 'updatedAt' | 'settings'> & { settings?: IProvidersConfig };

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
}

/** Default empty provider settings. */
const DEFAULT_SETTINGS: IProvidersConfig = { providers: {} };

/** In-memory implementation for testing and development. */
export class InMemoryUserStore implements IUserStore {
  private users = new Map<string, User & { deletedAt?: string; lastActiveAt?: string }>();
  private userInstallations = new Map<string, UserInstallation[]>();
  private nextId = 1;

  async upsertUser(user: UpsertUserInput): Promise<User> {
    // Check if user with this GitHub ID already exists
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

    const now = new Date().toISOString();
    const id = String(this.nextId++);
    const newUser: User = {
      ...user,
      settings: user.settings ?? structuredClone(DEFAULT_SETTINGS),
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
}
