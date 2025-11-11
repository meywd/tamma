# Task 4: Endpoint Documentation

**Story**: 1.4 - Basic API Infrastructure Documentation  
**Task**: 4 - Endpoint Documentation  
**Priority**: Medium  
**Estimated Time**: 1.5 hours

## API Endpoint Documentation

This section provides detailed documentation for all API endpoints, including request/response examples, error handling, and usage patterns.

## Authentication Endpoints

### POST /auth/register

Register a new user account.

**Request:**

```http
POST /v1/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "securePassword123",
  "name": "John Doe"
}
```

**Response (201):**

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

**Error Responses:**

- `400` - Validation error
- `409` - User already exists

### POST /auth/login

Authenticate user and return tokens.

**Request:**

```http
POST /v1/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "securePassword123"
}
```

**Response (200):**

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

**Error Responses:**

- `400` - Validation error
- `401` - Invalid credentials

### POST /auth/refresh

Refresh access token using refresh token.

**Request:**

```http
POST /v1/auth/refresh
Content-Type: application/json

{
  "refreshToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

**Response (200):**

```json
{
  "data": {
    "accessToken": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "expiresIn": 3600
  }
}
```

**Error Responses:**

- `400` - Invalid refresh token
- `401` - Refresh token expired

### POST /auth/logout

Invalidate current access token.

**Request:**

```http
POST /v1/auth/logout
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Response (200):**

```json
{
  "data": {
    "message": "Logged out successfully"
  }
}
```

## User Endpoints

### GET /users/profile

Get current user profile.

**Request:**

```http
GET /v1/users/profile
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Response (200):**

```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "email": "user@example.com",
    "name": "John Doe",
    "avatar": "https://example.com/avatar.jpg",
    "isActive": true,
    "lastLoginAt": "2025-01-15T10:30:00Z",
    "createdAt": "2025-01-01T00:00:00Z",
    "updatedAt": "2025-01-15T10:30:00Z"
  }
}
```

### PATCH /users/profile

Update current user profile.

**Request:**

```http
PATCH /v1/users/profile
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
Content-Type: application/json

{
  "name": "John Smith",
  "avatar": "https://example.com/new-avatar.jpg"
}
```

**Response (200):**

```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "email": "user@example.com",
    "name": "John Smith",
    "avatar": "https://example.com/new-avatar.jpg",
    "isActive": true,
    "lastLoginAt": "2025-01-15T10:30:00Z",
    "createdAt": "2025-01-01T00:00:00Z",
    "updatedAt": "2025-01-15T11:00:00Z"
  }
}
```

## Organization Endpoints

### GET /organizations

List user organizations.

**Request:**

```http
GET /v1/organizations?page=1&limit=20&sort=createdAt&order=desc
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

**Response (200):**

```json
{
  "data": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440001",
      "name": "Acme Corp",
      "slug": "acme-corp",
      "description": "Technology company specializing in AI solutions",
      "logo": "https://example.com/logo.png",
      "website": "https://acme.com",
      "ownerId": "550e8400-e29b-41d4-a716-446655440000",
      "createdAt": "2025-01-01T00:00:00Z",
      "updatedAt": "2025-01-15T10:30:00Z"
    }
  ],
  "meta": {
    "pagination": {
      "page": 1,
      "limit": 20,
      "total": 1,
      "totalPages": 1,
      "hasNext": false,
      "hasPrev": false
    },
    "timestamp": "2025-01-15T10:30:00Z",
    "requestId": "req_123456789"
  }
}
```

### POST /organizations

Create a new organization.

**Request:**

```http
POST /v1/organizations
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
Content-Type: application/json

{
  "name": "New Company",
  "slug": "new-company",
  "description": "A new technology startup",
  "logo": "https://example.com/logo.png",
  "website": "https://newcompany.com",
  "settings": {
    "allowInvitations": true,
    "defaultRole": "member"
  }
}
```

**Response (201):**

```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440002",
    "name": "New Company",
    "slug": "new-company",
    "description": "A new technology startup",
    "logo": "https://example.com/logo.png",
    "website": "https://newcompany.com",
    "ownerId": "550e8400-e29b-41d4-a716-446655440000",
    "settings": {
      "allowInvitations": true,
      "defaultRole": "member"
    },
    "createdAt": "2025-01-15T10:30:00Z",
    "updatedAt": "2025-01-15T10:30:00Z"
  }
}
```

### GET /organizations/{orgId}

Get organization details.

**Request:**

