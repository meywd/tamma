# Story 1.6 Task 2: Implement Authentication Handling

## Task Overview

Implement comprehensive authentication handling for GitLab platform integration, supporting both Personal Access Token (PAT) and OAuth2 authentication methods with secure credential management and token refresh capabilities.

## Acceptance Criteria

### 2.1 Personal Access Token (PAT) Authentication

- [ ] Implement PAT authentication with GitLab API v4
- [ ] Support token validation and scope verification
- [ ] Handle token expiration and refresh prompts
- [ ] Implement secure token storage using OS credential manager
- [ ] Support token revocation and cleanup

### 2.2 OAuth2 Authentication Implementation

- [ ] Implement OAuth2 authorization code flow for GitLab
- [ ] Support OAuth2 token refresh with automatic renewal
- [ ] Handle OAuth2 state parameter for CSRF protection
- [ ] Implement secure storage of refresh tokens
- [ ] Support OAuth2 scope management and verification

### 2.3 Authentication Configuration

- [ ] Implement authentication configuration schema
- [ ] Support environment variable configuration
- [ ] Implement configuration validation and error handling
- [ ] Support multiple authentication methods per instance
- [ ] Implement authentication method priority and fallback

### 2.4 Security Implementation

- [ ] Implement secure credential storage using OS-specific managers
- [ ] Support credential encryption at rest (AES-256)
- [ ] Implement secure token transmission over HTTPS
- [ ] Add credential validation and sanitization
- [ ] Implement audit logging for authentication events

### 2.5 Error Handling and Recovery

- [ ] Implement authentication failure handling with retry logic
- [ ] Support token expiration detection and automatic refresh
- [ ] Handle rate limiting and authentication quota exceeded
- [ ] Implement graceful degradation for authentication issues
- [ ] Add comprehensive error reporting and debugging

### 2.6 Testing and Validation

- [ ] Implement unit tests for all authentication methods
- [ ] Add integration tests with GitLab test instance
- [ ] Implement security testing for credential handling
- [ ] Add performance testing for authentication flows
- [ ] Implement end-to-end authentication workflow tests

## Implementation Details

### 2.1 Authentication Interface Design

```typescript
// Authentication types
interface GitLabAuthConfig {
  method: 'pat' | 'oauth2';
  instanceUrl: string;
  token?: string; // For PAT
  clientId?: string; // For OAuth2
  clientSecret?: string; // For OAuth2
  redirectUri?: string; // For OAuth2
  scopes: string[];
}

interface GitLabAuthToken {
  accessToken: string;
  tokenType: 'Bearer';
  expiresIn?: number;
  refreshToken?: string;
  scope?: string;
  createdAt: Date;
}

interface GitLabAuthResult {
  success: boolean;
  token?: GitLabAuthToken;
  user?: GitLabUser;
  error?: string;
  requiresAction?: 'oauth_redirect' | 'token_refresh' | 'reauth';
}

// Authentication manager
class GitLabAuthManager {
  private config: GitLabAuthConfig;
  private credentialManager: CredentialManager;
  private tokenCache: Map<string, GitLabAuthToken>;

  constructor(config: GitLabAuthConfig);

  // Authentication methods
  async authenticate(): Promise<GitLabAuthResult>;
  async authenticateWithPAT(token: string): Promise<GitLabAuthResult>;
  async authenticateWithOAuth2(): Promise<GitLabAuthResult>;

  // Token management
  async refreshToken(refreshToken: string): Promise<GitLabAuthToken>;
  async validateToken(token: string): Promise<boolean>;
  async revokeToken(token: string): Promise<void>;

  // Credential management
  async storeCredentials(token: GitLabAuthToken): Promise<void>;
  async retrieveCredentials(): Promise<GitLabAuthToken | null>;
  async clearCredentials(): Promise<void>;

  // Utility methods
  getAuthHeaders(): Record<string, string>;
  isTokenExpired(token: GitLabAuthToken): boolean;
  getRemainingTime(token: GitLabAuthToken): number;
}
```

### 2.2 Personal Access Token Implementation

