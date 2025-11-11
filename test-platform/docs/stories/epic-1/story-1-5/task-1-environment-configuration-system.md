# Implementation Plan: Task 1 - Environment Configuration System

**Story**: 1.5 Configuration Management & Environment Setup  
**Task**: 1 - Environment Configuration System  
**Acceptance Criteria**: #1, #3 - Environment-based configuration (development, staging, production); Configuration validation on startup

## Overview

Implement a robust configuration management system that supports multiple environments, validation, and runtime configuration updates with TypeScript type safety.

## Implementation Steps

### Subtask 1.1: Create Configuration Schema with Environment-Specific Settings

**Objective**: Define comprehensive TypeScript interfaces for all configuration sections

**File**: `packages/shared/src/config/types.ts`

```typescript
// Database Configuration
export interface DatabaseConfig {
  host: string;
  port: number;
  database: string;
  username: string;
  password: string;
  ssl: boolean;
  pool: {
    min: number;
    max: number;
    idleTimeoutMillis: number;
  };
}

// Redis Configuration
export interface RedisConfig {
  host: string;
  port: number;
  password?: string;
  db: number;
  keyPrefix: string;
  tls: boolean;
}

// JWT Configuration
export interface JWTConfig {
  accessTokenSecret: string;
  refreshTokenSecret: string;
  accessTokenExpiry: string;
  refreshTokenExpiry: string;
  issuer: string;
  audience: string;
}

// API Configuration
export interface APIConfig {
  port: number;
  host: string;
  cors: {
    origin: string | string[];
    credentials: boolean;
  };
  rateLimit: {
    windowMs: number;
    max: number;
  };
  compression: boolean;
  trustProxy: boolean;
}

// Logging Configuration
export interface LoggingConfig {
  level: string;
  format: string;
  pretty: boolean;
  file: {
    enabled: boolean;
    path: string;
    maxSize: string;
    maxFiles: number;
  };
  structured: boolean;
}

// Metrics Configuration
export interface MetricsConfig {
  enabled: boolean;
  port: number;
  path: string;
  collectDefaultMetrics: boolean;
  collectInterval: number;
}

// Email Configuration
export interface EmailConfig {
  provider: 'smtp' | 'sendgrid' | 'ses' | 'mailgun';
  smtp?: {
    host: string;
    port: number;
    secure: boolean;
    auth: {
      user: string;
      pass: string;
    };
  };
  sendgrid?: {
    apiKey: string;
    from: string;
  };
  ses?: {
    region: string;
    accessKeyId: string;
    secretAccessKey: string;
  };
  mailgun?: {
    domain: string;
    apiKey: string;
    from: string;
  };
}

// Storage Configuration
export interface StorageConfig {
  provider: 'local' | 's3' | 'gcs' | 'azure';
  local?: {
    path: string;
  };
  s3?: {
    region: string;
    bucket: string;
    accessKeyId: string;
    secretAccessKey: string;
  };
  gcs?: {
    bucket: string;
    keyFilename: string;
  };
  azure?: {
    account: string;
    container: string;
    sasToken: string;
  };
}

// Webhook Configuration
export interface WebhookConfig {
  secret: string;
  timeout: number;
  retryAttempts: number;
  retryDelay: number;
  batchSize: number;
}

// Main Application Configuration
export interface AppConfig {
  env: string;
  nodeEnv: string;
  version: string;
  database: DatabaseConfig;
  redis: RedisConfig;
  jwt: JWTConfig;
  api: APIConfig;
  logging: LoggingConfig;
  metrics: MetricsConfig;
  email: EmailConfig;
  storage: StorageConfig;
  webhook: WebhookConfig;
}

// Environment-specific configuration interfaces
export interface DevelopmentConfig extends Partial<AppConfig> {
  env: 'development';
  nodeEnv: 'development';
}

export interface TestConfig extends Partial<AppConfig> {
  env: 'test';
  nodeEnv: 'test';
}

export interface StagingConfig extends Partial<AppConfig> {
  env: 'staging';
  nodeEnv: 'production';
}

export interface ProductionConfig extends Partial<AppConfig> {
  env: 'production';
  nodeEnv: 'production';
}

export type EnvironmentConfig = DevelopmentConfig | TestConfig | StagingConfig | ProductionConfig;
```

