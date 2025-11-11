# Task 1: Environment Configuration System

**Story**: 1.5 - Configuration Management & Environment Setup  
**Task**: 1 - Environment Configuration System  
**Priority**: Medium  
**Estimated Time**: 2 hours

## Environment Configuration System

Implement a robust configuration management system that supports multiple environments, validation, and runtime configuration updates.

## Configuration Architecture

### 1. Configuration Hierarchy

```
1. Default values (config/default.ts)
2. Environment files (config/{env}.ts)
3. Environment variables
4. Runtime overrides
5. Command line arguments
```

### 2. Environment Types

- **development**: Local development environment
- **test**: Automated testing environment
- **staging**: Pre-production testing
- **production**: Live production environment

## Implementation

### Step 1: Configuration Schema

#### 1.1 Base Configuration Interface

```typescript
// packages/shared/src/config/types.ts
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

export interface RedisConfig {
  host: string;
  port: number;
  password?: string;
  db: number;
  keyPrefix: string;
  tls: boolean;
}

export interface JWTConfig {
  accessTokenSecret: string;
  refreshTokenSecret: string;
  accessTokenExpiry: string;
  refreshTokenExpiry: string;
  issuer: string;
  audience: string;
}

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

export interface MetricsConfig {
  enabled: boolean;
  port: number;
  path: string;
  collectDefaultMetrics: boolean;
  collectInterval: number;
}

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

export interface WebhookConfig {
  secret: string;
  timeout: number;
  retryAttempts: number;
  retryDelay: number;
  batchSize: number;
}

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
```

#### 1.2 Configuration Validation Schema

```typescript
// packages/shared/src/config/validation.ts
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
      windowMs: Joi.number().min(1000).default(900000), // 15 minutes
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
```

### Step 2: Configuration Loader

#### 2.1 Configuration Manager

```typescript
// packages/shared/src/config/manager.ts
import { config } from 'dotenv';
import { readFileSync } from 'fs';
import { resolve } from 'path';
import Joi from 'joi';
import type { AppConfig } from './types';
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
    // Load environment variables
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

  private mergeConfigs(base: Partial<AppConfig>, override: Partial<AppConfig>): Partial<AppConfig {
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

    // Database configuration
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

    // Redis configuration
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

    // JWT configuration
    envConfig.jwt = {
      accessTokenSecret: process.env.JWT_ACCESS_SECRET || 'default-access-secret-change-in-production',
      refreshTokenSecret: process.env.JWT_REFRESH_SECRET || 'default-refresh-secret-change-in-production',
      accessTokenExpiry: process.env.JWT_ACCESS_EXPIRY || '1h',
      refreshTokenExpiry: process.env.JWT_REFRESH_EXPIRY || '30d',
      issuer: process.env.JWT_ISSUER || 'testplatform.com',
      audience: process.env.JWT_AUDIENCE || 'testplatform-api',
    };

    // API configuration
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

    // Logging configuration
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

    // Metrics configuration
    envConfig.metrics = {
      enabled: process.env.METRICS_ENABLED !== 'false',
      port: parseInt(process.env.METRICS_PORT || '9090'),
      path: process.env.METRICS_PATH || '/metrics',
      collectDefaultMetrics: process.env.METRICS_COLLECT_DEFAULT !== 'false',
      collectInterval: parseInt(process.env.METRICS_COLLECT_INTERVAL || '10000'),
    };

    // Email configuration
    const emailProvider = process.env.EMAIL_PROVIDER || 'smtp';
    envConfig.email = { provider: emailProvider };

    if (emailProvider === 'smtp') {
      envConfig.email.smtp = {
        host: process.env.SMTP_HOST || 'localhost',
        port: parseInt(process.env.SMTP_PORT || '587'),
        secure: process.env.SMTP_SECURE === 'true',
        auth: {
          user: process.env.SMTP_USER || '',
          pass: process.env.SMTP_PASS || '',
        },
      };
    } else if (emailProvider === 'sendgrid') {
      envConfig.email.sendgrid = {
        apiKey: process.env.SENDGRID_API_KEY || '',
        from: process.env.SENDGRID_FROM || '',
      };
    }

    // Storage configuration
    const storageProvider = process.env.STORAGE_PROVIDER || 'local';
    envConfig.storage = { provider: storageProvider };

    if (storageProvider === 'local') {
      envConfig.storage.local = {
        path: process.env.STORAGE_LOCAL_PATH || './uploads',
      };
    } else if (storageProvider === 's3') {
      envConfig.storage.s3 = {
        region: process.env.AWS_REGION || '',
        bucket: process.env.AWS_S3_BUCKET || '',
        accessKeyId: process.env.AWS_ACCESS_KEY_ID || '',
        secretAccessKey: process.env.AWS_SECRET_ACCESS_KEY || '',
      };
    }

    // Webhook configuration
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

### Step 3: Environment Configuration Files

#### 3.1 Default Configuration

```typescript
// packages/shared/src/config/default.ts
import type { AppConfig } from './types';

