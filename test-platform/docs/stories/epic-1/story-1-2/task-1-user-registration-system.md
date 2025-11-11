# Implementation Plan: Task 1 - User Registration System

**Story**: 1.2 Authentication & Authorization System  
**Task**: 1 - User Registration System  
**Acceptance Criteria**: #1, #6, #7 - User registration with email verification; Rate limiting on auth endpoints (5 attempts per minute); Input validation and sanitization on all auth endpoints

## Overview

Implement secure user registration with email verification, rate limiting, and comprehensive input validation.

## Implementation Steps

### Subtask 1.1: Create user registration endpoint with email validation

**Objective**: Build secure user registration endpoint with email validation

**File**: `src/controllers/auth-controller.ts`

```typescript
import { Request, Response } from 'express';
import { body, validationResult } from 'express-validator';
import { AuthService } from '../services/auth-service';
import { EmailService } from '../services/email-service';
import { RateLimitService } from '../services/rate-limit-service';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export class AuthController {
  constructor(
    private authService: AuthService,
    private emailService: EmailService,
    private rateLimitService: RateLimitService
  ) {}

  // Validation middleware
  static registerValidation = [
    body('email').isEmail().normalizeEmail().withMessage('Valid email is required'),
    body('password')
      .isLength({ min: 8 })
      .withMessage('Password must be at least 8 characters long')
      .matches(/^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&])[A-Za-z\d@$!%*?&]/)
      .withMessage('Password must contain uppercase, lowercase, number, and special character'),
    body('firstName')
      .trim()
      .isLength({ min: 1, max: 100 })
      .withMessage('First name is required and must be less than 100 characters'),
    body('lastName')
      .trim()
      .isLength({ min: 1, max: 100 })
      .withMessage('Last name is required and must be less than 100 characters'),
    body('organizationName')
      .optional()
      .trim()
      .isLength({ min: 1, max: 255 })
      .withMessage('Organization name must be less than 255 characters'),
  ];

  async register(req: Request, res: Response): Promise<void> {
    try {
      // Check validation errors
      const errors = validationResult(req);
      if (!errors.isEmpty()) {
        throw new ApiError(400, 'Validation failed', errors.array());
      }

      const { email, password, firstName, lastName, organizationName } = req.body;

      // Check rate limiting
      const rateLimitKey = `register:${req.ip}`;
      const isAllowed = await this.rateLimitService.checkLimit(rateLimitKey, {
        windowMs: 60 * 1000, // 1 minute
        maxAttempts: 5,
      });

      if (!isAllowed) {
        throw new ApiError(429, 'Too many registration attempts. Please try again later.');
      }

      // Check if user already exists
      const existingUser = await this.authService.findUserByEmail(email);
      if (existingUser) {
        throw new ApiError(409, 'User with this email already exists');
      }

      // Create user
      const user = await this.authService.createUser({
        email,
        password,
        firstName,
        lastName,
      });

      // Create organization if provided
      if (organizationName) {
        const organization = await this.authService.createOrganization({
          name: organizationName,
          userId: user.id,
        });

        // Add user as owner
        await this.authService.addUserToOrganization(user.id, organization.id, 'owner');
      }

      // Generate email verification token
      const verificationToken = await this.authService.generateEmailVerificationToken(user.id);

      // Send verification email
      await this.emailService.sendEmailVerification(email, verificationToken);

      // Emit event for audit trail
      await this.authService.emitEvent('USER.REGISTERED', {
        userId: user.id,
        email,
        ipAddress: req.ip,
        userAgent: req.get('User-Agent'),
      });

      logger.info('User registered successfully', {
        userId: user.id,
        email,
        ipAddress: req.ip,
      });

      res.status(201).json({
        message: 'Registration successful. Please check your email for verification.',
        user: {
          id: user.id,
          email: user.email,
          firstName: user.first_name,
          lastName: user.last_name,
          emailVerified: user.email_verified,
        },
      });
    } catch (error) {
      if (error instanceof ApiError) {
        res.status(error.statusCode).json({
          error: error.message,
          details: error.details,
        });
      } else {
        logger.error('Registration error', { error, email: req.body.email });
        res.status(500).json({ error: 'Internal server error' });
      }
    }
  }
}
```

### Subtask 1.2: Implement email verification with secure tokens

**Objective**: Create secure email verification system with token generation and validation

**File**: `src/services/email-verification-service.ts`

