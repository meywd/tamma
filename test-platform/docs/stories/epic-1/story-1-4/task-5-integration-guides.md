# Task 5: Integration Guides

**Story**: 1.4 - Basic API Infrastructure Documentation  
**Task**: 5 - Integration Guides  
**Priority**: Medium  
**Estimated Time**: 1 hour

## Integration Guides

This section provides comprehensive guides for integrating with the Test Platform API, including quick start tutorials, SDK usage examples, webhook integration, and best practices.

## Quick Start Guide

### 1. Get Your API Credentials

1. **Sign up** for a Test Platform account at https://testplatform.com/signup
2. **Create an organization** or join an existing one
3. **Generate API tokens** in your organization settings
4. **Note your organization ID** from the organization dashboard

### 2. Make Your First API Call

#### Using cURL

```bash
# Login to get access token
curl -X POST https://api.testplatform.com/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{
    "email": "your-email@example.com",
    "password": "your-password"
  }'

# Use the access token to make an authenticated request
curl -X GET https://api.testplatform.com/v1/organizations \
  -H "Authorization: Bearer YOUR_ACCESS_TOKEN" \
  -H "X-Organization-ID: YOUR_ORG_ID"
```

#### Using JavaScript

```javascript
// Login and get token
const loginResponse = await fetch('https://api.testplatform.com/v1/auth/login', {
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
  },
  body: JSON.stringify({
    email: 'your-email@example.com',
    password: 'your-password',
  }),
});

const { data } = await loginResponse.json();
const { accessToken } = data;

// Make authenticated request
const orgsResponse = await fetch('https://api.testplatform.com/v1/organizations', {
  headers: {
    Authorization: `Bearer ${accessToken}`,
    'X-Organization-ID': 'YOUR_ORG_ID',
  },
});

const organizations = await orgsResponse.json();
console.log(organizations);
```

### 3. Create Your First Organization

```javascript
const createOrgResponse = await fetch('https://api.testplatform.com/v1/organizations', {
  method: 'POST',
  headers: {
    Authorization: `Bearer ${accessToken}`,
    'Content-Type': 'application/json',
  },
  body: JSON.stringify({
    name: 'My Test Organization',
    slug: 'my-test-org',
    description: 'Organization for testing the API',
  }),
});

const organization = await createOrgResponse.json();
console.log('Created organization:', organization.data);
```

## SDK Integration Guides

### JavaScript/TypeScript SDK

#### Installation

```bash
npm install @testplatform/api-client
# or
yarn add @testplatform/api-client
```

#### Basic Usage

```typescript
import { TestPlatformClient } from '@testplatform/api-client';

// Initialize client
const client = new TestPlatformClient({
  baseURL: 'https://api.testplatform.com/v1',
  auth: {
    username: 'your-email@example.com',
    password: 'your-password',
  },
});

// List organizations
const organizations = await client.organizations.list();
console.log('Organizations:', organizations.data);

// Create a new organization
const newOrg = await client.organizations.create({
  name: 'New Organization',
  slug: 'new-org',
  description: 'Created via SDK',
});

// Add a member
const member = await client.members.create(newOrg.data.id, {
  userId: 'user-uuid',
  roleIds: ['role-uuid'],
});
```

#### Advanced Usage with Token Management

```typescript
import { TestPlatformClient, TokenManager } from '@testplatform/api-client';

// Custom token management
const tokenManager = new TokenManager({
  onTokenRefresh: (newToken) => {
    // Store new token securely
    localStorage.setItem('access_token', newToken);
  },
  getStoredToken: () => {
    return localStorage.getItem('access_token');
  },
});

const client = new TestPlatformClient({
  baseURL: 'https://api.testplatform.com/v1',
  tokenManager,
});

// Automatic token refresh
const organizations = await client.organizations.list();
```

#### Error Handling

```typescript
import { TestPlatformApiError, TestPlatformValidationError } from '@testplatform/api-client';

try {
  const organization = await client.organizations.create({
    name: 'Test Org',
    slug: 'invalid-slug-with-spaces',
  });
} catch (error) {
  if (error instanceof TestPlatformValidationError) {
    console.log('Validation errors:', error.errors);
  } else if (error instanceof TestPlatformApiError) {
    console.log(`API Error: ${error.code} - ${error.message}`);
  } else {
    console.log('Unexpected error:', error);
  }
}
```