```http
GET /v1/organizations/550e8400-e29b-41d4-a716-446655440001
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440001",
    "name": "Acme Corp",
    "slug": "acme-corp",
    "description": "Technology company specializing in AI solutions",
    "logo": "https://example.com/logo.png",
    "website": "https://acme.com",
    "ownerId": "550e8400-e29b-41d4-a716-446655440000",
    "settings": {
      "allowInvitations": true,
      "defaultRole": "member"
    },
    "createdAt": "2025-01-01T00:00:00Z",
    "updatedAt": "2025-01-15T10:30:00Z"
  }
}
```

### PATCH /organizations/{orgId}

Update organization details.

**Request:**

```http
PATCH /v1/organizations/550e8400-e29b-41d4-a716-446655440001
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
Content-Type: application/json

{
  "name": "Acme Corporation",
  "description": "Updated description for Acme Corporation"
}
```

**Response (200):**

```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440001",
    "name": "Acme Corporation",
    "slug": "acme-corp",
    "description": "Updated description for Acme Corporation",
    "logo": "https://example.com/logo.png",
    "website": "https://acme.com",
    "ownerId": "550e8400-e29b-41d4-a716-446655440000",
    "settings": {
      "allowInvitations": true,
      "defaultRole": "member"
    },
    "createdAt": "2025-01-01T00:00:00Z",
    "updatedAt": "2025-01-15T11:00:00Z"
  }
}
```

### DELETE /organizations/{orgId}

Delete an organization (owner only).

**Request:**

```http
DELETE /v1/organizations/550e8400-e29b-41d4-a716-446655440001
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (204):**

```
(no content)
```

## Member Endpoints

### GET /organizations/{orgId}/members

List organization members.

**Request:**

```http
GET /v1/organizations/550e8400-e29b-41d4-a716-446655440001/members?page=1&limit=20&status=active
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440003",
      "organizationId": "550e8400-e29b-41d4-a716-446655440001",
      "userId": "550e8400-e29b-41d4-a716-446655440000",
      "email": "user@example.com",
      "name": "John Doe",
      "status": "active",
      "roles": [
        {
          "id": "550e8400-e29b-41d4-a716-446655440004",
          "name": "Admin",
          "description": "Full administrative access",
          "permissions": ["user:read", "user:write", "org:manage"],
          "isSystem": false
        }
      ],
      "joinedAt": "2025-01-01T00:00:00Z",
      "lastLoginAt": "2025-01-15T10:30:00Z"
    }
  ],
  "meta": {
    "pagination": {
      "page": 1,
      "limit": 20,
      "total": 1,
      "totalPages": 1,
      "hasNext": false,
      "hasPrev": false
    },
    "timestamp": "2025-01-15T10:30:00Z",
    "requestId": "req_123456789"
  }
}
```

### POST /organizations/{orgId}/members

Add a member to organization.

**Request:**

```http
POST /v1/organizations/550e8400-e29b-41d4-a716-446655440001/members
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
Content-Type: application/json

{
  "userId": "550e8400-e29b-41d4-a716-446655440005",
  "roleIds": ["550e8400-e29b-41d4-a716-446655440006"]
}
```

**Response (201):**

```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440007",
    "organizationId": "550e8400-e29b-41d4-a716-446655440001",
    "userId": "550e8400-e29b-41d4-a716-446655440005",
    "email": "newmember@example.com",
    "name": "Jane Smith",
    "status": "active",
    "roles": [
      {
        "id": "550e8400-e29b-41d4-a716-446655440006",
        "name": "Member",
        "description": "Standard member access",
        "permissions": ["project:read", "task:read"],
        "isSystem": false
      }
    ],
    "joinedAt": "2025-01-15T10:30:00Z"
  }
}
```

### PATCH /members/{memberId}

Update member roles or status.

**Request:**

```http
PATCH /v1/members/550e8400-e29b-41d4-a716-446655440007
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
Content-Type: application/json

{
  "roleIds": ["550e8400-e29b-41d4-a716-446655440004"],
  "status": "active"
}
```

**Response (200):**

```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440007",
    "organizationId": "550e8400-e29b-41d4-a716-446655440001",
    "userId": "550e8400-e29b-41d4-a716-446655440005",
    "email": "newmember@example.com",
    "name": "Jane Smith",
    "status": "active",
    "roles": [
      {
        "id": "550e8400-e29b-41d4-a716-446655440004",
        "name": "Admin",
        "description": "Full administrative access",
        "permissions": ["user:read", "user:write", "org:manage"],
        "isSystem": false
      }
    ],
    "joinedAt": "2025-01-15T10:30:00Z",
    "lastLoginAt": "2025-01-15T10:30:00Z"
  }
}
```

### DELETE /members/{memberId}

Remove member from organization.

**Request:**

```http
DELETE /v1/members/550e8400-e29b-41d4-a716-446655440007
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (204):**

