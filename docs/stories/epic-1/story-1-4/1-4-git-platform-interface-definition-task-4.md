# Task 4: Add Pagination and Rate Limit Support

**Story**: 1-4 Git Platform Interface Definition  
**Task**: 4 of 6 - Add Pagination and Rate Limit Support  
**Epic**: 1 - Foundation & Core Infrastructure  
**Status**: Pending

## Task Description

Define comprehensive pagination interfaces and rate limit handling mechanisms for Git platform operations. This includes support for different pagination strategies (page-based, cursor-based, offset-based) and intelligent rate limit detection, handling, and recovery across all platforms.

## Acceptance Criteria

1. **Pagination Interfaces**: Define unified pagination interfaces supporting cursor, page, and offset-based strategies
2. **Rate Limit Detection**: Add methods for detecting current rate limit status and reset times
3. **Rate Limit Handling**: Implement automatic retry logic with exponential backoff for rate-limited requests
4. **Pagination Utilities**: Create utilities for seamless pagination across different platform strategies
5. **Rate Limit Recovery**: Implement intelligent recovery strategies with configurable retry policies

## Implementation Details

### 1. Unified Pagination Interfaces

```typescript
/**
 * Unified pagination interface supporting multiple pagination strategies
 */
interface PaginationOptions {
  /** Pagination strategy */
  strategy: PaginationStrategy;

  /** Page-based pagination */
  page?: {
    /** Page number (1-based) */
    number: number;

    /** Items per page */
    size: number;
  };

  /** Offset-based pagination */
  offset?: {
    /** Number of items to skip */
    offset: number;

    /** Maximum number of items to return */
    limit: number;
  };

  /** Cursor-based pagination */
  cursor?: {
    /** Pagination cursor */
    value: string;

    /** Direction of pagination */
    direction?: 'forward' | 'backward';

    /** Maximum number of items to return */
    limit?: number;
  };

  /** Additional pagination parameters */
  parameters?: Record<string, string | number>;
}

/**
 * Pagination strategies supported by platforms
 */
enum PaginationStrategy {
  PAGE_BASED = 'page_based', // GitHub, Gitea, Forgejo
  OFFSET_BASED = 'offset_based', // GitLab
  CURSOR_BASED = 'cursor_based', // GraphQL APIs
  AUTO = 'auto', // Automatically detect best strategy
}

/**
 * Paginated response wrapper
 */
interface PaginatedResponse<T> {
  /** Response items */
  data: T[];

  /** Pagination information */
  pagination: PaginationInfo;

  /** Response metadata */
  metadata?: {
    /** Total processing time */
    processingTime?: number;

    /** Rate limit information */
    rateLimit?: RateLimitInfo;

    /** API response metadata */
    api?: Record<string, unknown>;
  };
}

/**
 * Pagination information
 */
interface PaginationInfo {
  /** Pagination strategy used */
  strategy: PaginationStrategy;

  /** Current page information */
  currentPage?: {
    /** Current page number */
    number: number;

    /** Items per page */
    size: number;

    /** Has next page */
    hasNext: boolean;

    /** Has previous page */
    hasPrev: boolean;
  };

  /** Offset information */
  offset?: {
    /** Current offset */
    current: number;

    /** Limit used */
    limit: number;

    /** Total items available */
    total?: number;

    /** Has more items */
    hasMore: boolean;
  };

  /** Cursor information */
  cursor?: {
    /** Current cursor */
    current?: string;

    /** Next cursor */
    next?: string;

    /** Previous cursor */
    prev?: string;

    /** Has next page */
    hasNext: boolean;

    /** Has previous page */
    hasPrev: boolean;
  };

  /** Total count information */
  totalCount?: {
    /** Total items available */
    items: number;

    /** Total pages (for page-based) */
    pages?: number;

    /** Count accuracy */
    accuracy: 'exact' | 'estimated' | 'unknown';
  };

  /** Navigation helpers */
  navigation?: {
    /** First page */
    first?: PaginationOptions;

    /** Last page */
    last?: PaginationOptions;

    /** Next page */
    next?: PaginationOptions;

    /** Previous page */
    prev?: PaginationOptions;
  };
}
```

### 2. Rate Limit Detection and Management

