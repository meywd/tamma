# Task 3: Authentication Documentation

**Story**: 1.4 - Basic API Infrastructure Documentation  
**Task**: 3 - Authentication Documentation  
**Priority**: Medium  
**Estimated Time**: 1 hour

## Authentication Overview

The Test Platform API uses JWT (JSON Web Token) based authentication with refresh token rotation for secure session management. This guide covers the complete authentication flow, token management, and best practices for integrating with the API.

## Authentication Flow

### 1. User Registration

```http
POST /v1/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "securePassword123",
  "name": "John Doe"
}
```

**Response:**

```json
{
  "data": {
    "user": {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "email": "user@example.com",
      "name": "John Doe",
      "isActive": true,
      "createdAt": "2025-01-15T10:30:00Z"
    },
    "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "refreshToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "expiresIn": 3600
  }
}
```

### 2. User Login

```http
POST /v1/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "securePassword123"
}
```

**Response:**

```json
{
  "data": {
    "user": {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "email": "user@example.com",
      "name": "John Doe",
      "isActive": true,
      "lastLoginAt": "2025-01-15T10:30:00Z"
    },
    "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "refreshToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "expiresIn": 3600
  }
}
```

### 3. Making Authenticated Requests

```http
GET /v1/organizations
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

### 4. Token Refresh

```http
POST /v1/auth/refresh
Content-Type: application/json

