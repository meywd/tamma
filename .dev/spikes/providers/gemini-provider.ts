/**
 * Google Gemini Provider
 *
 * Supports: Gemini 2.5 Flash, Gemini 2.5 Pro
 * Free Tier: 60 requests/min, 1500 requests/day
 */

import { BaseProvider, type ProviderConfig, type TestResult } from './base-provider.js';
import { scoreResponse } from '../scorers/index.js';

export class GeminiProvider extends BaseProvider {
  name = 'Google Gemini';
  defaultModels = ['gemini-1.5-flash', 'gemini-1.5-pro'];

  getApiKeyEnvVar(): string {
    return 'GOOGLE_AI_API_KEY';
  }

  async getModels(): Promise<string[]> {
    try {
      const apiKey = this.getApiKey();
      const response = await fetch(
        `https://generativelanguage.googleapis.com/v1beta/models?key=${apiKey}`,
        { signal: AbortSignal.timeout(5000) }
      );

      if (!response.ok) {
        console.warn(`Gemini: Failed to fetch models, using defaults`);
        return this.defaultModels;
      }

      const data = await response.json();

      // Filter for generative models only (exclude embedding models)
      const models = data.models
        ?.filter((m: any) => m.name?.includes('gemini') && m.supportedGenerationMethods?.includes('generateContent'))
        .map((m: any) => m.name.replace('models/', '')) || [];

      return models.length > 0 ? models : this.defaultModels;
    } catch (error) {
      console.warn(`Gemini: Error fetching models - ${error instanceof Error ? error.message : 'Unknown'}`);
      return this.defaultModels;
    }
  }

  async test(scenario: string, prompt: string, config?: ProviderConfig): Promise<TestResult> {
    const apiKey = this.getApiKey(config);
    const model = config?.model || 'gemini-1.5-flash';
    const startTime = Date.now();

    const url = `https://generativelanguage.googleapis.com/v1beta/models/${model}:generateContent?key=${apiKey}`;

    const response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        contents: [{ parts: [{ text: prompt }] }],
        generationConfig: {
          temperature: 0.7,
          maxOutputTokens: 2048
        }
      })
    });

    if (!response.ok) {
      const errorText = await response.text();
      throw new Error(`Gemini API error: ${response.status} ${errorText}`);
    }

    const data = await response.json();
    const responseTimeMs = Date.now() - startTime;

    const text = data.candidates?.[0]?.content?.parts?.[0]?.text || '';
    const inputTokens = data.usageMetadata?.promptTokenCount || 0;
    const outputTokens = data.usageMetadata?.candidatesTokenCount || 0;

    // Pricing: Flash $0.075/MTok input, $0.30/MTok output
    //          Pro $1.25/MTok input, $5.00/MTok output
    const isFlash = model.includes('flash');
    const estimatedCost = this.calculateCost(
      inputTokens,
      outputTokens,
      isFlash ? 0.075 : 1.25,
      isFlash ? 0.30 : 5.00
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