const defaultConfig: Partial<AppConfig> = {
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

  jwt: {
    accessTokenSecret: 'development-access-secret',
    refreshTokenSecret: 'development-refresh-secret',
    accessTokenExpiry: '1h',
    refreshTokenExpiry: '30d',
    issuer: 'testplatform.com',
    audience: 'testplatform-api',
  },

  api: {
    port: 3000,
    host: '0.0.0.0',
    cors: {
      origin: '*',
      credentials: true,
    },
    rateLimit: {
      windowMs: 900000, // 15 minutes
      max: 1000, // Higher limit for development
    },
    compression: true,
    trustProxy: false,
  },

  logging: {
    level: 'debug',
    format: 'pretty',
    pretty: true,
    file: {
      enabled: false,
      path: './logs/app.log',
      maxSize: '10m',
      maxFiles: 5,
    },
    structured: true,
  },

  metrics: {
    enabled: true,
    port: 9090,
    path: '/metrics',
    collectDefaultMetrics: true,
    collectInterval: 10000,
  },

  email: {
    provider: 'smtp',
    smtp: {
      host: 'localhost',
      port: 1025, // Mailhog port for development
      secure: false,
      auth: {
        user: '',
        pass: '',
      },
    },
  },

  storage: {
    provider: 'local',
    local: {
      path: './uploads',
    },
  },

  webhook: {
    secret: 'development-webhook-secret',
    timeout: 30000,
    retryAttempts: 3,
    retryDelay: 1000,
    batchSize: 100,
  },
};

export default defaultConfig;
```

#### 3.2 Production Configuration

```typescript
// packages/shared/src/config/production.ts
import type { AppConfig } from './types';

const productionConfig: Partial<AppConfig> = {
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

  api: {
    cors: {
      origin: ['https://app.testplatform.com', 'https://api.testplatform.com'],
      credentials: true,
    },
    rateLimit: {
      windowMs: 900000, // 15 minutes
      max: 100, // Production rate limit
    },
    compression: true,
    trustProxy: true,
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
    structured: true,
  },

  metrics: {
    enabled: true,
    port: 9090,
    path: '/metrics',
    collectDefaultMetrics: true,
    collectInterval: 30000,
  },
};

export default productionConfig;
```

#### 3.3 Test Configuration

```typescript
// packages/shared/src/config/test.ts
import type { AppConfig } from './types';

const testConfig: Partial<AppConfig> = {
  env: 'test',
  nodeEnv: 'test',

  database: {
    host: 'localhost',
    port: 5433, // Different port for test database
    database: 'tamma_test',
    username: 'postgres',
    password: 'password',
    ssl: false,
    pool: {
      min: 1,
      max: 5,
      idleTimeoutMillis: 10000,
    },
  },

  redis: {
    host: 'localhost',
    port: 6380, // Different port for test redis
    db: 1, // Different database for tests
    keyPrefix: 'tamma:test:',
    tls: false,
  },

  api: {
    port: 3001, // Different port for tests
    host: '127.0.0.1',
    cors: {
      origin: '*',
      credentials: true,
    },
    rateLimit: {
      windowMs: 60000, // 1 minute
      max: 10000, // High limit for tests
    },
    compression: false, // Disable for easier testing
    trustProxy: false,
  },

  logging: {
    level: 'error', // Minimal logging for tests
    format: 'json',
    pretty: false,
    file: {
      enabled: false,
    },
    structured: true,
  },

  metrics: {
    enabled: false, // Disable metrics for tests
  },

  email: {
    provider: 'smtp',
    smtp: {
      host: 'localhost',
      port: 1025,
      secure: false,
      auth: {
        user: '',
        pass: '',
      },
    },
  },

  storage: {
    provider: 'local',
    local: {
      path: './test-uploads',
    },
  },

  webhook: {
    secret: 'test-webhook-secret',
    timeout: 5000, // Shorter timeout for tests
    retryAttempts: 1, // Fewer retries for tests
    retryDelay: 100,
    batchSize: 10,
  },
};

export default testConfig;
```

### Step 4: Configuration Utilities

#### 4.1 Configuration Helpers

```typescript
// packages/shared/src/config/utils.ts
import type { AppConfig } from './types';
import { config } from './manager';

