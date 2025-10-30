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
  models = [
    'mistralai/mistral-7b-instruct:free',
    'meta-llama/llama-2-70b-chat:free',
    'google/gemma-7b-it:free',
    'mistralai/mixtral-8x7b-instruct:free',
  ];

  getApiKeyEnvVar(): string {
    return 'OPENROUTER_API_KEY';
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
