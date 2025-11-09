# Task 6: Create Configuration Validation

**Story**: 2.1 - AI Provider Abstraction Interface  
**Acceptance Criteria**: 6 - Configuration schema validation for each provider  
**Status**: Ready for Development

## Overview

This task involves implementing comprehensive configuration validation using JSON schemas to ensure that provider configurations are valid, complete, and secure before provider initialization. The system must validate structure, data types, values, and business rules for each provider type.

## Subtasks

### Subtask 6.1: Define Configuration Schemas

**Objective**: Create JSON schemas for validating provider configurations.

**Implementation Details**:

1. **File Location**: `packages/providers/src/config/schemas/`

2. **Base Configuration Schema**:

   ```json
   {
     "$schema": "http://json-schema.org/draft-07/schema#",
     "$id": "https://tamma.ai/schemas/provider-config-base.json",
     "title": "Base Provider Configuration",
     "description": "Base configuration schema for all AI providers",
     "type": "object",
     "properties": {
       "type": {
         "type": "string",
         "enum": [
           "anthropic",
           "openai",
           "github-copilot",
           "google-gemini",
           "opencode",
           "z-ai",
           "zen-mcp",
           "openrouter",
           "local",
           "custom"
         ],
         "description": "Provider type identifier"
       },
       "name": {
         "type": "string",
         "minLength": 1,
         "maxLength": 100,
         "pattern": "^[a-zA-Z][a-zA-Z0-9_-]*$",
         "description": "Unique name for this provider instance"
       },
       "enabled": {
         "type": "boolean",
         "default": true,
         "description": "Whether this provider is enabled"
       },
       "priority": {
         "type": "integer",
         "minimum": 1,
         "maximum": 100,
         "default": 50,
         "description": "Priority for provider selection (higher = more preferred)"
       },
       "timeout": {
         "type": "integer",
         "minimum": 1000,
         "maximum": 300000,
         "default": 30000,
         "description": "Request timeout in milliseconds"
       },
       "retry": {
         "$ref": "#/definitions/RetryConfig"
       },
       "rateLimit": {
         "$ref": "#/definitions/RateLimitConfig"
       },
       "logging": {
         "$ref": "#/definitions/LoggingConfig"
       },
       "metadata": {
         "type": "object",
         "additionalProperties": {
           "type": ["string", "number", "boolean"]
         },
         "description": "Additional metadata for the provider"
       }
     },
     "required": ["type", "name"],
     "additionalProperties": false,
     "definitions": {
       "RetryConfig": {
         "type": "object",
         "properties": {
           "maxAttempts": {
             "type": "integer",
             "minimum": 1,
             "maximum": 10,
             "default": 3
           },
           "baseDelay": {
             "type": "integer",
             "minimum": 100,
             "maximum": 60000,
             "default": 1000
           },
           "maxDelay": {
             "type": "integer",
             "minimum": 1000,
             "maximum": 300000,
             "default": 30000
           },
           "backoffMultiplier": {
             "type": "number",
             "minimum": 1.0,
             "maximum": 5.0,
             "default": 2.0
           },
           "jitterEnabled": {
             "type": "boolean",
             "default": true
           },
           "jitterFactor": {
             "type": "number",
             "minimum": 0.0,
             "maximum": 1.0,
             "default": 0.1
           }
         },
         "additionalProperties": false
       },
       "RateLimitConfig": {
         "type": "object",
         "properties": {
           "requestsPerMinute": {
             "type": "integer",
             "minimum": 1,
             "maximum": 10000
           },
           "tokensPerMinute": {
             "type": "integer",
             "minimum": 1000,
             "maximum": 10000000
           },
           "concurrentRequests": {
             "type": "integer",
             "minimum": 1,
             "maximum": 100
           },
           "autoThrottle": {
             "type": "boolean",
             "default": true
           }
         },
         "additionalProperties": false
       },
       "LoggingConfig": {
         "type": "object",
         "properties": {
           "level": {
             "type": "string",
             "enum": ["debug", "info", "warn", "error"],
             "default": "info"
           },
           "logRequests": {
             "type": "boolean",
             "default": false
           },
           "logResponses": {
             "type": "boolean",
             "default": false
           },
           "sanitizeLogs": {
             "type": "boolean",
             "default": true
           },
           "maxLogSize": {
             "type": "integer",
             "minimum": 100,
             "maximum": 100000,
             "default": 10000
           }
         },
         "additionalProperties": false
       }
     }
   }
   ```

