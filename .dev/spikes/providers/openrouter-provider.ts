/**
 * OpenRouter Provider
 *
 * Aggregator service providing access to multiple models including free options
 * Free Models: mistralai/mistral-7b-instruct, meta-llama/llama-2-70b-chat, etc.
 */

import { BaseProvider, type ProviderConfig, type TestResult } from './base-provider.js';
import { scoreResponse } from '../scorers/index.js';

export class OpenRouterProvider extends BaseProvider {
  name = 'OpenRouter';
  defaultModels = [
    'mistralai/mistral-7b-instruct:free',
    'meta-llama/llama-2-70b-chat:free',
    'google/gemma-7b-it:free',
    'mistralai/mixtral-8x7b-instruct:free',
  ];

  getApiKeyEnvVar(): string {
    return 'OPENROUTER_API_KEY';
  }

  async getModels(includePaid: boolean = false): Promise<string[]> {
    try {
      const response = await fetch('https://openrouter.ai/api/v1/models', {
        signal: AbortSignal.timeout(5000)
      });

      if (!response.ok) {
        console.warn(`OpenRouter: Failed to fetch models, using defaults`);
        return this.defaultModels;
      }

      const data = await response.json();

      if (!includePaid) {
        // Filter for free models only (default behavior)
        const freeModels = data.data
          ?.filter((m: any) => m.id.includes(':free'))
          .map((m: any) => m.id) || [];

        return freeModels.length > 0 ? freeModels : this.defaultModels;
      } else {
        // Include all models, but limit to top 20 to avoid hundreds
        const allModels = data.data
          ?.map((m: any) => m.id)
          .slice(0, 20) || [];

        return allModels.length > 0 ? allModels : this.defaultModels;
      }
    } catch (error) {
      console.warn(`OpenRouter: Error fetching models - ${error instanceof Error ? error.message : 'Unknown'}`);
      return this.defaultModels;
    }
  }

  async test(scenario: string, prompt: string, config?: ProviderConfig): Promise<TestResult> {
    const apiKey = this.getApiKey(config);
    const model = config?.model || 'mistralai/mistral-7b-instruct:free';
    const startTime = Date.now();

    const response = await fetch('https://openrouter.ai/api/v1/chat/completions', {
      method: 'POST',
      headers: {
        'Authorization': `Bearer ${apiKey}`,
        'Content-Type': 'application/json',
        'HTTP-Referer': 'https://github.com/tamma-dev/tamma',
        'X-Title': 'Tamma AI Provider Testing'
      },
      body: JSON.stringify({
        model,
        messages: [{ role: 'user', content: prompt }],
        temperature: 0.7,
        max_tokens: 2048
      })
    });

    if (!response.ok) {
      const errorText = await response.text();
      throw new Error(`OpenRouter API error: ${response.status} ${errorText}`);
    }

    const data = await response.json();
    const responseTimeMs = Date.now() - startTime;

    const text = data.choices?.[0]?.message?.content || '';
    const inputTokens = data.usage?.prompt_tokens || 0;
    const outputTokens = data.usage?.completion_tokens || 0;

    // Free models have $0 cost
    const estimatedCost = model.includes(':free') ? 0 : this.calculateCost(inputTokens, outputTokens, 0, 0);
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
