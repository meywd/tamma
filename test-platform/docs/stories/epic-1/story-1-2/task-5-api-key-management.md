# Implementation Plan: Task 5 - API Key Management

**Story**: 1.2 Authentication & Authorization System  
**Task**: 5 - API Key Management  
**Acceptance Criteria**: #9 - API key generation and management for programmatic access

## Overview

Implement comprehensive API key management system with secure generation, authentication, usage tracking, and lifecycle management.

## Implementation Steps

### Subtask 5.1: Create API key generation with secure hashing

**Objective**: Build secure API key generation and storage system

**File**: `src/services/api-key-service.ts`

```typescript
import { randomBytes } from 'crypto';
import { addDays, addMonths } from 'date-fns';
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface ApiKeyData {
  id: string;
  keyId: string;
  keyHash: string;
  keyPrefix: string;
  userId: string;
  organizationId?: string;
  name: string;
  description?: string;
  keyType: 'personal' | 'service' | 'integration';
  permissions: string[];
  scopes: string[];
  allowedIps?: string[];
  allowedDomains?: string[];
  status: 'active' | 'inactive' | 'revoked' | 'expired';
  expiresAt?: Date;
  usageCount: number;
  usageLimit?: number;
  usageResetAt?: Date;
  requireMfa: boolean;
  createdAt: Date;
  updatedAt: Date;
  createdBy?: string;
  lastUsedAt?: Date;
  lastUsedIp?: string;
  lastUsedUserAgent?: string;
}

export interface ApiKeyGenerationOptions {
  name: string;
  description?: string;
  keyType?: 'personal' | 'service' | 'integration';
  permissions?: string[];
  scopes?: string[];
  allowedIps?: string[];
  allowedDomains?: string[];
  expiresAt?: Date;
  usageLimit?: number;
  requireMfa?: boolean;
}

export class ApiKeyService {
  private readonly KEY_LENGTH = 64;
  private readonly KEY_PREFIX_LENGTH = 8;
  private readonly DEFAULT_EXPIRY_DAYS = 365;

  async generateApiKey(
    userId: string,
    options: ApiKeyGenerationOptions,
    organizationId?: string,
    context?: {
      ipAddress?: string;
      userAgent?: string;
    }
  ): Promise<{
    apiKey: string;
    keyData: ApiKeyData;
  }> {
    try {
      // Generate secure API key
      const apiKey = this.generateSecureKey();
      const keyHash = this.hashApiKey(apiKey);
      const keyPrefix = apiKey.substring(0, this.KEY_PREFIX_LENGTH);
      const keyId = `key_${Date.now()}_${randomBytes(8).toString('hex')}`;

      // Set default expiry if not provided
      const expiresAt = options.expiresAt || addDays(new Date(), this.DEFAULT_EXPIRY_DAYS);

      // Store API key in database
      const keyData: Partial<ApiKeyData> = {
        id: keyId,
        keyId: keyPrefix + '_' + randomBytes(4).toString('hex'),
        keyHash,
        keyPrefix,
        userId,
        organizationId,
        name: options.name,
        description: options.description,
        keyType: options.keyType || 'personal',
        permissions: options.permissions || [],
        scopes: options.scopes || [],
        allowedIps: options.allowedIps,
        allowedDomains: options.allowedDomains,
        status: 'active',
        expiresAt,
        usageCount: 0,
        usageLimit: options.usageLimit,
        usageResetAt: addMonths(new Date(), 1), // Reset monthly
        requireMfa: options.requireMfa || false,
        createdAt: new Date(),
        updatedAt: new Date(),
        createdBy: userId,
        createdFromIp: context?.ipAddress,
        createdFromUserAgent: context?.userAgent,
      };

      await db('api_keys').insert(keyData);

      const fullKeyData = await this.getApiKeyById(keyId);

      // Emit event for audit trail
      await this.emitEvent('API_KEY.CREATED', {
        keyId: keyData.id,
        keyPrefix: keyData.keyPrefix,
        userId,
        organizationId,
        name: options.name,
        keyType: keyData.keyType,
        ipAddress: context?.ipAddress,
        userAgent: context?.userAgent,
      });

      logger.info('API key generated', {
        keyId: keyData.id,
        keyPrefix: keyData.keyPrefix,
        userId,
        organizationId,
        name: options.name,
      });

      return {
        apiKey, // Return the full key only once
        keyData: fullKeyData!,
      };
    } catch (error) {
      logger.error('Failed to generate API key', { error, userId, options });
      throw new ApiError(500, 'Failed to generate API key');
    }
  }

  async validateApiKey(
    apiKey: string,
    context?: {
      ipAddress?: string;
      userAgent?: string;
      domain?: string;
    }
  ): Promise<{
    isValid: boolean;
    keyData?: ApiKeyData;
    error?: string;
  }> {
    try {
      // Extract key prefix for quick lookup
      const keyPrefix = apiKey.substring(0, this.KEY_PREFIX_LENGTH);

      // Find API key by prefix
      const keyData = await db('api_keys')
        .where('key_prefix', keyPrefix)
        .where('status', 'active')
        .first();

      if (!keyData) {
        return { isValid: false, error: 'Invalid API key' };
      }

      // Check expiry
      if (keyData.expires_at && new Date() > keyData.expires_at) {
        await this.updateKeyStatus(keyData.id, 'expired');
        return { isValid: false, error: 'API key has expired' };
      }

      // Verify key hash
      const isValidHash = await this.verifyApiKeyHash(apiKey, keyData.key_hash);
      if (!isValidHash) {
        return { isValid: false, error: 'Invalid API key' };
      }

      // Check IP restrictions
      if (keyData.allowed_ips && context?.ipAddress) {
        const allowedIps = keyData.allowed_ips.split(',').map((ip) => ip.trim());
        if (!allowedIps.includes(context.ipAddress)) {
          await this.recordFailedAttempt(keyData.id, context.ipAddress, 'IP_NOT_ALLOWED');
          return { isValid: false, error: 'IP address not allowed' };
        }
      }

      // Check domain restrictions
      if (keyData.allowed_domains && context?.domain) {
        const allowedDomains = keyData.allowed_domains.split(',').map((domain) => domain.trim());
        if (!allowedDomains.includes(context.domain)) {
          await this.recordFailedAttempt(keyData.id, context.ipAddress, 'DOMAIN_NOT_ALLOWED');
          return { isValid: false, error: 'Domain not allowed' };
        }
      }

      // Check usage limits
      if (keyData.usage_limit && keyData.usage_count >= keyData.usage_limit) {
        return { isValid: false, error: 'API key usage limit exceeded' };
      }

      // Update usage statistics
      await this.updateUsageStats(keyData.id, context);

      logger.debug('API key validated successfully', {
        keyId: keyData.id,
        keyPrefix: keyData.key_prefix,
        userId: keyData.user_id,
        ipAddress: context?.ipAddress,
      });

      return { isValid: true, keyData };
    } catch (error) {
      logger.error('API key validation failed', { error, keyPrefix: apiKey.substring(0, 8) });
      return { isValid: false, error: 'API key validation failed' };
    }
  }

  async getUserApiKeys(userId: string, organizationId?: string): Promise<ApiKeyData[]> {
    try {
      const query = db('api_keys').where('user_id', userId).orderBy('created_at', 'desc');

      if (organizationId) {
        query.where('organization_id', organizationId);
      }

      return await query.select([
        'id',
        'key_id',
        'key_prefix',
        'name',
        'description',
        'key_type',
        'permissions',
        'scopes',
        'allowed_ips',
        'allowed_domains',
        'status',
        'expires_at',
        'usage_count',
        'usage_limit',
        'require_mfa',
        'created_at',
        'updated_at',
        'last_used_at',
        'last_used_ip',
        'organization_id',
      ]);
    } catch (error) {
      logger.error('Failed to get user API keys', { error, userId });
      throw new ApiError(500, 'Failed to get API keys');
    }
  }

  async updateApiKey(
    keyId: string,
    userId: string,
    updates: Partial<ApiKeyKeyUpdate>,
    organizationId?: string
  ): Promise<ApiKeyData> {
    try {
      // Check if user owns the key or has admin permissions
      const keyData = await this.getApiKeyById(keyId);
      if (!keyData) {
        throw new ApiError(404, 'API key not found');
      }

      if (keyData.user_id !== userId) {
        // Check if user has admin permissions in the organization
        if (!organizationId || keyData.organization_id !== organizationId) {
          throw new ApiError(403, 'Access denied');
        }

        // This would require authorization service check
        // await this.authService.requirePermission(userId, Permission.API_UPDATE_KEYS, organizationId);
      }

      // Update API key
      const updateData = {
        ...updates,
        updated_at: new Date(),
      };

      await db('api_keys').where('id', keyId).update(updateData);

      const updatedKey = await this.getApiKeyById(keyId);

      // Emit event for audit trail
      await this.emitEvent('API_KEY.UPDATED', {
        keyId,
        userId,
        updates: Object.keys(updates),
        organizationId,
      });

      logger.info('API key updated', {
        keyId,
        userId,
        updates: Object.keys(updates),
      });

      return updatedKey!;
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to update API key', { error, keyId, userId });
      throw new ApiError(500, 'Failed to update API key');
    }
  }

  async revokeApiKey(keyId: string, userId: string, organizationId?: string): Promise<void> {
    try {
      const keyData = await this.getApiKeyById(keyId);
      if (!keyData) {
        throw new ApiError(404, 'API key not found');
      }

      if (keyData.user_id !== userId) {
        // Check admin permissions
        if (!organizationId || keyData.organization_id !== organizationId) {
          throw new ApiError(403, 'Access denied');
        }

        // This would require authorization service check
        // await this.authService.requirePermission(userId, Permission.API_DELETE_KEYS, organizationId);
      }

      await this.updateKeyStatus(keyId, 'revoked');

      // Emit event for audit trail
      await this.emitEvent('API_KEY.REVOKED', {
        keyId,
        userId,
        organizationId,
      });

      logger.info('API key revoked', {
        keyId,
        userId,
      });
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to revoke API key', { error, keyId, userId });
      throw new ApiError(500, 'Failed to revoke API key');
    }
  }

  private generateSecureKey(): string {
    const prefix = 'tp_'; // Test Platform prefix
    const key = randomBytes(this.KEY_LENGTH).toString('base64url');
    return prefix + key;
  }

  private hashApiKey(apiKey: string): string {
    const crypto = require('crypto');
    return crypto.createHash('sha256').update(apiKey).digest('hex');
  }

  private async verifyApiKeyHash(apiKey: string, hash: string): Promise<boolean> {
    const computedHash = this.hashApiKey(apiKey);
    return computedHash === hash;
  }

  private async getApiKeyById(keyId: string): Promise<ApiKeyData | null> {
    return await db('api_keys').where('id', keyId).first();
  }

  private async updateKeyStatus(keyId: string, status: string): Promise<void> {
    await db('api_keys').where('id', keyId).update({
      status,
      updated_at: new Date(),
    });
  }

  private async updateUsageStats(
    keyId: string,
    context?: {
      ipAddress?: string;
      userAgent?: string;
    }
  ): Promise<void> {
    await db('api_keys').where('id', keyId).increment('usage_count', 1).update({
      last_used_at: new Date(),
      last_used_ip: context?.ipAddress,
      last_used_user_agent: context?.userAgent,
      updated_at: new Date(),
    });
  }

  private async recordFailedAttempt(
    keyId: string,
    ipAddress?: string,
    reason?: string
  ): Promise<void> {
    // This could be implemented to track failed attempts for security monitoring
    logger.warn('API key failed attempt', {
      keyId,
      ipAddress,
      reason,
    });
  }

  private async emitEvent(eventType: string, data: any): Promise<void> {
    // Implementation would emit event to events table
    logger.info('API key event emitted', { eventType, data });
  }
}

interface ApiKeyKeyUpdate {
  name?: string;
  description?: string;
  permissions?: string[];
  scopes?: string[];
  allowedIps?: string[];
  allowedDomains?: string[];
  expiresAt?: Date;
  usageLimit?: number;
  requireMfa?: boolean;
  status?: string;
}
```

