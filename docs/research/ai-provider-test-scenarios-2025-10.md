# AI Provider Test Scenarios

**Date**: October 30, 2025  
**Purpose**: Validate AI provider capabilities against Tamma's autonomous development workflow  
**Test Issue**: "Add user authentication with JWT tokens"

## Test Methodology

### Evaluation Criteria

- **Code Quality**: Correctness, idiomatic code, best practices
- **Completeness**: Full implementation, error handling, edge cases
- **Test Coverage**: Generated tests, edge case handling
- **Documentation**: Code comments, docstrings, README updates
- **Security**: Proper authentication, input validation, error messages
- **Performance**: Efficient algorithms, appropriate data structures

### Scoring System

- **10/10**: Exceeds expectations, production-ready
- **8-9/10**: Meets expectations, minor improvements needed
- **6-7/10**: Acceptable, moderate improvements needed
- **<6/10**: Requires significant revision

## Test Scenarios

### Scenario 1: Issue Analysis

**Prompt**:

```
Analyze this GitHub issue for implementing user authentication with JWT tokens:

Issue: Add user authentication with JWT tokens
Description: Implement secure user authentication using JSON Web Tokens (JWT) for API access
Requirements:
- User login with email/password
- JWT token generation on successful authentication
- Token validation middleware
- Token refresh mechanism
- Password reset functionality
- Rate limiting for login attempts
- Secure password storage (bcrypt)
- Error handling for invalid tokens
- Logout functionality

Please analyze the requirements and identify:
1. Technical complexity (1-10)
2. Potential ambiguities
3. Missing requirements
4. Security considerations
5. Implementation approach
6. Estimated development time
7. Recommended file structure
```

#### Expected Output Structure

```json
{
  "complexity": 7,
  "ambiguities": [
    "What password complexity requirements?",
    "JWT expiration time not specified",
    "Rate limiting thresholds unclear"
  ],
  "missing_requirements": [
    "Account lockout policy",
    "Multi-factor authentication",
    "Session management"
  ],
  "security_considerations": [
    "CSRF protection",
    "HTTPS enforcement",
    "Token blacklisting",
    "Secure password reset flow"
  ],
  "implementation_approach": "Layered authentication with middleware",
  "estimated_time": "8-12 hours",
  "file_structure": [
    "src/auth/login.ts",
    "src/auth/jwt.ts",
    "src/auth/middleware.ts",
    "src/auth/password-reset.ts",
    "src/auth/logout.ts",
    "tests/auth.test.ts"
  ]
}
```

### Scenario 2: Code Generation

**Prompt**:

```
Generate TypeScript code for user authentication with JWT tokens based on the analysis:

Requirements:
- Use Express.js framework
- Implement bcrypt for password hashing
- Use jsonwebtoken library
- Include comprehensive error handling
- Add input validation with Joi
- Implement rate limiting with express-rate-limit
- Include TypeScript types
- Add logging with structured format
- Follow SOLID principles

File: src/auth/login.ts
```

#### Expected Code Quality

