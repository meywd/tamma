/**
 * Logger module for centralized logging with structured output
 * Supports different log levels and contextual metadata
 */

export enum LogLevel {
  ERROR = 'error',
  WARN = 'warn',
  INFO = 'info',
  DEBUG = 'debug',
  TRACE = 'trace',
}

export interface LogContext {
  [key: string]: any;
}

export interface Logger {
  error(message: string, context?: LogContext): void;
  warn(message: string, context?: LogContext): void;
  info(message: string, context?: LogContext): void;
  debug(message: string, context?: LogContext): void;
  trace(message: string, context?: LogContext): void;
}

class ConsoleLogger implements Logger {
  private serviceName: string;
  private environment: string;
  private logLevel: LogLevel;

  constructor(
    serviceName: string = 'test-platform',
    environment: string = process.env.NODE_ENV || 'development'
  ) {
    this.serviceName = serviceName;
    this.environment = environment;
    this.logLevel = this.getLogLevel();
  }

  private getLogLevel(): LogLevel {
    const level = process.env.LOG_LEVEL?.toLowerCase();
    switch (level) {
      case 'error':
        return LogLevel.ERROR;
      case 'warn':
        return LogLevel.WARN;
      case 'info':
        return LogLevel.INFO;
      case 'debug':
        return LogLevel.DEBUG;
      case 'trace':
        return LogLevel.TRACE;
      default:
        return this.environment === 'production' ? LogLevel.INFO : LogLevel.DEBUG;
    }
  }

  private shouldLog(level: LogLevel): boolean {
    const levels = [LogLevel.ERROR, LogLevel.WARN, LogLevel.INFO, LogLevel.DEBUG, LogLevel.TRACE];
    const currentLevelIndex = levels.indexOf(this.logLevel);
    const messageLevelIndex = levels.indexOf(level);
    return messageLevelIndex <= currentLevelIndex;
  }

  private formatMessage(level: LogLevel, message: string, context?: LogContext): string {
    const timestamp = new Date().toISOString();
    const logEntry = {
      timestamp,
      level,
      service: this.serviceName,
      environment: this.environment,
      message,
      ...context,
    };

    if (this.environment === 'development') {
      // Pretty print for development
      return JSON.stringify(logEntry, null, 2);
    }

    // Single line JSON for production
    return JSON.stringify(logEntry);
  }

  error(message: string, context?: LogContext): void {
    if (this.shouldLog(LogLevel.ERROR)) {
      console.error(this.formatMessage(LogLevel.ERROR, message, context));
    }
  }

  warn(message: string, context?: LogContext): void {
    if (this.shouldLog(LogLevel.WARN)) {
      console.warn(this.formatMessage(LogLevel.WARN, message, context));
    }
  }

  info(message: string, context?: LogContext): void {
    if (this.shouldLog(LogLevel.INFO)) {
      console.info(this.formatMessage(LogLevel.INFO, message, context));
    }
  }

  debug(message: string, context?: LogContext): void {
    if (this.shouldLog(LogLevel.DEBUG)) {
      console.debug(this.formatMessage(LogLevel.DEBUG, message, context));
    }
  }

  trace(message: string, context?: LogContext): void {
    if (this.shouldLog(LogLevel.TRACE)) {
      console.trace(this.formatMessage(LogLevel.TRACE, message, context));
    }
  }
}

// Export singleton instance
export const logger: Logger = new ConsoleLogger();

// Export metrics collector interface for future implementation
export interface MetricsCollector {
  incrementCounter(name: string, labels?: Record<string, string>): void;
  recordHistogram(name: string, value: number, labels?: Record<string, string>): void;
  recordGauge(name: string, value: number, labels?: Record<string, string>): void;
}

// Placeholder metrics collector
class NoOpMetricsCollector implements MetricsCollector {
  incrementCounter(name: string, labels?: Record<string, string>): void {
    // No-op for now
    logger.trace('Metric counter incremented', { metric: name, labels });
  }

  recordHistogram(name: string, value: number, labels?: Record<string, string>): void {
    // No-op for now
    logger.trace('Metric histogram recorded', { metric: name, value, labels });
  }

  recordGauge(name: string, value: number, labels?: Record<string, string>): void {
    // No-op for now
    logger.trace('Metric gauge recorded', { metric: name, value, labels });
  }
}

export const metrics: MetricsCollector = new NoOpMetricsCollector();

// Helper function to create child logger with additional context
export function createChildLogger(additionalContext: LogContext): Logger {
  return {
    error: (message: string, context?: LogContext) =>
      logger.error(message, { ...additionalContext, ...context }),
    warn: (message: string, context?: LogContext) =>
      logger.warn(message, { ...additionalContext, ...context }),
    info: (message: string, context?: LogContext) =>
      logger.info(message, { ...additionalContext, ...context }),
    debug: (message: string, context?: LogContext) =>
      logger.debug(message, { ...additionalContext, ...context }),
    trace: (message: string, context?: LogContext) =>
      logger.trace(message, { ...additionalContext, ...context }),
  };
}