### Subtask 5.2: Implement API key authentication middleware

**Objective**: Create middleware for API key authentication

**File**: `src/middleware/api-key-auth.ts`

```typescript
import { Request, Response, NextFunction } from 'express';
import { ApiKeyService } from '../services/api-key-service';
import { AuthorizationService } from '../services/authorization-service';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface ApiKeyAuthOptions {
  required?: boolean;
  checkPermissions?: boolean[];
  checkScopes?: string[];
  rateLimitPerHour?: number;
}

export class ApiKeyAuthMiddleware {
  constructor(
    private apiKeyService: ApiKeyService,
    private authService: AuthorizationService
  ) {}

  // Main API key authentication middleware
  authenticate(options: ApiKeyAuthOptions = {}) {
    return async (req: Request, res: Response, next: NextFunction): Promise<void> => {
      try {
        const apiKey = this.extractApiKey(req);

        if (!apiKey) {
          if (options.required) {
            throw new ApiError(401, 'API key is required');
          }
          return next();
        }

        // Get client context for validation
        const context = {
          ipAddress: req.ip,
          userAgent: req.get('User-Agent'),
          domain: this.getDomainFromRequest(req),
        };

        // Validate API key
        const validation = await this.apiKeyService.validateApiKey(apiKey, context);

        if (!validation.isValid) {
          throw new ApiError(401, validation.error || 'Invalid API key');
        }

        const keyData = validation.keyData!;

        // Check permissions if required
        if (options.checkPermissions && options.checkPermissions.length > 0) {
          await this.checkKeyPermissions(keyData, options.checkPermissions);
        }

        // Check scopes if required
        if (options.checkScopes && options.checkScopes.length > 0) {
          await this.checkKeyScopes(keyData, options.checkScopes);
        }

        // Apply rate limiting if configured
        if (options.rateLimitPerHour) {
          await this.checkRateLimit(keyData.id, options.rateLimitPerHour);
        }

        // Add API key info to request
        req.apiKey = {
          id: keyData.id,
          keyId: keyData.keyId,
          userId: keyData.userId,
          organizationId: keyData.organizationId,
          permissions: keyData.permissions,
          scopes: keyData.scopes,
          keyType: keyData.keyType,
        };

        // Add user info to request for compatibility with JWT auth
        req.user = {
          sub: keyData.userId,
          email: '', // Would need to fetch from user table
          organizationId: keyData.organizationId,
          permissions: keyData.permissions,
        };

        logger.debug('API key authentication successful', {
          keyId: keyData.keyId,
          userId: keyData.userId,
          organizationId: keyData.organizationId,
          ipAddress: req.ip,
        });

        next();
      } catch (error) {
        if (error instanceof ApiError) {
          res.status(error.statusCode).json({
            error: error.message,
            type: 'api_key_auth_error',
          });
        } else {
          logger.error('API key authentication error', { error });
          res.status(500).json({ error: 'Authentication failed' });
        }
      }
    };
  }

  // Middleware to require specific API key permissions
  requirePermissions(permissions: string[]) {
    return this.authenticate({
      required: true,
      checkPermissions: permissions,
    });
  }

  // Middleware to require specific API key scopes
  requireScopes(scopes: string[]) {
    return this.authenticate({
      required: true,
      checkScopes: scopes,
    });
  }

  // Middleware with rate limiting
  withRateLimit(requestsPerHour: number) {
    return this.authenticate({
      required: true,
      rateLimitPerHour: requestsPerHour,
    });
  }

  // Optional API key authentication (doesn't fail if no key provided)
  optional() {
    return this.authenticate({
      required: false,
    });
  }

  private extractApiKey(req: Request): string | null {
    // Check Authorization header (Bearer token)
    const authHeader = req.headers.authorization;
    if (authHeader && authHeader.startsWith('Bearer ')) {
      return authHeader.substring(7);
    }

    // Check X-API-Key header
    const apiKeyHeader = req.headers['x-api-key'];
    if (typeof apiKeyHeader === 'string') {
      return apiKeyHeader;
    }

    // Check query parameter (less secure, but sometimes needed)
    if (req.query.api_key && typeof req.query.api_key === 'string') {
      return req.query.api_key;
    }

    return null;
  }

  private getDomainFromRequest(req: Request): string | undefined {
    // Check Origin header
    const origin = req.headers.origin;
    if (origin) {
      try {
        return new URL(origin).hostname;
      } catch {
        // Invalid URL, continue
      }
    }

    // Check Referer header
    const referer = req.headers.referer;
    if (referer) {
      try {
        return new URL(referer).hostname;
      } catch {
        // Invalid URL, continue
      }
    }

    // Check Host header
    const host = req.headers.host;
    if (host) {
      return host.split(':')[0]; // Remove port if present
    }

    return undefined;
  }

  private async checkKeyPermissions(keyData: any, requiredPermissions: string[]): Promise<void> {
    const keyPermissions = keyData.permissions || [];

    for (const permission of requiredPermissions) {
      if (!keyPermissions.includes(permission)) {
        throw new ApiError(403, `API key lacks required permission: ${permission}`);
      }
    }
  }

  private async checkKeyScopes(keyData: any, requiredScopes: string[]): Promise<void> {
    const keyScopes = keyData.scopes || [];

    for (const scope of requiredScopes) {
      if (!keyScopes.includes(scope)) {
        throw new ApiError(403, `API key lacks required scope: ${scope}`);
      }
    }
  }

  private async checkRateLimit(keyId: string, requestsPerHour: number): Promise<void> {
    // This would integrate with a rate limiting service (Redis-based)
    // For now, just log the check
    logger.debug('API key rate limit check', {
      keyId,
      requestsPerHour,
    });
  }
}

// Extend Express Request interface
declare global {
  namespace Express {
    interface Request {
      apiKey?: {
        id: string;
        keyId: string;
        userId: string;
        organizationId?: string;
        permissions: string[];
        scopes: string[];
        keyType: string;
      };
      user?: {
        sub: string;
        email: string;
        organizationId?: string;
        permissions?: string[];
      };
    }
  }
}
```