### Python SDK

#### Installation

```bash
pip install testplatform-python
```

#### Basic Usage

```python
from testplatform import TestPlatformClient

# Initialize client
client = TestPlatformClient(
    base_url='https://api.testplatform.com/v1',
    auth=('your-email@example.com', 'your-password')
)

# List organizations
organizations = client.organizations.list()
print(f"Organizations: {organizations.data}")

# Create a new organization
new_org = client.organizations.create({
    'name': 'New Organization',
    'slug': 'new-org',
    'description': 'Created via Python SDK'
})

# Add a member
member = client.members.create(new_org.data['id'], {
    'user_id': 'user-uuid',
    'role_ids': ['role-uuid']
})
```

#### Advanced Usage

```python
from testplatform import TestPlatformClient
from testplatform.exceptions import TestPlatformError, ValidationError

# Custom session with retry logic
import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry

session = requests.Session()
retry_strategy = Retry(
    total=3,
    backoff_factor=1,
    status_forcelist=[429, 500, 502, 503, 504],
)
adapter = HTTPAdapter(max_retries=retry_strategy)
session.mount("https://", adapter)

client = TestPlatformClient(
    base_url='https://api.testplatform.com/v1',
    auth=('your-email@example.com', 'your-password'),
    session=session
)

# Error handling
try:
    organization = client.organizations.create({
        'name': 'Test Org',
        'slug': 'invalid-slug'
    })
except ValidationError as e:
    print(f"Validation errors: {e.errors}")
except TestPlatformError as e:
    print(f"API Error: {e.code} - {e.message}")
```

### Go SDK

#### Installation

```bash
go get github.com/testplatform/go-client
```

#### Basic Usage

```go
package main

import (
    "context"
    "fmt"
    "log"

    "github.com/testplatform/go-client"
)

func main() {
    // Initialize client
    client := testplatform.NewClient("https://api.testplatform.com/v1")

    // Authenticate
    authResp, err := client.Auth.Login(context.Background(), &testplatform.LoginRequest{
        Email:    "your-email@example.com",
        Password: "your-password",
    })
    if err != nil {
        log.Fatal(err)
    }

    // Set token for authenticated requests
    client.SetAuthToken(authResp.AccessToken)

    // List organizations
    orgs, err := client.Organizations.List(context.Background(), nil)
    if err != nil {
        log.Fatal(err)
    }

    fmt.Printf("Organizations: %+v\n", orgs.Data)

    // Create organization
    newOrg, err := client.Organizations.Create(context.Background(), &testplatform.CreateOrganizationRequest{
        Name:        "New Organization",
        Slug:        "new-org",
        Description:  "Created via Go SDK",
    })
    if err != nil {
        log.Fatal(err)
    }

    fmt.Printf("Created organization: %+v\n", newOrg.Data)
}
```

## Webhook Integration

### Setting Up Webhooks

1. **Configure webhook URL** in your organization settings
2. **Select events** you want to receive
3. **Verify webhook signature** using your webhook secret
4. **Process events** and return 200 status code

### Webhook Event Types

```javascript
// Member events
member.created; // New member added to organization
member.updated; // Member details updated
member.removed; // Member removed from organization

// Invitation events
invitation.created; // New invitation sent
invitation.accepted; // Invitation accepted
invitation.expired; // Invitation expired
invitation.revoked; // Invitation revoked

// Organization events
organization.created; // Organization created
organization.updated; // Organization updated
organization.deleted; // Organization deleted

// Usage events
quota.warning; // Resource quota warning (80% used)
quota.exceeded; // Resource quota exceeded
usage.spike; // Unusual usage spike detected
```

### Webhook Signature Verification

#### Node.js/Express

