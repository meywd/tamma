/**
 * @tamma/providers
 * AI provider abstraction layer for Tamma platform
 * Supports: Anthropic Claude, OpenAI, GitHub Copilot, Gemini, and more
 */

// Export all types and interfaces
export * from './types.js';

// Export registry and factory implementations
export { ProviderRegistry } from './registry.js';
export { ProviderFactory } from './factory.js';

// Placeholder for future implementations
export const placeholder = 'providers';
