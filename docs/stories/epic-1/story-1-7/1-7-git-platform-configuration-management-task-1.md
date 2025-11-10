# Story 1.7 Task 1: Design Configuration Schema and Interfaces

## Task Overview

Design comprehensive configuration schemas and interfaces for Git platform management, supporting multiple platforms (GitHub, GitLab, Gitea, Forgejo) with flexible authentication methods and platform-specific configurations. This task establishes the foundation for centralized Git platform configuration management.

## Acceptance Criteria

### 1.1 Platform Configuration Interface Design

- [ ] Create PlatformConfig interface with common and platform-specific fields
- [ ] Define authentication method types (PAT, OAuth, App) with proper typing
- [ ] Create platform-specific configuration interfaces (GitHubConfig, GitLabConfig, etc.)
- [ ] Implement configuration inheritance and default value patterns
- [ ] Add TypeScript strict mode compliance with proper type safety

### 1.2 Authentication Method Types

- [ ] Define AuthenticationMethod enum with all supported auth types
- [ ] Create PAT (Personal Access Token) configuration interface
- [ ] Define OAuth2 configuration with client credentials and flow settings
- [ ] Create App authentication configuration for GitHub Apps and GitLab Apps
- [ ] Implement authentication method validation and compatibility checking

### 1.3 Configuration Validation Schemas

- [ ] Create JSON Schema validation for platform configurations
- [ ] Implement field-level validation with custom error messages
- [ ] Add cross-field validation (e.g., OAuth requires client_id and client_secret)
- [ ] Create validation for platform-specific requirements and constraints
- [ ] Implement configuration normalization and sanitization

### 1.4 Platform Detection Logic Design

- [ ] Design URL pattern matching for platform detection
- [ ] Create repository URL parsing and platform identification
- [ ] Implement platform-specific URL format validation
- [ ] Add support for self-hosted instance detection
- [ ] Create platform priority and fallback logic

## Implementation Details

### 1.1 Core Configuration Interfaces

