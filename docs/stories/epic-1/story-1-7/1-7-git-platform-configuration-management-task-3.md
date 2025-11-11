# Story 1.7 Task 3: Implement Platform Registry and Selection

## Task Overview

Implement the PlatformRegistry for platform registration, platform detection from repository URLs, default branch configuration, PR template path configuration, and label conventions configuration. This task provides the core platform management and selection logic for the Tamma system.

## Acceptance Criteria

### 3.1 PlatformRegistry for Platform Registration

- [ ] Create PlatformRegistry class with platform registration and management
- [ ] Implement dynamic platform registration and deregistration
- [ ] Add platform capability discovery and validation
- [ ] Implement platform dependency management and resolution
- [ ] Add platform health monitoring and status tracking

### 3.2 Platform Detection from Repository URLs

- [ ] Implement intelligent platform detection from various URL formats
- [ ] Support custom platform URL patterns and rules
- [ ] Add platform priority-based selection logic
- [ ] Implement fallback and default platform selection
- [ ] Add platform detection caching and performance optimization

### 3.3 Default Branch Configuration per Platform

- [ ] Implement default branch configuration with platform-specific defaults
- [ ] Support repository-specific branch override detection
- [ ] Add branch name validation and normalization
- [ ] Implement branch protection rule integration
- [ ] Add branch migration and renaming support

### 3.4 PR Template Path Configuration

- [ ] Implement PR/MR template path configuration per platform
- [ ] Support template discovery and validation
- [ ] Add template variable substitution and rendering
- [ ] Implement template inheritance and override logic
- [ ] Add template versioning and update management

### 3.5 Label Conventions Configuration

- [ ] Implement label convention configuration per platform
- [ ] Support label prefix and suffix rules
- [ ] Add label validation and synchronization
- [ ] Implement label color and description management
- [ ] Add label migration and cleanup utilities

## Implementation Details

### 3.1 PlatformRegistry Core Implementation

