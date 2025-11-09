# Implementation Plan: Task 2 - Authentication Framework

**Story**: 1.2 Authentication & Authorization System  
**Task**: 2 - Authentication Framework  
**Acceptance Criteria**: #2, #3, #5 - Secure password hashing using bcrypt or Argon2; JWT-based authentication with refresh tokens; Session management with proper logout

## Overview

Implement secure authentication framework with password hashing, JWT tokens, refresh token rotation, and secure session management.

## Implementation Steps

### Subtask 2.1: Implement secure password hashing with bcrypt (12+ rounds)

**Objective**: Create secure password hashing and verification system

**File**: `src/services/password-service.ts`

```typescript
import bcrypt from 'bcrypt';
import { randomBytes } from 'crypto';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface PasswordHashResult {
  hash: string;
  salt: string;
  rounds: number;
}

export class PasswordService {
  private readonly SALT_ROUNDS = 12;
  private readonly PEPPER = process.env.PASSWORD_PEPPER || '';

  async hashPassword(password: string): Promise<PasswordHashResult> {
    try {
      // Generate salt
      const salt = await bcrypt.genSalt(this.SALT_ROUNDS);

      // Add pepper if configured
      const pepperedPassword = password + this.PEPPER;

      // Hash password
      const hash = await bcrypt.hash(pepperedPassword, salt);

      logger.debug('Password hashed successfully', {
        saltRounds: this.SALT_ROUNDS,
        hashLength: hash.length,
      });

      return {
        hash,
        salt,
        rounds: this.SALT_ROUNDS,
      };
    } catch (error) {
      logger.error('Password hashing failed', { error });
      throw new ApiError(500, 'Failed to hash password');
    }
  }

  async verifyPassword(password: string, hash: string): Promise<boolean> {
    try {
      // Add pepper if configured
      const pepperedPassword = password + this.PEPPER;

      // Verify password
      const isValid = await bcrypt.compare(pepperedPassword, hash);

      logger.debug('Password verification completed', {
        isValid,
        hashLength: hash.length,
      });

      return isValid;
    } catch (error) {
      logger.error('Password verification failed', { error });
      throw new ApiError(500, 'Failed to verify password');
    }
  }

  async checkPasswordStrength(password: string): Promise<{
    score: number;
    feedback: string[];
    isStrong: boolean;
  }> {
    const feedback: string[] = [];
    let score = 0;

    // Length check
    if (password.length >= 12) {
      score += 2;
    } else if (password.length >= 8) {
      score += 1;
      feedback.push('Consider using a longer password (12+ characters)');
    } else {
      feedback.push('Password must be at least 8 characters long');
    }

    // Character variety checks
    if (/[a-z]/.test(password)) score += 1;
    else feedback.push('Include lowercase letters');

    if (/[A-Z]/.test(password)) score += 1;
    else feedback.push('Include uppercase letters');

    if (/\d/.test(password)) score += 1;
    else feedback.push('Include numbers');

    if (/[^a-zA-Z\d]/.test(password)) score += 1;
    else feedback.push('Include special characters');

    // Common patterns check
    if (this.hasCommonPatterns(password)) {
      score -= 2;
      feedback.push('Avoid common patterns or dictionary words');
    }

    // Repeated characters check
    if (this.hasRepeatedCharacters(password)) {
      score -= 1;
      feedback.push('Avoid repeated characters');
    }

    const isStrong = score >= 5;

    return {
      score: Math.max(0, Math.min(8, score)),
      feedback,
      isStrong,
    };
  }

  private hasCommonPatterns(password: string): boolean {
    const commonPatterns = [
      /123456/,
      /password/i,
      /qwerty/i,
      /admin/i,
      /letmein/i,
      /welcome/i,
      /abc123/i,
    ];

    return commonPatterns.some((pattern) => pattern.test(password));
  }

  private hasRepeatedCharacters(password: string): boolean {
    // Check for 3 or more consecutive identical characters
    return /(.)\1{2,}/.test(password);
  }

  generateSecurePassword(length: number = 16): string {
    const charset =
      'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()_+-=[]{}|;:,.<>?';
    let password = '';

    // Ensure at least one character from each category
    const categories = [
      'abcdefghijklmnopqrstuvwxyz',
      'ABCDEFGHIJKLMNOPQRSTUVWXYZ',
      '0123456789',
      '!@#$%^&*()_+-=[]{}|;:,.<>?',
    ];

    // Add one character from each category
    for (const category of categories) {
      password += category[Math.floor(Math.random() * category.length)];
    }

    // Fill remaining length with random characters
    for (let i = password.length; i < length; i++) {
      password += charset[Math.floor(Math.random() * charset.length)];
    }

    // Shuffle the password
    return password
      .split('')
      .sort(() => Math.random() - 0.5)
      .join('');
  }
}
```