### Subtask 1.2: Implement Environment Variable Loading and Validation

**Objective**: Create ConfigManager class with hierarchical configuration loading

**File**: `packages/shared/src/config/manager.ts`

```typescript
import { config } from 'dotenv';
import { readFileSync } from 'fs';
import { resolve } from 'path';
import Joi from 'joi';
import type { AppConfig, EnvironmentConfig } from './types';
import { configSchema } from './validation';

export class ConfigManager {
  private static instance: ConfigManager;
  private config: AppConfig;
  private env: string;

  private constructor() {
    this.loadConfiguration();
  }

  public static getInstance(): ConfigManager {
    if (!ConfigManager.instance) {
      ConfigManager.instance = new ConfigManager();
    }
    return ConfigManager.instance;
  }

  private loadConfiguration(): void {
    // Load environment variables in order of precedence
    config({ path: '.env.local' });
    config({ path: '.env' });

    // Determine environment
    this.env = process.env.NODE_ENV || process.env.ENV || 'development';

    // Load base configuration
    const baseConfig = this.loadConfigFile('default');

    // Load environment-specific configuration
    const envConfig = this.loadConfigFile(this.env);

    // Merge configurations
    const mergedConfig = this.mergeConfigs(baseConfig, envConfig);

    // Override with environment variables
    const envVarConfig = this.loadFromEnvironmentVariables();
    const finalConfig = this.mergeConfigs(mergedConfig, envVarConfig);

    // Validate configuration
    const { error, value } = configSchema.validate(finalConfig, {
      allowUnknown: false,
      stripUnknown: true,
    });

    if (error) {
      throw new Error(`Configuration validation failed: ${error.message}`);
    }

    this.config = value;
  }

  private loadConfigFile(env: string): Partial<AppConfig> {
    try {
      const configPath = resolve(__dirname, `${env}.ts`);
      const configModule = require(configPath);
      return configModule.default || configModule;
    } catch (error) {
      if (env === 'default') {
        throw new Error(`Default configuration file not found: ${error.message}`);
      }
      // Environment-specific config is optional
      return {};
    }
  }

  private mergeConfigs(base: Partial<AppConfig>, override: Partial<AppConfig>): Partial<AppConfig> {
    return this.deepMerge(base, override);
  }

  private deepMerge(target: any, source: any): any {
    const result = { ...target };

    for (const key in source) {
      if (source[key] && typeof source[key] === 'object' && !Array.isArray(source[key])) {
        result[key] = this.deepMerge(result[key] || {}, source[key]);
      } else {
        result[key] = source[key];
      }
    }

    return result;
  }

  private loadFromEnvironmentVariables(): Partial<AppConfig> {
    const envConfig: any = {
      env: this.env,
      nodeEnv: process.env.NODE_ENV || 'development',
      version: process.env.npm_package_version || '1.0.0',
    };

    // Database configuration from environment variables
    if (process.env.DATABASE_URL) {
      const dbUrl = new URL(process.env.DATABASE_URL);
      envConfig.database = {
        host: dbUrl.hostname,
        port: parseInt(dbUrl.port) || 5432,
        database: dbUrl.pathname.slice(1),
        username: dbUrl.username,
        password: dbUrl.password,
        ssl: process.env.DATABASE_SSL === 'true',
        pool: {
          min: parseInt(process.env.DATABASE_POOL_MIN || '2'),
          max: parseInt(process.env.DATABASE_POOL_MAX || '10'),
          idleTimeoutMillis: parseInt(process.env.DATABASE_POOL_IDLE_TIMEOUT || '30000'),
        },
      };
    } else {
      envConfig.database = {
        host: process.env.DATABASE_HOST || 'localhost',
        port: parseInt(process.env.DATABASE_PORT || '5432'),
        database: process.env.DATABASE_NAME || 'tamma',
        username: process.env.DATABASE_USERNAME || 'postgres',
        password: process.env.DATABASE_PASSWORD || 'password',
        ssl: process.env.DATABASE_SSL === 'true',
        pool: {
          min: parseInt(process.env.DATABASE_POOL_MIN || '2'),
          max: parseInt(process.env.DATABASE_POOL_MAX || '10'),
          idleTimeoutMillis: parseInt(process.env.DATABASE_POOL_IDLE_TIMEOUT || '30000'),
        },
      };
    }

    // Redis configuration from environment variables
    if (process.env.REDIS_URL) {
      const redisUrl = new URL(process.env.REDIS_URL);
      envConfig.redis = {
        host: redisUrl.hostname,
        port: parseInt(redisUrl.port) || 6379,
        password: redisUrl.password || undefined,
        db: parseInt(process.env.REDIS_DB || '0'),
        keyPrefix: process.env.REDIS_KEY_PREFIX || 'tamma:',
        tls: process.env.REDIS_TLS === 'true',
      };
    } else {
      envConfig.redis = {
        host: process.env.REDIS_HOST || 'localhost',
        port: parseInt(process.env.REDIS_PORT || '6379'),
        password: process.env.REDIS_PASSWORD,
        db: parseInt(process.env.REDIS_DB || '0'),
        keyPrefix: process.env.REDIS_KEY_PREFIX || 'tamma:',
        tls: process.env.REDIS_TLS === 'true',
      };
    }

    // JWT configuration from environment variables
    envConfig.jwt = {
      accessTokenSecret:
        process.env.JWT_ACCESS_SECRET || 'default-access-secret-change-in-production',
      refreshTokenSecret:
        process.env.JWT_REFRESH_SECRET || 'default-refresh-secret-change-in-production',
      accessTokenExpiry: process.env.JWT_ACCESS_EXPIRY || '1h',
      refreshTokenExpiry: process.env.JWT_REFRESH_EXPIRY || '30d',
      issuer: process.env.JWT_ISSUER || 'testplatform.com',
      audience: process.env.JWT_AUDIENCE || 'testplatform-api',
    };

    // API configuration from environment variables
    envConfig.api = {
      port: parseInt(process.env.API_PORT || '3000'),
      host: process.env.API_HOST || '0.0.0.0',
      cors: {
        origin: process.env.API_CORS_ORIGIN ? process.env.API_CORS_ORIGIN.split(',') : '*',
        credentials: process.env.API_CORS_CREDENTIALS !== 'false',
      },
      rateLimit: {
        windowMs: parseInt(process.env.API_RATE_LIMIT_WINDOW || '900000'),
        max: parseInt(process.env.API_RATE_LIMIT_MAX || '100'),
      },
      compression: process.env.API_COMPRESSION !== 'false',
      trustProxy: process.env.API_TRUST_PROXY === 'true',
    };

    // Logging configuration from environment variables
    envConfig.logging = {
      level: process.env.LOG_LEVEL || 'info',
      format: process.env.LOG_FORMAT || 'json',
      pretty: process.env.LOG_PRETTY === 'true',
      file: {
        enabled: process.env.LOG_FILE_ENABLED === 'true',
        path: process.env.LOG_FILE_PATH || './logs/app.log',
        maxSize: process.env.LOG_FILE_MAX_SIZE || '10m',
        maxFiles: parseInt(process.env.LOG_FILE_MAX_FILES || '5'),
      },
      structured: process.env.LOG_STRUCTURED !== 'false',
    };

    // Metrics configuration from environment variables
    envConfig.metrics = {
      enabled: process.env.METRICS_ENABLED !== 'false',
      port: parseInt(process.env.METRICS_PORT || '9090'),
      path: process.env.METRICS_PATH || '/metrics',
      collectDefaultMetrics: process.env.METRICS_COLLECT_DEFAULT !== 'false',
      collectInterval: parseInt(process.env.METRICS_COLLECT_INTERVAL || '10000'),
    };

    // Webhook configuration from environment variables
    envConfig.webhook = {
      secret: process.env.WEBHOOK_SECRET || 'default-webhook-secret-change-in-production',
      timeout: parseInt(process.env.WEBHOOK_TIMEOUT || '30000'),
      retryAttempts: parseInt(process.env.WEBHOOK_RETRY_ATTEMPTS || '3'),
      retryDelay: parseInt(process.env.WEBHOOK_RETRY_DELAY || '1000'),
      batchSize: parseInt(process.env.WEBHOOK_BATCH_SIZE || '100'),
    };

    return envConfig;
  }

  public getConfig(): AppConfig {
    return this.config;
  }

  public getEnv(): string {
    return this.env;
  }

  public isDevelopment(): boolean {
    return this.env === 'development';
  }

  public isTest(): boolean {
    return this.env === 'test';
  }

  public isStaging(): boolean {
    return this.env === 'staging';
  }

  public isProduction(): boolean {
    return this.env === 'production';
  }

  public reload(): void {
    this.loadConfiguration();
  }
}

// Export singleton instance
export const config = ConfigManager.getInstance();
```