3. **Anthropic Claude Configuration Schema**:

   ```json
   {
     "$schema": "http://json-schema.org/draft-07/schema#",
     "$id": "https://tamma.ai/schemas/provider-config-anthropic.json",
     "title": "Anthropic Claude Provider Configuration",
     "description": "Configuration schema for Anthropic Claude provider",
     "allOf": [
       {
         "$ref": "provider-config-base.json"
       },
       {
         "type": "object",
         "properties": {
           "type": {
             "const": "anthropic"
           },
           "apiKey": {
             "type": "string",
             "minLength": 1,
             "description": "Anthropic API key (can also be set via ANTHROPIC_API_KEY env var)"
           },
           "baseUrl": {
             "type": "string",
             "format": "uri",
             "default": "https://api.anthropic.com",
             "description": "Anthropic API base URL"
           },
           "version": {
             "type": "string",
             "enum": ["2023-06-01", "2024-02-27"],
             "default": "2023-06-01",
             "description": "API version"
           },
           "model": {
             "type": "string",
             "enum": [
               "claude-3-5-sonnet-20241022",
               "claude-3-5-haiku-20241022",
               "claude-3-opus-20240229",
               "claude-3-sonnet-20240229",
               "claude-3-haiku-20240307"
             ],
             "default": "claude-3-5-sonnet-20241022",
             "description": "Default model to use"
           },
           "maxTokens": {
             "type": "integer",
             "minimum": 1,
             "maximum": 200000,
             "default": 4096,
             "description": "Maximum tokens in response"
           },
           "temperature": {
             "type": "number",
             "minimum": 0.0,
             "maximum": 1.0,
             "default": 1.0,
             "description": "Sampling temperature"
           },
           "topP": {
             "type": "number",
             "minimum": 0.0,
             "maximum": 1.0,
             "default": 1.0,
             "description": "Top-p sampling parameter"
           }
         },
         "required": ["apiKey"],
         "additionalProperties": false
       }
     ]
   }
   ```

4. **OpenAI Configuration Schema**:

   ```json
   {
     "$schema": "http://json-schema.org/draft-07/schema#",
     "$id": "https://tamma.ai/schemas/provider-config-openai.json",
     "title": "OpenAI Provider Configuration",
     "description": "Configuration schema for OpenAI provider",
     "allOf": [
       {
         "$ref": "provider-config-base.json"
       },
       {
         "type": "object",
         "properties": {
           "type": {
             "const": "openai"
           },
           "apiKey": {
             "type": "string",
             "minLength": 1,
             "description": "OpenAI API key (can also be set via OPENAI_API_KEY env var)"
           },
           "organization": {
             "type": "string",
             "description": "OpenAI organization ID (optional)"
           },
           "baseUrl": {
             "type": "string",
             "format": "uri",
             "default": "https://api.openai.com/v1",
             "description": "OpenAI API base URL"
           },
           "model": {
             "type": "string",
             "enum": [
               "gpt-4-turbo-preview",
               "gpt-4-1106-preview",
               "gpt-4",
               "gpt-3.5-turbo",
               "gpt-3.5-turbo-16k"
             ],
             "default": "gpt-4-turbo-preview",
             "description": "Default model to use"
           },
           "maxTokens": {
             "type": "integer",
             "minimum": 1,
             "maximum": 128000,
             "default": 4096,
             "description": "Maximum tokens in response"
           },
           "temperature": {
             "type": "number",
             "minimum": 0.0,
             "maximum": 2.0,
             "default": 1.0,
             "description": "Sampling temperature"
           },
           "topP": {
             "type": "number",
             "minimum": 0.0,
             "maximum": 1.0,
             "default": 1.0,
             "description": "Top-p sampling parameter"
           },
           "frequencyPenalty": {
             "type": "number",
             "minimum": -2.0,
             "maximum": 2.0,
             "default": 0.0,
             "description": "Frequency penalty"
           },
           "presencePenalty": {
             "type": "number",
             "minimum": -2.0,
             "maximum": 2.0,
             "default": 0.0,
             "description": "Presence penalty"
           },
           "responseFormat": {
             "type": "object",
             "properties": {
               "type": {
                 "type": "string",
                 "enum": ["text", "json_object", "json_schema"]
               }
             },
             "additionalProperties": false
           }
         },
         "required": ["apiKey"],
         "additionalProperties": false
       }
     ]
   }
   ```

5. **Local Provider Configuration Schema**:
   ```json
   {
     "$schema": "http://json-schema.org/draft-07/schema#",
     "$id": "https://tamma.ai/schemas/provider-config-local.json",
     "title": "Local Provider Configuration",
     "description": "Configuration schema for local LLM provider",
     "allOf": [
       {
         "$ref": "provider-config-base.json"
       },
       {
         "type": "object",
         "properties": {
           "type": {
             "const": "local"
           },
           "endpoint": {
             "type": "string",
             "format": "uri",
             "description": "Local model endpoint URL"
           },
           "modelPath": {
             "type": "string",
             "description": "Path to local model file"
           },
           "modelFormat": {
             "type": "string",
             "enum": ["gguf", "safetensors", "pytorch", "onnx"],
             "description": "Model file format"
           },
           "contextLength": {
             "type": "integer",
             "minimum": 512,
             "maximum": 200000,
             "default": 4096,
             "description": "Model context length"
           },
           "gpuLayers": {
             "type": "integer",
             "minimum": 0,
             "maximum": 100,
             "default": 0,
             "description": "Number of GPU layers (0 = CPU only)"
           },
           "threads": {
             "type": "integer",
             "minimum": 1,
             "maximum": 32,
             "default": 4,
             "description": "Number of CPU threads"
           },
           "batchSize": {
             "type": "integer",
             "minimum": 1,
             "maximum": 2048,
             "default": 512,
             "description": "Batch size for processing"
           }
         },
         "oneOf": [
           {
             "required": ["endpoint"]
           },
           {
             "required": ["modelPath"]
           }
         ],
         "additionalProperties": false
       }
     ]
   }
   ```

