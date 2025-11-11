# Story 1.7 Task 2: Implement Configuration Loading and Validation

## Task Overview

Implement the PlatformConfigManager class responsible for loading, validating, and managing Git platform configurations. This task handles configuration file parsing, environment variable overrides, endpoint validation, and credential verification with comprehensive error handling and logging.

## Acceptance Criteria

### 2.1 PlatformConfigManager Class Implementation

- [ ] Create PlatformConfigManager class with configuration lifecycle management
- [ ] Implement configuration file loading from JSON and YAML formats
- [ ] Add configuration caching and hot-reload capabilities
- [ ] Implement configuration versioning and migration support
- [ ] Add configuration change event emission and subscription

### 2.2 Configuration File Loading

- [ ] Support JSON configuration file format with validation
- [ ] Support YAML configuration file format with validation
- [ ] Implement configuration file discovery in standard locations
- [ ] Add configuration file permission and security validation
- [ ] Implement configuration file backup and rollback capabilities

### 2.3 Environment Variable Override Support

- [ ] Implement environment variable substitution for sensitive values
- [ ] Support nested environment variable references
- [ ] Add environment variable validation and type conversion
- [ ] Implement environment variable prefix filtering
- [ ] Add environment variable change detection and reload

### 2.4 Endpoint Reachability Validation

- [ ] Implement HTTP/HTTPS endpoint connectivity testing
- [ ] Add DNS resolution validation for platform URLs
- [ ] Implement SSL/TLS certificate validation
- [ ] Add endpoint response time and performance monitoring
- [ ] Implement endpoint health check scheduling

### 2.5 Credential Validation with Test API Calls

- [ ] Implement authentication credential validation
- [ ] Add test API calls for each authentication method
- [ ] Implement credential refresh and renewal logic
- [ ] Add credential expiration monitoring and alerts
- [ ] Implement secure credential caching with encryption

## Implementation Details

### 2.1 PlatformConfigManager Core Class

