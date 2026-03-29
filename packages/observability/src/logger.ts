import { createRequire } from 'node:module';
import pino from 'pino';
import type { ILogger } from '@tamma/shared';

// createRequire enables loading CJS-only packages (pino-elasticsearch) from ESM
const esmRequire = createRequire(import.meta.url);

/**
 * Configuration for the OpenSearch transport.
 * Reads from environment variables with sensible defaults.
 */
interface OpenSearchTransportConfig {
  /** OpenSearch node URL */
  node: string;
  /** Index name prefix (date suffix added automatically by pino-elasticsearch) */
  index: string;
  /** Whether OpenSearch transport is enabled */
  enabled: boolean;
  /** Flush threshold in bytes (default: 1000) */
  flushBytes: number;
  /** Flush interval in ms (default: 5000) */
  flushInterval: number;
}

function getOpenSearchConfig(): OpenSearchTransportConfig {
  return {
    node: process.env['OPENSEARCH_URL'] ?? 'http://opensearch:9200',
    index: process.env['LOG_INDEX_PREFIX'] ?? 'tamma-ts',
    enabled: process.env['OPENSEARCH_ENABLED'] !== 'false',
    flushBytes: 1000,
    flushInterval: 5000,
  };
}

/**
 * Wraps a pino.Logger to conform to the ILogger interface from @tamma/shared.
 * Supports child() for creating scoped loggers with bound context (e.g.
 * workflowInstanceId, issueNumber, sessionId).
 */
function wrapPinoLogger(pinoLogger: pino.Logger): ILogger {
  return {
    debug(message: string, context?: Record<string, unknown>): void {
      if (context !== undefined) {
        pinoLogger.debug(context, message);
      } else {
        pinoLogger.debug(message);
      }
    },
    info(message: string, context?: Record<string, unknown>): void {
      if (context !== undefined) {
        pinoLogger.info(context, message);
      } else {
        pinoLogger.info(message);
      }
    },
    warn(message: string, context?: Record<string, unknown>): void {
      if (context !== undefined) {
        pinoLogger.warn(context, message);
      } else {
        pinoLogger.warn(message);
      }
    },
    error(message: string, context?: Record<string, unknown>): void {
      if (context !== undefined) {
        pinoLogger.error(context, message);
      } else {
        pinoLogger.error(message);
      }
    },
    child(childContext: Record<string, unknown>): ILogger {
      return wrapPinoLogger(pinoLogger.child(childContext));
    },
  };
}

/**
 * Build a pino.Logger that writes to stdout and (optionally) to OpenSearch.
 *
 * - In development (NODE_ENV !== 'production'), uses pino-pretty for stdout.
 * - In production, writes JSON to stdout + streams to OpenSearch via pino-elasticsearch.
 * - If OPENSEARCH_ENABLED=false, only stdout is used.
 * - If pino-elasticsearch is not installed or fails to connect, falls back to stdout-only
 *   with a warning on stderr. Application logs are never lost.
 */
function buildPinoLogger(name: string, resolvedLevel: string): pino.Logger {
  const osConfig = getOpenSearchConfig();

  const options: pino.LoggerOptions = {
    name,
    level: resolvedLevel,
    // Add service field for OpenSearch filtering
    base: {
      pid: process.pid,
      hostname: undefined, // pino adds this by default
      service: process.env['SERVICE_NAME'] ?? name,
    },
  };

  if (osConfig.enabled) {
    try {
      // pino-elasticsearch is CJS-only — use createRequire to load it from ESM.
      // Dynamic require avoids hard failure when running without OpenSearch.
      const pinoElasticsearch = esmRequire('pino-elasticsearch') as (
        opts: Record<string, unknown>,
      ) => NodeJS.WritableStream & { on: (event: string, cb: (...args: unknown[]) => void) => void };

      const osStream = pinoElasticsearch({
        node: osConfig.node,
        index: osConfig.index,
        flushBytes: osConfig.flushBytes,
        flushInterval: osConfig.flushInterval,
        esVersion: 7, // OpenSearch uses ES 7.x compatible bulk API
        op_type: 'create',
      });

      // Log OpenSearch transport errors to stderr (not to pino, to avoid loops)
      osStream.on('error', (err: unknown) => {
        const msg = err instanceof Error ? err.message : String(err);
        process.stderr.write(
          `[tamma-logger] OpenSearch transport error: ${msg}\n`
        );
      });

      osStream.on('insertError', (err: unknown) => {
        process.stderr.write(
          `[tamma-logger] OpenSearch insert error: ${String(err)}\n`
        );
      });

      const multistream = pino.multistream([
        { stream: process.stdout },
        { stream: osStream },
      ]);

      process.stderr.write(
        `[tamma-logger] OpenSearch transport enabled → ${osConfig.node} (index: ${osConfig.index})\n`
      );
      return pino(options, multistream);
    } catch (loadErr) {
      const detail = loadErr instanceof Error ? loadErr.message : String(loadErr);
      process.stderr.write(
        `[tamma-logger] pino-elasticsearch not available (${detail}), falling back to stdout only\n`
      );
      if (process.env['NODE_ENV'] !== 'production') {
        options.transport = { target: 'pino-pretty', options: { colorize: true } };
      }
      return pino(options);
    }
  }

  if (process.env['NODE_ENV'] !== 'production') {
    options.transport = { target: 'pino-pretty', options: { colorize: true } };
  }
  return pino(options);
}

/**
 * Creates a raw pino.Logger with OpenSearch transport (when enabled).
 *
 * Use this when you need the native pino instance — e.g. to pass as
 * Fastify's `logger` option so that request/response logs also ship
 * to OpenSearch.
 *
 * @param name - Logger name (appears in `name` field in logs)
 * @param level - Minimum log level (default: LOG_LEVEL env var or 'info')
 */
export function createPinoLogger(name: string, level?: string): pino.Logger {
  const resolvedLevel = level ?? process.env['LOG_LEVEL'] ?? 'info';
  return buildPinoLogger(name, resolvedLevel);
}

/**
 * Creates a logger that writes to stdout and (optionally) to OpenSearch.
 *
 * Returns an ILogger wrapper. For a raw pino.Logger (e.g. Fastify integration),
 * use {@link createPinoLogger} instead.
 *
 * @param name - Logger name (appears in `name` field in logs)
 * @param level - Minimum log level (default: LOG_LEVEL env var or 'info')
 */
export function createLogger(name: string, level?: string): ILogger {
  const resolvedLevel = level ?? process.env['LOG_LEVEL'] ?? 'info';
  return wrapPinoLogger(buildPinoLogger(name, resolvedLevel));
}
