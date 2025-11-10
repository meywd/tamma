# Task 9: Configuration Management System

## Overview

This task implements a unified configuration management system for all AI providers in the Tamma platform. This system provides centralized configuration loading, validation, hot-reloading, and secure credential management across all provider implementations.

## Acceptance Criteria

### 9.1: Create unified configuration schema for all providers

- [ ] Define comprehensive configuration schema for all providers
- [ ] Implement configuration validation with detailed error messages
- [ ] Add configuration inheritance and composition support
- [ ] Create configuration templates and presets
- [ ] Implement configuration schema versioning

### 9.2: Implement configuration validation and loading

- [ ] Create configuration loader with multiple source support
- [ ] Implement JSON schema validation with custom validators
- [ ] Add configuration transformation and normalization
- [ ] Implement configuration merging and override logic
- [ ] Create configuration error reporting and recovery

### 9.3: Add environment variable support

- [ ] Implement environment variable mapping and substitution
- [ ] Add environment-specific configuration loading
- [ ] Create environment variable validation and type conversion
- [ ] Implement environment variable precedence rules
- [ ] Add environment variable encryption support

### 9.4: Create configuration hot-reload capability

- [ ] Implement file system watching for configuration changes
- [ ] Add configuration change detection and validation
- [ ] Create graceful configuration reloading without service interruption
- [ ] Implement configuration rollback on validation failures
- [ ] Add configuration change notifications

### 9.5: Add credential encryption and secure storage

- [ ] Implement credential encryption at rest
- [ ] Add OS-specific secure storage integration
- [ ] Create credential rotation and management
- [ ] Implement credential access controls and auditing
- [ ] Add credential backup and recovery

## Technical Implementation

### Configuration Schema Definition

```typescript
// src/core/config/unified-provider-config.schema.ts
export const UnifiedProviderConfigSchema = {
  $schema: 'https://json-schema.org/draft/2020-12/schema',
  $id: 'https://tamma.ai/schemas/unified-provider-config.json',
  title: 'Unified AI Provider Configuration',
  description: 'Configuration schema for all AI providers in the Tamma platform',
  type: 'object',
  required: ['version', 'providers'],
  properties: {
    version: {
      type: 'string',
      pattern: '^\\d+\\.\\d+\\.\\d+$',
      description: 'Configuration schema version',
    },
    global: {
      type: 'object',
      description: 'Global configuration settings',
      properties: {
        logging: {
          type: 'object',
          properties: {
            level: {
              type: 'string',
              enum: ['debug', 'info', 'warn', 'error'],
              default: 'info',
            },
            format: {
              type: 'string',
              enum: ['json', 'text'],
              default: 'json',
            },
            file: {
              type: 'string',
              description: 'Log file path',
            },
          },
        },
        monitoring: {
          type: 'object',
          properties: {
            enabled: { type: 'boolean', default: true },
            metricsInterval: { type: 'integer', default: 60000 },
            healthCheckInterval: { type: 'integer', default: 30000 },
          },
        },
        security: {
          type: 'object',
          properties: {
            encryptCredentials: { type: 'boolean', default: true },
            auditLogging: { type: 'boolean', default: true },
            accessControl: { type: 'boolean', default: false },
          },
        },
        rateLimits: {
          type: 'object',
          properties: {
            globalRequestsPerMinute: { type: 'integer', default: 1000 },
            globalTokensPerMinute: { type: 'integer', default: 1000000 },
            defaultRetryAttempts: { type: 'integer', default: 3 },
            defaultTimeout: { type: 'integer', default: 30000 },
          },
        },
      },
    },
    providers: {
      type: 'object',
      patternProperties: {
        '^[a-zA-Z0-9_-]+$': {
          type: 'object',
          required: ['enabled', 'type'],
          properties: {
            enabled: {
              type: 'boolean',
              description: 'Whether this provider is enabled',
            },
            type: {
              type: 'string',
              enum: [
                'anthropic-claude',
                'openai',
                'google-gemini',
                'github-copilot',
                'opencode',
                'zai',
                'zen-mcp',
                'openrouter',
                'local-llm',
              ],
              description: 'Provider type identifier',
            },
            priority: {
              type: 'integer',
              minimum: 1,
              maximum: 100,
              default: 50,
              description: 'Provider priority for load balancing',
            },
            weight: {
              type: 'number',
              minimum: 0,
              maximum: 1,
              default: 1,
              description: 'Provider weight for load balancing',
            },
            config: {
              type: 'object',
              description: 'Provider-specific configuration',
            },
            credentials: {
              type: 'object',
              description: 'Provider credentials (will be encrypted)',
            },
            rateLimits: {
              type: 'object',
              description: 'Provider-specific rate limits',
            },
            features: {
              type: 'object',
              description: 'Provider feature flags',
            },
            models: {
              type: 'object',
              description: 'Provider model configuration',
            },
          },
          allOf: [
            {
              if: {
                properties: { type: { const: 'anthropic-claude' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/anthropicClaudeConfig' },
                },
              },
            },
            {
              if: {
                properties: { type: { const: 'openai' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/openaiConfig' },
                },
              },
            },
            {
              if: {
                properties: { type: { const: 'google-gemini' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/googleGeminiConfig' },
                },
              },
            },
            {
              if: {
                properties: { type: { const: 'github-copilot' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/githubCopilotConfig' },
                },
              },
            },
            {
              if: {
                properties: { type: { const: 'opencode' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/opencodeConfig' },
                },
              },
            },
            {
              if: {
                properties: { type: { const: 'zai' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/zaiConfig' },
                },
              },
            },
            {
              if: {
                properties: { type: { const: 'zen-mcp' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/zenMcpConfig' },
                },
              },
            },
            {
              if: {
                properties: { type: { const: 'openrouter' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/openrouterConfig' },
                },
              },
            },
            {
              if: {
                properties: { type: { const: 'local-llm' } },
              },
              then: {
                properties: {
                  config: { $ref: '#/$defs/localLlmConfig' },
                },
              },
            },
          ],
        },
      },
    },
    profiles: {
      type: 'object',
      description: 'Configuration profiles for different environments',
      patternProperties: {
        '^[a-zA-Z0-9_-]+$': {
          type: 'object',
          properties: {
            description: { type: 'string' },
            inherits: {
              type: 'array',
              items: { type: 'string' },
              description: 'Parent profiles to inherit from',
            },
            overrides: {
              type: 'object',
              description: 'Configuration overrides for this profile',
            },
          },
        },
      },
    },
  },
  $defs: {
    anthropicClaudeConfig: {
      $ref: 'file://./src/providers/configs/anthropic-claude-config.schema.json',
    },
    openaiConfig: {
      $ref: 'file://./src/providers/configs/openai-config.schema.json',
    },
    googleGeminiConfig: {
      $ref: 'file://./src/providers/configs/google-gemini-config.schema.json',
    },
    githubCopilotConfig: {
      $ref: 'file://./src/providers/configs/github-copilot-config.schema.json',
    },
    opencodeConfig: {
      $ref: 'file://./src/providers/configs/opencode-config.schema.json',
    },
    zaiConfig: {
      $ref: 'file://./src/providers/configs/zai-config.schema.json',
    },
    zenMcpConfig: {
      $ref: 'file://./src/providers/configs/zen-mcp-config.schema.json',
    },
    openrouterConfig: {
      $ref: 'file://./src/providers/configs/openrouter-config.schema.json',
    },
    localLlmConfig: {
      $ref: 'file://./src/providers/configs/local-llm-config.schema.json',
    },
  },
};
```

