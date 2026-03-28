# Story 15.1: OpenSearch Log Aggregation

Status: ready-for-dev

## Story

As a **platform engineer**,
I want all Tamma services to ship structured logs to a centralized OpenSearch instance,
so that I can search, correlate, visualize, and alert on logs from every service through a single interface instead of SSH-ing into containers and grepping files.

## Acceptance Criteria

1. OpenSearch 2.19.x runs as a single-node Docker service on the `tamma-net` network, accessible to all other services at `opensearch:9200`
2. OpenSearch Dashboards 2.19.x runs as a Docker service, proxied through nginx at `logs.tamma.dev` (HTTPS via Cloudflare origin cert)
3. Serilog in `Tamma.ElsaServer/Program.cs` writes to Console + File + OpenSearch (index pattern `tamma-elsa-{yyyy.MM.dd}`)
4. Serilog in `Tamma.Api/Program.cs` writes to Console + File + OpenSearch (index pattern `tamma-api-dotnet-{yyyy.MM.dd}`)
5. Pino in `packages/observability/src/logger.ts` writes to stdout + OpenSearch (index pattern `tamma-ts-{yyyy.MM.dd}`)
6. An index template named `tamma-logs` is applied to `tamma-*` indices with explicit mappings for: `@timestamp`, `level`, `levelNum`, `service`, `message`, `workflowInstanceId`, `issueNumber`, `sessionId`, `correlationId`, `provider`, `model`, `durationMs`, `tokenCount`, `errorCode`, `stackTrace`, `host`, `environment`
7. An ISM (Index State Management) policy named `tamma-log-retention` automatically deletes indices older than 30 days and transitions to `warm` storage after 7 days (force-merge to 1 segment)
8. Pre-built OpenSearch Dashboards saved objects include: (a) Errors by Service bar chart, (b) Workflow Execution Timeline, (c) LLM Call Latency histogram, (d) Tool Execution Duration, (e) Log Volume over Time
9. A monitor named `tamma-error-spike` fires an alert when any service logs more than 50 ERROR-level messages in a 5-minute window
10. A monitor named `tamma-workflow-failure` fires an alert when `workflowInstanceId` appears with `level:ERROR` and message contains "workflow failed" or "unhandled exception"
11. OpenSearch security plugin is disabled (internal-only access behind nginx; no internet exposure)
12. OpenSearch JVM heap is capped at 4 GB; Dashboards Node.js heap is capped at 1.5 GB
13. All log sinks are non-blocking: if OpenSearch is unreachable, logs continue to stdout/file without error propagation to the application
14. Correlation ID fields (`workflowInstanceId`, `sessionId`, `issueNumber`) are enriched automatically in C# via `Serilog.Context.LogContext.PushProperty` and in TypeScript via pino child logger bindings
15. The `docker/.env.example` file documents all new environment variables (`OPENSEARCH_URL`, `OPENSEARCH_ENABLED`, `LOG_INDEX_PREFIX`)
16. Health check for OpenSearch service uses the `_cluster/health` endpoint
17. The nginx `logs.tamma.dev` location requires Cloudflare-authenticated access (no public anonymous access to Dashboards)

## Technical Context

### Files to Create

| File | Purpose |
|------|---------|
| `docker/opensearch/index-template.json` | Index template with field mappings for all Tamma structured log fields |
| `docker/opensearch/ism-policy.json` | ISM retention policy (7d warm, 30d delete) |
| `docker/opensearch/dashboards-saved-objects.ndjson` | Pre-built visualizations and dashboard |
| `docker/opensearch/setup.sh` | Bootstrap script: applies index template, ISM policy, and imports saved objects after OpenSearch starts |

### Files to Modify

| File | Change |
|------|--------|
| `docker/docker-compose.yml` | Add `opensearch` and `opensearch-dashboards` services, `tamma-os-data` volume |
| `docker/docker-compose.prod.yml` | Add resource limits for OpenSearch (4 GB) and Dashboards (1.5 GB) |
| `docker/nginx-proxy.conf` | Add `logs.tamma.dev` server block proxying to OpenSearch Dashboards |
| `docker/.env.example` | Add `OPENSEARCH_URL`, `OPENSEARCH_ENABLED`, `LOG_INDEX_PREFIX` |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Add `Serilog.Sinks.Elasticsearch` sink configuration |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Tamma.ElsaServer.csproj` | Add `Serilog.Sinks.Elasticsearch` NuGet package |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Add `Serilog.Sinks.Elasticsearch` sink configuration |
| `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj` | Add `Serilog.Sinks.Elasticsearch` NuGet package |
| `packages/observability/src/logger.ts` | Add `pino-elasticsearch` transport (multi-stream) |
| `packages/observability/package.json` | Add `pino-elasticsearch` dependency |

### Docker Services to Add

**opensearch** (single-node):
- Image: `opensearchproject/opensearch:2.19.0`
- Environment: single-node discovery, security disabled, JVM heap 4 GB
- Volume: `tamma-os-data` for persistent index storage
- Healthcheck: `curl -f http://localhost:9200/_cluster/health`
- Network: `tamma-net`

