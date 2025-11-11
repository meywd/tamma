# Implementation Plan: Task 3 - Password Management

**Story**: 1.2 Authentication & Authorization System  
**Task**: 3 - Password Management  
**Acceptance Criteria**: #4 - Password reset functionality with secure token generation

## Overview

Implement secure password reset system with token generation, email notifications, and comprehensive security measures.

## Implementation Steps

### Subtask 3.1: Create password reset request endpoint

**Objective**: Build secure password reset request endpoint with rate limiting

**File**: `src/controllers/auth-controller.ts` (continued)

```typescript
async requestPasswordReset(req: Request, res: Response): Promise<void> {
  try {
    // Check validation errors
    const errors = validationResult(req);
    if (!errors.isEmpty()) {
      throw new ApiError(400, 'Validation failed', errors.array());
    }

    const { email } = req.body;

    // Check rate limiting for password reset requests
    const rateLimitKey = `password-reset:${req.ip}:${email}`;
    const isAllowed = await this.rateLimitService.checkLimit(rateLimitKey, {
      windowMs: 60 * 60 * 1000, // 1 hour
      maxAttempts: 3,
    });

    if (!isAllowed) {
      throw new ApiError(429, 'Too many password reset requests. Please try again later.');
    }

    // Find user (don't reveal if user exists or not)
    const user = await this.authService.findUserByEmail(email);

    if (!user) {
      // Still return success to prevent email enumeration
      logger.info('Password reset requested for non-existent email', {
        email,
        ipAddress: req.ip,
      });

      res.json({
        message: 'If an account with this email exists, a password reset link has been sent.',
      });
      return;
    }

    // Check if user is active
    if (user.status !== 'active') {
      logger.info('Password reset requested for inactive account', {
        userId: user.id,
        email,
        status: user.status,
        ipAddress: req.ip,
      });

      res.json({
        message: 'If an account with this email exists, a password reset link has been sent.',
      });
      return;
    }

    // Invalidate existing password reset tokens
    await this.authService.invalidatePasswordResetTokens(user.id);

    // Generate secure reset token
    const resetToken = await this.authService.generatePasswordResetToken(user.id);

    // Send password reset email
    await this.emailService.sendPasswordResetEmail(email, resetToken, user.first_name);

    // Emit event for audit trail
    await this.authService.emitEvent('PASSWORD.RESET_REQUESTED', {
      userId: user.id,
      email,
      ipAddress: req.ip,
      userAgent: req.get('User-Agent'),
    });

    logger.info('Password reset email sent', {
      userId: user.id,
      email,
      ipAddress: req.ip,
    });

    res.json({
      message: 'If an account with this email exists, a password reset link has been sent.',
    });
  } catch (error) {
    if (error instanceof ApiError) {
      res.status(error.statusCode).json({
        error: error.message,
      });
    } else {
      logger.error('Password reset request error', { error, email: req.body.email });
      res.status(500).json({ error: 'Internal server error' });
    }
  }
}
```

### Subtask 3.2: Generate secure reset tokens with expiration

**Objective**: Create secure password reset token generation and management

**File**: `src/services/password-reset-service.ts`