### Subtask 1.3: Add Configuration Validation on Application Startup

**Objective**: Create comprehensive validation schemas with Joi

**File**: `packages/shared/src/config/validation.ts`

```typescript
import Joi from 'joi';
import type { AppConfig } from './types';

export const configSchema = Joi.object<AppConfig>({
  env: Joi.string().valid('development', 'test', 'staging', 'production').required(),
  nodeEnv: Joi.string().valid('development', 'production').required(),
  version: Joi.string().required(),

  database: Joi.object({
    host: Joi.string().required(),
    port: Joi.number().port().required(),
    database: Joi.string().required(),
    username: Joi.string().required(),
    password: Joi.string().required(),
    ssl: Joi.boolean().default(false),
    pool: Joi.object({
      min: Joi.number().min(0).default(2),
      max: Joi.number().min(1).default(10),
      idleTimeoutMillis: Joi.number().min(0).default(30000),
    }).required(),
  }).required(),

  redis: Joi.object({
    host: Joi.string().required(),
    port: Joi.number().port().required(),
    password: Joi.string().optional(),
    db: Joi.number().min(0).max(15).default(0),
    keyPrefix: Joi.string().default('tamma:'),
    tls: Joi.boolean().default(false),
  }).required(),

  jwt: Joi.object({
    accessTokenSecret: Joi.string().min(32).required(),
    refreshTokenSecret: Joi.string().min(32).required(),
    accessTokenExpiry: Joi.string().default('1h'),
    refreshTokenExpiry: Joi.string().default('30d'),
    issuer: Joi.string().required(),
    audience: Joi.string().required(),
  }).required(),

  api: Joi.object({
    port: Joi.number().port().default(3000),
    host: Joi.string().default('0.0.0.0'),
    cors: Joi.object({
      origin: Joi.alternatives().try(Joi.string(), Joi.array().items(Joi.string())).default('*'),
      credentials: Joi.boolean().default(true),
    }).required(),
    rateLimit: Joi.object({
      windowMs: Joi.number().min(1000).default(900000),
      max: Joi.number().min(1).default(100),
    }).required(),
    compression: Joi.boolean().default(true),
    trustProxy: Joi.boolean().default(false),
  }).required(),

  logging: Joi.object({
    level: Joi.string().valid('trace', 'debug', 'info', 'warn', 'error', 'fatal').default('info'),
    format: Joi.string().valid('json', 'pretty').default('json'),
    pretty: Joi.boolean().default(false),
    file: Joi.object({
      enabled: Joi.boolean().default(false),
      path: Joi.string().default('./logs/app.log'),
      maxSize: Joi.string().default('10m'),
      maxFiles: Joi.number().min(1).default(5),
    }).required(),
    structured: Joi.boolean().default(true),
  }).required(),

  metrics: Joi.object({
    enabled: Joi.boolean().default(true),
    port: Joi.number().port().default(9090),
    path: Joi.string().default('/metrics'),
    collectDefaultMetrics: Joi.boolean().default(true),
    collectInterval: Joi.number().min(1000).default(10000),
  }).required(),

  email: Joi.object({
    provider: Joi.string().valid('smtp', 'sendgrid', 'ses', 'mailgun').required(),
    smtp: Joi.when('provider', {
      is: 'smtp',
      then: Joi.object({
        host: Joi.string().required(),
        port: Joi.number().port().required(),
        secure: Joi.boolean().default(true),
        auth: Joi.object({
          user: Joi.string().required(),
          pass: Joi.string().required(),
        }).required(),
      }).required(),
      otherwise: Joi.optional(),
    }),
    sendgrid: Joi.when('provider', {
      is: 'sendgrid',
      then: Joi.object({
        apiKey: Joi.string().required(),
        from: Joi.string().email().required(),
      }).required(),
      otherwise: Joi.optional(),
    }),
    ses: Joi.when('provider', {
      is: 'ses',
      then: Joi.object({
        region: Joi.string().required(),
        accessKeyId: Joi.string().required(),
        secretAccessKey: Joi.string().required(),
      }).required(),
      otherwise: Joi.optional(),
    }),
    mailgun: Joi.when('provider', {
      is: 'mailgun',
      then: Joi.object({
        domain: Joi.string().required(),
        apiKey: Joi.string().required(),
        from: Joi.string().email().required(),
      }).required(),
      otherwise: Joi.optional(),
    }),
  }).required(),

  storage: Joi.object({
    provider: Joi.string().valid('local', 's3', 'gcs', 'azure').required(),
    local: Joi.when('provider', {
      is: 'local',
      then: Joi.object({
        path: Joi.string().required(),
      }).required(),
      otherwise: Joi.optional(),
    }),
    s3: Joi.when('provider', {
      is: 's3',
      then: Joi.object({
        region: Joi.string().required(),
        bucket: Joi.string().required(),
        accessKeyId: Joi.string().required(),
        secretAccessKey: Joi.string().required(),
      }).required(),
      otherwise: Joi.optional(),
    }),
    gcs: Joi.when('provider', {
      is: 'gcs',
      then: Joi.object({
        bucket: Joi.string().required(),
        keyFilename: Joi.string().required(),
      }).required(),
      otherwise: Joi.optional(),
    }),
    azure: Joi.when('provider', {
      is: 'azure',
      then: Joi.object({
        account: Joi.string().required(),
        container: Joi.string().required(),
        sasToken: Joi.string().required(),
      }).required(),
      otherwise: Joi.optional(),
    }),
  }).required(),

  webhook: Joi.object({
    secret: Joi.string().min(16).required(),
    timeout: Joi.number().min(1000).default(30000),
    retryAttempts: Joi.number().min(0).default(3),
    retryDelay: Joi.number().min(100).default(1000),
    batchSize: Joi.number().min(1).default(100),
  }).required(),
}).required();

// Production-specific validation
export const productionValidationSchema = configSchema.keys({
  jwt: Joi.object({
    accessTokenSecret: Joi.string()
      .min(32)
      .disallow('default-access-secret-change-in-production')
      .required(),
    refreshTokenSecret: Joi.string()
      .min(32)
      .disallow('default-refresh-secret-change-in-production')
      .required(),
  }).required(),
  webhook: Joi.object({
    secret: Joi.string().min(16).disallow('default-webhook-secret-change-in-production').required(),
  }).required(),
});

// Validation function
export function validateConfig(
  config: AppConfig,
  env: string
): { valid: boolean; errors: string[] } {
  const schema = env === 'production' ? productionValidationSchema : configSchema;
  const { error, value } = schema.validate(config, {
    allowUnknown: false,
    stripUnknown: true,
  });

  if (error) {
    return {
      valid: false,
      errors: error.details.map((detail) => detail.message),
    };
  }

  return {
    valid: true,
    errors: [],
  };
}
```

