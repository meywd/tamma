# Implementation Plan: Task 2 - Secrets Management

**Story**: 1.5 Configuration Management & Environment Setup  
**Task**: 2 - Secrets Management  
**Acceptance Criteria**: #2 - Secure handling of sensitive data (API keys, database credentials)

## Overview

Implement a comprehensive secrets management system that provides secure storage, encryption, rotation, and environment-specific loading for all sensitive configuration data.

## Implementation Steps

### Subtask 2.1: Implement Secure Storage for API Keys and Credentials

**Objective**: Create encrypted storage system using AES-256

**File**: `packages/shared/src/config/secrets.ts`

```typescript
import crypto from 'crypto';
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'fs';
import { join, dirname } from 'path';
import { homedir } from 'os';
import { logger } from '../observability/logger';

export interface SecretData {
  [key: string]: string | undefined;
}

export interface SecretMetadata {
  version: number;
  createdAt: string;
  updatedAt: string;
  algorithm: string;
  keyDerivation: {
    iterations: number;
    saltLength: number;
    keyLength: number;
  };
}

export interface EncryptedSecrets {
  metadata: SecretMetadata;
  data: string; // Base64 encrypted data
  iv: string; // Base64 initialization vector
  authTag: string; // Base64 authentication tag
}

export class SecretsManager {
  private static instance: SecretsManager;
  private masterKey: Buffer | null = null;
  private secretsPath: string;
  private algorithm = 'aes-256-gcm';

  private constructor() {
    this.secretsPath = this.getSecretsPath();
    this.ensureSecretsDirectory();
  }

  public static getInstance(): SecretsManager {
    if (!SecretsManager.instance) {
      SecretsManager.instance = new SecretsManager();
    }
    return SecretsManager.instance;
  }

  private getSecretsPath(): string {
    // Check for custom path
    if (process.env.TAMMA_SECRETS_PATH) {
      return process.env.TAMMA_SECRETS_PATH;
    }

    // Use OS-specific secure directory
    const os = process.platform;
    switch (os) {
      case 'win32':
        return join(process.env.APPDATA || '', 'Tamma', 'secrets');
      case 'darwin':
        return join(homedir(), 'Library', 'Application Support', 'Tamma', 'secrets');
      default: // Linux
        return join(homedir(), '.config', 'tamma', 'secrets');
    }
  }

  private ensureSecretsDirectory(): void {
    if (!existsSync(this.secretsPath)) {
      mkdirSync(this.secretsPath, { recursive: true, mode: 0o700 });
    }
  }

  private async deriveKey(password: string, salt: Buffer): Promise<Buffer> {
    return new Promise((resolve, reject) => {
      crypto.pbkdf2(password, salt, 100000, 32, 'sha512', (err, derivedKey) => {
        if (err) reject(err);
        else resolve(derivedKey);
      });
    });
  }

  private getMasterKey(): Buffer {
    if (this.masterKey) {
      return this.masterKey;
    }

    // Try to get master key from environment
    const envKey = process.env.TAMMA_MASTER_KEY;
    if (envKey) {
      this.masterKey = Buffer.from(envKey, 'base64');
      return this.masterKey;
    }

    // Try to get master key from file
    const keyPath = join(this.secretsPath, '.master_key');
    if (existsSync(keyPath)) {
      const keyData = readFileSync(keyPath, 'utf8');
      this.masterKey = Buffer.from(keyData, 'base64');
      return this.masterKey;
    }

    throw new Error(
      'Master key not found. Set TAMMA_MASTER_KEY environment variable or create .master_key file'
    );
  }

  private encryptData(
    data: string,
    key: Buffer
  ): { encrypted: string; iv: string; authTag: string } {
    const iv = crypto.randomBytes(16);
    const cipher = crypto.createCipher(this.algorithm, key);
    cipher.setAAD(Buffer.from('tamma-secrets'));

    let encrypted = cipher.update(data, 'utf8', 'hex');
    encrypted += cipher.final('hex');

    const authTag = cipher.getAuthTag();

    return {
      encrypted,
      iv: iv.toString('hex'),
      authTag: authTag.toString('hex'),
    };
  }

  private decryptData(encrypted: string, iv: string, authTag: string, key: Buffer): string {
    const decipher = crypto.createDecipher(this.algorithm, key);
    decipher.setAAD(Buffer.from('tamma-secrets'));
    decipher.setAuthTag(Buffer.from(authTag, 'hex'));

    let decrypted = decipher.update(encrypted, 'hex', 'utf8');
    decrypted += decipher.final('utf8');

    return decrypted;
  }

  public async storeSecrets(secrets: SecretData, password?: string): Promise<void> {
    try {
      const key = password
        ? await this.deriveKey(password, crypto.randomBytes(32))
        : this.getMasterKey();
      const secretsJson = JSON.stringify(secrets, null, 2);
      const { encrypted, iv, authTag } = this.encryptData(secretsJson, key);

      const encryptedSecrets: EncryptedSecrets = {
        metadata: {
          version: 1,
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          algorithm: this.algorithm,
          keyDerivation: {
            iterations: 100000,
            saltLength: 32,
            keyLength: 32,
          },
        },
        data: encrypted,
        iv,
        authTag,
      };

      const secretsFile = join(this.secretsPath, 'secrets.enc');
      writeFileSync(secretsFile, JSON.stringify(encryptedSecrets, null, 2), 'utf8');

      // Set secure permissions
      const fs = require('fs');
      fs.chmodSync(secretsFile, 0o600);

      logger.info('Secrets stored successfully', {
        secretsCount: Object.keys(secrets).length,
        algorithm: this.algorithm,
      });
    } catch (error) {
      logger.error('Failed to store secrets', { error });
      throw error;
    }
  }

  public async loadSecrets(password?: string): Promise<SecretData> {
    try {
      const secretsFile = join(this.secretsPath, 'secrets.enc');
      if (!existsSync(secretsFile)) {
        return {};
      }

      const encryptedSecrets: EncryptedSecrets = JSON.parse(readFileSync(secretsFile, 'utf8'));
      const key = password
        ? await this.deriveKey(password, crypto.randomBytes(32))
        : this.getMasterKey();

      const decryptedData = this.decryptData(
        encryptedSecrets.data,
        encryptedSecrets.iv,
        encryptedSecrets.authTag,
        key
      );

      const secrets: SecretData = JSON.parse(decryptedData);

      logger.info('Secrets loaded successfully', {
        secretsCount: Object.keys(secrets).length,
        version: encryptedSecrets.metadata.version,
        lastUpdated: encryptedSecrets.metadata.updatedAt,
      });

      return secrets;
    } catch (error) {
      logger.error('Failed to load secrets', { error });
      throw error;
    }
  }

  public async updateSecret(key: string, value: string, password?: string): Promise<void> {
    const secrets = await this.loadSecrets(password);
    secrets[key] = value;
    await this.storeSecrets(secrets, password);
  }

  public async deleteSecret(key: string, password?: string): Promise<void> {
    const secrets = await this.loadSecrets(password);
    delete secrets[key];
    await this.storeSecrets(secrets, password);
  }

  public async rotateSecrets(oldPassword?: string, newPassword?: string): Promise<void> {
    const secrets = await this.loadSecrets(oldPassword);
    await this.storeSecrets(secrets, newPassword);
  }

  public validateSecrets(secrets: SecretData): { valid: boolean; errors: string[] } {
    const errors: string[] = [];

    // Check for required secrets
    const requiredSecrets = ['database_password', 'jwt_access_secret', 'jwt_refresh_secret'];
    for (const secret of requiredSecrets) {
      if (!secrets[secret]) {
        errors.push(`Required secret '${secret}' is missing`);
      }
    }

    // Check secret strength
    if (secrets.jwt_access_secret && secrets.jwt_access_secret.length < 32) {
      errors.push('JWT access secret must be at least 32 characters');
    }

    if (secrets.jwt_refresh_secret && secrets.jwt_refresh_secret.length < 32) {
      errors.push('JWT refresh secret must be at least 32 characters');
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  public generateSecret(length: number = 64): string {
    return crypto.randomBytes(length).toString('base64');
  }

  public async initializeMasterKey(): Promise<string> {
    const masterKey = crypto.randomBytes(32);
    const masterKeyBase64 = masterKey.toString('base64');

    const keyPath = join(this.secretsPath, '.master_key');
    writeFileSync(keyPath, masterKeyBase64, 'utf8');

    // Set secure permissions
    const fs = require('fs');
    fs.chmodSync(keyPath, 0o600);

    logger.info('Master key initialized', { keyPath });
    return masterKeyBase64;
  }
}

// Export singleton instance
export const secretsManager = SecretsManager.getInstance();
```

