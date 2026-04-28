/**
 * API Key management routes
 * Handles API key generation, validation, and management
 */

import { Hono } from 'hono'
import { zValidator } from '@hono/zod-validator'
import { z } from 'zod'
import { APIKeyService } from '../services/api-key.service'
import { extractTokenFromHeader, verifyToken } from '../utils/jwt'
import type { HonoContext } from '../index'

const apiKeyRoutes = new Hono<HonoContext>()

// Validation schemas
const generateKeySchema = z.object({
  name: z.string().min(3).max(100),
  description: z.string().optional(),
  scopes: z.array(z.string()).min(1),
  rateLimit: z.number().int().min(0).max(1000000).optional().default(1000),
  expiresAt: z.iso.datetime().optional(),
  ipWhitelist: z.array(z.string()).optional(),
})

const updateKeySchema = z.object({
  name: z.string().min(3).max(100).optional(),
  description: z.string().optional(),
  scopes: z.array(z.string()).min(1).optional(),
  rateLimit: z.number().int().min(0).max(1000000).optional(),
  status: z.enum(['active', 'inactive']).optional(),
  ipWhitelist: z.array(z.string()).optional(),
})

const listKeysSchema = z.object({
  status: z.enum(['active', 'inactive', 'expired', 'revoked']).optional(),
  search: z.string().optional(),
  limit: z.string().transform(Number).pipe(z.number().int().min(1).max(100)).optional(),
  offset: z.string().transform(Number).pipe(z.number().int().min(0)).optional(),
})

const usageQuerySchema = z.object({
  startDate: z.iso.datetime().optional(),
  endDate: z.iso.datetime().optional(),
  limit: z.string().transform(Number).pipe(z.number().int().min(1).max(1000)).optional(),
  offset: z.string().transform(Number).pipe(z.number().int().min(0)).optional(),
})

/**
 * Authentication middleware - verifies JWT token
 */
async function requireAuth(c: HonoContext, next: Function) {
  const authHeader = c.req.header('Authorization')
  const token = extractTokenFromHeader(authHeader)

  if (!token) {
    return c.json({ error: 'Authentication required' }, 401)
  }

  const decoded = await verifyToken(token, c.env.JWT_SECRET)

  if (!decoded || decoded.type !== 'access') {
    return c.json({ error: 'Invalid access token' }, 401)
  }

  // Store user info in context
  c.set('user', decoded)
  await next()
}

/**
 * POST /api-keys
 * Generate a new API key
 */
apiKeyRoutes.post(
  '/',
  requireAuth,
  zValidator('json', generateKeySchema),
  async (c) => {
    try {
      const user = c.get('user')
      const { name, description, scopes, rateLimit, expiresAt, ipWhitelist } = c.req.valid('json')

      const apiKeyService = new APIKeyService({
        db: c.get('db'),
      })

      const { key, plainKey } = await apiKeyService.generateKey({
        userId: user.userId,
        name,
        description,
        scopes,
        rateLimit,
        expiresAt: expiresAt ? new Date(expiresAt) : undefined,
        ipWhitelist,
      })

      // Remove sensitive data from response
      const { keyHash, ...keyResponse } = key

      return c.json(
        {
          message: 'API key generated successfully',
          key: keyResponse,
          // Include the plain key only once - user must save it
          apiKey: plainKey,
          warning: 'Please save this API key. You will not be able to see it again.',
        },
        201
      )
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to generate API key'
      return c.json({ error: message }, 400)
    }
  }
)

/**
 * GET /api-keys
 * List all API keys for the authenticated user
 */
