/**
 * OpenAI Provider
 *
 * Supports: GPT-4o, GPT-4o-mini, GPT-4-turbo
 * Free Tier: $5 trial credits
 */

import { BaseProvider, type ProviderConfig, type TestResult } from './base-provider.js';
import { scoreResponse } from '../scorers/index.js';

export class OpenAIProvider extends BaseProvider {
  name = 'OpenAI';
  models = ['gpt-4o', 'gpt-4o-mini', 'gpt-4-turbo'];

  getApiKeyEnvVar(): string {
    return 'OPENAI_API_KEY';
  }

  async test(scenario: string, prompt: string, config?: ProviderConfig): Promise<TestResult> {
    const apiKey = this.getApiKey(config);
    const model = config?.model || 'gpt-4o-mini';
    const startTime = Date.now();

    const response = await fetch('https://api.openai.com/v1/chat/completions', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${apiKey}`
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
      throw new Error(`OpenAI API error: ${response.status} ${errorText}`);
    }

    const data = await response.json();
    const responseTimeMs = Date.now() - startTime;

    const text = data.choices?.[0]?.message?.content || '';
    const inputTokens = data.usage?.prompt_tokens || 0;
    const outputTokens = data.usage?.completion_tokens || 0;

    // Pricing:
    // GPT-4o: $5.00/MTok input, $15.00/MTok output
    // GPT-4o-mini: $0.15/MTok input, $0.60/MTok output
    const isMini = model.includes('mini');
    const estimatedCost = this.calculateCost(
      inputTokens,
      outputTokens,
      isMini ? 0.15 : 5.00,
      isMini ? 0.60 : 15.00
    );

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