### Subtask 2.2: Add Encryption for Sensitive Configuration Values

**Objective**: Implement encryption utilities for sensitive data

**File**: `packages/shared/src/config/encryption.ts`

```typescript
import crypto from 'crypto';
import { logger } from '../observability/logger';

export interface EncryptionResult {
  encrypted: string;
  iv: string;
  authTag: string;
  algorithm: string;
}

export interface DecryptionResult {
  decrypted: string;
  success: boolean;
  error?: string;
}

export class ConfigEncryption {
  private static algorithm = 'aes-256-gcm';
  private static keyLength = 32;

  public static encrypt(plaintext: string, key: string): EncryptionResult {
    try {
      const keyBuffer = Buffer.from(key, 'base64');
      const iv = crypto.randomBytes(16);

      const cipher = crypto.createCipher(this.algorithm, keyBuffer);
      cipher.setAAD(Buffer.from('tamma-config'));

      let encrypted = cipher.update(plaintext, 'utf8', 'hex');
      encrypted += cipher.final('hex');

      const authTag = cipher.getAuthTag();

      return {
        encrypted,
        iv: iv.toString('hex'),
        authTag: authTag.toString('hex'),
        algorithm: this.algorithm,
      };
    } catch (error) {
      logger.error('Encryption failed', { error });
      throw new Error(`Encryption failed: ${(error as Error).message}`);
    }
  }

  public static decrypt(
    encrypted: string,
    iv: string,
    authTag: string,
    key: string
  ): DecryptionResult {
    try {
      const keyBuffer = Buffer.from(key, 'base64');
      const decipher = crypto.createDecipher(this.algorithm, keyBuffer);
      decipher.setAAD(Buffer.from('tamma-config'));
      decipher.setAuthTag(Buffer.from(authTag, 'hex'));

      let decrypted = decipher.update(encrypted, 'hex', 'utf8');
      decrypted += decipher.final('utf8');

      return {
        decrypted,
        success: true,
      };
    } catch (error) {
      logger.error('Decryption failed', { error });
      return {
        decrypted: '',
        success: false,
        error: (error as Error).message,
      };
    }
  }

  public static generateEncryptionKey(): string {
    return crypto.randomBytes(this.keyLength).toString('base64');
  }

  public static hashPassword(password: string, salt?: string): { hash: string; salt: string } {
    const passwordSalt = salt || crypto.randomBytes(32).toString('hex');
    const hash = crypto.pbkdf2Sync(password, passwordSalt, 100000, 64, 'sha512');

    return {
      hash: hash.toString('hex'),
      salt: passwordSalt,
    };
  }

  public static verifyPassword(password: string, hash: string, salt: string): boolean {
    const { hash: computedHash } = this.hashPassword(password, salt);
    return crypto.timingSafeEqual(Buffer.from(hash, 'hex'), Buffer.from(computedHash, 'hex'));
  }

  public static encryptForEnvironment(
    data: Record<string, string>,
    environment: string
  ): Record<string, string> {
    const encryptionKey = process.env[`${environment.toUpperCase()}_ENCRYPTION_KEY`];
    if (!encryptionKey) {
      throw new Error(`Encryption key not found for environment: ${environment}`);
    }

    const encrypted: Record<string, string> = {};
    for (const [key, value] of Object.entries(data)) {
      const result = this.encrypt(value, encryptionKey);
      encrypted[key] = JSON.stringify(result);
    }

    return encrypted;
  }

  public static decryptForEnvironment(
    encryptedData: Record<string, string>,
    environment: string
  ): Record<string, string> {
    const encryptionKey = process.env[`${environment.toUpperCase()}_ENCRYPTION_KEY`];
    if (!encryptionKey) {
      throw new Error(`Encryption key not found for environment: ${environment}`);
    }

    const decrypted: Record<string, string> = {};
    for (const [key, encryptedValue] of Object.entries(encryptedData)) {
      try {
        const encrypted = JSON.parse(encryptedValue) as EncryptionResult;
        const result = this.decrypt(
          encrypted.encrypted,
          encrypted.iv,
          encrypted.authTag,
          encryptionKey
        );

        if (result.success) {
          decrypted[key] = result.decrypted;
        } else {
          logger.error(`Failed to decrypt ${key}`, { error: result.error });
          decrypted[key] = '';
        }
      } catch (error) {
        logger.error(`Failed to parse encrypted value for ${key}`, { error });
        decrypted[key] = '';
      }
    }

    return decrypted;
  }
}

// Utility functions for common encryption tasks
export function encryptDatabaseCredentials(config: any): any {
  const sensitiveFields = ['password', 'secret', 'key', 'token'];
  const encrypted = { ...config };

  for (const [key, value] of Object.entries(encrypted)) {
    if (
      typeof value === 'string' &&
      sensitiveFields.some((field) => key.toLowerCase().includes(field))
    ) {
      const encryptionKey = process.env.CONFIG_ENCRYPTION_KEY;
      if (encryptionKey) {
        const result = ConfigEncryption.encrypt(value, encryptionKey);
        encrypted[key] = `ENC:${JSON.stringify(result)}`;
      }
    }
  }

  return encrypted;
}

export function decryptDatabaseCredentials(config: any): any {
  const decrypted = { ...config };

  for (const [key, value] of Object.entries(decrypted)) {
    if (typeof value === 'string' && value.startsWith('ENC:')) {
      const encryptionKey = process.env.CONFIG_ENCRYPTION_KEY;
      if (encryptionKey) {
        try {
          const encrypted = JSON.parse(value.substring(4)) as EncryptionResult;
          const result = ConfigEncryption.decrypt(
            encrypted.encrypted,
            encrypted.iv,
            encrypted.authTag,
            encryptionKey
          );

          if (result.success) {
            decrypted[key] = result.decrypted;
          }
        } catch (error) {
          logger.error(`Failed to decrypt ${key}`, { error });
        }
      }
    }
  }

  return decrypted;
}
```