### Subtask 6.2: Implement Validation Logic

**Objective**: Create validation engine that can validate configurations against schemas.

**Implementation Details**:

1. **File Location**: `packages/providers/src/config/ConfigurationValidator.ts`

2. **Configuration Validator Class**:

   ```typescript
   import Ajv from 'ajv';
   import addFormats from 'ajv-formats';
   import { ProviderConfig, ValidationResult } from '../types';

   export class ConfigurationValidator {
     private ajv: Ajv;
     private schemas: Map<string, object> = new Map();
     private compiledValidators: Map<string, Ajv.ValidateFunction> = new Map();

     constructor() {
       this.ajv = new Ajv({
         allErrors: true,
         verbose: true,
         strict: true,
         removeAdditional: true,
         useDefaults: true,
         coerceTypes: true,
       });

       addFormats(this.ajv);
       this.loadSchemas();
     }

     async validate(config: ProviderConfig): Promise<ValidationResult> {
       const schemaId = this.getSchemaId(config.type);

       if (!schemaId) {
         return {
           valid: false,
           errors: [
             {
               field: 'type',
               code: 'UNKNOWN_PROVIDER_TYPE',
               message: `Unknown provider type: ${config.type}`,
               value: config.type,
             },
           ],
         };
       }

       const validator = this.getValidator(schemaId);

       if (!validator) {
         return {
           valid: false,
           errors: [
             {
               field: 'schema',
               code: 'SCHEMA_NOT_FOUND',
               message: `Schema not found for provider type: ${config.type}`,
               value: config.type,
             },
           ],
         };
       }

       const isValid = validator(config);

       if (isValid) {
         return {
           valid: true,
           errors: [],
           warnings: this.generateWarnings(config),
           normalized: config,
         };
       }

       const errors = this.formatValidationErrors(validator.errors || []);

       return {
         valid: false,
         errors,
         warnings: this.generateWarnings(config),
       };
     }

     async validateBatch(configs: ProviderConfig[]): Promise<BatchValidationResult> {
       const results: ValidationResult[] = [];
       const duplicateNames = this.findDuplicateNames(configs);

       for (const config of configs) {
         const result = await this.validate(config);

         // Check for duplicate names
         if (duplicateNames.includes(config.name)) {
           result.valid = false;
           result.errors.push({
             field: 'name',
             code: 'DUPLICATE_NAME',
             message: `Provider name '${config.name}' is not unique`,
             value: config.name,
           });
         }

         results.push(result);
       }

       const overallValid = results.every((r) => r.valid);
       const totalErrors = results.reduce((sum, r) => sum + r.errors.length, 0);
       const totalWarnings = results.reduce((sum, r) => sum + (r.warnings?.length || 0), 0);

       return {
         valid: overallValid,
         results,
         summary: {
           total: configs.length,
           valid: results.filter((r) => r.valid).length,
           invalid: results.filter((r) => !r.valid).length,
           totalErrors,
           totalWarnings,
         },
       };
     }

     validateEnvironment(config: ProviderConfig): ValidationResult {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       // Check for required environment variables
       const requiredEnvVars = this.getRequiredEnvironmentVariables(config.type);

       for (const envVar of requiredEnvVars) {
         if (!process.env[envVar]) {
           errors.push({
             field: 'environment',
             code: 'MISSING_ENV_VAR',
             message: `Required environment variable ${envVar} is not set`,
             value: envVar,
           });
         }
       }

       // Check for optional but recommended environment variables
       const recommendedEnvVars = this.getRecommendedEnvironmentVariables(config.type);

       for (const envVar of recommendedEnvVars) {
         if (!process.env[envVar]) {
           warnings.push({
             field: 'environment',
             code: 'MISSING_RECOMMENDED_ENV_VAR',
             message: `Recommended environment variable ${envVar} is not set`,
             value: envVar,
             recommendation: `Set ${envVar} for better performance or security`,
           });
         }
       }

       // Validate API key format if present
       if (config.apiKey) {
         const keyValidation = this.validateApiKeyFormat(config.type, config.apiKey);
         if (!keyValidation.valid) {
           errors.push(...keyValidation.errors);
         }
       }

       return {
         valid: errors.length === 0,
         errors,
         warnings,
       };
     }

     private loadSchemas(): void {
       const schemas = [
         require('./schemas/provider-config-base.json'),
         require('./schemas/provider-config-anthropic.json'),
         require('./schemas/provider-config-openai.json'),
         require('./schemas/provider-config-github-copilot.json'),
         require('./schemas/provider-config-google-gemini.json'),
         require('./schemas/provider-config-opencode.json'),
         require('./schemas/provider-config-z-ai.json'),
         require('./schemas/provider-config-zen-mcp.json'),
         require('./schemas/provider-config-openrouter.json'),
         require('./schemas/provider-config-local.json'),
       ];

       for (const schema of schemas) {
         this.ajv.addSchema(schema);
         this.schemas.set(schema.$id, schema);
       }
     }

     private getSchemaId(providerType: string): string | null {
       const schemaMap: Record<string, string> = {
         anthropic: 'https://tamma.ai/schemas/provider-config-anthropic.json',
         openai: 'https://tamma.ai/schemas/provider-config-openai.json',
         'github-copilot': 'https://tamma.ai/schemas/provider-config-github-copilot.json',
         'google-gemini': 'https://tamma.ai/schemas/provider-config-google-gemini.json',
         opencode: 'https://tamma.ai/schemas/provider-config-opencode.json',
         'z-ai': 'https://tamma.ai/schemas/provider-config-z-ai.json',
         'zen-mcp': 'https://tamma.ai/schemas/provider-config-zen-mcp.json',
         openrouter: 'https://tamma.ai/schemas/provider-config-openrouter.json',
         local: 'https://tamma.ai/schemas/provider-config-local.json',
       };

       return schemaMap[providerType] || null;
     }

     private getValidator(schemaId: string): Ajv.ValidateFunction | null {
       if (this.compiledValidators.has(schemaId)) {
         return this.compiledValidators.get(schemaId)!;
       }

       const validator = this.ajv.getSchema(schemaId);
       if (validator) {
         this.compiledValidators.set(schemaId, validator);
       }

       return validator || null;
     }

     private formatValidationErrors(ajvErrors: Ajv.ErrorObject[]): ValidationError[] {
       return ajvErrors.map((error) => ({
         field: this.getFieldPath(error),
         code: this.getErrorCode(error),
         message: this.getErrorMessage(error),
         value: error.data,
         allowedValues: error.schema?.enum,
         constraint: error.schema,
       }));
     }

     private getFieldPath(error: Ajv.ErrorObject): string {
       if (error.instancePath) {
         return error.instancePath.replace(/^\//, '').replace(/\//g, '.');
       }

       if (error.dataPath) {
         return error.dataPath.replace(/^\./, '');
       }

       return error.schemaPath || 'unknown';
     }

     private getErrorCode(error: Ajv.ErrorObject): string {
       const codeMap: Record<string, string> = {
         required: 'REQUIRED_FIELD',
         type: 'INVALID_TYPE',
         enum: 'INVALID_VALUE',
         minimum: 'VALUE_TOO_SMALL',
         maximum: 'VALUE_TOO_LARGE',
         minLength: 'STRING_TOO_SHORT',
         maxLength: 'STRING_TOO_LONG',
         pattern: 'INVALID_FORMAT',
         format: 'INVALID_FORMAT',
         additionalProperties: 'ADDITIONAL_PROPERTY',
         oneOf: 'INVALID_COMBINATION',
         anyOf: 'INVALID_COMBINATION',
         allOf: 'INVALID_COMBINATION',
         not: 'INVALID_VALUE',
       };

       return codeMap[error.keyword!] || 'VALIDATION_ERROR';
     }

     private getErrorMessage(error: Ajv.ErrorObject): string {
       if (error.message) {
         return error.message;
       }

       const keywordMessages: Record<string, string> = {
         required: 'This field is required',
         type: `Expected type ${error.schema?.type}`,
         enum: `Value must be one of: ${error.schema?.enum?.join(', ')}`,
         minimum: `Value must be >= ${error.schema?.minimum}`,
         maximum: `Value must be <= ${error.schema?.maximum}`,
         minLength: `String must be at least ${error.schema?.minLength} characters`,
         maxLength: `String must be at most ${error.schema?.maxLength} characters`,
         pattern: 'String format is invalid',
         format: 'String format is invalid',
         additionalProperties: `Additional property not allowed: ${error.params?.additionalProperty}`,
         oneOf: 'Value must match exactly one schema',
         anyOf: 'Value must match at least one schema',
         allOf: 'Value must match all schemas',
         not: 'Value must not match schema',
       };

       return keywordMessages[error.keyword!] || 'Validation failed';
     }

     private generateWarnings(config: ProviderConfig): ValidationWarning[] {
       const warnings: ValidationWarning[] = [];

       // Check for insecure configurations
       if (config.apiKey && config.apiKey.length < 20) {
         warnings.push({
           field: 'apiKey',
           code: 'WEAK_API_KEY',
           message: 'API key appears to be unusually short',
           value: '[REDACTED]',
           recommendation: 'Verify that the API key is correct and complete',
         });
       }

       // Check for non-HTTPS endpoints
       if (config.baseUrl && config.baseUrl.startsWith('http://')) {
         warnings.push({
           field: 'baseUrl',
           code: 'INSECURE_ENDPOINT',
           message: 'Base URL uses HTTP instead of HTTPS',
           value: config.baseUrl,
           recommendation: 'Use HTTPS for secure communication',
         });
       }

       // Check for very low timeouts
       if (config.timeout && config.timeout < 5000) {
         warnings.push({
           field: 'timeout',
           code: 'LOW_TIMEOUT',
           message: 'Timeout value is very low, may cause failures',
           value: config.timeout,
           recommendation: 'Consider increasing timeout to at least 10 seconds',
         });
       }

       // Check for excessive retry attempts
       if (config.retry?.maxAttempts && config.retry.maxAttempts > 5) {
         warnings.push({
           field: 'retry.maxAttempts',
           code: 'EXCESSIVE_RETRIES',
           message: 'High number of retry attempts may cause delays',
           value: config.retry.maxAttempts,
           recommendation: 'Consider reducing retry attempts to 3-5',
         });
       }

       return warnings;
     }

     private findDuplicateNames(configs: ProviderConfig[]): string[] {
       const nameCounts = new Map<string, number>();

       for (const config of configs) {
         nameCounts.set(config.name, (nameCounts.get(config.name) || 0) + 1);
       }

       return Array.from(nameCounts.entries())
         .filter(([_, count]) => count > 1)
         .map(([name, _]) => name);
     }

     private getRequiredEnvironmentVariables(providerType: string): string[] {
       const envVarMap: Record<string, string[]> = {
         anthropic: ['ANTHROPIC_API_KEY'],
         openai: ['OPENAI_API_KEY'],
         'github-copilot': ['GITHUB_TOKEN'],
         'google-gemini': ['GOOGLE_AI_API_KEY'],
         opencode: ['OPENCODE_API_KEY'],
         'z-ai': ['Z_AI_API_KEY'],
         'zen-mcp': ['ZEN_MCP_API_KEY'],
         openrouter: ['OPENROUTER_API_KEY'],
       };

       return envVarMap[providerType] || [];
     }

     private getRecommendedEnvironmentVariables(providerType: string): string[] {
       const envVarMap: Record<string, string[]> = {
         openai: ['OPENAI_ORGANIZATION'],
         anthropic: ['ANTHROPIC_BASE_URL'],
         'google-gemini': ['GOOGLE_AI_PROJECT_ID'],
       };

       return envVarMap[providerType] || [];
     }

     private validateApiKeyFormat(providerType: string, apiKey: string): ValidationResult {
       const errors: ValidationError[] = [];

       // Basic format validation
       if (apiKey.length < 10) {
         errors.push({
           field: 'apiKey',
           code: 'INVALID_API_KEY_FORMAT',
           message: 'API key is too short',
           value: '[REDACTED]',
         });
       }

       // Provider-specific validation
       const patterns: Record<string, RegExp> = {
         anthropic: /^sk-ant-api03-[A-Za-z0-9_-]{95}$/,
         openai: /^sk-[A-Za-z0-9]{48}$/,
         'github-copilot': /^ghp_[A-Za-z0-9]{36}$/,
         'google-gemini': /^[A-Za-z0-9_-]{39}$/,
       };

       const pattern = patterns[providerType];
       if (pattern && !pattern.test(apiKey)) {
         errors.push({
           field: 'apiKey',
           code: 'INVALID_API_KEY_FORMAT',
           message: `API key format is invalid for ${providerType}`,
           value: '[REDACTED]',
         });
       }

       return {
         valid: errors.length === 0,
         errors,
       };
     }
   }

   export interface ValidationResult {
     valid: boolean;
     errors: ValidationError[];
     warnings?: ValidationWarning[];
     normalized?: ProviderConfig;
   }

   export interface ValidationError {
     field: string;
     code: string;
     message: string;
     value: any;
     allowedValues?: any[];
     constraint?: any;
   }

   export interface ValidationWarning {
     field: string;
     code: string;
     message: string;
     value: any;
     recommendation?: string;
   }

   export interface BatchValidationResult {
     valid: boolean;
     results: ValidationResult[];
     summary: {
       total: number;
       valid: number;
       invalid: number;
       totalErrors: number;
       totalWarnings: number;
     };
   }
   ```

