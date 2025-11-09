# Implementation Plan: Task 1 - Organization Management

**Story**: 1.3 Organization Management & Multi-Tenancy  
**Task**: 1 - Organization Management  
**Acceptance Criteria**: #1, #5 - Organization creation with name, description, and settings; Organization settings management (quotas, branding)

## Overview

Implement comprehensive organization management system with CRUD operations, settings management, and branding customization.

## Implementation Steps

### Subtask 1.1: Create organization CRUD endpoints

**Objective**: Build complete CRUD API for organization management

**File**: `src/controllers/organization-controller.ts`

```typescript
import { Request, Response } from 'express';
import { body, param, validationResult } from 'express-validator';
import { OrganizationService } from '../services/organization-service';
import { AuthorizationService } from '../services/authorization-service';
import { Permission } from '../models/permissions';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export class OrganizationController {
  constructor(
    private organizationService: OrganizationService,
    private authService: AuthorizationService
  ) {}

  // Validation middleware
  static createOrganizationValidation = [
    body('name')
      .trim()
      .isLength({ min: 1, max: 255 })
      .withMessage('Organization name is required and must be less than 255 characters')
      .matches(/^[a-zA-Z0-9\s\-&.,]+$/)
      .withMessage('Organization name contains invalid characters'),
    body('slug')
      .optional()
      .trim()
      .isLength({ min: 3, max: 100 })
      .withMessage('Slug must be between 3 and 100 characters')
      .matches(/^[a-z0-9-]+$/)
      .withMessage('Slug must contain only lowercase letters, numbers, and hyphens'),
    body('description')
      .optional()
      .trim()
      .isLength({ max: 1000 })
      .withMessage('Description must be less than 1000 characters'),
    body('domain')
      .optional()
      .trim()
      .isLength({ max: 255 })
      .withMessage('Domain must be less than 255 characters')
      .isFQDN()
      .withMessage('Domain must be a valid fully qualified domain name'),
    body('email')
      .optional()
      .trim()
      .isEmail()
      .normalizeEmail()
      .withMessage('Valid email is required'),
    body('phone')
      .optional()
      .trim()
      .isLength({ max: 50 })
      .withMessage('Phone number must be less than 50 characters'),
    body('website').optional().trim().isURL().withMessage('Website must be a valid URL'),
    body('address').optional().isObject().withMessage('Address must be an object'),
    body('settings').optional().isObject().withMessage('Settings must be an object'),
  ];

  static updateOrganizationValidation = [
    param('organizationId').isUUID().withMessage('Valid organization ID is required'),
    body('name')
      .optional()
      .trim()
      .isLength({ min: 1, max: 255 })
      .withMessage('Organization name must be less than 255 characters')
      .matches(/^[a-zA-Z0-9\s\-&.,]+$/)
      .withMessage('Organization name contains invalid characters'),
    body('description')
      .optional()
      .trim()
      .isLength({ max: 1000 })
      .withMessage('Description must be less than 1000 characters'),
    body('domain')
      .optional()
      .trim()
      .isLength({ max: 255 })
      .withMessage('Domain must be less than 255 characters')
      .isFQDN()
      .withMessage('Domain must be a valid fully qualified domain name'),
    body('email')
      .optional()
      .trim()
      .isEmail()
      .normalizeEmail()
      .withMessage('Valid email is required'),
    body('phone')
      .optional()
      .trim()
      .isLength({ max: 50 })
      .withMessage('Phone number must be less than 50 characters'),
    body('website').optional().trim().isURL().withMessage('Website must be a valid URL'),
    body('address').optional().isObject().withMessage('Address must be an object'),
    body('settings').optional().isObject().withMessage('Settings must be an object'),
  ];

  async createOrganization(req: Request, res: Response): Promise<void> {
    try {
      // Check validation errors
      const errors = validationResult(req);
      if (!errors.isEmpty()) {
        throw new ApiError(400, 'Validation failed', errors.array());
      }

      const userId = req.user!.sub;
      const { name, slug, description, domain, email, phone, website, address, settings } =
        req.body;

      // Check if user can create organizations
      await this.authService.requirePermission(userId, Permission.ORG_CREATE);

      // Create organization
      const organization = await this.organizationService.createOrganization({
        name,
        slug,
        description,
        domain,
        email,
        phone,
        website,
        address,
        settings,
        createdBy: userId,
      });

      // Add user as owner
      await this.organizationService.addUserToOrganization(
        userId,
        organization.id,
        'owner',
        userId
      );

      // Emit event for audit trail
      await this.organizationService.emitEvent('ORGANIZATION.CREATED', {
        organizationId: organization.id,
        name: organization.name,
        userId,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('Organization created', {
        organizationId: organization.id,
        name: organization.name,
        userId,
      });

      res.status(201).json({
        message: 'Organization created successfully',
        organization: {
          id: organization.id,
          name: organization.name,
          slug: organization.slug,
          description: organization.description,
          domain: organization.domain,
          email: organization.email,
          phone: organization.phone,
          website: organization.website,
          address: organization.address,
          settings: organization.settings,
          status: organization.status,
          subscriptionTier: organization.subscription_tier,
          createdAt: organization.created_at,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
          details: error.details,
        });
      } else {
        logger.error('Create organization error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async getOrganizations(req: Request, res: Response): Promise<void> {
    try {
      const userId = req.user!.sub;
      const { page = 1, limit = 20, search, status } = req.query;

      const organizations = await this.organizationService.getUserOrganizations(userId, {
        page: parseInt(page as string),
        limit: parseInt(limit as string),
        search: search as string,
        status: status as string,
      });

      res.json({
        organizations: organizations.data.map((org) => ({
          id: org.id,
          name: org.name,
          slug: org.slug,
          description: org.description,
          domain: org.domain,
          email: org.email,
          phone: org.phone,
          website: org.website,
          status: org.status,
          subscriptionTier: org.subscription_tier,
          role: org.role,
          memberCount: org.member_count,
          createdAt: org.created_at,
          updatedAt: org.updated_at,
        })),
        pagination: {
          page: organizations.page,
          limit: organizations.limit,
          total: organizations.total,
          totalPages: Math.ceil(organizations.total / organizations.limit),
        },
      });
    } catch (error) {
      logger.error('Get organizations error', { error });
      res.status(500).json({ error: 'Internal server error' });
    }
  }

  async getOrganization(req: Request, res: Response): Promise<void> {
    try {
      const { organizationId } = req.params;
      const userId = req.user!.sub;

      // Check if user can read organization
      await this.authService.requirePermission(userId, Permission.ORG_READ, organizationId);

      const organization = await this.organizationService.getOrganizationById(
        organizationId,
        userId
      );

      if (!organization) {
        throw new ApiError(404, 'Organization not found');
      }

      res.json({
        organization: {
          id: organization.id,
          name: organization.name,
          slug: organization.slug,
          description: organization.description,
          domain: organization.domain,
          email: organization.email,
          phone: organization.phone,
          website: organization.website,
          address: organization.address,
          settings: organization.settings,
          status: organization.status,
          subscriptionTier: organization.subscription_tier,
          subscriptionExpiresAt: organization.subscription_expires_at,
          role: organization.role,
          memberCount: organization.member_count,
          createdAt: organization.created_at,
          updatedAt: organization.updated_at,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Get organization error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async updateOrganization(req: Request, res: Response): Promise<void> {
    try {
      // Check validation errors
      const errors = validationResult(req);
      if (!errors.isEmpty()) {
        throw new ApiError(400, 'Validation failed', errors.array());
      }

      const { organizationId } = req.params;
      const userId = req.user!.sub;
      const updates = req.body;

      // Check if user can update organization
      await this.authService.requirePermission(userId, Permission.ORG_UPDATE, organizationId);

      const updatedOrganization = await this.organizationService.updateOrganization(
        organizationId,
        updates,
        userId
      );

      // Emit event for audit trail
      await this.organizationService.emitEvent('ORGANIZATION.UPDATED', {
        organizationId,
        updates: Object.keys(updates),
        userId,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('Organization updated', {
        organizationId,
        updates: Object.keys(updates),
        userId,
      });

      res.json({
        message: 'Organization updated successfully',
        organization: {
          id: updatedOrganization.id,
          name: updatedOrganization.name,
          slug: updatedOrganization.slug,
          description: updatedOrganization.description,
          domain: updatedOrganization.domain,
          email: updatedOrganization.email,
          phone: updatedOrganization.phone,
          website: updatedOrganization.website,
          address: updatedOrganization.address,
          settings: updatedOrganization.settings,
          status: updatedOrganization.status,
          subscriptionTier: updatedOrganization.subscription_tier,
          subscriptionExpiresAt: updatedOrganization.subscription_expires_at,
          updatedAt: updatedOrganization.updated_at,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Update organization error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async deleteOrganization(req: Request, res: Response): Promise<void> {
    try {
      const { organizationId } = req.params;
      const userId = req.user!.sub;

      // Check if user can delete organization
      await this.authService.requirePermission(userId, Permission.ORG_DELETE, organizationId);

      // Soft delete organization
      await this.organizationService.deleteOrganization(organizationId, userId);

      // Emit event for audit trail
      await this.organizationService.emitEvent('ORGANIZATION.DELETED', {
        organizationId,
        userId,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('Organization deleted', {
        organizationId,
        userId,
      });

      res.json({
        message: 'Organization deleted successfully',
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Delete organization error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }
}
```

