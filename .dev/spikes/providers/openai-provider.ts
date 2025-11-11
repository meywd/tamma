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
  defaultModels = ['gpt-4o', 'gpt-4o-mini', 'gpt-4-turbo'];

  getApiKeyEnvVar(): string {
    return 'OPENAI_API_KEY';
  }

  async getModels(includePaid: boolean = false): Promise<string[]> {
    try {
      const apiKey = this.getApiKey();
      const response = await fetch('https://api.openai.com/v1/models', {
        headers: { 'Authorization': `Bearer ${apiKey}` },
        signal: AbortSignal.timeout(5000)
      });

      if (!response.ok) {
        console.warn(`OpenAI: Failed to fetch models, using defaults`);
        return this.defaultModels;
      }

      const data = await response.json();

      // Filter for GPT models suitable for chat/completion
      // Note: includePaid parameter not currently used - all OpenAI models are paid (have $5 trial credits)
      const models = data.data
        ?.filter((m: any) => m.id.startsWith('gpt-') && !m.id.includes('instruct'))
        .map((m: any) => m.id)
        .sort((a: string, b: string) => b.localeCompare(a)) || []; // Newest first

      return models.length > 0 ? models.slice(0, 10) : this.defaultModels; // Limit to 10 most recent
    } catch (error) {
      console.warn(`OpenAI: Error fetching models - ${error instanceof Error ? error.message : 'Unknown'}`);
      return this.defaultModels;
    }
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