### Configuration Manager Implementation

```typescript
// src/core/config/configuration-manager.ts
export class ConfigurationManager {
  private readonly config: UnifiedProviderConfig;
  private readonly schema: any;
  private readonly validator: Ajv;
  private readonly credentialManager: CredentialManager;
  private readonly environmentManager: EnvironmentManager;
  private readonly watchers: Map<string, ConfigWatcher[]>;
  private readonly logger: Logger;
  private readonly encryptionKey: string;

  constructor(options: ConfigurationManagerOptions) {
    this.logger = new Logger({ service: 'configuration-manager' });
    this.encryptionKey = options.encryptionKey || this.generateEncryptionKey();
    this.validator = new Ajv({ allErrors: true });
    this.credentialManager = new CredentialManager(this.encryptionKey);
    this.environmentManager = new EnvironmentManager();
    this.watchers = new Map();

    this.schema = UnifiedProviderConfigSchema;
    this.addCustomValidators();
    this.validator.addSchema(this.schema);

    this.config = this.loadConfiguration(options);
  }

  async initialize(): Promise<void> {
    await this.credentialManager.initialize();
    await this.environmentManager.initialize();
    await this.validateConfiguration();
    await this.setupWatchers();

    this.logger.info('Configuration manager initialized');
  }

  get<T = any>(path: string, defaultValue?: T): T {
    return this.getNestedValue(this.config, path, defaultValue);
  }

  async set(path: string, value: any): Promise<void> {
    this.setNestedValue(this.config, path, value);

    try {
      await this.validateConfiguration();
      await this.notifyWatchers(path, value);
      await this.saveConfiguration();
    } catch (error) {
      // Rollback on validation failure
      throw new ConfigurationError(
        'VALIDATION_FAILED',
        `Invalid configuration at ${path}: ${error.message}`
      );
    }
  }

  async watch(path: string, callback: ConfigChangeCallback): Promise<() => void> {
    if (!this.watchers.has(path)) {
      this.watchers.set(path, []);
    }

    const watcher: ConfigWatcher = {
      id: crypto.randomUUID(),
      callback,
      path,
    };

    this.watchers.get(path)!.push(watcher);

    return () => {
      const watchers = this.watchers.get(path);
      if (watchers) {
        const index = watchers.findIndex((w) => w.id === watcher.id);
        if (index >= 0) {
          watchers.splice(index, 1);
        }
      }
    };
  }

  getProviderConfig(providerId: string): ProviderConfig | null {
    const providerConfig = this.config.providers[providerId];
    if (!providerConfig || !providerConfig.enabled) {
      return null;
    }

    return {
      ...providerConfig,
      credentials: providerConfig.credentials
        ? this.credentialManager.decryptCredentials(providerConfig.credentials)
        : undefined,
    };
  }

  async updateProviderConfig(providerId: string, updates: Partial<ProviderConfig>): Promise<void> {
    const currentConfig = this.config.providers[providerId] || {};
    const updatedConfig = { ...currentConfig, ...updates };

    // Encrypt credentials if present
    if (updatedConfig.credentials) {
      updatedConfig.credentials = await this.credentialManager.encryptCredentials(
        updatedConfig.credentials
      );
    }

    await this.set(`providers.${providerId}`, updatedConfig);
  }

  async reload(): Promise<void> {
    try {
      const newConfig = this.loadConfiguration({
        encryptionKey: this.encryptionKey,
      });

      await this.validateConfiguration(newConfig);

      // Replace current config
      Object.assign(this.config, newConfig);

      // Notify all watchers
      await this.notifyAllWatchers();

      this.logger.info('Configuration reloaded successfully');
    } catch (error) {
      this.logger.error('Failed to reload configuration:', error);
      throw new ConfigurationError(
        'RELOAD_FAILED',
        `Configuration reload failed: ${error.message}`
      );
    }
  }

  async save(): Promise<void> {
    await this.saveConfiguration();
  }

  getSchema(): any {
    return this.schema;
  }

  validate(config: any): ValidationResult {
    const validate = this.validator.compile(this.schema);
    const valid = validate(config);

    return {
      valid,
      errors: validate.errors || [],
      warnings: this.collectWarnings(config),
    };
  }

  private loadConfiguration(options: ConfigurationManagerOptions): UnifiedProviderConfig {
    const sources = this.getConfigSources(options);
    let config: any = {};

    // Load and merge configurations in order of precedence
    for (const source of sources) {
      try {
        const sourceConfig = this.loadFromSource(source);
        config = this.mergeConfigurations(config, sourceConfig);
      } catch (error) {
        this.logger.warn(`Failed to load configuration from ${source.type}:`, error);
      }
    }

    // Apply environment variable substitutions
    config = this.environmentManager.substituteVariables(config);

    // Apply profile overrides
    config = this.applyProfiles(config, options.profile);

    return config;
  }

  private getConfigSources(options: ConfigurationManagerOptions): ConfigSource[] {
    const sources: ConfigSource[] = [];

    // Default configuration
    if (options.defaultConfig) {
      sources.push({ type: 'default', data: options.defaultConfig });
    }

    // Configuration files
    if (options.configPath) {
      sources.push({ type: 'file', path: options.configPath });
    }

    // Environment-specific files
    const env = process.env.NODE_ENV || 'development';
    sources.push({ type: 'file', path: options.configPath?.replace(/\.json$/, `.${env}.json`) });

    // Environment variables
    sources.push({ type: 'environment' });

    // Command line arguments
    if (options.argv) {
      sources.push({ type: 'argv', data: options.argv });
    }

    return sources;
  }

  private loadFromSource(source: ConfigSource): any {
    switch (source.type) {
      case 'default':
        return source.data;

      case 'file':
        return this.loadFromFile(source.path!);

      case 'environment':
        return this.loadFromEnvironment();

      case 'argv':
        return this.loadFromArgv(source.data);

      default:
        throw new Error(`Unknown configuration source type: ${source.type}`);
    }
  }

  private loadFromFile(filePath: string): any {
    if (!fs.existsSync(filePath)) {
      this.logger.debug(`Configuration file not found: ${filePath}`);
      return {};
    }

    try {
      const content = fs.readFileSync(filePath, 'utf-8');
      return JSON.parse(content);
    } catch (error) {
      throw new Error(`Failed to parse configuration file ${filePath}: ${error.message}`);
    }
  }

  private loadFromEnvironment(): any {
    const envConfig: any = {};

    // Parse environment variables with TAMMA_ prefix
    for (const [key, value] of Object.entries(process.env)) {
      if (key.startsWith('TAMMA_')) {
        const configPath = key.substring(6).toLowerCase().replace(/_/g, '.');
        this.setNestedValue(envConfig, configPath, this.parseEnvironmentValue(value));
      }
    }

    return envConfig;
  }

  private loadFromArgv(argv: any): any {
    // Parse command line arguments
    const argvConfig: any = {};

    for (const [key, value] of Object.entries(argv)) {
      if (key.startsWith('--')) {
        const configPath = key.substring(2).replace(/-/g, '.');
        this.setNestedValue(argvConfig, configPath, value);
      }
    }

    return argvConfig;
  }

  private mergeConfigurations(base: any, override: any): any {
    return deepmerge(base, override, {
      arrayMerge: (destination, source) => source,
      customMerge: (key) => {
        // Special handling for credentials
        if (key === 'credentials') {
          return (target: any, source: any) => ({ ...target, ...source });
        }
        return undefined;
      },
    });
  }

  private applyProfiles(config: any, profileName?: string): any {
    if (!profileName || !config.profiles) {
      return config;
    }

    const profile = config.profiles[profileName];
    if (!profile) {
      throw new Error(`Profile not found: ${profileName}`);
    }

    let result = { ...config };

    // Apply inherited profiles first
    if (profile.inherits) {
      for (const parentProfileName of profile.inherits) {
        result = this.applyProfiles(result, parentProfileName);
      }
    }

    // Apply profile overrides
    if (profile.overrides) {
      result = this.mergeConfigurations(result, profile.overrides);
    }

    return result;
  }

  private async validateConfiguration(config: any = this.config): Promise<void> {
    const result = this.validate(config);

    if (!result.valid) {
      const errorMessage = result.errors
        .map((err) => `${err.instancePath || 'root'}: ${err.message}`)
        .join('; ');

      throw new ConfigurationError(
        'VALIDATION_FAILED',
        `Configuration validation failed: ${errorMessage}`
      );
    }

    // Log warnings
    if (result.warnings.length > 0) {
      for (const warning of result.warnings) {
        this.logger.warn(`Configuration warning: ${warning}`);
      }
    }
  }

  private async saveConfiguration(): Promise<void> {
    const configPath = process.env.TAMMA_CONFIG_PATH || './tamma-config.json';

    // Create a copy without sensitive data
    const configToSave = JSON.parse(JSON.stringify(this.config));

    // Encrypt credentials
    for (const [providerId, providerConfig] of Object.entries(configToSave.providers || {})) {
      if ((providerConfig as any).credentials) {
        (providerConfig as any).credentials = await this.credentialManager.encryptCredentials(
          (providerConfig as any).credentials
        );
      }
    }

    try {
      fs.writeFileSync(configPath, JSON.stringify(configToSave, null, 2));
      this.logger.debug(`Configuration saved to ${configPath}`);
    } catch (error) {
      throw new ConfigurationError('SAVE_FAILED', `Failed to save configuration: ${error.message}`);
    }
  }

  private async setupWatchers(): Promise<void> {
    const configPath = process.env.TAMMA_CONFIG_PATH || './tamma-config.json';

    if (fs.existsSync(configPath)) {
      fs.watchFile(configPath, { interval: 1000 }, async () => {
        try {
          await this.reload();
        } catch (error) {
          this.logger.error('Auto-reload failed:', error);
        }
      });
    }
  }

  private async notifyWatchers(path: string, value: any): Promise<void> {
    const watchers = this.watchers.get(path) || [];

    for (const watcher of watchers) {
      try {
        await watcher.callback(path, value, this.config);
      } catch (error) {
        this.logger.error(`Error in configuration watcher for ${path}:`, error);
      }
    }
  }

  private async notifyAllWatchers(): Promise<void> {
    for (const [path, watchers] of this.watchers) {
      const value = this.get(path);
      await this.notifyWatchers(path, value);
    }
  }

  private getNestedValue(obj: any, path: string, defaultValue?: any): any {
    const keys = path.split('.');
    let current = obj;

    for (const key of keys) {
      if (current === null || current === undefined || !(key in current)) {
        return defaultValue;
      }
      current = current[key];
    }

    return current;
  }

  private setNestedValue(obj: any, path: string, value: any): void {
    const keys = path.split('.');
    let current = obj;

    for (let i = 0; i < keys.length - 1; i++) {
      const key = keys[i];
      if (!(key in current) || typeof current[key] !== 'object') {
        current[key] = {};
      }
      current = current[key];
    }

    current[keys[keys.length - 1]] = value;
  }

  private parseEnvironmentValue(value: string): any {
    // Try to parse as JSON
    try {
      return JSON.parse(value);
    } catch {
      // Return as string if not valid JSON
      return value;
    }
  }

  private collectWarnings(config: any): string[] {
    const warnings: string[] = [];

    // Check for deprecated settings
    if (config.global?.debug) {
      warnings.push('global.debug is deprecated, use global.logging.level instead');
    }

    // Check for security issues
    for (const [providerId, providerConfig] of Object.entries(config.providers || {})) {
      if ((providerConfig as any).credentials?.apiKey) {
        warnings.push(`Provider ${providerId} has plaintext API key in configuration`);
      }
    }

    return warnings;
  }

  private addCustomValidators(): void {
    // Add custom validation keywords
    this.validator.addKeyword({
      keyword: 'providerType',
      type: 'string',
      schemaType: 'string',
      compile: (schemaVal: string) => {
        return function validate(data: string) {
          return typeof data === 'string' && schemaVal === data;
        };
      },
    });

    this.validator.addKeyword({
      keyword: 'encrypted',
      type: 'string',
      compile: () => {
        return function validate(data: string) {
          // Check if string looks encrypted (base64 with specific pattern)
          return /^[A-Za-z0-9+/]+=*$/.test(data) && data.length > 20;
        };
      },
    });
  }

  private generateEncryptionKey(): string {
    return crypto.randomBytes(32).toString('hex');
  }
}

// Type definitions
export interface ConfigurationManagerOptions {
  configPath?: string;
  defaultConfig?: any;
  profile?: string;
  argv?: any;
  encryptionKey?: string;
}

export interface ConfigSource {
  type: 'default' | 'file' | 'environment' | 'argv';
  data?: any;
  path?: string;
}

export interface ConfigWatcher {
  id: string;
  path: string;
  callback: ConfigChangeCallback;
}

export type ConfigChangeCallback = (
  path: string,
  value: any,
  fullConfig: any
) => void | Promise<void>;

export interface ValidationResult {
  valid: boolean;
  errors: any[];
  warnings: string[];
}

export class ConfigurationError extends Error {
  constructor(
    public code: string,
    message: string,
    public details?: any
  ) {
    super(message);
    this.name = 'ConfigurationError';
  }
}
```

