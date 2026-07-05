/**
 * Query Processor
 *
 * Processes RAG queries by expanding terms, extracting entities,
 * and classifying intent.
 */

import type {
  RAGQuery,
  ProcessedQuery,
  ExtractedEntity,
  EntityType,
  QueryIntent,
  IQueryProcessor,
} from './types.js';
import type { EmbeddingService } from '../indexer/embedding/embedding-service.js';

/**
 * Common programming-related synonyms for query expansion
 */
const SYNONYM_MAP: Record<string, string[]> = {
  function: ['method', 'func', 'fn', 'procedure', 'subroutine'],
  method: ['function', 'func', 'fn'],
  class: ['type', 'struct', 'object', 'entity'],
  interface: ['type', 'protocol', 'contract'],
  variable: ['var', 'const', 'let', 'field', 'property', 'prop'],
  import: ['require', 'include', 'use'],
  export: ['expose', 'public'],
  error: ['exception', 'bug', 'issue', 'problem', 'failure'],
  fix: ['resolve', 'repair', 'patch', 'correct'],
  test: ['spec', 'unittest', 'it', 'describe'],
  async: ['await', 'promise', 'asynchronous'],
  array: ['list', 'collection', 'slice'],
  object: ['dict', 'map', 'hash', 'record'],
  string: ['str', 'text'],
  number: ['int', 'integer', 'float', 'num'],
  boolean: ['bool', 'flag'],
  null: ['nil', 'none', 'undefined'],
  create: ['add', 'new', 'make', 'generate'],
  delete: ['remove', 'drop', 'destroy'],
  update: ['modify', 'change', 'edit', 'set'],
  read: ['get', 'fetch', 'retrieve', 'query', 'find'],
  api: ['endpoint', 'route', 'handler'],
  database: ['db', 'store', 'repository'],
  config: ['configuration', 'settings', 'options'],
};

/**
 * Patterns for entity extraction
 */