```typescript
class GitLabPATAuth {
  private instanceUrl: string;
  private httpClient: HttpClient;

  constructor(instanceUrl: string, httpClient: HttpClient);

  async authenticate(token: string): Promise<GitLabAuthResult> {
    try {
      // Validate token format
      if (!this.validateTokenFormat(token)) {
        return {
          success: false,
          error: 'Invalid token format',
        };
      }

      // Test token with GitLab API
      const user = await this.validateTokenWithAPI(token);
      if (!user) {
        return {
          success: false,
          error: 'Invalid or expired token',
        };
      }

      // Check token scopes
      const scopes = await this.getTokenScopes(token);
      const missingScopes = this.validateRequiredScopes(scopes);

      if (missingScopes.length > 0) {
        return {
          success: false,
          error: `Token missing required scopes: ${missingScopes.join(', ')}`,
        };
      }

      const authToken: GitLabAuthToken = {
        accessToken: token,
        tokenType: 'Bearer',
        scope: scopes.join(' '),
        createdAt: new Date(),
      };

      return {
        success: true,
        token: authToken,
        user,
      };
    } catch (error) {
      return {
        success: false,
        error: `Authentication failed: ${error.message}`,
      };
    }
  }

  private validateTokenFormat(token: string): boolean {
    // GitLab PAT tokens are glpat- followed by 20 characters
    return /^glpat-[a-zA-Z0-9_-]{20}$/.test(token);
  }

  private async validateTokenWithAPI(token: string): Promise<GitLabUser | null> {
    try {
      const response = await this.httpClient.get(`${this.instanceUrl}/api/v4/user`, {
        headers: {
          Authorization: `Bearer ${token}`,
          'User-Agent': 'Tamma/1.0.0',
        },
      });

      if (response.status === 200) {
        return response.data as GitLabUser;
      }
      return null;
    } catch (error) {
      if (error.status === 401) {
        return null; // Invalid token
      }
      throw error;
    }
  }

  private async getTokenScopes(token: string): Promise<string[]> {
    try {
      const response = await this.httpClient.get(
        `${this.instanceUrl}/api/v4/personal_access_tokens/self`,
        {
          headers: {
            Authorization: `Bearer ${token}`,
          },
        }
      );

      return response.data.scopes || [];
    } catch (error) {
      // If we can't get scopes, assume basic read access
      return ['read_api'];
    }
  }

  private validateRequiredScopes(scopes: string[]): string[] {
    const requiredScopes = ['read_api', 'read_repository', 'write_repository'];
    return requiredScopes.filter((scope) => !scopes.includes(scope));
  }
}
```

### 2.3 OAuth2 Implementation

