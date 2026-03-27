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

/** Interface for user persistence. */
export interface IUserStore {
  upsertUser(user: UpsertUserInput): Promise<User>;
  getUser(id: string): Promise<User | null>;
  getUserByGithubId(githubId: number): Promise<User | null>;
  linkUserToInstallation(userId: string, installationId: number, role: 'owner' | 'admin' | 'member'): Promise<void>;
  getUserInstallations(userId: string): Promise<UserInstallation[]>;
  getUserSettings(userId: string): Promise<IProvidersConfig>;
  updateUserSettings(userId: string, settings: IProvidersConfig): Promise<IProvidersConfig>;
}

/** Default empty provider settings. */
const DEFAULT_SETTINGS: IProvidersConfig = { providers: {} };

/** In-memory implementation for testing and development. */
export class InMemoryUserStore implements IUserStore {
  private users = new Map<string, User>();
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
}