### Credential Manager Implementation

```typescript
// src/core/config/credential-manager.ts
export class CredentialManager {
  private readonly encryptionKey: string;
  private readonly algorithm = 'aes-256-gcm';
  private readonly keyDerivationInfo = 'tamma-credentials';
  private readonly logger: Logger;

  constructor(encryptionKey: string) {
    this.encryptionKey = encryptionKey;
    this.logger = new Logger({ service: 'credential-manager' });
  }

  async initialize(): Promise<void> {
    // Test encryption/decryption
    try {
      const test = 'test-value';
      const encrypted = await this.encrypt(test);
      const decrypted = await this.decrypt(encrypted);

      if (decrypted !== test) {
        throw new Error('Encryption/decryption test failed');
      }
    } catch (error) {
      throw new CredentialError(
        'INITIALIZATION_FAILED',
        'Failed to initialize credential manager',
        error
      );
    }
  }

  async encryptCredentials(credentials: Record<string, any>): Promise<Record<string, string>> {
    const encrypted: Record<string, string> = {};

    for (const [key, value] of Object.entries(credentials)) {
      if (value !== null && value !== undefined) {
        encrypted[key] = await this.encrypt(JSON.stringify(value));
      }
    }

    return encrypted;
  }

  async decryptCredentials(
    encryptedCredentials: Record<string, string>
  ): Promise<Record<string, any>> {
    const decrypted: Record<string, any> = {};

    for (const [key, encryptedValue] of Object.entries(encryptedCredentials)) {
      if (encryptedValue) {
        try {
          decrypted[key] = JSON.parse(await this.decrypt(encryptedValue));
        } catch (error) {
          this.logger.error(`Failed to decrypt credential ${key}:`, error);
          throw new CredentialError(
            'DECRYPTION_FAILED',
            `Failed to decrypt credential ${key}`,
            error
          );
        }
      }
    }

    return decrypted;
  }

  async encrypt(text: string): Promise<string> {
    try {
      const key = crypto.scryptSync(this.encryptionKey, this.keyDerivationInfo, 32);
      const iv = crypto.randomBytes(16);

      const cipher = crypto.createCipher(this.algorithm, key);
      cipher.setAAD(Buffer.from(this.keyDerivationInfo));

      let encrypted = cipher.update(text, 'utf8', 'hex');
      encrypted += cipher.final('hex');

      const authTag = cipher.getAuthTag();

      // Combine IV, authTag, and encrypted data
      return iv.toString('hex') + ':' + authTag.toString('hex') + ':' + encrypted;
    } catch (error) {
      throw new CredentialError('ENCRYPTION_FAILED', 'Failed to encrypt credentials', error);
    }
  }

  async decrypt(encryptedText: string): Promise<string> {
    try {
      const key = crypto.scryptSync(this.encryptionKey, this.keyDerivationInfo, 32);

      // Split IV, authTag, and encrypted data
      const parts = encryptedText.split(':');
      if (parts.length !== 3) {
        throw new Error('Invalid encrypted format');
      }

      const iv = Buffer.from(parts[0], 'hex');
      const authTag = Buffer.from(parts[1], 'hex');
      const encrypted = parts[2];

      const decipher = crypto.createDecipher(this.algorithm, key);
      decipher.setAAD(Buffer.from(this.keyDerivationInfo));
      decipher.setAuthTag(authTag);

      let decrypted = decipher.update(encrypted, 'hex', 'utf8');
      decrypted += decipher.final('utf8');

      return decrypted;
    } catch (error) {
      throw new CredentialError('DECRYPTION_FAILED', 'Failed to decrypt credentials', error);
    }
  }

  async storeSecurely(providerId: string, credentials: Record<string, any>): Promise<void> {
    try {
      // Try OS-specific secure storage first
      if (process.platform !== 'linux') {
        await this.storeInKeychain(providerId, credentials);
      } else {
        // Fallback to encrypted file storage
        await this.storeInEncryptedFile(providerId, credentials);
      }
    } catch (error) {
      this.logger.error(`Failed to store credentials for ${providerId}:`, error);
      throw new CredentialError(
        'STORAGE_FAILED',
        `Failed to store credentials for ${providerId}`,
        error
      );
    }
  }

  async retrieveSecurely(providerId: string): Promise<Record<string, any> | null> {
    try {
      // Try OS-specific secure storage first
      if (process.platform !== 'linux') {
        return await this.retrieveFromKeychain(providerId);
      } else {
        // Fallback to encrypted file storage
        return await this.retrieveFromEncryptedFile(providerId);
      }
    } catch (error) {
      this.logger.error(`Failed to retrieve credentials for ${providerId}:`, error);
      return null;
    }
  }

  async deleteSecurely(providerId: string): Promise<void> {
    try {
      if (process.platform !== 'linux') {
        await this.deleteFromKeychain(providerId);
      } else {
        await this.deleteFromEncryptedFile(providerId);
      }
    } catch (error) {
      this.logger.error(`Failed to delete credentials for ${providerId}:`, error);
      throw new CredentialError(
        'DELETION_FAILED',
        `Failed to delete credentials for ${providerId}`,
        error
      );
    }
  }

  private async storeInKeychain(
    providerId: string,
    credentials: Record<string, any>
  ): Promise<void> {
    const keytar = await import('keytar');
    const serviceName = 'tamma-ai-providers';
    const account = providerId;
    const password = JSON.stringify(credentials);

    await keytar.setPassword(serviceName, account, password);
  }

  private async retrieveFromKeychain(providerId: string): Promise<Record<string, any> | null> {
    const keytar = await import('keytar');
    const serviceName = 'tamma-ai-providers';
    const account = providerId;

    const password = await keytar.getPassword(serviceName, account);
    return password ? JSON.parse(password) : null;
  }

  private async deleteFromKeychain(providerId: string): Promise<void> {
    const keytar = await import('keytar');
    const serviceName = 'tamma-ai-providers';
    const account = providerId;

    await keytar.deletePassword(serviceName, account);
  }

  private async storeInEncryptedFile(
    providerId: string,
    credentials: Record<string, any>
  ): Promise<void> {
    const configDir = this.getConfigDir();
    const credentialsFile = path.join(configDir, `credentials-${providerId}.enc`);

    // Ensure config directory exists
    await fs.promises.mkdir(configDir, { recursive: true });

    const encrypted = await this.encryptCredentials(credentials);
    await fs.promises.writeFile(credentialsFile, JSON.stringify(encrypted), { mode: 0o600 });
  }

  private async retrieveFromEncryptedFile(providerId: string): Promise<Record<string, any> | null> {
    const configDir = this.getConfigDir();
    const credentialsFile = path.join(configDir, `credentials-${providerId}.enc`);

    try {
      const encrypted = JSON.parse(await fs.promises.readFile(credentialsFile, 'utf-8'));
      return await this.decryptCredentials(encrypted);
    } catch (error) {
      if (error.code === 'ENOENT') {
        return null;
      }
      throw error;
    }
  }

  private async deleteFromEncryptedFile(providerId: string): Promise<void> {
    const configDir = this.getConfigDir();
    const credentialsFile = path.join(configDir, `credentials-${providerId}.enc`);

    try {
      await fs.promises.unlink(credentialsFile);
    } catch (error) {
      if (error.code !== 'ENOENT') {
        throw error;
      }
    }
  }

  private getConfigDir(): string {
    const homeDir = os.homedir();
    return path.join(homeDir, '.tamma');
  }
}

export class CredentialError extends Error {
  constructor(
    public code: string,
    message: string,
    public cause?: Error
  ) {
    super(message);
    this.name = 'CredentialError';
  }
}
```