```typescript
/**
 * Rate limit information
 */
interface RateLimitInfo {
  /** Current rate limit status */
  status: RateLimitStatus;

  /** Request limits */
  limits: {
    /** Maximum requests per window */
    maxRequests: number;

    /** Remaining requests */
    remainingRequests: number;

    /** Requests used */
    usedRequests: number;

    /** Reset timestamp */
    resetAt: string;

    /** Seconds until reset */
    resetInSeconds: number;
  };

  /** Rate limit window */
  window: {
    /** Window duration in seconds */
    duration: number;

    /** Window type */
    type: RateLimitWindowType;

    /** Window start timestamp */
    startedAt?: string;
  };

  /** Resource-specific limits */
  resources?: {
    /** Core API limits */
    core?: RateLimitResource;

    /** Search API limits */
    search?: RateLimitResource;

    /** GraphQL API limits */
    graphql?: RateLimitResource;

    /** Integration limits */
    integration?: RateLimitResource;

    /** Custom resource limits */
    [key: string]: RateLimitResource | undefined;
  };

  /** Platform-specific information */
  platformData?: Record<string, unknown>;
}

/**
 * Rate limit status
 */
enum RateLimitStatus {
  OK = 'ok', // No rate limiting
  WARNING = 'warning', // Approaching limit
  LIMITED = 'limited', // Rate limited
  EXCEEDED = 'exceeded', // Limit exceeded
  UNKNOWN = 'unknown', // Status unknown
}

/**
 * Rate limit window types
 */
enum RateLimitWindowType {
  PER_HOUR = 'per_hour', // Hourly limits
  PER_MINUTE = 'per_minute', // Per-minute limits
  PER_SECOND = 'per_second', // Per-second limits
  PER_DAY = 'per_day', // Daily limits
  CUSTOM = 'custom', // Custom window
}

/**
 * Resource-specific rate limit information
 */
interface RateLimitResource {
  /** Maximum requests for this resource */
  maxRequests: number;

  /** Remaining requests */
  remainingRequests: number;

  /** Reset timestamp */
  resetAt: string;

  /** Reset in seconds */
  resetInSeconds: number;
}

/**
 * Rate limit detection interface
 */
interface RateLimitDetector {
  /** Detect current rate limit status */
  detectRateLimit(response: unknown): RateLimitInfo;

  /** Check if rate limited based on response */
  isRateLimited(response: unknown): boolean;

  /** Extract retry-after duration */
  getRetryAfter(response: unknown): number;

  /** Get rate limit headers */
  getRateLimitHeaders(response: unknown): Record<string, string>;
}
```

### 3. Rate Limit Handling and Recovery