{
  "refreshToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

**Response:**

```json
{
  "data": {
    "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "expiresIn": 3600
  }
}
```

### 5. Logout

```http
POST /v1/auth/logout
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

## Token Structure

### Access Token

```json
{
  "sub": "550e8400-e29b-41d4-a716-446655440000",
  "iss": "testplatform.com",
  "aud": "testplatform-api",
  "exp": 1642248000,
  "iat": 1642244400,
  "type": "access",
  "org": "550e8400-e29b-41d4-a716-446655440001",
  "roles": ["member", "admin"],
  "permissions": ["user:read", "member:manage", "org:read"]
}
```

### Refresh Token

```json
{
  "sub": "550e8400-e29b-41d4-a716-446655440000",
  "iss": "testplatform.com",
  "aud": "testplatform-api",
  "exp": 1644836400,
  "iat": 1642244400,
  "type": "refresh",
  "jti": "refresh_123456789",
  "sessionId": "session_123456789"
}
```

## Token Management Best Practices

### 1. Access Token Usage

- **Short-lived**: Access tokens expire in 1 hour
- **Bearer format**: Include in `Authorization: Bearer <token>` header
- **Storage**: Store in memory or secure HTTP-only cookies
- **Transmission**: Always use HTTPS

### 2. Refresh Token Usage

- **Long-lived**: Refresh tokens expire in 30 days
- **Secure storage**: Store in encrypted storage or secure HTTP-only cookies
- **Rotation**: New refresh token issued on each refresh
- **Single use**: Each refresh token can only be used once

### 3. Token Validation

```javascript
// Client-side token validation
function isTokenExpired(token) {
  try {
    const payload = JSON.parse(atob(token.split('.')[1]));
    return Date.now() >= payload.exp * 1000;
  } catch (error) {
    return true; // Invalid token
  }
}

// Automatic token refresh
async function makeAuthenticatedRequest(url, options = {}) {
  let token = getAccessToken();

  if (isTokenExpired(token)) {
    token = await refreshAccessToken();
  }

  return fetch(url, {
    ...options,
    headers: {
      ...options.headers,
      Authorization: `Bearer ${token}`,
    },
  });
}
```

## Multi-Tenant Authentication

### Organization Context

For multi-tenant operations, include the organization ID in requests:

```http
GET /v1/organizations/{orgId}/members
Authorization: Bearer <access_token>
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

### Permission Checking

The API automatically checks permissions based on:

1. User's roles in the organization
2. Role permissions
3. Resource ownership

```javascript
// Example permission check in API response
{
  "data": [...],
  "meta": {
    "permissions": {
      "canCreate": true,
      "canUpdate": true,
      "canDelete": false
    }
  }
}
```

## SDK Integration Examples

### JavaScript/TypeScript

```typescript
import { TestPlatformClient } from '@testplatform/api-client';

const client = new TestPlatformClient({
  baseURL: 'https://api.testplatform.com/v1',
  auth: {
    username: 'user@example.com',
    password: 'securePassword123',
  },
});

// Automatic token handling
const organizations = await client.organizations.list();
```

### Python

```python
from testplatform import TestPlatformClient

client = TestPlatformClient(
    base_url='https://api.testplatform.com/v1',
    auth=('user@example.com', 'securePassword123')
)

# Automatic token handling
organizations = client.organizations.list()
```

### cURL Examples

```bash
# Login
curl -X POST https://api.testplatform.com/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{
    "email": "user@example.com",
    "password": "securePassword123"
  }'

# Authenticated request
curl -X GET https://api.testplatform.com/v1/organizations \
  -H "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..." \
  -H "X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001"
```

## Error Handling

### Authentication Errors

```json
{
  "errors": [
    {
      "code": "INVALID_CREDENTIALS",
      "message": "Invalid email or password"
    }
  ]
}
```

### Authorization Errors

```json
{
  "errors": [
    {
      "code": "INSUFFICIENT_PERMISSIONS",
      "message": "You don't have permission to perform this action",
      "details": "Required permission: org:manage"
    }
  ]
}
```

### Token Errors

```json
{
  "errors": [
    {
      "code": "TOKEN_EXPIRED",
      "message": "Access token has expired"
    }
  ]
}
```

## Security Considerations

### 1. Token Storage

- **Never store tokens in localStorage** in web browsers
- Use **HTTP-only cookies** or **secure storage** (Keychain, Credential Manager)
- Implement **token encryption** for client-side storage

### 2. Token Transmission

- **Always use HTTPS** for API communication
- **Validate SSL certificates** in production
- **Implement certificate pinning** for mobile apps

### 3. Session Management

- **Implement logout** on all devices
- **Support session revocation** for compromised accounts
- **Monitor anomalous usage** patterns

### 4. Password Security

- **Enforce strong password policies**
- **Implement rate limiting** for authentication attempts
- **Support password reset** functionality

## Rate Limiting

### Authentication Endpoints

- **Login**: 5 attempts per minute per IP
- **Registration**: 3 attempts per hour per IP
- **Token refresh**: 10 attempts per minute per user

### Authenticated Endpoints

- **Standard users**: 1000 requests per hour
- **Premium users**: 10000 requests per hour
- **Enterprise users**: Unlimited

Rate limit headers are included in all responses:

```http
X-RateLimit-Limit: 1000
X-RateLimit-Remaining: 999
X-RateLimit-Reset: 1642248000
```

## Webhook Authentication

### Webhook Signature Verification

```javascript
import crypto from 'crypto';

function verifyWebhookSignature(payload, signature, secret) {
  const expectedSignature = crypto.createHmac('sha256', secret).update(payload).digest('hex');

  return `sha256=${expectedSignature}` === signature;
}

// Usage
const payload = request.body;
const signature = request.headers['x-webhook-signature'];
const secret = 'your-webhook-secret';

if (verifyWebhookSignature(payload, signature, secret)) {
  // Process webhook
} else {
  // Reject webhook
}
```

## API Key Authentication (Future)

For service-to-service authentication, API keys will be supported:

```http
GET /v1/organizations
Authorization: Bearer api_key_live_123456789
X-API-Key: api_key_live_123456789
```

## Troubleshooting

### Common Issues

1. **"Token expired" error**
   - Implement automatic token refresh
   - Check system time synchronization

2. **"Invalid token" error**
   - Verify token format and structure
   - Check for token tampering

3. **"Insufficient permissions" error**
   - Verify user roles in organization
   - Check required permissions for endpoint

4. **"Rate limit exceeded" error**
   - Implement exponential backoff
   - Check rate limit headers

### Debugging Tools

1. **JWT Decoder**: Use online tools to inspect token contents
2. **API Logs**: Check request/response logs for authentication issues
3. **Network Inspector**: Monitor HTTP headers and token transmission

## Support Resources

### Documentation

- [API Reference](./task-4-endpoint-documentation.md)
- [Error Reference](./task-6-reference-documentation.md)
- [Integration Guides](./task-5-integration-guides.md)

### Tools

- [JWT Debugger](https://jwt.io/)
- [API Explorer](https://api.testplatform.com/explorer)
- [Postman Collection](https://testplatform.com/postman)

### Support

- **Developer Forum**: https://community.testplatform.com
- **Support Email**: api-support@testplatform.com
- **Status Page**: https://status.testplatform.com
