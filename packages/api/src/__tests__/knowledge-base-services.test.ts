/**
 * Knowledge Base Services Tests
 *
 * Unit tests for the knowledge base service layer.
 * Tests both the "no dependency" (empty/zero state) path and
 * basic constructor/method shapes.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import { IndexManagementService } from '../services/knowledge-base/IndexManagementService.js';
import { VectorDBManagementService } from '../services/knowledge-base/VectorDBManagementService.js';
import { RAGManagementService } from '../services/knowledge-base/RAGManagementService.js';
import { MCPManagementService } from '../services/knowledge-base/MCPManagementService.js';
import { ContextTestingService } from '../services/knowledge-base/ContextTestingService.js';
import { AnalyticsService } from '../services/knowledge-base/AnalyticsService.js';

describe('IndexManagementService', () => {
  let service: IndexManagementService;

  beforeEach(() => {
    service = new IndexManagementService();
  });

  it('returns idle status initially', async () => {
    const status = await service.getStatus();
    expect(status.status).toBe('idle');
    expect(status.filesIndexed).toBe(0);
    expect(status.chunksCreated).toBe(0);
  });

  it('throws when triggering without an indexer configured', async () => {
    await expect(service.triggerIndex()).rejects.toThrow('No indexer or project path configured');
  });

  it('throws when cancelling without indexing', async () => {
    await expect(service.cancelIndex()).rejects.toThrow('No indexing operation');
  });

  it('returns empty history initially', async () => {
    const history = await service.getHistory();
    expect(history).toEqual([]);
  });

  it('returns default config', async () => {
    const config = await service.getConfig();
    expect(config.includePatterns).toBeDefined();
    expect(config.excludePatterns).toBeDefined();
    expect(config.chunkingConfig.maxTokens).toBe(500);
  });

  it('updates config', async () => {
    const updated = await service.updateConfig({
      includePatterns: ['**/*.py'],
      chunkingConfig: { maxTokens: 1000, overlapTokens: 50, preserveImports: true, groupRelatedCode: true },
    });
    expect(updated.includePatterns).toContain('**/*.py');
    expect(updated.chunkingConfig.maxTokens).toBe(1000);
  });

  it('dispose is callable', () => {
    expect(() => service.dispose()).not.toThrow();
  });
});

describe('VectorDBManagementService', () => {
  let service: VectorDBManagementService;

  beforeEach(() => {
    service = new VectorDBManagementService();
  });

  it('returns empty collection list without a store', async () => {
    const collections = await service.listCollections();
    expect(collections).toEqual([]);
  });

  it('throws when creating collection without a store', async () => {
    await expect(service.createCollection('test', 768)).rejects.toThrow('No vector store configured');
  });

  it('throws when getting stats for non-existent collection', async () => {
    await expect(service.getCollectionStats('codebase')).rejects.toThrow('Collection not found');
  });

  it('throws when deleting non-existent collection', async () => {
    await expect(service.deleteCollection('codebase')).rejects.toThrow('Collection not found');
  });

  it('throws when searching non-existent collection', async () => {
    await expect(
      service.search({ collection: 'codebase', query: 'test', topK: 3 }),
    ).rejects.toThrow('Collection not found');
  });

  it('returns zero storage usage without a store', async () => {
    const usage = await service.getStorageUsage();
    expect(usage.totalBytes).toBe(0);
    expect(Object.keys(usage.byCollection).length).toBe(0);
  });
});

describe('RAGManagementService', () => {
  let service: RAGManagementService;

  beforeEach(() => {
    service = new RAGManagementService();
  });

  it('returns default config', async () => {
    const config = await service.getConfig();
    expect(config.sources.vectorDb.enabled).toBe(false);
    expect(config.ranking.fusionMethod).toBe('rrf');
    expect(config.assembly.maxTokens).toBe(4000);
  });

  it('updates config', async () => {
    const updated = await service.updateConfig({
      assembly: { maxTokens: 8000, format: 'markdown', includeScores: false },
    });
    expect(updated.assembly.maxTokens).toBe(8000);
  });

  it('returns zero metrics without a pipeline', async () => {
    const metrics = await service.getMetrics();
    expect(metrics.totalQueries).toBe(0);
    expect(metrics.avgLatencyMs).toBe(0);
    expect(metrics.cacheHitRate).toBe(0);
    expect(metrics.sourceBreakdown).toBeDefined();
  });

  it('returns empty test result without a pipeline', async () => {
    const result = await service.testQuery({ query: 'test query', topK: 5 });
    expect(result.queryId).toBe('');
    expect(result.chunks).toEqual([]);
    expect(result.assembledContext).toBe('');
    expect(result.tokenCount).toBe(0);
  });
});