### Subtask 1.2: Implement organization settings management

**Objective**: Create comprehensive organization settings system

**File**: `src/services/organization-settings-service.ts`

```typescript
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface OrganizationSettings {
  // General settings
  name: string;
  description?: string;
  domain?: string;
  email?: string;
  phone?: string;
  website?: string;
  address?: {
    line1?: string;
    line2?: string;
    city?: string;
    state?: string;
    country?: string;
    postalCode?: string;
  };

  // Branding settings
  branding: {
    logo?: string;
    primaryColor?: string;
    secondaryColor?: string;
    fontFamily?: string;
    customCSS?: string;
    favicon?: string;
    theme?: 'light' | 'dark' | 'auto';
  };

  // Security settings
  security: {
    requireMfa: boolean;
    passwordPolicy: {
      minLength: number;
      requireUppercase: boolean;
      requireLowercase: boolean;
      requireNumbers: boolean;
      requireSpecialChars: boolean;
    };
    sessionTimeout: number; // minutes
    maxLoginAttempts: number;
    lockoutDuration: number; // minutes
    allowedIpRanges: string[];
    enforceSso: boolean;
  };

  // Notification settings
  notifications: {
    email: {
      enabled: boolean;
      welcomeEmail: boolean;
      securityAlerts: boolean;
      weeklyReports: boolean;
      billingAlerts: boolean;
    };
    slack?: {
      webhookUrl?: string;
      channel?: string;
      enabled: boolean;
    };
    webhook?: {
      url?: string;
      events: string[];
      secret?: string;
      enabled: boolean;
    };
  };

  // Feature flags
  features: {
    betaFeatures: boolean;
    advancedAnalytics: boolean;
    customIntegrations: boolean;
    apiAccess: boolean;
    ssoIntegration: boolean;
    auditLogs: boolean;
    exportData: boolean;
  };

  // Quota settings
  quotas: {
    maxUsers: number;
    maxProjects: number;
    maxApiCallsPerMonth: number;
    maxStorageGb: number;
    maxBenchmarksPerMonth: number;
  };

  // Integration settings
  integrations: {
    github?: {
      enabled: boolean;
      clientId?: string;
      webhookSecret?: string;
    };
    gitlab?: {
      enabled: boolean;
      instanceUrl?: string;
      applicationId?: string;
    };
    slack?: {
      enabled: boolean;
      teamId?: string;
      botToken?: string;
    };
  };
}

export class OrganizationSettingsService {
  private readonly DEFAULT_SETTINGS: Partial<OrganizationSettings> = {
    branding: {
      primaryColor: '#2563eb',
      secondaryColor: '#64748b',
      fontFamily: 'Inter, sans-serif',
      theme: 'light',
    },
    security: {
      requireMfa: false,
      passwordPolicy: {
        minLength: 8,
        requireUppercase: true,
        requireLowercase: true,
        requireNumbers: true,
        requireSpecialChars: true,
      },
      sessionTimeout: 480, // 8 hours
      maxLoginAttempts: 5,
      lockoutDuration: 15, // 15 minutes
      allowedIpRanges: [],
      enforceSso: false,
    },
    notifications: {
      email: {
        enabled: true,
        welcomeEmail: true,
        securityAlerts: true,
        weeklyReports: false,
        billingAlerts: true,
      },
    },
    features: {
      betaFeatures: false,
      advancedAnalytics: false,
      customIntegrations: false,
      apiAccess: true,
      ssoIntegration: false,
      auditLogs: true,
      exportData: true,
    },
    quotas: {
      maxUsers: 10,
      maxProjects: 50,
      maxApiCallsPerMonth: 10000,
      maxStorageGb: 10,
      maxBenchmarksPerMonth: 100,
    },
  };

  async getSettings(organizationId: string): Promise<OrganizationSettings> {
    try {
      const organization = await db('organizations').where('id', organizationId).first('settings');

      if (!organization) {
        throw new ApiError(404, 'Organization not found');
      }

      const storedSettings = organization.settings || {};

      // Merge with defaults
      return this.mergeWithDefaults(storedSettings);
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to get organization settings', { error, organizationId });
      throw new ApiError(500, 'Failed to get organization settings');
    }
  }

  async updateSettings(
    organizationId: string,
    updates: Partial<OrganizationSettings>,
    userId: string
  ): Promise<OrganizationSettings> {
    try {
      // Get current settings
      const currentSettings = await this.getSettings(organizationId);

      // Validate updates
      this.validateSettingsUpdates(updates);

      // Merge updates with current settings
      const newSettings = this.deepMerge(currentSettings, updates);

      // Store in database
      await db('organizations')
        .where('id', organizationId)
        .update({
          settings: JSON.stringify(newSettings),
          updated_at: new Date(),
          updated_by: userId,
        });

      logger.info('Organization settings updated', {
        organizationId,
        userId,
        updatedKeys: Object.keys(updates),
      });

      return newSettings;
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to update organization settings', { error, organizationId });
      throw new ApiError(500, 'Failed to update organization settings');
    }
  }

  async resetToDefaults(organizationId: string, userId: string): Promise<OrganizationSettings> {
    try {
      const defaultSettings = this.mergeWithDefaults({});

      await db('organizations')
        .where('id', organizationId)
        .update({
          settings: JSON.stringify(defaultSettings),
          updated_at: new Date(),
          updated_by: userId,
        });

      logger.info('Organization settings reset to defaults', {
        organizationId,
        userId,
      });

      return defaultSettings;
    } catch (error) {
      logger.error('Failed to reset organization settings', { error, organizationId });
      throw new ApiError(500, 'Failed to reset organization settings');
    }
  }

  async exportSettings(organizationId: string): Promise<Record<string, any>> {
    try {
      const settings = await this.getSettings(organizationId);

      // Remove sensitive information
      const exportSettings = this.sanitizeForExport(settings);

      return exportSettings;
    } catch (error) {
      logger.error('Failed to export organization settings', { error, organizationId });
      throw new ApiError(500, 'Failed to export organization settings');
    }
  }

  async importSettings(
    organizationId: string,
    settingsData: Record<string, any>,
    userId: string
  ): Promise<OrganizationSettings> {
    try {
      // Validate imported data
      this.validateImportData(settingsData);

      // Import settings
      const updatedSettings = await this.updateSettings(organizationId, settingsData, userId);

      logger.info('Organization settings imported', {
        organizationId,
        userId,
        importedKeys: Object.keys(settingsData),
      });

      return updatedSettings;
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to import organization settings', { error, organizationId });
      throw new ApiError(500, 'Failed to import organization settings');
    }
  }

  private mergeWithDefaults(storedSettings: any): OrganizationSettings {
    return this.deepMerge(this.DEFAULT_SETTINGS, storedSettings) as OrganizationSettings;
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

  private validateSettingsUpdates(updates: Partial<OrganizationSettings>): void {
    // Validate branding colors
    if (updates.branding?.primaryColor) {
      if (!/^#[0-9A-Fa-f]{6}$/.test(updates.branding.primaryColor)) {
        throw new ApiError(400, 'Primary color must be a valid hex color');
      }
    }

    if (updates.branding?.secondaryColor) {
      if (!/^#[0-9A-Fa-f]{6}$/.test(updates.branding.secondaryColor)) {
        throw new ApiError(400, 'Secondary color must be a valid hex color');
      }
    }

    // Validate security settings
    if (updates.security?.passwordPolicy) {
      const policy = updates.security.passwordPolicy;
      if (policy.minLength < 6 || policy.minLength > 128) {
        throw new ApiError(400, 'Password minimum length must be between 6 and 128');
      }
      if (policy.sessionTimeout < 5 || policy.sessionTimeout > 1440) {
        throw new ApiError(400, 'Session timeout must be between 5 and 1440 minutes');
      }
    }

    // Validate quotas
    if (updates.quotas) {
      const quotas = updates.quotas;
      if (quotas.maxUsers < 1 || quotas.maxUsers > 10000) {
        throw new ApiError(400, 'Max users must be between 1 and 10000');
      }
      if (quotas.maxStorageGb < 1 || quotas.maxStorageGb > 10000) {
        throw new ApiError(400, 'Max storage must be between 1 and 10000 GB');
      }
    }
  }

  private validateImportData(data: Record<string, any>): void {
    // Check for required structure
    const allowedKeys = [
      'branding',
      'security',
      'notifications',
      'features',
      'quotas',
      'integrations',
    ];

    for (const key in data) {
      if (!allowedKeys.includes(key)) {
        throw new ApiError(400, `Invalid settings section: ${key}`);
      }
    }

    // Validate nested structure
    this.validateSettingsUpdates(data);
  }

  private sanitizeForExport(settings: OrganizationSettings): Record<string, any> {
    const exportData = JSON.parse(JSON.stringify(settings));

    // Remove sensitive information
    if (exportData.integrations?.github?.webhookSecret) {
      delete exportData.integrations.github.webhookSecret;
    }
    if (exportData.integrations?.gitlab?.applicationId) {
      delete exportData.integrations.gitlab.applicationId;
    }
    if (exportData.integrations?.slack?.botToken) {
      delete exportData.integrations.slack.botToken;
    }
    if (exportData.notifications?.webhook?.secret) {
      delete exportData.notifications.webhook.secret;
    }

    return exportData;
  }
}
```