```
(no content)
```

## Invitation Endpoints

### GET /organizations/{orgId}/invitations

List organization invitations.

**Request:**

```http
GET /v1/organizations/550e8400-e29b-41d4-a716-446655440001/invitations?status=pending
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": [
    {
      "id": "550e8400-e29b-41d4-a716-446655440008",
      "organizationId": "550e8400-e29b-41d4-a716-446655440001",
      "email": "invite@example.com",
      "roleId": "550e8400-e29b-41d4-a716-446655440006",
      "status": "pending",
      "expiresAt": "2025-01-22T10:30:00Z",
      "createdAt": "2025-01-15T10:30:00Z"
    }
  ],
  "meta": {
    "timestamp": "2025-01-15T10:30:00Z",
    "requestId": "req_123456789"
  }
}
```

### POST /organizations/{orgId}/invitations

Create invitation.

**Request:**

```http
POST /v1/organizations/550e8400-e29b-41d4-a716-446655440001/invitations
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
Content-Type: application/json

{
  "email": "invite@example.com",
  "roleId": "550e8400-e29b-41d4-a716-446655440006",
  "message": "Join our team to work on exciting projects!"
}
```

**Response (201):**

```json
{
  "data": {
    "id": "550e8400-e29b-41d4-a716-446655440008",
    "organizationId": "550e8400-e29b-41d4-a716-446655440001",
    "email": "invite@example.com",
    "roleId": "550e8400-e29b-41d4-a716-446655440006",
    "status": "pending",
    "expiresAt": "2025-01-22T10:30:00Z",
    "createdAt": "2025-01-15T10:30:00Z"
  }
}
```

### POST /invitations/{invitationId}/resend

Resend invitation.

**Request:**

```http
POST /v1/invitations/550e8400-e29b-41d4-a716-446655440008/resend
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": {
    "message": "Invitation resent successfully"
  }
}
```

### DELETE /invitations/{invitationId}

Revoke invitation.

**Request:**

```http
DELETE /v1/invitations/550e8400-e29b-41d4-a716-446655440008
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": {
    "message": "Invitation revoked successfully"
  }
}
```

### POST /invitations/accept

Accept invitation.

**Request:**

```http
POST /v1/invitations/accept
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
Content-Type: application/json

{
  "token": "inv_abc123def456"
}
```

**Response (200):**

```json
{
  "data": {
    "message": "Invitation accepted successfully"
  }
}
```

## Resource Quota Endpoints

### GET /organizations/{orgId}/quotas

Get organization resource quotas.

**Request:**

```http
GET /v1/organizations/550e8400-e29b-41d4-a716-446655440001/quotas
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": [
    {
      "resourceTypeId": "550e8400-e29b-41d4-a716-446655440009",
      "resourceType": "api_requests",
      "unit": "requests",
      "quotaLimit": 10000,
      "isUnlimited": false,
      "currentUsage": 1500,
      "usagePercentage": 15.0,
      "remaining": 8500
    },
    {
      "resourceTypeId": "550e8400-e29b-41d4-a716-446655440010",
      "resourceType": "storage",
      "unit": "bytes",
      "quotaLimit": 10737418240,
      "isUnlimited": false,
      "currentUsage": 5368709120,
      "usagePercentage": 50.0,
      "remaining": 5368709120
    }
  ],
  "meta": {
    "timestamp": "2025-01-15T10:30:00Z",
    "requestId": "req_123456789"
  }
}
```

### POST /organizations/{orgId}/quotas/check

Check resource quota.

**Request:**

```http
POST /v1/organizations/550e8400-e29b-41d4-a716-446655440001/quotas/check
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
Content-Type: application/json

{
  "resourceType": "api_requests",
  "amount": 100,
  "metadata": {
    "endpoint": "/organizations",
    "method": "GET"
  }
}
```

**Response (200):**

```json
{
  "data": {
    "allowed": true,
    "quota": {
      "resourceTypeId": "550e8400-e29b-41d4-a716-446655440009",
      "resourceType": "api_requests",
      "unit": "requests",
      "quotaLimit": 10000,
      "isUnlimited": false,
      "currentUsage": 1500,
      "usagePercentage": 15.0,
      "remaining": 8500
    }
  }
}
```

**Response (400) - Quota Exceeded:**

```json
{
  "data": {
    "allowed": false,
    "quota": {
      "resourceTypeId": "550e8400-e29b-41d4-a716-446655440009",
      "resourceType": "api_requests",
      "unit": "requests",
      "quotaLimit": 10000,
      "isUnlimited": false,
      "currentUsage": 9950,
      "usagePercentage": 99.5,
      "remaining": 50
    },
    "reason": "Quota would be exceeded. Requested: 100, Available: 50"
  }
}
```

