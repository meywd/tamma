/**
 * Provider Registry Implementation
 *
 * This module implements the provider registry for managing multiple AI providers.
 * It provides methods to register, unregister, and retrieve providers.
 */

import type { IAIProvider, IProviderRegistry } from './types.js';

/**
 * Provider registry implementation
 */
export class ProviderRegistry implements IProviderRegistry {
  private providers = new Map<string, IAIProvider>();

  /**
   * Register a provider
   * @param name Provider name
   * @param provider Provider instance
   */
  register(name: string, provider: IAIProvider): void {
    if (!name || name.trim() === '') {
      throw new Error('Provider name cannot be empty');
    }

    if (!provider) {
      throw new Error('Provider cannot be null or undefined');
    }

    if (this.providers.has(name)) {
      throw new Error(`Provider "${name}" is already registered`);
    }

    this.providers.set(name, provider);
  }

  /**
   * Get a provider by name
   * @param name Provider name
   * @returns Provider instance or undefined
   */
  getProvider(name: string): IAIProvider | undefined {
    return this.providers.get(name);
  }

  /**
   * Get all registered providers
   * @returns Map of provider name to provider instance
   */
  getProviders(): Map<string, IAIProvider> {
    return new Map(this.providers);
  }

  /**
   * Unregister a provider
   * @param name Provider name
   */
  unregister(name: string): void {
    this.providers.delete(name);
  }

  /**
   * Check if a provider is registered
   * @param name Provider name
   * @returns True if provider is registered
   */
  hasProvider(name: string): boolean {
    return this.providers.has(name);
  }

  /**
   * Clear all registered providers
   */
  clear(): void {
    this.providers.clear();
  }

  /**
   * Get the number of registered providers
   * @returns Number of registered providers
   */
  size(): number {
    return this.providers.size;
  }

  /**
   * Get all registered provider names
   * @returns Array of provider names
   */
  getProviderNames(): string[] {
    return Array.from(this.providers.keys());
  }

  /**
   * Dispose all providers and clear the registry
   */
  async dispose(): Promise<void> {
    const disposePromises = Array.from(this.providers.values()).map((provider) =>
      provider.dispose().catch((error) => {
        // Log error but continue disposing other providers
        console.error('Error disposing provider:', error);
      })
    );

    await Promise.allSettled(disposePromises);
    this.clear();
  }
}