```typescript
// packages/platforms/src/config/types.ts

/**
 * Supported authentication methods for Git platforms
 */
export enum AuthenticationMethod {
  PERSONAL_ACCESS_TOKEN = 'pat',
  OAUTH2 = 'oauth2',
  APP = 'app',
  SSH_KEY = 'ssh_key',
}

/**
 * Supported Git platform types
 */
export enum PlatformType {
  GITHUB = 'github',
  GITLAB = 'gitlab',
  GITEA = 'gitea',
  FORGEJO = 'forgejo',
}

/**
 * Base authentication configuration interface
 */
export interface BaseAuthenticationConfig {
  method: AuthenticationMethod;
  /**
   * Environment variable name for token/secret override
   * If provided, the actual token will be read from this environment variable
   */
  tokenEnvVar?: string;
  /**
   * Additional headers to include in API requests
   */
  headers?: Record<string, string>;
  /**
   * Request timeout in milliseconds
   */
  timeout?: number;
  /**
   * Rate limiting configuration
   */
  rateLimit?: {
    requestsPerSecond: number;
    burstSize: number;
  };
}

/**
 * Personal Access Token authentication configuration
 */
export interface PATAuthenticationConfig extends BaseAuthenticationConfig {
  method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN;
  /**
   * The personal access token (should be overridden by environment variable)
   */
  token?: string;
  /**
   * Token scopes/permissions required
   */
  scopes?: string[];
  /**
   * Token expiration date (if known)
   */
  expiresAt?: string;
}

/**
 * OAuth2 authentication configuration
 */
export interface OAuth2AuthenticationConfig extends BaseAuthenticationConfig {
  method: AuthenticationMethod.OAUTH2;
  /**
   * OAuth2 client ID
   */
  clientId: string;
  /**
   * OAuth2 client secret (should be overridden by environment variable)
   */
  clientSecret?: string;
  /**
   * OAuth2 client secret environment variable override
   */
  clientSecretEnvVar?: string;
  /**
   * OAuth2 redirect URI
   */
  redirectUri: string;
  /**
   * OAuth2 scopes to request
   */
  scopes: string[];
  /**
   * OAuth2 token endpoint (if different from default)
   */
  tokenEndpoint?: string;
  /**
   * OAuth2 authorization endpoint (if different from default)
   */
  authorizationEndpoint?: string;
}

/**
 * App authentication configuration (GitHub Apps, GitLab Apps)
 */
export interface AppAuthenticationConfig extends BaseAuthenticationConfig {
  method: AuthenticationMethod.APP;
  /**
   * App ID
   */
  appId: string;
  /**
   * Private key for JWT signing (should be overridden by environment variable)
   */
  privateKey?: string;
  /**
   * Private key environment variable override
   */
  privateKeyEnvVar?: string;
  /**
   * Installation ID (for specific installation access)
   */
  installationId?: string;
  /**
   * Webhook secret for event verification
   */
  webhookSecret?: string;
  /**
   * Webhook secret environment variable override
   */
  webhookSecretEnvVar?: string;
}

/**
 * SSH Key authentication configuration
 */
export interface SSHKeyAuthenticationConfig extends BaseAuthenticationConfig {
  method: AuthenticationMethod.SSH_KEY;
  /**
   * SSH private key (should be overridden by environment variable)
   */
  privateKey?: string;
  /**
   * SSH private key environment variable override
   */
  privateKeyEnvVar?: string;
  /**
   * SSH key passphrase (if encrypted)
   */
  passphrase?: string;
  /**
   * SSH key passphrase environment variable override
   */
  passphraseEnvVar?: string;
  /**
   * SSH key type (rsa, ed25519, etc.)
   */
  keyType?: 'rsa' | 'ed25519' | 'ecdsa';
}

/**
 * Union type for all authentication configurations
 */
export type AuthenticationConfig =
  | PATAuthenticationConfig
  | OAuth2AuthenticationConfig
  | AppAuthenticationConfig
  | SSHKeyAuthenticationConfig;

/**
 * Base platform configuration interface
 */
export interface BasePlatformConfig {
  /**
   * Platform type identifier
   */
  type: PlatformType;
  /**
   * Human-readable platform name
   */
  name: string;
  /**
   * Base URL for the platform instance
   */
  baseUrl: string;
  /**
   * Authentication configuration
   */
  auth: AuthenticationConfig;
  /**
   * Default branch name for repositories
   */
  defaultBranch: string;
  /**
   * Whether this platform is enabled
   */
  enabled: boolean;
  /**
   * Platform priority for auto-detection (higher = more preferred)
   */
  priority: number;
  /**
   * Custom headers to include in all requests
   */
  headers?: Record<string, string>;
  /**
   * Proxy configuration for corporate environments
   */
  proxy?: {
    http: string;
    https: string;
    noProxy?: string[];
  };
}

/**
 * GitHub-specific configuration
 */
export interface GitHubPlatformConfig extends BasePlatformConfig {
  type: PlatformType.GITHUB;
  /**
   * GitHub API version (default: 2022-11-28)
   */
  apiVersion?: string;
  /**
   * GitHub Enterprise Server specific settings
   */
  enterprise?: {
    /**
     * GitHub Enterprise Server version
     */
    version?: string;
    /**
     * Custom API endpoints for Enterprise Server
     */
    apiEndpoints?: {
      graphql: string;
      rest: string;
    };
  };
  /**
   * Pull request template configuration
   */
  pullRequestTemplate?: {
    /**
     * Path to PR template file in repository
     */
    path?: string;
    /**
     * Default PR template content
     */
    content?: string;
    /**
     * Whether to require PR template
     */
    required?: boolean;
  };
  /**
   * Label conventions
   */
  labels?: {
    /**
     * Default labels to apply to new PRs
     */
    defaults?: string[];
    /**
     * Label prefixes for different types
     */
    prefixes?: {
      feature?: string;
      bugfix?: string;
      hotfix?: string;
      docs?: string;
      test?: string;
    };
  };
}

/**
 * GitLab-specific configuration
 */
export interface GitLabPlatformConfig extends BasePlatformConfig {
  type: PlatformType.GITLAB;
  /**
   * GitLab API version (default: v4)
   */
  apiVersion?: string;
  /**
   * GitLab instance specific settings
   */
  instance?: {
    /**
     * GitLab version
     */
    version?: string;
    /**
     * Whether this is a self-hosted instance
     */
    selfHosted?: boolean;
    /**
     * Instance-specific features
     */
    features?: {
      /**
       * Whether merge requests are called pull requests
       */
      pullRequests?: boolean;
      /**
       * Whether epics are available
       */
      epics?: boolean;
      /**
       * Whether groups are available
       */
      groups?: boolean;
    };
  };
  /**
   * Merge request template configuration
   */
  mergeRequestTemplate?: {
    /**
     * Path to MR template file in repository
     */
    path?: string;
    /**
     * Default MR template content
     */
    content?: string;
    /**
     * Whether to require MR template
     */
    required?: boolean;
  };
  /**
   * Label conventions
   */
  labels?: {
    /**
     * Default labels to apply to new MRs
     */
    defaults?: string[];
    /**
     * Label prefixes for different types
     */
    prefixes?: {
      feature?: string;
      bugfix?: string;
      hotfix?: string;
      docs?: string;
      test?: string;
    };
  };
  /**
   * Group/namespace configuration
   */
  namespace?: {
    /**
     * Default group for new projects
     */
    defaultGroup?: string;
    /**
     * Group hierarchy configuration
     */
    hierarchy?: string[];
  };
}

/**
 * Gitea-specific configuration
 */
export interface GiteaPlatformConfig extends BasePlatformConfig {
  type: PlatformType.GITEA;
  /**
   * Gitea API version (default: v1)
   */
  apiVersion?: string;
  /**
   * Gitea instance specific settings
   */
  instance?: {
    /**
     * Gitea version
     */
    version?: string;
    /**
     * Whether this is a self-hosted instance
     */
    selfHosted?: boolean;
    /**
     * Instance-specific features
     */
    features?: {
      /**
       * Whether pull requests are supported
       */
      pullRequests?: boolean;
      /**
       * Whether organizations are available
       */
      organizations?: boolean;
      /**
       * Whether issues are available
       */
      issues?: boolean;
    };
  };
  /**
   * Pull request template configuration
   */
  pullRequestTemplate?: {
    /**
     * Path to PR template file in repository
     */
    path?: string;
    /**
     * Default PR template content
     */
    content?: string;
    /**
     * Whether to require PR template
     */
    required?: boolean;
  };
  /**
   * Label conventions
   */
  labels?: {
    /**
     * Default labels to apply to new PRs
     */
    defaults?: string[];
    /**
     * Label prefixes for different types
     */
    prefixes?: {
      feature?: string;
      bugfix?: string;
      hotfix?: string;
      docs?: string;
      test?: string;
    };
  };
}

/**
 * Forgejo-specific configuration (extends Gitea with Forgejo-specific features)
 */
export interface ForgejoPlatformConfig extends GiteaPlatformConfig {
  type: PlatformType.FORGEJO;
  /**
   * Forgejo-specific settings
   */
  forgejo?: {
    /**
     * Forgejo version
     */
    version?: string;
    /**
     * Forgejo-specific features
     */
    features?: {
      /**
       * Whether actions are available
       */
      actions?: boolean;
      /**
       * Whether packages are available
       */
      packages?: boolean;
    };
  };
}

/**
 * Union type for all platform configurations
 */
export type PlatformConfig =
  | GitHubPlatformConfig
  | GitLabPlatformConfig
  | GiteaPlatformConfig
  | ForgejoPlatformConfig;

/**
 * Complete configuration file structure
 */
export interface GitPlatformConfiguration {
  /**
   * Configuration version for migration purposes
   */
  version: string;
  /**
   * Default platform to use when auto-detection fails
   */
  defaultPlatform?: string;
  /**
   * Array of configured platforms
   */
  platforms: PlatformConfig[];
  /**
   * Global configuration settings
   */
  global?: {
    /**
     * Default timeout for all API requests
     */
    defaultTimeout?: number;
    /**
     * Global rate limiting settings
     */
    rateLimit?: {
      requestsPerSecond: number;
      burstSize: number;
    };
    /**
     * Global proxy configuration
     */
    proxy?: {
      http: string;
      https: string;
      noProxy?: string[];
    };
    /**
     * Logging configuration
     */
    logging?: {
      level: 'debug' | 'info' | 'warn' | 'error';
      format: 'json' | 'text';
    };
  };
}
```