```typescript
import { randomBytes } from 'crypto';
import { addHours, isAfter } from 'date-fns';
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface PasswordResetToken {
  id: string;
  user_id: string;
  token: string;
  expires_at: Date;
  created_at: Date;
  used_at?: Date;
  ip_address?: string;
  user_agent?: string;
}

export class PasswordResetService {
  private readonly TOKEN_EXPIRY_HOURS = 2;
  private readonly TOKEN_LENGTH = 64;
  private readonly MAX_ACTIVE_TOKENS = 3;

  async generateToken(
    userId: string,
    context?: {
      ipAddress?: string;
      userAgent?: string;
    }
  ): Promise<string> {
    try {
      // Check user exists and is active
      const user = await db('users').where('id', userId).where('status', 'active').first();

      if (!user) {
        throw new ApiError(404, 'User not found or inactive');
      }

      // Clean up old tokens
      await this.cleanupExpiredTokens();

      // Check token limit
      await this.enforceTokenLimit(userId);

      // Generate secure random token
      const token = randomBytes(this.TOKEN_LENGTH).toString('hex');
      const expiresAt = addHours(new Date(), this.TOKEN_EXPIRY_HOURS);

      // Store token in database
      await db('password_reset_tokens').insert({
        id: `reset_${Date.now()}_${randomBytes(8).toString('hex')}`,
        user_id: userId,
        token,
        expires_at: expiresAt,
        ip_address: context?.ipAddress,
        user_agent: context?.userAgent,
        created_at: new Date(),
      });

      logger.info('Password reset token generated', {
        userId,
        tokenPrefix: token.substring(0, 8),
        expiresAt,
        ipAddress: context?.ipAddress,
      });

      return token;
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to generate password reset token', { error, userId });
      throw new ApiError(500, 'Failed to generate reset token');
    }
  }

  async validateToken(token: string): Promise<{
    userId: string;
    email: string;
    firstName: string;
  }> {
    try {
      // Find valid token
      const tokenRecord = await db('password_reset_tokens')
        .where('token', token)
        .andWhere('used_at', null)
        .andWhere('expires_at', '>', new Date())
        .first();

      if (!tokenRecord) {
        throw new ApiError(400, 'Invalid or expired password reset token');
      }

      // Get user
      const user = await db('users')
        .where('id', tokenRecord.user_id)
        .where('status', 'active')
        .first();

      if (!user) {
        throw new ApiError(404, 'User not found or inactive');
      }

      // Check if token was used recently (prevent replay attacks)
      if (tokenRecord.used_at) {
        const timeSinceUsed = new Date().getTime() - tokenRecord.used_at.getTime();
        if (timeSinceUsed < 5 * 60 * 1000) {
          // 5 minutes
          throw new ApiError(400, 'This reset token has already been used');
        }
      }

      logger.info('Password reset token validated', {
        userId: user.id,
        email: user.email,
        tokenPrefix: token.substring(0, 8),
      });

      return {
        userId: user.id,
        email: user.email,
        firstName: user.first_name,
      };
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Password reset token validation failed', {
        error,
        token: token.substring(0, 8),
      });
      throw new ApiError(400, 'Invalid or expired password reset token');
    }
  }

  async useToken(
    token: string,
    context?: {
      ipAddress?: string;
      userAgent?: string;
    }
  ): Promise<void> {
    try {
      // Mark token as used
      const updated = await db('password_reset_tokens')
        .where('token', token)
        .andWhere('used_at', null)
        .update({
          used_at: new Date(),
          used_ip_address: context?.ipAddress,
          used_user_agent: context?.userAgent,
          updated_at: new Date(),
        });

      if (updated === 0) {
        throw new ApiError(400, 'Invalid or already used reset token');
      }

      logger.info('Password reset token used', {
        tokenPrefix: token.substring(0, 8),
        ipAddress: context?.ipAddress,
      });
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to use password reset token', { error, token: token.substring(0, 8) });
      throw new ApiError(500, 'Failed to process reset token');
    }
  }

  async invalidateUserTokens(userId: string): Promise<void> {
    try {
      const invalidatedCount = await db('password_reset_tokens')
        .where('user_id', userId)
        .where('used_at', null)
        .update({
          used_at: new Date(),
          updated_at: new Date(),
        });

      if (invalidatedCount > 0) {
        logger.info('User password reset tokens invalidated', {
          userId,
          invalidatedCount,
        });
      }
    } catch (error) {
      logger.error('Failed to invalidate user password reset tokens', { error, userId });
    }
  }

  async getUserActiveTokens(userId: string): Promise<PasswordResetToken[]> {
    try {
      return await db('password_reset_tokens')
        .where('user_id', userId)
        .where('used_at', null)
        .where('expires_at', '>', new Date())
        .orderBy('created_at', 'desc');
    } catch (error) {
      logger.error('Failed to get user active reset tokens', { error, userId });
      return [];
    }
  }

  async cleanupExpiredTokens(): Promise<number> {
    try {
      const deletedCount = await db('password_reset_tokens')
        .where('expires_at', '<', new Date())
        .del();

      if (deletedCount > 0) {
        logger.info('Cleaned up expired password reset tokens', {
          deletedCount,
        });
      }

      return deletedCount;
    } catch (error) {
      logger.error('Failed to cleanup expired password reset tokens', { error });
      return 0;
    }
  }

  private async enforceTokenLimit(userId: string): Promise<void> {
    try {
      const activeTokens = await db('password_reset_tokens')
        .where('user_id', userId)
        .where('used_at', null)
        .where('expires_at', '>', new Date())
        .count('* as count')
        .first();

      const count = parseInt(activeTokens.count);

      if (count >= this.MAX_ACTIVE_TOKENS) {
        // Invalidate oldest tokens
        const tokensToInvalidate = await db('password_reset_tokens')
          .where('user_id', userId)
          .where('used_at', null)
          .where('expires_at', '>', new Date())
          .orderBy('created_at', 'asc')
          .limit(count - this.MAX_ACTIVE_TOKENS + 1);

        for (const token of tokensToInvalidate) {
          await db('password_reset_tokens').where('id', token.id).update({
            used_at: new Date(),
            updated_at: new Date(),
          });
        }

        logger.info('Invalidated excess password reset tokens', {
          userId,
          invalidatedCount: tokensToInvalidate.length,
        });
      }
    } catch (error) {
      logger.error('Failed to enforce password reset token limit', { error, userId });
    }
  }
}
```