```typescript
// packages/platforms/src/config/platform-registry.ts

import {
  PlatformConfig,
  PlatformType,
  AuthenticationMethod,
  GitHubPlatformConfig,
  GitLabPlatformConfig,
  GiteaPlatformConfig,
  ForgejoPlatformConfig,
} from './types';
import { PlatformDetector } from './detection';
import { EventEmitter } from 'events';
import { Logger } from '@tamma/shared/logging';

/**
 * Platform capability interface
 */
export interface PlatformCapability {
  name: string;
  version: string;
  description: string;
  supported: boolean;
  requiredFeatures?: string[];
}

/**
 * Platform registration information
 */
export interface PlatformRegistration {
  config: PlatformConfig;
  capabilities: PlatformCapability[];
  registeredAt: Date;
  lastHealthCheck?: Date;
  healthStatus?: 'healthy' | 'degraded' | 'unhealthy';
  metadata?: Record<string, any>;
}

/**
 * Platform selection criteria
 */
export interface PlatformSelectionCriteria {
  repositoryUrl?: string;
  platformType?: PlatformType;
  priority?: number;
  capabilities?: string[];
  healthStatus?: 'healthy' | 'degraded' | 'unhealthy';
  excludeUnhealthy?: boolean;
}

/**
 * Platform registry for managing multiple Git platforms
 */
export class PlatformRegistry extends EventEmitter {
  private platforms: Map<string, PlatformRegistration> = new Map();
  private platformByType: Map<PlatformType, PlatformRegistration[]> = new Map();
  private platformByUrl: Map<string, PlatformRegistration> = new Map();
  private detectionCache: Map<string, PlatformRegistration | null> = new Map();
  private logger: Logger;

  constructor(logger: Logger) {
    super();
    this.logger = logger.child({ component: 'PlatformRegistry' });
  }

  /**
   * Register a platform configuration
   */
  async registerPlatform(config: PlatformConfig): Promise<void> {
    try {
      this.logger.info('Registering platform', {
        name: config.name,
        type: config.type,
        baseUrl: config.baseUrl,
      });

      // Validate platform configuration
      this.validatePlatformConfig(config);

      // Discover platform capabilities
      const capabilities = await this.discoverCapabilities(config);

      // Create registration
      const registration: PlatformRegistration = {
        config,
        capabilities,
        registeredAt: new Date(),
      };

      // Check for conflicts
      await this.checkForConflicts(registration);

      // Register platform
      this.platforms.set(config.name, registration);

      // Update type-based index
      if (!this.platformByType.has(config.type)) {
        this.platformByType.set(config.type, []);
      }
      this.platformByType.get(config.type)!.push(registration);

      // Update URL-based index
      this.platformByUrl.set(config.baseUrl, registration);

      // Clear detection cache
      this.detectionCache.clear();

      this.logger.info('Platform registered successfully', {
        name: config.name,
        capabilities: capabilities.length,
      });

      this.emit('platformRegistered', registration);
    } catch (error) {
      this.logger.error('Failed to register platform', {
        name: config.name,
        error: error.message,
      });
      throw error;
    }
  }

  /**
   * Unregister a platform
   */
  async unregisterPlatform(name: string): Promise<void> {
    const registration = this.platforms.get(name);
    if (!registration) {
      throw new Error(`Platform '${name}' is not registered`);
    }

    this.logger.info('Unregistering platform', { name });

    // Remove from all indexes
    this.platforms.delete(name);

    const typePlatforms = this.platformByType.get(registration.config.type);
    if (typePlatforms) {
      const index = typePlatforms.findIndex((p) => p.config.name === name);
      if (index >= 0) {
        typePlatforms.splice(index, 1);
      }
      if (typePlatforms.length === 0) {
        this.platformByType.delete(registration.config.type);
      }
    }

    this.platformByUrl.delete(registration.config.baseUrl);

    // Clear detection cache
    this.detectionCache.clear();

    this.logger.info('Platform unregistered successfully', { name });
    this.emit('platformUnregistered', registration);
  }

  /**
   * Get platform registration by name
   */
  getPlatform(name: string): PlatformRegistration | null {
    return this.platforms.get(name) || null;
  }

  /**
   * Get all registered platforms
   */
  getAllPlatforms(): PlatformRegistration[] {
    return Array.from(this.platforms.values());
  }

  /**
   * Get platforms by type
   */
  getPlatformsByType(type: PlatformType): PlatformRegistration[] {
    return this.platformByType.get(type) || [];
  }

  /**
   * Get enabled platforms
   */
  getEnabledPlatforms(): PlatformRegistration[] {
    return this.getAllPlatforms().filter((reg) => reg.config.enabled);
  }

  /**
   * Select best platform based on criteria
   */
  async selectPlatform(criteria: PlatformSelectionCriteria): Promise<PlatformRegistration | null> {
    this.logger.debug('Selecting platform', { criteria });

    let candidates: PlatformRegistration[] = [];

    // Filter by repository URL if provided
    if (criteria.repositoryUrl) {
      const detected = await this.detectPlatformFromUrl(criteria.repositoryUrl);
      if (detected) {
        candidates.push(detected);
      }
    }

    // If no candidates from URL detection, use all enabled platforms
    if (candidates.length === 0) {
      candidates = this.getEnabledPlatforms();
    }

    // Apply filters
    if (criteria.platformType) {
      candidates = candidates.filter((reg) => reg.config.type === criteria.platformType);
    }

    if (criteria.capabilities && criteria.capabilities.length > 0) {
      candidates = candidates.filter((reg) => this.hasCapabilities(reg, criteria.capabilities!));
    }

    if (criteria.excludeUnhealthy) {
      candidates = candidates.filter((reg) => reg.healthStatus !== 'unhealthy');
    }

    if (criteria.healthStatus) {
      candidates = candidates.filter((reg) => reg.healthStatus === criteria.healthStatus);
    }

    // Sort by priority (highest first)
    candidates.sort((a, b) => b.config.priority - a.config.priority);

    const selected = candidates[0] || null;

    this.logger.debug('Platform selection result', {
      selected: selected?.config.name,
      candidatesCount: candidates.length,
    });

    return selected;
  }

  /**
   * Detect platform from repository URL
   */
  async detectPlatformFromUrl(url: string): Promise<PlatformRegistration | null> {
    // Check cache first
    if (this.detectionCache.has(url)) {
      return this.detectionCache.get(url)!;
    }

    this.logger.debug('Detecting platform from URL', { url });

    // Use PlatformDetector for initial detection
    const detectedType = PlatformDetector.detectPlatformFromUrl(url);
    if (!detectedType) {
      this.detectionCache.set(url, null);
      return null;
    }

    // Find matching platform registration
    const platforms = this.getPlatformsByType(detectedType);
    const match = PlatformDetector.findBestPlatformMatch(
      url,
      platforms.map((p) => p.config)
    );

    const registration = match ? this.platforms.get(match.name) || null : null;

    // Cache result
    this.detectionCache.set(url, registration);

    this.logger.debug('Platform detection result', {
      url,
      detectedType,
      selectedPlatform: registration?.config.name,
    });

    return registration;
  }

  /**
   * Update platform health status
   */
  async updateHealthStatus(
    name: string,
    status: 'healthy' | 'degraded' | 'unhealthy'
  ): Promise<void> {
    const registration = this.platforms.get(name);
    if (!registration) {
      throw new Error(`Platform '${name}' is not registered`);
    }

    const previousStatus = registration.healthStatus;
    registration.healthStatus = status;
    registration.lastHealthCheck = new Date();

    this.logger.info('Platform health status updated', {
      name,
      previousStatus,
      currentStatus: status,
    });

    if (previousStatus !== status) {
      this.emit('healthStatusChanged', {
        platform: registration,
        previousStatus,
        currentStatus: status,
      });
    }
  }

  /**
   * Get platform capabilities
   */
  getPlatformCapabilities(name: string): PlatformCapability[] {
    const registration = this.platforms.get(name);
    return registration ? registration.capabilities : [];
  }

  /**
   * Check if platform supports specific capabilities
   */
  hasCapabilities(registration: PlatformRegistration, requiredCapabilities: string[]): boolean {
    const availableCapabilities = registration.capabilities.map((cap) => cap.name);
    return requiredCapabilities.every((cap) => availableCapabilities.includes(cap));
  }

  /**
   * Get platform statistics
   */
  getStatistics(): {
    total: number;
    enabled: number;
    byType: Record<PlatformType, number>;
    healthStatus: Record<string, number>;
  } {
    const platforms = this.getAllPlatforms();
    const enabled = platforms.filter((p) => p.config.enabled);

    const byType: Record<PlatformType, number> = {} as any;
    const healthStatus: Record<string, number> = {};

    for (const platform of platforms) {
      // Count by type
      byType[platform.config.type] = (byType[platform.config.type] || 0) + 1;

      // Count by health status
      const status = platform.healthStatus || 'unknown';
      healthStatus[status] = (healthStatus[status] || 0) + 1;
    }

    return {
      total: platforms.length,
      enabled: enabled.length,
      byType,
      healthStatus,
    };
  }

  /**
   * Clear all registrations (for testing)
   */
  clear(): void {
    this.platforms.clear();
    this.platformByType.clear();
    this.platformByUrl.clear();
    this.detectionCache.clear();
  }

  // Private methods

  /**
   * Validate platform configuration
   */
  private validatePlatformConfig(config: PlatformConfig): void {
    if (!config.name || config.name.trim().length === 0) {
      throw new Error('Platform name is required');
    }

    if (!config.baseUrl || !this.isValidUrl(config.baseUrl)) {
      throw new Error('Platform baseUrl must be a valid URL');
    }

    if (!Object.values(PlatformType).includes(config.type)) {
      throw new Error(`Invalid platform type: ${config.type}`);
    }

    if (this.platforms.has(config.name)) {
      throw new Error(`Platform '${config.name}' is already registered`);
    }
  }

  /**
   * Discover platform capabilities
   */
  private async discoverCapabilities(config: PlatformConfig): Promise<PlatformCapability[]> {
    const capabilities: PlatformCapability[] = [];

    // Base capabilities for all platforms
    capabilities.push(
      {
        name: 'repository_access',
        version: '1.0.0',
        description: 'Access repository information and content',
        supported: true,
      },
      {
        name: 'branch_management',
        version: '1.0.0',
        description: 'Create, list, and manage branches',
        supported: true,
      },
      {
        name: 'file_operations',
        version: '1.0.0',
        description: 'Read, write, and manage files',
        supported: true,
      }
    );

    // Platform-specific capabilities
    switch (config.type) {
      case PlatformType.GITHUB:
        capabilities.push(
          {
            name: 'pull_requests',
            version: '1.0.0',
            description: 'Create and manage pull requests',
            supported: true,
          },
          {
            name: 'github_actions',
            version: '1.0.0',
            description: 'Integration with GitHub Actions',
            supported: true,
          },
          {
            name: 'github_apps',
            version: '1.0.0',
            description: 'GitHub Apps authentication',
            supported: config.auth.method === AuthenticationMethod.APP,
          }
        );
        break;

      case PlatformType.GITLAB:
        capabilities.push(
          {
            name: 'merge_requests',
            version: '1.0.0',
            description: 'Create and manage merge requests',
            supported: true,
          },
          {
            name: 'gitlab_ci',
            version: '1.0.0',
            description: 'Integration with GitLab CI/CD',
            supported: true,
          },
          {
            name: 'gitlab_groups',
            version: '1.0.0',
            description: 'GitLab groups and namespaces',
            supported: true,
          }
        );
        break;

      case PlatformType.GITEA:
      case PlatformType.FORGEJO:
        capabilities.push(
          {
            name: 'pull_requests',
            version: '1.0.0',
            description: 'Create and manage pull requests',
            supported: true,
          },
          {
            name: 'gitea_actions',
            version: '1.0.0',
            description: 'Integration with Gitea Actions',
            supported: config.type === PlatformType.FORGEJO,
          }
        );
        break;
    }

    // Authentication-specific capabilities
    if (config.auth.method === AuthenticationMethod.OAUTH2) {
      capabilities.push({
        name: 'oauth2',
        version: '1.0.0',
        description: 'OAuth2 authentication support',
        supported: true,
      });
    }

    if (config.auth.method === AuthenticationMethod.APP) {
      capabilities.push({
        name: 'app_authentication',
        version: '1.0.0',
        description: 'App-based authentication',
        supported: true,
      });
    }

    return capabilities;
  }

  /**
   * Check for platform conflicts
   */
  private async checkForConflicts(registration: PlatformRegistration): Promise<void> {
    const { config } = registration;

    // Check for duplicate base URLs
    for (const [name, existing] of this.platforms) {
      if (name !== config.name && existing.config.baseUrl === config.baseUrl) {
        throw new Error(
          `Platform '${config.name}' has the same base URL as existing platform '${name}'`
        );
      }
    }

    // Check for authentication conflicts
    if (config.auth.method === AuthenticationMethod.APP) {
      const appPlatforms = this.getAllPlatforms().filter(
        (reg) =>
          reg.config.auth.method === AuthenticationMethod.APP && reg.config.type === config.type
      );

      for (const existing of appPlatforms) {
        if ((existing.config.auth as any).appId === (config.auth as any).appId) {
          this.logger.warn('Duplicate app ID detected', {
            existing: existing.config.name,
            new: config.name,
            appId: (config.auth as any).appId,
          });
        }
      }
    }
  }

  /**
   * Validate URL format
   */
  private isValidUrl(url: string): boolean {
    try {
      new URL(url);
      return true;
    } catch {
      return false;
    }
  }
}
```