### 1.2 Configuration Validation Schemas

```typescript
// packages/platforms/src/config/validation.ts

import { JSONSchema7 } from 'json-schema';
import {
  GitPlatformConfiguration,
  PlatformConfig,
  AuthenticationConfig,
  AuthenticationMethod,
  PlatformType,
} from './types';

/**
 * JSON Schema for platform configuration validation
 */
export const platformConfigSchema: JSONSchema7 = {
  $schema: 'http://json-schema.org/draft-07/schema#',
  type: 'object',
  required: ['version', 'platforms'],
  properties: {
    version: {
      type: 'string',
      pattern: '^\\d+\\.\\d+\\.\\d+$',
      description: 'Configuration version in semantic version format',
    },
    defaultPlatform: {
      type: 'string',
      description: 'Default platform to use when auto-detection fails',
    },
    platforms: {
      type: 'array',
      minItems: 1,
      items: {
        type: 'object',
        required: ['type', 'name', 'baseUrl', 'auth', 'defaultBranch', 'enabled', 'priority'],
        properties: {
          type: {
            type: 'string',
            enum: Object.values(PlatformType),
            description: 'Platform type identifier',
          },
          name: {
            type: 'string',
            minLength: 1,
            description: 'Human-readable platform name',
          },
          baseUrl: {
            type: 'string',
            format: 'uri',
            description: 'Base URL for the platform instance',
          },
          auth: {
            oneOf: [
              { $ref: '#/definitions/patAuth' },
              { $ref: '#/definitions/oauth2Auth' },
              { $ref: '#/definitions/appAuth' },
              { $ref: '#/definitions/sshKeyAuth' },
            ],
          },
          defaultBranch: {
            type: 'string',
            pattern: '^[a-zA-Z0-9/_-]+$',
            description: 'Default branch name for repositories',
          },
          enabled: {
            type: 'boolean',
            description: 'Whether this platform is enabled',
          },
          priority: {
            type: 'integer',
            minimum: 0,
            maximum: 100,
            description: 'Platform priority for auto-detection',
          },
          headers: {
            type: 'object',
            patternProperties: {
              '^[a-zA-Z0-9-]+$': {
                type: 'string',
              },
            },
            additionalProperties: false,
          },
          proxy: {
            type: 'object',
            properties: {
              http: { type: 'string', format: 'uri' },
              https: { type: 'string', format: 'uri' },
              noProxy: {
                type: 'array',
                items: { type: 'string' },
              },
            },
            required: ['http', 'https'],
          },
        },
        oneOf: [
          { $ref: '#/definitions/githubConfig' },
          { $ref: '#/definitions/gitlabConfig' },
          { $ref: '#/definitions/giteaConfig' },
          { $ref: '#/definitions/forgejoConfig' },
        ],
      },
    },
    global: {
      type: 'object',
      properties: {
        defaultTimeout: {
          type: 'integer',
          minimum: 1000,
          maximum: 300000,
          description: 'Default timeout in milliseconds',
        },
        rateLimit: {
          type: 'object',
          properties: {
            requestsPerSecond: {
              type: 'number',
              minimum: 0.1,
              maximum: 1000,
            },
            burstSize: {
              type: 'integer',
              minimum: 1,
              maximum: 1000,
            },
          },
          required: ['requestsPerSecond', 'burstSize'],
        },
        proxy: {
          type: 'object',
          properties: {
            http: { type: 'string', format: 'uri' },
            https: { type: 'string', format: 'uri' },
            noProxy: {
              type: 'array',
              items: { type: 'string' },
            },
          },
          required: ['http', 'https'],
        },
        logging: {
          type: 'object',
          properties: {
            level: {
              type: 'string',
              enum: ['debug', 'info', 'warn', 'error'],
            },
            format: {
              type: 'string',
              enum: ['json', 'text'],
            },
          },
          required: ['level', 'format'],
        },
      },
    },
  },
  definitions: {
    patAuth: {
      type: 'object',
      required: ['method'],
      properties: {
        method: { type: 'string', const: AuthenticationMethod.PERSONAL_ACCESS_TOKEN },
        token: { type: 'string' },
        tokenEnvVar: { type: 'string' },
        scopes: {
          type: 'array',
          items: { type: 'string' },
        },
        expiresAt: { type: 'string', format: 'date-time' },
        headers: { type: 'object' },
        timeout: { type: 'integer', minimum: 1000 },
        rateLimit: { $ref: '#/definitions/rateLimit' },
      },
    },
    oauth2Auth: {
      type: 'object',
      required: ['method', 'clientId', 'redirectUri', 'scopes'],
      properties: {
        method: { type: 'string', const: AuthenticationMethod.OAUTH2 },
        clientId: { type: 'string', minLength: 1 },
        clientSecret: { type: 'string' },
        clientSecretEnvVar: { type: 'string' },
        redirectUri: { type: 'string', format: 'uri' },
        scopes: {
          type: 'array',
          items: { type: 'string' },
          minItems: 1,
        },
        tokenEndpoint: { type: 'string', format: 'uri' },
        authorizationEndpoint: { type: 'string', format: 'uri' },
        headers: { type: 'object' },
        timeout: { type: 'integer', minimum: 1000 },
        rateLimit: { $ref: '#/definitions/rateLimit' },
      },
    },
    appAuth: {
      type: 'object',
      required: ['method', 'appId'],
      properties: {
        method: { type: 'string', const: AuthenticationMethod.APP },
        appId: { type: 'string', minLength: 1 },
        privateKey: { type: 'string' },
        privateKeyEnvVar: { type: 'string' },
        installationId: { type: 'string' },
        webhookSecret: { type: 'string' },
        webhookSecretEnvVar: { type: 'string' },
        headers: { type: 'object' },
        timeout: { type: 'integer', minimum: 1000 },
        rateLimit: { $ref: '#/definitions/rateLimit' },
      },
    },
    sshKeyAuth: {
      type: 'object',
      required: ['method'],
      properties: {
        method: { type: 'string', const: AuthenticationMethod.SSH_KEY },
        privateKey: { type: 'string' },
        privateKeyEnvVar: { type: 'string' },
        passphrase: { type: 'string' },
        passphraseEnvVar: { type: 'string' },
        keyType: {
          type: 'string',
          enum: ['rsa', 'ed25519', 'ecdsa'],
        },
        headers: { type: 'object' },
        timeout: { type: 'integer', minimum: 1000 },
        rateLimit: { $ref: '#/definitions/rateLimit' },
      },
    },
    rateLimit: {
      type: 'object',
      properties: {
        requestsPerSecond: {
          type: 'number',
          minimum: 0.1,
          maximum: 1000,
        },
        burstSize: {
          type: 'integer',
          minimum: 1,
          maximum: 1000,
        },
      },
      required: ['requestsPerSecond', 'burstSize'],
    },
    githubConfig: {
      type: 'object',
      properties: {
        type: { const: PlatformType.GITHUB },
        apiVersion: { type: 'string' },
        enterprise: {
          type: 'object',
          properties: {
            version: { type: 'string' },
            apiEndpoints: {
              type: 'object',
              properties: {
                graphql: { type: 'string', format: 'uri' },
                rest: { type: 'string', format: 'uri' },
              },
            },
          },
        },
        pullRequestTemplate: {
          type: 'object',
          properties: {
            path: { type: 'string' },
            content: { type: 'string' },
            required: { type: 'boolean' },
          },
        },
        labels: {
          type: 'object',
          properties: {
            defaults: {
              type: 'array',
              items: { type: 'string' },
            },
            prefixes: {
              type: 'object',
              properties: {
                feature: { type: 'string' },
                bugfix: { type: 'string' },
                hotfix: { type: 'string' },
                docs: { type: 'string' },
                test: { type: 'string' },
              },
            },
          },
        },
      },
    },
    gitlabConfig: {
      type: 'object',
      properties: {
        type: { const: PlatformType.GITLAB },
        apiVersion: { type: 'string' },
        instance: {
          type: 'object',
          properties: {
            version: { type: 'string' },
            selfHosted: { type: 'boolean' },
            features: {
              type: 'object',
              properties: {
                pullRequests: { type: 'boolean' },
                epics: { type: 'boolean' },
                groups: { type: 'boolean' },
              },
            },
          },
        },
        mergeRequestTemplate: {
          type: 'object',
          properties: {
            path: { type: 'string' },
            content: { type: 'string' },
            required: { type: 'boolean' },
          },
        },
        labels: {
          type: 'object',
          properties: {
            defaults: {
              type: 'array',
              items: { type: 'string' },
            },
            prefixes: {
              type: 'object',
              properties: {
                feature: { type: 'string' },
                bugfix: { type: 'string' },
                hotfix: { type: 'string' },
                docs: { type: 'string' },
                test: { type: 'string' },
              },
            },
          },
        },
        namespace: {
          type: 'object',
          properties: {
            defaultGroup: { type: 'string' },
            hierarchy: {
              type: 'array',
              items: { type: 'string' },
            },
          },
        },
      },
    },
    giteaConfig: {
      type: 'object',
      properties: {
        type: { const: PlatformType.GITEA },
        apiVersion: { type: 'string' },
        instance: {
          type: 'object',
          properties: {
            version: { type: 'string' },
            selfHosted: { type: 'boolean' },
            features: {
              type: 'object',
              properties: {
                pullRequests: { type: 'boolean' },
                organizations: { type: 'boolean' },
                issues: { type: 'boolean' },
              },
            },
          },
        },
        pullRequestTemplate: {
          type: 'object',
          properties: {
            path: { type: 'string' },
            content: { type: 'string' },
            required: { type: 'boolean' },
          },
        },
        labels: {
          type: 'object',
          properties: {
            defaults: {
              type: 'array',
              items: { type: 'string' },
            },
            prefixes: {
              type: 'object',
              properties: {
                feature: { type: 'string' },
                bugfix: { type: 'string' },
                hotfix: { type: 'string' },
                docs: { type: 'string' },
                test: { type: 'string' },
              },
            },
          },
        },
      },
    },
    forgejoConfig: {
      type: 'object',
      properties: {
        type: { const: PlatformType.FORGEJO },
        forgejo: {
          type: 'object',
          properties: {
            version: { type: 'string' },
            features: {
              type: 'object',
              properties: {
                actions: { type: 'boolean' },
                packages: { type: 'boolean' },
              },
            },
          },
        },
      },
      allOf: [{ $ref: '#/definitions/giteaConfig' }],
    },
  },
};

/**
 * Custom validation functions for complex business logic
 */
export class ConfigurationValidator {
  /**
   * Validate authentication configuration based on method
   */
  static validateAuthentication(auth: AuthenticationConfig): string[] {
    const errors: string[] = [];

    switch (auth.method) {
      case AuthenticationMethod.OAUTH2:
        if (!auth.clientId) {
          errors.push('OAuth2 authentication requires clientId');
        }
        if (!auth.clientSecret && !auth.clientSecretEnvVar) {
          errors.push('OAuth2 authentication requires clientSecret or clientSecretEnvVar');
        }
        if (!auth.scopes || auth.scopes.length === 0) {
          errors.push('OAuth2 authentication requires at least one scope');
        }
        break;

      case AuthenticationMethod.APP:
        if (!auth.appId) {
          errors.push('App authentication requires appId');
        }
        if (!auth.privateKey && !auth.privateKeyEnvVar) {
          errors.push('App authentication requires privateKey or privateKeyEnvVar');
        }
        break;

      case AuthenticationMethod.SSH_KEY:
        if (!auth.privateKey && !auth.privateKeyEnvVar) {
          errors.push('SSH key authentication requires privateKey or privateKeyEnvVar');
        }
        break;
    }

    return errors;
  }

  /**
   * Validate platform-specific configuration
   */
  static validatePlatform(platform: PlatformConfig): string[] {
    const errors: string[] = [];

    // Base validation
    if (!platform.name || platform.name.trim().length === 0) {
      errors.push('Platform name is required');
    }

    if (!platform.baseUrl || !this.isValidUrl(platform.baseUrl)) {
      errors.push('Platform baseUrl must be a valid URL');
    }

    if (!platform.defaultBranch || !/^[a-zA-Z0-9/_-]+$/.test(platform.defaultBranch)) {
      errors.push(
        'Default branch must contain only alphanumeric characters, underscores, hyphens, and forward slashes'
      );
    }

    if (platform.priority < 0 || platform.priority > 100) {
      errors.push('Platform priority must be between 0 and 100');
    }

    // Authentication validation
    errors.push(...this.validateAuthentication(platform.auth));

    // Platform-specific validation
    switch (platform.type) {
      case PlatformType.GITHUB:
        errors.push(...this.validateGitHubConfig(platform as GitHubPlatformConfig));
        break;
      case PlatformType.GITLAB:
        errors.push(...this.validateGitLabConfig(platform as GitLabPlatformConfig));
        break;
      case PlatformType.GITEA:
        errors.push(...this.validateGiteaConfig(platform as GiteaPlatformConfig));
        break;
      case PlatformType.FORGEJO:
        errors.push(...this.validateForgejoConfig(platform as ForgejoPlatformConfig));
        break;
    }

    return errors;
  }

  /**
   * Validate complete configuration
   */
  static validateConfiguration(config: GitPlatformConfiguration): string[] {
    const errors: string[] = [];

    if (!config.version || !/^\d+\.\d+\.\d+$/.test(config.version)) {
      errors.push('Configuration version must be in semantic version format (x.y.z)');
    }

    if (!config.platforms || config.platforms.length === 0) {
      errors.push('At least one platform must be configured');
    }

    // Validate each platform
    config.platforms?.forEach((platform, index) => {
      const platformErrors = this.validatePlatform(platform);
      platformErrors.forEach((error) => {
        errors.push(`Platform ${index + 1}: ${error}`);
      });
    });

    // Check for duplicate platform names
    const platformNames = config.platforms?.map((p) => p.name) || [];
    const duplicates = platformNames.filter((name, index) => platformNames.indexOf(name) !== index);
    if (duplicates.length > 0) {
      errors.push(`Duplicate platform names found: ${duplicates.join(', ')}`);
    }

    // Check for duplicate base URLs
    const baseUrls = config.platforms?.map((p) => p.baseUrl) || [];
    const duplicateUrls = baseUrls.filter((url, index) => baseUrls.indexOf(url) !== index);
    if (duplicateUrls.length > 0) {
      errors.push(`Duplicate base URLs found: ${duplicateUrls.join(', ')}`);
    }

    // Validate default platform
    if (config.defaultPlatform) {
      const defaultExists = config.platforms?.some((p) => p.name === config.defaultPlatform);
      if (!defaultExists) {
        errors.push(`Default platform '${config.defaultPlatform}' is not configured`);
      }
    }

    return errors;
  }

  private static isValidUrl(url: string): boolean {
    try {
      new URL(url);
      return true;
    } catch {
      return false;
    }
  }

  private static validateGitHubConfig(config: GitHubPlatformConfig): string[] {
    const errors: string[] = [];

    if (config.enterprise) {
      if (config.enterprise.apiEndpoints) {
        if (!config.enterprise.apiEndpoints.graphql) {
          errors.push('GitHub Enterprise configuration requires graphql endpoint');
        }
        if (!config.enterprise.apiEndpoints.rest) {
          errors.push('GitHub Enterprise configuration requires rest endpoint');
        }
      }
    }

    return errors;
  }

  private static validateGitLabConfig(config: GitLabPlatformConfig): string[] {
    const errors: string[] = [];

    // GitLab-specific validation
    if (config.namespace?.defaultGroup && !config.namespace.hierarchy) {
      errors.push('GitLab namespace defaultGroup requires hierarchy configuration');
    }

    return errors;
  }

  private static validateGiteaConfig(config: GiteaPlatformConfig): string[] {
    const errors: string[] = [];

    // Gitea-specific validation
    return errors;
  }

  private static validateForgejoConfig(config: ForgejoPlatformConfig): string[] {
    const errors: string[] = [];

    // Forgejo-specific validation (extends Gitea)
    return errors;
  }
}
```