```typescript
/**
 * Rate limit handling configuration
 */
interface RateLimitConfig {
  /** Retry configuration */
  retry: {
    /** Maximum number of retries */
    maxRetries: number;

    /** Base delay in milliseconds */
    baseDelay: number;

    /** Maximum delay in milliseconds */
    maxDelay: number;

    /** Exponential backoff multiplier */
    backoffMultiplier: number;

    /** Jitter factor */
    jitterFactor: number;

    /** Retry condition */
    shouldRetry: (error: unknown, attempt: number) => boolean;
  };

  /** Rate limit thresholds */
  thresholds: {
    /** Warning threshold (percentage) */
    warning: number; // e.g., 0.8 (80%)

    /** Critical threshold (percentage) */
    critical: number; // e.g., 0.95 (95%)

    /** Buffer before limit (percentage) */
    buffer: number; // e.g., 0.1 (10%)
  };

  /** Adaptive throttling */
  adaptive: {
    /** Enable adaptive throttling */
    enabled: boolean;

    /** Adjustment factor */
    adjustmentFactor: number;

    /** Minimum request interval */
    minInterval: number; // milliseconds

    /** Maximum request interval */
    maxInterval: number; // milliseconds
  };

  /** Queue configuration */
  queue: {
    /** Enable request queuing */
    enabled: boolean;

    /** Maximum queue size */
    maxSize: number;

    /** Queue timeout in milliseconds */
    timeout: number;
  };
}

/**
 * Rate limit handler interface
 */
interface RateLimitHandler {
  /** Handle rate limited response */
  handleRateLimit(
    error: RateLimitError,
    request: () => Promise<unknown>,
    config?: RateLimitConfig
  ): Promise<unknown>;

  /** Execute request with rate limit protection */
  executeWithRateLimit<T>(request: () => Promise<T>, config?: RateLimitConfig): Promise<T>;

  /** Get current rate limit status */
  getCurrentRateLimit(): RateLimitInfo | null;

  /** Update rate limit status */
  updateRateLimit(rateLimit: RateLimitInfo): void;

  /** Reset rate limit tracking */
  resetRateLimit(): void;
}

/**
 * Rate limit error
 */
class RateLimitError extends Error {
  constructor(
    public rateLimitInfo: RateLimitInfo,
    public retryAfter: number,
    message?: string
  ) {
    super(message || `Rate limit exceeded. Retry after ${retryAfter} seconds`);
    this.name = 'RateLimitError';
  }
}

/**
 * Default rate limit handler implementation
 */
class DefaultRateLimitHandler implements RateLimitHandler {
  private currentRateLimit: RateLimitInfo | null = null;
  private requestQueue: Array<() => void> = [];
  private isProcessingQueue = false;

  constructor(private config: RateLimitConfig = this.getDefaultConfig()) {}

  async handleRateLimit<T>(
    error: RateLimitError,
    request: () => Promise<T>,
    config?: RateLimitConfig
  ): Promise<T> {
    const finalConfig = { ...this.config, ...config };

    // Calculate retry delay
    const delay = this.calculateRetryDelay(error.retryAfter, finalConfig);

    // Wait before retrying
    await this.sleep(delay);

    // Retry the request
    return request();
  }

  async executeWithRateLimit<T>(request: () => Promise<T>, config?: RateLimitConfig): Promise<T> {
    const finalConfig = { ...this.config, ...config };
    let attempt = 0;

    while (attempt <= finalConfig.retry.maxRetries) {
      try {
        // Check if we should throttle based on current rate limit
        if (this.shouldThrottle(finalConfig)) {
          await this.throttle(finalConfig);
        }

        // Execute the request
        const result = await request();

        // Update rate limit info from response if available
        // This would be implemented by platform-specific code

        return result;
      } catch (error) {
        attempt++;

        // Check if error is rate limit related
        if (this.isRateLimitError(error) && attempt <= finalConfig.retry.maxRetries) {
          const rateLimitError = error as RateLimitError;

          // Update current rate limit info
          this.updateRateLimit(rateLimitError.rateLimitInfo);

          // Handle rate limit with retry
          continue;
        }

        // Re-throw non-rate-limit errors or max retries exceeded
        throw error;
      }
    }

    throw new Error(`Max retries (${finalConfig.retry.maxRetries}) exceeded`);
  }

  getCurrentRateLimit(): RateLimitInfo | null {
    return this.currentRateLimit;
  }

  updateRateLimit(rateLimit: RateLimitInfo): void {
    this.currentRateLimit = rateLimit;

    // Trigger queue processing if rate limit is OK
    if (rateLimit.status === RateLimitStatus.OK && this.requestQueue.length > 0) {
      this.processQueue();
    }
  }

  resetRateLimit(): void {
    this.currentRateLimit = null;
    this.requestQueue = [];
    this.isProcessingQueue = false;
  }

  private calculateRetryDelay(retryAfter: number, config: RateLimitConfig): number {
    const baseDelay = Math.max(retryAfter * 1000, config.retry.baseDelay);
    const exponentialDelay = baseDelay * Math.pow(config.retry.backoffMultiplier, 1);
    const jitter = exponentialDelay * config.retry.jitterFactor * Math.random();

    return Math.min(exponentialDelay + jitter, config.retry.maxDelay);
  }

  private shouldThrottle(config: RateLimitConfig): boolean {
    if (!config.adaptive.enabled || !this.currentRateLimit) {
      return false;
    }

    const usageRatio =
      this.currentRateLimit.limits.usedRequests / this.currentRateLimit.limits.maxRequests;
    return usageRatio > config.thresholds.warning;
  }

  private async throttle(config: RateLimitConfig): Promise<void> {
    if (!this.currentRateLimit) return;

    const usageRatio =
      this.currentRateLimit.limits.usedRequests / this.currentRateLimit.limits.maxRequests;
    const adjustmentFactor = config.adaptive.adjustmentFactor;
    const baseInterval = config.adaptive.minInterval;

    // Calculate adaptive delay based on usage ratio
    const adaptiveDelay = Math.min(
      baseInterval * Math.pow(adjustmentFactor, usageRatio * 10),
      config.adaptive.maxInterval
    );

    await this.sleep(adaptiveDelay);
  }

  private isRateLimitError(error: unknown): boolean {
    return (
      error instanceof RateLimitError ||
      (error instanceof Error &&
        (error.message.includes('rate limit') ||
          error.message.includes('too many requests') ||
          error.message.includes('quota exceeded')))
    );
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  private getDefaultConfig(): RateLimitConfig {
    return {
      retry: {
        maxRetries: 5,
        baseDelay: 1000,
        maxDelay: 60000,
        backoffMultiplier: 2,
        jitterFactor: 0.1,
        shouldRetry: (error, attempt) => attempt < 5,
      },
      thresholds: {
        warning: 0.8,
        critical: 0.95,
        buffer: 0.1,
      },
      adaptive: {
        enabled: true,
        adjustmentFactor: 1.5,
        minInterval: 100,
        maxInterval: 5000,
      },
      queue: {
        enabled: true,
        maxSize: 100,
        timeout: 30000,
      },
    };
  }

  private async processQueue(): Promise<void> {
    if (this.isProcessingQueue || this.requestQueue.length === 0) {
      return;
    }

    this.isProcessingQueue = true;

    while (this.requestQueue.length > 0) {
      const request = this.requestQueue.shift();
      if (request) {
        try {
          request();
        } catch (error) {
          console.error('Error processing queued request:', error);
        }
      }
    }

    this.isProcessingQueue = false;
  }
}
```