```typescript
class GitLabOAuth2Auth {
  private config: {
    clientId: string;
    clientSecret: string;
    redirectUri: string;
    instanceUrl: string;
  };
  private httpClient: HttpClient;

  constructor(config: GitLabOAuth2Auth['config'], httpClient: HttpClient);

  async initiateOAuth2Flow(): Promise<{ url: string; state: string }> {
    const state = this.generateSecureState();
    const scopes = ['read_api', 'read_repository', 'write_repository'];

    const params = new URLSearchParams({
      client_id: this.config.clientId,
      redirect_uri: this.config.redirectUri,
      response_type: 'code',
      scope: scopes.join(' '),
      state: state,
    });

    const authUrl = `${this.config.instanceUrl}/oauth/authorize?${params.toString()}`;

    // Store state for verification
    await this.storeOAuthState(state);

    return { url: authUrl, state };
  }

  async handleOAuth2Callback(code: string, state: string): Promise<GitLabAuthResult> {
    try {
      // Verify state parameter
      const storedState = await this.retrieveOAuthState(state);
      if (!storedState) {
        return {
          success: false,
          error: 'Invalid state parameter',
        };
      }

      // Exchange authorization code for tokens
      const tokenResponse = await this.exchangeCodeForTokens(code);

      // Get user information
      const user = await this.getUserInfo(tokenResponse.accessToken);

      // Store tokens securely
      await this.storeOAuthTokens(tokenResponse);

      // Clean up state
      await this.clearOAuthState(state);

      return {
        success: true,
        token: tokenResponse,
        user,
      };
    } catch (error) {
      return {
        success: false,
        error: `OAuth2 callback failed: ${error.message}`,
      };
    }
  }

  async refreshToken(refreshToken: string): Promise<GitLabAuthToken> {
    try {
      const response = await this.httpClient.post(`${this.config.instanceUrl}/oauth/token`, {
        grant_type: 'refresh_token',
        refresh_token: refreshToken,
        client_id: this.config.clientId,
        client_secret: this.config.clientSecret,
      });

      const newToken: GitLabAuthToken = {
        accessToken: response.data.access_token,
        tokenType: 'Bearer',
        expiresIn: response.data.expires_in,
        refreshToken: response.data.refresh_token,
        scope: response.data.scope,
        createdAt: new Date(),
      };

      // Update stored tokens
      await this.storeOAuthTokens(newToken);

      return newToken;
    } catch (error) {
      throw new Error(`Token refresh failed: ${error.message}`);
    }
  }

  private async exchangeCodeForTokens(code: string): Promise<GitLabAuthToken> {
    const response = await this.httpClient.post(`${this.config.instanceUrl}/oauth/token`, {
      grant_type: 'authorization_code',
      code: code,
      redirect_uri: this.config.redirectUri,
      client_id: this.config.clientId,
      client_secret: this.config.clientSecret,
    });

    return {
      accessToken: response.data.access_token,
      tokenType: 'Bearer',
      expiresIn: response.data.expires_in,
      refreshToken: response.data.refresh_token,
      scope: response.data.scope,
      createdAt: new Date(),
    };
  }

  private generateSecureState(): string {
    return crypto.randomBytes(32).toString('hex');
  }

  private async storeOAuthState(state: string): Promise<void> {
    // Store state with expiration (10 minutes)
    const key = `oauth_state_${state}`;
    const value = {
      state,
      createdAt: new Date().toISOString(),
      expiresAt: new Date(Date.now() + 10 * 60 * 1000).toISOString(),
    };

    await this.credentialManager.store(key, value);
  }

  private async retrieveOAuthState(state: string): Promise<boolean> {
    const key = `oauth_state_${state}`;
    const stored = await this.credentialManager.retrieve(key);

    if (!stored) return false;

    // Check expiration
    if (new Date(stored.expiresAt) < new Date()) {
      await this.credentialManager.delete(key);
      return false;
    }

    return true;
  }
}
```

### 2.4 Credential Management Implementation