### Subtask 2.2: Create JWT token generation with RS256 signing

**Objective**: Implement secure JWT token generation and validation

**File**: `src/services/jwt-service.ts`

```typescript
import jwt from 'jsonwebtoken';
import { readFileSync } from 'fs';
import { join } from 'path';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface JWTPayload {
  sub: string; // User ID
  email: string;
  organizationId?: string;
  role: string;
  permissions: string[];
  tokenId: string;
  type: 'access' | 'refresh';
  iat?: number;
  exp?: number;
}

export interface TokenPair {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
  tokenType: 'Bearer';
}

export class JWTService {
  private privateKey: string;
  private publicKey: string;
  private readonly ACCESS_TOKEN_EXPIRY = '15m';
  private readonly REFRESH_TOKEN_EXPIRY = '7d';

  constructor() {
    try {
      // Load RSA keys
      const keyPath = process.env.JWT_KEY_PATH || join(process.cwd(), 'keys');
      this.privateKey = readFileSync(join(keyPath, 'private.pem'), 'utf8');
      this.publicKey = readFileSync(join(keyPath, 'public.pem'), 'utf8');
    } catch (error) {
      logger.error('Failed to load JWT keys', { error });
      throw new Error('JWT keys not found. Please generate RSA keys.');
    }
  }

  async generateTokenPair(user: {
    id: string;
    email: string;
    organizationId?: string;
    role: string;
    permissions: string[];
  }): Promise<TokenPair> {
    try {
      const tokenId = this.generateTokenId();
      const now = Math.floor(Date.now() / 1000);

      // Access token payload
      const accessPayload: JWTPayload = {
        sub: user.id,
        email: user.email,
        organizationId: user.organizationId,
        role: user.role,
        permissions: user.permissions,
        tokenId,
        type: 'access',
        iat: now,
      };

      // Refresh token payload
      const refreshPayload: JWTPayload = {
        sub: user.id,
        email: user.email,
        organizationId: user.organizationId,
        role: user.role,
        permissions: user.permissions,
        tokenId,
        type: 'refresh',
        iat: now,
      };

      // Generate tokens
      const accessToken = jwt.sign(accessPayload, this.privateKey, {
        algorithm: 'RS256',
        expiresIn: this.ACCESS_TOKEN_EXPIRY,
        issuer: process.env.JWT_ISSUER || 'test-platform',
        audience: process.env.JWT_AUDIENCE || 'test-platform-users',
      });

      const refreshToken = jwt.sign(refreshPayload, this.privateKey, {
        algorithm: 'RS256',
        expiresIn: this.REFRESH_TOKEN_EXPIRY,
        issuer: process.env.JWT_ISSUER || 'test-platform',
        audience: process.env.JWT_AUDIENCE || 'test-platform-users',
      });

      // Store refresh token in database
      await this.storeRefreshToken(user.id, tokenId, refreshToken);

      logger.info('Token pair generated', {
        userId: user.id,
        tokenId,
        expiresIn: this.ACCESS_TOKEN_EXPIRY,
      });

      return {
        accessToken,
        refreshToken,
        expiresIn: 15 * 60, // 15 minutes in seconds
        tokenType: 'Bearer',
      };
    } catch (error) {
      logger.error('Failed to generate token pair', { error, userId: user.id });
      throw new ApiError(500, 'Failed to generate authentication tokens');
    }
  }

  async verifyToken(token: string): Promise<JWTPayload> {
    try {
      const decoded = jwt.verify(token, this.publicKey, {
        algorithms: ['RS256'],
        issuer: process.env.JWT_ISSUER || 'test-platform',
        audience: process.env.JWT_AUDIENCE || 'test-platform-users',
      }) as JWTPayload;

      // Check if token is blacklisted
      if (await this.isTokenBlacklisted(decoded.tokenId)) {
        throw new ApiError(401, 'Token has been revoked');
      }

      return decoded;
    } catch (error) {
      if (error instanceof jwt.TokenExpiredError) {
        throw new ApiError(401, 'Token has expired');
      } else if (error instanceof jwt.JsonWebTokenError) {
        throw new ApiError(401, 'Invalid token');
      } else {
        logger.error('Token verification failed', { error });
        throw new ApiError(401, 'Token verification failed');
      }
    }
  }

  async refreshToken(refreshToken: string): Promise<TokenPair> {
    try {
      // Verify refresh token
      const payload = await this.verifyToken(refreshToken);

      if (payload.type !== 'refresh') {
        throw new ApiError(401, 'Invalid token type');
      }

      // Check if refresh token exists in database
      const storedToken = await this.getStoredRefreshToken(payload.sub, payload.tokenId);
      if (!storedToken || storedToken.revoked_at) {
        throw new ApiError(401, 'Refresh token has been revoked');
      }

      // Get current user data
      const user = await this.getUserById(payload.sub);
      if (!user) {
        throw new ApiError(401, 'User not found');
      }

      // Revoke old refresh token
      await this.revokeRefreshToken(payload.sub, payload.tokenId);

      // Generate new token pair
      return await this.generateTokenPair({
        id: user.id,
        email: user.email,
        organizationId: user.organization_id,
        role: user.role,
        permissions: user.permissions || [],
      });
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Token refresh failed', { error });
      throw new ApiError(401, 'Failed to refresh token');
    }
  }

  async revokeToken(tokenId: string, userId: string): Promise<void> {
    try {
      // Add to blacklist
      await this.blacklistToken(tokenId);

      // Revoke refresh token
      await this.revokeRefreshToken(userId, tokenId);

      logger.info('Token revoked', { tokenId, userId });
    } catch (error) {
      logger.error('Failed to revoke token', { error, tokenId, userId });
      throw new ApiError(500, 'Failed to revoke token');
    }
  }

  async revokeAllUserTokens(userId: string): Promise<void> {
    try {
      // Revoke all refresh tokens for user
      await this.revokeAllUserRefreshTokens(userId);

      logger.info('All user tokens revoked', { userId });
    } catch (error) {
      logger.error('Failed to revoke all user tokens', { error, userId });
      throw new ApiError(500, 'Failed to revoke all tokens');
    }
  }

  private generateTokenId(): string {
    return `tok_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private async storeRefreshToken(userId: string, tokenId: string, token: string): Promise<void> {
    const db = await import('../database/connection').then((m) => m.default);

    await db('refresh_tokens').insert({
      id: tokenId,
      user_id: userId,
      token_hash: this.hashToken(token),
      expires_at: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000), // 7 days
      created_at: new Date(),
    });
  }

  private async getStoredRefreshToken(userId: string, tokenId: string): Promise<any> {
    const db = await import('../database/connection').then((m) => m.default);

    return await db('refresh_tokens').where('user_id', userId).where('id', tokenId).first();
  }

  private async revokeRefreshToken(userId: string, tokenId: string): Promise<void> {
    const db = await import('../database/connection').then((m) => m.default);

    await db('refresh_tokens').where('user_id', userId).where('id', tokenId).update({
      revoked_at: new Date(),
      updated_at: new Date(),
    });
  }

  private async revokeAllUserRefreshTokens(userId: string): Promise<void> {
    const db = await import('../database/connection').then((m) => m.default);

    await db('refresh_tokens').where('user_id', userId).whereNull('revoked_at').update({
      revoked_at: new Date(),
      updated_at: new Date(),
    });
  }

  private async blacklistToken(tokenId: string): Promise<void> {
    const db = await import('../database/connection').then((m) => m.default);

    await db('token_blacklist').insert({
      token_id: tokenId,
      blacklisted_at: new Date(),
    });
  }

  private async isTokenBlacklisted(tokenId: string): Promise<boolean> {
    const db = await import('../database/connection').then((m) => m.default);

    const result = await db('token_blacklist').where('token_id', tokenId).first();

    return !!result;
  }

  private hashToken(token: string): string {
    const crypto = require('crypto');
    return crypto.createHash('sha256').update(token).digest('hex');
  }

  private async getUserById(userId: string): Promise<any> {
    const db = await import('../database/connection').then((m) => m.default);

    return await db('users').where('id', userId).where('status', 'active').first();
  }
}
```

### Subtask 2.3: Implement refresh token rotation system

**Objective**: Create secure refresh token rotation to prevent token reuse

**File**: `src/services/refresh-token-service.ts`

```typescript
import { db } from '../database/connection';
import { logger } from '../observability/logger';
import { ApiError } from '../utils/api-error';

