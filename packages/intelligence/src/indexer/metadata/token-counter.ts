/**
 * Token Counter
 *
 * Estimates token counts for text content.
 * Uses a simple heuristic-based approach for efficiency.
 */

/**
 * Token counting method
 */
export type TokenCountMethod = 'simple' | 'approximate' | 'gpt';

/**
 * Simple token estimation based on character count and word boundaries.
 * This is a fast approximation that works well for code.
 *
 * Rules:
 * - Average English word: ~1.3 tokens
 * - Average code token: ~4 characters
 * - Punctuation and operators: 1 token each
 *
 * @param text - Text to estimate tokens for
 * @returns Estimated token count
 */
export function estimateTokensSimple(text: string): number {
  if (!text) return 0;

  // Split by whitespace and punctuation
  const tokens = text.split(/[\s\n\r\t]+/).filter(Boolean);

  let tokenCount = 0;

  for (const token of tokens) {
    // Short tokens (1-4 chars) are usually 1 token
    if (token.length <= 4) {
      tokenCount += 1;
    }
    // Medium tokens (5-8 chars) are usually 1-2 tokens
    else if (token.length <= 8) {
      tokenCount += Math.ceil(token.length / 4);
    }
    // Longer tokens get split more
    else {
      tokenCount += Math.ceil(token.length / 3);
    }
  }

  return tokenCount;
}

/**
 * More accurate token estimation using GPT tokenization rules.
 * Approximates the behavior of tiktoken without the dependency.
 *
 * @param text - Text to estimate tokens for
 * @returns Estimated token count
 */
export function estimateTokensApproximate(text: string): number {
  if (!text) return 0;

  let count = 0;
  let i = 0;

  while (i < text.length) {
    const char = text[i]!;

    // Whitespace characters
    if (/\s/.test(char)) {
      // Count consecutive whitespace as 1 token if it's spaces
      // Newlines are usually 1 token each
      if (char === '\n') {
        count++;
        i++;
      } else {
        // Skip consecutive spaces, they often get merged
        while (i < text.length && /[ \t]/.test(text[i]!)) {
          i++;
        }
        count++;
      }
      continue;
    }

    // Numbers are usually 1 token per digit or grouped
    if (/\d/.test(char)) {
      const numStart = i;
      while (i < text.length && /[\d.]/.test(text[i]!)) {
        i++;
      }
      const numLen = i - numStart;
      count += Math.ceil(numLen / 3);
      continue;
    }

    // Common punctuation is usually 1 token
    if (/[.,;:!?()[\]{}<>"'`@#$%^&*+=|\\~/-]/.test(char)) {
      count++;
      i++;
      continue;
    }

    // Words (alphanumeric sequences)
    if (/[a-zA-Z_]/.test(char)) {
      const wordStart = i;
      while (i < text.length && /[a-zA-Z0-9_]/.test(text[i]!)) {
        i++;
      }
      const word = text.slice(wordStart, i);

      // Common short words: 1 token
      if (word.length <= 4) {
        count++;
      }
      // Longer words: approximate based on subword tokenization
      else {
        // CamelCase or snake_case often splits
        const subwords = word.split(/(?=[A-Z])|_/).filter(Boolean);
        if (subwords.length > 1) {
          count += subwords.length;
        } else {
          count += Math.ceil(word.length / 4);
        }
      }
      continue;
    }

    // Any other character (unicode, etc.)
    count++;
    i++;
  }

  return count;
}

/**
 * Default token counter using approximate method
 * @param text - Text to count tokens for
 * @returns Estimated token count
 */
export function estimateTokens(text: string): number {
  return estimateTokensApproximate(text);
}

/**
 * Token counter class with configurable method
 */
export class TokenCounter {
  private method: TokenCountMethod;

  constructor(method: TokenCountMethod = 'approximate') {
    this.method = method;
  }

  /**
   * Count tokens in text
   * @param text - Text to count
   * @returns Token count
   */
  count(text: string): number {
    switch (this.method) {
      case 'simple':
        return estimateTokensSimple(text);
      case 'approximate':
      case 'gpt':
        return estimateTokensApproximate(text);
      default:
        return estimateTokensApproximate(text);
    }
  }

  /**
   * Check if text exceeds token limit
   * @param text - Text to check
   * @param limit - Token limit
   * @returns True if exceeds limit
   */
  exceedsLimit(text: string, limit: number): boolean {
    return this.count(text) > limit;
  }

  /**
   * Truncate text to fit within token limit (approximate)
   * @param text - Text to truncate
   * @param limit - Token limit
   * @returns Truncated text
   */
  truncateToLimit(text: string, limit: number): string {
    if (!this.exceedsLimit(text, limit)) {
      return text;
    }

    // Binary search for the right length
    let low = 0;
    let high = text.length;

    while (low < high) {
      const mid = Math.floor((low + high + 1) / 2);
      const truncated = text.slice(0, mid);
      if (this.count(truncated) <= limit) {
        low = mid;
      } else {
        high = mid - 1;
      }
    }

    return text.slice(0, low);
  }
}