### 3.2 Default Branch Configuration Manager

```typescript
// packages/platforms/src/config/branch-config.ts

import { PlatformConfig, PlatformType } from './types';
import { Logger } from '@tamma/shared/logging';

/**
 * Default branch configuration
 */
export interface DefaultBranchConfig {
  platformName: string;
  defaultBranch: string;
  repositoryOverrides?: Map<string, string>;
  branchProtection?: {
    enabled: boolean;
    requireReviews: boolean;
    requireStatusChecks: boolean;
    enforceAdmins: boolean;
    requiredApprovingReviewCount?: number;
    requiredStatusCheckContexts?: string[];
  };
  migration?: {
    fromBranch: string;
    toBranch: string;
    autoMigrate: boolean;
    updateReferences: boolean;
  };
}

/**
 * Branch configuration manager
 */
export class BranchConfigManager {
  private configs: Map<string, DefaultBranchConfig> = new Map();
  private logger: Logger;

  constructor(logger: Logger) {
    this.logger = logger.child({ component: 'BranchConfigManager' });
  }

  /**
   * Configure default branch for a platform
   */
  configureDefaultBranch(platformName: string, config: DefaultBranchConfig): void {
    this.logger.info('Configuring default branch', {
      platformName,
      defaultBranch: config.defaultBranch,
    });

    this.configs.set(platformName, config);
  }

  /**
   * Get default branch for platform
   */
  getDefaultBranch(platformName: string): string {
    const config = this.configs.get(platformName);
    return config?.defaultBranch || 'main';
  }

  /**
   * Get default branch for specific repository
   */
  getRepositoryDefaultBranch(platformName: string, repositoryUrl: string): string {
    const config = this.configs.get(platformName);

    // Check for repository-specific override
    if (config?.repositoryOverrides?.has(repositoryUrl)) {
      return config.repositoryOverrides.get(repositoryUrl)!;
    }

    // Return platform default
    return this.getDefaultBranch(platformName);
  }

  /**
   * Set repository-specific branch override
   */
  setRepositoryBranchOverride(platformName: string, repositoryUrl: string, branch: string): void {
    let config = this.configs.get(platformName);
    if (!config) {
      config = {
        platformName,
        defaultBranch: 'main',
      };
      this.configs.set(platformName, config);
    }

    if (!config.repositoryOverrides) {
      config.repositoryOverrides = new Map();
    }

    config.repositoryOverrides.set(repositoryUrl, branch);

    this.logger.debug('Set repository branch override', {
      platformName,
      repositoryUrl,
      branch,
    });
  }

  /**
   * Validate branch name
   */
  validateBranchName(branch: string): { valid: boolean; errors: string[] } {
    const errors: string[] = [];

    if (!branch || branch.trim().length === 0) {
      errors.push('Branch name cannot be empty');
    }

    if (branch.length > 255) {
      errors.push('Branch name cannot exceed 255 characters');
    }

    // Git branch name rules
    if (!/^[a-zA-Z0-9/_-]+$/.test(branch)) {
      errors.push(
        'Branch name can only contain alphanumeric characters, underscores, hyphens, and forward slashes'
      );
    }

    // Cannot start with . or end with .lock
    if (branch.startsWith('.')) {
      errors.push('Branch name cannot start with a dot');
    }

    if (branch.endsWith('.lock')) {
      errors.push('Branch name cannot end with .lock');
    }

    // Cannot contain consecutive slashes
    if (branch.includes('//')) {
      errors.push('Branch name cannot contain consecutive slashes');
    }

    // Cannot contain spaces
    if (branch.includes(' ')) {
      errors.push('Branch name cannot contain spaces');
    }

    // Platform-specific restrictions
    const platformRestrictions = this.getPlatformBranchRestrictions(branch);
    errors.push(...platformRestrictions);

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  /**
   * Normalize branch name
   */
  normalizeBranchName(branch: string): string {
    // Trim whitespace
    branch = branch.trim();

    // Replace spaces with hyphens
    branch = branch.replace(/\s+/g, '-');

    // Remove invalid characters
    branch = branch.replace(/[^a-zA-Z0-9/_-]/g, '');

    // Remove consecutive slashes
    branch = branch.replace(/\/+/g, '/');

    // Remove leading/trailing slashes
    branch = branch.replace(/^\/+|\/+$/g, '');

    // Ensure it doesn't start with a dot
    branch = branch.replace(/^\./, '');

    // Ensure it doesn't end with .lock
    branch = branch.replace(/\.lock$/, '');

    return branch || 'main';
  }

  /**
   * Get branch protection configuration
   */
  getBranchProtection(platformName: string): DefaultBranchConfig['branchProtection'] {
    const config = this.configs.get(platformName);
    return (
      config?.branchProtection || {
        enabled: false,
        requireReviews: false,
        requireStatusChecks: false,
        enforceAdmins: false,
      }
    );
  }

  /**
   * Get branch migration configuration
   */
  getBranchMigration(platformName: string): DefaultBranchConfig['migration'] | null {
    const config = this.configs.get(platformName);
    return config?.migration || null;
  }

  /**
   * Initialize default configurations for all platform types
   */
  initializeDefaults(): void {
    // GitHub defaults
    this.configureDefaultBranch('github', {
      platformName: 'github',
      defaultBranch: 'main',
      branchProtection: {
        enabled: true,
        requireReviews: true,
        requireStatusChecks: true,
        enforceAdmins: false,
        requiredApprovingReviewCount: 1,
        requiredStatusCheckContexts: ['ci/travis-ci', 'ci/circleci'],
      },
    });

    // GitLab defaults
    this.configureDefaultBranch('gitlab', {
      platformName: 'gitlab',
      defaultBranch: 'main',
      branchProtection: {
        enabled: true,
        requireReviews: true,
        requireStatusChecks: true,
        enforceAdmins: false,
        requiredApprovingReviewCount: 1,
      },
    });

    // Gitea/Forgejo defaults
    this.configureDefaultBranch('gitea', {
      platformName: 'gitea',
      defaultBranch: 'main',
      branchProtection: {
        enabled: false,
        requireReviews: false,
        requireStatusChecks: false,
        enforceAdmins: false,
      },
    });

    this.configureDefaultBranch('forgejo', {
      platformName: 'forgejo',
      defaultBranch: 'main',
      branchProtection: {
        enabled: false,
        requireReviews: false,
        requireStatusChecks: false,
        enforceAdmins: false,
      },
    });

    this.logger.info('Default branch configurations initialized');
  }

  /**
   * Get platform-specific branch restrictions
   */
  private getPlatformBranchRestrictions(branch: string): string[] {
    const restrictions: string[] = [];

    // GitHub-specific restrictions
    if (branch === 'gh-pages') {
      restrictions.push('gh-pages is reserved for GitHub Pages');
    }

    // GitLab-specific restrictions
    if (branch.startsWith('-')) {
      restrictions.push('GitLab branch names cannot start with a hyphen');
    }

    return restrictions;
  }
}
```

