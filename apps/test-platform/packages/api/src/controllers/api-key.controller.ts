/**
 * API Key Controller
 * Handles API key management endpoints
 */

import { Context } from 'hono';
import { z } from 'zod';
import { zValidator } from '@hono/zod-validator';
import { apiKeyService } from '../services/api-key-service';
import { logger } from '../../../../src/observability/logger';

// Validation schemas
const createApiKeySchema = z.object({
  name: z.string().min(1).max(255),
  description: z.string().max(1000).optional(),
  keyType: z.enum(['personal', 'service', 'integration']).optional(),
  permissions: z.array(z.string()).optional(),
  scopes: z.array(z.string()).optional(),
  allowedIps: z.array(z.string()).optional(),
  allowedDomains: z.array(z.string()).optional(),
  expiresAt: z.iso.datetime().optional(),
  usageLimit: z.number().int().positive().optional(),
  requireMfa: z.boolean().optional(),
});

const updateApiKeySchema = z.object({
  name: z.string().min(1).max(255).optional(),
  description: z.string().max(1000).optional(),
  permissions: z.array(z.string()).optional(),
  scopes: z.array(z.string()).optional(),
  allowedIps: z.array(z.string()).optional(),
  allowedDomains: z.array(z.string()).optional(),
  expiresAt: z.iso.datetime().optional(),
  usageLimit: z.number().int().positive().optional(),
  requireMfa: z.boolean().optional(),
  status: z.enum(['active', 'inactive']).optional(),
});

/**
 * Create a new API key
 */
export const createApiKey = zValidator('json', createApiKeySchema, async (result, c: Context) => {
  if (!result.success) {
    return c.json(
      {
        error: 'Validation failed',
        details: result.error.flatten()
      },
      400
    );
  }

  try {
    const user = c.get('user');
    if (!user) {
      return c.json({ error: 'Unauthorized' }, 401);
    }

    const userId = user.sub;
    const organizationId = user.organizationId;
    const data = result.data;

    // Get request context
    const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown';
    const userAgent = c.req.header('user-agent');

    // Generate API key
    const { apiKey, keyData } = await apiKeyService.generateApiKey(
      userId,
      {
        name: data.name,
        description: data.description,
        keyType: data.keyType,
        permissions: data.permissions,
        scopes: data.scopes,
        allowedIps: data.allowedIps,
        allowedDomains: data.allowedDomains,
        expiresAt: data.expiresAt ? new Date(data.expiresAt) : undefined,
        usageLimit: data.usageLimit,
        requireMfa: data.requireMfa,
      },
      organizationId,
      {
        ipAddress,
        userAgent,
      }
    );

    logger.info('API key created via API', {
      keyId: keyData.id,
      userId,
      organizationId,
      name: data.name,
    });

    return c.json({
      message: 'API key created successfully',
      apiKey, // Only return the full key once
      keyData: {
        id: keyData.id,
        keyId: keyData.key_id,
        keyPrefix: keyData.key_prefix,
        name: keyData.name,
        description: keyData.description,
        keyType: keyData.key_type,
        permissions: keyData.permissions,
        scopes: keyData.scopes,
        allowedIps: keyData.allowed_ips?.split(',').filter(Boolean),
        allowedDomains: keyData.allowed_domains?.split(',').filter(Boolean),
        status: keyData.status,
        expiresAt: keyData.expires_at,
        usageLimit: keyData.usage_limit,
        requireMfa: keyData.require_mfa,
        createdAt: keyData.created_at,
      },
    }, 201);
  } catch (error) {
    logger.error('Create API key error', { error });
    return c.json({ error: 'Failed to create API key' }, 500);
  }
});

/**
 * Get all API keys for the authenticated user
 */
export const getApiKeys = async (c: Context) => {
  try {
    const user = c.get('user');
    if (!user) {
      return c.json({ error: 'Unauthorized' }, 401);
    }

    const userId = user.sub;
    const organizationId = user.organizationId;

    const apiKeys = await apiKeyService.getUserApiKeys(userId, organizationId);

    return c.json({
      apiKeys: apiKeys.map((key) => ({
        id: key.id,
        keyId: key.key_id,
        keyPrefix: key.key_prefix,
        name: key.name,
        description: key.description,
        keyType: key.key_type,
        permissions: key.permissions,
        scopes: key.scopes,
        allowedIps: key.allowed_ips?.split(',').filter(Boolean),
        allowedDomains: key.allowed_domains?.split(',').filter(Boolean),
        status: key.status,
        expiresAt: key.expires_at,
        usageCount: key.usage_count,
        usageLimit: key.usage_limit,
        requireMfa: key.require_mfa,
        createdAt: key.created_at,
        updatedAt: key.updated_at,
        lastUsedAt: key.last_used_at,
        lastUsedIp: key.last_used_ip,
        organizationId: key.organization_id,
      })),
    });
  } catch (error) {
    logger.error('Get API keys error', { error });
    return c.json({ error: 'Failed to get API keys' }, 500);
  }
};

/**
 * Get a specific API key
 */