export class ConfigUtils {
  static getDatabaseUrl(cfg: AppConfig): string {
    const { database } = cfg;
    const auth = database.password
      ? `${database.username}:${database.password}`
      : database.username;

    return `postgresql://${auth}@${database.host}:${database.port}/${database.database}${database.ssl ? '?sslmode=require' : ''}`;
  }

  static getRedisUrl(cfg: AppConfig): string {
    const { redis } = cfg;
    const auth = redis.password ? `:${redis.password}@` : '';

    return `redis://${auth}${redis.host}:${redis.port}/${redis.db}`;
  }

  static isProduction(cfg: AppConfig): boolean {
    return cfg.env === 'production';
  }

  static isDevelopment(cfg: AppConfig): boolean {
    return cfg.env === 'development';
  }

  static getLogLevel(cfg: AppConfig): string {
    if (cfg.env === 'production') {
      return 'info';
    } else if (cfg.env === 'test') {
      return 'error';
    } else {
      return cfg.logging.level;
    }
  }

  static getRateLimitConfig(
    cfg: AppConfig,
    tier: 'anonymous' | 'authenticated' | 'premium' | 'enterprise'
  ) {
    const baseConfig = cfg.api.rateLimit;

    const tierMultipliers = {
      anonymous: 1,
      authenticated: 10,
      premium: 100,
      enterprise: 1000,
    };

    return {
      windowMs: baseConfig.windowMs,
      max: baseConfig.max * tierMultipliers[tier],
    };
  }

  static getCorsOrigin(cfg: AppConfig): string | string[] {
    if (cfg.env === 'production') {
      return cfg.api.cors.origin;
    }

    // Allow all origins in development
    return '*';
  }

  static getStorageConfig(cfg: AppConfig) {
    const { storage } = cfg;

    switch (storage.provider) {
      case 's3':
        return {
          provider: 's3',
          config: storage.s3,
        };
      case 'gcs':
        return {
          provider: 'gcs',
          config: storage.gcs,
        };
      case 'azure':
        return {
          provider: 'azure',
          config: storage.azure,
        };
      default:
        return {
          provider: 'local',
          config: storage.local,
        };
    }
  }

  static getEmailConfig(cfg: AppConfig) {
    const { email } = cfg;

    switch (email.provider) {
      case 'sendgrid':
        return {
          provider: 'sendgrid',
          config: email.sendgrid,
        };
      case 'ses':
        return {
          provider: 'ses',
          config: email.ses,
        };
      case 'mailgun':
        return {
          provider: 'mailgun',
          config: email.mailgun,
        };
      default:
        return {
          provider: 'smtp',
          config: email.smtp,
        };
    }
  }