```typescript
import { randomBytes } from 'crypto';
import { addHours, isAfter } from 'date-fns';
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface EmailVerificationToken {
  id: string;
  user_id: string;
  token: string;
  expires_at: Date;
  created_at: Date;
  used_at?: Date;
}

export class EmailVerificationService {
  private readonly TOKEN_EXPIRY_HOURS = 24;
  private readonly TOKEN_LENGTH = 32;

  async generateToken(userId: string): Promise<string> {
    try {
      // Generate secure random token
      const token = randomBytes(this.TOKEN_LENGTH).toString('hex');
      const expiresAt = addHours(new Date(), this.TOKEN_EXPIRY_HOURS);

      // Store token in database
      await db('email_verification_tokens').insert({
        user_id: userId,
        token,
        expires_at: expiresAt,
      });

      logger.info('Email verification token generated', { userId });
      return token;
    } catch (error) {
      logger.error('Failed to generate email verification token', { error, userId });
      throw new ApiError(500, 'Failed to generate verification token');
    }
  }

  async verifyToken(token: string): Promise<{ userId: string; email: string }> {
    try {
      // Find valid token
      const tokenRecord = await db('email_verification_tokens')
        .where('token', token)
        .andWhere('used_at', null)
        .andWhere('expires_at', '>', new Date())
        .first();

      if (!tokenRecord) {
        throw new ApiError(400, 'Invalid or expired verification token');
      }

      // Get user
      const user = await db('users').where('id', tokenRecord.user_id).first();

      if (!user) {
        throw new ApiError(404, 'User not found');
      }

      if (user.email_verified) {
        throw new ApiError(400, 'Email already verified');
      }

      // Mark token as used
      await db('email_verification_tokens').where('id', tokenRecord.id).update({
        used_at: new Date(),
      });

      // Update user email verification status
      await db('users').where('id', user.id).update({
        email_verified: true,
        email_verified_at: new Date(),
        updated_at: new Date(),
      });

      logger.info('Email verified successfully', {
        userId: user.id,
        email: user.email,
      });

      return {
        userId: user.id,
        email: user.email,
      };
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Email verification failed', { error, token });
      throw new ApiError(500, 'Email verification failed');
    }
  }

  async resendVerificationEmail(email: string): Promise<void> {
    try {
      const user = await db('users').where('email', email).first();
      if (!user) {
        throw new ApiError(404, 'User not found');
      }

      if (user.email_verified) {
        throw new ApiError(400, 'Email already verified');
      }

      // Invalidate existing tokens
      await db('email_verification_tokens')
        .where('user_id', user.id)
        .where('used_at', null)
        .update({
          used_at: new Date(),
        });

      // Generate new token
      const token = await this.generateToken(user.id);

      // Send verification email
      await this.emailService.sendEmailVerification(email, token);

      logger.info('Verification email resent', { userId: user.id, email });
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Failed to resend verification email', { error, email });
      throw new ApiError(500, 'Failed to resend verification email');
    }
  }

  async cleanupExpiredTokens(): Promise<void> {
    try {
      const deletedCount = await db('email_verification_tokens')
        .where('expires_at', '<', new Date())
        .del();

      if (deletedCount > 0) {
        logger.info('Cleaned up expired email verification tokens', {
          deletedCount,
        });
      }
    } catch (error) {
      logger.error('Failed to cleanup expired tokens', { error });
    }
  }
}
```

### Subtask 1.3: Add rate limiting to registration endpoint

**Objective**: Implement Redis-based rate limiting for auth endpoints

**File**: `src/services/rate-limit-service.ts`