### Subtask 6.3: Add Configuration Error Handling

**Objective**: Implement comprehensive error handling for configuration validation failures.

**Implementation Details**:

1. **File Location**: `packages/providers/src/config/ConfigurationErrorHandler.ts`

2. **Configuration Error Handler Class**:

   ```typescript
   export class ConfigurationErrorHandler {
     private logger: Logger;

     constructor(logger: Logger) {
       this.logger = logger;
     }

     handleValidationError(result: ValidationResult, config: ProviderConfig): ConfigurationError {
       const primaryError = result.errors[0];

       if (!primaryError) {
         return new ConfigurationError(
           'UNKNOWN_VALIDATION_ERROR',
           'Unknown configuration validation error',
           config.type,
           config.name
         );
       }

       const error = this.createSpecificError(primaryError, config);

       // Log the error with context
       this.logger.error('Configuration validation failed', {
         providerType: config.type,
         providerName: config.name,
         errorCode: error.code,
         errorMessage: error.message,
         field: primaryError.field,
         validationErrors: result.errors,
       });

       return error;
     }

     handleBatchValidationError(result: BatchValidationResult): ConfigurationError[] {
       const errors: ConfigurationError[] = [];

       for (const validationResult of result.results) {
         if (!validationResult.valid) {
           // Extract provider info from the normalized config or create a placeholder
           const config = validationResult.normalized || { type: 'unknown', name: 'unknown' };
           const error = this.handleValidationError(validationResult, config as ProviderConfig);
           errors.push(error);
         }
       }

       this.logger.error('Batch configuration validation failed', {
         totalConfigs: result.summary.total,
         invalidConfigs: result.summary.invalid,
         totalErrors: result.summary.totalErrors,
         errors: errors.map((e) => ({
           code: e.code,
           message: e.message,
           provider: e.providerName,
         })),
       });

       return errors;
     }

     suggestFixes(error: ConfigurationError): ConfigurationFix[] {
       const fixSuggestions: ConfigurationFix[] = [];

       switch (error.code) {
         case 'REQUIRED_FIELD':
           fixSuggestions.push({
             type: 'add_field',
             description: `Add required field '${error.field}'`,
             field: error.field,
             suggestedValue: this.getSuggestedValue(error.field, error.providerType),
             priority: 'high',
           });
           break;

         case 'INVALID_TYPE':
           fixSuggestions.push({
             type: 'fix_type',
             description: `Fix type for field '${error.field}'`,
             field: error.field,
             suggestedValue: this.getTypeSuggestion(error.field, error.providerType),
             priority: 'high',
           });
           break;

         case 'INVALID_VALUE':
           fixSuggestions.push({
             type: 'fix_value',
             description: `Fix value for field '${error.field}'`,
             field: error.field,
             suggestedValue: error.allowedValues?.[0],
             priority: 'high',
           });
           break;

         case 'MISSING_ENV_VAR':
           fixSuggestions.push({
             type: 'set_env_var',
             description: `Set environment variable ${error.value}`,
             field: 'environment',
             suggestedValue: `export ${error.value}=your_api_key_here`,
             priority: 'high',
           });
           break;

         case 'INSECURE_ENDPOINT':
           fixSuggestions.push({
             type: 'fix_url',
             description: 'Use HTTPS for secure communication',
             field: error.field,
             suggestedValue: error.value?.replace('http://', 'https://'),
             priority: 'medium',
           });
           break;

         case 'WEAK_API_KEY':
           fixSuggestions.push({
             type: 'verify_api_key',
             description: 'Verify API key is correct and complete',
             field: error.field,
             suggestedValue: 'Check your provider dashboard for the correct API key',
             priority: 'medium',
           });
           break;

         case 'DUPLICATE_NAME':
           fixSuggestions.push({
             type: 'rename_provider',
             description: 'Choose a unique name for this provider',
             field: error.field,
             suggestedValue: `${error.providerName}-${Date.now()}`,
             priority: 'high',
           });
           break;
       }

       return fixSuggestions;
     }

     generateErrorReport(results: ValidationResult[]): ConfigurationErrorReport {
       const errorCounts = this.countErrorsByType(results);
       const fieldErrors = this.groupErrorsByField(results);
       const providerErrors = this.groupErrorsByProvider(results);

       return {
         summary: {
           totalConfigs: results.length,
           validConfigs: results.filter((r) => r.valid).length,
           invalidConfigs: results.filter((r) => !r.valid).length,
           totalErrors: results.reduce((sum, r) => sum + r.errors.length, 0),
           totalWarnings: results.reduce((sum, r) => sum + (r.warnings?.length || 0), 0),
         },
         errorCounts,
         fieldErrors,
         providerErrors,
         recommendations: this.generateRecommendations(errorCounts, fieldErrors),
       };
     }

     private createSpecificError(
       error: ValidationError,
       config: ProviderConfig
     ): ConfigurationError {
       const errorMap: Record<string, typeof ConfigurationError> = {
         REQUIRED_FIELD: RequiredFieldError,
         INVALID_TYPE: InvalidTypeError,
         INVALID_VALUE: InvalidValueError,
         MISSING_ENV_VAR: MissingEnvironmentVariableError,
         INSECURE_ENDPOINT: InsecureEndpointError,
         WEAK_API_KEY: WeakAPIKeyError,
         DUPLICATE_NAME: DuplicateNameError,
         UNKNOWN_PROVIDER_TYPE: UnknownProviderTypeError,
         SCHEMA_NOT_FOUND: SchemaNotFoundError,
       };

       const ErrorClass = errorMap[error.code] || ConfigurationError;

       return new ErrorClass(
         error.code,
         error.message,
         config.type,
         config.name,
         error.field,
         error.value,
         error.allowedValues
       );
     }

     private getSuggestedValue(field: string, providerType: string): any {
       const suggestions: Record<string, Record<string, any>> = {
         anthropic: {
           model: 'claude-3-5-sonnet-20241022',
           maxTokens: 4096,
           temperature: 1.0,
           timeout: 30000,
         },
         openai: {
           model: 'gpt-4-turbo-preview',
           maxTokens: 4096,
           temperature: 1.0,
           timeout: 30000,
         },
       };

       return suggestions[providerType]?.[field] || null;
     }

     private getTypeSuggestion(field: string, providerType: string): any {
       const typeSuggestions: Record<string, any> = {
         model: 'gpt-4-turbo-preview',
         maxTokens: 4096,
         temperature: 1.0,
         timeout: 30000,
         enabled: true,
         priority: 50,
       };

       return typeSuggestions[field] || null;
     }

     private countErrorsByType(results: ValidationResult[]): Record<string, number> {
       const counts: Record<string, number> = {};

       for (const result of results) {
         for (const error of result.errors) {
           counts[error.code] = (counts[error.code] || 0) + 1;
         }
       }

       return counts;
     }

     private groupErrorsByField(results: ValidationResult[]): Record<string, number> {
       const groups: Record<string, number> = {};

       for (const result of results) {
         for (const error of result.errors) {
           groups[error.field] = (groups[error.field] || 0) + 1;
         }
       }

       return groups;
     }

     private groupErrorsByProvider(
       results: ValidationResult[]
     ): Record<string, ValidationResult[]> {
       const groups: Record<string, ValidationResult[]> = {};

       for (const result of results) {
         if (!result.valid) {
           const providerType = result.normalized?.type || 'unknown';
           if (!groups[providerType]) {
             groups[providerType] = [];
           }
           groups[providerType].push(result);
         }
       }

       return groups;
     }

     private generateRecommendations(
       errorCounts: Record<string, number>,
       fieldErrors: Record<string, number>
     ): string[] {
       const recommendations: string[] = [];

       // Most common errors
       const sortedErrors = Object.entries(errorCounts)
         .sort(([, a], [, b]) => b - a)
         .slice(0, 3);

       for (const [errorCode, count] of sortedErrors) {
         switch (errorCode) {
           case 'REQUIRED_FIELD':
             recommendations.push(
               `${count} configurations are missing required fields. Check the provider documentation for required parameters.`
             );
             break;
           case 'MISSING_ENV_VAR':
             recommendations.push(
               `${count} configurations are missing environment variables. Ensure all required API keys are set.`
             );
             break;
           case 'INVALID_TYPE':
             recommendations.push(
               `${count} configurations have incorrect field types. Verify data types match the schema.`
             );
             break;
         }
       }

       // Most problematic fields
       const sortedFields = Object.entries(fieldErrors)
         .sort(([, a], [, b]) => b - a)
         .slice(0, 3);

       if (sortedFields.length > 0) {
         const fieldNames = sortedFields.map(([field]) => field).join(', ');
         recommendations.push(
           `The most problematic fields are: ${fieldNames}. Pay special attention to these fields.`
         );
       }

       return recommendations;
     }
   }

   // Specialized error classes
   export class ConfigurationError extends Error {
     constructor(
       public code: string,
       message: string,
       public providerType: string,
       public providerName: string,
       public field?: string,
       public value?: any,
       public allowedValues?: any[]
     ) {
       super(message);
       this.name = 'ConfigurationError';
     }
   }

   export class RequiredFieldError extends ConfigurationError {
     constructor(field: string, providerType: string, providerName: string) {
       super(
         'REQUIRED_FIELD',
         `Required field '${field}' is missing`,
         providerType,
         providerName,
         field
       );
       this.name = 'RequiredFieldError';
     }
   }

   export class InvalidTypeError extends ConfigurationError {
     constructor(
       field: string,
       expectedType: string,
       actualValue: any,
       providerType: string,
       providerName: string
     ) {
       super(
         'INVALID_TYPE',
         `Field '${field}' must be of type ${expectedType}`,
         providerType,
         providerName,
         field,
         actualValue
       );
       this.name = 'InvalidTypeError';
     }
   }

   export class InvalidValueError extends ConfigurationError {
     constructor(
       field: string,
       value: any,
       allowedValues: any[],
       providerType: string,
       providerName: string
     ) {
       super(
         'INVALID_VALUE',
         `Field '${field}' has invalid value`,
         providerType,
         providerName,
         field,
         value,
         allowedValues
       );
       this.name = 'InvalidValueError';
     }
   }

   export class MissingEnvironmentVariableError extends ConfigurationError {
     constructor(envVar: string, providerType: string, providerName: string) {
       super(
         'MISSING_ENV_VAR',
         `Environment variable ${envVar} is required`,
         providerType,
         providerName,
         'environment',
         envVar
       );
       this.name = 'MissingEnvironmentVariableError';
     }
   }

   export class InsecureEndpointError extends ConfigurationError {
     constructor(url: string, providerType: string, providerName: string) {
       super(
         'INSECURE_ENDPOINT',
         `Endpoint ${url} uses insecure HTTP protocol`,
         providerType,
         providerName,
         'baseUrl',
         url
       );
       this.name = 'InsecureEndpointError';
     }
   }

   export class WeakAPIKeyError extends ConfigurationError {
     constructor(providerType: string, providerName: string) {
       super(
         'WEAK_API_KEY',
         'API key appears to be invalid or incomplete',
         providerType,
         providerName,
         'apiKey'
       );
       this.name = 'WeakAPIKeyError';
     }
   }

   export class DuplicateNameError extends ConfigurationError {
     constructor(name: string, providerType: string) {
       super(
         'DUPLICATE_NAME',
         `Provider name '${name}' is not unique`,
         providerType,
         name,
         'name',
         name
       );
       this.name = 'DuplicateNameError';
     }
   }

   export interface ConfigurationFix {
     type:
       | 'add_field'
       | 'fix_type'
       | 'fix_value'
       | 'set_env_var'
       | 'fix_url'
       | 'verify_api_key'
       | 'rename_provider';
     description: string;
     field: string;
     suggestedValue: any;
     priority: 'low' | 'medium' | 'high';
   }

   export interface ConfigurationErrorReport {
     summary: {
       totalConfigs: number;
       validConfigs: number;
       invalidConfigs: number;
       totalErrors: number;
       totalWarnings: number;
     };
     errorCounts: Record<string, number>;
     fieldErrors: Record<string, number>;
     providerErrors: Record<string, ValidationResult[]>;
     recommendations: string[];
   }
   ```