export const getApiKey = async (c: Context) => {
  try {
    const user = c.get('user');
    if (!user) {
      return c.json({ error: 'Unauthorized' }, 401);
    }

    const keyId = c.req.param('keyId');
    const userId = user.sub;
    const organizationId = user.organizationId;

    const apiKeys = await apiKeyService.getUserApiKeys(userId, organizationId);
    const apiKey = apiKeys.find((key) => key.id === keyId);

    if (!apiKey) {
      return c.json({ error: 'API key not found' }, 404);
    }

    return c.json({
      apiKey: {
        id: apiKey.id,
        keyId: apiKey.key_id,
        keyPrefix: apiKey.key_prefix,
        name: apiKey.name,
        description: apiKey.description,
        keyType: apiKey.key_type,
        permissions: apiKey.permissions,
        scopes: apiKey.scopes,
        allowedIps: apiKey.allowed_ips?.split(',').filter(Boolean),
        allowedDomains: apiKey.allowed_domains?.split(',').filter(Boolean),
        status: apiKey.status,
        expiresAt: apiKey.expires_at,
        usageCount: apiKey.usage_count,
        usageLimit: apiKey.usage_limit,
        requireMfa: apiKey.require_mfa,
        createdAt: apiKey.created_at,
        updatedAt: apiKey.updated_at,
        lastUsedAt: apiKey.last_used_at,
        lastUsedIp: apiKey.last_used_ip,
        organizationId: apiKey.organization_id,
      },
    });
  } catch (error) {
    logger.error('Get API key error', { error });
    return c.json({ error: 'Failed to get API key' }, 500);
  }
};

/**
 * Update an API key
 */
export const updateApiKey = zValidator('json', updateApiKeySchema, async (result, c: Context) => {
  if (!result.success) {
    return c.json(
      {
        error: 'Validation failed',
        details: result.error.flatten()
      },
      400
    );
  }

  try {
    const user = c.get('user');
    if (!user) {
      return c.json({ error: 'Unauthorized' }, 401);
    }

    const keyId = c.req.param('keyId');
    const userId = user.sub;
    const organizationId = user.organizationId;
    const updates = result.data;

    // Convert expiry date if provided
    const updateData: any = { ...updates };
    if (updates.expiresAt) {
      updateData.expiresAt = new Date(updates.expiresAt);
    }

    const updatedKey = await apiKeyService.updateApiKey(
      keyId,
      userId,
      updateData,
      organizationId
    );

    logger.info('API key updated via API', {
      keyId,
      userId,
      organizationId,
      updates: Object.keys(updates),
    });

    return c.json({
      message: 'API key updated successfully',
      apiKey: {
        id: updatedKey.id,
        keyId: updatedKey.key_id,
        keyPrefix: updatedKey.key_prefix,
        name: updatedKey.name,
        description: updatedKey.description,
        keyType: updatedKey.key_type,
        permissions: updatedKey.permissions,
        scopes: updatedKey.scopes,
        allowedIps: updatedKey.allowed_ips?.split(',').filter(Boolean),
        allowedDomains: updatedKey.allowed_domains?.split(',').filter(Boolean),
        status: updatedKey.status,
        expiresAt: updatedKey.expires_at,
        usageCount: updatedKey.usage_count,
        usageLimit: updatedKey.usage_limit,
        requireMfa: updatedKey.require_mfa,
        createdAt: updatedKey.created_at,
        updatedAt: updatedKey.updated_at,
        lastUsedAt: updatedKey.last_used_at,
        lastUsedIp: updatedKey.last_used_ip,
        organizationId: updatedKey.organization_id,
      },
    });
  } catch (error) {
    logger.error('Update API key error', { error });
    return c.json({ error: 'Failed to update API key' }, 500);
  }
});

/**
 * Revoke an API key
 */
export const revokeApiKey = async (c: Context) => {
  try {
    const user = c.get('user');
    if (!user) {
      return c.json({ error: 'Unauthorized' }, 401);
    }

    const keyId = c.req.param('keyId');
    const userId = user.sub;
    const organizationId = user.organizationId;

    await apiKeyService.revokeApiKey(keyId, userId, organizationId);

    logger.info('API key revoked via API', {
      keyId,
      userId,
      organizationId,
    });

    return c.json({
      message: 'API key revoked successfully',
    });
  } catch (error) {
    logger.error('Revoke API key error', { error });
    return c.json({ error: 'Failed to revoke API key' }, 500);
  }
};

/**
 * Rotate an API key
 */
export const rotateApiKey = async (c: Context) => {
  try {
    const user = c.get('user');
    if (!user) {
      return c.json({ error: 'Unauthorized' }, 401);
    }

    const keyId = c.req.param('keyId');
    const userId = user.sub;
    const organizationId = user.organizationId;

    const { apiKey, keyData } = await apiKeyService.rotateApiKey(
      keyId,
      userId,
      organizationId
    );

    logger.info('API key rotated via API', {
      oldKeyId: keyId,
      newKeyId: keyData.id,
      userId,
      organizationId,
    });

    return c.json({
      message: 'API key rotated successfully',
      apiKey, // Return the new full key once
      keyData: {
        id: keyData.id,
        keyId: keyData.key_id,
        keyPrefix: keyData.key_prefix,
        name: keyData.name,
        description: keyData.description,
        keyType: keyData.key_type,
        permissions: keyData.permissions,
        scopes: keyData.scopes,
        allowedIps: keyData.allowed_ips?.split(',').filter(Boolean),
        allowedDomains: keyData.allowed_domains?.split(',').filter(Boolean),
        status: keyData.status,
        expiresAt: keyData.expires_at,
        usageLimit: keyData.usage_limit,
        requireMfa: keyData.require_mfa,
        createdAt: keyData.created_at,
      },
    });
  } catch (error) {
    logger.error('Rotate API key error', { error });
    return c.json({ error: 'Failed to rotate API key' }, 500);
  }
};