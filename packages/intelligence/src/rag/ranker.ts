/**
 * Result Ranker
 *
 * Implements ranking algorithms for RAG results including:
 * - Reciprocal Rank Fusion (RRF) for multi-source merging
 * - Max Marginal Relevance (MMR) for diversity
 * - Recency boosting
 */

import type {
  RetrievedChunk,
  RAGSourceType,
  RankingConfig,
  IRanker,
} from './types.js';
import { cosineSimilarity } from '../vector-store/utils/distance-metrics.js';

/**
 * Ranker implementation
 */
export class Ranker implements IRanker {
  /**
   * Merge results from multiple sources using Reciprocal Rank Fusion
   *
   * RRF Formula: score = sum(1 / (k + rank)) for each source where document appears
   * This method is effective at combining ranked lists from different sources.
   */
  mergeWithRRF(
    sourceResults: Map<RAGSourceType, RetrievedChunk[]>,
    config: RankingConfig
  ): RetrievedChunk[] {
    const k = config.rrfK;
    const fusedScores = new Map<string, number>();
    const chunkMap = new Map<string, RetrievedChunk>();

    // Calculate RRF scores for each source
    for (const [_source, chunks] of sourceResults) {
      // Get weight for this source (default to 1.0 if not specified)
      const weight = 1.0; // Weights are applied during retrieval

      chunks.forEach((chunk, rank) => {
        // RRF score formula: weight * 1/(k + rank + 1)
        const rrfScore = weight * (1 / (k + rank + 1));
        const currentScore = fusedScores.get(chunk.id) ?? 0;
        fusedScores.set(chunk.id, currentScore + rrfScore);

        // Store the chunk (prefer higher-scoring version if duplicate)
        if (!chunkMap.has(chunk.id) || (chunkMap.get(chunk.id)!.score < chunk.score)) {
          chunkMap.set(chunk.id, chunk);
        }
      });
    }

    // Create result array with fused scores
    const results: RetrievedChunk[] = [];
    for (const [id, fusedScore] of fusedScores) {
      const chunk = chunkMap.get(id)!;
      results.push({
        ...chunk,
        fusedScore,
      });
    }

    // Sort by fused score descending
    return results.sort((a, b) => (b.fusedScore ?? 0) - (a.fusedScore ?? 0));
  }

  /**
   * Apply Max Marginal Relevance for result diversity
   *
   * MMR = argmax[lambda * Sim(d, q) - (1 - lambda) * max(Sim(d, d_j))]
   *
   * Where:
   * - lambda = relevance vs diversity trade-off (1.0 = pure relevance, 0.0 = pure diversity)
   * - Sim(d, q) = similarity of document to query (using fusedScore)
   * - Sim(d, d_j) = similarity between documents (using embeddings)
   */
  applyMMR(chunks: RetrievedChunk[], k: number, lambda: number): RetrievedChunk[] {
    if (chunks.length === 0) {
      return [];
    }

    if (chunks.length <= k) {
      return chunks;
    }

    // Check if we have embeddings for diversity calculation
    const hasEmbeddings = chunks.some((c) => c.embedding && c.embedding.length > 0);

    if (!hasEmbeddings) {
      // Without embeddings, just return top-k by fused score
      return chunks.slice(0, k);
    }

    const selected: RetrievedChunk[] = [];
    const remaining = [...chunks];

    // Normalize scores to 0-1 range for fair comparison
    const maxScore = Math.max(...chunks.map((c) => c.fusedScore ?? c.score));
    const normalizedChunks = remaining.map((c) => ({
      ...c,
      normalizedScore: (c.fusedScore ?? c.score) / (maxScore || 1),
    }));

    while (selected.length < k && normalizedChunks.length > 0) {
      let bestIdx = 0;
      let bestMMRScore = -Infinity;

      for (let i = 0; i < normalizedChunks.length; i++) {
        const candidate = normalizedChunks[i]!;
        const relevance = candidate.normalizedScore;

        // Calculate max similarity to already selected documents
        let maxSimilarity = 0;
        if (selected.length > 0 && candidate.embedding) {
          for (const selectedChunk of selected) {
            if (selectedChunk.embedding) {
              const similarity = cosineSimilarity(candidate.embedding, selectedChunk.embedding);
              maxSimilarity = Math.max(maxSimilarity, similarity);
            }
          }
        }

        // MMR score
        const mmrScore = lambda * relevance - (1 - lambda) * maxSimilarity;

        if (mmrScore > bestMMRScore) {
          bestMMRScore = mmrScore;
          bestIdx = i;
        }
      }

      // Add best candidate to selected
      const bestChunk = normalizedChunks[bestIdx]!;
      selected.push({
        ...bestChunk,
        fusedScore: bestChunk.fusedScore,
      });

      // Remove from remaining
      normalizedChunks.splice(bestIdx, 1);
    }

    return selected;
  }