### Subtask 1.4: Create Configuration Documentation with Examples

**Objective**: Comprehensive documentation for all configuration options

**File**: `docs/configuration.md`

````markdown
# Configuration Management

## Overview

The Test Platform uses a hierarchical configuration system that supports multiple environments with comprehensive validation and type safety.

## Configuration Hierarchy

Configuration is loaded in the following order (later items override earlier ones):

1. Default values (`config/default.ts`)
2. Environment-specific files (`config/{env}.ts`)
3. Environment variables
4. Runtime overrides

## Environment Types

### Development

- Environment: `development`
- Node Environment: `development`
- Use case: Local development with hot reloading
- Default settings: Debug logging, relaxed security, local services

### Test

- Environment: `test`
- Node Environment: `test`
- Use case: Automated testing with isolated services
- Default settings: Minimal logging, in-memory services, fast execution

### Staging

- Environment: `staging`
- Node Environment: `production`
- Use case: Pre-production testing with production-like settings
- Default settings: Production logging, staging services, full security

### Production

- Environment: `production`
- Node Environment: `production`
- Use case: Live production deployment
- Default settings: Optimized logging, production services, maximum security

## Configuration Sections

### Database Configuration

```typescript
database: {
  host: string; // Database host
  port: number; // Database port (5432)
  database: string; // Database name
  username: string; // Database username
  password: string; // Database password
  ssl: boolean; // Enable SSL (false)
  pool: {
    min: number; // Minimum connections (2)
    max: number; // Maximum connections (10)
    idleTimeoutMillis: number; // Idle timeout (30000)
  }
}
```
````