**opensearch-dashboards**:
- Image: `opensearchproject/opensearch-dashboards:2.19.0`
- Environment: points to `opensearch:9200`, security disabled
- Healthcheck: `curl -f http://localhost:5601/api/status`
- Network: `tamma-net`
- Depends on: `opensearch` healthy

**opensearch-setup** (init container):
- Image: `curlimages/curl:8.12.1`
- Runs `setup.sh` to apply index template, ISM policy, import saved objects
- Depends on: `opensearch` healthy
- Restart: `no` (one-shot)

## Implementation Notes

### 1. Docker Compose Additions

```yaml
# In docker/docker-compose.yml — add under services:

  opensearch:
    image: opensearchproject/opensearch:2.19.0
    environment:
      discovery.type: single-node
      OPENSEARCH_JAVA_OPTS: "-Xms4g -Xmx4g"
      DISABLE_SECURITY_PLUGIN: "true"
      DISABLE_INSTALL_DEMO_CONFIG: "true"
      cluster.name: tamma-logs
      node.name: tamma-os-node1
      bootstrap.memory_lock: "true"
    ulimits:
      memlock:
        soft: -1
        hard: -1
      nofile:
        soft: 65536
        hard: 65536
    volumes:
      - tamma-os-data:/usr/share/opensearch/data
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:9200/_cluster/health | grep -qE '\"status\":\"(green|yellow)\"'"]
      interval: 15s
      timeout: 10s
      start_period: 60s
      retries: 5
    networks:
      - tamma-net

  opensearch-dashboards:
    image: opensearchproject/opensearch-dashboards:2.19.0
    environment:
      OPENSEARCH_HOSTS: '["http://opensearch:9200"]'
      DISABLE_SECURITY_DASHBOARDS_PLUGIN: "true"
      SERVER_BASEPATH: ""
      SERVER_REWRITEBASEPATH: "true"
    depends_on:
      opensearch:
        condition: service_healthy
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:5601/api/status | grep -q '\"overall\"'"]
      interval: 15s
      timeout: 10s
      start_period: 45s
      retries: 5
    networks:
      - tamma-net

  opensearch-setup:
    image: curlimages/curl:8.12.1
    volumes:
      - ./opensearch:/setup:ro
    entrypoint: ["sh", "/setup/setup.sh"]
    depends_on:
      opensearch:
        condition: service_healthy
    restart: "no"
    networks:
      - tamma-net

# Add to volumes:
  tamma-os-data:
```

### 2. Serilog OpenSearch Sink (C#)

The `Serilog.Sinks.Elasticsearch` package (v10.0.0) supports OpenSearch via its `TypedConnectionPool`. The sink uses the Elasticsearch bulk API which OpenSearch is fully compatible with.

**NuGet package** (add to both .csproj files):
```xml
<PackageReference Include="Serilog.Sinks.Elasticsearch" Version="10.0.0" />
```

**Program.cs pattern** (both ElsaServer and Tamma.Api):
```csharp
using Serilog.Sinks.Elasticsearch;

var opensearchUrl = builder.Configuration["OpenSearch:Url"] ?? "http://opensearch:9200";
var opensearchEnabled = builder.Configuration.GetValue<bool>("OpenSearch:Enabled", true);

var logConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "tamma-elsa") // or "tamma-api-dotnet"
    .Enrich.WithProperty("environment", builder.Environment.EnvironmentName)
    .WriteTo.Console()
    .WriteTo.File("logs/tamma-elsa-.log", rollingInterval: RollingInterval.Day);

if (opensearchEnabled)
{
    logConfig.WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(opensearchUrl))
    {
        AutoRegisterTemplate = false, // We manage templates externally
        IndexFormat = "tamma-elsa-{0:yyyy.MM.dd}",
        BatchAction = ElasticOpType.Create,
        ModifyConnectionSettings = conn => conn.ServerCertificateValidationCallback(
            (_, _, _, _) => true), // Internal network, no TLS
        EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog,
        FailureCallback = e => Console.Error.WriteLine(
            $"[Serilog-OpenSearch] Failed to index log event: {e.MessageTemplate}"),
        BufferBaseFilename = "./logs/opensearch-buffer",
        BufferFileSizeLimitBytes = 50_000_000, // 50 MB buffer
        Period = TimeSpan.FromSeconds(2),
        BatchPostingLimit = 500,
    });
}

Log.Logger = logConfig.CreateLogger();
```