```typescript
import Redis from 'ioredis';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface RateLimitOptions {
  windowMs: number;
  maxAttempts: number;
  keyGenerator?: (req: any) => string;
  skipSuccessfulRequests?: boolean;
  skipFailedRequests?: boolean;
}

export class RateLimitService {
  private redis: Redis;

  constructor(redisUrl?: string) {
    this.redis = new Redis(redisUrl || process.env.REDIS_URL);

    this.redis.on('error', (error) => {
      logger.error('Redis connection error', { error });
    });
  }

  async checkLimit(key: string, options: RateLimitOptions): Promise<boolean> {
    try {
      const now = Date.now();
      const windowStart = now - options.windowMs;

      // Use Redis sorted set for sliding window
      const pipeline = this.redis.pipeline();

      // Remove old entries
      pipeline.zremrangebyscore(key, 0, windowStart);

      // Add current request
      pipeline.zadd(key, now, `${now}-${Math.random()}`);

      // Count requests in window
      pipeline.zcard(key);

      // Set expiration
      pipeline.expire(key, Math.ceil(options.windowMs / 1000));

      const results = await pipeline.exec();
      const requestCount = (results?.[2]?.[1] as number) || 0;

      if (requestCount > options.maxAttempts) {
        logger.warn('Rate limit exceeded', {
          key,
          requestCount,
          maxAttempts: options.maxAttempts,
          windowMs: options.windowMs,
        });
        return false;
      }

      return true;
    } catch (error) {
      logger.error('Rate limit check failed', { error, key });
      // Fail open - allow request if rate limiting fails
      return true;
    }
  }

  async getRemainingRequests(
    key: string,
    options: RateLimitOptions
  ): Promise<{
    remaining: number;
    resetTime: Date;
    total: number;
  }> {
    try {
      const now = Date.now();
      const windowStart = now - options.windowMs;

      // Clean old entries and count current
      await this.redis.zremrangebyscore(key, 0, windowStart);
      const requestCount = await this.redis.zcard(key);

      const remaining = Math.max(0, options.maxAttempts - requestCount);
      const resetTime = new Date(now + options.windowMs);

      return {
        remaining,
        resetTime,
        total: options.maxAttempts,
      };
    } catch (error) {
      logger.error('Failed to get remaining requests', { error, key });
      return {
        remaining: options.maxAttempts,
        resetTime: new Date(Date.now() + options.windowMs),
        total: options.maxAttempts,
      };
    }
  }

  async resetKey(key: string): Promise<void> {
    try {
      await this.redis.del(key);
      logger.info('Rate limit key reset', { key });
    } catch (error) {
      logger.error('Failed to reset rate limit key', { error, key });
    }
  }

  // Express middleware factory
  middleware(options: RateLimitOptions) {
    return async (req: any, res: any, next: any) => {
      try {
        const key = options.keyGenerator
          ? options.keyGenerator(req)
          : `rate-limit:${req.ip}:${req.path}`;

        const isAllowed = await this.checkLimit(key, options);

        if (!isAllowed) {
          const remaining = await this.getRemainingRequests(key, options);

          res.set({
            'X-RateLimit-Limit': remaining.total.toString(),
            'X-RateLimit-Remaining': remaining.remaining.toString(),
            'X-RateLimit-Reset': Math.ceil(remaining.resetTime.getTime() / 1000).toString(),
          });

          throw new ApiError(429, 'Too many requests. Please try again later.');
        }

        // Add rate limit headers
        const remaining = await this.getRemainingRequests(key, options);
        res.set({
          'X-RateLimit-Limit': remaining.total.toString(),
          'X-RateLimit-Remaining': remaining.remaining.toString(),
          'X-RateLimit-Reset': Math.ceil(remaining.resetTime.getTime() / 1000).toString(),
        });

        next();
      } catch (error) {
        next(error);
      }
    };
  }
}
```

### Subtask 1.4: Implement input validation and sanitization

**Objective**: Add comprehensive input validation and sanitization

**File**: `src/middleware/validation-middleware.ts`