```typescript
class GitLabCredentialManager {
  private serviceName = 'tamma-gitlab';
  private osCredentialManager: CredentialManager;

  constructor() {
    this.osCredentialManager = this.createOSSpecificCredentialManager();
  }

  async storeToken(instanceUrl: string, token: GitLabAuthToken): Promise<void> {
    const accountName = this.getAccountName(instanceUrl);
    const encryptedData = await this.encryptToken(token);

    await this.osCredentialManager.store(this.serviceName, accountName, encryptedData);
  }

  async retrieveToken(instanceUrl: string): Promise<GitLabAuthToken | null> {
    const accountName = this.getAccountName(instanceUrl);

    try {
      const encryptedData = await this.osCredentialManager.retrieve(this.serviceName, accountName);

      if (!encryptedData) return null;

      return await this.decryptToken(encryptedData);
    } catch (error) {
      // Token might be corrupted or from different version
      await this.deleteToken(instanceUrl);
      return null;
    }
  }

  async deleteToken(instanceUrl: string): Promise<void> {
    const accountName = this.getAccountName(instanceUrl);
    await this.osCredentialManager.delete(this.serviceName, accountName);
  }

  private getAccountName(instanceUrl: string): string {
    // Create a consistent account name from instance URL
    const url = new URL(instanceUrl);
    return url.hostname.replace(/\./g, '_');
  }

  private async encryptToken(token: GitLabAuthToken): Promise<string> {
    const key = await this.getEncryptionKey();
    const iv = crypto.randomBytes(16);

    const cipher = crypto.createCipher('aes-256-cbc', key);
    let encrypted = cipher.update(JSON.stringify(token), 'utf8', 'hex');
    encrypted += cipher.final('hex');

    // Combine IV and encrypted data
    return iv.toString('hex') + ':' + encrypted;
  }

  private async decryptToken(encryptedData: string): Promise<GitLabAuthToken> {
    const key = await this.getEncryptionKey();
    const [ivHex, encrypted] = encryptedData.split(':');

    const iv = Buffer.from(ivHex, 'hex');
    const decipher = crypto.createDecipher('aes-256-cbc', key);

    let decrypted = decipher.update(encrypted, 'hex', 'utf8');
    decrypted += decipher.final('utf8');

    return JSON.parse(decrypted) as GitLabAuthToken;
  }

  private async getEncryptionKey(): Promise<string> {
    // Derive encryption key from machine-specific secret
    const machineId = await this.getMachineId();
    return crypto.createHash('sha256').update(`tamma-${machineId}`).digest('hex');
  }

  private async getMachineId(): Promise<string> {
    // Generate or retrieve machine-specific identifier
    const os = require('os');
    const crypto = require('crypto');

    const hostname = os.hostname();
    const cpus = os.cpus();
    const networkInterfaces = os.networkInterfaces();

    const combined = [
      hostname,
      cpus[0]?.model || '',
      Object.values(networkInterfaces)
        .flat()
        .filter((iface) => iface && !iface.internal)
        .map((iface) => iface?.mac)
        .filter(Boolean)
        .join(','),
    ].join('|');

    return crypto.createHash('sha256').update(combined).digest('hex');
  }

  private createOSSpecificCredentialManager(): CredentialManager {
    const platform = process.platform;

    switch (platform) {
      case 'darwin':
        return new MacOSKeychainManager();
      case 'win32':
        return new WindowsCredentialManager();
      case 'linux':
        return new LinuxSecretServiceManager();
      default:
        // Fallback to file-based storage with encryption
        return new FileCredentialManager();
    }
  }
}
```

### 2.5 Authentication Integration with GitLabPlatform

```typescript
class GitLabPlatform implements IGitPlatform {
  private authManager: GitLabAuthManager;
  private credentialManager: GitLabCredentialManager;
  private currentToken: GitLabAuthToken | null = null;

  constructor(config: GitLabPlatformConfig) {
    this.authManager = new GitLabAuthManager(config.auth);
    this.credentialManager = new GitLabCredentialManager();
  }

  async initialize(): Promise<void> {
    // Try to retrieve stored credentials
    const storedToken = await this.credentialManager.retrieveToken(
      this.authManager.getInstanceUrl()
    );

    if (storedToken) {
      // Validate stored token
      const isValid = await this.authManager.validateToken(storedToken.accessToken);

      if (isValid && !this.authManager.isTokenExpired(storedToken)) {
        this.currentToken = storedToken;
        return;
      }

      // Token is invalid or expired, try refresh if possible
      if (storedToken.refreshToken) {
        try {
          this.currentToken = await this.authManager.refreshToken(storedToken.refreshToken);
          await this.credentialManager.storeToken(
            this.authManager.getInstanceUrl(),
            this.currentToken
          );
          return;
        } catch (error) {
          // Refresh failed, clear stored credentials
          await this.credentialManager.deleteToken(this.authManager.getInstanceUrl());
        }
      }
    }

    // No valid credentials available
    throw new Error('No valid GitLab credentials available. Please authenticate.');
  }

  async authenticate(): Promise<GitLabAuthResult> {
    const result = await this.authManager.authenticate();

    if (result.success && result.token) {
      this.currentToken = result.token;
      await this.credentialManager.storeToken(this.authManager.getInstanceUrl(), result.token);
    }

    return result;
  }

  private async ensureAuthenticated(): Promise<void> {
    if (!this.currentToken) {
      await this.initialize();
      return;
    }

    if (this.authManager.isTokenExpired(this.currentToken)) {
      if (this.currentToken.refreshToken) {
        try {
          this.currentToken = await this.authManager.refreshToken(this.currentToken.refreshToken);
          await this.credentialManager.storeToken(
            this.authManager.getInstanceUrl(),
            this.currentToken
          );
        } catch (error) {
          throw new Error('Token refresh failed. Please re-authenticate.');
        }
      } else {
        throw new Error('Token expired. Please re-authenticate.');
      }
    }
  }

  private getAuthHeaders(): Record<string, string> {
    if (!this.currentToken) {
      throw new Error('Not authenticated');
    }

    return {
      Authorization: `Bearer ${this.currentToken.accessToken}`,
      'User-Agent': 'Tamma/1.0.0',
    };
  }

  // Example method using authentication
  async getCurrentUser(): Promise<GitLabUser> {
    await this.ensureAuthenticated();

    const response = await this.httpClient.get(`${this.instanceUrl}/api/v4/user`, {
      headers: this.getAuthHeaders(),
    });

    return response.data as GitLabUser;
  }
}
```

