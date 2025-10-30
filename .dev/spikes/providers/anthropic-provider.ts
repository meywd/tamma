/**
 * Anthropic Claude Provider
 *
 * Supports: Claude 3.5 Sonnet, Claude 3 Haiku, Claude 3 Opus
 * Free Tier: Limited console credits
 */

import { BaseProvider, type ProviderConfig, type TestResult } from './base-provider.js';
import { scoreResponse } from '../scorers/index.js';

export class AnthropicProvider extends BaseProvider {
  name = 'Anthropic Claude';
  models = ['claude-3-5-sonnet-20241022', 'claude-3-haiku-20240307', 'claude-3-opus-20240229'];

  getApiKeyEnvVar(): string {
    return 'ANTHROPIC_API_KEY';
  }

  async test(scenario: string, prompt: string, config?: ProviderConfig): Promise<TestResult> {
    const apiKey = this.getApiKey(config);
    const model = config?.model || 'claude-3-haiku-20240307';
    const startTime = Date.now();

    const response = await fetch('https://api.anthropic.com/v1/messages', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'x-api-key': apiKey,
        'anthropic-version': '2023-06-01'
      },
      body: JSON.stringify({
        model,
        max_tokens: 2048,
        messages: [{ role: 'user', content: prompt }]
      })
    });

    if (!response.ok) {
      const errorText = await response.text();
      throw new Error(`Anthropic API error: ${response.status} ${errorText}`);
    }

    const data = await response.json();
    const responseTimeMs = Date.now() - startTime;

    const text = data.content?.[0]?.text || '';
    const inputTokens = data.usage?.input_tokens || 0;
    const outputTokens = data.usage?.output_tokens || 0;

    // Pricing:
    // Sonnet 3.5: $3.00/MTok input, $15.00/MTok output
    // Haiku: $0.25/MTok input, $1.25/MTok output
    // Opus: $15.00/MTok input, $75.00/MTok output
    let inputRate = 0.25, outputRate = 1.25; // Haiku default
    if (model.includes('sonnet')) {
      inputRate = 3.00;
      outputRate = 15.00;
    } else if (model.includes('opus')) {
      inputRate = 15.00;
      outputRate = 75.00;
    }

    const estimatedCost = this.calculateCost(inputTokens, outputTokens, inputRate, outputRate);
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