### 3.3 PR Template Configuration Manager

```typescript
// packages/platforms/src/config/template-config.ts

import { PlatformConfig, PlatformType } from './types';
import { Logger } from '@tamma/shared/logging';
import { readFile } from 'fs/promises';

/**
 * Template configuration
 */
export interface TemplateConfig {
  platformName: string;
  templateType: 'pull_request' | 'merge_request';
  templatePath?: string;
  defaultContent?: string;
  required?: boolean;
  variables?: Record<string, string>;
  inheritance?: {
    parentTemplate?: string;
    overrides?: Record<string, any>;
  };
  validation?: {
    requiredSections?: string[];
    maxLength?: number;
    allowedFormats?: string[];
  };
}

/**
 * Template rendering context
 */
export interface TemplateContext {
  repositoryUrl: string;
  branchName: string;
  targetBranch: string;
  author: string;
  title: string;
  description?: string;
  issueNumber?: string;
  customVariables?: Record<string, string>;
}

/**
 * Template configuration manager
 */
export class TemplateConfigManager {
  private configs: Map<string, TemplateConfig[]> = new Map();
  private templateCache: Map<string, string> = new Map();
  private logger: Logger;

  constructor(logger: Logger) {
    this.logger = logger.child({ component: 'TemplateConfigManager' });
  }

  /**
   * Configure template for a platform
   */
  configureTemplate(platformName: string, config: TemplateConfig): void {
    if (!this.configs.has(platformName)) {
      this.configs.set(platformName, []);
    }

    const platformConfigs = this.configs.get(platformName)!;
    platformConfigs.push(config);

    this.logger.info('Template configured', {
      platformName,
      templateType: config.templateType,
      templatePath: config.templatePath,
    });
  }

  /**
   * Get template configuration for platform
   */
  getTemplateConfig(
    platformName: string,
    templateType: 'pull_request' | 'merge_request'
  ): TemplateConfig | null {
    const configs = this.configs.get(platformName);
    if (!configs) {
      return null;
    }

    return configs.find((config) => config.templateType === templateType) || null;
  }

  /**
   * Load template content
   */
  async loadTemplateContent(config: TemplateConfig): Promise<string> {
    if (config.defaultContent) {
      return config.defaultContent;
    }

    if (!config.templatePath) {
      throw new Error('Template has no path or default content');
    }

    // Check cache first
    if (this.templateCache.has(config.templatePath)) {
      return this.templateCache.get(config.templatePath)!;
    }

    try {
      const content = await readFile(config.templatePath, 'utf-8');
      this.templateCache.set(config.templatePath, content);
      return content;
    } catch (error) {
      throw new Error(`Failed to load template from ${config.templatePath}: ${error.message}`);
    }
  }

  /**
   * Render template with context
   */
  async renderTemplate(
    platformName: string,
    templateType: 'pull_request' | 'merge_request',
    context: TemplateContext
  ): Promise<string> {
    const config = this.getTemplateConfig(platformName, templateType);
    if (!config) {
      throw new Error(`No ${templateType} template configured for platform ${platformName}`);
    }

    let template = await this.loadTemplateContent(config);

    // Handle template inheritance
    if (config.inheritance?.parentTemplate) {
      const parentContent = await this.loadInheritedTemplate(config.inheritance.parentTemplate);
      template = this.mergeTemplates(parentContent, template, config.inheritance.overrides || {});
    }

    // Substitute variables
    template = this.substituteVariables(template, {
      ...config.variables,
      ...context.customVariables,
      REPOSITORY_URL: context.repositoryUrl,
      BRANCH_NAME: context.branchName,
      TARGET_BRANCH: context.targetBranch,
      AUTHOR: context.author,
      TITLE: context.title,
      DESCRIPTION: context.description || '',
      ISSUE_NUMBER: context.issueNumber || '',
    });

    // Validate rendered template
    if (config.validation) {
      this.validateRenderedTemplate(template, config.validation);
    }

    return template;
  }

  /**
   * Validate template configuration
   */
  validateTemplateConfig(config: TemplateConfig): { valid: boolean; errors: string[] } {
    const errors: string[] = [];

    if (!config.platformName || config.platformName.trim().length === 0) {
      errors.push('Platform name is required');
    }

    if (!config.templateType) {
      errors.push('Template type is required');
    }

    if (!config.templatePath && !config.defaultContent) {
      errors.push('Either template path or default content is required');
    }

    if (config.templatePath && config.defaultContent) {
      errors.push('Cannot specify both template path and default content');
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  /**
   * Initialize default templates for all platform types
   */
  async initializeDefaults(): Promise<void> {
    // GitHub PR template
    this.configureTemplate('github', {
      platformName: 'github',
      templateType: 'pull_request',
      defaultContent: this.getGitHubPRTemplate(),
      required: false,
      variables: {
        CHANGE_TYPE: 'feat|fix|docs|style|refactor|test|chore',
        BREAKING_CHANGE: 'YES|NO',
      },
      validation: {
        requiredSections: ['## Description', '## Type of Change'],
        maxLength: 10000,
      },
    });

    // GitLab MR template
    this.configureTemplate('gitlab', {
      platformName: 'gitlab',
      templateType: 'merge_request',
      defaultContent: this.getGitLabMRTemplate(),
      required: false,
      variables: {
        CHANGE_TYPE: 'feature|bugfix|hotfix|documentation|performance|refactoring',
        TESTING: 'YES|NO',
      },
      validation: {
        requiredSections: ['### Description', '### Type of change'],
        maxLength: 10000,
      },
    });

    // Gitea PR template
    this.configureTemplate('gitea', {
      platformName: 'gitea',
      templateType: 'pull_request',
      defaultContent: this.getGiteaPRTemplate(),
      required: false,
      validation: {
        requiredSections: ['## Description', '## Checklist'],
        maxLength: 8000,
      },
    });

    // Forgejo PR template (extends Gitea)
    this.configureTemplate('forgejo', {
      platformName: 'forgejo',
      templateType: 'pull_request',
      defaultContent: this.getForgejoPRTemplate(),
      required: false,
      validation: {
        requiredSections: ['## Description', '## Checklist'],
        maxLength: 8000,
      },
    });

    this.logger.info('Default template configurations initialized');
  }

  // Private methods

  /**
   * Substitute variables in template
   */
  private substituteVariables(template: string, variables: Record<string, string>): string {
    let result = template;

    for (const [key, value] of Object.entries(variables)) {
      const placeholder = `\${${key}}`;
      result = result.replace(
        new RegExp(placeholder.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'g'),
        value || ''
      );
    }

    return result;
  }

  /**
   * Load inherited template
   */
  private async loadInheritedTemplate(parentPath: string): Promise<string> {
    if (this.templateCache.has(parentPath)) {
      return this.templateCache.get(parentPath)!;
    }

    try {
      const content = await readFile(parentPath, 'utf-8');
      this.templateCache.set(parentPath, content);
      return content;
    } catch (error) {
      throw new Error(`Failed to load parent template from ${parentPath}: ${error.message}`);
    }
  }

  /**
   * Merge parent and child templates
   */
  private mergeTemplates(parent: string, child: string, overrides: Record<string, any>): string {
    // Simple merge strategy - child overrides parent sections
    const merged = parent;

    // Apply overrides
    for (const [key, value] of Object.entries(overrides)) {
      const sectionRegex = new RegExp(`^##\\s*${key}.*$`, 'im');
      const sections = parent.split(sectionRegex);

      if (sections.length > 1) {
        // Replace the section with override content
        const beforeSection = sections[0];
        const afterSection = sections.slice(2).join('');
        const overrideContent = typeof value === 'string' ? value : JSON.stringify(value, null, 2);

        return beforeSection + `## ${key}\n${overrideContent}\n` + afterSection;
      }
    }

    // If no overrides, append child content
    return merged + '\n\n' + child;
  }

  /**
   * Validate rendered template
   */
  private validateRenderedTemplate(
    template: string,
    validation: NonNullable<TemplateConfig['validation']>
  ): void {
    if (validation.requiredSections) {
      for (const section of validation.requiredSections) {
        if (!template.includes(section)) {
          throw new Error(`Template missing required section: ${section}`);
        }
      }
    }

    if (validation.maxLength && template.length > validation.maxLength) {
      throw new Error(`Template exceeds maximum length of ${validation.maxLength} characters`);
    }
  }

  /**
   * Get GitHub PR template
   */
  private getGitHubPRTemplate(): string {
    return `## Description
\${DESCRIPTION}

## Type of Change
- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Documentation update

## Checklist
- [ ] My code follows the style guidelines of this project
- [ ] I have performed a self-review of my own code
- [ ] I have commented my code, particularly in hard-to-understand areas
- [ ] I have made corresponding changes to the documentation
- [ ] My changes generate no new warnings
- [ ] I have added tests that prove my fix is effective or that my feature works
- [ ] New and existing unit tests pass locally with my changes
- [ ] Any dependent changes have been merged and published in downstream modules
`;
  }

  /**
   * Get GitLab MR template
   */
  private getGitLabMRTemplate(): string {
    return `### Description
\${DESCRIPTION}

### Type of change
- [ ] Feature (non-breaking change which adds functionality)
- [ ] Bugfix (non-breaking change which fixes an issue)
- [ ] Breaking change (fix or feature that would cause existing functionality to not work as expected)
- [ ] Documentation update

### Checklist
- [ ] Code follows the project style guidelines
- [ ] Self-review of the code has been completed
- [ ] Code has been commented, especially in hard-to-understand areas
- [ ] Documentation has been updated accordingly
- [ ] No new warnings are generated by the changes
- [ ] Tests have been added to prove the fix is effective or that the feature works
- [ ] All unit tests pass locally with the changes
- [ ] Any dependent changes have been merged and published
`;
  }

  /**
   * Get Gitea PR template
   */
  private getGiteaPRTemplate(): string {
    return `## Description
\${DESCRIPTION}

## What type of PR is this?
- [ ] Bugfix
- [ ] Feature
- [ ] Code style update (formatting, local variables)
- [ ] Refactoring (no functional changes, no api changes)
- [ ] Build related changes
- [ ] CI related changes
- [ ] Documentation content changes
- [ ] Other... Please describe:

## What this PR does / why we need it:
\${CHANGE_DESCRIPTION}

## Which issue(s) this PR fixes:
\${ISSUES}

## Special notes for your reviewer:
\${REVIEWER_NOTES}
`;
  }

  /**
   * Get Forgejo PR template
   */
  private getForgejoPRTemplate(): string {
    return `## Description
\${DESCRIPTION}

## What type of PR is this?
- [ ] Bugfix
- [ ] Feature
- [ ] Code style update (formatting, local variables)
- [ ] Refactoring (no functional changes, no api changes)
- [ ] Build related changes
- [ ] CI related changes
- [ ] Documentation content changes
- [ ] Other... Please describe:

## What this PR does / why we need it:
\${CHANGE_DESCRIPTION}

## Which issue(s) this PR fixes:
\${ISSUES}

## Special notes for your reviewer:
\${REVIEWER_NOTES}

## Forgejo-specific considerations
- [ ] Compatible with Forgejo Actions
- [ ] Follows Forgejo contribution guidelines
`;
  }
}
```

### 3.4 Label Conventions Manager

```typescript
// packages/platforms/src/config/label-config.ts

