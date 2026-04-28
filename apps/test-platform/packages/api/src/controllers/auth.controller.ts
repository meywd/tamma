import { Context } from 'hono'
import { zValidator } from '@hono/zod-validator'
import { z } from 'zod'
import { authService } from '../services/auth.service'
import { jwtService } from '../services/jwt.service'
import { refreshTokenService } from '../services/refresh-token.service'
import { emailVerificationService } from '../services/email-verification.service'
import { emailService } from '../services/email.service'
import { rateLimitService } from '../services/rate-limit.service'
import { getDatabase } from '../../../../src/database/connection'

const registerSchema = z.object({
  email: z.email(),
  password: z.string().min(8),
  firstName: z.string().min(1),
  lastName: z.string().min(1),
  organizationName: z.string().optional(),
})

const requestPasswordResetSchema = z.object({
  email: z.email(),
})

const resetPasswordSchema = z.object({
  token: z.string().min(1),
  password: z.string().min(8),
})

const changePasswordSchema = z.object({
  currentPassword: z.string().min(1),
  newPassword: z.string().min(8),
})

const validateTokenSchema = z.object({
  token: z.string().min(1),
})

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

const verifyEmailSchema = z.object({
  token: z.string().min(32),
})

const resendVerificationSchema = z.object({
  email: z.email(),
})

export const register = [
  rateLimitService.createLimiter('register', {
    windowMs: 60 * 1000, // 1 minute
    maxAttempts: 5,
    message: 'Too many registration attempts. Please try again later.'
  }),
  zValidator('json', registerSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const userData = c.req.valid('json')
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      // Register user
      const result = await authService.register(userData)

      // Generate email verification token
      const verificationToken = await emailVerificationService.generateToken(result.user.id)

      // Send verification email
      await emailService.sendEmailVerification(
        result.user.email,
        verificationToken,
        result.user.first_name
      )

      // Log registration event
      await authService.logEvent('USER.REGISTERED', {
        userId: result.user.id,
        email: result.user.email,
        ipAddress,
        userAgent
      })

      return c.json({
        message: 'Registration successful. Please check your email for verification.',
        user: {
          id: result.user.id,
          email: result.user.email,
          firstName: result.user.first_name,
          lastName: result.user.last_name,
          emailVerified: false
        },
        organization: result.organization
      })
    } catch (error) {
      console.error('Registration error:', error)
      if (error instanceof Error) {
        if (error.message.includes('already exists')) {
          return c.json({ error: 'User with this email already exists' }, 409)
        }
        return c.json({ error: error.message }, 400)
      }
      return c.json({ error: 'Failed to register user' }, 500)
    }
  },
]

export const requestPasswordReset = [
  zValidator('json', requestPasswordResetSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const { email } = c.req.valid('json')
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      await authService.requestPasswordReset(email, ipAddress, userAgent)

      // Always return success to prevent email enumeration
      return c.json({
        message: 'If an account with this email exists, a password reset link has been sent.',
      })
    } catch (error) {
      // Handle rate limiting error differently
      if (error instanceof Error && error.message.includes('Too many')) {
        return c.json({ error: error.message }, 429)
      }

      // For all other errors, still return success to prevent enumeration
      console.error('Password reset request error:', error)
      return c.json({
        message: 'If an account with this email exists, a password reset link has been sent.',
      })
    }
  },
]

export const resetPassword = [
  zValidator('json', resetPasswordSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const { token, password } = c.req.valid('json')
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      await authService.resetPassword(token, password, ipAddress, userAgent)

      return c.json({
        message: 'Password has been reset successfully. Please log in with your new password.',
      })
    } catch (error) {
      if (error instanceof Error) {
        return c.json({ error: error.message }, 400)
      }
      return c.json({ error: 'Failed to reset password' }, 500)
    }
  },
]

export const validateResetToken = [
  zValidator('json', validateTokenSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const { token } = c.req.valid('json')
      const tokenData = await authService.validatePasswordResetToken(token)

      return c.json({
        valid: true,
        user: {
          email: tokenData.email,
          firstName: tokenData.firstName,
        },
      })
    } catch (error) {
      return c.json({
        valid: false,
        error: error instanceof Error ? error.message : 'Invalid or expired token',
      }, 400)
    }
  },
]

