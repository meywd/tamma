"use strict";
var __importDefault = (this && this.__importDefault) || function (mod) {
    return (mod && mod.__esModule) ? mod : { "default": mod };
};
Object.defineProperty(exports, "__esModule", { value: true });
const dotenv_1 = __importDefault(require("dotenv"));
const path_1 = __importDefault(require("path"));
// Load environment variables
dotenv_1.default.config();
const config = {
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
            directory: path_1.default.join(__dirname, 'database', 'migrations'),
            tableName: process.env.MIGRATION_TABLE_NAME || 'knex_migrations',
            extension: 'ts',
            stub: path_1.default.join(__dirname, 'database', 'migration.stub.ts'),
        },
        seeds: {
            directory: path_1.default.join(__dirname, 'database', 'seeds'),
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
            password: process.env.DB_PASSWORD || 'test_platform_pass',
        },
        pool: {
            min: 1,
            max: 5,
        },
        migrations: {
            directory: path_1.default.join(__dirname, 'database', 'migrations'),
            tableName: process.env.MIGRATION_TABLE_NAME || 'knex_migrations',
            extension: 'ts',
        },
        seeds: {
            directory: path_1.default.join(__dirname, 'database', 'seeds'),
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
            directory: path_1.default.join(__dirname, 'database', 'migrations'),
            tableName: process.env.MIGRATION_TABLE_NAME || 'knex_migrations',
            extension: 'ts',
        },
        seeds: {
            directory: path_1.default.join(__dirname, 'database', 'seeds'),
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
            directory: path_1.default.join(__dirname, 'database', 'migrations'),
            tableName: process.env.MIGRATION_TABLE_NAME || 'knex_migrations',
            extension: 'ts',
        },
        acquireConnectionTimeout: 60000,
        createTimeoutMillis: 30000,
        destroyTimeoutMillis: 5000,
        idleTimeoutMillis: 30000,
        reapIntervalMillis: 1000,
    },
};
exports.default = config;
//# sourceMappingURL=knexfile.js.map