import { PlatformConfig, PlatformType } from './types';
import { Logger } from '@tamma/shared/logging';

/**
 * Label convention configuration
 */
export interface LabelConvention {
  platformName: string;
  defaults: string[];
  prefixes: {
    feature?: string;
    bugfix?: string;
    hotfix?: string;
    docs?: string;
    test?: string;
    refactor?: string;
    chore?: string;
    performance?: string;
    security?: string;
  };
  colors?: Record<string, string>;
  descriptions?: Record<string, string>;
  validation?: {
    maxLength?: number;
    allowedCharacters?: RegExp;
    forbiddenPatterns?: RegExp[];
  };
  synchronization?: {
    enabled: boolean;
    syncFromPlatform?: boolean;
    syncToPlatform?: boolean;
    conflictResolution?: 'platform_wins' | 'config_wins' | 'merge';
  };
}

/**
 * Label manager
 */
export class LabelConfigManager {
  private configs: Map<string, LabelConvention> = new Map();
  private logger: Logger;

  constructor(logger: Logger) {
    this.logger = logger.child({ component: 'LabelConfigManager' });
  }

  /**
   * Configure label conventions for a platform
   */
  configureLabelConvention(platformName: string, config: LabelConvention): void {
    this.logger.info('Configuring label convention', {
      platformName,
      defaultLabels: config.defaults.length,
      prefixes: Object.keys(config.prefixes).length,
    });

    this.configs.set(platformName, config);
  }

