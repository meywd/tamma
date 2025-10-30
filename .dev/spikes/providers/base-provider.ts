/**
 * Base Provider Interface
 *
 * All AI providers must implement this interface for consistent testing
 */

export interface TestResult {
  provider: string;
  model: string;
  scenario: string;
  timestamp: string;
  metrics: {
    inputTokens: number;
    outputTokens: number;
    totalTokens: number;
    responseTimeMs: number;
    estimatedCost: number;
  };
  score: {
    total: number;
    breakdown: Record<string, number>;
    details: string[];
  };
  response: string;
  rawResponse?: unknown;
}

export interface ProviderConfig {
  apiKey?: string;
  baseUrl?: string;
  model?: string;
  timeout?: number;
}

export interface AIProvider {
  name: string;
  models: string[];
  isAvailable(): Promise<boolean>;
  test(scenario: string, prompt: string, config?: ProviderConfig): Promise<TestResult>;
}

export abstract class BaseProvider implements AIProvider {
  abstract name: string;
  abstract models: string[];

  async isAvailable(): Promise<boolean> {
    // Default implementation - check for API key in env
    const envVar = this.getApiKeyEnvVar();
    return Boolean(process.env[envVar]);
  }

  abstract getApiKeyEnvVar(): string;
  abstract test(scenario: string, prompt: string, config?: ProviderConfig): Promise<TestResult>;

  protected getApiKey(config?: ProviderConfig): string {
    const apiKey = config?.apiKey || process.env[this.getApiKeyEnvVar()];
    if (!apiKey) {
      throw new Error(`${this.name}: API key not found. Set ${this.getApiKeyEnvVar()} environment variable.`);
    }
    return apiKey;
  }

  protected calculateCost(inputTokens: number, outputTokens: number, inputRate: number, outputRate: number): number {
    return (inputTokens / 1_000_000 * inputRate) + (outputTokens / 1_000_000 * outputRate);
  }
}