### 4. Pagination Utilities

```typescript
/**
 * Pagination utility interface
 */
interface PaginationUtil {
  /** Create pagination options from different input types */
  createPaginationOptions(input: PaginationInput): PaginationOptions;

  /** Detect pagination strategy from response */
  detectStrategy(response: unknown): PaginationStrategy;

  /** Extract pagination info from response */
  extractPaginationInfo(response: unknown, strategy: PaginationStrategy): PaginationInfo;

  /** Create next page request options */
  getNextPageOptions(currentInfo: PaginationInfo): PaginationOptions | null;

  /** Create previous page request options */
  getPrevPageOptions(currentInfo: PaginationInfo): PaginationOptions | null;

  /** Iterate through all pages */
  async *paginateAll<T>(
    request: (options: PaginationOptions) => Promise<PaginatedResponse<T>>,
    initialOptions: PaginationOptions
  ): AsyncGenerator<T[], void, unknown>;
}

/**
 * Pagination input types
 */
type PaginationInput =
  | { page: number; size: number }
  | { offset: number; limit: number }
  | { cursor: string; limit?: number }
  | PaginationOptions;

/**
 * Default pagination utility implementation
 */
class DefaultPaginationUtil implements PaginationUtil {
  createPaginationOptions(input: PaginationInput): PaginationOptions {
    if ('strategy' in input) {
      return input as PaginationOptions;
    }

    if ('page' in input) {
      return {
        strategy: PaginationStrategy.PAGE_BASED,
        page: {
          number: input.page,
          size: input.size
        }
      };
    }

    if ('offset' in input) {
      return {
        strategy: PaginationStrategy.OFFSET_BASED,
        offset: {
          offset: input.offset,
          limit: input.limit
        }
      };
    }

    if ('cursor' in input) {
      return {
        strategy: PaginationStrategy.CURSOR_BASED,
        cursor: {
          value: input.cursor,
          limit: input.limit
        }
      };
    }

    throw new Error('Invalid pagination input');
  }

  detectStrategy(response: unknown): PaginationStrategy {
    // Platform-specific strategy detection
    // This would be implemented by platform-specific transformers

    // For now, return AUTO to let the platform decide
    return PaginationStrategy.AUTO;
  }

  extractPaginationInfo(response: unknown, strategy: PaginationStrategy): PaginationInfo {
    // Platform-specific pagination info extraction
    // This would be implemented by platform-specific transformers

    switch (strategy) {
      case PaginationStrategy.PAGE_BASED:
        return this.extractPageBasedInfo(response);
      case PaginationStrategy.OFFSET_BASED:
        return this.extractOffsetBasedInfo(response);
      case PaginationStrategy.CURSOR_BASED:
        return this.extractCursorBasedInfo(response);
      default:
        throw new Error(`Unsupported pagination strategy: ${strategy}`);
    }
  }

  getNextPageOptions(currentInfo: PaginationInfo): PaginationOptions | null {
    if (!currentInfo.navigation?.next) {
      return null;
    }

    return currentInfo.navigation.next;
  }

  getPrevPageOptions(currentInfo: PaginationInfo): PaginationOptions | null {
    if (!currentInfo.navigation?.prev) {
      return null;
    }

    return currentInfo.navigation.prev;
  }

  async *paginateAll<T>(
    request: (options: PaginationOptions) => Promise<PaginatedResponse<T>>,
    initialOptions: PaginationOptions
  ): AsyncGenerator<T[], void, unknown> {
    let currentOptions = initialOptions;
    let hasMore = true;

    while (hasMore) {
      const response = await request(currentOptions);

      // Yield current page data
      if (response.data.length > 0) {
        yield response.data;
      }

      // Check if there are more pages
      const nextOptions = this.getNextPageOptions(response.pagination);
      if (nextOptions) {
        currentOptions = nextOptions;
      } else {
        hasMore = false;
      }
    }
  }

  private extractPageBasedInfo(response: unknown): PaginationInfo {
    // Implementation would extract page-based pagination info
    // This is platform-specific and would be implemented by transformers
    throw new Error('Page-based pagination extraction not implemented');
  }

  private extractOffsetBasedInfo(response: unknown): PaginationInfo {
    // Implementation would extract offset-based pagination info
    // This is platform-specific and would be implemented by transformers
    throw new Error('Offset-based pagination extraction not implemented');
  }

  private extractCursorBasedInfo(response: unknown): PaginationInfo {
    // Implementation would extract cursor-based pagination info
    // This is platform-specific and would be implemented by transformers
    throw new Error('Cursor-based pagination extraction not implemented');
  }
}
```