```typescript
// packages/platforms/src/config/platform-config-manager.ts

import {
  GitPlatformConfiguration,
  PlatformConfig,
  AuthenticationConfig,
  AuthenticationMethod,
  PlatformType,
} from './types';
import { ConfigurationValidator } from './validation';
import { EventEmitter } from 'events';
import { readFile, writeFile, access, constants } from 'fs/promises';
import { resolve, dirname, extname } from 'path';
import { homedir } from 'os';
import * as yaml from 'js-yaml';
import * as json5 from 'json5';
import { Logger } from '@tamma/shared/logging';

/**
 * Configuration file locations in order of precedence
 */
const CONFIG_LOCATIONS = [
  './tamma-platforms.json',
  './tamma-platforms.yaml',
  './tamma-platforms.yml',
  './.tamma/platforms.json',
  './.tamma/platforms.yaml',
  './.tamma/platforms.yml',
  `${homedir()}/.tamma/platforms.json`,
  `${homedir()}/.tamma/platforms.yaml`,
  `${homedir()}/.tamma/platforms.yml`,
  '/etc/tamma/platforms.json',
  '/etc/tamma/platforms.yaml',
  '/etc/tamma/platforms.yml',
];

/**
 * Environment variable prefix for configuration overrides
 */
const ENV_PREFIX = 'TAMMA_PLATFORM_';

/**
 * Configuration change event types
 */
export enum ConfigChangeEvent {
  LOADED = 'loaded',
  VALIDATED = 'validated',
  RELOADED = 'reloaded',
  ERROR = 'error',
  CREDENTIAL_REFRESHED = 'credential_refreshed',
}

/**
 * Configuration validation result
 */
export interface ConfigValidationResult {
  valid: boolean;
  errors: string[];
  warnings: string[];
  normalized?: GitPlatformConfiguration;
}

/**
 * Endpoint health check result
 */
export interface EndpointHealthResult {
  url: string;
  reachable: boolean;
  responseTime: number;
  error?: string;
  sslValid?: boolean;
  certificateInfo?: {
    issuer: string;
    expiresAt: Date;
    daysUntilExpiry: number;
  };
}

/**
 * Credential validation result
 */
export interface CredentialValidationResult {
  platformName: string;
  valid: boolean;
  error?: string;
  expiresAt?: Date;
  requiresRefresh?: boolean;
  testResponse?: any;
}

/**
 * Platform configuration manager with loading, validation, and lifecycle management
 */
export class PlatformConfigManager extends EventEmitter {
  private config: GitPlatformConfiguration | null = null;
  private configPath: string | null = null;
  private lastModified: Date | null = null;
  private validationCache: Map<string, ConfigValidationResult> = new Map();
  private healthCheckCache: Map<string, EndpointHealthResult> = new Map();
  private credentialCache: Map<string, CredentialValidationResult> = new Map();
  private reloadTimer: NodeJS.Timeout | null = null;
  private healthCheckTimer: NodeJS.Timeout | null = null;
  private logger: Logger;

  constructor(logger: Logger) {
    super();
    this.logger = logger.child({ component: 'PlatformConfigManager' });
  }

  /**
   * Load configuration from file with environment variable overrides
   */
  async loadConfiguration(configPath?: string): Promise<GitPlatformConfiguration> {
    try {
      this.logger.info('Loading platform configuration', { configPath });

      // Discover configuration file if not provided
      const filePath = configPath || (await this.discoverConfigFile());
      if (!filePath) {
        throw new Error('No configuration file found. Please create a configuration file.');
      }

      this.configPath = filePath;

      // Load and parse configuration file
      const rawConfig = await this.loadConfigFile(filePath);

      // Apply environment variable overrides
      const configWithOverrides = this.applyEnvironmentOverrides(rawConfig);

      // Validate configuration
      const validationResult = this.validateConfiguration(configWithOverrides);

      if (!validationResult.valid) {
        throw new Error(`Configuration validation failed:\n${validationResult.errors.join('\n')}`);
      }

      // Use normalized configuration
      this.config = validationResult.normalized!;

      // Cache validation result
      this.validationCache.set(filePath, validationResult);

      // Get file modification time
      const stats = await access(filePath, constants.F_OK);
      this.lastModified = new Date();

      // Start background monitoring
      this.startBackgroundMonitoring();

      this.logger.info('Configuration loaded successfully', {
        filePath,
        platforms: this.config.platforms.length,
        version: this.config.version,
      });

      this.emit(ConfigChangeEvent.LOADED, this.config);
      this.emit(ConfigChangeEvent.VALIDATED, validationResult);

      return this.config;
    } catch (error) {
      this.logger.error('Failed to load configuration', { error, configPath });
      this.emit(ConfigChangeEvent.ERROR, error);
      throw error;
    }
  }

  /**
   * Get current configuration
   */
  getConfiguration(): GitPlatformConfiguration | null {
    return this.config;
  }

  /**
   * Get platform configuration by name
   */
  getPlatformConfig(name: string): PlatformConfig | null {
    if (!this.config) {
      return null;
    }

    return this.config.platforms.find((platform) => platform.name === name) || null;
  }

  /**
   * Get all enabled platform configurations
   */
  getEnabledPlatforms(): PlatformConfig[] {
    if (!this.config) {
      return [];
    }

    return this.config.platforms.filter((platform) => platform.enabled);
  }

  /**
   * Reload configuration from file
   */
  async reloadConfiguration(): Promise<GitPlatformConfiguration> {
    if (!this.configPath) {
      throw new Error('No configuration file path available for reload');
    }

    this.logger.info('Reloading configuration', { configPath: this.configPath });

    try {
      const newConfig = await this.loadConfiguration(this.configPath);
      this.emit(ConfigChangeEvent.RELOADED, newConfig);
      return newConfig;
    } catch (error) {
      this.logger.error('Failed to reload configuration', { error });
      this.emit(ConfigChangeEvent.ERROR, error);
      throw error;
    }
  }

  /**
   * Validate endpoint connectivity for all platforms
   */
  async validateEndpoints(): Promise<Map<string, EndpointHealthResult>> {
    if (!this.config) {
      throw new Error('No configuration loaded');
    }

    const results = new Map<string, EndpointHealthResult>();

    for (const platform of this.config.platforms) {
      try {
        const health = await this.checkEndpointHealth(platform.baseUrl);
        results.set(platform.name, health);
        this.healthCheckCache.set(platform.name, health);
      } catch (error) {
        const errorResult: EndpointHealthResult = {
          url: platform.baseUrl,
          reachable: false,
          responseTime: -1,
          error: error.message,
        };
        results.set(platform.name, errorResult);
        this.healthCheckCache.set(platform.name, errorResult);
      }
    }

    return results;
  }

  /**
   * Validate credentials for all platforms
   */
  async validateCredentials(): Promise<Map<string, CredentialValidationResult>> {
    if (!this.config) {
      throw new Error('No configuration loaded');
    }

    const results = new Map<string, CredentialValidationResult>();

    for (const platform of this.config.platforms) {
      try {
        const validation = await this.validatePlatformCredentials(platform);
        results.set(platform.name, validation);
        this.credentialCache.set(platform.name, validation);
      } catch (error) {
        const errorResult: CredentialValidationResult = {
          platformName: platform.name,
          valid: false,
          error: error.message,
        };
        results.set(platform.name, errorResult);
        this.credentialCache.set(platform.name, errorResult);
      }
    }

    return results;
  }

  /**
   * Get cached endpoint health result
   */
  getEndpointHealth(platformName: string): EndpointHealthResult | null {
    return this.healthCheckCache.get(platformName) || null;
  }

  /**
   * Get cached credential validation result
   */
  getCredentialValidation(platformName: string): CredentialValidationResult | null {
    return this.credentialCache.get(platformName) || null;
  }

  /**
   * Save configuration to file
   */
  async saveConfiguration(config: GitPlatformConfiguration, filePath?: string): Promise<void> {
    const targetPath = filePath || this.configPath || './tamma-platforms.json';

    try {
      // Validate configuration before saving
      const validationResult = this.validateConfiguration(config);
      if (!validationResult.valid) {
        throw new Error(
          `Cannot save invalid configuration:\n${validationResult.errors.join('\n')}`
        );
      }

      // Create backup of existing file
      if (await this.fileExists(targetPath)) {
        const backupPath = `${targetPath}.backup.${Date.now()}`;
        await this.copyFile(targetPath, backupPath);
        this.logger.info('Created configuration backup', { backupPath });
      }

      // Save configuration
      const serialized = this.serializeConfiguration(config, targetPath);
      await writeFile(targetPath, serialized, 'utf-8');

      // Set secure permissions
      await this.setSecurePermissions(targetPath);

      this.logger.info('Configuration saved successfully', { filePath: targetPath });
    } catch (error) {
      this.logger.error('Failed to save configuration', { error, filePath: targetPath });
      throw error;
    }
  }

  /**
   * Dispose of resources and stop background monitoring
   */
  async dispose(): Promise<void> {
    this.logger.info('Disposing PlatformConfigManager');

    if (this.reloadTimer) {
      clearInterval(this.reloadTimer);
      this.reloadTimer = null;
    }

    if (this.healthCheckTimer) {
      clearInterval(this.healthCheckTimer);
      this.healthCheckTimer = null;
    }

    this.removeAllListeners();
    this.validationCache.clear();
    this.healthCheckCache.clear();
    this.credentialCache.clear();
  }

  // Private methods

  /**
   * Discover configuration file in standard locations
   */
  private async discoverConfigFile(): Promise<string | null> {
    for (const location of CONFIG_LOCATIONS) {
      try {
        await access(location, constants.R_OK);
        return resolve(location);
      } catch {
        // File doesn't exist or isn't readable, continue
      }
    }
    return null;
  }

  /**
   * Load and parse configuration file
   */
  private async loadConfigFile(filePath: string): Promise<any> {
    const content = await readFile(filePath, 'utf-8');
    const ext = extname(filePath).toLowerCase();

    try {
      switch (ext) {
        case '.json':
          return json5.parse(content);
        case '.yaml':
        case '.yml':
          return yaml.load(content);
        default:
          throw new Error(`Unsupported configuration file format: ${ext}`);
      }
    } catch (error) {
      throw new Error(`Failed to parse configuration file ${filePath}: ${error.message}`);
    }
  }

  /**
   * Apply environment variable overrides
   */
  private applyEnvironmentOverrides(config: any): any {
    const overrides = this.collectEnvironmentOverrides();
    return this.mergeOverrides(config, overrides);
  }

  /**
   * Collect environment variable overrides
   */
  private collectEnvironmentOverrides(): any {
    const overrides: any = {};

    for (const [key, value] of Object.entries(process.env)) {
      if (key.startsWith(ENV_PREFIX)) {
        const configPath = key.substring(ENV_PREFIX.length).toLowerCase();
        this.setNestedValue(overrides, configPath.split('_'), value);
      }
    }

    return overrides;
  }

  /**
   * Set nested value in object using path array
   */
  private setNestedValue(obj: any, path: string[], value: string): void {
    let current = obj;

    for (let i = 0; i < path.length - 1; i++) {
      const segment = path[i];
      if (!(segment in current)) {
        current[segment] = {};
      }
      current = current[segment];
    }

    const finalKey = path[path.length - 1];

    // Try to parse as JSON, fallback to string
    try {
      current[finalKey] = JSON.parse(value);
    } catch {
      current[finalKey] = value;
    }
  }

  /**
   * Merge overrides into configuration
   */
  private mergeOverrides(config: any, overrides: any): any {
    const merged = JSON.parse(JSON.stringify(config)); // Deep clone

    return this.deepMerge(merged, overrides);
  }

  /**
   * Deep merge two objects
   */
  private deepMerge(target: any, source: any): any {
    for (const key in source) {
      if (source[key] && typeof source[key] === 'object' && !Array.isArray(source[key])) {
        target[key] = target[key] || {};
        this.deepMerge(target[key], source[key]);
      } else {
        target[key] = source[key];
      }
    }
    return target;
  }

  /**
   * Validate configuration
   */
  private validateConfiguration(config: any): ConfigValidationResult {
    const errors = ConfigurationValidator.validateConfiguration(config);

    return {
      valid: errors.length === 0,
      errors,
      warnings: [],
      normalized: this.normalizeConfiguration(config),
    };
  }

  /**
   * Normalize configuration with default values
   */
  private normalizeConfiguration(config: any): GitPlatformConfiguration {
    const normalized: GitPlatformConfiguration = {
      version: config.version || '1.0.0',
      platforms: config.platforms || [],
      defaultPlatform: config.defaultPlatform,
      global: {
        defaultTimeout: config.global?.defaultTimeout || 30000,
        rateLimit: config.global?.rateLimit || {
          requestsPerSecond: 10,
          burstSize: 100,
        },
        logging: config.global?.logging || {
          level: 'info',
          format: 'json',
        },
      },
    };

    // Normalize platform configurations
    normalized.platforms = normalized.platforms.map((platform: any) => ({
      ...platform,
      enabled: platform.enabled !== false, // Default to true
      priority: platform.priority || 50,
      defaultBranch: platform.defaultBranch || 'main',
      headers: platform.headers || {},
      auth: this.normalizeAuthentication(platform.auth),
    }));

    return normalized;
  }

  /**
   * Normalize authentication configuration
   */
  private normalizeAuthentication(auth: any): AuthenticationConfig {
    const normalized = { ...auth };

    // Set default timeout
    if (!normalized.timeout) {
      normalized.timeout = 30000;
    }

    // Set default rate limiting
    if (!normalized.rateLimit) {
      normalized.rateLimit = {
        requestsPerSecond: 10,
        burstSize: 100,
      };
    }

    return normalized;
  }

  /**
   * Check endpoint health
   */
  private async checkEndpointHealth(baseUrl: string): Promise<EndpointHealthResult> {
    const startTime = Date.now();

    try {
      // Test basic connectivity
      const response = await fetch(`${baseUrl}/`, {
        method: 'HEAD',
        timeout: 10000,
        signal: AbortSignal.timeout(10000),
      });

      const responseTime = Date.now() - startTime;

      // Check SSL certificate for HTTPS
      let sslValid = true;
      let certificateInfo;

      if (baseUrl.startsWith('https://')) {
        try {
          certificateInfo = await this.checkSSLCertificate(baseUrl);
          sslValid = certificateInfo.daysUntilExpiry > 0;
        } catch (error) {
          sslValid = false;
        }
      }

      return {
        url: baseUrl,
        reachable: response.ok || response.status === 404, // 404 is OK for API endpoints
        responseTime,
        sslValid,
        certificateInfo,
      };
    } catch (error) {
      return {
        url: baseUrl,
        reachable: false,
        responseTime: Date.now() - startTime,
        error: error.message,
      };
    }
  }

  /**
   * Check SSL certificate information
   */
  private async checkSSLCertificate(baseUrl: string): Promise<{
    issuer: string;
    expiresAt: Date;
    daysUntilExpiry: number;
  }> {
    // This would require additional dependencies like 'node-forge' or 'tls'
    // For now, return a placeholder implementation
    const url = new URL(baseUrl);
    const tls = require('tls');
    const connection = tls.connect(443, url.hostname, () => {
      connection.destroy();
    });

    return new Promise((resolve, reject) => {
      connection.on('secureConnect', () => {
        const cert = connection.getPeerCertificate();
        if (!cert) {
          reject(new Error('No certificate found'));
          return;
        }

        const expiresAt = new Date(cert.valid_to);
        const daysUntilExpiry = Math.ceil(
          (expiresAt.getTime() - Date.now()) / (1000 * 60 * 60 * 24)
        );

        resolve({
          issuer: cert.issuer.CN || 'Unknown',
          expiresAt,
          daysUntilExpiry,
        });
      });

      connection.on('error', reject);
      connection.setTimeout(10000, () => {
        connection.destroy();
        reject(new Error('SSL certificate check timeout'));
      });
    });
  }

  /**
   * Validate platform credentials
   */
  private async validatePlatformCredentials(
    platform: PlatformConfig
  ): Promise<CredentialValidationResult> {
    try {
      // Resolve authentication credentials from environment variables
      const auth = this.resolveAuthenticationCredentials(platform.auth);

      // Perform platform-specific validation
      const testResponse = await this.performCredentialTest(platform, auth);

      return {
        platformName: platform.name,
        valid: true,
        testResponse,
        expiresAt: this.getCredentialExpiration(auth),
      };
    } catch (error) {
      return {
        platformName: platform.name,
        valid: false,
        error: error.message,
      };
    }
  }

  /**
   * Resolve authentication credentials from environment variables
   */
  private resolveAuthenticationCredentials(auth: AuthenticationConfig): AuthenticationConfig {
    const resolved = { ...auth };

    // Resolve token from environment variable
    if ('tokenEnvVar' in auth && auth.tokenEnvVar) {
      (resolved as any).token = process.env[auth.tokenEnvVar];
    }

    // Resolve client secret from environment variable
    if ('clientSecretEnvVar' in auth && auth.clientSecretEnvVar) {
      (resolved as any).clientSecret = process.env[auth.clientSecretEnvVar];
    }

    // Resolve private key from environment variable
    if ('privateKeyEnvVar' in auth && auth.privateKeyEnvVar) {
      (resolved as any).privateKey = process.env[auth.privateKeyEnvVar];
    }

    // Resolve webhook secret from environment variable
    if ('webhookSecretEnvVar' in auth && auth.webhookSecretEnvVar) {
      (resolved as any).webhookSecret = process.env[auth.webhookSecretEnvVar];
    }

    return resolved;
  }

  /**
   * Perform platform-specific credential test
   */
  private async performCredentialTest(
    platform: PlatformConfig,
    auth: AuthenticationConfig
  ): Promise<any> {
    switch (platform.type) {
      case PlatformType.GITHUB:
        return this.testGitHubCredentials(platform.baseUrl, auth);
      case PlatformType.GITLAB:
        return this.testGitLabCredentials(platform.baseUrl, auth);
      case PlatformType.GITEA:
      case PlatformType.FORGEJO:
        return this.testGiteaCredentials(platform.baseUrl, auth);
      default:
        throw new Error(`Unsupported platform type: ${platform.type}`);
    }
  }

  /**
   * Test GitHub credentials
   */
  private async testGitHubCredentials(baseUrl: string, auth: AuthenticationConfig): Promise<any> {
    const apiUrl = baseUrl.includes('api.github.com') ? baseUrl : `${baseUrl}/api/v4`;

    const response = await fetch(`${apiUrl}/user`, {
      headers: {
        Authorization: `Bearer ${(auth as any).token}`,
        'User-Agent': 'Tamma/1.0.0',
      },
      timeout: 10000,
    });

    if (!response.ok) {
      throw new Error(`GitHub credential test failed: ${response.status} ${response.statusText}`);
    }

    return response.json();
  }

  /**
   * Test GitLab credentials
   */
  private async testGitLabCredentials(baseUrl: string, auth: AuthenticationConfig): Promise<any> {
    const apiUrl = `${baseUrl}/api/v4`;

    const response = await fetch(`${apiUrl}/user`, {
      headers: {
        Authorization: `Bearer ${(auth as any).token}`,
        'User-Agent': 'Tamma/1.0.0',
      },
      timeout: 10000,
    });

    if (!response.ok) {
      throw new Error(`GitLab credential test failed: ${response.status} ${response.statusText}`);
    }

    return response.json();
  }

  /**
   * Test Gitea/Forgejo credentials
   */
  private async testGiteaCredentials(baseUrl: string, auth: AuthenticationConfig): Promise<any> {
    const apiUrl = `${baseUrl}/api/v1`;

    const response = await fetch(`${apiUrl}/user`, {
      headers: {
        Authorization: `token ${(auth as any).token}`,
        'User-Agent': 'Tamma/1.0.0',
      },
      timeout: 10000,
    });

    if (!response.ok) {
      throw new Error(`Gitea credential test failed: ${response.status} ${response.statusText}`);
    }

    return response.json();
  }

  /**
   * Get credential expiration date
   */
  private getCredentialExpiration(auth: AuthenticationConfig): Date | undefined {
    if ('expiresAt' in auth && auth.expiresAt) {
      return new Date(auth.expiresAt);
    }
    return undefined;
  }

  /**
   * Start background monitoring for configuration changes and health checks
   */
  private startBackgroundMonitoring(): void {
    // Configuration file monitoring (every 30 seconds)
    this.reloadTimer = setInterval(async () => {
      try {
        if (this.configPath && (await this.hasFileChanged(this.configPath))) {
          this.logger.info('Configuration file changed, reloading');
          await this.reloadConfiguration();
        }
      } catch (error) {
        this.logger.error('Error during configuration monitoring', { error });
      }
    }, 30000);

    // Health check monitoring (every 5 minutes)
    this.healthCheckTimer = setInterval(async () => {
      try {
        await this.validateEndpoints();
      } catch (error) {
        this.logger.error('Error during health check monitoring', { error });
      }
    }, 300000);
  }

  /**
   * Check if file has been modified
   */
  private async hasFileChanged(filePath: string): Promise<boolean> {
    try {
      const stats = await access(filePath, constants.F_OK);
      const currentModified = new Date();

      if (!this.lastModified || currentModified > this.lastModified) {
        this.lastModified = currentModified;
        return true;
      }

      return false;
    } catch {
      return false;
    }
  }

  /**
   * Serialize configuration to file format
   */
  private serializeConfiguration(config: GitPlatformConfiguration, filePath: string): string {
    const ext = extname(filePath).toLowerCase();

    switch (ext) {
      case '.json':
        return JSON.stringify(config, null, 2);
      case '.yaml':
      case '.yml':
        return yaml.dump(config, { indent: 2 });
      default:
        return JSON.stringify(config, null, 2);
    }
  }

  /**
   * Check if file exists
   */
  private async fileExists(filePath: string): Promise<boolean> {
    try {
      await access(filePath, constants.F_OK);
      return true;
    } catch {
      return false;
    }
  }

  /**
   * Copy file
   */
  private async copyFile(source: string, destination: string): Promise<void> {
    const content = await readFile(source, 'utf-8');
    await writeFile(destination, content, 'utf-8');
  }

  /**
   * Set secure file permissions (owner read/write only)
   */
  private async setSecurePermissions(filePath: string): Promise<void> {
    try {
      const { chmod } = require('fs/promises');
      await chmod(filePath, 0o600); // rw-------
    } catch (error) {
      this.logger.warn('Failed to set secure file permissions', { filePath, error });
    }
  }
}
```

