/**
 * Provider Registry
 *
 * Central registry for all AI providers
 */

export { BaseProvider, type AIProvider, type ProviderConfig, type TestResult } from './base-provider.js';
export { GeminiProvider } from './gemini-provider.js';
export { OpenAIProvider } from './openai-provider.js';
export { AnthropicProvider } from './anthropic-provider.js';
export { OpenRouterProvider } from './openrouter-provider.js';
export { OllamaProvider } from './ollama-provider.js';

import type { AIProvider } from './base-provider.js';
import { GeminiProvider } from './gemini-provider.js';
import { OpenAIProvider } from './openai-provider.js';
import { AnthropicProvider } from './anthropic-provider.js';
import { OpenRouterProvider } from './openrouter-provider.js';
import { OllamaProvider } from './ollama-provider.js';

/**
 * Get all available providers
 */
export function getAllProviders(): AIProvider[] {
  return [
    new GeminiProvider(),
    new OpenAIProvider(),
    new AnthropicProvider(),
    new OpenRouterProvider(),
    new OllamaProvider()
  ];
}

/**
 * Get only providers that are currently available (have API keys or are running)
 */
export async function getAvailableProviders(): Promise<AIProvider[]> {
  const all = getAllProviders();
  const results = await Promise.all(
    all.map(async provider => ({
      provider,
      available: await provider.isAvailable()
    }))
  );

  return results
    .filter(r => r.available)
    .map(r => r.provider);
}

/**
 * Get a specific provider by name
 */
export function getProvider(name: string): AIProvider | undefined {
  const providers = getAllProviders();
  return providers.find(p =>
    p.name.toLowerCase() === name.toLowerCase() ||
    p.name.toLowerCase().includes(name.toLowerCase())
  );
}