```typescript
import express from 'express';
import bcrypt from 'bcryptjs';
import jwt from 'jsonwebtoken';
import Joi from 'joi';
import rateLimit from 'express-rate-limit';
import { logger } from '../utils/logger';

interface LoginRequest {
  email: string;
  password: string;
}

interface LoginResponse {
  success: boolean;
  token?: string;
  user?: {
    id: string;
    email: string;
  };
  error?: string;
}

const loginSchema = Joi.object({
  email: Joi.string().email().required(),
  password: Joi.string().min(8).required(),
});

const loginLimiter = rateLimit({
  windowMs: 15 * 60 * 1000, // 15 minutes
  max: 5, // limit each IP to 5 requests per windowMs
  message: 'Too many login attempts, please try again later.',
});

export class AuthService {
  constructor(private readonly jwtSecret: string) {}

  async login(req: express.Request, res: express.Response): Promise<void> {
    const startTime = Date.now();
    const traceId = generateTraceId();

    logger.info('Login attempt started', {
      traceId,
      ip: req.ip,
      email: req.body.email,
    });

    try {
      // Validate input
      const { error, value } = loginSchema.validate(req.body);
      if (error) {
        logger.warn('Invalid login input', {
          traceId,
          error: error.details,
          input: { email: req.body.email },
        });
        res.status(400).json({
          success: false,
          error: 'Invalid input format',
        });
        return;
      }

      // Authenticate user (implementation would go here)
      const user = await this.authenticateUser(value.email, value.password);
      if (!user) {
        logger.warn('Authentication failed', {
          traceId,
          email: value.email,
          reason: 'invalid_credentials',
        });
        res.status(401).json({
          success: false,
          error: 'Invalid credentials',
        });
        return;
      }

      // Generate JWT token
      const token = jwt.sign({ userId: user.id, email: user.email }, this.jwtSecret, {
        expiresIn: '24h',
      });

      const duration = Date.now() - startTime;
      logger.info('Login successful', {
        traceId,
        userId: user.id,
        duration,
      });

      res.json({
        success: true,
        token,
        user: { id: user.id, email: user.email },
      });
    } catch (error) {
      const duration = Date.now() - startTime;
      logger.error('Login error', {
        traceId,
        error: error.message,
        duration,
      });

      res.status(500).json({
        success: false,
        error: 'Internal server error',
      });
    }
  }

  private async authenticateUser(email: string, password: string): Promise<any> {
    // Implementation would query database
    // This is a placeholder for testing
    return null;
  }
}

function generateTraceId(): string {
  return `trace-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
}

export default AuthService;
```

### Scenario 3: Test Generation

**Prompt**:

```
Generate comprehensive unit tests for the AuthService.login method using Vitest:

Test cases to include:
1. Valid login credentials
2. Invalid email format
3. Password too short
4. Missing required fields
5. Rate limiting
6. Database connection errors
7. JWT generation errors
8. Successful token generation
9. Error logging verification
10. Performance benchmarks

Use TypeScript, include mocking, and achieve 100% code coverage.
```

#### Expected Test Quality

```typescript
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { Request, Response } from 'express';
import AuthService from '../src/auth/login';