export interface RefreshTokenRecord {
  id: string;
  user_id: string;
  token_hash: string;
  expires_at: Date;
  created_at: Date;
  revoked_at?: Date;
  last_used_at?: Date;
  device_info?: string;
  ip_address?: string;
}

export class RefreshTokenService {
  private readonly MAX_ACTIVE_TOKENS = 5;
  private readonly TOKEN_REUSE_DETECTION_WINDOW = 30 * 60 * 1000; // 30 minutes

  async createRefreshToken(
    userId: string,
    tokenId: string,
    token: string,
    context?: {
      deviceInfo?: string;
      ipAddress?: string;
    }
  ): Promise<void> {
    try {
      // Clean up old expired tokens
      await this.cleanupExpiredTokens();

      // Check if user has too many active tokens
      await this.enforceTokenLimit(userId);

      // Store new refresh token
      await db('refresh_tokens').insert({
        id: tokenId,
        user_id: userId,
        token_hash: this.hashToken(token),
        expires_at: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000), // 7 days
        device_info: context?.deviceInfo,
        ip_address: context?.ipAddress,
        created_at: new Date(),
      });

      logger.info('Refresh token created', {
        userId,
        tokenId,
        deviceInfo: context?.deviceInfo,
        ipAddress: context?.ipAddress,
      });
    } catch (error) {
      logger.error('Failed to create refresh token', { error, userId });
      throw new ApiError(500, 'Failed to create refresh token');
    }
  }

  async validateRefreshToken(
    userId: string,
    tokenId: string,
    token: string
  ): Promise<RefreshTokenRecord> {
    try {
      const storedToken = await db('refresh_tokens')
        .where('user_id', userId)
        .where('id', tokenId)
        .first();

      if (!storedToken) {
        throw new ApiError(401, 'Refresh token not found');
      }

      if (storedToken.revoked_at) {
        throw new ApiError(401, 'Refresh token has been revoked');
      }

      if (new Date() > storedToken.expires_at) {
        throw new ApiError(401, 'Refresh token has expired');
      }

      // Verify token hash
      if (storedToken.token_hash !== this.hashToken(token)) {
        // Possible token reuse attack - revoke all user tokens
        await this.revokeAllUserTokens(userId);
        throw new ApiError(401, 'Invalid refresh token');
      }

      // Update last used timestamp
      await db('refresh_tokens').where('id', tokenId).update({
        last_used_at: new Date(),
        updated_at: new Date(),
      });

      return storedToken;
    } catch (error) {
      if (error instanceof ApiError) {
        throw error;
      }
      logger.error('Refresh token validation failed', { error, userId, tokenId });
      throw new ApiError(401, 'Invalid refresh token');
    }
  }

  async rotateRefreshToken(
    userId: string,
    oldTokenId: string,
    oldToken: string,
    newTokenId: string,
    newToken: string,
    context?: {
      deviceInfo?: string;
      ipAddress?: string;
    }
  ): Promise<void> {
    try {
      // Validate old token
      await this.validateRefreshToken(userId, oldTokenId, oldToken);

      // Revoke old token
      await this.revokeRefreshToken(oldTokenId);

      // Create new token
      await this.createRefreshToken(userId, newTokenId, newToken, context);

      logger.info('Refresh token rotated', {
        userId,
        oldTokenId,
        newTokenId,
        deviceInfo: context?.deviceInfo,
        ipAddress: context?.ipAddress,
      });
    } catch (error) {
      logger.error('Refresh token rotation failed', { error, userId });
      throw new ApiError(401, 'Failed to rotate refresh token');
    }
  }

  async revokeRefreshToken(tokenId: string): Promise<void> {
    try {
      await db('refresh_tokens').where('id', tokenId).update({
        revoked_at: new Date(),
        updated_at: new Date(),
      });

      logger.info('Refresh token revoked', { tokenId });
    } catch (error) {
      logger.error('Failed to revoke refresh token', { error, tokenId });
      throw new ApiError(500, 'Failed to revoke refresh token');
    }
  }

  async revokeAllUserTokens(userId: string, reason?: string): Promise<void> {
    try {
      await db('refresh_tokens').where('user_id', userId).whereNull('revoked_at').update({
        revoked_at: new Date(),
        updated_at: new Date(),
      });

      logger.info('All user refresh tokens revoked', {
        userId,
        reason,
      });
    } catch (error) {
      logger.error('Failed to revoke all user tokens', { error, userId });
      throw new ApiError(500, 'Failed to revoke all tokens');
    }
  }

  async getUserActiveTokens(userId: string): Promise<RefreshTokenRecord[]> {
    try {
      return await db('refresh_tokens')
        .where('user_id', userId)
        .whereNull('revoked_at')
        .where('expires_at', '>', new Date())
        .orderBy('created_at', 'desc');
    } catch (error) {
      logger.error('Failed to get user active tokens', { error, userId });
      return [];
    }
  }

  async cleanupExpiredTokens(): Promise<number> {
    try {
      const deletedCount = await db('refresh_tokens').where('expires_at', '<', new Date()).del();

      if (deletedCount > 0) {
        logger.info('Cleaned up expired refresh tokens', {
          deletedCount,
        });
      }

      return deletedCount;
    } catch (error) {
      logger.error('Failed to cleanup expired tokens', { error });
      return 0;
    }
  }

  private async enforceTokenLimit(userId: string): Promise<void> {
    try {
      const activeTokens = await db('refresh_tokens')
        .where('user_id', userId)
        .whereNull('revoked_at')
        .where('expires_at', '>', new Date())
        .orderBy('created_at', 'asc');

      if (activeTokens.length >= this.MAX_ACTIVE_TOKENS) {
        // Revoke oldest tokens
        const tokensToRevoke = activeTokens.slice(
          0,
          activeTokens.length - this.MAX_ACTIVE_TOKENS + 1
        );

        for (const token of tokensToRevoke) {
          await this.revokeRefreshToken(token.id);
        }

        logger.info('Revoked excess refresh tokens', {
          userId,
          revokedCount: tokensToRevoke.length,
        });
      }
    } catch (error) {
      logger.error('Failed to enforce token limit', { error, userId });
    }
  }

  private hashToken(token: string): string {
    const crypto = require('crypto');
    return crypto.createHash('sha256').update(token).digest('hex');
  }
}
```

### Subtask 2.4: Create login endpoint with authentication logic

**Objective**: Implement secure login endpoint with comprehensive authentication

**File**: `src/controllers/auth-controller.ts` (continued)

```typescript
// Add to AuthController class

