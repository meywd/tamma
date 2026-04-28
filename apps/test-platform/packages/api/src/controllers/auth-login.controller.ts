import { Context } from 'hono'
import { zValidator } from '@hono/zod-validator'
import { z } from 'zod'
import { authService } from '../services/auth.service'
import { jwtService } from '../services/jwt.service'
import { refreshTokenService } from '../services/refresh-token.service'
import { getDatabase } from '../../../../src/database/connection'

const loginSchema = z.object({
  email: z.email(),
  password: z.string().min(1),
})

const refreshTokenSchema = z.object({
  refreshToken: z.string().min(1),
})

const logoutSchema = z.object({
  refreshToken: z.string().optional(),
})

/**
 * User login endpoint
 */
export const login = [
  zValidator('json', loginSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const { email, password } = c.req.valid('json')
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      // Find user
      const user = await authService.findUserByEmail(email)
      if (!user) {
        // Log failed attempt for security monitoring
        console.warn('Login attempt with non-existent email', { email, ipAddress })
        await authService.logEvent('AUTH.LOGIN_FAILED', {
          email,
          ipAddress,
          userAgent,
          reason: 'USER_NOT_FOUND'
        })
        return c.json({ error: 'Invalid email or password' }, 401)
      }

      // Check account status
      const db = await getDatabase()
      if (user.status !== 'active') {
        console.warn('Login attempt on inactive account', { email, status: user.status, ipAddress })
        await authService.logEvent('AUTH.LOGIN_FAILED', {
          userId: user.id,
          email,
          ipAddress,
          userAgent,
          reason: 'ACCOUNT_INACTIVE'
        })
        return c.json({ error: 'Account is not active' }, 401)
      }

      // Check if email is verified
      if (!user.email_verified) {
        await authService.logEvent('AUTH.LOGIN_FAILED', {
          userId: user.id,
          email,
          ipAddress,
          userAgent,
          reason: 'EMAIL_NOT_VERIFIED'
        })
        return c.json({ error: 'Please verify your email before logging in' }, 401)
      }

      // Check if account is locked
      if (user.locked_until && new Date() < new Date(user.locked_until)) {
        await authService.logEvent('AUTH.LOGIN_FAILED', {
          userId: user.id,
          email,
          ipAddress,
          userAgent,
          reason: 'ACCOUNT_LOCKED'
        })
        return c.json({ error: 'Account is temporarily locked due to too many failed attempts' }, 423)
      }

      // Verify password
      const isPasswordValid = await authService.verifyPassword(password, user.password_hash)
      if (!isPasswordValid) {
        // Increment failed login attempts
        const failedAttempts = (user.failed_login_attempts || 0) + 1
        const updates: any = {
          failed_login_attempts: failedAttempts,
          updated_at: new Date(),
        }

        // Lock account after 5 failed attempts
        if (failedAttempts >= 5) {
          updates.locked_until = new Date(Date.now() + 30 * 60 * 1000) // Lock for 30 minutes
          await authService.logEvent('AUTH.ACCOUNT_LOCKED', {
            userId: user.id,
            email,
            ipAddress,
            userAgent,
            failedAttempts
          })
        }

        await db('users').where('id', user.id).update(updates)

        await authService.logEvent('AUTH.LOGIN_FAILED', {
          userId: user.id,
          email,
          ipAddress,
          userAgent,
          reason: 'INVALID_PASSWORD',
          failedAttempts
        })

        console.warn('Failed login attempt', { email, ipAddress, failedAttempts })
        return c.json({ error: 'Invalid email or password' }, 401)
      }

      // Reset failed login attempts and update login tracking
      await db('users').where('id', user.id).update({
        failed_login_attempts: 0,
        last_login_at: new Date(),
        last_login_ip: ipAddress,
        locked_until: null,
        updated_at: new Date(),
      })

      // Get user's organization
      const userOrg = await db('user_organizations')
        .where('user_id', user.id)
        .where('status', 'active')
        .first()

      // Get user permissions (simplified for now)
      const permissions = user.role === 'admin' ? ['*'] : ['read:self', 'update:self']

      // Generate tokens
      const tokens = await jwtService.generateTokenPair({
        id: user.id,
        email: user.email,
        organizationId: userOrg?.organization_id,
        role: user.role || 'user',
        permissions,
      })

      // Log successful login
      await authService.logEvent('AUTH.LOGIN_SUCCESS', {
        userId: user.id,
        email,
        ipAddress,
        userAgent,
        organizationId: userOrg?.organization_id
      })

      console.log('User logged in successfully', {
        userId: user.id,
        email,
        ipAddress,
        organizationId: userOrg?.organization_id,
      })

      return c.json({
        message: 'Login successful',
        user: {
          id: user.id,
          email: user.email,
          firstName: user.first_name,
          lastName: user.last_name,
          role: user.role,
          organizationId: userOrg?.organization_id,
          permissions,
        },
        tokens,
      })
    } catch (error) {
      console.error('Login error:', error)
      return c.json({ error: 'Internal server error' }, 500)
    }
  },
]