### 2.2 Configuration File Security and Validation

```typescript
// packages/platforms/src/config/security.ts

import { readFile, stat } from 'fs/promises';
import { resolve } from 'path';
import { createHash } from 'crypto';

/**
 * Configuration file security utilities
 */
export class ConfigSecurity {
  /**
   * Validate configuration file permissions
   */
  static async validateFilePermissions(filePath: string): Promise<{
    valid: boolean;
    issues: string[];
  }> {
    const issues: string[] = [];

    try {
      const stats = await stat(filePath);
      const mode = stats.mode;

      // Check if file is readable by others
      if (mode & 0o004) {
        // Others read
        issues.push('Configuration file is readable by others (security risk)');
      }

      // Check if file is writable by group
      if (mode & 0o020) {
        // Group write
        issues.push('Configuration file is writable by group (security risk)');
      }

      // Check if file is writable by others
      if (mode & 0o002) {
        // Others write
        issues.push('Configuration file is writable by others (security risk)');
      }

      return {
        valid: issues.length === 0,
        issues,
      };
    } catch (error) {
      return {
        valid: false,
        issues: [`Unable to check file permissions: ${error.message}`],
      };
    }
  }

  /**
   * Calculate file checksum for integrity verification
   */
  static async calculateChecksum(filePath: string): Promise<string> {
    try {
      const content = await readFile(filePath);
      return createHash('sha256').update(content).digest('hex');
    } catch (error) {
      throw new Error(`Failed to calculate checksum for ${filePath}: ${error.message}`);
    }
  }

  /**
   * Verify file integrity against expected checksum
   */
  static async verifyIntegrity(filePath: string, expectedChecksum: string): Promise<boolean> {
    try {
      const actualChecksum = await this.calculateChecksum(filePath);
      return actualChecksum === expectedChecksum;
    } catch {
      return false;
    }
  }

  /**
   * Sanitize configuration data by removing sensitive information
   */
  static sanitizeForLogging(config: any): any {
    const sanitized = JSON.parse(JSON.stringify(config)); // Deep clone

    const sensitiveKeys = ['token', 'clientSecret', 'privateKey', 'webhookSecret', 'passphrase'];

    const sanitizeObject = (obj: any): void => {
      for (const key in obj) {
        if (typeof obj[key] === 'object' && obj[key] !== null) {
          sanitizeObject(obj[key]);
        } else if (
          sensitiveKeys.some((sensitive) => key.toLowerCase().includes(sensitive.toLowerCase()))
        ) {
          obj[key] = '[REDACTED]';
        }
      }
    };

    sanitizeObject(sanitized);
    return sanitized;
  }

  /**
   * Validate environment variable security
   */
  static validateEnvironmentSecurity(): {
    valid: boolean;
    issues: string[];
  } {
    const issues: string[] = [];

    // Check for sensitive data in environment variables
    const sensitivePatterns = [/token/i, /secret/i, /key/i, /password/i];

    for (const [key, value] of Object.entries(process.env)) {
      if (sensitivePatterns.some((pattern) => pattern.test(key)) && value) {
        // Check if value looks like actual secret (not placeholder)
        if (value.length > 10 && !value.includes('${') && !value.includes('<')) {
          issues.push(`Sensitive data found in environment variable: ${key}`);
        }
      }
    }

    return {
      valid: issues.length === 0,
      issues,
    };
  }
}
```