```javascript
const crypto = require('crypto');
const express = require('express');

const app = express();
const WEBHOOK_SECRET = 'your-webhook-secret';

// Raw body parser for signature verification
app.use(
  '/webhooks',
  express.raw({
    type: 'application/json',
    verify: (req, res, buf) => {
      req.rawBody = buf;
    },
  })
);

app.post('/webhooks', (req, res) => {
  const signature = req.headers['x-webhook-signature'];
  const payload = req.rawBody.toString();

  if (!verifyWebhookSignature(payload, signature, WEBHOOK_SECRET)) {
    return res.status(401).send('Invalid signature');
  }

  try {
    const event = JSON.parse(payload);
    console.log('Received webhook event:', event);

    // Process event based on type
    switch (event.event) {
      case 'member.created':
        handleMemberCreated(event.data);
        break;
      case 'invitation.accepted':
        handleInvitationAccepted(event.data);
        break;
      // ... other event types
    }

    res.status(200).send('OK');
  } catch (error) {
    console.error('Error processing webhook:', error);
    res.status(400).send('Invalid payload');
  }
});

function verifyWebhookSignature(payload, signature, secret) {
  const expectedSignature = crypto.createHmac('sha256', secret).update(payload).digest('hex');

  return `sha256=${expectedSignature}` === signature;
}

function handleMemberCreated(memberData) {
  // Handle new member logic
  console.log('New member added:', memberData);
}

function handleInvitationAccepted(invitationData) {
  // Handle invitation acceptance logic
  console.log('Invitation accepted:', invitationData);
}

app.listen(3000, () => {
  console.log('Webhook server listening on port 3000');
});
```

#### Python/Flask

```python
from flask import Flask, request, jsonify
import hmac
import hashlib
import json

app = Flask(__name__)
WEBHOOK_SECRET = 'your-webhook-secret'

def verify_webhook_signature(payload, signature, secret):
    expected_signature = hmac.new(
        secret.encode('utf-8'),
        payload.encode('utf-8'),
        hashlib.sha256
    ).hexdigest()
    return f"sha256={expected_signature}" == signature

@app.route('/webhooks', methods=['POST'])
def handle_webhook():
    signature = request.headers.get('X-Webhook-Signature')
    payload = request.get_data(as_text=True)

    if not verify_webhook_signature(payload, signature, WEBHOOK_SECRET):
        return jsonify({'error': 'Invalid signature'}), 401

    try:
        event = json.loads(payload)
        print(f"Received webhook event: {event}")

        # Process event based on type
        if event['event'] == 'member.created':
            handle_member_created(event['data'])
        elif event['event'] == 'invitation.accepted':
            handle_invitation_accepted(event['data'])
        # ... other event types

        return jsonify({'status': 'OK'}), 200
    except Exception as error:
        print(f"Error processing webhook: {error}")
        return jsonify({'error': 'Invalid payload'}), 400

def handle_member_created(member_data):
    # Handle new member logic
    print(f"New member added: {member_data}")

def handle_invitation_accepted(invitation_data):
    # Handle invitation acceptance logic
    print(f"Invitation accepted: {invitation_data}")

if __name__ == '__main__':
    app.run(port=3000)
```

### Webhook Event Payload Examples

#### Member Created

```json
{
  "event": "member.created",
  "data": {
    "member": {
      "id": "550e8400-e29b-41d4-a716-446655440000",
      "organizationId": "550e8400-e29b-41d4-a716-446655440001",
      "userId": "550e8400-e29b-41d4-a716-446655440002",
      "email": "newmember@example.com",
      "name": "John Doe",
      "status": "active",
      "roles": [
        {
          "id": "550e8400-e29b-41d4-a716-446655440003",
          "name": "Member",
          "permissions": ["project:read"]
        }
      ],
      "joinedAt": "2025-01-15T10:30:00Z"
    },
    "invitedBy": {
      "id": "550e8400-e29b-41d4-a716-446655440004",
      "name": "Jane Smith"
    }
  },
  "timestamp": "2025-01-15T10:30:00Z",
  "signature": "sha256=5d41402abc4b2a76b9719d911017c592"
}
```

#### Quota Warning