  /**
   * Apply recency boost to chunks with dates
   *
   * Boost formula: boost = recencyBoost * (1 - daysOld / decayDays)
   * Clamps to 0 if older than decayDays.
   */
  applyRecencyBoost(chunks: RetrievedChunk[], config: RankingConfig): RetrievedChunk[] {
    const now = Date.now();
    const decayMs = config.recencyDecayDays * 24 * 60 * 60 * 1000;

    return chunks.map((chunk) => {
      if (!chunk.metadata.date) {
        return chunk;
      }

      const chunkDate = chunk.metadata.date instanceof Date
        ? chunk.metadata.date.getTime()
        : new Date(chunk.metadata.date).getTime();

      const ageMs = now - chunkDate;

      // Calculate decay factor (1.0 for new, 0.0 for old)
      const decayFactor = Math.max(0, 1 - ageMs / decayMs);

      // Apply boost to fused score
      const boost = config.recencyBoost * decayFactor;
      const currentScore = chunk.fusedScore ?? chunk.score;

      return {
        ...chunk,
        fusedScore: currentScore * (1 + boost),
      };
    });
  }

  /**
   * Normalize scores to 0-1 range
   */
  normalizeScores(chunks: RetrievedChunk[]): RetrievedChunk[] {
    if (chunks.length === 0) {
      return [];
    }

    const scores = chunks.map((c) => c.fusedScore ?? c.score);
    const minScore = Math.min(...scores);
    const maxScore = Math.max(...scores);
    const range = maxScore - minScore;

    if (range === 0) {
      // All scores are the same
      return chunks.map((c) => ({ ...c, fusedScore: 1 }));
    }

    return chunks.map((chunk) => ({
      ...chunk,
      fusedScore: ((chunk.fusedScore ?? chunk.score) - minScore) / range,
    }));
  }

  /**
   * Apply linear fusion (weighted average of scores)
   */
  mergeWithLinear(
    sourceResults: Map<RAGSourceType, RetrievedChunk[]>,
    weights: Map<RAGSourceType, number>
  ): RetrievedChunk[] {
    const fusedScores = new Map<string, { score: number; weight: number }>();
    const chunkMap = new Map<string, RetrievedChunk>();

    for (const [source, chunks] of sourceResults) {
      const weight = weights.get(source) ?? 1.0;

      for (const chunk of chunks) {
        const current = fusedScores.get(chunk.id) ?? { score: 0, weight: 0 };
        fusedScores.set(chunk.id, {
          score: current.score + chunk.score * weight,
          weight: current.weight + weight,
        });

        if (!chunkMap.has(chunk.id)) {
          chunkMap.set(chunk.id, chunk);
        }
      }
    }

    // Calculate weighted average
    const results: RetrievedChunk[] = [];
    for (const [id, { score, weight }] of fusedScores) {
      const chunk = chunkMap.get(id)!;
      results.push({
        ...chunk,
        fusedScore: weight > 0 ? score / weight : 0,
      });
    }

    return results.sort((a, b) => (b.fusedScore ?? 0) - (a.fusedScore ?? 0));
  }

  /**
   * Deduplicate chunks based on content similarity
   */
  deduplicateChunks(chunks: RetrievedChunk[], threshold: number): RetrievedChunk[] {
    if (chunks.length === 0) {
      return [];
    }

    const deduplicated: RetrievedChunk[] = [];
    const seen = new Set<string>();

    for (const chunk of chunks) {
      // Check for exact ID duplicates
      if (seen.has(chunk.id)) {
        continue;
      }

      // Check for content similarity if we have embeddings
      let isDuplicate = false;
      if (threshold < 1.0 && chunk.embedding) {
        for (const existing of deduplicated) {
          if (existing.embedding) {
            const similarity = cosineSimilarity(chunk.embedding, existing.embedding);
            if (similarity >= threshold) {
              isDuplicate = true;
              break;
            }
          }
        }
      }

      if (!isDuplicate) {
        deduplicated.push(chunk);
        seen.add(chunk.id);
      }
    }

    return deduplicated;
  }
}

/**
 * Create a ranker instance
 */
export function createRanker(): Ranker {
  return new Ranker();
}