## Analytics Endpoints

### GET /organizations/{orgId}/analytics/usage-report

Generate usage report.

**Request:**

```http
GET /v1/organizations/550e8400-e29b-41d4-a716-446655440001/analytics/usage-report?startDate=2025-01-01&endDate=2025-01-15
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": {
    "organizationId": "550e8400-e29b-41d4-a716-446655440001",
    "period": {
      "start": "2025-01-01T00:00:00Z",
      "end": "2025-01-15T23:59:59Z"
    },
    "totalUsage": {
      "api_requests": 15000,
      "storage": 5368709120
    },
    "costBreakdown": {
      "api_requests": 1.5,
      "storage": 10.0
    },
    "trends": [
      {
        "resourceType": "api_requests",
        "currentUsage": 15000,
        "previousUsage": 12000,
        "growthRate": 25.0,
        "projectedUsage": 18750,
        "costImpact": 1.5
      }
    ],
    "recommendations": [
      "Consider upgrading to Pro tier for higher limits",
      "API usage increased by 25% this month"
    ]
  },
  "meta": {
    "timestamp": "2025-01-15T10:30:00Z",
    "requestId": "req_123456789"
  }
}
```

### GET /organizations/{orgId}/analytics/top-consumers

Get top resource consumers.

**Request:**

```http
GET /v1/organizations/550e8400-e29b-41d4-a716-446655440001/analytics/top-consumers?resourceType=api_requests&limit=10
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": [
    {
      "userId": "550e8400-e29b-41d4-a716-446655440000",
      "usage": 5000,
      "percentage": 33.3
    },
    {
      "userId": "550e8400-e29b-41d4-a716-446655440005",
      "usage": 3000,
      "percentage": 20.0
    }
  ],
  "meta": {
    "timestamp": "2025-01-15T10:30:00Z",
    "requestId": "req_123456789"
  }
}
```

### GET /organizations/{orgId}/analytics/trends

Get usage trends.

**Request:**

```http
GET /v1/organizations/550e8400-e29b-41d4-a716-446655440001/analytics/trends?resourceType=api_requests&days=30
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
X-Organization-ID: 550e8400-e29b-41d4-a716-446655440001
```

**Response (200):**

```json
{
  "data": [
    {
      "date": "2025-01-15",
      "usage": 500
    },
    {
      "date": "2025-01-14",
      "usage": 450
    },
    {
      "date": "2025-01-13",
      "usage": 480
    }
  ],
  "meta": {
    "timestamp": "2025-01-15T10:30:00Z",
    "requestId": "req_123456789"
  }
}
```

## Error Response Format

All error responses follow a consistent format:

```json
{
  "errors": [
    {
      "code": "ERROR_CODE",
      "message": "Human-readable error message",
      "field": "field_name",
      "details": "Additional error details"
    }
  ],
  "meta": {
    "timestamp": "2025-01-15T10:30:00Z",
    "requestId": "req_123456789"
  }
}
```

## Common Error Codes

### Authentication Errors

- `INVALID_CREDENTIALS` - Invalid email or password
- `TOKEN_EXPIRED` - Access token has expired
- `TOKEN_INVALID` - Invalid or malformed token
- `INSUFFICIENT_PERMISSIONS` - User lacks required permissions

### Validation Errors

- `VALIDATION_ERROR` - Request validation failed
- `REQUIRED_FIELD` - Required field is missing
- `INVALID_FORMAT` - Field format is invalid
- `INVALID_VALUE` - Field value is not allowed

### Resource Errors

- `NOT_FOUND` - Resource does not exist
- `ALREADY_EXISTS` - Resource already exists
- `CONFLICT` - Resource conflict
- `FORBIDDEN` - Access to resource is forbidden

### Rate Limit Errors

- `RATE_LIMIT_EXCEEDED` - API rate limit exceeded
- `QUOTA_EXCEEDED` - Resource quota exceeded

## HTTP Status Codes

- `200` - OK (successful GET, PATCH)
- `201` - Created (successful POST)
- `204` - No Content (successful DELETE)
- `400` - Bad Request (validation error)
- `401` - Unauthorized (authentication required)
- `403` - Forbidden (insufficient permissions)
- `404` - Not Found (resource does not exist)
- `409` - Conflict (resource already exists)
- `422` - Unprocessable Entity (validation error)
- `429` - Too Many Requests (rate limit exceeded)
- `500` - Internal Server Error (server error)