```json
{
  "event": "quota.warning",
  "data": {
    "organizationId": "550e8400-e29b-41d4-a716-446655440001",
    "resourceType": "api_requests",
    "currentUsage": 8000,
    "quotaLimit": 10000,
    "usagePercentage": 80,
    "thresholdPercentage": 80
  },
  "timestamp": "2025-01-15T10:30:00Z",
  "signature": "sha256=5d41402abc4b2a76b9719d911017c592"
}
```

## Best Practices

### 1. Authentication and Security

#### Token Management

```javascript
// Store tokens securely
class TokenStore {
  static setTokens(accessToken, refreshToken) {
    // Use secure storage
    if (typeof window !== 'undefined') {
      // Browser: Use secure, http-only cookies or secure storage
      document.cookie = `access_token=${accessToken}; Secure; HttpOnly; SameSite=Strict`;
      // For refresh token, consider server-side storage
    } else {
      // Node.js: Use environment variables or secure key management
      process.env.ACCESS_TOKEN = accessToken;
    }
  }

  static getAccessToken() {
    if (typeof window !== 'undefined') {
      return document.cookie
        .split('; ')
        .find((row) => row.startsWith('access_token='))
        ?.split('=')[1];
    }
    return process.env.ACCESS_TOKEN;
  }
}

// Automatic token refresh
class ApiClient {
  constructor() {
    this.baseURL = 'https://api.testplatform.com/v1';
  }

  async makeRequest(endpoint, options = {}) {
    let token = TokenStore.getAccessToken();

    if (this.isTokenExpired(token)) {
      token = await this.refreshToken();
    }

    const response = await fetch(`${this.baseURL}${endpoint}`, {
      ...options,
      headers: {
        ...options.headers,
        Authorization: `Bearer ${token}`,
        'Content-Type': 'application/json',
      },
    });

    if (response.status === 401) {
      // Token might be invalid, try refresh once
      token = await this.refreshToken();
      return fetch(`${this.baseURL}${endpoint}`, {
        ...options,
        headers: {
          ...options.headers,
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
        },
      });
    }

    return response;
  }

  async refreshToken() {
    const refreshToken = TokenStore.getRefreshToken();
    const response = await fetch(`${this.baseURL}/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    });

    if (response.ok) {
      const { data } = await response.json();
      TokenStore.setTokens(data.accessToken, data.refreshToken);
      return data.accessToken;
    }

    throw new Error('Token refresh failed');
  }
}
```

#### Rate Limiting

```javascript
class RateLimitedClient {
  constructor() {
    this.requestQueue = [];
    this.processing = false;
    this.rateLimits = {
      limit: 1000,
      remaining: 1000,
      resetTime: null,
    };
  }

  async makeRequest(endpoint, options = {}) {
    return new Promise((resolve, reject) => {
      this.requestQueue.push({ endpoint, options, resolve, reject });
      this.processQueue();
    });
  }

  async processQueue() {
    if (this.processing || this.requestQueue.length === 0) {
      return;
    }

    this.processing = true;

    while (this.requestQueue.length > 0) {
      if (this.rateLimits.remaining <= 0) {
        const waitTime = this.rateLimits.resetTime - Date.now();
        if (waitTime > 0) {
          await this.sleep(waitTime);
        }
      }

      const request = this.requestQueue.shift();
      try {
        const response = await this.executeRequest(request);
        this.updateRateLimits(response);
        request.resolve(response);
      } catch (error) {
        request.reject(error);
      }
    }

    this.processing = false;
  }

  updateRateLimits(response) {
    const limit = response.headers.get('X-RateLimit-Limit');
    const remaining = response.headers.get('X-RateLimit-Remaining');
    const resetTime = response.headers.get('X-RateLimit-Reset');

    if (limit) this.rateLimits.limit = parseInt(limit);
    if (remaining) this.rateLimits.remaining = parseInt(remaining);
    if (resetTime) this.rateLimits.resetTime = parseInt(resetTime) * 1000;
  }

  sleep(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }
}
```

### 2. Error Handling

#### Comprehensive Error Handling

```javascript
class ApiError extends Error {
  constructor(response, data) {
    super(data.message || 'API request failed');
    this.name = 'ApiError';
    this.status = response.status;
    this.code = data.code;
    this.errors = data.errors;
    this.requestId = data.meta?.requestId;
  }
}

