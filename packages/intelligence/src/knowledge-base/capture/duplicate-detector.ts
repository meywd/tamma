/**
 * Duplicate Detector
 *
 * Detects duplicate or similar learnings to prevent redundancy in the knowledge base.
 */

import type { LearningCapture, KnowledgeEntry } from '@tamma/shared';
import type { IKnowledgeStore } from '../types.js';

/**
 * Options for duplicate detection
 */
export interface DuplicateDetectorOptions {
  /** Minimum keyword overlap ratio to consider duplicate (0-1) */
  keywordOverlapThreshold: number;
  /** Minimum title similarity to consider duplicate (0-1) */
  titleSimilarityThreshold: number;
  /** Minimum description similarity to consider duplicate (0-1) */
  descriptionSimilarityThreshold: number;
  /** Check within same project only */
  projectScopeOnly: boolean;
}

/**
 * Default duplicate detection options
 */
const DEFAULT_OPTIONS: DuplicateDetectorOptions = {
  keywordOverlapThreshold: 0.7,
  titleSimilarityThreshold: 0.8,
  descriptionSimilarityThreshold: 0.7,
  projectScopeOnly: false,
};

/**
 * Calculate Jaccard similarity between two sets
 */
function jaccardSimilarity(a: Set<string>, b: Set<string>): number {
  if (a.size === 0 && b.size === 0) {
    return 1;
  }

  const intersection = new Set([...a].filter((x) => b.has(x)));
  const union = new Set([...a, ...b]);

  return intersection.size / union.size;
}

/**
 * Calculate string similarity using bigram Dice coefficient.
 * Dice = 2*|intersection| / (|A|+|B|) — more robust than Jaccard
 * when strings differ in length (e.g. prefix + extra words).
 */
function bigramSimilarity(a: string, b: string): number {
  const aNorm = a.toLowerCase().trim();
  const bNorm = b.toLowerCase().trim();

  if (aNorm === bNorm) {
    return 1;
  }
  if (aNorm.length < 2 || bNorm.length < 2) {
    return 0;
  }

  // Generate bigrams
  const getBigrams = (s: string): Set<string> => {
    const bigrams = new Set<string>();
    for (let i = 0; i < s.length - 1; i++) {
      bigrams.add(s.substring(i, i + 2));
    }
    return bigrams;
  };

  const aBigrams = getBigrams(aNorm);
  const bBigrams = getBigrams(bNorm);

  const intersection = new Set([...aBigrams].filter((x) => bBigrams.has(x)));
  return (2 * intersection.size) / (aBigrams.size + bBigrams.size);
}

/**
 * Duplicate detector for learnings
 */
export class DuplicateDetector {
  private store: IKnowledgeStore;
  private options: DuplicateDetectorOptions;

  constructor(
    store: IKnowledgeStore,
    options?: Partial<DuplicateDetectorOptions>
  ) {
    this.store = store;
    this.options = { ...DEFAULT_OPTIONS, ...options };
  }

  /**
   * Check if a learning capture is a duplicate of existing entries
   */
  async isDuplicate(capture: LearningCapture): Promise<boolean> {
    const similar = await this.findSimilar(capture);
    return similar.length > 0;
  }

  /**
   * Find similar existing entries
   */
  async findSimilar(capture: LearningCapture): Promise<KnowledgeEntry[]> {
    // Get existing learnings to compare against
    const { entries } = await this.store.list({
      types: ['learning'],
      ...(this.options.projectScopeOnly
        ? { projectId: capture.projectId }
        : {}),
      enabled: true,
      limit: 100,
    });

    // When projectScopeOnly, exclude entries with a different projectId
    const filteredEntries = this.options.projectScopeOnly
      ? entries.filter(
          (e) => !e.projectId || e.projectId === capture.projectId
        )
      : entries;

    const similar: KnowledgeEntry[] = [];

    for (const entry of filteredEntries) {
      if (this.isSimilarEntry(capture, entry)) {
        similar.push(entry);
      }
    }

    return similar;
  }

  /**
   * Check if a capture is similar to an existing entry
   */
  private isSimilarEntry(
    capture: LearningCapture,
    entry: KnowledgeEntry
  ): boolean {
    // Check title similarity (primary signal)
    const titleSim = bigramSimilarity(
      capture.suggestedTitle,
      entry.title
    );
    if (titleSim >= this.options.titleSimilarityThreshold) {
      return true;
    }

    // If title is somewhat similar (near threshold), don't rely on secondary
    // signals alone — this respects strict title thresholds
    if (titleSim >= this.options.titleSimilarityThreshold * 0.6) {
      return false;
    }

    // For clearly different titles, check keyword overlap as secondary signal
    const captureKeywords = new Set(
      capture.suggestedKeywords.map((k) => k.toLowerCase())
    );
    const entryKeywords = new Set(
      entry.keywords.map((k) => k.toLowerCase())
    );
    const keywordOverlap = jaccardSimilarity(captureKeywords, entryKeywords);
    if (keywordOverlap >= this.options.keywordOverlapThreshold) {
      return true;
    }

    return false;
  }

  /**
   * Get similarity score between a capture and an entry
   */
  getSimilarityScore(
    capture: LearningCapture,
    entry: KnowledgeEntry
  ): number {
    const titleSim = bigramSimilarity(capture.suggestedTitle, entry.title);
    const descSim = bigramSimilarity(
      capture.suggestedDescription,
      entry.description
    );

    const captureKeywords = new Set(
      capture.suggestedKeywords.map((k) => k.toLowerCase())
    );
    const entryKeywords = new Set(entry.keywords.map((k) => k.toLowerCase()));
    const keywordSim = jaccardSimilarity(captureKeywords, entryKeywords);

    // Weighted average
    return titleSim * 0.3 + descSim * 0.4 + keywordSim * 0.3;
  }

  /**
   * Update options
   */
  setOptions(options: Partial<DuplicateDetectorOptions>): void {
    this.options = { ...this.options, ...options };
  }

  /**
   * Get current options
   */
  getOptions(): DuplicateDetectorOptions {
    return { ...this.options };
  }
}
