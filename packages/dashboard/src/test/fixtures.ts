import type { AdminUser, ApiKeyEntry, ServiceHealth, CurrentUser } from '../services/admin/admin-api-client.js';

export const OWNER_USER: CurrentUser = {
  id: 'user-1',
  username: 'owner-user',
  githubId: 1001,
  role: 'owner',
};

export const ADMIN_USER: CurrentUser = {
  id: 'user-2',
  username: 'admin-user',
  githubId: 1002,
  role: 'admin',
};

export const MEMBER_USER: CurrentUser = {
  id: 'user-3',
  username: 'member-user',
  githubId: 1003,
  role: 'member',
};

export const ADMIN_USERS: AdminUser[] = [
  {
    id: 'user-1',
    githubId: 1001,
    githubLogin: 'owner-user',
    email: 'owner@example.com',
    role: 'owner',
    lastActiveAt: '2026-04-15T10:00:00.000Z',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-04-15T10:00:00.000Z',
  },
  {
    id: 'user-2',
    githubId: 1002,
    githubLogin: 'admin-user',
    email: 'admin@example.com',
    role: 'admin',
    lastActiveAt: '2026-04-14T08:00:00.000Z',
    createdAt: '2026-02-01T00:00:00.000Z',
    updatedAt: '2026-04-14T08:00:00.000Z',
  },
  {
    id: 'user-3',
    githubId: 1003,
    githubLogin: 'member-user',
    email: null,
    role: 'member',
    lastActiveAt: null,
    createdAt: '2026-03-01T00:00:00.000Z',
    updatedAt: '2026-03-01T00:00:00.000Z',
  },
];

export const API_KEYS: ApiKeyEntry[] = [
  {
    id: 'key-1',
    keyPrefix: 'tmk_abc1',
    label: 'CI Pipeline',
    userId: 'user-1',
    lastUsedAt: '2026-04-15T09:00:00.000Z',
    createdAt: '2026-03-01T00:00:00.000Z',
    revokedAt: null,
  },
  {
    id: 'key-2',
    keyPrefix: 'tmk_def2',
    label: 'Dev Machine',
    userId: 'user-2',
    lastUsedAt: null,
    createdAt: '2026-04-01T00:00:00.000Z',
    revokedAt: null,
  },
];

export const HEALTHY_SERVICES: ServiceHealth[] = [
  {
    name: 'Tamma API',
    status: 'healthy',
    responseTime: 12,
    checkedAt: '2026-04-15T10:00:00.000Z',
  },
  {
    name: 'PostgreSQL',
    status: 'healthy',
    responseTime: 3,
    checkedAt: '2026-04-15T10:00:00.000Z',
  },
  {
    name: 'ELSA Server',
    status: 'unhealthy',
    responseTime: null,
    checkedAt: '2026-04-15T10:00:00.000Z',
    details: 'Connection refused',
  },
  {
    name: 'OpenSearch',
    status: 'unknown',
    responseTime: null,
    checkedAt: '2026-04-15T10:00:00.000Z',
  },
  {
    name: 'RabbitMQ',
    status: 'healthy',
    responseTime: 8,
    checkedAt: '2026-04-15T10:00:00.000Z',
  },
  {
    name: 'ChromaDB',
    status: 'healthy',
    responseTime: 15,
    checkedAt: '2026-04-15T10:00:00.000Z',
  },
];