### Subtask 5.3: Add API key management endpoints

**Objective**: Create REST endpoints for API key CRUD operations

**File**: `src/controllers/api-key-controller.ts`

```typescript
import { Request, Response } from 'express';
import { body, param, validationResult } from 'express-validator';
import { ApiKeyService } from '../services/api-key-service';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export class ApiKeyController {
  constructor(private apiKeyService: ApiKeyService) {}

  // Validation middleware
  static createApiKeyValidation = [
    body('name')
      .trim()
      .isLength({ min: 1, max: 255 })
      .withMessage('Name is required and must be less than 255 characters'),
    body('description')
      .optional()
      .trim()
      .isLength({ max: 1000 })
      .withMessage('Description must be less than 1000 characters'),
    body('keyType')
      .optional()
      .isIn(['personal', 'service', 'integration'])
      .withMessage('Key type must be personal, service, or integration'),
    body('permissions').optional().isArray().withMessage('Permissions must be an array'),
    body('scopes').optional().isArray().withMessage('Scopes must be an array'),
    body('allowedIps').optional().isArray().withMessage('Allowed IPs must be an array'),
    body('allowedDomains').optional().isArray().withMessage('Allowed domains must be an array'),
    body('expiresAt').optional().isISO8601().withMessage('Expiry date must be a valid date'),
    body('usageLimit')
      .optional()
      .isInt({ min: 1 })
      .withMessage('Usage limit must be a positive integer'),
    body('requireMfa').optional().isBoolean().withMessage('Require MFA must be a boolean'),
  ];

  static updateApiKeyValidation = [
    param('keyId').isUUID().withMessage('Valid key ID is required'),
    body('name')
      .optional()
      .trim()
      .isLength({ min: 1, max: 255 })
      .withMessage('Name must be less than 255 characters'),
    body('description')
      .optional()
      .trim()
      .isLength({ max: 1000 })
      .withMessage('Description must be less than 1000 characters'),
    body('permissions').optional().isArray().withMessage('Permissions must be an array'),
    body('scopes').optional().isArray().withMessage('Scopes must be an array'),
    body('allowedIps').optional().isArray().withMessage('Allowed IPs must be an array'),
    body('allowedDomains').optional().isArray().withMessage('Allowed domains must be an array'),
    body('expiresAt').optional().isISO8601().withMessage('Expiry date must be a valid date'),
    body('usageLimit')
      .optional()
      .isInt({ min: 1 })
      .withMessage('Usage limit must be a positive integer'),
    body('requireMfa').optional().isBoolean().withMessage('Require MFA must be a boolean'),
    body('status')
      .optional()
      .isIn(['active', 'inactive'])
      .withMessage('Status must be active or inactive'),
  ];

  async createApiKey(req: Request, res: Response): Promise<void> {
    try {
      // Check validation errors
      const errors = validationResult(req);
      if (!errors.isEmpty()) {
        throw new ApiError(400, 'Validation failed', errors.array());
      }

      const userId = req.user!.sub;
      const organizationId = req.user?.organizationId;
      const {
        name,
        description,
        keyType,
        permissions,
        scopes,
        allowedIps,
        allowedDomains,
        expiresAt,
        usageLimit,
        requireMfa,
      } = req.body;

      // Generate API key
      const result = await this.apiKeyService.generateApiKey(
        userId,
        {
          name,
          description,
          keyType,
          permissions,
          scopes,
          allowedIps: allowedIps?.join(','),
          allowedDomains: allowedDomains?.join(','),
          expiresAt: expiresAt ? new Date(expiresAt) : undefined,
          usageLimit,
          requireMfa,
        },
        organizationId,
        {
          ipAddress: req.ip,
          userAgent: req.get('User-Agent'),
        }
      );

      logger.info('API key created via API', {
        keyId: result.keyData.id,
        userId,
        organizationId,
        name,
      });

      res.status(201).json({
        message: 'API key created successfully',
        apiKey: result.apiKey, // Only return the full key once
        keyData: {
          id: result.keyData.id,
          keyId: result.keyData.keyId,
          keyPrefix: result.keyData.keyPrefix,
          name: result.keyData.name,
          description: result.keyData.description,
          keyType: result.keyData.keyType,
          permissions: result.keyData.permissions,
          scopes: result.keyData.scopes,
          allowedIps: result.keyData.allowedIps?.split(','),
          allowedDomains: result.keyData.allowedDomains?.split(','),
          status: result.keyData.status,
          expiresAt: result.keyData.expiresAt,
          usageLimit: result.keyData.usageLimit,
          requireMfa: result.keyData.requireMfa,
          createdAt: result.keyData.createdAt,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
          details: error.details,
        });
      } else {
        logger.error('Create API key error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async getApiKeys(req: Request, res: Response): Promise<void> {
    try {
      const userId = req.user!.sub;
      const organizationId = req.user?.organizationId;

      const apiKeys = await this.apiKeyService.getUserApiKeys(userId, organizationId);

      res.json({
        apiKeys: apiKeys.map((key) => ({
          id: key.id,
          keyId: key.keyId,
          keyPrefix: key.keyPrefix,
          name: key.name,
          description: key.description,
          keyType: key.keyType,
          permissions: key.permissions,
          scopes: key.scopes,
          allowedIps: key.allowedIps?.split(','),
          allowedDomains: key.allowedDomains?.split(','),
          status: key.status,
          expiresAt: key.expiresAt,
          usageCount: key.usageCount,
          usageLimit: key.usageLimit,
          requireMfa: key.requireMfa,
          createdAt: key.createdAt,
          updatedAt: key.updatedAt,
          lastUsedAt: key.lastUsedAt,
          lastUsedIp: key.lastUsedIp,
          organizationId: key.organizationId,
        })),
      });
    } catch (error) {
      logger.error('Get API keys error', { error });
      res.status(500).json({ error: 'Internal server error' });
    }
  }

  async getApiKey(req: Request, res: Response): Promise<void> {
    try {
      const { keyId } = req.params;
      const userId = req.user!.sub;
      const organizationId = req.user?.organizationId;

      const apiKeys = await this.apiKeyService.getUserApiKeys(userId, organizationId);
      const apiKey = apiKeys.find((key) => key.id === keyId);

      if (!apiKey) {
        throw new ApiError(404, 'API key not found');
      }

      res.json({
        apiKey: {
          id: apiKey.id,
          keyId: apiKey.keyId,
          keyPrefix: apiKey.keyPrefix,
          name: apiKey.name,
          description: apiKey.description,
          keyType: apiKey.keyType,
          permissions: apiKey.permissions,
          scopes: apiKey.scopes,
          allowedIps: apiKey.allowedIps?.split(','),
          allowedDomains: apiKey.allowedDomains?.split(','),
          status: apiKey.status,
          expiresAt: apiKey.expiresAt,
          usageCount: apiKey.usageCount,
          usageLimit: apiKey.usageLimit,
          requireMfa: apiKey.requireMfa,
          createdAt: apiKey.createdAt,
          updatedAt: apiKey.updatedAt,
          lastUsedAt: apiKey.lastUsedAt,
          lastUsedIp: apiKey.lastUsedIp,
          organizationId: apiKey.organizationId,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Get API key error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async updateApiKey(req: Request, res: Response): Promise<void> {
    try {
      // Check validation errors
      const errors = validationResult(req);
      if (!errors.isEmpty()) {
        throw new ApiError(400, 'Validation failed', errors.array());
      }

      const { keyId } = req.params;
      const userId = req.user!.sub;
      const organizationId = req.user?.organizationId;
      const updates = req.body;

      // Convert arrays to strings for storage
      if (updates.allowedIps) {
        updates.allowedIps = updates.allowedIps.join(',');
      }
      if (updates.allowedDomains) {
        updates.allowedDomains = updates.allowedDomains.join(',');
      }
      if (updates.expiresAt) {
        updates.expiresAt = new Date(updates.expiresAt);
      }

      const updatedKey = await this.apiKeyService.updateApiKey(
        keyId,
        userId,
        updates,
        organizationId
      );

      logger.info('API key updated via API', {
        keyId,
        userId,
        organizationId,
        updates: Object.keys(updates),
      });

      res.json({
        message: 'API key updated successfully',
        apiKey: {
          id: updatedKey.id,
          keyId: updatedKey.keyId,
          keyPrefix: updatedKey.keyPrefix,
          name: updatedKey.name,
          description: updatedKey.description,
          keyType: updatedKey.keyType,
          permissions: updatedKey.permissions,
          scopes: updatedKey.scopes,
          allowedIps: updatedKey.allowedIps?.split(','),
          allowedDomains: updatedKey.allowedDomains?.split(','),
          status: updatedKey.status,
          expiresAt: updatedKey.expiresAt,
          usageCount: updatedKey.usageCount,
          usageLimit: updatedKey.usageLimit,
          requireMfa: updatedKey.requireMfa,
          createdAt: updatedKey.createdAt,
          updatedAt: updatedKey.updatedAt,
          lastUsedAt: updatedKey.lastUsedAt,
          lastUsedIp: updatedKey.lastUsedIp,
          organizationId: updatedKey.organizationId,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Update API key error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }

  async revokeApiKey(req: Request, res: Response): Promise<void> {
    try {
      const { keyId } = req.params;
      const userId = req.user!.sub;
      const organizationId = req.user?.organizationId;

      await this.apiKeyService.revokeApiKey(keyId, userId, organizationId);

      logger.info('API key revoked via API', {
        keyId,
        userId,
        organizationId,
      });

      res.json({
        message: 'API key revoked successfully',
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
        });
      } else {
        logger.error('Revoke API key error', { error });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }
}
```