### 5. Platform-Specific Implementations

```typescript
/**
 * GitHub pagination and rate limit implementation
 */
class GitHubPaginationHandler {
  /**
   * Extract pagination info from GitHub response
   */
  extractPaginationInfo(response: GitHubResponse): PaginationInfo {
    const linkHeader = response.headers?.link;
    const totalCount = parseInt(response.headers?.['x-total-count'] || '0');

    // Parse Link header for pagination
    const links = this.parseLinkHeader(linkHeader);

    return {
      strategy: PaginationStrategy.PAGE_BASED,
      currentPage: {
        number: this.extractPageNumber(links.self),
        size: this.extractPageSize(links.self),
        hasNext: !!links.next,
        hasPrev: !!links.prev,
      },
      totalCount: {
        items: totalCount,
        accuracy: totalCount > 0 ? 'exact' : 'unknown',
      },
      navigation: {
        first: links.first ? this.parseUrlToOptions(links.first) : undefined,
        last: links.last ? this.parseUrlToOptions(links.last) : undefined,
        next: links.next ? this.parseUrlToOptions(links.next) : undefined,
        prev: links.prev ? this.parseUrlToOptions(links.prev) : undefined,
      },
    };
  }

  /**
   * Extract rate limit info from GitHub response
   */
  extractRateLimitInfo(response: GitHubResponse): RateLimitInfo {
    const headers = response.headers;

    return {
      status: this.determineRateLimitStatus(headers),
      limits: {
        maxRequests: parseInt(headers['x-ratelimit-limit'] || '0'),
        remainingRequests: parseInt(headers['x-ratelimit-remaining'] || '0'),
        usedRequests: parseInt(headers['x-ratelimit-used'] || '0'),
        resetAt: new Date(parseInt(headers['x-ratelimit-reset'] || '0') * 1000).toISOString(),
        resetInSeconds:
          parseInt(headers['x-ratelimit-reset'] || '0') - Math.floor(Date.now() / 1000),
      },
      window: {
        duration: 3600, // GitHub uses hourly limits
        type: RateLimitWindowType.PER_HOUR,
      },
      resources: {
        core: {
          maxRequests: parseInt(headers['x-ratelimit-limit'] || '0'),
          remainingRequests: parseInt(headers['x-ratelimit-remaining'] || '0'),
          resetAt: new Date(parseInt(headers['x-ratelimit-reset'] || '0') * 1000).toISOString(),
          resetInSeconds:
            parseInt(headers['x-ratelimit-reset'] || '0') - Math.floor(Date.now() / 1000),
        },
      },
    };
  }

  private parseLinkHeader(linkHeader: string): Record<string, string> {
    const links: Record<string, string> = {};

    if (!linkHeader) return links;

    const entries = linkHeader.split(',');

    for (const entry of entries) {
      const match = entry.match(/<([^>]+)>;\s*rel="([^"]+)"/);
      if (match) {
        links[match[2]] = match[1];
      }
    }

    return links;
  }

  private extractPageNumber(url: string): number {
    const match = url.match(/[?&]page=(\d+)/);
    return match ? parseInt(match[1]) : 1;
  }

  private extractPageSize(url: string): number {
    const match = url.match(/[?&]per_page=(\d+)/);
    return match ? parseInt(match[1]) : 30;
  }

  private parseUrlToOptions(url: string): PaginationOptions {
    const urlObj = new URL(url);
    const page = urlObj.searchParams.get('page');
    const perPage = urlObj.searchParams.get('per_page');

    return {
      strategy: PaginationStrategy.PAGE_BASED,
      page: {
        number: page ? parseInt(page) : 1,
        size: perPage ? parseInt(perPage) : 30,
      },
    };
  }

  private determineRateLimitStatus(headers: Record<string, string>): RateLimitStatus {
    const remaining = parseInt(headers['x-ratelimit-remaining'] || '0');
    const limit = parseInt(headers['x-ratelimit-limit'] || '0');

    if (remaining === 0) {
      return RateLimitStatus.LIMITED;
    }

    const ratio = remaining / limit;
    if (ratio < 0.05) {
      return RateLimitStatus.CRITICAL;
    } else if (ratio < 0.2) {
      return RateLimitStatus.WARNING;
    }

    return RateLimitStatus.OK;
  }
}

/**
 * GitLab pagination and rate limit implementation
 */
class GitLabPaginationHandler {
  /**
   * Extract pagination info from GitLab response
   */
  extractPaginationInfo(response: GitLabResponse): PaginationInfo {
    const headers = response.headers;

    return {
      strategy: PaginationStrategy.OFFSET_BASED,
      offset: {
        current: parseInt(headers['x-next-page'] || '0') - parseInt(headers['x-per-page'] || '20'),
        limit: parseInt(headers['x-per-page'] || '20'),
        total: parseInt(headers['x-total'] || '0'),
        hasMore: !!headers['x-next-page'],
      },
      totalCount: {
        items: parseInt(headers['x-total'] || '0'),
        accuracy: 'exact',
      },
      navigation: {
        next: headers['x-next-page']
          ? {
              strategy: PaginationStrategy.OFFSET_BASED,
              offset: {
                offset: parseInt(headers['x-next-page']) - 1,
                limit: parseInt(headers['x-per-page'] || '20'),
              },
            }
          : undefined,
        prev: headers['x-prev-page']
          ? {
              strategy: PaginationStrategy.OFFSET_BASED,
              offset: {
                offset: parseInt(headers['x-prev-page']) - 1,
                limit: parseInt(headers['x-per-page'] || '20'),
              },
            }
          : undefined,
      },
    };
  }

  /**
   * Extract rate limit info from GitLab response
   */
  extractRateLimitInfo(response: GitLabResponse): RateLimitInfo {
    const headers = response.headers;
    const rateLimitHeaders = this.parseRateLimitHeaders(headers['ratelimit-remaining'] || '');

    return {
      status: this.determineRateLimitStatus(rateLimitHeaders),
      limits: {
        maxRequests: rateLimitHeaders.limit || 0,
        remainingRequests: rateLimitHeaders.remaining || 0,
        usedRequests: (rateLimitHeaders.limit || 0) - (rateLimitHeaders.remaining || 0),
        resetAt: new Date(rateLimitHeaders.reset || Date.now() + 60000).toISOString(),
        resetInSeconds: Math.max(
          0,
          Math.floor((rateLimitHeaders.reset || Date.now() + 60000 - Date.now()) / 1000)
        ),
      },
      window: {
        duration: rateLimitHeaders.window || 60,
        type:
          rateLimitHeaders.window === 3600
            ? RateLimitWindowType.PER_HOUR
            : RateLimitWindowType.PER_MINUTE,
      },
    };
  }

  private parseRateLimitHeaders(headerValue: string): RateLimitHeaders {
    // GitLab rate limit format: "remaining, limit, reset, window"
    const parts = headerValue.split(',').map((p) => p.trim());

    return {
      remaining: parts[0] ? parseInt(parts[0]) : undefined,
      limit: parts[1] ? parseInt(parts[1]) : undefined,
      reset: parts[2] ? parseInt(parts[2]) * 1000 : undefined,
      window: parts[3] ? parseInt(parts[3]) : undefined,
    };
  }

  private determineRateLimitStatus(headers: RateLimitHeaders): RateLimitStatus {
    if (!headers.remaining || !headers.limit) {
      return RateLimitStatus.UNKNOWN;
    }

    if (headers.remaining === 0) {
      return RateLimitStatus.LIMITED;
    }

    const ratio = headers.remaining / headers.limit;
    if (ratio < 0.05) {
      return RateLimitStatus.CRITICAL;
    } else if (ratio < 0.2) {
      return RateLimitStatus.WARNING;
    }

    return RateLimitStatus.OK;
  }
}

interface RateLimitHeaders {
  remaining?: number;
  limit?: number;
  reset?: number;
  window?: number;
}
```