### Environment Manager Implementation

```typescript
// src/core/config/environment-manager.ts
export class EnvironmentManager {
  private readonly logger: Logger;
  private readonly variableMappings: Map<string, string>;
  private readonly typeConverters: Map<string, (value: string) => any>;

  constructor() {
    this.logger = new Logger({ service: 'environment-manager' });
    this.variableMappings = new Map();
    this.typeConverters = new Map();
    this.initializeMappings();
  }

  async initialize(): Promise<void> {
    // Load environment-specific mappings
    await this.loadEnvironmentMappings();
    this.logger.debug('Environment manager initialized');
  }

  substituteVariables(config: any): any {
    if (typeof config === 'string') {
      return this.substituteInString(config);
    } else if (Array.isArray(config)) {
      return config.map((item) => this.substituteVariables(item));
    } else if (config && typeof config === 'object') {
      const result: any = {};
      for (const [key, value] of Object.entries(config)) {
        result[key] = this.substituteVariables(value);
      }
      return result;
    }

    return config;
  }

  getVariable(name: string): string | undefined {
    // Check direct environment variable
    let value = process.env[name];

    // Check mapped variable
    if (!value) {
      const mappedName = this.variableMappings.get(name);
      if (mappedName) {
        value = process.env[mappedName];
      }
    }

    return value;
  }

  getVariableWithDefault(name: string, defaultValue: string): string {
    return this.getVariable(name) || defaultValue;
  }

  getTypedVariable<T>(name: string, type: 'string' | 'number' | 'boolean' | 'json'): T | undefined {
    const value = this.getVariable(name);
    if (!value) {
      return undefined;
    }

    const converter = this.typeConverters.get(type);
    return converter ? converter(value) : value;
  }

  private substituteInString(text: string): string {
    // Replace ${VAR_NAME} patterns
    return text.replace(/\$\{([^}]+)\}/g, (match, varName) => {
      const value = this.getVariable(varName);
      return value !== undefined ? value : match;
    });
  }

  private initializeMappings(): void {
    // Common environment variable mappings
    this.variableMappings.set('api_key', 'API_KEY');
    this.variableMappings.set('api_key', 'API_KEY');
    this.variableMappings.set('secret_key', 'SECRET_KEY');
    this.variableMappings.set('access_token', 'ACCESS_TOKEN');
    this.variableMappings.set('refresh_token', 'REFRESH_TOKEN');
    this.variableMappings.set('client_id', 'CLIENT_ID');
    this.variableMappings.set('client_secret', 'CLIENT_SECRET');
    this.variableMappings.set('webhook_url', 'WEBHOOK_URL');
    this.variableMappings.set('base_url', 'BASE_URL');
    this.variableMappings.set('timeout', 'TIMEOUT');
    this.variableMappings.set('max_retries', 'MAX_RETRIES');
    this.variableMappings.set('enable_debug', 'ENABLE_DEBUG');
    this.variableMappings.set('log_level', 'LOG_LEVEL');

    // Provider-specific mappings
    this.variableMappings.set('anthropic_api_key', 'ANTHROPIC_API_KEY');
    this.variableMappings.set('openai_api_key', 'OPENAI_API_KEY');
    this.variableMappings.set('google_ai_api_key', 'GOOGLE_AI_API_KEY');
    this.variableMappings.set('github_token', 'GITHUB_TOKEN');
    this.variableMappings.set('opencode_api_key', 'OPENCODE_API_KEY');
    this.variableMappings.set('zai_api_key', 'ZAI_API_KEY');
    this.variableMappings.set('openrouter_api_key', 'OPENROUTER_API_KEY');
    this.variableMappings.set('ollama_host', 'OLLAMA_HOST');

    // Initialize type converters
    this.typeConverters.set('string', (value: string) => value);
    this.typeConverters.set('number', (value: string) => parseFloat(value));
    this.typeConverters.set('boolean', (value: string) => {
      const lower = value.toLowerCase();
      return lower === 'true' || lower === '1' || lower === 'yes';
    });
    this.typeConverters.set('json', (value: string) => {
      try {
        return JSON.parse(value);
      } catch {
        throw new Error(`Invalid JSON value: ${value}`);
      }
    });
  }

  private async loadEnvironmentMappings(): Promise<void> {
    // Load additional mappings from environment file if present
    const envFile = process.env.TAMMA_ENV_FILE || '.tamma.env';

    if (fs.existsSync(envFile)) {
      try {
        const envContent = await fs.promises.readFile(envFile, 'utf-8');
        const envLines = envContent.split('\n');

        for (const line of envLines) {
          const trimmed = line.trim();
          if (trimmed && !trimmed.startsWith('#')) {
            const [key, ...valueParts] = trimmed.split('=');
            if (key && valueParts.length > 0) {
              const value = valueParts.join('=');
              this.variableMappings.set(key.toLowerCase(), key);
              process.env[key] = value;
            }
          }
        }

        this.logger.debug(`Loaded environment mappings from ${envFile}`);
      } catch (error) {
        this.logger.warn(`Failed to load environment file ${envFile}:`, error);
      }
    }
  }
}
```

