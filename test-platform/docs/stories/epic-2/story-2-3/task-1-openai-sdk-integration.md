# Task 1: OpenAI SDK Integration (AC: 1)

## Overview

Implement OpenAI SDK integration with proper authentication, organization support, and base URL configuration for the Tamma platform's AI provider system.

## Objectives

- Install and configure OpenAI SDK as a dependency
- Implement secure API key authentication with validation
- Add support for organization and custom base URL configuration
- Create provider initialization and setup

## Implementation Steps

### Subtask 1.1: Install and configure OpenAI SDK

**Objective**: Set up OpenAI SDK as a dependency and create basic integration structure.

**Implementation Steps**:

1. **Install Dependencies**:

```bash
# Navigate to providers package
cd packages/providers

# Install OpenAI SDK
pnpm add openai
pnpm add -D @types/openai
```

2. **Create Provider Configuration Schema**:

```typescript
// packages/providers/src/configs/openai-config.schema.ts
import { z } from 'zod';

export const OpenAIConfigSchema = z.object({
  apiKey: z.string().min(1, 'OpenAI API key is required'),
  organization: z.string().optional(),
  baseURL: z.string().url().optional(),
  maxRetries: z.number().min(0).max(10).default(3),
  timeout: z.number().min(1000).max(300000).default(60000),
  defaultModel: z.string().default('gpt-4'),
  models: z.array(z.string()).default(['gpt-4', 'gpt-4-turbo-preview', 'gpt-3.5-turbo']),
  enabled: z.boolean().default(true),
});

export type OpenAIConfig = z.infer<typeof OpenAIConfigSchema>;

export const defaultOpenAIConfig: OpenAIConfig = {
  apiKey: '',
  maxRetries: 3,
  timeout: 60000,
  defaultModel: 'gpt-4',
  models: ['gpt-4', 'gpt-4-turbo-preview', 'gpt-3.5-turbo'],
  enabled: true,
};
```

3. **Create Base Provider Structure**:

```typescript
// packages/providers/src/implementations/openai-provider.ts
import { OpenAI } from 'openai';
import { OpenAIConfig, OpenAIConfigSchema } from '../configs/openai-config.schema';
import { IAIProvider } from '../interfaces/iai-provider';
import { TammaError } from '@shared/errors';

export class OpenAIProvider implements IAIProvider {
  private client: OpenAI;
  private config: OpenAIConfig;
  private initialized: boolean = false;

  constructor(config: OpenAIConfig) {
    this.validateConfig(config);
    this.config = { ...defaultOpenAIConfig, ...config };
    this.client = new OpenAI({
      apiKey: this.config.apiKey,
      organization: this.config.organization,
      baseURL: this.config.baseURL,
      maxRetries: this.config.maxRetries,
      timeout: this.config.timeout,
    });
  }

  private validateConfig(config: OpenAIConfig): void {
    try {
      OpenAIConfigSchema.parse(config);
    } catch (error) {
      throw new TammaError(
        'INVALID_OPENAI_CONFIG',
        'Invalid OpenAI provider configuration',
        {
          validationError: error instanceof Error ? error.message : 'Unknown error',
          config: this.sanitizeConfig(config),
        },
        false,
        'high'
      );
    }
  }

  private sanitizeConfig(config: Partial<OpenAIConfig>): Partial<OpenAIConfig> {
    const sanitized = { ...config };
    if (sanitized.apiKey) {
      sanitized.apiKey = this.redactApiKey(sanitized.apiKey);
    }
    return sanitized;
  }

  private redactApiKey(apiKey: string): string {
    if (!apiKey || apiKey.length < 10) return '[INVALID]';
    return `${apiKey.substring(0, 7)}...${apiKey.substring(apiKey.length - 4)}`;
  }

  getClient(): OpenAI {
    return this.client;
  }

  getConfig(): OpenAIConfig {
    return { ...this.config };
  }

  async initialize(): Promise<void> {
    if (this.initialized) {
      return;
    }

    try {
      await this.validateAuthentication();
      this.initialized = true;
    } catch (error) {
      throw new TammaError(
        'OPENAI_INITIALIZATION_FAILED',
        'Failed to initialize OpenAI provider',
        {
          originalError: error instanceof Error ? error.message : 'Unknown error',
          config: this.sanitizeConfig(this.config),
        },
        false,
        'critical'
      );
    }
  }

  private async validateAuthentication(): Promise<void> {
    try {
      // Test authentication with a minimal API call
      await this.client.models.list();
    } catch (error) {
      if (error instanceof Error) {
        throw new TammaError(
          'OPENAI_AUTH_FAILED',
          `OpenAI authentication failed: ${error.message}`,
          {
            originalError: error.message,
            config: this.sanitizeConfig(this.config),
          },
          false,
          'critical'
        );
      }
      throw error;
    }
  }

  isInitialized(): boolean {
    return this.initialized;
  }

  async dispose(): Promise<void> {
    // OpenAI SDK doesn't require explicit cleanup
    this.initialized = false;
  }
}
```