describe('AuthService', () => {
  let authService: AuthService;
  let mockRequest: Partial<Request>;
  let mockResponse: Partial<Response>;
  let jsonSpy: any;
  let statusSpy: any;

  beforeEach(() => {
    authService = new AuthService('test-secret');
    mockRequest = {
      body: {},
      ip: '127.0.0.1',
    };
    mockResponse = {
      json: vi.fn(),
      status: vi.fn(() => mockResponse),
    };
    jsonSpy = vi.spyOn(mockResponse, 'json');
    statusSpy = vi.spyOn(mockResponse, 'status');
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  describe('login', () => {
    it('should successfully login with valid credentials', async () => {
      // Arrange
      mockRequest.body = {
        email: 'test@example.com',
        password: 'validpassword123',
      };

      // Mock successful authentication
      vi.spyOn(authService, 'authenticateUser').mockResolvedValue({
        id: 'user123',
        email: 'test@example.com',
      });

      // Act
      await authService.login(mockRequest as Request, mockResponse as Response);

      // Assert
      expect(statusSpy).toHaveBeenCalledWith(200);
      expect(jsonSpy).toHaveBeenCalledWith(
        expect.objectContaining({
          success: true,
          token: expect.any(String),
          user: {
            id: 'user123',
            email: 'test@example.com',
          },
        })
      );
    });

    it('should reject invalid email format', async () => {
      // Arrange
      mockRequest.body = {
        email: 'invalid-email',
        password: 'validpassword123',
      };

      // Act
      await authService.login(mockRequest as Request, mockResponse as Response);

      // Assert
      expect(statusSpy).toHaveBeenCalledWith(400);
      expect(jsonSpy).toHaveBeenCalledWith({
        success: false,
        error: 'Invalid input format',
      });
    });

    it('should reject password too short', async () => {
      // Arrange
      mockRequest.body = {
        email: 'test@example.com',
        password: '123',
      };

      // Act
      await authService.login(mockRequest as Request, mockResponse as Response);

      // Assert
      expect(statusSpy).toHaveBeenCalledWith(400);
      expect(jsonSpy).toHaveBeenCalledWith({
        success: false,
        error: 'Invalid input format',
      });
    });

    it('should handle missing email field', async () => {
      // Arrange
      mockRequest.body = {
        password: 'validpassword123',
      };

      // Act
      await authService.login(mockRequest as Request, mockResponse as Response);

      // Assert
      expect(statusSpy).toHaveBeenCalledWith(400);
      expect(jsonSpy).toHaveBeenCalledWith({
        success: false,
        error: 'Invalid input format',
      });
    });

    it('should reject invalid credentials', async () => {
      // Arrange
      mockRequest.body = {
        email: 'test@example.com',
        password: 'wrongpassword',
      };

      vi.spyOn(authService, 'authenticateUser').mockResolvedValue(null);

      // Act
      await authService.login(mockRequest as Request, mockResponse as Response);

      // Assert
      expect(statusSpy).toHaveBeenCalledWith(401);
      expect(jsonSpy).toHaveBeenCalledWith({
        success: false,
        error: 'Invalid credentials',
      });
    });

    it('should handle database errors gracefully', async () => {
      // Arrange
      mockRequest.body = {
        email: 'test@example.com',
        password: 'validpassword123',
      };

      vi.spyOn(authService, 'authenticateUser').mockRejectedValue(
        new Error('Database connection failed')
      );

      // Act
      await authService.login(mockRequest as Request, mockResponse as Response);

      // Assert
      expect(statusSpy).toHaveBeenCalledWith(500);
      expect(jsonSpy).toHaveBeenCalledWith({
        success: false,
        error: 'Internal server error',
      });
    });

    it('should complete within performance threshold', async () => {
      // Arrange
      mockRequest.body = {
        email: 'test@example.com',
        password: 'validpassword123',
      };

      vi.spyOn(authService, 'authenticateUser').mockResolvedValue({
        id: 'user123',
        email: 'test@example.com',
      });

      const startTime = Date.now();

      // Act
      await authService.login(mockRequest as Request, mockResponse as Response);

      // Assert
      const duration = Date.now() - startTime;
      expect(duration).toBeLessThan(1000); // Should complete in < 1 second
    });
  });
});
```

### Scenario 4: Code Review

**Prompt**:

```
Review this authentication code for security, performance, and best practices:

Code: [Provide the generated login code]

Focus on:
1. Security vulnerabilities
2. Performance bottlenecks
3. Code quality issues
4. Missing error handling
5. TypeScript best practices
6. SOLID principle violations
7. Logging improvements
8. Testing gaps

Provide specific recommendations with severity levels:
- CRITICAL: Security issues that must be fixed
- HIGH: Performance or major quality issues
- MEDIUM: Code improvements
- LOW: Nice to have enhancements
```

#### Expected Review Quality

```json
{
  "overall_score": 8.5,
  "issues": [
    {
      "severity": "CRITICAL",
      "category": "Security",
      "description": "JWT secret should be stored in environment variables, not hardcoded",
      "line": 15,
      "recommendation": "Move JWT secret to process.env.JWT_SECRET"
    },
    {
      "severity": "HIGH",
      "category": "Security",
      "description": "Missing account lockout after failed attempts",
      "recommendation": "Implement account lockout after 5 failed attempts for 30 minutes"
    },
    {
      "severity": "MEDIUM",
      "category": "Performance",
      "description": "bcrypt work factor not specified",
      "recommendation": "Use bcrypt.rounds=12 for optimal security/performance balance"
    },
    {
      "severity": "LOW",
      "category": "Code Quality",
      "description": "Add JSDoc comments for better documentation",
      "recommendation": "Document all public methods with JSDoc"
    }
  ],
  "strengths": [
    "Good input validation with Joi",
    "Proper error handling structure",
    "Structured logging implementation",
    "Rate limiting implemented",
    "TypeScript types well defined"
  ],
  "security_score": 7.5,
  "performance_score": 8.0,
  "maintainability_score": 9.0
}
```

### Scenario 5: Refactoring

**Prompt**:

```
Refactor this authentication code to improve SOLID principles and maintainability:

Current issues to address:
1. Single Responsibility Principle violations
2. Dependency injection improvements
3. Error handling centralization
4. Configuration management
5. Logging consistency
6. Testability improvements

Provide refactored code with explanations of changes made.
```

#### Expected Refactoring Quality

```typescript
// Configuration interface
interface AuthConfig {
  jwtSecret: string;
  jwtExpiration: string;
  bcryptRounds: number;
  rateLimitWindow: number;
  rateLimitMax: number;
}

// User repository interface
interface IUserRepository {
  findByEmail(email: string): Promise<User | null>;
  create(user: CreateUserDto): Promise<User>;
  updateLastLogin(userId: string): Promise<void>;
}

// Token service interface
interface ITokenService {
  generateToken(payload: any): string;
  verifyToken(token: string): any;
  refreshToken(oldToken: string): string;
}

// Rate limiting service interface
interface IRateLimitService {
  isAllowed(ip: string): Promise<boolean>;
  recordAttempt(ip: string): Promise<void>;
}

// Refactored AuthService
export class AuthService {
  constructor(
    private readonly config: AuthConfig,
    private readonly userRepository: IUserRepository,
    private readonly tokenService: ITokenService,
    private readonly rateLimitService: IRateLimitService,
    private readonly logger: ILogger
  ) {}

  async login(req: LoginRequest): Promise<LoginResponse> {
    const traceId = this.logger.generateTraceId();

    try {
      // Rate limiting check
      if (!(await this.rateLimitService.isAllowed(req.ip))) {
        return this.createErrorResponse('Too many attempts', 429);
      }

      // Input validation
      const validation = this.validateInput(req);
      if (!validation.isValid) {
        return this.createErrorResponse(validation.error, 400);
      }

      // Authentication
      const user = await this.userRepository.findByEmail(req.email);
      if (!user || !(await this.verifyPassword(req.password, user.passwordHash))) {
        await this.rateLimitService.recordAttempt(req.ip);
        return this.createErrorResponse('Invalid credentials', 401);
      }

      // Success path
      const token = this.tokenService.generateToken({
        userId: user.id,
        email: user.email,
      });

      await this.userRepository.updateLastLogin(user.id);

      this.logger.info('Login successful', { traceId, userId: user.id });

      return {
        success: true,
        token,
        user: { id: user.id, email: user.email },
      };
    } catch (error) {
      this.logger.error('Login error', { traceId, error: error.message });
      return this.createErrorResponse('Internal server error', 500);
    }
  }

  private validateInput(req: LoginRequest): ValidationResult {
    // Implementation
  }

  private async verifyPassword(password: string, hash: string): Promise<boolean> {
    // Implementation
  }

  private createErrorResponse(message: string, status: number): LoginResponse {
    return {
      success: false,
      error: message,
    };
  }
}
```

### Scenario 6: Documentation Generation

**Prompt**:

```
Generate comprehensive documentation for the authentication system:

Include:
1. API documentation with OpenAPI/Swagger spec
2. README with setup instructions
3. JSDoc comments for all methods
4. Architecture overview
5. Security considerations
6. Environment configuration
7. Testing instructions
8. Troubleshooting guide

Generate in Markdown format with proper structure.
```

#### Expected Documentation Quality

```markdown
# Authentication Service

## Overview

This service provides secure user authentication using JSON Web Tokens (JWT) with rate limiting, password security, and comprehensive error handling.

## Architecture
```

┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐
│ AuthService │────│ TokenService │────│ JWT Token │
│ │ │ │ │ │
│ - Login │ │ - Generate │ │ - User ID │
│ - Validation │ │ - Verify │ │ - Email │
│ - Rate Limit │ │ - Refresh │ │ - Expiration │
└─────────────────┘ └─────────────────┘ └─────────────────┘
│ │ │
▼ ▼ ▼
┌─────────────────┐ ┌─────────────────┐ ┌─────────────────┐
│ UserRepository │ │ RateLimitService│ │ Logger │
│ │ │ │ │ │
│ - Find User │ │ - Check IP │ │ - Structured │
│ - Create User │ │ - Record Attempt│ │ - Trace IDs │
│ - Update Login │ │ - Window Config │ │ - Levels │
└─────────────────┘ └─────────────────┘ └─────────────────┘

````

## API Endpoints

### POST /auth/login

Authenticates a user and returns a JWT token.

**Request Body:**
```json
{
  "email": "user@example.com",
  "password": "securePassword123"
}
````

**Response:**

```json
{
  "success": true,
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "user": {
    "id": "user123",
    "email": "user@example.com"
  }
}
```

**Error Responses:**

- `400 Bad Request`: Invalid input format
- `401 Unauthorized`: Invalid credentials
- `429 Too Many Requests`: Rate limit exceeded
- `500 Internal Server Error`: Server error

## Security Considerations

- ✅ Passwords hashed with bcrypt (12 rounds)
- ✅ JWT tokens with 24-hour expiration
- ✅ Rate limiting (5 attempts per 15 minutes)
- ✅ HTTPS enforcement required
- ✅ Input validation on all endpoints
- ⚠️ Consider implementing account lockout
- ⚠️ Add CSRF protection for web forms

## Environment Configuration

```bash
# Required
JWT_SECRET=your-super-secret-jwt-key
DATABASE_URL=postgresql://user:pass@localhost:5432/authdb

# Optional
JWT_EXPIRATION=24h
BCRYPT_ROUNDS=12
RATE_LIMIT_WINDOW=900000
RATE_LIMIT_MAX=5
LOG_LEVEL=info
```

## Setup Instructions

1. Install dependencies:

```bash
npm install express bcryptjs jsonwebtoken joi express-rate-limit
```

2. Configure environment variables
3. Initialize database tables
4. Start the service:

```bash
npm run dev
```

## Testing

Run the test suite:

```bash
npm test
```

Run with coverage:

```bash
npm run test:coverage
```

## Troubleshooting

### Common Issues

**"Invalid credentials" error**

- Verify email exists in database
- Check password hashing consistency
- Ensure bcrypt rounds match configuration

**"Token expired" error**

- Check JWT_EXPIRATION setting
- Verify client token refresh logic
- Check system time synchronization

**Rate limiting issues**

- Verify RATE_LIMIT_MAX setting
- Check IP detection in production
- Consider CDN/proxy IP forwarding

---

**Last Updated**: October 30, 2025  
**Version**: 1.0.0

```

## Test Execution Plan

### Phase 1: Provider Setup (Week 1)
- [ ] Configure API keys for all providers
- [ ] Set up testing environment
- [ ] Create test data and scenarios
- [ ] Implement test harness

### Phase 2: Capability Testing (Week 2-3)
- [ ] Test Issue Analysis scenario
- [ ] Test Code Generation scenario
- [ ] Test Test Generation scenario
- [ ] Test Code Review scenario
- [ ] Test Refactoring scenario
- [ ] Test Documentation scenario

### Phase 3: Evaluation (Week 4)
- [ ] Score each provider on all scenarios
- [ ] Document strengths and weaknesses
- [ ] Calculate cost-effectiveness
- [ ] Generate final recommendations

### Success Criteria
- All 6 scenarios tested with each provider
- Quantitative scoring completed
- Cost analysis finalized
- Recommendations documented

---

**Test Lead**: AI Research Team
**Review Date**: November 15, 2025
**Approval**: Technical Leadership
```
