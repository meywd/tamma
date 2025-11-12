import type { Knex } from 'knex';
import dotenv from 'dotenv';
import path from 'path';

// Load environment variables
dotenv.config();

const config: { [key: string]: Knex.Config } = {
  development: {
    client: 'postgresql',
    connection: {
      host: process.env.DB_HOST || 'localhost',
      port: parseInt(process.env.DB_PORT || '5432'),
      database: process.env.DB_NAME || 'test_platform_dev',
      user: process.env.DB_USER || 'test_platform_user',
      password: process.env.DB_PASSWORD || 'test_platform_pass',
    },
    pool: {
      min: parseInt(process.env.DB_POOL_MIN || '2'),
      max: parseInt(process.env.DB_POOL_MAX || '10'),
    },
    migrations: {
      directory: path.join(__dirname, 'database', 'migrations'),
      tableName: process.env.MIGRATION_TABLE_NAME || 'knex_migrations',
      extension: 'ts',
      stub: path.join(__dirname, 'database', 'migration.stub.ts'),
    },
    seeds: {
      directory: path.join(__dirname, 'database', 'seeds'),
      extension: 'ts',
    },
  },

  test: {
    client: 'postgresql',
    connection: {
      host: process.env.DB_HOST || 'localhost',
      port: parseInt(process.env.DB_PORT || '5432'),
      database: process.env.DB_NAME || 'test_platform_test',
      user: process.env.DB_USER || 'test_platform_user',
      password: process.env.DB_PASSWORD, // Explicitly use process.env.DB_PASSWORD
    },
    pool: {
      min: 1,
      max: 5,
    },
    migrations: {
      directory: path.join(__dirname, 'database', 'migrations'),
      tableName: process.env.MIGRATION_TABLE_NAME || 'knex_migrations',
      extension: 'ts',
    },
    seeds: {
      directory: path.join(__dirname, 'database', 'seeds'),
      extension: 'ts',
    },
  },

  staging: {
    client: 'postgresql',
    connection: {
      host: process.env.DB_HOST,
      port: parseInt(process.env.DB_PORT || '5432'),
      database: process.env.DB_NAME,
      user: process.env.DB_USER,
      password: process.env.DB_PASSWORD,
      ssl: process.env.DB_SSL === 'true' ? { rejectUnauthorized: false } : undefined,
    },
    pool: {
      min: parseInt(process.env.DB_POOL_MIN || '2'),
      max: parseInt(process.env.DB_POOL_MAX || '20'),
    },
    migrations: {
      directory: path.join(__dirname, 'database', 'migrations'),
      tableName: process.env.MIGRATION_TABLE_NAME || 'knex_migrations',
      extension: 'ts',
    },
    seeds: {
      directory: path.join(__dirname, 'database', 'seeds'),
      extension: 'ts',
    },
    acquireConnectionTimeout: 60000,
  },

  production: {
    client: 'postgresql',
    connection: {
      host: process.env.DB_HOST,
      port: parseInt(process.env.DB_PORT || '5432'),
      database: process.env.DB_NAME,
      user: process.env.DB_USER,
      password: process.env.DB_PASSWORD,
      ssl: process.env.DB_SSL === 'true' ? { rejectUnauthorized: false } : undefined,
    },
    pool: {
      min: parseInt(process.env.DB_POOL_MIN || '5'),
      max: parseInt(process.env.DB_POOL_MAX || '30'),
    },
    migrations: {
      directory: path.join(__dirname, 'database', 'migrations'),
      tableName: process.env.MIGRATION_TABLE_NAME || 'knex_migrations',
      extension: 'ts',
    },
    acquireConnectionTimeout: 60000,
  },
};

export default config;