### Subtask 3.3: Implement password reset confirmation endpoint

**Objective**: Create secure password reset confirmation with validation

**File**: `src/controllers/auth-controller.ts` (continued)

```typescript
async confirmPasswordReset(req: Request, res: Response): Promise<void> {
  try {
    // Check validation errors
    const errors = validationResult(req);
    if (!errors.isEmpty()) {
      throw new ApiError(400, 'Validation failed', errors.array());
    }

    const { token, password } = req.body;

    // Validate reset token
    const tokenData = await this.authService.validatePasswordResetToken(token);

    // Check password strength
    const passwordStrength = await this.authService.checkPasswordStrength(password);
    if (!passwordStrength.isStrong) {
      throw new ApiError(400, 'Password does not meet security requirements', {
        feedback: passwordStrength.feedback,
        score: passwordStrength.score,
      });
    }

    // Hash new password
    const passwordHash = await this.authService.hashPassword(password);

    // Update user password
    await this.authService.updateUserPassword(tokenData.userId, passwordHash.hash, passwordHash.salt);

    // Mark token as used
    await this.authService.usePasswordResetToken(token, {
      ipAddress: req.ip,
      userAgent: req.get('User-Agent'),
    });

    // Invalidate all user sessions (force logout from all devices)
    await this.authService.revokeAllUserTokens(tokenData.userId);

    // Invalidate all other password reset tokens for this user
    await this.authService.invalidatePasswordResetTokens(tokenData.userId);

    // Emit event for audit trail
    await this.authService.emitEvent('PASSWORD.RESET_COMPLETED', {
      userId: tokenData.userId,
      email: tokenData.email,
      ipAddress: req.ip,
      userAgent: req.get('User-Agent'),
    });

    logger.info('Password reset completed successfully', {
      userId: tokenData.userId,
      email: tokenData.email,
      ipAddress: req.ip,
    });

    res.json({
      message: 'Password has been reset successfully. Please log in with your new password.',
    });
  } catch (error) {
    if (error instanceof ApiError) {
      res.status(error.statusCode).json({
        error: error.message,
        details: error.details,
      });
    } else {
      logger.error('Password reset confirmation error', { error });
      res.status(500).json({ error: 'Internal server error' });
    }
  }
}

async validatePasswordResetToken(req: Request, res: Response): Promise<void> {
  try {
    const { token } = req.params;

    if (!token) {
      throw new ApiError(400, 'Reset token is required');
    }

    // Validate token
    const tokenData = await this.authService.validatePasswordResetToken(token);

    res.json({
      valid: true,
      user: {
        email: tokenData.email,
        firstName: tokenData.firstName,
      },
    });
  } catch (error) {
    if (error instanceof ApiError) {
      res.status(error.statusCode).json({
        valid: false,
        error: error.message,
      });
    } else {
      logger.error('Password reset token validation error', { error });
      res.status(500).json({
        valid: false,
        error: 'Internal server error',
      });
    }
  }
}
```