async login(req: Request, res: Response): Promise<void> {
  try {
    // Check validation errors
    const errors = validationResult(req);
    if (!errors.isEmpty()) {
      throw new ApiError(400, 'Validation failed', errors.array());
    }

    const { email, password } = req.body;

    // Check rate limiting
    const rateLimitKey = `login:${req.ip}:${email}`;
    const isAllowed = await this.rateLimitService.checkLimit(rateLimitKey, {
      windowMs: 15 * 60 * 1000, // 15 minutes
      maxAttempts: 5,
    });

    if (!isAllowed) {
      throw new ApiError(429, 'Too many login attempts. Please try again later.');
    }

    // Find user
    const user = await this.authService.findUserByEmail(email);
    if (!user) {
      await this.handleFailedLogin(email, req.ip, 'USER_NOT_FOUND');
      throw new ApiError(401, 'Invalid email or password');
    }

    // Check account status
    if (user.status !== 'active') {
      await this.handleFailedLogin(email, req.ip, 'ACCOUNT_INACTIVE');
      throw new ApiError(401, 'Account is not active');
    }

    // Check if email is verified
    if (!user.email_verified) {
      throw new ApiError(401, 'Please verify your email before logging in');
    }

    // Check if account is locked
    if (user.locked_until && new Date() < user.locked_until) {
      throw new ApiError(423, 'Account is temporarily locked due to too many failed attempts');
    }

    // Verify password
    const isPasswordValid = await this.authService.verifyPassword(password, user.password_hash);
    if (!isPasswordValid) {
      await this.handleFailedLogin(email, req.ip, 'INVALID_PASSWORD', user.id);
      throw new ApiError(401, 'Invalid email or password');
    }

    // Reset failed login attempts
    await this.authService.resetFailedLoginAttempts(user.id);

    // Get user's organization and permissions
    const userOrg = await this.authService.getUserOrganization(user.id);
    const permissions = await this.authService.getUserPermissions(user.id, userOrg?.organization_id);

    // Generate tokens
    const tokens = await this.authService.generateTokenPair({
      id: user.id,
      email: user.email,
      organizationId: userOrg?.organization_id,
      role: user.role,
      permissions,
    });

    // Update login tracking
    await this.authService.updateLoginTracking(user.id, {
      ipAddress: req.ip,
      userAgent: req.get('User-Agent'),
      loginAt: new Date(),
    });

    // Emit event for audit trail
    await this.authService.emitEvent('USER.LOGGED_IN', {
      userId: user.id,
      email,
      organizationId: userOrg?.organization_id,
      ipAddress: req.ip,
      userAgent: req.get('User-Agent'),
    });

    logger.info('User logged in successfully', {
      userId: user.id,
      email,
      ipAddress: req.ip,
      organizationId: userOrg?.organization_id,
    });

    res.json({
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
    });
  } catch (error) {
    if (error instanceof ApiError) {
      res.status(error.statusCode).json({
        error: error.message,
        details: error.details,
      });
    } else {
      logger.error('Login error', { error, email: req.body.email });
      res.status(500).json({ error: 'Internal server error' });
    }
  }
}

