import pino from 'pino';
import type { ILogger } from '@tamma/shared';

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
 * Creates a logger that writes to stdout and (optionally) to OpenSearch.
 *
 * - In development (NODE_ENV !== 'production'), uses pino-pretty for stdout.
 * - In production, writes JSON to stdout + streams to OpenSearch via pino-elasticsearch.
 * - If OPENSEARCH_ENABLED=false, only stdout is used.
 * - If pino-elasticsearch is not installed or fails to connect, falls back to stdout-only
 *   with a warning on stderr. Application logs are never lost.
 *
 * @param name - Logger name (appears in `name` field in logs)
 * @param level - Minimum log level (default: LOG_LEVEL env var or 'info')
 */
export function createLogger(name: string, level?: string): ILogger {
  const resolvedLevel = level ?? process.env['LOG_LEVEL'] ?? 'info';
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

  let pinoLogger: pino.Logger;

  if (osConfig.enabled) {
    try {
      // pino-elasticsearch is a peer dependency — dynamically require to avoid
      // hard failure when running in environments without OpenSearch.
      // eslint-disable-next-line @typescript-eslint/no-require-imports
      const pinoElasticsearch = require('pino-elasticsearch');

      const osStream = pinoElasticsearch({
        node: osConfig.node,
        index: osConfig.index,
        flushBytes: osConfig.flushBytes,
        flushInterval: osConfig.flushInterval,
        esVersion: 7, // OpenSearch uses ES 7.x compatible bulk API
        op_type: 'create',
      });

      // Log OpenSearch transport errors to stderr (not to pino, to avoid loops)
      osStream.on('error', (err: Error) => {
        process.stderr.write(
          `[tamma-logger] OpenSearch transport error: ${err.message}\n`
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

      pinoLogger = pino(options, multistream);
    } catch {
      process.stderr.write(
        '[tamma-logger] pino-elasticsearch not available, falling back to stdout only\n'
      );
      if (process.env['NODE_ENV'] !== 'production') {
        options.transport = { target: 'pino-pretty', options: { colorize: true } };
      }
      pinoLogger = pino(options);
    }
  } else if (process.env['NODE_ENV'] !== 'production') {
    options.transport = { target: 'pino-pretty', options: { colorize: true } };
    pinoLogger = pino(options);
  } else {
    pinoLogger = pino(options);
  }

  return wrapPinoLogger(pinoLogger);
}