## Technical Requirements

### Validation Requirements

- All configurations must be validated against JSON schemas
- Validation must provide clear, actionable error messages
- Support for both individual and batch validation
- Environment variable validation and integration

### Error Handling Requirements

- Specialized error classes for different validation failures
- Automatic fix suggestions for common issues
- Comprehensive error reporting and analytics
- Integration with logging and monitoring systems

### Performance Requirements

- Schema compilation should be cached for performance
- Validation should complete within 100ms per configuration
- Batch validation should be parallelized where possible
- Memory usage should be minimal for large configuration sets

## Testing Strategy

### Unit Tests

```typescript
describe('ConfigurationValidator', () => {
  describe('validation', () => {
    it('should validate valid configurations');
    it('should reject invalid configurations');
    it('should provide clear error messages');
    it('should handle missing required fields');
    it('should validate field types and values');
  });

  describe('batch validation', () => {
    it('should validate multiple configurations');
    it('should detect duplicate names');
    it('should provide batch summary');
  });

  describe('environment validation', () => {
    it('should check required environment variables');
    it('should validate API key formats');
    it('should provide warnings for missing optional vars');
  });
});
```

### Integration Tests

- Test validation with real configuration files
- Test environment variable integration
- Test error handling and fix suggestions
- Test performance with large configuration sets