## File Structure

```
packages/platforms/src/
├── pagination/
│   ├── index.ts                                      # Pagination exports
│   ├── pagination.interface.ts                        # Pagination interfaces
│   ├── pagination.util.ts                             # Pagination utilities
│   └── pagination-handler.ts                          # Pagination handler
├── rate-limit/
│   ├── index.ts                                      # Rate limit exports
│   ├── rate-limit.interface.ts                        # Rate limit interfaces
│   ├── rate-limit.handler.ts                          # Rate limit handler
│   ├── rate-limit.config.ts                           # Rate limit configuration
│   └── rate-limit.error.ts                            # Rate limit error
├── platform-specific/
│   ├── github/
│   │   ├── github-pagination.handler.ts               # GitHub pagination
│   │   └── github-rate-limit.handler.ts               # GitHub rate limits
│   ├── gitlab/
│   │   ├── gitlab-pagination.handler.ts               # GitLab pagination
│   │   └── gitlab-rate-limit.handler.ts               # GitLab rate limits
│   ├── gitea/
│   │   ├── gitea-pagination.handler.ts               # Gitea pagination
│   │   └── gitea-rate-limit.handler.ts               # Gitea rate limits
│   └── forgejo/
│       ├── forgejo-pagination.handler.ts             # Forgejo pagination
│       └── forgejo-rate-limit.handler.ts             # Forgejo rate limits
└── __tests__/
    ├── pagination/                                    # Pagination tests
    ├── rate-limit/                                    # Rate limit tests
    └── integration/                                   # Integration tests
```