### Subtask 3.4: Add email notifications for password resets

**Objective**: Create comprehensive email notification system for password resets

**File**: `src/services/email-service.ts` (continued)

```typescript
async sendPasswordResetEmail(
  email: string,
  token: string,
  firstName?: string
): Promise<void> {
  try {
    const resetUrl = `${process.env.FRONTEND_URL}/reset-password?token=${token}`;
    const expiryHours = 2;

    const htmlContent = `
      <!DOCTYPE html>
      <html>
      <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Password Reset - Test Platform</title>
        <style>
          body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; line-height: 1.6; color: #333; }
          .container { max-width: 600px; margin: 0 auto; padding: 20px; }
          .header { background: #2563eb; color: white; padding: 30px; text-align: center; border-radius: 8px 8px 0 0; }
          .content { background: #f8fafc; padding: 30px; border-radius: 0 0 8px 8px; }
          .button { display: inline-block; background: #2563eb; color: white; padding: 12px 24px; text-decoration: none; border-radius: 6px; margin: 20px 0; }
          .footer { text-align: center; margin-top: 30px; color: #64748b; font-size: 14px; }
          .warning { background: #fef3c7; border: 1px solid #f59e0b; padding: 15px; border-radius: 6px; margin: 20px 0; }
        </style>
      </head>
      <body>
        <div class="container">
          <div class="header">
            <h1>Password Reset</h1>
          </div>
          <div class="content">
            <p>Hello ${firstName || 'there'},</p>
            <p>We received a request to reset your password for your Test Platform account.</p>

            <p>Click the button below to reset your password:</p>
            <div style="text-align: center;">
              <a href="${resetUrl}" class="button">Reset Password</a>
            </div>

            <p>Or copy and paste this link into your browser:</p>
            <p style="word-break: break-all; background: #e2e8f0; padding: 10px; border-radius: 4px;">
              ${resetUrl}
            </p>

            <div class="warning">
              <strong>Important:</strong>
              <ul>
                <li>This link will expire in ${expiryHours} hours</li>
                <li>If you didn't request this password reset, please ignore this email</li>
                <li>Never share this link with anyone</li>
              </ul>
            </div>

            <p>If you have any questions, please contact our support team.</p>

            <p>Best regards,<br>The Test Platform Team</p>
          </div>
          <div class="footer">
            <p>This is an automated message. Please do not reply to this email.</p>
            <p>© 2024 Test Platform. All rights reserved.</p>
          </div>
        </div>
      </body>
      </html>
    `;

    const textContent = `
      Password Reset - Test Platform

      Hello ${firstName || 'there'},

      We received a request to reset your password for your Test Platform account.

      Click the link below to reset your password:
      ${resetUrl}

      Important:
      - This link will expire in ${expiryHours} hours
      - If you didn't request this password reset, please ignore this email
      - Never share this link with anyone

      If you have any questions, please contact our support team.

      Best regards,
      The Test Platform Team
    `;

    await this.sendEmail({
      to: email,
      subject: 'Reset your Test Platform password',
      html: htmlContent,
      text: textContent,
      category: 'password_reset',
    });

    logger.info('Password reset email sent', {
      email,
      tokenPrefix: token.substring(0, 8),
    });
  } catch (error) {
    logger.error('Failed to send password reset email', { error, email });
    throw new ApiError(500, 'Failed to send password reset email');
  }
}

async sendPasswordChangedNotification(
  email: string,
  firstName?: string,
  context?: {
    ipAddress?: string;
    userAgent?: string;
  }
): Promise<void> {
  try {
    const htmlContent = `
      <!DOCTYPE html>
      <html>
      <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Password Changed - Test Platform</title>
        <style>
          body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; line-height: 1.6; color: #333; }
          .container { max-width: 600px; margin: 0 auto; padding: 20px; }
          .header { background: #10b981; color: white; padding: 30px; text-align: center; border-radius: 8px 8px 0 0; }
          .content { background: #f8fafc; padding: 30px; border-radius: 0 0 8px 8px; }
          .footer { text-align: center; margin-top: 30px; color: #64748b; font-size: 14px; }
          .alert { background: #fee2e2; border: 1px solid #ef4444; padding: 15px; border-radius: 6px; margin: 20px 0; }
        </style>
      </head>
      <body>
        <div class="container">
          <div class="header">
            <h1>Password Changed Successfully</h1>
          </div>
          <div class="content">
            <p>Hello ${firstName || 'there'},</p>
            <p>Your password for your Test Platform account has been successfully changed.</p>

            ${context ? `
            <p>Change details:</p>
            <ul>
              <li>IP Address: ${context.ipAddress || 'Unknown'}</li>
              <li>Device: ${context.userAgent || 'Unknown'}</li>
              <li>Time: ${new Date().toLocaleString()}</li>
            </ul>
            ` : ''}

            <div class="alert">
              <strong>Security Notice:</strong>
              <p>If you didn't make this change, please:</p>
              <ul>
                <li>Contact our support team immediately</li>
                <li>Check your account for any unauthorized activity</li>
                <li>Consider enabling two-factor authentication</li>
              </ul>
            </div>

            <p>If you have any concerns about your account security, please don't hesitate to reach out.</p>

            <p>Best regards,<br>The Test Platform Team</p>
          </div>
          <div class="footer">
            <p>This is an automated message. Please do not reply to this email.</p>
            <p>© 2024 Test Platform. All rights reserved.</p>
          </div>
        </div>
      </body>
      </html>
    `;

    const textContent = `
      Password Changed Successfully - Test Platform

      Hello ${firstName || 'there'},

      Your password for your Test Platform account has been successfully changed.

      ${context ? `
      Change details:
      - IP Address: ${context.ipAddress || 'Unknown'}
      - Device: ${context.userAgent || 'Unknown'}
      - Time: ${new Date().toLocaleString()}
      ` : ''}

      Security Notice:
      If you didn't make this change, please:
      - Contact our support team immediately
      - Check your account for any unauthorized activity
      - Consider enabling two-factor authentication

      If you have any concerns about your account security, please don't hesitate to reach out.

      Best regards,
      The Test Platform Team
    `;

    await this.sendEmail({
      to: email,
      subject: 'Your Test Platform password has been changed',
      html: htmlContent,
      text: textContent,
      category: 'password_changed',
    });

    logger.info('Password changed notification sent', {
      email,
      ipAddress: context?.ipAddress,
    });
  } catch (error) {
    logger.error('Failed to send password changed notification', { error, email });
    // Don't throw error for notification emails
  }
}
```

## Files to Create

1. `src/services/password-reset-service.ts` - Password reset token management
2. Update `src/controllers/auth-controller.ts` - Password reset endpoints
3. Update `src/services/email-service.ts` - Password reset email templates
4. Database migration for password_reset_tokens table

## Dependencies

- crypto for secure token generation
- date-fns for date handling
- Email service provider (SendGrid, AWS SES, etc.)
- HTML email templates

## Testing

1. Password reset token generation and validation tests
2. Rate limiting tests for reset requests
3. Email notification tests
4. Security tests for token replay attacks
5. Password strength validation tests

## Notes

- Use secure random token generation (64+ characters)
- Implement proper rate limiting to prevent abuse
- Don't reveal if email exists or not in responses
- Invalidate all sessions after password reset
- Send security notifications for password changes
- Consider adding security questions or 2FA for additional protection