**Environment Variables:**

- `DATABASE_URL` - Full database URL (overrides individual settings)
- `DATABASE_HOST` - Database host (localhost)
- `DATABASE_PORT` - Database port (5432)
- `DATABASE_NAME` - Database name (tamma)
- `DATABASE_USERNAME` - Database username (postgres)
- `DATABASE_PASSWORD` - Database password
- `DATABASE_SSL` - Enable SSL (false)
- `DATABASE_POOL_MIN` - Minimum pool connections (2)
- `DATABASE_POOL_MAX` - Maximum pool connections (10)
- `DATABASE_POOL_IDLE_TIMEOUT` - Idle timeout in ms (30000)

### Redis Configuration

```typescript
redis: {
  host: string;           // Redis host
  port: number;           // Redis port (6379)
  password?: string;      // Redis password
  db: number;            // Redis database (0)
  keyPrefix: string;      // Key prefix (tamma:)
  tls: boolean;          // Enable TLS (false)
}
```

**Environment Variables:**

- `REDIS_URL` - Full Redis URL (overrides individual settings)
- `REDIS_HOST` - Redis host (localhost)
- `REDIS_PORT` - Redis port (6379)
- `REDIS_PASSWORD` - Redis password
- `REDIS_DB` - Redis database (0)
- `REDIS_KEY_PREFIX` - Key prefix (tamma:)
- `REDIS_TLS` - Enable TLS (false)