### Subtask 5.4: Implement API key usage tracking and rate limiting

**Objective**: Create comprehensive usage tracking and rate limiting system

**File**: `src/services/api-key-usage-service.ts`

```typescript
import Redis from 'ioredis';
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface UsageStats {
  totalRequests: number;
  requestsThisHour: number;
  requestsToday: number;
  requestsThisMonth: number;
  lastUsedAt?: Date;
  topEndpoints: Array<{
    endpoint: string;
    count: number;
  }>;
  errorRate: number;
  averageResponseTime: number;
}

export class ApiKeyUsageService {
  private redis: Redis;

  constructor(redisUrl?: string) {
    this.redis = new Redis(redisUrl || process.env.REDIS_URL);

    this.redis.on('error', (error) => {
      logger.error('Redis connection error', { error });
    });
  }

  async trackUsage(
    keyId: string,
    endpoint: string,
    statusCode: number,
    responseTime: number,
    context?: {
      ipAddress?: string;
      userAgent?: string;
    }
  ): Promise<void> {
    try {
      const now = new Date();
      const hourKey = this.getHourKey(keyId);
      const dayKey = this.getDayKey(keyId);
      const monthKey = this.getMonthKey(keyId);

      // Increment counters in Redis
      const pipeline = this.redis.pipeline();

      // Hourly counter
      pipeline.incr(hourKey);
      pipeline.expire(hourKey, 2 * 60 * 60); // 2 hours

      // Daily counter
      pipeline.incr(dayKey);
      pipeline.expire(dayKey, 25 * 60 * 60); // 25 hours

      // Monthly counter
      pipeline.incr(monthKey);
      pipeline.expire(monthKey, 32 * 24 * 60 * 60); // 32 days

      // Track endpoints
      const endpointKey = `api_key:${keyId}:endpoints`;
      pipeline.hincrby(endpointKey, endpoint, 1);
      pipeline.expire(endpointKey, 7 * 24 * 60 * 60); // 7 days

      // Track errors
      if (statusCode >= 400) {
        const errorKey = `api_key:${keyId}:errors`;
        pipeline.incr(errorKey);
        pipeline.expire(errorKey, 24 * 60 * 60); // 24 hours
      }

      // Track response times
      const responseTimeKey = `api_key:${keyId}:response_times`;
      pipeline.lpush(responseTimeKey, responseTime.toString());
      pipeline.ltrim(responseTimeKey, 0, 999); // Keep last 1000 response times
      pipeline.expire(responseTimeKey, 24 * 60 * 60); // 24 hours

      await pipeline.exec();

      // Update database record
      await this.updateDatabaseUsage(keyId, endpoint, statusCode, responseTime, context);

      logger.debug('API key usage tracked', {
        keyId,
        endpoint,
        statusCode,
        responseTime,
      });
    } catch (error) {
      logger.error('Failed to track API key usage', { error, keyId, endpoint });
      // Don't throw error to avoid impacting the main request flow
    }
  }

  async checkRateLimit(keyId: string, limitPerHour: number): Promise<boolean> {
    try {
      const hourKey = this.getHourKey(keyId);
      const currentUsage = await this.redis.get(hourKey);

      const usage = parseInt(currentUsage || '0');

      if (usage >= limitPerHour) {
        logger.warn('API key rate limit exceeded', {
          keyId,
          usage,
          limit: limitPerHour,
        });
        return false;
      }

      return true;
    } catch (error) {
      logger.error('Failed to check rate limit', { error, keyId });
      // Fail open - allow request if rate limiting fails
      return true;
    }
  }

  async getUsageStats(keyId: string): Promise<UsageStats> {
    try {
      const hourKey = this.getHourKey(keyId);
      const dayKey = this.getDayKey(keyId);
      const monthKey = this.getMonthKey(keyId);

      // Get usage counters
      const [hourly, daily, monthly] = await Promise.all([
        this.redis.get(hourKey),
        this.redis.get(dayKey),
        this.redis.get(monthKey),
      ]);

      // Get top endpoints
      const endpointKey = `api_key:${keyId}:endpoints`;
      const endpoints = await this.redis.hgetall(endpointKey);
      const topEndpoints = Object.entries(endpoints || {})
        .map(([endpoint, count]) => ({
          endpoint,
          count: parseInt(count),
        }))
        .sort((a, b) => b.count - a.count)
        .slice(0, 10);

      // Get error rate
      const errorKey = `api_key:${keyId}:errors`;
      const errors = await this.redis.get(errorKey);
      const totalRequests = parseInt(hourly || '0');
      const errorCount = parseInt(errors || '0');
      const errorRate = totalRequests > 0 ? (errorCount / totalRequests) * 100 : 0;

      // Get average response time
      const responseTimeKey = `api_key:${keyId}:response_times`;
      const responseTimes = await this.redis.lrange(responseTimeKey, 0, -1);
      const averageResponseTime =
        responseTimes.length > 0
          ? responseTimes.reduce((sum, time) => sum + parseFloat(time), 0) / responseTimes.length
          : 0;

      // Get last used timestamp from database
      const apiKey = await db('api_keys').where('id', keyId).first('last_used_at');

      return {
        totalRequests: parseInt(monthly || '0'),
        requestsThisHour: parseInt(hourly || '0'),
        requestsToday: parseInt(daily || '0'),
        requestsThisMonth: parseInt(monthly || '0'),
        lastUsedAt: apiKey?.last_used_at,
        topEndpoints,
        errorRate,
        averageResponseTime,
      };
    } catch (error) {
      logger.error('Failed to get usage stats', { error, keyId });
      throw new ApiError(500, 'Failed to get usage statistics');
    }
  }

  async resetUsageCounters(keyId: string): Promise<void> {
    try {
      const keys = await this.redis.keys(`api_key:${keyId}:*`);

      if (keys.length > 0) {
        await this.redis.del(...keys);
        logger.info('API key usage counters reset', { keyId, deletedKeys: keys.length });
      }
    } catch (error) {
      logger.error('Failed to reset usage counters', { error, keyId });
      throw new ApiError(500, 'Failed to reset usage counters');
    }
  }

  async getTopApiKeys(limit: number = 10): Promise<
    Array<{
      keyId: string;
      keyPrefix: string;
      usage: number;
    }>
  > {
    try {
      const keys = await this.redis.keys('api_key_usage:*:hour');

      const usagePromises = keys.map(async (key) => {
        const usage = await this.redis.get(key);
        const keyId = key.split(':')[2];

        // Get key prefix from database
        const apiKey = await db('api_keys').where('id', keyId).first('key_prefix');

        return {
          keyId,
          keyPrefix: apiKey?.key_prefix || 'unknown',
          usage: parseInt(usage || '0'),
        };
      });

      const results = await Promise.all(usagePromises);

      return results.sort((a, b) => b.usage - a.usage).slice(0, limit);
    } catch (error) {
      logger.error('Failed to get top API keys', { error });
      return [];
    }
  }

  private async updateDatabaseUsage(
    keyId: string,
    endpoint: string,
    statusCode: number,
    responseTime: number,
    context?: {
      ipAddress?: string;
      userAgent?: string;
    }
  ): Promise<void> {
    try {
      await db('api_keys').where('id', keyId).increment('usage_count', 1).update({
        last_used_at: new Date(),
        last_used_ip: context?.ipAddress,
        last_used_user_agent: context?.userAgent,
        updated_at: new Date(),
      });

      // Store detailed usage record for analytics
      await db('api_key_usage_logs').insert({
        api_key_id: keyId,
        endpoint,
        status_code: statusCode,
        response_time: responseTime,
        ip_address: context?.ipAddress,
        user_agent: context?.user_agent,
        created_at: new Date(),
      });
    } catch (error) {
      logger.error('Failed to update database usage', { error, keyId });
    }
  }

  private getHourKey(keyId: string): string {
    const now = new Date();
    const hour = now.getHours();
    const date = now.toISOString().split('T')[0];
    return `api_key_usage:${keyId}:${date}:hour:${hour}`;
  }

  private getDayKey(keyId: string): string {
    const date = new Date().toISOString().split('T')[0];
    return `api_key_usage:${keyId}:${date}:day`;
  }

  private getMonthKey(keyId: string): string {
    const now = new Date();
    const year = now.getFullYear();
    const month = now.getMonth() + 1;
    return `api_key_usage:${keyId}:${year}:month:${month}`;
  }
}
```

## Files to Create

1. `src/services/api-key-service.ts` - API key generation and management
2. `src/middleware/api-key-auth.ts` - API key authentication middleware
3. `src/controllers/api-key-controller.ts` - API key management endpoints
4. `src/services/api-key-usage-service.ts` - Usage tracking and rate limiting
5. Database migration for api_key_usage_logs table

## Dependencies

- crypto for secure key generation
- Redis for rate limiting and usage tracking
- Express.js for middleware and controllers
- Database connection for persistent storage

## Testing

1. API key generation and validation tests
2. Authentication middleware tests
3. Rate limiting tests
4. Usage tracking tests
5. Security tests for key exposure and replay attacks

## Notes

- Never return the full API key after creation
- Implement proper key rotation policies
- Monitor usage patterns for anomaly detection
- Use secure key generation with sufficient entropy
- Consider implementing key expiration and renewal
- Add comprehensive audit logging for all key operations