apiKeyRoutes.get(
  '/',
  requireAuth,
  zValidator('query', listKeysSchema),
  async (c) => {
    try {
      const user = c.get('user')
      const { status, search, limit = 20, offset = 0 } = c.req.valid('query')

      const apiKeyService = new APIKeyService({
        db: c.get('db'),
      })

      const { keys, total } = await apiKeyService.listKeys({
        userId: user.userId,
        status,
        search,
        limit,
        offset,
      })

      // Remove sensitive data from response
      const sanitizedKeys = keys.map(({ keyHash, ...key }) => key)

      return c.json({
        keys: sanitizedKeys,
        total,
        limit,
        offset,
        hasMore: offset + limit < total,
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to list API keys'
      return c.json({ error: message }, 500)
    }
  }
)

/**
 * GET /api-keys/:id
 * Get details of a specific API key
 */
apiKeyRoutes.get(
  '/:id',
  requireAuth,
  async (c) => {
    try {
      const user = c.get('user')
      const keyId = c.req.param('id')

      const apiKeyService = new APIKeyService({
        db: c.get('db'),
      })

      const key = await apiKeyService.getKey(keyId, user.userId)

      if (!key) {
        return c.json({ error: 'API key not found' }, 404)
      }

      // Remove sensitive data from response
      const { keyHash, ...keyResponse } = key

      return c.json({
        key: keyResponse,
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to get API key'
      return c.json({ error: message }, 500)
    }
  }
)

/**
 * PUT /api-keys/:id
 * Update an API key
 */
apiKeyRoutes.put(
  '/:id',
  requireAuth,
  zValidator('json', updateKeySchema),
  async (c) => {
    try {
      const user = c.get('user')
      const keyId = c.req.param('id')
      const updates = c.req.valid('json')

      const apiKeyService = new APIKeyService({
        db: c.get('db'),
      })

      const updatedKey = await apiKeyService.updateKey(keyId, user.userId, updates)

      // Remove sensitive data from response
      const { keyHash, ...keyResponse } = updatedKey

      return c.json({
        message: 'API key updated successfully',
        key: keyResponse,
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to update API key'
      return c.json({ error: message }, 400)
    }
  }
)

/**
 * DELETE /api-keys/:id
 * Revoke an API key
 */
apiKeyRoutes.delete(
  '/:id',
  requireAuth,
  async (c) => {
    try {
      const user = c.get('user')
      const keyId = c.req.param('id')

      const apiKeyService = new APIKeyService({
        db: c.get('db'),
      })

      await apiKeyService.revokeKey(keyId, user.userId)

      return c.json({
        message: 'API key revoked successfully',
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to revoke API key'
      return c.json({ error: message }, 400)
    }
  }
)

/**
 * GET /api-keys/:id/usage
 * Get usage statistics for an API key
 */
apiKeyRoutes.get(
  '/:id/usage',
  requireAuth,
  zValidator('query', usageQuerySchema),
  async (c) => {
    try {
      const user = c.get('user')
      const keyId = c.req.param('id')
      const { startDate, endDate, limit = 100, offset = 0 } = c.req.valid('query')

      const apiKeyService = new APIKeyService({
        db: c.get('db'),
      })

      const { usage, total } = await apiKeyService.getKeyUsage(
        keyId,
        user.userId,
        {
          startDate: startDate ? new Date(startDate) : undefined,
          endDate: endDate ? new Date(endDate) : undefined,
          limit,
          offset,
        }
      )

      // Calculate statistics
      const stats = {
        totalRequests: total,
        successRate: usage.length > 0
          ? (usage.filter(u => u.statusCode >= 200 && u.statusCode < 300).length / usage.length) * 100
          : 0,
        averageResponseTime: usage.length > 0
          ? usage.reduce((acc, u) => acc + (u.responseTime || 0), 0) / usage.length
          : 0,
        endpointBreakdown: usage.reduce((acc, u) => {
          const key = `${u.method} ${u.endpoint}`
          acc[key] = (acc[key] || 0) + 1
          return acc
        }, {} as Record<string, number>),
      }

      return c.json({
        usage,
        total,
        limit,
        offset,
        hasMore: offset + limit < total,
        stats,
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to get API key usage'
      return c.json({ error: message }, 500)
    }
  }
)

export { apiKeyRoutes }