**Files to Create**:

- `packages/providers/src/configs/openai-config.schema.ts`
- `packages/providers/src/implementations/openai-provider.ts`

**Dependencies**:

- `openai` package
- `zod` for schema validation
- `@shared/errors` for error handling

---

### Subtask 1.2: Implement authentication with API key

**Objective**: Implement secure API key authentication with validation and error handling.

**Implementation Steps**:

1. **Add Authentication Validation**:

```typescript
// packages/providers/src/utils/security.ts
import { createHash } from 'crypto';
import { TammaError } from '@shared/errors';

export class SecurityUtils {
  static hashApiKey(apiKey: string): string {
    return createHash('sha256').update(apiKey).digest('hex').substring(0, 8);
  }

  static validateApiKeyFormat(apiKey: string): boolean {
    // OpenAI API keys start with 'sk-' and are typically 51 characters
    return /^sk-[A-Za-z0-9]{48}$/.test(apiKey);
  }

  static redactApiKey(apiKey: string): string {
    if (!apiKey || apiKey.length < 10) return '[INVALID]';
    return `${apiKey.substring(0, 7)}...${apiKey.substring(apiKey.length - 4)}`;
  }

  static validateApiKeySecurity(apiKey: string): void {
    if (!apiKey) {
      throw new TammaError('MISSING_API_KEY', 'OpenAI API key is required', {}, false, 'critical');
    }

    if (!this.validateApiKeyFormat(apiKey)) {
      throw new TammaError(
        'INVALID_API_KEY_FORMAT',
        'Invalid OpenAI API key format. Expected format: sk-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx',
        {
          providedKey: this.redactApiKey(apiKey),
          expectedFormat: 'sk-[48 characters]',
        },
        false,
        'critical'
      );
    }

    // Check for common test/placeholder keys
    const placeholderPatterns = [/^sk-test-/, /^sk-example-/, /^sk-demo-/, /^sk-your-/];

    for (const pattern of placeholderPatterns) {
      if (pattern.test(apiKey)) {
        throw new TammaError(
          'PLACEHOLDER_API_KEY',
          'Placeholder or test API key detected. Please use a valid OpenAI API key',
          { providedKey: this.redactApiKey(apiKey) },
          false,
          'high'
        );
      }
    }
  }

  static createSecureContext(apiKey: string): {
    apiKey: string;
    keyHash: string;
    isValid: boolean;
  } {
    this.validateApiKeySecurity(apiKey);

    return {
      apiKey,
      keyHash: this.hashApiKey(apiKey),
      isValid: true,
    };
  }
}
```

2. **Enhance Provider with Authentication**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
import { SecurityUtils } from '../utils/security';

export class OpenAIProvider implements IAIProvider {
  // ... previous code ...