### 1.3 Platform Detection Logic

```typescript
// packages/platforms/src/config/detection.ts

import { PlatformType, PlatformConfig } from './types';

/**
 * Platform detection patterns and utilities
 */
export class PlatformDetector {
  /**
   * URL patterns for platform detection
   */
  private static readonly PLATFORM_PATTERNS = {
    [PlatformType.GITHUB]: [
      /^https?:\/\/github\.com\//,
      /^https?:\/\/api\.github\.com\//,
      /^https?:\/\/[^\/]+\.github\.com\//, // GitHub Enterprise
    ],
    [PlatformType.GITLAB]: [
      /^https?:\/\/gitlab\.com\//,
      /^https?:\/\/[^\/]+\.gitlab\.com\//, // GitLab dedicated
      /^https?:\/\/gitlab\./, // Self-hosted GitLab
      /^https?:\/\/[^\/]*gitlab[^\/]*\//, // Generic GitLab instances
    ],
    [PlatformType.GITEA]: [
      /^https?:\/\/[^\/]*gitea[^\/]*\//,
      /^https?:\/\/code\.gitea\.io\//,
      /^https?:\/\/try\.gitea\.io\//,
    ],
    [PlatformType.FORGEJO]: [
      /^https?:\/\/[^\/]*forgejo[^\/]*\//,
      /^https?:\/\/code\.forgejo\.org\//,
    ],
  };

  /**
   * Default base URLs for each platform
   */
  private static readonly DEFAULT_BASE_URLS = {
    [PlatformType.GITHUB]: 'https://github.com',
    [PlatformType.GITLAB]: 'https://gitlab.com',
    [PlatformType.GITEA]: 'https://code.gitea.io',
    [PlatformType.FORGEJO]: 'https://code.forgejo.org',
  };

  /**
   * Detect platform type from repository URL
   */
  static detectPlatformFromUrl(url: string): PlatformType | null {
    try {
      const normalizedUrl = url.toLowerCase().trim();

      for (const [platform, patterns] of Object.entries(this.PLATFORM_PATTERNS)) {
        for (const pattern of patterns) {
          if (pattern.test(normalizedUrl)) {
            return platform as PlatformType;
          }
        }
      }

      return null;
    } catch {
      return null;
    }
  }

  /**
   * Extract base URL from repository URL
   */
  static extractBaseUrl(url: string): string | null {
    try {
      const urlObj = new URL(url);

      // Remove path components to get base URL
      const baseUrl = `${urlObj.protocol}//${urlObj.host}`;
      return baseUrl;
    } catch {
      return null;
    }
  }

  /**
   * Extract repository path from URL
   */
  static extractRepositoryPath(url: string): string | null {
    try {
      const urlObj = new URL(url);
      let pathname = urlObj.pathname;

      // Remove leading slash and .git suffix
      pathname = pathname.replace(/^\//, '').replace(/\.git$/, '');

      // For GitHub/GitLab API URLs, extract from query parameters
      if (pathname.includes('/api/')) {
        const searchParams = new URLSearchParams(urlObj.search);
        const repoPath = searchParams.get('repo') || searchParams.get('repository');
        if (repoPath) {
          return repoPath;
        }
      }

      return pathname;
    } catch {
      return null;
    }
  }

  /**
   * Normalize repository URL to standard format
   */
  static normalizeRepositoryUrl(url: string, platformType: PlatformType): string {
    try {
      const urlObj = new URL(url);
      const baseUrl = this.DEFAULT_BASE_URLS[platformType];

      // Extract repository path
      const repoPath = this.extractRepositoryPath(url);
      if (!repoPath) {
        throw new Error('Could not extract repository path from URL');
      }

      // Construct normalized URL
      return `${baseUrl}/${repoPath}`;
    } catch {
      throw new Error(`Invalid repository URL: ${url}`);
    }
  }

  /**
   * Find best matching platform configuration for a URL
   */
  static findBestPlatformMatch(url: string, platforms: PlatformConfig[]): PlatformConfig | null {
    const detectedType = this.detectPlatformFromUrl(url);
    const baseUrl = this.extractBaseUrl(url);

    // First, try to find exact base URL match
    const exactMatch = platforms.find(
      (platform) => platform.baseUrl === baseUrl && platform.enabled
    );

    if (exactMatch) {
      return exactMatch;
    }

    // Next, try to find platform type match
    if (detectedType) {
      const typeMatches = platforms.filter(
        (platform) => platform.type === detectedType && platform.enabled
      );

      if (typeMatches.length > 0) {
        // Return the highest priority match
        return typeMatches.reduce((best, current) =>
          current.priority > best.priority ? current : best
        );
      }
    }

    // Finally, return the default platform if configured
    const defaultPlatform = platforms.find(
      (platform) =>
        platform.enabled && platform.name === platforms.find((p) => p.name === platform.name)?.name
    );

    return defaultPlatform || null;
  }

  /**
   * Validate platform URL format
   */
  static validatePlatformUrl(url: string, platformType: PlatformType): boolean {
    const patterns = this.PLATFORM_PATTERNS[platformType];
    if (!patterns) {
      return false;
    }

    return patterns.some((pattern) => pattern.test(url.toLowerCase()));
  }

  /**
   * Get platform-specific API endpoints
   */
  static getApiEndpoints(
    platformType: PlatformType,
    baseUrl: string
  ): {
    rest: string;
    graphql?: string;
    v3?: string; // For GitHub API v3
    v4?: string; // For GitLab API v4
  } {
    const baseEndpoints = {
      [PlatformType.GITHUB]: {
        rest: `${baseUrl.replace('github.com', 'api.github.com')}/api/v4`,
        graphql: `${baseUrl.replace('github.com', 'api.github.com')}/graphql`,
        v3: `${baseUrl.replace('github.com', 'api.github.com')}/api/v3`,
      },
      [PlatformType.GITLAB]: {
        rest: `${baseUrl}/api/v4`,
        v4: `${baseUrl}/api/v4`,
      },
      [PlatformType.GITEA]: {
        rest: `${baseUrl}/api/v1`,
        v1: `${baseUrl}/api/v1`,
      },
      [PlatformType.FORGEJO]: {
        rest: `${baseUrl}/api/v1`,
        v1: `${baseUrl}/api/v1`,
      },
    };

    return baseEndpoints[platformType] || { rest: baseUrl };
  }

  /**
   * Check if URL is from a self-hosted instance
   */
  static isSelfHosted(url: string, platformType: PlatformType): boolean {
    const defaultUrl = this.DEFAULT_BASE_URLS[platformType];
    const baseUrl = this.extractBaseUrl(url);

    return baseUrl !== defaultUrl;
  }

  /**
   * Get platform type from configuration name
   */
  static getPlatformTypeFromName(name: string): PlatformType | null {
    const nameLower = name.toLowerCase();

    if (nameLower.includes('github')) {
      return PlatformType.GITHUB;
    }
    if (nameLower.includes('gitlab')) {
      return PlatformType.GITLAB;
    }
    if (nameLower.includes('gitea')) {
      return PlatformType.GITEA;
    }
    if (nameLower.includes('forgejo')) {
      return PlatformType.FORGEJO;
    }

    return null;
  }
}
```

## Testing Strategy

### 1.1 Unit Tests for Configuration Types

```typescript
// packages/platforms/src/config/types.test.ts