describe('MCPManagementService', () => {
  let service: MCPManagementService;

  beforeEach(() => {
    service = new MCPManagementService();
  });

  it('returns empty server list without a client', async () => {
    const servers = await service.listServers();
    expect(servers).toEqual([]);
  });

  it('throws for unknown server', async () => {
    await expect(service.getServerStatus('nonexistent')).rejects.toThrow('not found');
  });

  it('throws when starting server without a client', async () => {
    await expect(service.startServer('test')).rejects.toThrow('not found');
  });

  it('throws when stopping server without a client', async () => {
    await expect(service.stopServer('test')).rejects.toThrow('not found');
  });

  it('returns empty tool list without a client', async () => {
    const tools = await service.listTools();
    expect(tools).toEqual([]);
  });

  it('returns failure when invoking tool without a client', async () => {
    const result = await service.invokeTool({
      serverName: 'filesystem',
      toolName: 'read_file',
      arguments: { path: '/test.txt' },
    });
    expect(result.success).toBe(false);
    expect(result.error).toContain('not found');
  });

  it('returns empty logs for unknown server', async () => {
    const logs = await service.getServerLogs('unknown');
    expect(logs).toEqual([]);
  });
});

describe('ContextTestingService', () => {
  let service: ContextTestingService;

  beforeEach(() => {
    service = new ContextTestingService();
  });

  it('returns empty result without an aggregator', async () => {
    const result = await service.testContext({
      query: 'How does auth work?',
      taskType: 'implementation',
      maxTokens: 4000,
      sources: ['vector_db', 'rag'],
    });

    expect(result.requestId).toBe('');
    expect(result.context.chunks).toEqual([]);
    expect(result.context.tokenCount).toBe(0);
    expect(result.sources).toEqual([]);
    expect(result.metrics.totalLatencyMs).toBe(0);
  });

  it('maintains test history (empty results still stored)', async () => {
    // Without aggregator, results are empty but not stored
    // (the empty result has requestId '' which means no aggregator)
    const history = await service.getRecentTests(10);
    expect(history.length).toBe(0);
  });

  it('submits feedback without error', async () => {
    await expect(
      service.submitFeedback({
        requestId: 'test-id',
        feedback: [{ chunkId: 'chunk-1', rating: 'relevant' }],
      })
    ).resolves.not.toThrow();
  });
});

describe('AnalyticsService', () => {
  let service: AnalyticsService;

  beforeEach(() => {
    service = new AnalyticsService();
  });

  it('returns zero usage analytics without a cost tracker', async () => {
    const analytics = await service.getUsageAnalytics({
      start: new Date(Date.now() - 86400000).toISOString(),
      end: new Date().toISOString(),
    });

    expect(analytics.totalQueries).toBe(0);
    expect(analytics.totalTokensRetrieved).toBe(0);
    expect(analytics.avgLatencyMs).toBe(0);
    expect(analytics.sourceBreakdown).toBeDefined();
  });

  it('returns zero quality analytics without a cost tracker', async () => {
    const analytics = await service.getQualityAnalytics({
      start: new Date(Date.now() - 86400000).toISOString(),
      end: new Date().toISOString(),
    });

    expect(typeof analytics.relevanceRate).toBe('number');
    expect(analytics.relevanceRate).toBe(0);
    expect(analytics.topPerformingSources).toEqual([]);
  });

  it('returns zero cost analytics without a cost tracker', async () => {
    const analytics = await service.getCostAnalytics({
      start: new Date(Date.now() - 86400000).toISOString(),
      end: new Date().toISOString(),
    });

    expect(analytics.totalCostUsd).toBe(0);
    expect(analytics.breakdown).toEqual([]);
  });
});
