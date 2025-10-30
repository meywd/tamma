/**
 * Provider Factory Implementation
 *
 * This module implements provider factory for creating AI provider instances.
 * It supports built-in provider types and allows registration of custom providers.
 */

import type { IAIProvider, IProviderFactory, ProviderConfig } from './types.js';
import { PROVIDER_TYPES } from './types.js';

/**
 * Provider creator function type
 */
type ProviderCreator = (config: ProviderConfig) => Promise<IAIProvider>;

/**
 * Provider factory implementation
 */
export class ProviderFactory implements IProviderFactory {
  private creators = new Map<string, ProviderCreator>();

  constructor() {
    this.registerBuiltinCreators();
  }

  /**
   * Create a provider instance
   * @param type Provider type
   * @param config Provider configuration
   * @returns Provider instance
   */
  async createProvider(type: string, config: ProviderConfig): Promise<IAIProvider> {
    if (!type || type.trim() === '') {
      throw new Error('Provider type cannot be empty');
    }

    if (!config) {
      throw new Error('Provider config cannot be null or undefined');
    }

    if (!config.apiKey) {
      throw new Error('API key is required');
    }

    const creator = this.creators.get(type);
    if (!creator) {
      throw new Error(`Unsupported provider type: ${type}`);
    }

    return creator(config);
  }

  /**
   * Get supported provider types
   * @returns Array of supported provider types
   */
  getSupportedTypes(): string[] {
    return Array.from(this.creators.keys());
  }

  /**
   * Register a custom provider creator
   * @param type Provider type
   * @param creator Provider creator function
   */
  registerProviderCreator(type: string, creator: ProviderCreator): void {
    if (this.creators.has(type)) {
      throw new Error(`Provider creator for "${type}" is already registered`);
    }
    this.creators.set(type, creator);
  }

  /**
   * Unregister a provider creator
   * @param type Provider type
   */
  unregisterProviderCreator(type: string): void {
    this.creators.delete(type);
  }

  /**
   * Register built-in provider creators
   */
  private registerBuiltinCreators(): void {
    // Register placeholder creators for each provider type
    // These will be replaced with actual implementations in subsequent stories

    this.creators.set(PROVIDER_TYPES.ANTHROPIC_CLAUDE, async (_config) => {
      // Placeholder - will be implemented in Story 1-2
      throw new Error('Anthropic Claude provider not yet implemented');
    });

    this.creators.set(PROVIDER_TYPES.OPENAI_GPT, async (_config) => {
      // Placeholder - will be implemented in Story 1-10
      throw new Error('OpenAI GPT provider not yet implemented');
    });

    this.creators.set(PROVIDER_TYPES.GITHUB_COPILOT, async (_config) => {
      // Placeholder - will be implemented in Story 1-10
      throw new Error('GitHub Copilot provider not yet implemented');
    });

    this.creators.set(PROVIDER_TYPES.GOOGLE_GEMINI, async (_config) => {
      // Placeholder - will be implemented in Story 1-10
      throw new Error('Google Gemini provider not yet implemented');
    });

    this.creators.set(PROVIDER_TYPES.LOCAL_LLM, async (_config) => {
      // Placeholder - will be implemented in Story 1-10
      throw new Error('Local LLM provider not yet implemented');
    });

    this.creators.set(PROVIDER_TYPES.OPENCODE, async (_config) => {
      // Placeholder - will be implemented in Story 1-10
      throw new Error('OpenCode provider not yet implemented');
    });

    this.creators.set(PROVIDER_TYPES.Z_AI, async (_config) => {
      // Placeholder - will be implemented in Story 1-10
      throw new Error('Z.AI provider not yet implemented');
    });

    this.creators.set(PROVIDER_TYPES.ZEN_MCP, async (_config) => {
      // Placeholder - will be implemented in Story 1-10
      throw new Error('Zen MCP provider not yet implemented');
    });

    this.creators.set(PROVIDER_TYPES.OPENROUTER, async (_config) => {
      // Placeholder - will be implemented in Story 1-10
      throw new Error('OpenRouter provider not yet implemented');
    });
  }
}
