/**
 * Authentication routes
 * Handles user registration, login, email verification, password reset, etc.
 */

import { Hono } from 'hono'
import { zValidator } from '@hono/zod-validator'
import { z } from 'zod'
import { AuthService } from '../services/auth.service'
import { extractTokenFromHeader } from '../utils/jwt'
import type { HonoContext } from '../index'

const authRoutes = new Hono<HonoContext>()

// Validation schemas
const registerSchema = z.object({
  email: z.email('Invalid email format'),
  password: z.string().min(12, 'Password must be at least 12 characters'),
  fullName: z.string().optional(),
})

const loginSchema = z.object({
  email: z.email('Invalid email format'),
  password: z.string().min(1, 'Password is required'),
})

const verifyEmailSchema = z.object({
  token: z.string().min(1, 'Verification token is required'),
})

const forgotPasswordSchema = z.object({
  email: z.email('Invalid email format'),
})

const resetPasswordSchema = z.object({
  token: z.string().min(1, 'Reset token is required'),
  newPassword: z.string().min(12, 'Password must be at least 12 characters'),
})

const changePasswordSchema = z.object({
  currentPassword: z.string().min(1, 'Current password is required'),
  newPassword: z.string().min(12, 'New password must be at least 12 characters'),
})

const refreshTokenSchema = z.object({
  refreshToken: z.string().min(1, 'Refresh token is required').optional(),
})

const resendVerificationSchema = z.object({
  email: z.email('Invalid email format'),
})

/**
 * POST /auth/register
 * Register a new user
 */
authRoutes.post(
  '/register',
  zValidator('json', registerSchema),
  async (c) => {
    try {
      const { email, password, fullName } = c.req.valid('json')

      const authService = new AuthService({
        db: c.get('db'),
        jwtSecret: c.env.JWT_SECRET,
        kv: c.env.KV,
      })

      const { user, verificationToken } = await authService.registerUser(
        email,
        password,
        fullName
      )

      // Return user without sensitive data
      const { passwordHash, ...userResponse } = user

      return c.json(
        {
          message: 'Registration successful. Please check your email to verify your account.',
          user: userResponse,
          // Only include token in development for testing
          ...(c.env.ENVIRONMENT === 'development' && { verificationToken }),
        },
        201
      )
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Registration failed'
      return c.json({ error: message }, 400)
    }
  }
)

/**
 * POST /auth/verify-email
 * Verify email address with token
 */