private async handleFailedLogin(
  email: string,
  ipAddress: string,
  reason: string,
  userId?: string
): Promise<void> {
  try {
    if (userId) {
      await this.authService.incrementFailedLoginAttempts(userId);
    }

    await this.authService.emitEvent('AUTH.LOGIN_FAILED', {
      email,
      ipAddress,
      reason,
      userId,
    });

    logger.warn('Failed login attempt', {
      email,
      ipAddress,
      reason,
      userId,
    });
  } catch (error) {
    logger.error('Failed to handle failed login', { error, email, ipAddress });
  }
}
```

### Subtask 2.5: Implement secure logout with token invalidation

**Objective**: Create secure logout with proper token invalidation

**File**: `src/controllers/auth-controller.ts` (continued)

```typescript
async logout(req: Request, res: Response): Promise<void> {
  try {
    const authHeader = req.headers.authorization;
    const refreshToken = req.body.refreshToken;

    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      throw new ApiError(400, 'Access token is required');
    }

    const accessToken = authHeader.substring(7);

    // Verify access token to get user info
    const payload = await this.authService.verifyToken(accessToken);

    // Revoke access token
    await this.authService.revokeToken(payload.tokenId, payload.sub);

    // Revoke refresh token if provided
    if (refreshToken) {
      try {
        const refreshPayload = await this.authService.verifyToken(refreshToken);
        if (refreshPayload.type === 'refresh' && refreshPayload.sub === payload.sub) {
          await this.authService.revokeToken(refreshPayload.tokenId, refreshPayload.sub);
        }
      } catch (error) {
        // Refresh token might be expired or invalid, but that's okay for logout
        logger.debug('Refresh token invalid during logout', { error });
      }
    }

    // Emit event for audit trail
    await this.authService.emitEvent('USER.LOGGED_OUT', {
      userId: payload.sub,
      email: payload.email,
      organizationId: payload.organizationId,
      ipAddress: req.ip,
      userAgent: req.get('User-Agent'),
    });

    logger.info('User logged out successfully', {
      userId: payload.sub,
      email: payload.email,
      ipAddress: req.ip,
    });

    res.json({ message: 'Logout successful' });
  } catch (error) {
    if (error instanceof ApiError) {
      res.status(error.statusCode).json({
        error: error.message,
      });
    } else {
      logger.error('Logout error', { error });
      res.status(500).json({ error: 'Internal server error' });
    }
  }
}