### JWT Configuration

```typescript
jwt: {
  accessTokenSecret: string; // Access token secret (min 32 chars)
  refreshTokenSecret: string; // Refresh token secret (min 32 chars)
  accessTokenExpiry: string; // Access token expiry (1h)
  refreshTokenExpiry: string; // Refresh token expiry (30d)
  issuer: string; // Token issuer
  audience: string; // Token audience
}
```

**Environment Variables:**

- `JWT_ACCESS_SECRET` - Access token secret
- `JWT_REFRESH_SECRET` - Refresh token secret
- `JWT_ACCESS_EXPIRY` - Access token expiry (1h)
- `JWT_REFRESH_EXPIRY` - Refresh token expiry (30d)
- `JWT_ISSUER` - Token issuer (testplatform.com)
- `JWT_AUDIENCE` - Token audience (testplatform-api)

### API Configuration

```typescript
api: {
  port: number;           // API port (3000)
  host: string;           // API host (0.0.0.0)
  cors: {
    origin: string | string[]; // Allowed origins (*)
    credentials: boolean;     // Allow credentials (true)
  };
  rateLimit: {
    windowMs: number;    // Rate limit window (900000)
    max: number;         // Max requests per window (100)
  };
  compression: boolean;    // Enable compression (true)
  trustProxy: boolean;    // Trust proxy headers (false)
}
```

**Environment Variables:**