## Testing Strategy

### 2.1 Unit Tests

```typescript
describe('GitLabAuthManager', () => {
  let authManager: GitLabAuthManager;
  let mockHttpClient: jest.Mocked<HttpClient>;
  let mockCredentialManager: jest.Mocked<CredentialManager>;

  beforeEach(() => {
    mockHttpClient = createMockHttpClient();
    mockCredentialManager = createMockCredentialManager();
    authManager = new GitLabAuthManager(config, mockHttpClient, mockCredentialManager);
  });

  describe('PAT Authentication', () => {
    it('should authenticate with valid PAT token', async () => {
      const validToken = 'glpat-1234567890abcdef1234';
      const mockUser = { id: 1, username: 'testuser' };

      mockHttpClient.get.mockResolvedValue({ status: 200, data: mockUser });
      mockHttpClient.get.mockResolvedValue({ status: 200, data: { scopes: ['read_api'] } });

      const result = await authManager.authenticateWithPAT(validToken);

      expect(result.success).toBe(true);
      expect(result.token?.accessToken).toBe(validToken);
      expect(result.user).toEqual(mockUser);
    });

    it('should reject invalid token format', async () => {
      const invalidToken = 'invalid-token';

      const result = await authManager.authenticateWithPAT(invalidToken);

      expect(result.success).toBe(false);
      expect(result.error).toContain('Invalid token format');
    });

    it('should handle expired token', async () => {
      const expiredToken = 'glpat-1234567890abcdef1234';

      mockHttpClient.get.mockRejectedValue({ status: 401 });

      const result = await authManager.authenticateWithPAT(expiredToken);

      expect(result.success).toBe(false);
      expect(result.error).toContain('Invalid or expired token');
    });
  });

  describe('OAuth2 Authentication', () => {
    it('should generate OAuth2 authorization URL', async () => {
      const result = await authManager.initiateOAuth2Flow();

      expect(result.url).toContain('oauth/authorize');
      expect(result.state).toBeDefined();
      expect(result.state).toHaveLength(64); // 32 bytes * 2 (hex)
    });

    it('should handle OAuth2 callback successfully', async () => {
      const code = 'auth_code';
      const state = 'test_state';
      const mockTokenResponse = {
        access_token: 'access_token',
        refresh_token: 'refresh_token',
        expires_in: 7200,
      };
      const mockUser = { id: 1, username: 'testuser' };

      mockHttpClient.post.mockResolvedValue({ data: mockTokenResponse });
      mockHttpClient.get.mockResolvedValue({ data: mockUser });

      const result = await authManager.handleOAuth2Callback(code, state);

      expect(result.success).toBe(true);
      expect(result.token?.accessToken).toBe('access_token');
      expect(result.user).toEqual(mockUser);
    });
  });

  describe('Token Management', () => {
    it('should detect expired tokens', () => {
      const expiredToken: GitLabAuthToken = {
        accessToken: 'token',
        tokenType: 'Bearer',
        expiresIn: 3600,
        createdAt: new Date(Date.now() - 2 * 60 * 60 * 1000), // 2 hours ago
      };

      expect(authManager.isTokenExpired(expiredToken)).toBe(true);
    });

    it('should refresh OAuth2 tokens', async () => {
      const refreshToken = 'refresh_token';
      const mockNewToken = {
        access_token: 'new_access_token',
        refresh_token: 'new_refresh_token',
        expires_in: 7200,
      };

      mockHttpClient.post.mockResolvedValue({ data: mockNewToken });

      const result = await authManager.refreshToken(refreshToken);

      expect(result.accessToken).toBe('new_access_token');
      expect(result.refreshToken).toBe('new_refresh_token');
    });
  });
});
```