import {
  PlatformType,
  AuthenticationMethod,
  GitHubPlatformConfig,
  GitLabPlatformConfig,
  GiteaPlatformConfig,
  ForgejoPlatformConfig,
} from './types';

describe('Configuration Types', () => {
  describe('PlatformType Enum', () => {
    it('should have all expected platform types', () => {
      expect(PlatformType.GITHUB).toBe('github');
      expect(PlatformType.GITLAB).toBe('gitlab');
      expect(PlatformType.GITEA).toBe('gitea');
      expect(PlatformType.FORGEJO).toBe('forgejo');
    });
  });

  describe('AuthenticationMethod Enum', () => {
    it('should have all expected authentication methods', () => {
      expect(AuthenticationMethod.PERSONAL_ACCESS_TOKEN).toBe('pat');
      expect(AuthenticationMethod.OAUTH2).toBe('oauth2');
      expect(AuthenticationMethod.APP).toBe('app');
      expect(AuthenticationMethod.SSH_KEY).toBe('ssh_key');
    });
  });

  describe('Platform Configuration Interfaces', () => {
    it('should validate GitHub configuration structure', () => {
      const config: GitHubPlatformConfig = {
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
        apiVersion: '2022-11-28',
      };

      expect(config.type).toBe(PlatformType.GITHUB);
      expect(config.auth.method).toBe(AuthenticationMethod.PERSONAL_ACCESS_TOKEN);
    });

    it('should validate GitLab configuration structure', () => {
      const config: GitLabPlatformConfig = {
        type: PlatformType.GITLAB,
        name: 'GitLab',
        baseUrl: 'https://gitlab.com',
        auth: {
          method: AuthenticationMethod.OAUTH2,
          clientId: 'test-client-id',
          redirectUri: 'https://example.com/callback',
          scopes: ['read_api', 'read_repository'],
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 90,
        apiVersion: 'v4',
      };

      expect(config.type).toBe(PlatformType.GITLAB);
      expect(config.auth.method).toBe(AuthenticationMethod.OAUTH2);
    });
  });
});
```

### 1.2 Unit Tests for Configuration Validation

```typescript
// packages/platforms/src/config/validation.test.ts

import { ConfigurationValidator } from './validation';
import { GitPlatformConfiguration, PlatformType, AuthenticationMethod } from './types';

describe('ConfigurationValidator', () => {
  describe('validateAuthentication', () => {
    it('should validate PAT authentication', () => {
      const auth = {
        method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
        tokenEnvVar: 'TEST_TOKEN',
      };

      const errors = ConfigurationValidator.validateAuthentication(auth);
      expect(errors).toHaveLength(0);
    });

    it('should require OAuth2 fields', () => {
      const auth = {
        method: AuthenticationMethod.OAUTH2,
        clientId: '',
        scopes: [],
      };

      const errors = ConfigurationValidator.validateAuthentication(auth);
      expect(errors).toContain('OAuth2 authentication requires clientId');
      expect(errors).toContain('OAuth2 authentication requires at least one scope');
    });

    it('should require App authentication fields', () => {
      const auth = {
        method: AuthenticationMethod.APP,
        appId: '',
      };

      const errors = ConfigurationValidator.validateAuthentication(auth);
      expect(errors).toContain('App authentication requires appId');
    });
  });

  describe('validateConfiguration', () => {
    it('should validate complete configuration', () => {
      const config: GitPlatformConfiguration = {
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

      const errors = ConfigurationValidator.validateConfiguration(config);
      expect(errors).toHaveLength(0);
    });

    it('should detect missing version', () => {
      const config = {
        platforms: [],
      } as GitPlatformConfiguration;

      const errors = ConfigurationValidator.validateConfiguration(config);
      expect(errors).toContain('Configuration version must be in semantic version format (x.y.z)');
    });

    it('should detect no platforms configured', () => {
      const config: GitPlatformConfiguration = {
        version: '1.0.0',
        platforms: [],
      };

      const errors = ConfigurationValidator.validateConfiguration(config);
      expect(errors).toContain('At least one platform must be configured');
    });
  });
});
```

### 1.3 Unit Tests for Platform Detection

```typescript
// packages/platforms/src/config/detection.test.ts

import { PlatformDetector } from './detection';
import { PlatformType } from './types';

describe('PlatformDetector', () => {
  describe('detectPlatformFromUrl', () => {
    it('should detect GitHub URLs', () => {
      expect(PlatformDetector.detectPlatformFromUrl('https://github.com/user/repo')).toBe(
        PlatformType.GITHUB
      );
      expect(PlatformDetector.detectPlatformFromUrl('https://api.github.com/repos/user/repo')).toBe(
        PlatformType.GITHUB
      );
      expect(
        PlatformDetector.detectPlatformFromUrl('https://enterprise.github.com/user/repo')
      ).toBe(PlatformType.GITHUB);
    });

    it('should detect GitLab URLs', () => {
      expect(PlatformDetector.detectPlatformFromUrl('https://gitlab.com/user/repo')).toBe(
        PlatformType.GITLAB
      );
      expect(PlatformDetector.detectPlatformFromUrl('https://gitlab.example.com/user/repo')).toBe(
        PlatformType.GITLAB
      );
    });

    it('should detect Gitea URLs', () => {
      expect(PlatformDetector.detectPlatformFromUrl('https://code.gitea.io/user/repo')).toBe(
        PlatformType.GITEA
      );
      expect(PlatformDetector.detectPlatformFromUrl('https://gitea.example.com/user/repo')).toBe(
        PlatformType.GITEA
      );
    });

    it('should return null for unknown URLs', () => {
      expect(PlatformDetector.detectPlatformFromUrl('https://example.com/user/repo')).toBeNull();
    });
  });

  describe('extractBaseUrl', () => {
    it('should extract base URL correctly', () => {
      expect(PlatformDetector.extractBaseUrl('https://github.com/user/repo')).toBe(
        'https://github.com'
      );
      expect(PlatformDetector.extractBaseUrl('https://gitlab.example.com/group/project')).toBe(
        'https://gitlab.example.com'
      );
    });
  });

  describe('extractRepositoryPath', () => {
    it('should extract repository path correctly', () => {
      expect(PlatformDetector.extractRepositoryPath('https://github.com/user/repo')).toBe(
        'user/repo'
      );
      expect(PlatformDetector.extractRepositoryPath('https://gitlab.com/group/project')).toBe(
        'group/project'
      );
    });

    it('should handle .git suffix', () => {
      expect(PlatformDetector.extractRepositoryPath('https://github.com/user/repo.git')).toBe(
        'user/repo'
      );
    });
  });

  describe('findBestPlatformMatch', () => {
    it('should find exact base URL match', () => {
      const platforms = [
        {
          type: PlatformType.GITHUB,
          name: 'GitHub',
          baseUrl: 'https://github.com',
          auth: { method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN },
          defaultBranch: 'main',
          enabled: true,
          priority: 100,
        },
      ];

      const match = PlatformDetector.findBestPlatformMatch(
        'https://github.com/user/repo',
        platforms
      );

      expect(match).toBe(platforms[0]);
    });

    it('should find platform type match', () => {
      const platforms = [
        {
          type: PlatformType.GITHUB,
          name: 'GitHub Enterprise',
          baseUrl: 'https://enterprise.github.com',
          auth: { method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN },
          defaultBranch: 'main',
          enabled: true,
          priority: 90,
        },
      ];

      const match = PlatformDetector.findBestPlatformMatch(
        'https://github.com/user/repo',
        platforms
      );

      expect(match).toBe(platforms[0]);
    });
  });
});
```

## Completion Checklist

- [ ] Implement all configuration interfaces with TypeScript strict mode
- [ ] Create comprehensive JSON Schema validation
- [ ] Implement custom validation functions for business logic
- [ ] Create platform detection logic with URL pattern matching
- [ ] Add comprehensive unit tests for all validation functions
- [ ] Add unit tests for platform detection logic
- [ ] Verify TypeScript compilation with strict mode
- [ ] Ensure all interfaces are properly documented
- [ ] Validate configuration inheritance and default value patterns
- [ ] Test platform-specific configuration validation

## Dependencies

- Story 1.3: Provider Configuration Management (for configuration patterns)
- Story 1.4-1.6: Platform Implementations (for platform-specific requirements)
- JSON Schema validation library
- TypeScript strict mode configuration

## Estimated Time

**Interface Design**: 2-3 days
**Validation Implementation**: 2-3 days
**Platform Detection Logic**: 1-2 days
**Unit Tests**: 2-3 days
**Documentation**: 1 day
**Total**: 8-12 days