## Testing Strategy

### Unit Tests

```typescript
// tests/core/config/configuration-manager.test.ts
describe('ConfigurationManager', () => {
  let configManager: ConfigurationManager;
  let tempDir: string;

  beforeEach(async () => {
    tempDir = await fs.mkdtemp('tamma-config-test-');
    configManager = new ConfigurationManager({
      configPath: path.join(tempDir, 'config.json'),
      encryptionKey: 'test-encryption-key-32-chars-long',
    });
  });

  afterEach(async () => {
    await fs.rm(tempDir, { recursive: true, force: true });
  });

  it('should load configuration from file', async () => {
    const testConfig = {
      version: '1.0.0',
      providers: {
        'test-provider': {
          enabled: true,
          type: 'anthropic-claude',
          config: { apiKey: 'test-key' },
        },
      },
    };

    await fs.writeFile(path.join(tempDir, 'config.json'), JSON.stringify(testConfig));

    await configManager.initialize();

    expect(configManager.get('providers.test-provider.enabled')).toBe(true);
    expect(configManager.get('providers.test-provider.type')).toBe('anthropic-claude');
  });

  it('should validate configuration against schema', async () => {
    const invalidConfig = {
      version: '1.0.0',
      providers: {
        'test-provider': {
          enabled: true,
          type: 'invalid-type',
        },
      },
    };

    await fs.writeFile(path.join(tempDir, 'config.json'), JSON.stringify(invalidConfig));

    await expect(configManager.initialize()).rejects.toThrow('VALIDATION_FAILED');
  });

  it('should substitute environment variables', async () => {
    process.env.TEST_API_KEY = 'substituted-key';

    const testConfig = {
      version: '1.0.0',
      providers: {
        'test-provider': {
          enabled: true,
          type: 'anthropic-claude',
          config: { apiKey: '${TEST_API_KEY}' },
        },
      },
    };

    await fs.writeFile(path.join(tempDir, 'config.json'), JSON.stringify(testConfig));

    await configManager.initialize();

    expect(configManager.get('providers.test-provider.config.apiKey')).toBe('substituted-key');

    delete process.env.TEST_API_KEY;
  });

  it('should watch for configuration changes', async () => {
    const testConfig = {
      version: '1.0.0',
      providers: {
        'test-provider': {
          enabled: true,
          type: 'anthropic-claude',
        },
      },
    };

    await fs.writeFile(path.join(tempDir, 'config.json'), JSON.stringify(testConfig));

    await configManager.initialize();

    const callback = jest.fn();
    const unwatch = await configManager.watch('providers.test-provider.enabled', callback);

    // Update configuration
    await configManager.set('providers.test-provider.enabled', false);

    expect(callback).toHaveBeenCalledWith(
      'providers.test-provider.enabled',
      false,
      expect.any(Object)
    );

    unwatch();
  });
});

// tests/core/config/credential-manager.test.ts
describe('CredentialManager', () => {
  let credentialManager: CredentialManager;

  beforeEach(() => {
    credentialManager = new CredentialManager('test-encryption-key-32-chars-long');
  });

  it('should encrypt and decrypt credentials', async () => {
    const credentials = {
      apiKey: 'test-api-key',
      secret: 'test-secret',
    };

    const encrypted = await credentialManager.encryptCredentials(credentials);
    const decrypted = await credentialManager.decryptCredentials(encrypted);

    expect(decrypted).toEqual(credentials);
  });

  it('should handle encryption errors gracefully', async () => {
    const invalidEncrypted = 'invalid-encrypted-data';

    await expect(
      credentialManager.decryptCredentials({ apiKey: invalidEncrypted })
    ).rejects.toThrow('DECRYPTION_FAILED');
  });
});
```

