import type { IContextSource, ContextSourceType, SourceConfig, SourceQuery, SourceResult, ContextChunk } from '../types.js';

export abstract class BaseContextSource implements IContextSource {
  abstract readonly name: ContextSourceType;
  protected config: SourceConfig = { enabled: false, timeoutMs: 5000 };
  protected initialized = false;

  async initialize(config: SourceConfig): Promise<void> {
    this.config = config;
    this.initialized = true;
  }

  async isAvailable(): Promise<boolean> {
    return this.initialized && this.config.enabled;
  }

  async retrieve(query: SourceQuery): Promise<SourceResult> {
    const startTime = Date.now();
    try {
      const chunks = await this.withTimeout(
        this.doRetrieve(query),
        query.timeout ?? this.config.timeoutMs
      );
      return {
        chunks: chunks.slice(0, query.maxChunks),
        latencyMs: Date.now() - startTime,
        cacheHit: false,
      };
    } catch (error) {
      return {
        chunks: [],
        latencyMs: Date.now() - startTime,
        cacheHit: false,
        error: error instanceof Error ? error.message : String(error),
      };
    }
  }

  async dispose(): Promise<void> {
    this.initialized = false;
  }

  protected abstract doRetrieve(query: SourceQuery): Promise<ContextChunk[]>;

  protected async withTimeout<T>(promise: Promise<T>, timeoutMs: number): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`Source ${this.name} timed out after ${timeoutMs}ms`)), timeoutMs);
      promise.then(result => { clearTimeout(timer); resolve(result); }).catch(err => { clearTimeout(timer); reject(err); });
    });
  }
}