## Testing Strategy

**Pagination Testing**:

- Test all pagination strategies (page, offset, cursor)
- Test pagination info extraction from platform responses
- Test pagination utilities and navigation
- Test edge cases (empty responses, single page, etc.)

**Rate Limit Testing**:

- Test rate limit detection from headers
- Test retry logic with exponential backoff
- Test adaptive throttling mechanisms
- Test rate limit recovery strategies

**Integration Testing**:

- Test end-to-end pagination with rate limiting
- Test concurrent request handling
- Test queue management and processing
- Test performance under load

## Completion Checklist

- [ ] Define unified pagination interfaces for all strategies
- [ ] Create comprehensive rate limit detection and management
- [ ] Implement automatic retry logic with exponential backoff
- [ ] Create pagination utilities for seamless navigation
- [ ] Add platform-specific implementations for GitHub and GitLab
- [ ] Implement adaptive throttling mechanisms
- [ ] Create request queue management
- [ ] Add comprehensive error handling
- [ ] Implement extensive testing suite
- [ ] Document configuration and usage patterns

## Dependencies

- Task 1: Core Git Platform Interface Structure (base interfaces)
- Task 2: Platform Capabilities Discovery (capability-based behavior)

## Risks and Mitigations

**Risk**: Rate limit handling complexity leads to request storms
**Mitigation**: Conservative retry policies, adaptive throttling, request queuing

**Risk**: Pagination differences cause data inconsistencies
**Mitigation**: Comprehensive testing, unified pagination utilities

**Risk**: Performance impact from rate limit overhead
**Mitigation**: Efficient detection algorithms, caching, minimal overhead

## Success Criteria

- Seamless pagination across all platforms and strategies
- Intelligent rate limit handling with automatic recovery
- Configurable retry policies and throttling
- High-performance implementation with minimal overhead
- Comprehensive error handling and recovery

This task ensures robust handling of pagination and rate limiting across all Git platforms, providing reliable and efficient API interactions.