  constructor(config: OpenAIConfig) {
    // Create secure context for API key
    const secureContext = SecurityUtils.createSecureContext(config.apiKey);

    this.validateConfig(config);
    this.config = {
      ...defaultOpenAIConfig,
      ...config,
      apiKey: secureContext.apiKey, // Store validated key
    };

    this.client = new OpenAI({
      apiKey: this.config.apiKey,
      organization: this.config.organization,
      baseURL: this.config.baseURL,
      maxRetries: this.config.maxRetries,
      timeout: this.config.timeout,
    });
  }

  private validateConfig(config: OpenAIConfig): void {
    try {
      OpenAIConfigSchema.parse(config);
    } catch (error) {
      throw new TammaError(
        'INVALID_OPENAI_CONFIG',
        'Invalid OpenAI provider configuration',
        {
          validationError: error instanceof Error ? error.message : 'Unknown error',
          config: this.sanitizeConfig(config),
        },
        false,
        'high'
      );
    }
  }

  async testAuthentication(): Promise<{
    success: boolean;
    error?: string;
    details?: any;
  }> {
    try {
      await this.client.models.list();
      return { success: true };
    } catch (error) {
      return {
        success: false,
        error: error instanceof Error ? error.message : 'Unknown authentication error',
        details: {
          type: error?.error?.type,
          code: error?.error?.code,
          config: this.sanitizeConfig(this.config),
        },
      };
    }
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/security.ts`

---

### Subtask 1.3: Add organization and base URL support

**Objective**: Support OpenAI organization and custom base URL configuration.

**Implementation Steps**:

1. **Enhance Configuration Handling**:

```typescript
// packages/providers/src/implementations/openai-provider.ts (enhanced)
export class OpenAIProvider implements IAIProvider {
  // ... previous code ...

  constructor(config: OpenAIConfig) {
    this.validateConfig(config);
    this.config = { ...defaultOpenAIConfig, ...config };

    const clientConfig: any = {
      apiKey: this.config.apiKey,
      maxRetries: this.config.maxRetries,
      timeout: this.config.timeout,
    };

    // Add organization if provided
    if (this.config.organization) {
      this.validateOrganization(this.config.organization);
      clientConfig.organization = this.config.organization;
    }

    // Add custom base URL if provided
    if (this.config.baseURL) {
      this.validateBaseURL(this.config.baseURL);
      clientConfig.baseURL = this.config.baseURL;
    }

    this.client = new OpenAI(clientConfig);
  }

  private validateOrganization(organization: string): void {
    if (!organization || typeof organization !== 'string') {
      throw new TammaError(
        'INVALID_ORGANIZATION',
        'Organization must be a non-empty string',
        { organization },
        false,
        'medium'
      );
    }

    // OpenAI organization IDs are typically strings like 'org-xxxxxxxxxxxxxxxxxxxxxxxx'
    if (!/^org-[A-Za-z0-9]{24}$/.test(organization)) {
      throw new TammaError(
        'INVALID_ORGANIZATION_FORMAT',
        'Invalid organization format. Expected format: org-xxxxxxxxxxxxxxxxxxxxxxxx',
        {
          providedOrganization: organization,
          expectedFormat: 'org-[24 characters]',
        },
        false,
        'medium'
      );
    }
  }

  private validateBaseURL(baseURL: string): void {
    try {
      const url = new URL(baseURL);

      // Must be HTTPS
      if (url.protocol !== 'https:') {
        throw new TammaError(
          'INVALID_BASE_URL_PROTOCOL',
          'Base URL must use HTTPS protocol',
          { baseURL, protocol: url.protocol },
          false,
          'high'
        );
      }

      // Check for common OpenAI API endpoints
      const allowedHosts = [
        'api.openai.com',
        'openai.ai',
        'api.openai.com',
        'oai.azure.com', // Azure OpenAI
        'localhost', // For development
        '127.0.0.1', // For development
      ];

      const isAllowedHost = allowedHosts.some(
        (host) => url.hostname === host || url.hostname.endsWith(`.${host}`)
      );

      if (
        !isAllowedHost &&
        !url.hostname.includes('localhost') &&
        !url.hostname.includes('127.0.0.1')
      ) {
        console.warn(`Unusual base URL hostname: ${url.hostname}. Please verify this is correct.`);
      }
    } catch (error) {
      throw new TammaError(
        'INVALID_BASE_URL',
        'Invalid base URL provided',
        { baseURL, error: error instanceof Error ? error.message : 'Unknown error' },
        false,
        'high'
      );
    }
  }

  getOrganization(): string | undefined {
    return this.config.organization;
  }

  getBaseURL(): string | undefined {
    return this.config.baseURL;
  }

  async testOrganizationAccess(): Promise<{
    success: boolean;
    organization?: string;
    error?: string;
  }> {
    if (!this.config.organization) {
      return { success: true, organization: 'default' };
    }

    try {
      const response = await this.client.models.list();
      const orgHeader = response.headers?.['openai-organization'];

      return {
        success: true,
        organization: orgHeader || this.config.organization,
      };
    } catch (error) {
      return {
        success: false,
        organization: this.config.organization,
        error: error instanceof Error ? error.message : 'Organization access test failed',
      };
    }
  }
}
```

2. **Add Configuration Validation Utilities**:

```typescript
// packages/providers/src/utils/config-validator.ts
import { OpenAIConfig } from '../configs/openai-config.schema';
import { TammaError } from '@shared/errors';

export class ConfigValidator {
  static validateOpenAIConfig(config: OpenAIConfig): {
    isValid: boolean;
    errors: string[];
    warnings: string[];
  } {
    const errors: string[] = [];
    const warnings: string[] = [];

    // API key validation
    if (!config.apiKey) {
      errors.push('API key is required');
    } else if (!/^sk-[A-Za-z0-9]{48}$/.test(config.apiKey)) {
      errors.push('Invalid API key format');
    }

    // Organization validation
    if (config.organization && !/^org-[A-Za-z0-9]{24}$/.test(config.organization)) {
      errors.push('Invalid organization format');
    }

    // Base URL validation
    if (config.baseURL) {
      try {
        const url = new URL(config.baseURL);
        if (url.protocol !== 'https:') {
          errors.push('Base URL must use HTTPS');
        }
      } catch {
        errors.push('Invalid base URL format');
      }
    }

    // Timeout validation
    if (config.timeout && (config.timeout < 1000 || config.timeout > 300000)) {
      errors.push('Timeout must be between 1000ms and 300000ms');
    }

    // Retry validation
    if (config.maxRetries && (config.maxRetries < 0 || config.maxRetries > 10)) {
      errors.push('Max retries must be between 0 and 10');
    }

    // Model validation
    if (config.models && config.models.length === 0) {
      warnings.push('No models specified, will use default models');
    }

    return {
      isValid: errors.length === 0,
      errors,
      warnings,
    };
  }

  static sanitizeConfigForLogging(config: OpenAIConfig): Partial<OpenAIConfig> {
    const sanitized = { ...config };

    // Redact API key
    if (sanitized.apiKey) {
      sanitized.apiKey = sanitized.apiKey.substring(0, 7) + '...';
    }

    return sanitized;
  }
}
```

**Files to Create**:

- `packages/providers/src/utils/config-validator.ts`

## Testing Requirements

### Unit Tests

1. **Configuration Validation Tests**:

```typescript
// tests/providers/openai-config.test.ts
describe('OpenAI Configuration', () => {
  test('should validate correct configuration', () => {
    const config = {
      apiKey: 'sk-1234567890abcdef1234567890abcdef1234567890abcdef',
      organization: 'org-1234567890abcdef1234567890',
      baseURL: 'https://api.openai.com',
      maxRetries: 3,
      timeout: 60000,
    };

    const validation = ConfigValidator.validateOpenAIConfig(config);
    expect(validation.isValid).toBe(true);
    expect(validation.errors).toHaveLength(0);
  });

  test('should reject invalid API key format', () => {
    const config = {
      apiKey: 'invalid-key',
      organization: 'org-1234567890abcdef1234567890',
    };

    const validation = ConfigValidator.validateOpenAIConfig(config);
    expect(validation.isValid).toBe(false);
    expect(validation.errors).toContain('Invalid API key format');
  });
});
```

2. **Authentication Tests**:

```typescript
// tests/providers/openai-authentication.test.ts
describe('OpenAI Authentication', () => {
  test('should successfully authenticate with valid API key', async () => {
    const provider = new OpenAIProvider({
      apiKey: process.env.OPENAI_TEST_API_KEY || 'sk-test-key',
    });

    const result = await provider.testAuthentication();
    expect(result.success).toBe(true);
  });

  test('should fail authentication with invalid API key', async () => {
    const provider = new OpenAIProvider({
      apiKey: 'sk-invalid-key',
    });

    const result = await provider.testAuthentication();
    expect(result.success).toBe(false);
    expect(result.error).toBeDefined();
  });
});
```

3. **Security Tests**:

```typescript
// tests/providers/security.test.ts
describe('Security Utils', () => {
  test('should validate API key format correctly', () => {
    expect(
      SecurityUtils.validateApiKeyFormat('sk-1234567890abcdef1234567890abcdef1234567890abcdef')
    ).toBe(true);
    expect(SecurityUtils.validateApiKeyFormat('invalid')).toBe(false);
    expect(SecurityUtils.validateApiKeyFormat('sk-test-123')).toBe(false);
  });

  test('should redact API keys properly', () => {
    const apiKey = 'sk-1234567890abcdef1234567890abcdef1234567890abcdef';
    const redacted = SecurityUtils.redactApiKey(apiKey);
    expect(redacted).toBe('sk-12345...cdef');
  });
});
```

### Integration Tests

1. **End-to-End Provider Tests**:

```typescript
// tests/providers/openai-integration.test.ts
describe('OpenAI Provider Integration', () => {
  let provider: OpenAIProvider;

  beforeAll(async () => {
    provider = new OpenAIProvider({
      apiKey: process.env.OPENAI_TEST_API_KEY || '',
      organization: process.env.OPENAI_TEST_ORG,
      timeout: 30000,
    });
    await provider.initialize();
  });

  afterAll(async () => {
    await provider.dispose();
  });

  test('should initialize successfully', () => {
    expect(provider.isInitialized()).toBe(true);
  });

  test('should get configuration', () => {
    const config = provider.getConfig();
    expect(config).toBeDefined();
    expect(config.apiKey).toBeDefined();
    expect(config.timeout).toBe(30000);
  });
});
```

## Security Considerations

1. **API Key Security**:
   - Never log full API keys
   - Validate API key format before use
   - Reject placeholder/test keys in production
   - Use secure storage for API keys

2. **Configuration Security**:
   - Validate all configuration parameters
   - Sanitize configuration for logging
   - Use HTTPS for all API communications
   - Validate base URLs to prevent SSRF attacks

3. **Error Handling**:
   - Don't expose sensitive information in error messages
   - Use generic error messages for authentication failures
   - Log security events appropriately

## Dependencies

- **Runtime Dependencies**:
  - `openai`: Official OpenAI SDK
  - `zod`: Schema validation
  - `@shared/errors`: Error handling utilities

- **Development Dependencies**:
  - `@types/openai`: TypeScript definitions
  - `vitest`: Testing framework

## Notes

- All API communications must use HTTPS
- API keys should be stored securely using environment variables or secure storage
- Configuration validation should happen at startup
- Provider should be lazy-initialized to avoid unnecessary API calls
- Error messages should be user-friendly while maintaining security
- Consider implementing API key rotation support for production use