/**
 * Refresh access token using refresh token
 */
export const refreshAccessToken = [
  zValidator('json', refreshTokenSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const { refreshToken } = c.req.valid('json')
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      // Refresh the token
      const tokens = await jwtService.refreshToken(refreshToken)

      // Log token refresh
      const user = jwtService.decodeToken(tokens.accessToken)
      if (user) {
        await authService.logEvent('AUTH.TOKEN_REFRESHED', {
          userId: user.sub,
          email: user.email,
          ipAddress,
          userAgent
        })
      }

      return c.json({
        message: 'Token refreshed successfully',
        tokens,
      })
    } catch (error: any) {
      console.error('Token refresh error:', error)
      return c.json({ error: error.message || 'Failed to refresh token' }, 401)
    }
  },
]

/**
 * User logout endpoint
 */
export const logout = [
  async (c: Context) => {
    try {
      const authHeader = c.req.header('Authorization')
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      if (!authHeader || !authHeader.startsWith('Bearer ')) {
        return c.json({ error: 'Access token is required' }, 400)
      }

      const accessToken = authHeader.substring(7)

      // Parse request body for optional refresh token
      let refreshToken: string | undefined
      try {
        const body = await c.req.json()
        refreshToken = body.refreshToken
      } catch {
        // Body might be empty or not JSON
      }

      // Verify and decode access token to get user info
      const payload = await jwtService.verifyToken(accessToken)

      // Revoke access token
      await jwtService.revokeToken(payload.tokenId, payload.sub)

      // Revoke refresh token if provided
      if (refreshToken) {
        try {
          const refreshPayload = await jwtService.verifyToken(refreshToken)
          if (refreshPayload.type === 'refresh' && refreshPayload.sub === payload.sub) {
            await refreshTokenService.revokeRefreshToken(refreshPayload.tokenId)
          }
        } catch (error) {
          // Refresh token might be expired or invalid, but that's okay for logout
          console.debug('Refresh token invalid during logout', { error })
        }
      }

      // Log logout event
      await authService.logEvent('AUTH.LOGOUT', {
        userId: payload.sub,
        email: payload.email,
        ipAddress,
        userAgent
      })

      console.log('User logged out successfully', {
        userId: payload.sub,
        email: payload.email,
      })

      return c.json({ message: 'Logout successful' })
    } catch (error: any) {
      console.error('Logout error:', error)
      return c.json({ error: error.message || 'Internal server error' }, 500)
    }
  },
]

/**
 * Logout from all devices
 */
export const logoutAll = async (c: Context) => {
  try {
    const authHeader = c.req.header('Authorization')
    const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
    const userAgent = c.req.header('user-agent')

    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      return c.json({ error: 'Access token is required' }, 400)
    }

    const accessToken = authHeader.substring(7)
    const payload = await jwtService.verifyToken(accessToken)

    // Revoke all user tokens
    await jwtService.revokeAllUserTokens(payload.sub)
    await refreshTokenService.revokeAllUserTokens(payload.sub)

    // Log logout all event
    await authService.logEvent('AUTH.LOGOUT_ALL', {
      userId: payload.sub,
      email: payload.email,
      ipAddress,
      userAgent
    })

    console.log('User logged out from all devices', {
      userId: payload.sub,
      email: payload.email,
    })

    return c.json({ message: 'Logged out from all devices successfully' })
  } catch (error: any) {
    console.error('Logout all error:', error)
    return c.json({ error: error.message || 'Internal server error' }, 500)
  }
}

/**
 * Get current session information
 */
export const getSession = async (c: Context) => {
  try {
    const authHeader = c.req.header('Authorization')

    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      return c.json({ error: 'Access token is required' }, 400)
    }

    const accessToken = authHeader.substring(7)
    const payload = await jwtService.verifyToken(accessToken)

    // Get user details
    const db = await getDatabase()
    const user = await db('users').where('id', payload.sub).first()

    if (!user) {
      return c.json({ error: 'User not found' }, 404)
    }

    // Get user's organization
    const userOrg = await db('user_organizations')
      .where('user_id', user.id)
      .where('status', 'active')
      .first()

    // Get refresh token stats
    const tokenStats = await refreshTokenService.getUserTokenStats(user.id)

    return c.json({
      user: {
        id: user.id,
        email: user.email,
        firstName: user.first_name,
        lastName: user.last_name,
        role: user.role,
        organizationId: userOrg?.organization_id,
        permissions: payload.permissions,
        emailVerified: user.email_verified,
      },
      session: {
        tokenId: payload.tokenId,
        expiresAt: payload.exp ? new Date(payload.exp * 1000).toISOString() : null,
        issuedAt: payload.iat ? new Date(payload.iat * 1000).toISOString() : null,
      },
      tokenStats,
    })
  } catch (error: any) {
    console.error('Get session error:', error)
    return c.json({ error: error.message || 'Failed to get session' }, 401)
  }
}