### Subtask 2.3: Create Secrets Rotation Mechanisms

**Objective**: Implement automatic secret rotation with versioning

**File**: `packages/shared/src/config/rotation.ts`

```typescript
import { secretsManager } from './secrets';
import { ConfigEncryption } from './encryption';
import { logger } from '../observability/logger';

export interface RotationPolicy {
  secretName: string;
  rotationInterval: number; // in days
  gracePeriod: number; // in days
  notifyBefore: number; // in days
  autoRotate: boolean;
}

export interface RotationHistory {
  id: string;
  secretName: string;
  rotatedAt: string;
  previousValueHash: string;
  newValueHash: string;
  rotatedBy: string;
  reason: string;
  success: boolean;
  error?: string;
}

export interface RotationSchedule {
  id: string;
  secretName: string;
  scheduledAt: string;
  status: 'pending' | 'in_progress' | 'completed' | 'failed';
  policy: RotationPolicy;
}

export class SecretsRotator {
  private static instance: SecretsRotator;
  private rotationHistory: RotationHistory[] = [];
  private rotationSchedule: RotationSchedule[] = [];

  private constructor() {
    this.loadRotationHistory();
    this.loadRotationSchedule();
  }

  public static getInstance(): SecretsRotator {
    if (!SecretsRotator.instance) {
      SecretsRotator.instance = new SecretsRotator();
    }
    return SecretsRotator.instance;
  }

  private loadRotationHistory(): void {
    // Load rotation history from storage
    // Implementation depends on storage backend
  }

  private loadRotationSchedule(): void {
    // Load rotation schedule from storage
    // Implementation depends on storage backend
  }

  public addRotationPolicy(policy: RotationPolicy): void {
    // Add rotation policy for a secret
    logger.info('Rotation policy added', { secretName: policy.secretName });
  }

  public async rotateSecret(secretName: string, reason?: string): Promise<boolean> {
    try {
      logger.info('Starting secret rotation', { secretName });

      const secrets = await secretsManager.loadSecrets();
      const currentValue = secrets[secretName];

      if (!currentValue) {
        throw new Error(`Secret '${secretName}' not found`);
      }

      // Generate new secret value
      const newValue = this.generateNewSecretValue(secretName);

      // Update secret
      await secretsManager.updateSecret(secretName, newValue);

      // Record rotation
      const history: RotationHistory = {
        id: this.generateId(),
        secretName,
        rotatedAt: new Date().toISOString(),
        previousValueHash: this.hashValue(currentValue),
        newValueHash: this.hashValue(newValue),
        rotatedBy: 'system',
        reason: reason || 'Scheduled rotation',
        success: true,
      };

      this.recordRotation(history);

      logger.info('Secret rotation completed', {
        secretName,
        rotatedAt: history.rotatedAt,
      });

      return true;
    } catch (error) {
      const history: RotationHistory = {
        id: this.generateId(),
        secretName,
        rotatedAt: new Date().toISOString(),
        previousValueHash: '',
        newValueHash: '',
        rotatedBy: 'system',
        reason: reason || 'Scheduled rotation',
        success: false,
        error: (error as Error).message,
      };

      this.recordRotation(history);

      logger.error('Secret rotation failed', {
        secretName,
        error: (error as Error).message,
      });

      return false;
    }
  }

  private generateNewSecretValue(secretName: string): string {
    // Generate appropriate secret based on type
    if (secretName.includes('jwt')) {
      return ConfigEncryption.generateEncryptionKey();
    } else if (secretName.includes('database')) {
      return this.generateDatabasePassword();
    } else if (secretName.includes('api') && secretName.includes('key')) {
      return this.generateApiKey();
    } else {
      return ConfigEncryption.generateEncryptionKey();
    }
  }

  private generateDatabasePassword(): string {
    const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*';
    let password = '';
    for (let i = 0; i < 32; i++) {
      password += chars.charAt(Math.floor(Math.random() * chars.length));
    }
    return password;
  }

  private generateApiKey(): string {
    const prefix = 'tk_';
    const randomPart = crypto.randomBytes(32).toString('hex');
    return prefix + randomPart;
  }

  private hashValue(value: string): string {
    return crypto.createHash('sha256').update(value).digest('hex');
  }

  private generateId(): string {
    return crypto.randomUUID();
  }

  private recordRotation(history: RotationHistory): void {
    this.rotationHistory.push(history);
    // Persist to storage
  }

  public async checkAndRotateScheduledSecrets(): Promise<void> {
    const now = new Date();

    for (const schedule of this.rotationSchedule) {
      if (schedule.status === 'pending' && new Date(schedule.scheduledAt) <= now) {
        schedule.status = 'in_progress';

        const success = await this.rotateSecret(schedule.secretName, 'Scheduled rotation');

        schedule.status = success ? 'completed' : 'failed';
      }
    }
  }

  public getRotationHistory(secretName?: string): RotationHistory[] {
    if (secretName) {
      return this.rotationHistory.filter((h) => h.secretName === secretName);
    }
    return this.rotationHistory;
  }

  public getUpcomingRotations(days: number = 30): RotationSchedule[] {
    const cutoff = new Date();
    cutoff.setDate(cutoff.getDate() + days);

    return this.rotationSchedule.filter(
      (schedule) => schedule.status === 'pending' && new Date(schedule.scheduledAt) <= cutoff
    );
  }

  public async rollbackRotation(secretName: string, rotationId: string): Promise<boolean> {
    try {
      const rotation = this.rotationHistory.find((h) => h.id === rotationId);
      if (!rotation) {
        throw new Error(`Rotation with ID ${rotationId} not found`);
      }

      if (rotation.secretName !== secretName) {
        throw new Error('Rotation ID does not match secret name');
      }

      // This would require storing the previous value securely
      // For now, just log the rollback attempt
      logger.warn('Secret rotation rollback requested', {
        secretName,
        rotationId,
        rotatedAt: rotation.rotatedAt,
      });

      return true;
    } catch (error) {
      logger.error('Failed to rollback rotation', {
        secretName,
        rotationId,
        error: (error as Error).message,
      });
      return false;
    }
  }

  public scheduleRotation(secretName: string, scheduledAt: Date, policy: RotationPolicy): void {
    const schedule: RotationSchedule = {
      id: this.generateId(),
      secretName,
      scheduledAt: scheduledAt.toISOString(),
      status: 'pending',
      policy,
    };

    this.rotationSchedule.push(schedule);
    logger.info('Secret rotation scheduled', {
      secretName,
      scheduledAt: schedule.scheduledAt,
    });
  }

  public getRotationStatus(secretName: string): {
    lastRotated?: string;
    nextRotation?: string;
    needsRotation: boolean;
    daysUntilRotation?: number;
  } {
    const history = this.getRotationHistory(secretName);
    const lastRotation = history.filter((h) => h.success).pop();

    if (!lastRotation) {
      return {
        needsRotation: true,
      };
    }

    const lastRotated = new Date(lastRotation.rotatedAt);
    const now = new Date();
    const daysSinceRotation = Math.floor(
      (now.getTime() - lastRotated.getTime()) / (1000 * 60 * 60 * 24)
    );

    // Default rotation interval of 90 days if no policy is set
    const rotationInterval = 90;
    const needsRotation = daysSinceRotation >= rotationInterval;
    const daysUntilRotation = Math.max(0, rotationInterval - daysSinceRotation);

    const nextRotation = new Date(lastRotated);
    nextRotation.setDate(nextRotation.getDate() + rotationInterval);

    return {
      lastRotated: lastRotation.rotatedAt,
      nextRotation: nextRotation.toISOString(),
      needsRotation,
      daysUntilRotation,
    };
  }
}

// Export singleton instance
export const secretsRotator = SecretsRotator.getInstance();

// CLI commands for secret rotation
export async function rotateSecretCommand(secretName: string): Promise<void> {
  const success = await secretsRotator.rotateSecret(secretName);
  if (success) {
    console.log(`✅ Secret '${secretName}' rotated successfully`);
    process.exit(0);
  } else {
    console.error(`❌ Failed to rotate secret '${secretName}'`);
    process.exit(1);
  }
}

export async function listRotationsCommand(secretName?: string): Promise<void> {
  const history = secretsRotator.getRotationHistory(secretName);

  console.log('\n=== Secret Rotation History ===');
  for (const rotation of history) {
    const status = rotation.success ? '✅' : '❌';
    console.log(`${status} ${rotation.secretName}`);
    console.log(`   ID: ${rotation.id}`);
    console.log(`   Rotated: ${rotation.rotatedAt}`);
    console.log(`   By: ${rotation.rotatedBy}`);
    console.log(`   Reason: ${rotation.reason}`);
    if (rotation.error) {
      console.log(`   Error: ${rotation.error}`);
    }
    console.log('');
  }
}

export async function checkRotationsCommand(): Promise<void> {
  const upcoming = secretsRotator.getUpcomingRotations(30);

  console.log('\n=== Upcoming Rotations (Next 30 Days) ===');
  if (upcoming.length === 0) {
    console.log('No upcoming rotations scheduled');
  } else {
    for (const schedule of upcoming) {
      console.log(`📅 ${schedule.secretName}`);
      console.log(`   Scheduled: ${schedule.scheduledAt}`);
      console.log(`   Status: ${schedule.status}`);
      console.log('');
    }
  }
}
```