**Correlation ID enrichment** — push properties in activity execution context:
```csharp
using (LogContext.PushProperty("workflowInstanceId", workflowInstanceId))
using (LogContext.PushProperty("issueNumber", issueNumber))
using (LogContext.PushProperty("sessionId", sessionId))
{
    _logger.LogInformation("Activity started: {ActivityName}", activityName);
    // ... activity execution ...
}
```

### 3. Pino OpenSearch Transport (TypeScript)

The `pino-elasticsearch` package sends logs directly to OpenSearch using the bulk API. Use `pino.multistream` to write to both stdout and OpenSearch simultaneously.

**package.json addition**:
```json
{
  "dependencies": {
    "pino-elasticsearch": "^8.0.0"
  }
}
```

**logger.ts updated pattern**:
```typescript
import pino, { type DestinationStream, type LoggerOptions } from 'pino';
import type { ILogger } from '@tamma/shared';

interface OpenSearchConfig {
  node: string;
  index: string;
  enabled: boolean;
  flushBytes?: number;
  flushInterval?: number;
}

function getOpenSearchConfig(): OpenSearchConfig {
  return {
    node: process.env['OPENSEARCH_URL'] ?? 'http://opensearch:9200',
    index: process.env['LOG_INDEX_PREFIX'] ?? 'tamma-ts',
    enabled: process.env['OPENSEARCH_ENABLED'] !== 'false',
    flushBytes: 1000,
    flushInterval: 5000,
  };
}

function createOpenSearchStream(config: OpenSearchConfig): DestinationStream | undefined {
  if (!config.enabled) return undefined;

  try {
    // Dynamic import to avoid hard dependency when disabled
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const pinoElasticsearch = require('pino-elasticsearch');
    return pinoElasticsearch({
      node: config.node,
      index: config.index,
      flushBytes: config.flushBytes,
      flushInterval: config.flushInterval,
      esVersion: 7, // OpenSearch uses ES 7.x compatible API
      op_type: 'create',
    });
  } catch {
    process.stderr.write(
      '[tamma-logger] pino-elasticsearch unavailable, logging to stdout only\n'
    );
    return undefined;
  }
}

export function createLogger(name: string, level?: string): ILogger {
  const options: LoggerOptions = {
    name,
    level: level ?? process.env['LOG_LEVEL'] ?? 'info',
  };

  const osConfig = getOpenSearchConfig();
  const osStream = createOpenSearchStream(osConfig);

  let pinoLogger: pino.Logger;

  if (osStream) {
    const multistream = pino.multistream([
      { stream: process.stdout },
      { stream: osStream },
    ]);
    pinoLogger = pino(options, multistream);
  } else if (process.env['NODE_ENV'] !== 'production') {
    options.transport = { target: 'pino-pretty', options: { colorize: true } };
    pinoLogger = pino(options);
  } else {
    pinoLogger = pino(options);
  }

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
      const childPino = pinoLogger.child(childContext);
      return {
        debug(message: string, ctx?: Record<string, unknown>): void {
          if (ctx !== undefined) childPino.debug(ctx, message);
          else childPino.debug(message);
        },
        info(message: string, ctx?: Record<string, unknown>): void {
          if (ctx !== undefined) childPino.info(ctx, message);
          else childPino.info(message);
        },
        warn(message: string, ctx?: Record<string, unknown>): void {
          if (ctx !== undefined) childPino.warn(ctx, message);
          else childPino.warn(message);
        },
        error(message: string, ctx?: Record<string, unknown>): void {
          if (ctx !== undefined) childPino.error(ctx, message);
          else childPino.error(message);
        },
        child(nestedCtx: Record<string, unknown>): ILogger {
          // Recursive — child of child works
          return createLogger(name, level);
        },
      };
    },
  };
}
```

**Correlation ID usage** in TypeScript:
```typescript
const logger = createLogger('tamma-engine');
const scopedLogger = logger.child({
  workflowInstanceId: 'wf-abc123',
  issueNumber: 42,
  sessionId: 'sess-xyz',
});
scopedLogger.info('Processing issue'); // All child fields appear in OpenSearch
```

### 4. Index Template

The index template maps all Tamma-specific fields with correct types (keyword for filtering, text for full-text search, date for timestamps, integer for numeric aggregations).

See implementation plan for the complete JSON.

### 5. ISM Retention Policy