  /**
   * Get label convention for platform
   */
  getLabelConvention(platformName: string): LabelConvention | null {
    return this.configs.get(platformName) || null;
  }

  /**
   * Generate label with prefix
   */
  generateLabel(
    platformName: string,
    type: keyof LabelConvention['prefixes'],
    name: string
  ): string {
    const config = this.configs.get(platformName);
    if (!config || !config.prefixes[type]) {
      return name;
    }

    const prefix = config.prefixes[type];
    return `${prefix}/${name}`;
  }

  /**
   * Parse label to extract type and name
   */
  parseLabel(
    platformName: string,
    label: string
  ): { type?: string; name: string; prefix?: string } | null {
    const config = this.configs.get(platformName);
    if (!config) {
      return { name: label };
    }

    for (const [type, prefix] of Object.entries(config.prefixes)) {
      if (prefix && label.startsWith(`${prefix}/`)) {
        return {
          type,
          name: label.substring(prefix.length + 1),
          prefix,
        };
      }
    }

    return { name: label };
  }

  /**
   * Validate label name
   */
  validateLabel(platformName: string, label: string): { valid: boolean; errors: string[] } {
    const config = this.configs.get(platformName);
    const errors: string[] = [];

    if (!label || label.trim().length === 0) {
      errors.push('Label name cannot be empty');
    }

    if (label.length > 50) {
      errors.push('Label name cannot exceed 50 characters');
    }

    // Platform-specific validation
    if (config?.validation) {
      const validation = config.validation;

      if (validation.maxLength && label.length > validation.maxLength) {
        errors.push(`Label name cannot exceed ${validation.maxLength} characters`);
      }

      if (validation.allowedCharacters && !validation.allowedCharacters.test(label)) {
        errors.push('Label name contains invalid characters');
      }

      if (validation.forbiddenPatterns) {
        for (const pattern of validation.forbiddenPatterns) {
          if (pattern.test(label)) {
            errors.push(`Label name matches forbidden pattern: ${pattern.source}`);
          }
        }
      }
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  /**
   * Get default labels for platform
   */
  getDefaultLabels(platformName: string): string[] {
    const config = this.configs.get(platformName);
    return config?.defaults || [];
  }

  /**
   * Get all configured prefixes for platform
   */
  getPrefixes(platformName: string): LabelConvention['prefixes'] {
    const config = this.configs.get(platformName);
    return config?.prefixes || {};
  }

  /**
   * Get label color
   */
  getLabelColor(platformName: string, label: string): string | undefined {
    const config = this.configs.get(platformName);
    return config?.colors?.[label];
  }

  /**
   * Get label description
   */
  getLabelDescription(platformName: string, label: string): string | undefined {
    const config = this.configs.get(platformName);
    return config?.descriptions?.[label];
  }

  /**
   * Synchronize labels with platform
   */
  async synchronizeLabels(
    platformName: string,
    platformLabels: string[]
  ): Promise<{
    added: string[];
    removed: string[];
    updated: string[];
    conflicts: string[];
  }> {
    const config = this.configs.get(platformName);
    if (!config?.synchronization?.enabled) {
      return { added: [], removed: [], updated: [], conflicts: [] };
    }

    const result = {
      added: [] as string[],
      removed: [] as string[],
      updated: [] as string[],
      conflicts: [] as string[],
    };

    const configLabels = new Set(config.defaults);
    const platformLabelSet = new Set(platformLabels);

    // Find labels to add (in config but not on platform)
    for (const label of configLabels) {
      if (!platformLabelSet.has(label)) {
        if (config.synchronization.syncToPlatform) {
          result.added.push(label);
        }
      }
    }

    // Find labels to remove (on platform but not in config)
    for (const label of platformLabelSet) {
      if (!configLabels.has(label)) {
        if (config.synchronization.syncFromPlatform) {
          result.removed.push(label);
        }
      }
    }

    // Check for conflicts (different colors/descriptions)
    if (config.colors || config.descriptions) {
      for (const label of platformLabels) {
        if (configLabels.has(label)) {
          // This would require platform API calls to check actual colors/descriptions
          // For now, just mark as potential conflict
          result.conflicts.push(label);
        }
      }
    }

    this.logger.info('Label synchronization completed', {
      platformName,
      added: result.added.length,
      removed: result.removed.length,
      conflicts: result.conflicts.length,
    });

    return result;
  }

  /**
   * Initialize default label conventions for all platform types
   */
  initializeDefaults(): void {
    // GitHub label conventions
    this.configureLabelConvention('github', {
      platformName: 'github',
      defaults: ['bug', 'enhancement', 'documentation', 'good first issue', 'help wanted'],
      prefixes: {
        feature: 'feat',
        bugfix: 'fix',
        hotfix: 'hotfix',
        docs: 'docs',
        test: 'test',
        refactor: 'refactor',
        chore: 'chore',
        performance: 'perf',
        security: 'security',
      },
      colors: {
        bug: '#d73a4a',
        enhancement: '#a2eeef',
        documentation: '#0075ca',
        'good first issue': '#7057ff',
        'help wanted': '#008672',
        feat: '#a2eeef',
        fix: '#d73a4a',
        hotfix: '#d73a4a',
        docs: '#0075ca',
        test: '#f9d0c4',
        refactor: '#fbca04',
        chore: '#e1e4e8',
        perf: '#f9d0c4',
        security: '#d73a4a',
      },
      validation: {
        maxLength: 50,
        allowedCharacters: /^[a-zA-Z0-9\/\-_\s]+$/,
        forbiddenPatterns: [/^\s+/, /\s+$/, /\/\s*\/$/],
      },
      synchronization: {
        enabled: true,
        syncFromPlatform: false,
        syncToPlatform: true,
        conflictResolution: 'config_wins',
      },
    });

    // GitLab label conventions
    this.configureLabelConvention('gitlab', {
      platformName: 'gitlab',
      defaults: ['bug', 'feature', 'documentation', 'support', 'duplicate'],
      prefixes: {
        feature: 'feature',
        bugfix: 'bugfix',
        hotfix: 'hotfix',
        docs: 'documentation',
        test: 'testing',
        refactor: 'refactoring',
        chore: 'chore',
        performance: 'performance',
        security: 'security',
      },
      colors: {
        bug: '#CC0000',
        feature: '#1F78D1',
        documentation: '#8950FF',
        support: '#FBBA23',
        duplicate: '#CCCCCC',
      },
      validation: {
        maxLength: 50,
        allowedCharacters: /^[a-zA-Z0-9\/\-_\s]+$/,
        forbiddenPatterns: [/^\s+/, /\s+$/, /\/\s*\/$/],
      },
      synchronization: {
        enabled: true,
        syncFromPlatform: true,
        syncToPlatform: false,
        conflictResolution: 'platform_wins',
      },
    });

    // Gitea label conventions
    this.configureLabelConvention('gitea', {
      platformName: 'gitea',
      defaults: ['bug', 'enhancement', 'documentation'],
      prefixes: {
        feature: 'feature',
        bugfix: 'bugfix',
        hotfix: 'hotfix',
        docs: 'docs',
        test: 'test',
        refactor: 'refactor',
        chore: 'chore',
      },
      validation: {
        maxLength: 50,
        allowedCharacters: /^[a-zA-Z0-9\/\-_\s]+$/,
      },
      synchronization: {
        enabled: false,
      },
    });

    // Forgejo label conventions (extends Gitea)
    this.configureLabelConvention('forgejo', {
      platformName: 'forgejo',
      defaults: ['bug', 'enhancement', 'documentation', 'forgejo-specific'],
      prefixes: {
        feature: 'feature',
        bugfix: 'bugfix',
        hotfix: 'hotfix',
        docs: 'docs',
        test: 'test',
        refactor: 'refactor',
        chore: 'chore',
        performance: 'performance',
        security: 'security',
      },
      validation: {
        maxLength: 50,
        allowedCharacters: /^[a-zA-Z0-9\/\-_\s]+$/,
      },
      synchronization: {
        enabled: false,
      },
    });

    this.logger.info('Default label conventions initialized');
  }
}
```

## Testing Strategy

### 3.1 Unit Tests for PlatformRegistry

```typescript
// packages/platforms/src/config/platform-registry.test.ts

import { PlatformRegistry } from './platform-registry';
import { Logger } from '@tamma/shared/logging';
import { PlatformType, AuthenticationMethod } from './types';

describe('PlatformRegistry', () => {
  let registry: PlatformRegistry;
  let logger: Logger;

  beforeEach(() => {
    logger = {
      child: jest.fn(() => logger),
      info: jest.fn(),
      warn: jest.fn(),
      error: jest.fn(),
      debug: jest.fn(),
    } as any;

    registry = new PlatformRegistry(logger);
  });

  afterEach(() => {
    registry.clear();
  });

  describe('Platform Registration', () => {
    it('should register a valid platform', async () => {
      const config = {
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
      };

      await registry.registerPlatform(config);

      const registration = registry.getPlatform('GitHub');
      expect(registration).toBeDefined();
      expect(registration!.config.name).toBe('GitHub');
      expect(registration!.capabilities.length).toBeGreaterThan(0);
    });

    it('should reject duplicate platform names', async () => {
      const config = {
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
      };

      await registry.registerPlatform(config);

      await expect(registry.registerPlatform(config)).rejects.toThrow(
        "Platform 'GitHub' is already registered"
      );
    });

    it('should reject invalid configuration', async () => {
      const config = {
        type: PlatformType.GITHUB,
        name: '', // Invalid empty name
        baseUrl: 'invalid-url',
        auth: {
          method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 100,
      };

      await expect(registry.registerPlatform(config)).rejects.toThrow('Platform name is required');
    });
  });

  describe('Platform Detection', () => {
    beforeEach(async () => {
      const githubConfig = {
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
      };

      await registry.registerPlatform(githubConfig);
    });

    it('should detect platform from GitHub URL', async () => {
      const detected = await registry.detectPlatformFromUrl('https://github.com/user/repo');

      expect(detected).toBeDefined();
      expect(detected!.config.name).toBe('GitHub');
      expect(detected!.config.type).toBe(PlatformType.GITHUB);
    });

    it('should return null for unknown URL', async () => {
      const detected = await registry.detectPlatformFromUrl(
        'https://unknown-platform.com/user/repo'
      );

      expect(detected).toBeNull();
    });

    it('should cache detection results', async () => {
      const url = 'https://github.com/user/repo';

      // First call
      const detected1 = await registry.detectPlatformFromUrl(url);

      // Second call should use cache
      const detected2 = await registry.detectPlatformFromUrl(url);

      expect(detected1).toBe(detected2);
    });
  });

  describe('Platform Selection', () => {
    beforeEach(async () => {
      const githubConfig = {
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
      };

      const gitlabConfig = {
        type: PlatformType.GITLAB,
        name: 'GitLab',
        baseUrl: 'https://gitlab.com',
        auth: {
          method: AuthenticationMethod.PERSONAL_ACCESS_TOKEN,
          tokenEnvVar: 'GITLAB_TOKEN',
        },
        defaultBranch: 'main',
        enabled: true,
        priority: 90,
      };

      await registry.registerPlatform(githubConfig);
      await registry.registerPlatform(gitlabConfig);
    });

    it('should select platform by repository URL', async () => {
      const selected = await registry.selectPlatform({
        repositoryUrl: 'https://github.com/user/repo',
      });

      expect(selected).toBeDefined();
      expect(selected!.config.name).toBe('GitHub');
    });

    it('should select platform by type', async () => {
      const selected = await registry.selectPlatform({
        platformType: PlatformType.GITLAB,
      });

      expect(selected).toBeDefined();
      expect(selected!.config.name).toBe('GitLab');
    });

    it('should select platform by capabilities', async () => {
      const selected = await registry.selectPlatform({
        capabilities: ['pull_requests'],
      });

      expect(selected).toBeDefined();
      expect(selected!.config.type).toBe(PlatformType.GITHUB);
    });

    it('should respect priority ordering', async () => {
      const selected = await registry.selectPlatform({});

      expect(selected).toBeDefined();
      expect(selected!.config.priority).toBe(100); // GitHub has higher priority
    });
  });

  describe('Health Status Management', () => {
    beforeEach(async () => {
      const config = {
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
      };

      await registry.registerPlatform(config);
    });

    it('should update health status', async () => {
      await registry.updateHealthStatus('GitHub', 'healthy');

      const registration = registry.getPlatform('GitHub');
      expect(registration!.healthStatus).toBe('healthy');
      expect(registration!.lastHealthCheck).toBeDefined();
    });

    it('should emit health status change events', async () => {
      const eventSpy = jest.fn();
      registry.on('healthStatusChanged', eventSpy);

      await registry.updateHealthStatus('GitHub', 'degraded');

      expect(eventSpy).toHaveBeenCalledWith({
        platform: expect.any(Object),
        previousStatus: undefined,
        currentStatus: 'degraded',
      });
    });
  });
});
```

## Completion Checklist

- [ ] Implement PlatformRegistry class with full registration and management
- [ ] Add platform detection from repository URLs with caching
- [ ] Implement DefaultBranchConfigManager with platform-specific defaults
- [ ] Add TemplateConfigManager with PR/MR template rendering
- [ ] Implement LabelConfigManager with convention management
- [ ] Add comprehensive unit tests for all components
- [ ] Add integration tests with real platform configurations
- [ ] Verify platform capability discovery and validation
- [ ] Test template rendering and variable substitution
- [ ] Validate label synchronization and conflict resolution
- [ ] Ensure TypeScript strict mode compliance
- [ ] Add performance monitoring and caching

## Dependencies

- Task 1: Configuration schema and interfaces
- Task 2: Configuration loading and validation
- Platform detection utilities from Task 1
- Template rendering engine
- File system access for template loading
- Logger from @tamma/shared package

## Estimated Time

**PlatformRegistry**: 4-5 days
**BranchConfigManager**: 2-3 days
**TemplateConfigManager**: 3-4 days
**LabelConfigManager**: 3-4 days
**Integration and Testing**: 3-4 days
**Total**: 15-20 days