async logoutAll(req: Request, res: Response): Promise<void> {
  try {
    const authHeader = req.headers.authorization;

    if (!authHeader || !authHeader.startsWith('Bearer ')) {
      throw new ApiError(400, 'Access token is required');
    }

    const accessToken = authHeader.substring(7);
    const payload = await this.authService.verifyToken(accessToken);

    // Revoke all user tokens
    await this.authService.revokeAllUserTokens(payload.sub);

    // Emit event for audit trail
    await this.authService.emitEvent('USER.LOGGED_OUT_ALL', {
      userId: payload.sub,
      email: payload.email,
      organizationId: payload.organizationId,
      ipAddress: req.ip,
      userAgent: req.get('User-Agent'),
    });

    logger.info('User logged out from all devices', {
      userId: payload.sub,
      email: payload.email,
      ipAddress: req.ip,
    });

    res.json({ message: 'Logged out from all devices successfully' });
  } catch (error) {
    if (error instanceof ApiError) {
      res.status(error.statusCode).json({
        error: error.message,
      });
    } else {
      logger.error('Logout all error', { error });
      res.status(500).json({ error: 'Internal server error' });
    }
  }
}
```

## Files to Create

1. `src/services/password-service.ts` - Password hashing and verification
2. `src/services/jwt-service.ts` - JWT token generation and validation
3. `src/services/refresh-token-service.ts` - Refresh token rotation
4. Update `src/controllers/auth-controller.ts` - Login and logout endpoints
5. Database migration for refresh_tokens and token_blacklist tables

## Dependencies

- bcrypt for password hashing
- jsonwebtoken for JWT tokens
- Node.js crypto module for secure random generation
- RSA key pair for token signing

## Testing

1. Password hashing and verification tests
2. JWT token generation and validation tests
3. Refresh token rotation tests
4. Login/logout flow tests
5. Security tests for token reuse attacks

## Notes

- Use RS256 for JWT tokens (asymmetric keys)
- Implement proper token rotation to prevent reuse
- Monitor failed login attempts for security
- Use secure HTTP-only cookies for tokens in web apps
- Consider implementing device fingerprinting for additional security