### 2.3 Configuration Migration System

```typescript
// packages/platforms/src/config/migration.ts

import { GitPlatformConfiguration } from './types';
import { Logger } from '@tamma/shared/logging';

/**
 * Configuration migration interface
 */
export interface ConfigMigration {
  version: string;
  description: string;
  migrate: (config: any) => GitPlatformConfiguration;
}

/**
 * Configuration migration manager
 */
export class ConfigMigrationManager {
  private migrations: ConfigMigration[] = [];
  private logger: Logger;

  constructor(logger: Logger) {
    this.logger = logger.child({ component: 'ConfigMigrationManager' });
    this.registerMigrations();
  }

  /**
   * Migrate configuration to latest version
   */
  async migrateConfiguration(config: any): Promise<GitPlatformConfiguration> {
    const currentVersion = this.parseVersion(config.version || '0.0.0');
    let migratedConfig = config;

    // Apply migrations in order
    for (const migration of this.migrations) {
      const migrationVersion = this.parseVersion(migration.version);

      if (this.compareVersions(currentVersion, migrationVersion) < 0) {
        this.logger.info('Applying configuration migration', {
          from: config.version || '0.0.0',
          to: migration.version,
          description: migration.description,
        });

        migratedConfig = migration.migrate(migratedConfig);
      }
    }

    return migratedConfig;
  }

  /**
   * Get latest configuration version
   */
  getLatestVersion(): string {
    if (this.migrations.length === 0) {
      return '1.0.0';
    }

    return this.migrations[this.migrations.length - 1].version;
  }

  /**
   * Register all available migrations
   */
  private registerMigrations(): void {
    this.migrations = [
      {
        version: '1.0.0',
        description: 'Initial configuration format',
        migrate: (config) => this.migrateToV1_0_0(config),
      },
      {
        version: '1.1.0',
        description: 'Add global configuration section',
        migrate: (config) => this.migrateToV1_1_0(config),
      },
      {
        version: '1.2.0',
        description: 'Add platform priority and enhanced authentication',
        migrate: (config) => this.migrateToV1_2_0(config),
      },
    ];
  }

  /**
   * Migration to version 1.0.0
   */
  private migrateToV1_0_0(config: any): GitPlatformConfiguration {
    return {
      version: '1.0.0',
      platforms: config.platforms || [],
      defaultPlatform: config.defaultPlatform,
    };
  }

  /**
   * Migration to version 1.1.0
   */
  private migrateToV1_1_0(config: GitPlatformConfiguration): GitPlatformConfiguration {
    return {
      ...config,
      version: '1.1.0',
      global: config.global || {
        defaultTimeout: 30000,
        rateLimit: {
          requestsPerSecond: 10,
          burstSize: 100,
        },
        logging: {
          level: 'info',
          format: 'json',
        },
      },
    };
  }

  /**
   * Migration to version 1.2.0
   */
  private migrateToV1_2_0(config: GitPlatformConfiguration): GitPlatformConfiguration {
    return {
      ...config,
      version: '1.2.0',
      platforms: config.platforms.map((platform) => ({
        ...platform,
        priority: platform.priority || 50,
        headers: platform.headers || {},
        auth: {
          ...platform.auth,
          timeout: platform.auth.timeout || 30000,
          rateLimit: platform.auth.rateLimit || {
            requestsPerSecond: 10,
            burstSize: 100,
          },
        },
      })),
    };
  }

  /**
   * Parse semantic version string
   */
  private parseVersion(version: string): { major: number; minor: number; patch: number } {
    const match = version.match(/^(\d+)\.(\d+)\.(\d+)$/);
    if (!match) {
      return { major: 0, minor: 0, patch: 0 };
    }

    return {
      major: parseInt(match[1], 10),
      minor: parseInt(match[2], 10),
      patch: parseInt(match[3], 10),
    };
  }

  /**
   * Compare two semantic versions
   */
  private compareVersions(
    v1: { major: number; minor: number; patch: number },
    v2: { major: number; minor: number; patch: number }
  ): number {
    if (v1.major !== v2.major) {
      return v1.major - v2.major;
    }
    if (v1.minor !== v2.minor) {
      return v1.minor - v2.minor;
    }
    return v1.patch - v2.patch;
  }
}
```