class ApiClient {
  async handleResponse(response) {
    const data = await response.json();

    if (!response.ok) {
      throw new ApiError(response, data);
    }

    return data;
  }

  async makeRequest(endpoint, options = {}) {
    try {
      const response = await fetch(`${this.baseURL}${endpoint}`, options);
      return await this.handleResponse(response);
    } catch (error) {
      if (error instanceof ApiError) {
        // Handle API-specific errors
        switch (error.code) {
          case 'TOKEN_EXPIRED':
            return await this.handleTokenRefresh(endpoint, options);
          case 'RATE_LIMIT_EXCEEDED':
            return await this.handleRateLimit(endpoint, options);
          case 'VALIDATION_ERROR':
            console.error('Validation failed:', error.errors);
            throw error;
          default:
            throw error;
        }
      } else {
        // Handle network errors, etc.
        console.error('Network error:', error);
        throw error;
      }
    }
  }

  async handleTokenRefresh(endpoint, options) {
    await this.refreshToken();
    // Retry request with new token
    options.headers = {
      ...options.headers,
      Authorization: `Bearer ${this.getAccessToken()}`,
    };
    return this.makeRequest(endpoint, options);
  }

  async handleRateLimit(endpoint, options) {
    const retryAfter = this.getRetryAfterTime();
    await this.sleep(retryAfter);
    return this.makeRequest(endpoint, options);
  }
}
```

### 3. Performance Optimization

#### Request Batching

```javascript
class BatchClient {
  constructor() {
    this.batchQueue = [];
    this.batchTimeout = null;
    this.batchSize = 10;
    this.batchDelay = 100; // ms
  }

  addToBatch(request) {
    return new Promise((resolve, reject) => {
      this.batchQueue.push({ ...request, resolve, reject });

      if (this.batchQueue.length >= this.batchSize) {
        this.processBatch();
      } else if (!this.batchTimeout) {
        this.batchTimeout = setTimeout(() => this.processBatch(), this.batchDelay);
      }
    });
  }