- `API_PORT` - API port (3000)
- `API_HOST` - API host (0.0.0.0)
- `API_CORS_ORIGIN` - Allowed origins (comma-separated)
- `API_CORS_CREDENTIALS` - Allow credentials (true)
- `API_RATE_LIMIT_WINDOW` - Rate limit window in ms (900000)
- `API_RATE_LIMIT_MAX` - Max requests per window (100)
- `API_COMPRESSION` - Enable compression (true)
- `API_TRUST_PROXY` - Trust proxy headers (false)

### Logging Configuration

```typescript
logging: {
  level: string; // Log level (info)
  format: string; // Log format (json)
  pretty: boolean; // Pretty print (false)
  file: {
    enabled: boolean; // Enable file logging (false)
    path: string; // Log file path (./logs/app.log)
    maxSize: string; // Max file size (10m)
    maxFiles: number; // Max files (5)
  }
  structured: boolean; // Structured logging (true)
}
```

**Environment Variables:**

- `LOG_LEVEL` - Log level (trace, debug, info, warn, error, fatal)
- `LOG_FORMAT` - Log format (json, pretty)
- `LOG_PRETTY` - Pretty print logs (true)
- `LOG_FILE_ENABLED` - Enable file logging (false)
- `LOG_FILE_PATH` - Log file path (./logs/app.log)
- `LOG_FILE_MAX_SIZE` - Max file size (10m)
- `LOG_FILE_MAX_FILES` - Max files (5)
- `LOG_STRUCTURED` - Structured logging (true)

### Metrics Configuration

```typescript
metrics: {
  enabled: boolean; // Enable metrics (true)
  port: number; // Metrics port (9090)
  path: string; // Metrics path (/metrics)
  collectDefaultMetrics: boolean; // Collect default metrics (true)
  collectInterval: number; // Collection interval (10000)
}
```

**Environment Variables:**

- `METRICS_ENABLED` - Enable metrics (true)
- `METRICS_PORT` - Metrics port (9090)
- `METRICS_PATH` - Metrics path (/metrics)
- `METRICS_COLLECT_DEFAULT` - Collect default metrics (true)
- `METRICS_COLLECT_INTERVAL` - Collection interval in ms (10000)

### Webhook Configuration

```typescript
webhook: {
  secret: string; // Webhook secret (min 16 chars)
  timeout: number; // Request timeout (30000)
  retryAttempts: number; // Retry attempts (3)
  retryDelay: number; // Retry delay (1000)
  batchSize: number; // Batch size (100)
}
```

**Environment Variables:**

- `WEBHOOK_SECRET` - Webhook secret
- `WEBHOOK_TIMEOUT` - Request timeout in ms (30000)
- `WEBHOOK_RETRY_ATTEMPTS` - Retry attempts (3)
- `WEBHOOK_RETRY_DELAY` - Retry delay in ms (1000)
- `WEBHOOK_BATCH_SIZE` - Batch size (100)

## Configuration Files

### Default Configuration (`config/default.ts`)

```typescript
export default {
  env: 'development',
  nodeEnv: 'development',
  version: '1.0.0',

  database: {
    host: 'localhost',
    port: 5432,
    database: 'tamma_dev',
    username: 'postgres',
    password: 'password',
    ssl: false,
    pool: {
      min: 2,
      max: 10,
      idleTimeoutMillis: 30000,
    },
  },

  redis: {
    host: 'localhost',
    port: 6379,
    db: 0,
    keyPrefix: 'tamma:',
    tls: false,
  },

  // ... other default settings
};
```

### Environment-Specific Configuration (`config/production.ts`)

```typescript
export default {
  env: 'production',
  nodeEnv: 'production',

  database: {
    ssl: true,
    pool: {
      min: 5,
      max: 20,
      idleTimeoutMillis: 60000,
    },
  },

  redis: {
    tls: true,
  },

  logging: {
    level: 'info',
    format: 'json',
    pretty: false,
    file: {
      enabled: true,
      path: '/var/log/tamma/app.log',
      maxSize: '100m',
      maxFiles: 10,
    },
  },

  // ... other production overrides
};
```