## Testing Strategy

### 2.1 Unit Tests for PlatformConfigManager

```typescript
// packages/platforms/src/config/platform-config-manager.test.ts

import { PlatformConfigManager } from './platform-config-manager';
import { Logger } from '@tamma/shared/logging';
import { PlatformType, AuthenticationMethod } from './types';
import { tmpdir } from 'os';
import { join } from 'path';
import { writeFile, unlink } from 'fs/promises';

describe('PlatformConfigManager', () => {
  let configManager: PlatformConfigManager;
  let logger: Logger;
  let tempConfigPath: string;

  beforeEach(() => {
    logger = {
      child: jest.fn(() => logger),
      info: jest.fn(),
      warn: jest.fn(),
      error: jest.fn(),
      debug: jest.fn(),
    } as any;

    configManager = new PlatformConfigManager(logger);
    tempConfigPath = join(tmpdir(), `tamma-test-${Date.now()}.json`);
  });

  afterEach(async () => {
    await configManager.dispose();
    try {
      await unlink(tempConfigPath);
    } catch {
      // File might not exist
    }
  });

  describe('Configuration Loading', () => {
    it('should load valid JSON configuration', async () => {
      const config = {
        version: '1.0.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      await writeFile(tempConfigPath, JSON.stringify(config, null, 2));

      const loadedConfig = await configManager.loadConfiguration(tempConfigPath);

      expect(loadedConfig.version).toBe('1.0.0');
      expect(loadedConfig.platforms).toHaveLength(1);
      expect(loadedConfig.platforms[0].type).toBe(PlatformType.GITHUB);
    });

    it('should load YAML configuration', async () => {
      const config = `