export const changePassword = [
  zValidator('json', changePasswordSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const { currentPassword, newPassword } = c.req.valid('json')
      const userId = c.get('userId') // Assuming you have auth middleware that sets userId
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      if (!userId) {
        return c.json({ error: 'Unauthorized' }, 401)
      }

      await authService.changePassword(userId, currentPassword, newPassword, ipAddress, userAgent)

      return c.json({
        message: 'Password has been changed successfully.',
      })
    } catch (error) {
      if (error instanceof Error) {
        return c.json({ error: error.message }, 400)
      }
      return c.json({ error: 'Failed to change password' }, 500)
    }
  },
]

export const checkPasswordStrength = async (c: Context) => {
  try {
    const { password } = await c.req.json()

    if (!password) {
      return c.json({ error: 'Password is required' }, 400)
    }

    const strength = await authService.checkPasswordStrength(password)

    return c.json({
      isStrong: strength.isStrong,
      score: strength.score,
      feedback: strength.feedback,
    })
  } catch (error) {
    return c.json({ error: 'Failed to check password strength' }, 500)
  }
}

export const verifyEmail = [
  zValidator('json', verifyEmailSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const { token } = c.req.valid('json')
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      // Verify token
      const result = await emailVerificationService.verifyToken(token)

      // Log verification event
      await authService.logEvent('EMAIL.VERIFIED', {
        userId: result.userId,
        email: result.email,
        ipAddress,
        userAgent
      })

      return c.json({
        message: 'Email verified successfully. You can now log in.',
        user: {
          id: result.userId,
          email: result.email,
          emailVerified: true
        }
      })
    } catch (error) {
      console.error('Email verification error:', error)
      if (error instanceof Error) {
        return c.json({ error: error.message }, 400)
      }
      return c.json({ error: 'Failed to verify email' }, 500)
    }
  },
]

export const resendVerificationEmail = [
  rateLimitService.createLimiter('email-verification-resend', {
    windowMs: 60 * 1000, // 1 minute
    maxAttempts: 2,
    message: 'Please wait before requesting another verification email.'
  }),
  zValidator('json', resendVerificationSchema, (result, c) => {
    if (!result.success) {
      return c.json({ error: 'Validation failed', details: result.error.issues }, 400)
    }
  }),
  async (c: Context) => {
    try {
      const { email } = c.req.valid('json')
      const ipAddress = c.req.header('x-forwarded-for') || c.req.header('x-real-ip') || 'unknown'
      const userAgent = c.req.header('user-agent')

      // Resend verification token
      const token = await emailVerificationService.resendVerificationToken(email)

      // Get user for sending email
      const user = await authService.findUserByEmail(email)

      // Send verification email
      await emailService.sendEmailVerification(email, token, user?.first_name)

      // Log resend event
      if (user) {
        await authService.logEvent('EMAIL.VERIFICATION_RESENT', {
          userId: user.id,
          email,
          ipAddress,
          userAgent
        })
      }

      // Always return success to prevent email enumeration
      return c.json({
        message: 'If an unverified account with this email exists, a verification email has been sent.'
      })
    } catch (error) {
      console.error('Resend verification error:', error)
      // Still return success to prevent enumeration
      return c.json({
        message: 'If an unverified account with this email exists, a verification email has been sent.'
      })
    }
  },
]

export const getVerificationStatus = async (c: Context) => {
  try {
    const userId = c.get('userId')

    if (!userId) {
      return c.json({ error: 'Unauthorized' }, 401)
    }

    const status = await emailVerificationService.getVerificationStatus(userId)

    return c.json(status)
  } catch (error) {
    console.error('Get verification status error:', error)
    if (error instanceof Error) {
      return c.json({ error: error.message }, 400)
    }
    return c.json({ error: 'Failed to get verification status' }, 500)
  }
}