### Subtask 1.3: Add organization validation and uniqueness checks

**Objective**: Implement comprehensive validation for organization data

**File**: `src/services/organization-service.ts` (continued)

```typescript
// Add to OrganizationService class

async validateOrganizationData(data: {
  name?: string;
  slug?: string;
  domain?: string;
  email?: string;
}, organizationId?: string): Promise<void> {
  const validations: Promise<void>[] = [];

  // Check name uniqueness
  if (data.name) {
    validations.push(this.validateNameUniqueness(data.name, organizationId));
  }

  // Check slug uniqueness
  if (data.slug) {
    validations.push(this.validateSlugUniqueness(data.slug, organizationId));
  }

  // Check domain uniqueness
  if (data.domain) {
    validations.push(this.validateDomainUniqueness(data.domain, organizationId));
  }

  // Check email uniqueness
  if (data.email) {
    validations.push(this.validateEmailUniqueness(data.email, organizationId));
  }

  await Promise.all(validations);
}

private async validateNameUniqueness(name: string, excludeId?: string): Promise<void> {
  const existing = await db('organizations')
    .where('name', name)
    .whereNot('status', 'deleted')
    .whereNot('id', excludeId || '')
    .first();

  if (existing) {
    throw new ApiError(409, 'Organization with this name already exists');
  }
}

private async validateSlugUniqueness(slug: string, excludeId?: string): Promise<void> {
  const existing = await db('organizations')
    .where('slug', slug)
    .whereNot('status', 'deleted')
    .whereNot('id', excludeId || '')
    .first();

  if (existing) {
    throw new ApiError(409, 'Organization with this slug already exists');
  }
}

private async validateDomainUniqueness(domain: string, excludeId?: string): Promise<void> {
  const existing = await db('organizations')
    .where('domain', domain)
    .whereNot('status', 'deleted')
    .whereNot('id', excludeId || '')
    .first();

  if (existing) {
    throw new ApiError(409, 'Organization with this domain already exists');
  }
}

private async validateEmailUniqueness(email: string, excludeId?: string): Promise<void> {
  const existing = await db('organizations')
    .where('email', email)
    .whereNot('status', 'deleted')
    .whereNot('id', excludeId || '')
    .first();

  if (existing) {
    throw new ApiError(409, 'Organization with this email already exists');
  }
}

async generateUniqueSlug(name: string): Promise<string> {
  // Generate base slug from name
  let baseSlug = name
    .toLowerCase()
    .replace(/[^a-z0-9\s-]/g, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .trim('-');

  if (baseSlug.length < 3) {
    baseSlug = `org-${baseSlug}`;
  }

  let slug = baseSlug;
  let counter = 1;

  // Check if slug is unique, add counter if not
  while (await this.isSlugTaken(slug)) {
    slug = `${baseSlug}-${counter}`;
    counter++;
  }

  return slug;
}

private async isSlugTaken(slug: string): Promise<boolean> {
  const existing = await db('organizations')
    .where('slug', slug)
    .whereNot('status', 'deleted')
    .first();

  return !!existing;
}

async validateOrganizationLimits(userId: string): Promise<void> {
  // Check how many organizations user owns
  const ownedOrgs = await db('organizations')
    .join('user_organizations', 'organizations.id', 'user_organizations.organization_id')
    .where('user_organizations.user_id', userId)
    .where('user_organizations.role', 'owner')
    .where('organizations.status', 'active')
    .count('* as count')
    .first();

  const maxOrgs = await this.getUserMaxOrganizations(userId);

  if (parseInt(ownedOrgs.count) >= maxOrgs) {
    throw new ApiError(429, `You have reached the maximum limit of ${maxOrgs} organizations`);
  }
}

private async getUserMaxOrganizations(userId: string): Promise<number> {
  // This would check user's subscription tier
  // For now, return default limit
  return 5;
}
```