## Security Best Practices

### Production Security

1. **Change Default Secrets**: Never use default secrets in production
2. **Use Environment Variables**: Store sensitive data in environment variables
3. **Enable SSL**: Always use SSL for database and Redis connections
4. **Restrict CORS**: Limit CORS origins to specific domains
5. **Enable Rate Limiting**: Implement appropriate rate limits
6. **Monitor Logs**: Enable structured logging and monitoring

### Development Security

1. **Use Development Secrets**: Use development-specific secrets
2. **Local Services**: Use local database and Redis instances
3. **Debug Logging**: Enable debug logging for development
4. **Hot Reloading**: Enable hot reloading for faster development

## Troubleshooting

### Common Issues

1. **Configuration Validation Failed**
   - Check all required fields are present
   - Verify environment variables are set correctly
   - Ensure secret values meet minimum length requirements

2. **Database Connection Failed**
   - Verify database is running
   - Check connection parameters
   - Ensure network connectivity

3. **Redis Connection Failed**
   - Verify Redis is running
   - Check connection parameters
   - Ensure Redis is accessible

### Debug Configuration

```typescript
import { config } from '@tamma/shared/config';

// Get current configuration
const currentConfig = config.getConfig();

// Check environment
console.log('Environment:', config.getEnv());

// Validate configuration
import { validateConfig } from '@tamma/shared/config';
const validation = validateConfig(currentConfig, config.getEnv());
if (!validation.valid) {
  console.error('Configuration errors:', validation.errors);
}
```

## Examples

### Development Setup

```bash
# .env file for development
NODE_ENV=development
DATABASE_HOST=localhost
DATABASE_PORT=5432
DATABASE_NAME=tamma_dev
DATABASE_USERNAME=postgres
DATABASE_PASSWORD=password
REDIS_HOST=localhost
REDIS_PORT=6379
LOG_LEVEL=debug
LOG_PRETTY=true
```

### Production Setup

```bash
# Environment variables for production
NODE_ENV=production
DATABASE_URL=postgresql://user:pass@prod-db:5432/tamma_prod?ssl=true
REDIS_URL=redis://:password@prod-redis:6380/0?tls=true
JWT_ACCESS_SECRET=your-super-secret-access-key
JWT_REFRESH_SECRET=your-super-secret-refresh-key
WEBHOOK_SECRET=your-webhook-secret
LOG_LEVEL=info
API_CORS_ORIGIN=https://app.testplatform.com
```

## Migration Guide

### From Environment Variables to Configuration Files

1. Create `config/default.ts` with default values
2. Create environment-specific files (`config/production.ts`, etc.)
3. Move environment-specific settings to appropriate files
4. Keep sensitive data in environment variables
5. Update deployment scripts to set required environment variables

### Adding New Configuration Options

1. Add TypeScript interfaces to `types.ts`
2. Add validation rules to `validation.ts`
3. Add default values to `default.ts`
4. Add environment variable loading to `manager.ts`
5. Update documentation

```

## Files to Create

1. `packages/shared/src/config/types.ts` - TypeScript interfaces
2. `packages/shared/src/config/manager.ts` - Configuration manager
3. `packages/shared/src/config/validation.ts` - Validation schemas
4. `packages/shared/src/config/default.ts` - Default configuration
5. `packages/shared/src/config/development.ts` - Development config
6. `packages/shared/src/config/test.ts` - Test configuration
7. `packages/shared/src/config/staging.ts` - Staging configuration
8. `packages/shared/src/config/production.ts` - Production configuration
9. `docs/configuration.md` - Configuration documentation

## Dependencies

- dotenv - Environment variable loading
- joi - Schema validation
- TypeScript - Type safety

## Testing

1. Unit tests for configuration loading
2. Validation tests for all schemas
3. Environment-specific configuration tests
4. Error handling tests
5. Integration tests with real environment variables

## Notes

- Always validate configuration on startup
- Use environment variables for sensitive data
- Document all configuration options
- Test configuration in all environments
- Implement configuration hot-reload for development
```