  static validateConfig(cfg: AppConfig): { valid: boolean; errors: string[] } {
    const errors: string[] = [];

    // Validate required secrets in production
    if (cfg.env === 'production') {
      if (cfg.jwt.accessTokenSecret === 'default-access-secret-change-in-production') {
        errors.push('JWT access secret must be changed in production');
      }

      if (cfg.jwt.refreshTokenSecret === 'default-refresh-secret-change-in-production') {
        errors.push('JWT refresh secret must be changed in production');
      }

      if (cfg.webhook.secret === 'default-webhook-secret-change-in-production') {
        errors.push('Webhook secret must be changed in production');
      }
    }

    // Validate database configuration
    if (!cfg.database.host) {
      errors.push('Database host is required');
    }

    if (!cfg.database.database) {
      errors.push('Database name is required');
    }

    if (!cfg.database.username) {
      errors.push('Database username is required');
    }

    // Validate Redis configuration
    if (!cfg.redis.host) {
      errors.push('Redis host is required');
    }

    // Validate email configuration
    if (cfg.email.provider === 'smtp' && !cfg.email.smtp?.host) {
      errors.push('SMTP host is required when using SMTP provider');
    }

    if (cfg.email.provider === 'sendgrid' && !cfg.email.sendgrid?.apiKey) {
      errors.push('SendGrid API key is required when using SendGrid provider');
    }

    // Validate storage configuration
    if (cfg.storage.provider === 's3' && !cfg.storage.s3?.bucket) {
      errors.push('S3 bucket is required when using S3 provider');
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }
}

// Export convenience functions
export const getDatabaseUrl = () => ConfigUtils.getDatabaseUrl(config.getConfig());
export const getRedisUrl = () => ConfigUtils.getRedisUrl(config.getConfig());
export const isProduction = () => ConfigUtils.isProduction(config.getConfig());
export const isDevelopment = () => ConfigUtils.isDevelopment(config.getConfig());
```

### Step 5: Configuration Middleware

#### 5.1 Configuration Validation Middleware

```typescript
// packages/shared/src/config/middleware.ts
import { Request, Response, NextFunction } from 'express';
import { config } from './manager';
import { ConfigUtils } from './utils';

export function validateConfig(req: Request, res: Response, next: NextFunction): void {
  const validation = ConfigUtils.validateConfig(config.getConfig());

  if (!validation.valid) {
    console.error('Configuration validation failed:', validation.errors);

    if (config.isProduction()) {
      return res.status(500).json({
        errors: [
          {
            code: 'CONFIGURATION_ERROR',
            message: 'Server configuration is invalid',
          },
        ],
      });
    } else {
      // In development, show detailed errors
      return res.status(500).json({
        errors: [
          {
            code: 'CONFIGURATION_ERROR',
            message: 'Server configuration is invalid',
            details: validation.errors.join(', '),
          },
        ],
      });
    }
  }

  next();
}

export function configInfo(req: Request, res: Response): void {
  const cfg = config.getConfig();

  // Return non-sensitive configuration info
  const info = {
    env: cfg.env,
    version: cfg.version,
    api: {
      port: cfg.api.port,
      cors: cfg.api.cors,
      rateLimit: cfg.api.rateLimit,
    },
    logging: {
      level: cfg.logging.level,
      format: cfg.logging.format,
    },
    metrics: {
      enabled: cfg.metrics.enabled,
      port: cfg.metrics.port,
    },
    email: {
      provider: cfg.email.provider,
    },
    storage: {
      provider: cfg.storage.provider,
    },
  };

  res.json({ data: info });
}
```

## Testing

### Configuration Tests

```typescript
// packages/shared/src/config/__tests__/manager.test.ts
import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { ConfigManager } from '../manager';

describe('ConfigManager', () => {
  let originalEnv: NodeJS.ProcessEnv;

  beforeEach(() => {
    originalEnv = { ...process.env };
    // Reset singleton
    (ConfigManager as any).instance = null;
  });

  afterEach(() => {
    process.env = originalEnv;
  });

  it('should load default configuration', () => {
    const configManager = ConfigManager.getInstance();
    const config = configManager.getConfig();

    expect(config.env).toBe('development');
    expect(config.database.host).toBe('localhost');
    expect(config.api.port).toBe(3000);
  });

  it('should override with environment variables', () => {
    process.env.NODE_ENV = 'production';
    process.env.API_PORT = '4000';
    process.env.DATABASE_HOST = 'prod-db.example.com';

    const configManager = ConfigManager.getInstance();
    const config = configManager.getConfig();

    expect(config.env).toBe('production');
    expect(config.api.port).toBe(4000);
    expect(config.database.host).toBe('prod-db.example.com');
  });

  it('should validate configuration', () => {
    process.env.JWT_ACCESS_SECRET = 'short';

    expect(() => {
      ConfigManager.getInstance();
    }).toThrow('Configuration validation failed');
  });

  it('should detect environment correctly', () => {
    process.env.NODE_ENV = 'staging';

    const configManager = ConfigManager.getInstance();

    expect(configManager.isStaging()).toBe(true);
    expect(configManager.isProduction()).toBe(false);
    expect(configManager.isDevelopment()).toBe(false);
  });
});
```

## Usage Examples

### Basic Usage

```typescript
import { config } from '@tamma/shared/config';

// Get configuration
const appConfig = config.getConfig();

// Use configuration
const server = fastify({
  port: appConfig.api.port,
  host: appConfig.api.host,
});

// Environment checks
if (config.isProduction()) {
  // Production-specific logic
}
```

### Database Connection

```typescript
import { getDatabaseUrl } from '@tamma/shared/config';

const db = new Database(getDatabaseUrl());
```

### Configuration Validation

```typescript
import { ConfigUtils } from '@tamma/shared/config';
import { config } from '@tamma/shared/config';

const validation = ConfigUtils.validateConfig(config.getConfig());
if (!validation.valid) {
  console.error('Configuration errors:', validation.errors);
  process.exit(1);
}
```

## Security Considerations

1. **Secret Management**: Never commit secrets to version control
2. **Environment Variables**: Use environment variables for sensitive data
3. **Validation**: Always validate configuration on startup
4. **Default Values**: Use secure defaults for production
5. **Logging**: Avoid logging sensitive configuration values

## Best Practices

1. **Environment Isolation**: Separate configs for each environment
2. **Type Safety**: Use TypeScript interfaces for configuration
3. **Validation**: Validate all configuration values
4. **Documentation**: Document all configuration options
5. **Testing**: Test configuration loading and validation