### Subtask 1.4: Create organization branding customization

**Objective**: Implement branding customization features

**File**: `src/services/branding-service.ts`

```typescript
import sharp from 'sharp';
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface BrandingAssets {
  logo?: {
    light?: string; // URL or base64
    dark?: string;
    favicon?: string;
  };
  colors: {
    primary: string;
    secondary: string;
    accent?: string;
    background?: string;
    surface?: string;
    text?: string;
  };
  typography: {
    fontFamily: string;
    headingFont?: string;
    bodyFont?: string;
    fontSize: {
      xs: string;
      sm: string;
      base: string;
      lg: string;
      xl: string;
      '2xl': string;
      '3xl': string;
    };
  };
  layout: {
    borderRadius: string;
    spacing: {
      xs: string;
      sm: string;
      md: string;
      lg: string;
      xl: string;
    };
  };
  customCSS?: string;
}

export class BrandingService {
  private readonly MAX_LOGO_SIZE = 2 * 1024 * 1024; // 2MB
  private readonly MAX_FAVICON_SIZE = 256 * 1024; // 256KB
  private readonly ALLOWED_LOGO_FORMATS = ['image/png', 'image/jpeg', 'image/svg+xml'];
  private readonly ALLOWED_FAVICON_FORMATS = ['image/png', 'image/x-icon'];

  async uploadLogo(
    organizationId: string,
    file: Buffer,
    mimeType: string,
    type: 'light' | 'dark' | 'favicon',
    userId: string
  ): Promise<string> {
    try {
      // Validate file
      this.validateLogoFile(file, mimeType, type);

      // Process image
      const processedImage = await this.processLogoImage(file, mimeType, type);

      // Store image (this would integrate with your storage service)
      const imageUrl = await this.storeImage(organizationId, processedImage, type, mimeType);

      // Update organization settings
      await this.updateOrganizationBranding(
        organizationId,
        {
          logo: {
            [type]: imageUrl,
          },
        },
        userId
      );

      logger.info('Organization logo uploaded', {
        organizationId,
        type,
        userId,
      });

      return imageUrl;
    } catch (error) {
      logger.error('Failed to upload logo', { error, organizationId, type });
      throw new ApiError(500, 'Failed to upload logo');
    }
  }

  async updateBranding(
    organizationId: string,
    branding: Partial<BrandingAssets>,
    userId: string
  ): Promise<BrandingAssets> {
    try {
      // Validate branding data
      this.validateBrandingData(branding);

      // Get current branding
      const currentBranding = await this.getOrganizationBranding(organizationId);

      // Merge with current branding
      const updatedBranding = this.deepMerge(currentBranding, branding);

      // Update organization settings
      await this.updateOrganizationBranding(organizationId, updatedBranding, userId);

      logger.info('Organization branding updated', {
        organizationId,
        userId,
        updatedKeys: Object.keys(branding),
      });

      return updatedBranding;
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to update branding', { error, organizationId });
      throw new ApiError(500, 'Failed to update branding');
    }
  }

  async getOrganizationBranding(organizationId: string): Promise<BrandingAssets> {
    try {
      const organization = await db('organizations').where('id', organizationId).first('settings');

      if (!organization) {
        throw new ApiError(404, 'Organization not found');
      }

      const settings = organization.settings || {};
      const branding = settings.branding || {};

      // Return with defaults
      return this.mergeWithDefaultBranding(branding);
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to get organization branding', { error, organizationId });
      throw new ApiError(500, 'Failed to get organization branding');
    }
  }

  async generateCSS(organizationId: string): Promise<string> {
    try {
      const branding = await this.getOrganizationBranding(organizationId);

      return this.generateCSSFromBranding(branding);
    } catch (error) {
      logger.error('Failed to generate CSS', { error, organizationId });
      throw new ApiError(500, 'Failed to generate CSS');
    }
  }

  private validateLogoFile(file: Buffer, mimeType: string, type: string): void {
    const maxSize = type === 'favicon' ? this.MAX_FAVICON_SIZE : this.MAX_LOGO_SIZE;
    const allowedFormats =
      type === 'favicon' ? this.ALLOWED_FAVICON_FORMATS : this.ALLOWED_LOGO_FORMATS;

    if (file.length > maxSize) {
      throw new ApiError(400, `File size exceeds maximum limit of ${maxSize / 1024 / 1024}MB`);
    }

    if (!allowedFormats.includes(mimeType)) {
      throw new ApiError(400, `Invalid file format. Allowed formats: ${allowedFormats.join(', ')}`);
    }
  }

  private async processLogoImage(file: Buffer, mimeType: string, type: string): Promise<Buffer> {
    let image = sharp(file);

    if (type === 'favicon') {
      // Resize favicon to 32x32
      image = image.resize(32, 32, {
        fit: 'inside',
        background: { r: 0, g: 0, b: 0, alpha: 0 },
      });
    } else {
      // Resize logo to max 200x200
      image = image.resize(200, 200, {
        fit: 'inside',
        background: { r: 0, g: 0, b: 0, alpha: 0 },
      });
    }

    // Convert to PNG for consistency
    if (mimeType !== 'image/png') {
      image = image.png();
    }

    return await image.toBuffer();
  }

  private async storeImage(
    organizationId: string,
    imageBuffer: Buffer,
    type: string,
    mimeType: string
  ): Promise<string> {
    // This would integrate with your storage service (S3, GCS, etc.)
    // For now, return a mock URL
    const filename = `${organizationId}-${type}-${Date.now()}.png`;
    return `https://storage.example.com/branding/${filename}`;
  }

  private async updateOrganizationBranding(
    organizationId: string,
    branding: Partial<BrandingAssets>,
    userId: string
  ): Promise<void> {
    await db('organizations')
      .where('id', organizationId)
      .update({
        settings: db.raw(
          `
          jsonb_set(
            coalesce(settings, '{}'),
            '{branding}',
            coalesce(settings->'branding', '{}'::jsonb) || ?::jsonb
          )
        `,
          [JSON.stringify(branding)]
        ),
        updated_at: new Date(),
        updated_by: userId,
      });
  }

  private validateBrandingData(branding: Partial<BrandingAssets>): void {
    // Validate colors
    if (branding.colors) {
      const colorFields = ['primary', 'secondary', 'accent', 'background', 'surface', 'text'];

      for (const field of colorFields) {
        if (branding.colors[field as keyof typeof branding.colors]) {
          const color = branding.colors[field as keyof typeof branding.colors]!;
          if (!/^#[0-9A-Fa-f]{6}$/.test(color)) {
            throw new ApiError(400, `Invalid ${field} color format. Must be hex color (#RRGGBB)`);
          }
        }
      }
    }

    // Validate font family
    if (branding.typography?.fontFamily) {
      if (
        typeof branding.typography.fontFamily !== 'string' ||
        branding.typography.fontFamily.length === 0
      ) {
        throw new ApiError(400, 'Font family must be a non-empty string');
      }
    }

    // Validate border radius
    if (branding.layout?.borderRadius) {
      if (!/^\d+(px|rem|em|%)$/.test(branding.layout.borderRadius)) {
        throw new ApiError(400, 'Border radius must be a valid CSS value (e.g., 4px, 0.5rem, 50%)');
      }
    }
  }

  private mergeWithDefaultBranding(branding: any): BrandingAssets {
    const defaults: BrandingAssets = {
      colors: {
        primary: '#2563eb',
        secondary: '#64748b',
        accent: '#f59e0b',
        background: '#ffffff',
        surface: '#f8fafc',
        text: '#1e293b',
      },
      typography: {
        fontFamily: 'Inter, system-ui, sans-serif',
        fontSize: {
          xs: '0.75rem',
          sm: '0.875rem',
          base: '1rem',
          lg: '1.125rem',
          xl: '1.25rem',
          '2xl': '1.5rem',
          '3xl': '1.875rem',
        },
      },
      layout: {
        borderRadius: '0.375rem',
        spacing: {
          xs: '0.25rem',
          sm: '0.5rem',
          md: '1rem',
          lg: '1.5rem',
          xl: '2rem',
        },
      },
    };

    return this.deepMerge(defaults, branding);
  }

  private generateCSSFromBranding(branding: BrandingAssets): string {
    const { colors, typography, layout, customCSS } = branding;

    let css = `
:root {
  /* Colors */
  --color-primary: ${colors.primary};
  --color-secondary: ${colors.secondary};
  --color-accent: ${colors.accent || colors.primary};
  --color-background: ${colors.background || '#ffffff'};
  --color-surface: ${colors.surface || '#f8fafc'};
  --color-text: ${colors.text || '#1e293b'};
  
  /* Typography */
  --font-family: ${typography.fontFamily};
  --font-heading: ${typography.headingFont || typography.fontFamily};
  --font-body: ${typography.bodyFont || typography.fontFamily};
  
  /* Font Sizes */
  --font-size-xs: ${typography.fontSize.xs};
  --font-size-sm: ${typography.fontSize.sm};
  --font-size-base: ${typography.fontSize.base};
  --font-size-lg: ${typography.fontSize.lg};
  --font-size-xl: ${typography.fontSize.xl};
  --font-size-2xl: ${typography.fontSize['2xl']};
  --font-size-3xl: ${typography.fontSize['3xl']};
  
  /* Layout */
  --border-radius: ${layout.borderRadius};
  --spacing-xs: ${layout.spacing.xs};
  --spacing-sm: ${layout.spacing.sm};
  --spacing-md: ${layout.spacing.md};
  --spacing-lg: ${layout.spacing.lg};
  --spacing-xl: ${layout.spacing.xl};
}

/* Apply branding styles */
body {
  font-family: var(--font-family);
  color: var(--color-text);
  background-color: var(--color-background);
}

.btn-primary {
  background-color: var(--color-primary);
  border-color: var(--color-primary);
}

.btn-secondary {
  background-color: var(--color-secondary);
  border-color: var(--color-secondary);
}

.card {
  background-color: var(--color-surface);
  border-radius: var(--border-radius);
}
`;

    if (customCSS) {
      css += `\n/* Custom CSS */\n${customCSS}\n`;
    }

    return css;
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
}
```

## Files to Create

1. `src/controllers/organization-controller.ts` - Organization CRUD endpoints
2. `src/services/organization-service.ts` - Organization business logic
3. `src/services/organization-settings-service.ts` - Settings management
4. `src/services/branding-service.ts` - Branding customization
5. `src/routes/organization-routes.ts` - Organization routes

## Dependencies

- Express.js for controllers and routing
- Sharp for image processing
- Database connection for persistence
- Authorization service for permission checking
- File storage service for logo uploads

## Testing

1. Organization CRUD operations tests
2. Settings management tests
3. Validation and uniqueness tests
4. Branding customization tests
5. Permission-based access tests

## Notes

- Implement proper validation for all organization fields
- Use soft deletes for organizations
- Store settings as JSONB for flexibility
- Implement image optimization for logos
- Add comprehensive audit logging
- Consider caching frequently accessed organization data