version: "1.0.0"
platforms:
  - type: gitlab
    name: GitLab
    baseUrl: https://gitlab.com
    auth:
      method: oauth2
      clientId: test-client-id
      redirectUri: https://example.com/callback
      scopes:
        - read_api
        - read_repository
    defaultBranch: main
    enabled: true
    priority: 90
      `;

      const yamlPath = tempConfigPath.replace('.json', '.yaml');
      await writeFile(yamlPath, config);

      const loadedConfig = await configManager.loadConfiguration(yamlPath);

      expect(loadedConfig.version).toBe('1.0.0');
      expect(loadedConfig.platforms).toHaveLength(1);
      expect(loadedConfig.platforms[0].type).toBe(PlatformType.GITLAB);
    });

    it('should apply environment variable overrides', async () => {
      // Set environment variable
      process.env.TAMMA_PLATFORM_PLATFORMS_0_ENABLED = 'false';
      process.env.TAMMA_PLATFORM_GLOBAL_DEFAULT_TIMEOUT = '60000';

      const config = {
        version: '1.0.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      await writeFile(tempConfigPath, JSON.stringify(config, null, 2));

      const loadedConfig = await configManager.loadConfiguration(tempConfigPath);

      expect(loadedConfig.platforms[0].enabled).toBe(false);
      expect(loadedConfig.global?.defaultTimeout).toBe(60000);

      // Clean up
      delete process.env.TAMMA_PLATFORM_PLATFORMS_0_ENABLED;
      delete process.env.TAMMA_PLATFORM_GLOBAL_DEFAULT_TIMEOUT;
    });

    it('should reject invalid configuration', async () => {
      const config = {
        version: 'invalid',
        platforms: [],
      };

      await writeFile(tempConfigPath, JSON.stringify(config, null, 2));

      await expect(configManager.loadConfiguration(tempConfigPath)).rejects.toThrow(
        'Configuration validation failed'
      );
    });
  });

  describe('Endpoint Validation', () => {
    beforeEach(async () => {
      const config = {
        version: '1.0.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      await writeFile(tempConfigPath, JSON.stringify(config, null, 2));
      await configManager.loadConfiguration(tempConfigPath);
    });

    it('should validate endpoint connectivity', async () => {
      const results = await configManager.validateEndpoints();

      expect(results).toBeDefined();
      expect(results.size).toBe(1);

      const githubResult = results.get('GitHub');
      expect(githubResult).toBeDefined();
      expect(githubResult.url).toBe('https://github.com');
      expect(typeof githubResult.reachable).toBe('boolean');
      expect(typeof githubResult.responseTime).toBe('number');
    });
  });

  describe('Credential Validation', () => {
    beforeEach(async () => {
      const config = {
        version: '1.0.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      await writeFile(tempConfigPath, JSON.stringify(config, null, 2));
      await configManager.loadConfiguration(tempConfigPath);
    });

    it('should validate credentials with environment variable', async () => {
      // Set mock token
      process.env.GITHUB_TOKEN = 'mock-token';

      const results = await configManager.validateCredentials();

      expect(results).toBeDefined();
      expect(results.size).toBe(1);

      const githubResult = results.get('GitHub');
      expect(githubResult).toBeDefined();
      expect(githubResult.platformName).toBe('GitHub');

      // Clean up
      delete process.env.GITHUB_TOKEN;
    });

    it('should handle missing credentials', async () => {
      const results = await configManager.validateCredentials();

      const githubResult = results.get('GitHub');
      expect(githubResult?.valid).toBe(false);
      expect(githubResult?.error).toBeDefined();
    });
  });

  describe('Configuration Management', () => {
    it('should save configuration', async () => {
      const config = {
        version: '1.0.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      await writeFile(tempConfigPath, JSON.stringify(config, null, 2));
      await configManager.loadConfiguration(tempConfigPath);

      const updatedConfig = {
        ...config,
        platforms: [
          {
            ...config.platforms[0],
            enabled: false,
          },
        ],
      };

      await configManager.saveConfiguration(updatedConfig);

      const reloadedConfig = await configManager.loadConfiguration(tempConfigPath);
      expect(reloadedConfig.platforms[0].enabled).toBe(false);
    });

    it('should reload configuration', async () => {
      const config = {
        version: '1.0.0',
        platforms: [
          {
            type: PlatformType.GITHUB,
            name: 'GitHub',
            baseUrl: 'https://github.com',
            auth: {
              method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
              tokenEnvVar: 'GITHUB_TOKEN',
            },
            defaultBranch: 'main',
            enabled: true,
            priority: 100,
          },
        ],
      };

      await writeFile(tempConfigPath, JSON.stringify(config, null, 2));
      await configManager.loadConfiguration(tempConfigPath);

      // Modify file externally
      const updatedConfig = {
        ...config,
        version: '1.0.1',
      };
      await writeFile(tempConfigPath, JSON.stringify(updatedConfig, null, 2));

      const reloadedConfig = await configManager.reloadConfiguration();
      expect(reloadedConfig.version).toBe('1.0.1');
    });
  });
});
```

## Completion Checklist

- [ ] Implement PlatformConfigManager class with full lifecycle management
- [ ] Add configuration file loading for JSON and YAML formats
- [ ] Implement environment variable override system
- [ ] Add endpoint reachability validation with SSL checking
- [ ] Implement credential validation with test API calls
- [ ] Add configuration security validation and sanitization
- [ ] Implement configuration migration system
- [ ] Add comprehensive unit tests for all functionality
- [ ] Add integration tests with real configuration files
- [ ] Verify error handling and logging requirements
- [ ] Ensure TypeScript strict mode compliance
- [ ] Add performance monitoring and caching

## Dependencies

- Task 1: Configuration schema and interfaces
- JSON5 and YAML parsing libraries
- File system monitoring capabilities
- SSL/TLS certificate validation
- HTTP client for endpoint testing
- Logger from @tamma/shared package

## Estimated Time

**Core Implementation**: 4-5 days
**File Loading & Parsing**: 2-3 days
**Environment Overrides**: 2-3 days
**Endpoint Validation**: 2-3 days
**Credential Validation**: 3-4 days
**Security & Migration**: 2-3 days
**Testing**: 3-4 days
**Total**: 18-25 days