```json
{
  "policy": {
    "description": "Tamma log retention: warm after 7d, delete after 30d",
    "default_state": "hot",
    "states": [
      {
        "name": "hot",
        "actions": [],
        "transitions": [{ "state_name": "warm", "conditions": { "min_index_age": "7d" } }]
      },
      {
        "name": "warm",
        "actions": [{ "force_merge": { "max_num_segments": 1 } }, { "read_only": {} }],
        "transitions": [{ "state_name": "delete", "conditions": { "min_index_age": "30d" } }]
      },
      {
        "name": "delete",
        "actions": [{ "delete": {} }],
        "transitions": []
      }
    ],
    "ism_template": [{ "index_patterns": ["tamma-*"], "priority": 100 }]
  }
}
```

### 6. nginx Proxy for Dashboards

Add a new server block for `logs.tamma.dev`:

```nginx
# logs.tamma.dev — OpenSearch Dashboards (internal access only)
server {
    listen 443 ssl;
    server_name logs.tamma.dev;

    # Cloudflare origin cert (same as other services)
    ssl_certificate /etc/nginx/certs/origin-cert.pem;
    ssl_certificate_key /etc/nginx/certs/origin-key.pem;

    location / {
        proxy_pass http://opensearch-dashboards:5601;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_buffer_size 128k;
        proxy_buffers 4 256k;
        proxy_busy_buffers_size 256k;
    }
}
```

### 7. CI/CD Integration

- Docker image build: No change (OpenSearch/Dashboards use official images)
- Deployment script: Run `docker compose up -d opensearch opensearch-dashboards opensearch-setup` after other services
- Smoke test: `curl -sf http://localhost:9200/_cluster/health` must return `green` or `yellow`
- Dashboard import: `opensearch-setup` container runs once on deploy, idempotent

## Logging Requirements

This story itself introduces the logging infrastructure. Each log sink must:

- **C# Serilog sink**: Log at DEBUG when batch is sent, WARN when batch fails, ERROR when buffer is full
- **TS Pino transport**: Log connection errors to stderr (not to the OpenSearch transport itself, to avoid infinite loop)
- **Setup script**: Log each step (template applied, ISM policy applied, saved objects imported)

## Testing Strategy

### Manual Verification

1. `docker compose up -d` and wait for all health checks to pass
2. Trigger a workflow execution via ELSA Studio
3. Open `https://logs.tamma.dev` and verify:
   - Indices `tamma-elsa-*`, `tamma-api-dotnet-*`, `tamma-ts-*` exist
   - Logs from all services appear with correct `service` field
   - `workflowInstanceId` field is searchable and filterable
   - Pre-built dashboards load and display data
4. Stop the `opensearch` container and verify applications continue running (logs go to stdout/file only)
5. Restart `opensearch` and verify logs resume flowing (buffered events delivered)

### Automated Checks

- `curl -sf http://opensearch:9200/_cat/indices?v` returns `tamma-*` indices
- `curl -sf http://opensearch:9200/_index_template/tamma-logs` returns the template
- `curl -sf http://opensearch:9200/_plugins/_ism/policies/tamma-log-retention` returns the ISM policy
- `curl -sf http://opensearch:9200/tamma-elsa-*/_count` returns count > 0 after ELSA activity execution
- `curl -sf http://opensearch:9200/tamma-ts-*/_count` returns count > 0 after TS API request

### Integration Test (Optional)

A Vitest integration test in `packages/observability/src/logger.integration.test.ts` that:
1. Creates a logger with `OPENSEARCH_URL=http://localhost:9200`
2. Writes 10 log entries
3. Waits 6 seconds (flush interval)
4. Queries OpenSearch for the entries
5. Asserts all 10 are present with correct fields

## Dependencies

- No story dependencies (this is a foundation story)
- External: Docker, nginx, Cloudflare DNS for `logs.tamma.dev`
- NuGet: `Serilog.Sinks.Elasticsearch` 10.0.0
- npm: `pino-elasticsearch` ^8.0.0

## Estimated Effort

| Task | Hours |
|------|-------|
| Docker Compose services + healthchecks | 2 |
| Index template + ISM policy + setup script | 2 |
| Serilog sink (ElsaServer + Tamma.Api) | 2 |
| Pino transport (observability package) | 3 |
| nginx proxy configuration | 1 |
| Dashboards saved objects (5 visualizations + 1 dashboard) | 3 |
| Alerting monitors (2) | 1 |
| Production resource limits | 0.5 |
| Testing + verification | 2.5 |
| Documentation (.env.example, README) | 1 |
| **Total** | **18 hours** |

---

**Last Updated**: 2026-03-28
**Story Owner**: Platform Engineering