  async processBatch() {
    if (this.batchTimeout) {
      clearTimeout(this.batchTimeout);
      this.batchTimeout = null;
    }

    const batch = this.batchQueue.splice(0, this.batchSize);

    try {
      const response = await fetch(`${this.baseURL}/batch`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${this.getAccessToken()}`,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          requests: batch.map((req) => ({
            id: req.id,
            method: req.method,
            path: req.endpoint,
            body: req.body,
          })),
        }),
      });

      const result = await response.json();

      // Resolve individual promises
      batch.forEach((request) => {
        const batchResult = result.data.results.find((r) => r.id === request.id);
        if (batchResult.success) {
          request.resolve(batchResult.data);
        } else {
          request.reject(new Error(batchResult.error));
        }
      });
    } catch (error) {
      // Reject all requests in batch
      batch.forEach((request) => request.reject(error));
    }
  }
}
```

#### Caching Strategy

```javascript
class CachedClient {
  constructor() {
    this.cache = new Map();
    this.cacheTimeout = 5 * 60 * 1000; // 5 minutes
  }

  async get(endpoint, options = {}) {
    const cacheKey = this.getCacheKey(endpoint, options);
    const cached = this.cache.get(cacheKey);

    if (cached && Date.now() - cached.timestamp < this.cacheTimeout) {
      return cached.data;
    }

    const response = await fetch(`${this.baseURL}${endpoint}`, options);
    const data = await response.json();

    // Cache successful responses
    if (response.ok) {
      this.cache.set(cacheKey, {
        data,
        timestamp: Date.now(),
      });
    }

    return data;
  }

  invalidateCache(pattern) {
    for (const key of this.cache.keys()) {
      if (key.includes(pattern)) {
        this.cache.delete(key);
      }
    }
  }

  getCacheKey(endpoint, options) {
    return `${endpoint}:${JSON.stringify(options)}`;
  }
}
```

## Testing Your Integration

### Unit Testing with Mocks

```javascript
// Using Jest for testing
import { TestPlatformClient } from '@testplatform/api-client';

// Mock the fetch function
global.fetch = jest.fn();

describe('TestPlatformClient', () => {
  let client;

  beforeEach(() => {
    client = new TestPlatformClient({
      baseURL: 'https://api.testplatform.com/v1',
      auth: { username: 'test@example.com', password: 'password' },
    });

    fetch.mockClear();
  });

  test('should list organizations', async () => {
    const mockResponse = {
      data: [
        { id: 'org-1', name: 'Test Org' },
        { id: 'org-2', name: 'Another Org' },
      ],
    };

    fetch.mockResolvedValueOnce({
      ok: true,
      json: async () => mockResponse,
    });

    const result = await client.organizations.list();

    expect(fetch).toHaveBeenCalledWith(
      'https://api.testplatform.com/v1/organizations',
      expect.objectContaining({
        headers: expect.objectContaining({
          Authorization: expect.stringMatching(/^Bearer /),
        }),
      })
    );

    expect(result.data).toEqual(mockResponse.data);
  });
});
```

### Integration Testing

```javascript
// Integration tests against real API
describe('API Integration Tests', () => {
  let client;
  let testOrgId;

  beforeAll(async () => {
    client = new TestPlatformClient({
      baseURL: process.env.API_BASE_URL,
      auth: {
        username: process.env.TEST_USER_EMAIL,
        password: process.env.TEST_USER_PASSWORD,
      },
    });
  });

  test('should create and delete organization', async () => {
    // Create organization
    const createResponse = await client.organizations.create({
      name: 'Test Integration Org',
      slug: `test-integration-${Date.now()}`,
      description: 'Organization for integration testing',
    });

    expect(createResponse.data).toHaveProperty('id');
    testOrgId = createResponse.data.id;

    // Get organization details
    const getResponse = await client.organizations.get(testOrgId);
    expect(getResponse.data.name).toBe('Test Integration Org');

    // Delete organization
    await client.organizations.delete(testOrgId);

    // Verify deletion
    await expect(client.organizations.get(testOrgId)).rejects.toThrow('404');
  });
});
```

## Troubleshooting

### Common Issues and Solutions

1. **CORS Errors**
   - Ensure your domain is whitelisted in organization settings
   - Check that you're using HTTPS in production

2. **Authentication Failures**
   - Verify token format and expiration
   - Check organization ID header for multi-tenant requests

3. **Rate Limiting**
   - Implement exponential backoff
   - Use batch operations when available

4. **Webhook Delivery Issues**
   - Verify webhook URL is accessible
   - Check signature verification logic
   - Ensure your endpoint returns 200 status quickly

### Debug Tools

1. **Request Logging**

```javascript
// Add request interceptor for debugging
client.interceptors.request.use((request) => {
  console.log('API Request:', {
    method: request.method,
    url: request.url,
    headers: request.headers,
    body: request.body,
  });
  return request;
});
```

2. **Response Logging**

```javascript
// Add response interceptor for debugging
client.interceptors.response.use((response) => {
  console.log('API Response:', {
    status: response.status,
    headers: response.headers,
    data: response.data,
  });
  return response;
});
```

3. **Network Inspector**
   - Use browser DevTools Network tab
   - Use tools like Postman or Insomnia
   - Enable debug logging in SDKs

## Support Resources

### Documentation

- [API Reference](./task-4-endpoint-documentation.md)
- [Authentication Guide](./task-3-authentication-documentation.md)
- [Error Reference](./task-6-reference-documentation.md)

### Tools

- [Postman Collection](https://testplatform.com/postman)
- [API Explorer](https://api.testplatform.com/explorer)
- [SDK Documentation](https://docs.testplatform.com/sdk)

### Community

- [Developer Forum](https://community.testplatform.com)
- [GitHub Discussions](https://github.com/testplatform/api/discussions)
- [Stack Overflow](https://stackoverflow.com/questions/tagged/testplatform-api)

### Support

- **Email**: api-support@testplatform.com
- **Status Page**: https://status.testplatform.com
- **Bug Reports**: https://github.com/testplatform/api/issues