### Schema Tests

- Validate all JSON schemas against the specification
- Test schema inheritance and composition
- Test custom format validators
- Test schema compilation and caching

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared utilities and types
- Task 1 output - Base provider interfaces

### External Dependencies

- `ajv` - JSON schema validation
- `ajv-formats` - Format validation for AJV
- TypeScript 5.7+ - Type safety

## Deliverables

1. **JSON Schemas**: `packages/providers/src/config/schemas/`
2. **Configuration Validator**: `packages/providers/src/config/ConfigurationValidator.ts`
3. **Error Handler**: `packages/providers/src/config/ConfigurationErrorHandler.ts`
4. **Error Classes**: `packages/providers/src/config/errors/`
5. **Unit Tests**: `packages/providers/src/config/__tests__/`
6. **Integration Tests**: `packages/providers/src/config/__integration__/`
7. **Schema Documentation**: `packages/providers/src/config/README.md`

## Acceptance Criteria Verification

- [ ] JSON schemas cover all provider configuration options
- [ ] Validation logic catches all configuration errors
- [ ] Error handling provides actionable feedback
- [ ] Environment variable integration works correctly
- [ ] Batch validation handles multiple configurations
- [ ] Performance requirements are met
- [ ] Error reporting is comprehensive and useful
- [ ] Fix suggestions help users resolve issues

## Implementation Notes

### Schema Management

Use a modular schema approach:

- Base schema with common fields
- Provider-specific schemas extending base
- Version-specific schemas for backward compatibility
- Automated schema validation and testing

### Validation Strategy

Implement multi-layer validation:

1. **Schema Validation**: Basic structure and types
2. **Business Logic Validation**: Provider-specific rules
3. **Environment Validation**: External dependencies
4. **Security Validation**: Sensitive data checks

### Error Reporting

Provide comprehensive error information:

- Exact field location and error type
- Suggested fixes with examples
- Links to documentation
- Context-specific recommendations

## Next Steps

After completing this task:

1. Move to Task 7: Implement Mock Provider
2. Integrate configuration validation with provider registry
3. Add configuration management CLI tools

## Risk Mitigation

### Technical Risks

- **Schema Complexity**: Keep schemas maintainable and well-documented
- **Performance Overhead**: Optimize validation for large configuration sets
- **False Positives**: Ensure validation rules are accurate

### Mitigation Strategies

- Regular schema reviews and refactoring
- Performance profiling and optimization
- Comprehensive testing with real configurations
- User feedback integration for validation rules
