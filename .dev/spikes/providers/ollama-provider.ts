/**
 * Ollama Provider (Local Models)
 *
 * Requires Ollama installed locally: https://ollama.ai/download
 * Models: codellama:7b, mistral:7b, deepseek-coder:6.7b, qwen2.5-coder:7b
 * Cost: $0 (local execution)
 */

import { BaseProvider, type ProviderConfig, type TestResult } from './base-provider.js';
import { scoreResponse } from '../scorers/index.js';

export class OllamaProvider extends BaseProvider {
  name = 'Ollama (Local)';
  defaultModels = [
    'codellama:7b',
    'mistral:7b',
    'deepseek-coder:6.7b',
    'qwen2.5-coder:7b'
  ];

  getApiKeyEnvVar(): string {
    // Ollama doesn't require API key (local)
    return 'OLLAMA_ENABLED'; // Can set to 'true' to enable
  }

  async getModels(): Promise<string[]> {
    try {
      const baseUrl = 'http://localhost:11434';
      const response = await fetch(`${baseUrl}/api/tags`, {
        signal: AbortSignal.timeout(2000)
      });

      if (!response.ok) {
        console.warn(`Ollama: Failed to fetch models, using defaults`);
        return this.defaultModels;
      }

      const data = await response.json();

      // Get all locally installed models
      const installedModels = data.models?.map((m: any) => m.name) || [];

      return installedModels.length > 0 ? installedModels : this.defaultModels;
    } catch (error) {
      console.warn(`Ollama: Not running or error fetching models - ${error instanceof Error ? error.message : 'Unknown'}`);
      return this.defaultModels;
    }
  }

  async isAvailable(): Promise<boolean> {
    try {
      // Check if Ollama is running locally
      const response = await fetch('http://localhost:11434/api/tags', {
        signal: AbortSignal.timeout(2000)
      });
      return response.ok;
    } catch {
      return false;
    }
  }

  async test(scenario: string, prompt: string, config?: ProviderConfig): Promise<TestResult> {
    const model = config?.model || 'codellama:7b';
    const startTime = Date.now();

    const baseUrl = config?.baseUrl || 'http://localhost:11434';

    const response = await fetch(`${baseUrl}/api/generate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        model,
        prompt,
        stream: false,
        options: {
          temperature: 0.7,
          num_predict: 2048
        }
      })
    });

    if (!response.ok) {
      const errorText = await response.text();
      throw new Error(`Ollama API error: ${response.status} ${errorText}`);
    }

    const data = await response.json();
    const responseTimeMs = Date.now() - startTime;

    const text = data.response || '';
    const inputTokens = data.prompt_eval_count || 0;
    const outputTokens = data.eval_count || 0;

    // Local models have $0 cost (only electricity, not tracked)
    const estimatedCost = 0;

    const score = scoreResponse(scenario as any, text);

    return {
      provider: this.name,
      model,
      scenario,
      timestamp: new Date().toISOString(),
      metrics: {
        inputTokens,
        outputTokens,
        totalTokens: inputTokens + outputTokens,
        responseTimeMs,
        estimatedCost
      },
      score,
      response: text,
      rawResponse: data
    };
  }
}