### Subtask 2.4: Add Environment-Specific Secret Loading

**Objective**: Implement environment-specific secret loading with cloud provider support

**File**: `packages/shared/src/config/env-secrets.ts`

```typescript
import { secretsManager, SecretData } from './secrets';
import { logger } from '../observability/logger';

export interface CloudSecretProvider {
  name: string;
  loadSecrets(): Promise<SecretData>;
  storeSecret(key: string, value: string): Promise<void>;
  deleteSecret(key: string): Promise<void>;
}

export class AWSSecretsManager implements CloudSecretProvider {
  name = 'aws-secrets-manager';
  private region: string;
  private client: any;

  constructor(region: string = process.env.AWS_REGION || 'us-east-1') {
    this.region = region;
    // Initialize AWS Secrets Manager client
    // This would require @aws-sdk/client-secrets-manager
  }

  async loadSecrets(): Promise<SecretData> {
    try {
      const secrets: SecretData = {};
      const secretName = process.env.AWS_SECRET_NAME || 'tamma/secrets';

      // Load secret from AWS Secrets Manager
      // const response = await this.client.getSecretValue({ SecretId: secretName });
      // const secretData = JSON.parse(response.SecretString);

      // For now, return empty object
      logger.info('AWS Secrets Manager loaded', { secretName });
      return secrets;
    } catch (error) {
      logger.error('Failed to load secrets from AWS Secrets Manager', { error });
      return {};
    }
  }

  async storeSecret(key: string, value: string): Promise<void> {
    // Implementation for storing secrets in AWS Secrets Manager
    logger.info('Storing secret in AWS Secrets Manager', { key });
  }

  async deleteSecret(key: string): Promise<void> {
    // Implementation for deleting secrets from AWS Secrets Manager
    logger.info('Deleting secret from AWS Secrets Manager', { key });
  }
}

export class AzureKeyVault implements CloudSecretProvider {
  name = 'azure-key-vault';
  private vaultName: string;
  private client: any;

  constructor(vaultName: string = process.env.AZURE_KEY_VAULT_NAME || '') {
    this.vaultName = vaultName;
    // Initialize Azure Key Vault client
    // This would require @azure/keyvault-secrets
  }

  async loadSecrets(): Promise<SecretData> {
    try {
      const secrets: SecretData = {};

      // Load secrets from Azure Key Vault
      // Implementation depends on Azure SDK

      logger.info('Azure Key Vault loaded', { vaultName: this.vaultName });
      return secrets;
    } catch (error) {
      logger.error('Failed to load secrets from Azure Key Vault', { error });
      return {};
    }
  }

  async storeSecret(key: string, value: string): Promise<void> {
    // Implementation for storing secrets in Azure Key Vault
    logger.info('Storing secret in Azure Key Vault', { key });
  }

  async deleteSecret(key: string): Promise<void> {
    // Implementation for deleting secrets in Azure Key Vault
    logger.info('Deleting secret from Azure Key Vault', { key });
  }
}

export class GoogleSecretManager implements CloudSecretProvider {
  name = 'google-secret-manager';
  private projectId: string;
  private client: any;

  constructor(projectId: string = process.env.GOOGLE_PROJECT_ID || '') {
    this.projectId = projectId;
    // Initialize Google Secret Manager client
    // This would require @google-cloud/secret-manager
  }

  async loadSecrets(): Promise<SecretData> {
    try {
      const secrets: SecretData = {};

      // Load secrets from Google Secret Manager
      // Implementation depends on Google Cloud SDK

      logger.info('Google Secret Manager loaded', { projectId: this.projectId });
      return secrets;
    } catch (error) {
      logger.error('Failed to load secrets from Google Secret Manager', { error });
      return {};
    }
  }

  async storeSecret(key: string, value: string): Promise<void> {
    // Implementation for storing secrets in Google Secret Manager
    logger.info('Storing secret in Google Secret Manager', { key });
  }

  async deleteSecret(key: string): Promise<void> {
    // Implementation for deleting secrets in Google Secret Manager
    logger.info('Deleting secret from Google Secret Manager', { key });
  }
}

export class EnvironmentSecretLoader {
  private static instance: EnvironmentSecretLoader;
  private providers: Map<string, CloudSecretProvider> = new Map();

  private constructor() {
    this.initializeProviders();
  }

  public static getInstance(): EnvironmentSecretLoader {
    if (!EnvironmentSecretLoader.instance) {
      EnvironmentSecretLoader.instance = new EnvironmentSecretLoader();
    }
    return EnvironmentSecretLoader.instance;
  }

  private initializeProviders(): void {
    // Initialize cloud providers based on environment
    if (process.env.AWS_SECRET_NAME) {
      this.providers.set('aws', new AWSSecretsManager());
    }

    if (process.env.AZURE_KEY_VAULT_NAME) {
      this.providers.set('azure', new AzureKeyVault());
    }

    if (process.env.GOOGLE_PROJECT_ID) {
      this.providers.set('google', new GoogleSecretManager());
    }
  }

  public async loadEnvironmentSecrets(environment: string): Promise<SecretData> {
    const secrets: SecretData = {};

    // Load from local encrypted storage first
    try {
      const localSecrets = await secretsManager.loadSecrets();
      Object.assign(secrets, localSecrets);
    } catch (error) {
      logger.warn('Failed to load local secrets', { error });
    }

    // Load from cloud providers
    for (const [providerName, provider] of this.providers) {
      try {
        const cloudSecrets = await provider.loadSecrets();
        Object.assign(secrets, cloudSecrets);
        logger.info(`Loaded secrets from ${providerName}`, {
          provider: provider.name,
          count: Object.keys(cloudSecrets).length,
        });
      } catch (error) {
        logger.error(`Failed to load secrets from ${providerName}`, { error });
      }
    }

    // Load from environment variables (highest precedence)
    const envSecrets = this.loadSecretsFromEnvironment();
    Object.assign(secrets, envSecrets);

    return secrets;
  }

  private loadSecretsFromEnvironment(): SecretData {
    const secrets: SecretData = {};

    // Common secret environment variables
    const secretMappings = {
      DATABASE_PASSWORD: 'database_password',
      REDIS_PASSWORD: 'redis_password',
      JWT_ACCESS_SECRET: 'jwt_access_secret',
      JWT_REFRESH_SECRET: 'jwt_refresh_secret',
      WEBHOOK_SECRET: 'webhook_secret',
      EMAIL_PASSWORD: 'email_password',
      SMTP_PASSWORD: 'smtp_password',
      SENDGRID_API_KEY: 'sendgrid_api_key',
      AWS_ACCESS_KEY_ID: 'aws_access_key_id',
      AWS_SECRET_ACCESS_KEY: 'aws_secret_access_key',
      AZURE_CLIENT_SECRET: 'azure_client_secret',
      GOOGLE_PRIVATE_KEY: 'google_private_key',
    };

    for (const [envVar, secretKey] of Object.entries(secretMappings)) {
      const value = process.env[envVar];
      if (value) {
        secrets[secretKey] = value;
      }
    }

    return secrets;
  }

  public async storeSecret(key: string, value: string, provider?: string): Promise<void> {
    // Store in local encrypted storage
    await secretsManager.updateSecret(key, value);

    // Store in cloud provider if specified
    if (provider && this.providers.has(provider)) {
      const cloudProvider = this.providers.get(provider)!;
      await cloudProvider.storeSecret(key, value);
    }
  }

  public async deleteSecret(key: string, provider?: string): Promise<void> {
    // Delete from local encrypted storage
    await secretsManager.deleteSecret(key);

    // Delete from cloud provider if specified
    if (provider && this.providers.has(provider)) {
      const cloudProvider = this.providers.get(provider)!;
      await cloudProvider.deleteSecret(key);
    }
  }

  public getAvailableProviders(): string[] {
    return Array.from(this.providers.keys());
  }

  public async validateSecrets(secrets: SecretData): Promise<{ valid: boolean; errors: string[] }> {
    const errors: string[] = [];

    // Check for required secrets based on environment
    const requiredSecrets = this.getRequiredSecrets();
    for (const secret of requiredSecrets) {
      if (!secrets[secret]) {
        errors.push(`Required secret '${secret}' is missing`);
      }
    }

    // Validate secret formats
    if (secrets.jwt_access_secret && secrets.jwt_access_secret.length < 32) {
      errors.push('JWT access secret must be at least 32 characters');
    }

    if (secrets.jwt_refresh_secret && secrets.jwt_refresh_secret.length < 32) {
      errors.push('JWT refresh secret must be at least 32 characters');
    }

    return {
      valid: errors.length === 0,
      errors,
    };
  }

  private getRequiredSecrets(): string[] {
    const environment = process.env.NODE_ENV || 'development';

    switch (environment) {
      case 'production':
        return ['database_password', 'jwt_access_secret', 'jwt_refresh_secret', 'webhook_secret'];
      case 'staging':
        return ['database_password', 'jwt_access_secret', 'jwt_refresh_secret', 'webhook_secret'];
      default:
        return ['database_password', 'jwt_access_secret', 'jwt_refresh_secret'];
    }
  }
}

// Export singleton instance
export const envSecretLoader = EnvironmentSecretLoader.getInstance();

// Utility function to load secrets for current environment
export async function loadEnvironmentSecrets(): Promise<SecretData> {
  const environment = process.env.NODE_ENV || 'development';
  return await envSecretLoader.loadEnvironmentSecrets(environment);
}
```

## Files to Create

1. `packages/shared/src/config/secrets.ts` - Core secrets management
2. `packages/shared/src/config/encryption.ts` - Encryption utilities
3. `packages/shared/src/config/rotation.ts` - Secret rotation system
4. `packages/shared/src/config/env-secrets.ts` - Environment-specific loading
5. Update `packages/shared/src/config/manager.ts` to integrate secrets

## Dependencies

- Node.js crypto module
- AWS SDK (optional): @aws-sdk/client-secrets-manager
- Azure SDK (optional): @azure/keyvault-secrets
- Google Cloud SDK (optional): @google-cloud/secret-manager

## Testing

1. Unit tests for encryption/decryption
2. Integration tests for secret storage
3. Rotation mechanism tests
4. Cloud provider integration tests
5. Security tests for secret handling

## Notes

- Always use secure storage for secrets
- Implement proper key rotation policies
- Monitor secret access and rotation
- Use different secrets for different environments
- Never commit secrets to version control