```typescript
import { Request, Response, NextFunction } from 'express';
import { body, param, query, validationResult } from 'express-validator';
import DOMPurify from 'isomorphic-dompurify';
import { ApiError } from '../utils/api-error';

export class ValidationMiddleware {
  // Sanitize HTML content
  static sanitizeHtml(value: string): string {
    return DOMPurify.sanitize(value, {
      ALLOWED_TAGS: ['b', 'i', 'em', 'strong', 'a', 'p', 'br'],
      ALLOWED_ATTR: ['href', 'target'],
    });
  }

  // Custom validator for passwords
  static validatePassword(value: string): boolean {
    const minLength = 8;
    const hasUpperCase = /[A-Z]/.test(value);
    const hasLowerCase = /[a-z]/.test(value);
    const hasNumbers = /\d/.test(value);
    const hasNonalphas = /\W/.test(value);

    return value.length >= minLength && hasUpperCase && hasLowerCase && hasNumbers && hasNonalphas;
  }

  // Custom validator for organization slugs
  static validateSlug(value: string): boolean {
    return /^[a-z0-9-]+$/.test(value) && value.length >= 3 && value.length <= 100;
  }

  // Handle validation errors
  static handleValidationErrors(req: Request, res: Response, next: NextFunction): void {
    const errors = validationResult(req);

    if (!errors.isEmpty()) {
      const formattedErrors = errors.array().map((error) => ({
        field: error.type === 'field' ? error.path : 'unknown',
        message: error.msg,
        value: error.value,
      }));

      throw new ApiError(400, 'Validation failed', formattedErrors);
    }

    next();
  }

  // Registration validation
  static registerValidation = [
    body('email')
      .trim()
      .isEmail()
      .normalizeEmail({ all_lowercase: true })
      .withMessage('Valid email is required'),

    body('password')
      .trim()
      .isLength({ min: 8, max: 128 })
      .withMessage('Password must be between 8 and 128 characters')
      .custom(this.validatePassword)
      .withMessage('Password must contain uppercase, lowercase, number, and special character'),

    body('firstName')
      .trim()
      .isLength({ min: 1, max: 100 })
      .withMessage('First name is required and must be less than 100 characters')
      .custom((value) => /^[a-zA-Z\s'-]+$/.test(value))
      .withMessage('First name can only contain letters, spaces, hyphens, and apostrophes'),

    body('lastName')
      .trim()
      .isLength({ min: 1, max: 100 })
      .withMessage('Last name is required and must be less than 100 characters')
      .custom((value) => /^[a-zA-Z\s'-]+$/.test(value))
      .withMessage('Last name can only contain letters, spaces, hyphens, and apostrophes'),

    body('organizationName')
      .optional()
      .trim()
      .isLength({ min: 1, max: 255 })
      .withMessage('Organization name must be less than 255 characters')
      .customSanitizer(this.sanitizeHtml),
  ];

  // Login validation
  static loginValidation = [
    body('email')
      .trim()
      .isEmail()
      .normalizeEmail({ all_lowercase: true })
      .withMessage('Valid email is required'),

    body('password').trim().notEmpty().withMessage('Password is required'),
  ];

  // Password reset validation
  static passwordResetValidation = [
    body('email')
      .trim()
      .isEmail()
      .normalizeEmail({ all_lowercase: true })
      .withMessage('Valid email is required'),
  ];

  // Password reset confirmation validation
  static passwordResetConfirmValidation = [
    body('token')
      .trim()
      .isLength({ min: 32, max: 128 })
      .withMessage('Valid reset token is required'),

    body('password')
      .trim()
      .isLength({ min: 8, max: 128 })
      .withMessage('Password must be between 8 and 128 characters')
      .custom(this.validatePassword)
      .withMessage('Password must contain uppercase, lowercase, number, and special character'),
  ];

  // Email verification validation
  static emailVerificationValidation = [
    body('token')
      .trim()
      .isLength({ min: 32, max: 128 })
      .withMessage('Valid verification token is required'),
  ];

  // UUID parameter validation
  static uuidParam(paramName: string) {
    return param(paramName).isUUID().withMessage(`${paramName} must be a valid UUID`);
  }

  // Pagination query validation
  static paginationValidation = [
    query('page')
      .optional()
      .isInt({ min: 1 })
      .withMessage('Page must be a positive integer')
      .toInt(),

    query('limit')
      .optional()
      .isInt({ min: 1, max: 100 })
      .withMessage('Limit must be between 1 and 100')
      .toInt(),

    query('sort').optional().isIn(['asc', 'desc']).withMessage('Sort must be either asc or desc'),
  ];

  // Organization validation
  static organizationValidation = [
    body('name')
      .trim()
      .isLength({ min: 1, max: 255 })
      .withMessage('Organization name is required and must be less than 255 characters')
      .customSanitizer(this.sanitizeHtml),

    body('slug')
      .optional()
      .trim()
      .custom(this.validateSlug)
      .withMessage('Slug must contain only lowercase letters, numbers, and hyphens'),

    body('description')
      .optional()
      .trim()
      .isLength({ max: 1000 })
      .withMessage('Description must be less than 1000 characters')
      .customSanitizer(this.sanitizeHtml),
  ];
}
```

## Files to Create

1. `src/controllers/auth-controller.ts` - Registration endpoint controller
2. `src/services/email-verification-service.ts` - Email verification service
3. `src/services/rate-limit-service.ts` - Rate limiting service
4. `src/middleware/validation-middleware.ts` - Input validation middleware
5. `src/routes/auth-routes.ts` - Authentication routes

## Dependencies

- express-validator for input validation
- ioredis for rate limiting
- isomorphic-dompurify for HTML sanitization
- date-fns for date handling
- bcrypt for password hashing

## Testing

1. Unit tests for registration flow
2. Rate limiting integration tests
3. Input validation tests with malicious inputs
4. Email verification token tests
5. Security tests for common vulnerabilities

## Notes

- Use secure random token generation
- Implement proper error handling and logging
- Add comprehensive input sanitization
- Monitor rate limiting effectiveness
- Consider CAPTCHA for additional protection