## Success Metrics

### Configuration Management Metrics

- Configuration loading time: < 100ms
- Validation time: < 50ms
- Hot-reload latency: < 500ms
- Credential encryption/decryption time: < 10ms
- Environment substitution accuracy: 100%

### Reliability Metrics

- Configuration validation accuracy: 100%
- Hot-reload success rate: 99%+
- Credential security: 100%
- Error handling coverage: 100%
- Test coverage: 95%+

### Security Metrics

- Credential encryption strength: AES-256-GCM
- Key derivation security: scrypt with salt
- Access control enforcement: 100%
- Audit logging completeness: 100%
- Data leakage prevention: 100%

## Dependencies

### External Dependencies

- `ajv`: JSON schema validation
- `deepmerge`: Object merging with customization
- `keytar`: OS-specific secure credential storage
- `chokidar`: File system watching for hot-reload

### Internal Dependencies

- `@tamma/shared/types`: Shared type definitions
- `@tamma/core/errors`: Error handling utilities
- `@tamma/core/logging`: Logging utilities

## Security Considerations

### Credential Security

- Use AES-256-GCM for encryption
- Implement proper key derivation with scrypt
- Store credentials in OS keychain when available
- Use file permissions 0600 for credential files
- Never log credentials or encryption keys

### Configuration Security

- Validate all configuration inputs
- Sanitize environment variable substitutions
- Implement configuration access controls
- Audit all configuration changes
- Use secure defaults for sensitive settings

### Network Security

- Validate URLs and endpoints in configuration
- Implement certificate pinning for external services
- Use secure protocols (HTTPS/WSS) only
- Implement timeout and retry limits

## Deliverables

1. **Configuration Manager** (`src/core/config/configuration-manager.ts`)
2. **Credential Manager** (`src/core/config/credential-manager.ts`)
3. **Environment Manager** (`src/core/config/environment-manager.ts`)
4. **Unified Schema** (`src/core/config/unified-provider-config.schema.ts`)
5. **Configuration Templates** (`config/templates/`)
6. **Unit Tests** (`tests/core/config/`)
7. **Integration Tests** (`tests/core/config-integration.test.ts`)
8. **Documentation** (`docs/core/configuration-management.md`)

This implementation provides comprehensive configuration management with validation, hot-reloading, secure credential storage, and environment variable support while maintaining security and reliability across all AI providers.