const ENTITY_PATTERNS: Array<{ pattern: RegExp; type: EntityType }> = [
  // File paths
  { pattern: /(?:^|[\s"'`])([a-zA-Z0-9_\-./]+\.[a-zA-Z]{2,4})(?:[\s"'`]|$)/g, type: 'file' },
  // Function/method calls
  { pattern: /(?:function|def|func|fn)\s+([a-zA-Z_][a-zA-Z0-9_]*)/gi, type: 'function' },
  { pattern: /([a-zA-Z_][a-zA-Z0-9_]*)\s*\(/g, type: 'function' },
  // Class names (PascalCase)
  { pattern: /(?:class|interface|type|struct)\s+([A-Z][a-zA-Z0-9]*)/g, type: 'class' },
  { pattern: /\b([A-Z][a-zA-Z0-9]{2,})\b/g, type: 'class' },
  // Package/module names
  { pattern: /(?:from|import)\s+['"]?([a-zA-Z@][a-zA-Z0-9_\-./]*)/g, type: 'package' },
  { pattern: /@([a-zA-Z][a-zA-Z0-9_\-/]*)/g, type: 'package' },
  // Variable/symbol references
  { pattern: /(?:const|let|var)\s+([a-zA-Z_][a-zA-Z0-9_]*)/g, type: 'variable' },
];

/**
 * Intent classification keywords
 */
const INTENT_KEYWORDS: Record<QueryIntent, string[]> = {
  code_search: ['find', 'search', 'where', 'locate', 'show', 'list', 'get'],
  explanation: ['what', 'why', 'how', 'explain', 'describe', 'understand'],
  implementation: ['implement', 'create', 'add', 'build', 'make', 'write', 'code'],
  debugging: ['fix', 'bug', 'error', 'issue', 'problem', 'debug', 'wrong', 'broken'],
  documentation: ['document', 'docs', 'comment', 'readme', 'guide', 'tutorial'],
  refactoring: ['refactor', 'improve', 'optimize', 'clean', 'restructure', 'simplify'],
  general: [],
};

/**
 * Escape special regex characters in a string for safe use in RegExp constructor
 */
function escapeRegExp(s: string): string {
  return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Collapse all runs of whitespace into a single space and trim.
 * Avoids regex \s+ which some engines handle poorly on large inputs.
 */
function collapseWhitespace(text: string): string {
  const result: string[] = [];
  let inWhitespace = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text.charCodeAt(i);
    // Space, tab, newline, carriage return, form feed, vertical tab
    if (ch === 0x20 || ch === 0x09 || ch === 0x0a || ch === 0x0d || ch === 0x0c || ch === 0x0b) {
      if (!inWhitespace && result.length > 0) {
        result.push(' ');
      }
      inWhitespace = true;
    } else {
      inWhitespace = false;
      result.push(text[i]!);
    }
  }
  // Trim trailing space
  if (result.length > 0 && result[result.length - 1] === ' ') {
    result.pop();
  }
  return result.join('');
}

/**
 * Count whitespace-separated words without regex.
 */
function countWords(text: string): number {
  let count = 0;
  let inWord = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text.charCodeAt(i);
    if (ch === 0x20 || ch === 0x09 || ch === 0x0a || ch === 0x0d) {
      inWord = false;
    } else if (!inWord) {
      inWord = true;
      count++;
    }
  }
  return count;
}

/**
 * Query processor implementation
 */
export class QueryProcessor implements IQueryProcessor {
  private embeddingService?: EmbeddingService | undefined;

  constructor(embeddingService?: EmbeddingService) {
    this.embeddingService = embeddingService;
  }

  /**
   * Process a RAG query into an expanded, analyzed form
   */
  async process(query: RAGQuery): Promise<ProcessedQuery> {
    const text = query.text.trim();

    // Expand query with synonyms
    const expanded = this.expandQuery(text);

    // Extract entities
    const entities = this.extractEntities(text);

    // Classify intent
    const intent = this.classifyIntent(text);

    // Detect language (for multilingual support)
    const language = this.detectLanguage(text);

    // Decompose complex queries
    const decomposed = this.decomposeQuery(text);

    // Generate embedding if service is available
    let embedding: number[] | undefined;
    if (this.embeddingService) {
      try {
        embedding = await this.embeddingService.embed(text);
      } catch (error) {
        // Embedding generation is optional, continue without it
        console.warn('[QueryProcessor] Embedding generation failed:', error instanceof Error ? error.message : String(error));
      }
    }

    return {
      original: text,
      expanded,
      entities,
      decomposed: decomposed.length > 1 ? decomposed : undefined,
      language,
      embedding,
      intent,
    };
  }

  /**
   * Expand query with synonyms and related terms
   */
  expandQuery(text: string): string[] {
    const words = text.toLowerCase().split(/\s+/);
    const expanded = new Set<string>([text]);

    for (const word of words) {
      // Add synonyms
      const synonyms = SYNONYM_MAP[word];
      if (synonyms) {
        for (const synonym of synonyms) {
          // Create expanded query with synonym replacement
          const expandedText = text.replace(new RegExp(`\\b${escapeRegExp(word)}\\b`, 'gi'), synonym);
          if (expandedText !== text) {
            expanded.add(expandedText);
          }
        }
      }
    }

    return Array.from(expanded);
  }

  /**
   * Extract entities (file names, functions, classes, etc.) from query
   */
  extractEntities(text: string): ExtractedEntity[] {
    const entities: ExtractedEntity[] = [];
    const seen = new Set<string>();

    for (const { pattern, type } of ENTITY_PATTERNS) {
      // Reset regex state
      pattern.lastIndex = 0;

      let match;
      while ((match = pattern.exec(text)) !== null) {
        const value = match[1];

        // Skip if already seen or too short
        if (!value || value.length < 2 || seen.has(value.toLowerCase())) {
          continue;
        }

        // Skip common words that might match patterns
        if (this.isCommonWord(value)) {
          continue;
        }

        seen.add(value.toLowerCase());

        // Calculate confidence based on pattern specificity
        const confidence = this.calculateEntityConfidence(value, type);

        entities.push({
          type,
          value,
          confidence,
        });
      }
    }

    // Sort by confidence descending
    return entities.sort((a, b) => b.confidence - a.confidence);
  }

  /**
   * Decompose complex queries into sub-queries
   */
  decomposeQuery(text: string): string[] {
    const subQueries: string[] = [];

    // Normalize whitespace to single spaces.
    // Use a character class with explicit whitespace chars and a single quantifier
    // to avoid ReDoS on strings with many repeated whitespace characters.
    const normalized = collapseWhitespace(text);

    // Split on common conjunctions and question boundaries.
    // Using literal space matches since input is already normalized to single spaces.
    const splitPatterns = [
      ' and ',
      ' also ',
      '? ',
      '; ',
      '?',
      ';',
      ' then ',
      ' after that ',
    ];

    let parts = [normalized];
    for (const sep of splitPatterns) {
      const newParts: string[] = [];
      for (const part of parts) {
        const lowerPart = part.toLowerCase();
        const lowerSep = sep.toLowerCase();
        // Split using indexOf to avoid regex entirely
        let startIdx = 0;
        let idx = lowerPart.indexOf(lowerSep, startIdx);
        if (idx === -1) {
          // No match, keep the part as-is
          const trimmed = part.trim();
          if (trimmed.length > 0) newParts.push(trimmed);
        } else {
          while (idx !== -1) {
            const segment = part.slice(startIdx, idx).trim();
            if (segment.length > 0) newParts.push(segment);
            startIdx = idx + sep.length;
            idx = lowerPart.indexOf(lowerSep, startIdx);
          }
          // Push the remainder
          const remainder = part.slice(startIdx).trim();
          if (remainder.length > 0) newParts.push(remainder);
        }
      }
      parts = newParts;
    }

    // Only keep substantial sub-queries (at least 2 words and > 10 chars)
    for (const part of parts) {
      if (part.length > 10 && countWords(part) >= 2) {
        subQueries.push(part);
      }
    }

    return subQueries.length > 0 ? subQueries : [text];
  }

  /**
   * Classify the intent of a query
   */
  classifyIntent(text: string): QueryIntent {
    const lowerText = text.toLowerCase();
    const scores: Map<QueryIntent, number> = new Map();

    for (const [intent, keywords] of Object.entries(INTENT_KEYWORDS)) {
      let score = 0;
      for (const keyword of keywords) {
        if (lowerText.includes(keyword)) {
          score++;
        }
      }
      scores.set(intent as QueryIntent, score);
    }

    // Find highest scoring intent
    let maxIntent: QueryIntent = 'general';
    let maxScore = 0;

    for (const [intent, score] of scores) {
      if (score > maxScore) {
        maxScore = score;
        maxIntent = intent;
      }
    }

    return maxIntent;
  }

  /**
   * Detect the language of a query (simplified)
   */
  detectLanguage(text: string): string | undefined {
    // Check for explicit language mentions
    const languagePatterns: Record<string, RegExp> = {
      typescript: /\b(typescript|ts)\b/i,
      javascript: /\b(javascript|js)\b/i,
      python: /\b(python|py)\b/i,
      go: /\b(golang|go)\b/i,
      rust: /\b(rust|rs)\b/i,
      java: /\b(java)\b/i,
    };

    for (const [lang, pattern] of Object.entries(languagePatterns)) {
      if (pattern.test(text)) {
        return lang;
      }
    }

    return undefined;
  }

  /**
   * Check if a word is too common to be an entity
   */
  private isCommonWord(word: string): boolean {
    const commonWords = new Set([
      'the', 'a', 'an', 'is', 'are', 'was', 'were', 'be', 'been', 'being',
      'have', 'has', 'had', 'do', 'does', 'did', 'will', 'would', 'could',
      'should', 'may', 'might', 'must', 'can', 'this', 'that', 'these',
      'those', 'it', 'its', 'if', 'then', 'else', 'for', 'while', 'with',
      'to', 'from', 'in', 'on', 'at', 'by', 'of', 'and', 'or', 'not',
      'true', 'false', 'null', 'undefined', 'void', 'new', 'return',
    ]);
    return commonWords.has(word.toLowerCase());
  }

  /**
   * Calculate confidence score for an extracted entity
   */
  private calculateEntityConfidence(value: string, type: EntityType): number {
    let confidence = 0.5;

    // Boost for file extensions
    if (type === 'file' && /\.[a-z]{2,4}$/.test(value)) {
      confidence += 0.3;
    }

    // Boost for path separators in files
    if (type === 'file' && value.includes('/')) {
      confidence += 0.2;
    }

    // Boost for PascalCase (likely class)
    if (type === 'class' && /^[A-Z][a-z]/.test(value)) {
      confidence += 0.2;
    }

    // Boost for camelCase (likely function/variable)
    if ((type === 'function' || type === 'variable') && /^[a-z]+[A-Z]/.test(value)) {
      confidence += 0.2;
    }

    // Boost for package scope (@org/package)
    if (type === 'package' && value.includes('/')) {
      confidence += 0.2;
    }

    // Cap at 1.0
    return Math.min(confidence, 1.0);
  }

  /**
   * Set embedding service (for dependency injection)
   */
  setEmbeddingService(service: EmbeddingService): void {
    this.embeddingService = service;
  }
}

/**
 * Create a query processor instance
 */
export function createQueryProcessor(embeddingService?: EmbeddingService): QueryProcessor {
  return new QueryProcessor(embeddingService);
}