authRoutes.post(
  '/verify-email',
  zValidator('json', verifyEmailSchema),
  async (c) => {
    try {
      const { token } = c.req.valid('json')

      const authService = new AuthService({
        db: c.get('db'),
        jwtSecret: c.env.JWT_SECRET,
      })

      const user = await authService.verifyEmail(token)

      // Return user without sensitive data
      const { passwordHash, ...userResponse } = user

      return c.json({
        message: 'Email verified successfully',
        user: userResponse,
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Email verification failed'
      return c.json({ error: message }, 400)
    }
  }
)

/**
 * POST /auth/resend-verification
 * Resend verification email
 */
authRoutes.post(
  '/resend-verification',
  zValidator('json', resendVerificationSchema),
  async (c) => {
    try {
      const { email } = c.req.valid('json')

      const authService = new AuthService({
        db: c.get('db'),
        jwtSecret: c.env.JWT_SECRET,
      })

      await authService.resendVerificationEmail(email)

      return c.json({
        message: 'Verification email sent successfully',
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to resend verification email'
      return c.json({ error: message }, 400)
    }
  }
)

/**
 * POST /auth/login
 * Login user
 */
authRoutes.post(
  '/login',
  zValidator('json', loginSchema),
  async (c) => {
    try {
      const { email, password } = c.req.valid('json')

      const authService = new AuthService({
        db: c.get('db'),
        jwtSecret: c.env.JWT_SECRET,
        kv: c.env.KV,
      })

      const { user, accessToken, refreshToken } = await authService.login(
        email,
        password
      )

      // Set refresh token as httpOnly cookie
      c.header(
        'Set-Cookie',
        `refreshToken=${refreshToken}; HttpOnly; Secure; SameSite=Strict; Path=/; Max-Age=${7 * 24 * 60 * 60}`
      )

      return c.json({
        message: 'Login successful',
        user,
        accessToken,
        // Also return refreshToken in body for non-cookie clients
        refreshToken,
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Login failed'
      return c.json({ error: message }, 401)
    }
  }
)

/**
 * POST /auth/logout
 * Logout user
 */
authRoutes.post('/logout', async (c) => {
  try {
    // Get user from token if available
    const authHeader = c.req.header('Authorization')
    const token = extractTokenFromHeader(authHeader)

    if (token) {
      const { verifyToken } = await import('../utils/jwt')
      const decoded = await verifyToken(token, c.env.JWT_SECRET)

      if (decoded) {
        const authService = new AuthService({
          db: c.get('db'),
          jwtSecret: c.env.JWT_SECRET,
          kv: c.env.KV,
        })

        await authService.logout(decoded.userId)
      }
    }

    // Clear refresh token cookie
    c.header(
      'Set-Cookie',
      'refreshToken=; HttpOnly; Secure; SameSite=Strict; Path=/; Max-Age=0'
    )

    return c.json({ message: 'Logout successful' })
  } catch (error) {
    // Always succeed logout
    return c.json({ message: 'Logout successful' })
  }
})

/**
 * POST /auth/refresh
 * Refresh access token
 */
authRoutes.post(
  '/refresh',
  zValidator('json', refreshTokenSchema),
  async (c) => {
    try {
      let refreshToken = c.req.valid('json').refreshToken

      // Try to get refresh token from cookie if not in body
      if (!refreshToken) {
        const cookie = c.req.header('Cookie')
        if (cookie) {
          const match = cookie.match(/refreshToken=([^;]+)/)
          if (match) {
            refreshToken = match[1]
          }
        }
      }

      if (!refreshToken) {
        return c.json({ error: 'Refresh token is required' }, 401)
      }

      const authService = new AuthService({
        db: c.get('db'),
        jwtSecret: c.env.JWT_SECRET,
        kv: c.env.KV,
      })

      const { accessToken } = await authService.refreshToken(refreshToken)

      return c.json({
        message: 'Token refreshed successfully',
        accessToken,
        expiresIn: 900, // 15 minutes
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Token refresh failed'
      return c.json({ error: message }, 401)
    }
  }
)

/**
 * POST /auth/forgot-password
 * Request password reset
 */
authRoutes.post(
  '/forgot-password',
  zValidator('json', forgotPasswordSchema),
  async (c) => {
    try {
      const { email } = c.req.valid('json')

      const authService = new AuthService({
        db: c.get('db'),
        jwtSecret: c.env.JWT_SECRET,
      })

      await authService.forgotPassword(email)

      // Always return success to prevent email enumeration
      return c.json({
        message: 'If an account exists with this email, a password reset link has been sent.',
      })
    } catch (error) {
      // Always return success to prevent email enumeration
      return c.json({
        message: 'If an account exists with this email, a password reset link has been sent.',
      })
    }
  }
)

/**
 * POST /auth/reset-password
 * Reset password with token
 */
authRoutes.post(
  '/reset-password',
  zValidator('json', resetPasswordSchema),
  async (c) => {
    try {
      const { token, newPassword } = c.req.valid('json')

      const authService = new AuthService({
        db: c.get('db'),
        jwtSecret: c.env.JWT_SECRET,
        kv: c.env.KV,
      })

      await authService.resetPassword(token, newPassword)

      return c.json({
        message: 'Password reset successful. You can now login with your new password.',
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Password reset failed'
      return c.json({ error: message }, 400)
    }
  }
)

/**
 * POST /auth/change-password
 * Change password for authenticated user
 */
authRoutes.post(
  '/change-password',
  zValidator('json', changePasswordSchema),
  async (c) => {
    try {
      // Get user from token
      const authHeader = c.req.header('Authorization')
      const token = extractTokenFromHeader(authHeader)

      if (!token) {
        return c.json({ error: 'Authentication required' }, 401)
      }

      const { verifyToken } = await import('../utils/jwt')
      const decoded = await verifyToken(token, c.env.JWT_SECRET)

      if (!decoded || decoded.type !== 'access') {
        return c.json({ error: 'Invalid access token' }, 401)
      }

      const { currentPassword, newPassword } = c.req.valid('json')

      const authService = new AuthService({
        db: c.get('db'),
        jwtSecret: c.env.JWT_SECRET,
        kv: c.env.KV,
      })

      await authService.changePassword(
        decoded.userId,
        currentPassword,
        newPassword
      )

      return c.json({
        message: 'Password changed successfully',
      })
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Password change failed'
      return c.json({ error: message }, 400)
    }
  }
)

/**
 * GET /auth/me
 * Get current user profile
 */
authRoutes.get('/me', async (c) => {
  try {
    // Get user from token
    const authHeader = c.req.header('Authorization')
    const token = extractTokenFromHeader(authHeader)

    if (!token) {
      return c.json({ error: 'Authentication required' }, 401)
    }

    const { verifyToken } = await import('../utils/jwt')
    const decoded = await verifyToken(token, c.env.JWT_SECRET)

    if (!decoded || decoded.type !== 'access') {
      return c.json({ error: 'Invalid access token' }, 401)
    }

    const authService = new AuthService({
      db: c.get('db'),
      jwtSecret: c.env.JWT_SECRET,
    })

    const user = await authService.getUserById(decoded.userId)

    if (!user) {
      return c.json({ error: 'User not found' }, 404)
    }

    return c.json({
      user,
    })
  } catch (error) {
    const message = error instanceof Error ? error.message : 'Failed to get user profile'
    return c.json({ error: message }, 500)
  }
})

export { authRoutes }