### 2.2 Integration Tests

```typescript
describe('GitLab Authentication Integration', () => {
  let gitlabPlatform: GitLabPlatform;
  const testConfig = {
    instanceUrl: process.env.GITLAB_TEST_URL || 'https://gitlab.com',
    auth: {
      method: 'pat' as const,
      token: process.env.GITLAB_TEST_TOKEN,
    },
  };

  beforeAll(async () => {
    if (!testConfig.auth.token) {
      throw new Error('GITLAB_TEST_TOKEN environment variable required for integration tests');
    }

    gitlabPlatform = new GitLabPlatform(testConfig);
  });

  it('should authenticate with real GitLab instance', async () => {
    const result = await gitlabPlatform.authenticate();

    expect(result.success).toBe(true);
    expect(result.token).toBeDefined();
    expect(result.user).toBeDefined();
    expect(result.user?.username).toBeDefined();
  });

  it('should fetch current user after authentication', async () => {
    await gitlabPlatform.authenticate();
    const user = await gitlabPlatform.getCurrentUser();

    expect(user.id).toBeDefined();
    expect(user.username).toBeDefined();
    expect(user.email).toBeDefined();
  });

  it('should handle API calls with authentication headers', async () => {
    await gitlabPlatform.authenticate();

    // This should work without throwing authentication errors
    const projects = await gitlabPlatform.getProjects();

    expect(Array.isArray(projects)).toBe(true);
  });
});
```

### 2.3 Security Tests

```typescript
describe('GitLab Credential Security', () => {
  let credentialManager: GitLabCredentialManager;

  beforeEach(() => {
    credentialManager = new GitLabCredentialManager();
  });

  it('should encrypt and decrypt tokens correctly', async () => {
    const originalToken: GitLabAuthToken = {
      accessToken: 'glpat-1234567890abcdef1234',
      tokenType: 'Bearer',
      expiresIn: 3600,
      createdAt: new Date(),
    };

    const encrypted = await credentialManager['encryptToken'](originalToken);
    const decrypted = await credentialManager['decryptToken'](encrypted);

    expect(decrypted).toEqual(originalToken);
  });

  it('should not store plaintext tokens', async () => {
    const token: GitLabAuthToken = {
      accessToken: 'sensitive_token',
      tokenType: 'Bearer',
    };

    await credentialManager.storeToken('https://gitlab.com', token);

    // Verify stored data is encrypted (not plaintext)
    const stored = await credentialManager['osCredentialManager'].retrieve(
      'tamma-gitlab',
      'gitlab_com'
    );

    expect(stored).not.toContain('sensitive_token');
    expect(stored).toMatch(/^[a-f0-9]+:/); // Should be hex:encrypted format
  });
});
```

## Completion Checklist

- [ ] Implement GitLabAuthManager class with PAT and OAuth2 support
- [ ] Implement GitLabPATAuth class for token validation
- [ ] Implement GitLabOAuth2Auth class for OAuth2 flow
- [ ] Implement GitLabCredentialManager for secure storage
- [ ] Integrate authentication with GitLabPlatform class
- [ ] Add comprehensive unit tests for all authentication methods
- [ ] Add integration tests with GitLab test instance
- [ ] Add security tests for credential handling
- [ ] Update documentation with authentication setup instructions
- [ ] Verify all acceptance criteria are met
- [ ] Ensure code coverage targets are achieved
- [ ] Validate error handling and recovery mechanisms

## Dependencies

- Task 1: GitLabPlatform class implementation (for integration)
- Node.js 22 LTS crypto module for encryption
- OS-specific credential manager libraries
- GitLab test instance for integration testing
- Test credentials for OAuth2 flow testing

## Estimated Time

**Implementation**: 3-4 days
**Testing**: 2-3 days
**Documentation**: 1 day
**Total**: 6-8 days
