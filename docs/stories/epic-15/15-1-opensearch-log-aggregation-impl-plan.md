# Story 15.1: OpenSearch Log Aggregation — Implementation Plan

## Overview

Deploy OpenSearch + OpenSearch Dashboards in Docker Compose, wire Serilog (C#) and Pino (TypeScript) to ship structured logs, create index templates with proper field mappings, configure 30-day retention, build dashboards, and set up alerting.

---

## Step-by-Step Implementation Tasks

### Task 1: Create OpenSearch Configuration Files

#### 1a. Index Template

**File to create:** `docker/opensearch/index-template.json`

This template is applied to all `tamma-*` indices. It defines explicit field mappings so OpenSearch does not auto-detect types (which often gets them wrong -- e.g., treating `durationMs` as text).

```json
{
  "index_patterns": ["tamma-*"],
  "template": {
    "settings": {
      "number_of_shards": 1,
      "number_of_replicas": 0,
      "index.refresh_interval": "5s",
      "index.codec": "best_compression",
      "index.mapping.total_fields.limit": 2000
    },
    "mappings": {
      "dynamic": "true",
      "dynamic_templates": [
        {
          "strings_as_keywords": {
            "match_mapping_type": "string",
            "mapping": {
              "type": "keyword",
              "ignore_above": 1024
            }
          }
        }
      ],
      "properties": {
        "@timestamp": {
          "type": "date",
          "format": "strict_date_optional_time||epoch_millis"
        },
        "level": {
          "type": "keyword"
        },
        "levelNum": {
          "type": "integer"
        },
        "message": {
          "type": "text",
          "fields": {
            "keyword": {
              "type": "keyword",
              "ignore_above": 4096
            }
          }
        },
        "messageTemplate": {
          "type": "text",
          "fields": {
            "keyword": {
              "type": "keyword",
              "ignore_above": 2048
            }
          }
        },
        "service": {
          "type": "keyword"
        },
        "environment": {
          "type": "keyword"
        },
        "host": {
          "type": "keyword"
        },
        "pid": {
          "type": "integer"
        },
        "name": {
          "type": "keyword"
        },
        "workflowInstanceId": {
          "type": "keyword"
        },
        "workflowDefinitionId": {
          "type": "keyword"
        },
        "workflowName": {
          "type": "keyword"
        },
        "activityId": {
          "type": "keyword"
        },
        "activityName": {
          "type": "keyword"
        },
        "activityType": {
          "type": "keyword"
        },
        "issueNumber": {
          "type": "integer"
        },
        "issueId": {
          "type": "keyword"
        },
        "sessionId": {
          "type": "keyword"
        },
        "correlationId": {
          "type": "keyword"
        },
        "provider": {
          "type": "keyword"
        },
        "model": {
          "type": "keyword"
        },
        "durationMs": {
          "type": "long"
        },
        "tokenCount": {
          "type": "integer"
        },
        "inputTokens": {
          "type": "integer"
        },
        "outputTokens": {
          "type": "integer"
        },
        "costUsd": {
          "type": "float"
        },
        "toolName": {
          "type": "keyword"
        },
        "toolCallId": {
          "type": "keyword"
        },
        "errorCode": {
          "type": "keyword"
        },
        "errorMessage": {
          "type": "text",
          "fields": {
            "keyword": {
              "type": "keyword",
              "ignore_above": 2048
            }
          }
        },
        "stackTrace": {
          "type": "text",
          "index": false
        },
        "exception": {
          "type": "object",
          "properties": {
            "type": { "type": "keyword" },
            "message": { "type": "text" },
            "stackTrace": { "type": "text", "index": false },
            "innerException": { "type": "text", "index": false }
          }
        },
        "requestId": {
          "type": "keyword"
        },
        "httpMethod": {
          "type": "keyword"
        },
        "httpPath": {
          "type": "keyword"
        },
        "httpStatusCode": {
          "type": "integer"
        },
        "userAgent": {
          "type": "keyword",
          "ignore_above": 512
        },
        "clientIp": {
          "type": "ip"
        },
        "repository": {
          "type": "keyword"
        },
        "branch": {
          "type": "keyword"
        },
        "commitSha": {
          "type": "keyword"
        },
        "prNumber": {
          "type": "integer"
        },
        "gitPlatform": {
          "type": "keyword"
        },
        "retryAttempt": {
          "type": "integer"
        },
        "circuitBreakerState": {
          "type": "keyword"
        },
        "budgetRemainingUsd": {
          "type": "float"
        },
        "fields": {
          "type": "object",
          "enabled": true
        }
      }
    }
  },
  "priority": 200,
  "composed_of": [],
  "version": 1,
  "_meta": {
    "description": "Tamma platform structured log template",
    "created_by": "tamma-opensearch-setup"
  }
}
```

#### 1b. ISM Retention Policy

**File to create:** `docker/opensearch/ism-policy.json`

```json
{
  "policy": {
    "description": "Tamma log retention: warm after 7 days (force-merge, read-only), delete after 30 days",
    "default_state": "hot",
    "states": [
      {
        "name": "hot",
        "actions": [],
        "transitions": [
          {
            "state_name": "warm",
            "conditions": {
              "min_index_age": "7d"
            }
          }
        ]
      },
      {
        "name": "warm",
        "actions": [
          {
            "force_merge": {
              "max_num_segments": 1
            }
          },
          {
            "read_only": {}
          }
        ],
        "transitions": [
          {
            "state_name": "delete",
            "conditions": {
              "min_index_age": "30d"
            }
          }
        ]
      },
      {
        "name": "delete",
        "actions": [
          {
            "delete": {}
          }
        ],
        "transitions": []
      }
    ],
    "ism_template": [
      {
        "index_patterns": ["tamma-*"],
        "priority": 100
      }
    ]
  }
}
```

#### 1c. Setup Script

**File to create:** `docker/opensearch/setup.sh`

```bash
#!/bin/sh
# =============================================================================
# OpenSearch Bootstrap Script
#
# Applies index template, ISM policy, and imports Dashboards saved objects.
# Designed to be idempotent — safe to re-run on every deploy.
# =============================================================================

set -e

OPENSEARCH_URL="${OPENSEARCH_URL:-http://opensearch:9200}"
DASHBOARDS_URL="${DASHBOARDS_URL:-http://opensearch-dashboards:5601}"
SETUP_DIR="/setup"

echo "[opensearch-setup] Waiting for OpenSearch to be ready..."
until curl -sf "${OPENSEARCH_URL}/_cluster/health" > /dev/null 2>&1; do
  echo "[opensearch-setup] OpenSearch not ready, retrying in 5s..."
  sleep 5
done
echo "[opensearch-setup] OpenSearch is ready."

# ---- 1. Apply index template ----
echo "[opensearch-setup] Applying index template 'tamma-logs'..."
HTTP_CODE=$(curl -sf -o /dev/null -w "%{http_code}" \
  -X PUT "${OPENSEARCH_URL}/_index_template/tamma-logs" \
  -H "Content-Type: application/json" \
  -d @"${SETUP_DIR}/index-template.json")

if [ "$HTTP_CODE" = "200" ]; then
  echo "[opensearch-setup] Index template 'tamma-logs' applied successfully."
else
  echo "[opensearch-setup] WARNING: Index template returned HTTP ${HTTP_CODE}"
fi

# ---- 2. Apply ISM retention policy ----
echo "[opensearch-setup] Applying ISM policy 'tamma-log-retention'..."

# Check if policy already exists
EXISTING=$(curl -sf -o /dev/null -w "%{http_code}" \
  "${OPENSEARCH_URL}/_plugins/_ism/policies/tamma-log-retention")

if [ "$EXISTING" = "200" ]; then
  # Update existing policy (requires seq_no and primary_term)
  SEQ_INFO=$(curl -sf "${OPENSEARCH_URL}/_plugins/_ism/policies/tamma-log-retention")
  SEQ_NO=$(echo "$SEQ_INFO" | sed -n 's/.*"_seq_no":\([0-9]*\).*/\1/p')
  PRIMARY_TERM=$(echo "$SEQ_INFO" | sed -n 's/.*"_primary_term":\([0-9]*\).*/\1/p')

  HTTP_CODE=$(curl -sf -o /dev/null -w "%{http_code}" \
    -X PUT "${OPENSEARCH_URL}/_plugins/_ism/policies/tamma-log-retention?if_seq_no=${SEQ_NO}&if_primary_term=${PRIMARY_TERM}" \
    -H "Content-Type: application/json" \
    -d @"${SETUP_DIR}/ism-policy.json")
  echo "[opensearch-setup] ISM policy updated (HTTP ${HTTP_CODE})."
else
  HTTP_CODE=$(curl -sf -o /dev/null -w "%{http_code}" \
    -X PUT "${OPENSEARCH_URL}/_plugins/_ism/policies/tamma-log-retention" \
    -H "Content-Type: application/json" \
    -d @"${SETUP_DIR}/ism-policy.json")
  echo "[opensearch-setup] ISM policy created (HTTP ${HTTP_CODE})."
fi

# ---- 3. Wait for Dashboards and import saved objects ----
if [ -f "${SETUP_DIR}/dashboards-saved-objects.ndjson" ]; then
  echo "[opensearch-setup] Waiting for OpenSearch Dashboards to be ready..."
  RETRIES=0
  until curl -sf "${DASHBOARDS_URL}/api/status" > /dev/null 2>&1; do
    RETRIES=$((RETRIES + 1))
    if [ "$RETRIES" -gt 60 ]; then
      echo "[opensearch-setup] WARNING: Dashboards not ready after 5 minutes, skipping saved objects import."
      exit 0
    fi
    sleep 5
  done
  echo "[opensearch-setup] Dashboards is ready."

  echo "[opensearch-setup] Importing saved objects..."
  HTTP_CODE=$(curl -sf -o /dev/null -w "%{http_code}" \
    -X POST "${DASHBOARDS_URL}/api/saved_objects/_import?overwrite=true" \
    -H "osd-xsrf: true" \
    --form file=@"${SETUP_DIR}/dashboards-saved-objects.ndjson")

  if [ "$HTTP_CODE" = "200" ]; then
    echo "[opensearch-setup] Saved objects imported successfully."
  else
    echo "[opensearch-setup] WARNING: Saved objects import returned HTTP ${HTTP_CODE}"
  fi
else
  echo "[opensearch-setup] No saved objects file found, skipping import."
fi

echo "[opensearch-setup] Bootstrap complete."
```

#### 1d. Dashboards Saved Objects (NDJSON)

**File to create:** `docker/opensearch/dashboards-saved-objects.ndjson`

Each line is a separate JSON object. This file includes an index pattern, 5 visualizations, and 1 dashboard.

```ndjson
{"id":"tamma-logs-*","type":"index-pattern","attributes":{"title":"tamma-*","timeFieldName":"@timestamp","fields":"[]"},"references":[]}
{"id":"vis-errors-by-service","type":"visualization","attributes":{"title":"Errors by Service","visState":"{\"title\":\"Errors by Service\",\"type\":\"histogram\",\"aggs\":[{\"id\":\"1\",\"enabled\":true,\"type\":\"count\",\"params\":{},\"schema\":\"metric\"},{\"id\":\"2\",\"enabled\":true,\"type\":\"terms\",\"params\":{\"field\":\"service\",\"orderBy\":\"1\",\"order\":\"desc\",\"size\":20},\"schema\":\"segment\"},{\"id\":\"3\",\"enabled\":true,\"type\":\"date_histogram\",\"params\":{\"field\":\"@timestamp\",\"calendar_interval\":\"1h\",\"min_doc_count\":1},\"schema\":\"group\"}],\"params\":{\"type\":\"histogram\",\"grid\":{\"categoryLines\":false},\"categoryAxes\":[{\"id\":\"CategoryAxis-1\",\"type\":\"category\",\"position\":\"bottom\"}],\"valueAxes\":[{\"id\":\"ValueAxis-1\",\"name\":\"LeftAxis-1\",\"type\":\"value\",\"position\":\"left\"}],\"seriesParams\":[{\"show\":true,\"type\":\"histogram\",\"mode\":\"stacked\",\"valueAxis\":\"ValueAxis-1\",\"data\":{\"label\":\"Count\",\"id\":\"1\"}}]}}","uiStateJSON":"{}","description":"Error count per service over time","kibanaSavedObjectMeta":{"searchSourceJSON":"{\"index\":\"tamma-logs-*\",\"query\":{\"query\":\"level:ERROR OR level:error OR levelNum:>=50\",\"language\":\"kuery\"},\"filter\":[]}"}},"references":[{"id":"tamma-logs-*","name":"kibanaSavedObjectMeta.searchSourceJSON.index","type":"index-pattern"}]}
{"id":"vis-workflow-timeline","type":"visualization","attributes":{"title":"Workflow Execution Timeline","visState":"{\"title\":\"Workflow Execution Timeline\",\"type\":\"line\",\"aggs\":[{\"id\":\"1\",\"enabled\":true,\"type\":\"avg\",\"params\":{\"field\":\"durationMs\"},\"schema\":\"metric\"},{\"id\":\"2\",\"enabled\":true,\"type\":\"date_histogram\",\"params\":{\"field\":\"@timestamp\",\"calendar_interval\":\"1h\",\"min_doc_count\":0},\"schema\":\"segment\"},{\"id\":\"3\",\"enabled\":true,\"type\":\"terms\",\"params\":{\"field\":\"workflowName\",\"orderBy\":\"1\",\"order\":\"desc\",\"size\":10},\"schema\":\"group\"}],\"params\":{\"type\":\"line\",\"grid\":{\"categoryLines\":false},\"categoryAxes\":[{\"id\":\"CategoryAxis-1\",\"type\":\"category\",\"position\":\"bottom\"}],\"valueAxes\":[{\"id\":\"ValueAxis-1\",\"name\":\"LeftAxis-1\",\"type\":\"value\",\"position\":\"left\",\"title\":{\"text\":\"Avg Duration (ms)\"}}],\"seriesParams\":[{\"show\":true,\"type\":\"line\",\"mode\":\"normal\",\"valueAxis\":\"ValueAxis-1\",\"data\":{\"label\":\"Avg Duration (ms)\",\"id\":\"1\"}}]}}","uiStateJSON":"{}","description":"Average workflow execution duration over time, grouped by workflow name","kibanaSavedObjectMeta":{"searchSourceJSON":"{\"index\":\"tamma-logs-*\",\"query\":{\"query\":\"workflowInstanceId:* AND durationMs:>0\",\"language\":\"kuery\"},\"filter\":[]}"}},"references":[{"id":"tamma-logs-*","name":"kibanaSavedObjectMeta.searchSourceJSON.index","type":"index-pattern"}]}
{"id":"vis-llm-latency","type":"visualization","attributes":{"title":"LLM Call Latency","visState":"{\"title\":\"LLM Call Latency\",\"type\":\"histogram\",\"aggs\":[{\"id\":\"1\",\"enabled\":true,\"type\":\"count\",\"params\":{},\"schema\":\"metric\"},{\"id\":\"2\",\"enabled\":true,\"type\":\"histogram\",\"params\":{\"field\":\"durationMs\",\"interval\":500,\"min_doc_count\":1},\"schema\":\"segment\"},{\"id\":\"3\",\"enabled\":true,\"type\":\"terms\",\"params\":{\"field\":\"provider\",\"orderBy\":\"1\",\"order\":\"desc\",\"size\":10},\"schema\":\"group\"}],\"params\":{\"type\":\"histogram\",\"grid\":{\"categoryLines\":false},\"categoryAxes\":[{\"id\":\"CategoryAxis-1\",\"type\":\"category\",\"position\":\"bottom\",\"title\":{\"text\":\"Duration (ms)\"}}],\"valueAxes\":[{\"id\":\"ValueAxis-1\",\"name\":\"LeftAxis-1\",\"type\":\"value\",\"position\":\"left\",\"title\":{\"text\":\"Call Count\"}}],\"seriesParams\":[{\"show\":true,\"type\":\"histogram\",\"mode\":\"stacked\",\"valueAxis\":\"ValueAxis-1\",\"data\":{\"label\":\"Count\",\"id\":\"1\"}}]}}","uiStateJSON":"{}","description":"Distribution of LLM call latency, grouped by provider","kibanaSavedObjectMeta":{"searchSourceJSON":"{\"index\":\"tamma-logs-*\",\"query\":{\"query\":\"provider:* AND durationMs:>0 AND (message:\\\"LLM call\\\" OR message:\\\"CallLlm\\\" OR activityType:\\\"CallLlmActivity\\\" OR activityType:\\\"CallLlmInlineActivity\\\")\",\"language\":\"kuery\"},\"filter\":[]}"}},"references":[{"id":"tamma-logs-*","name":"kibanaSavedObjectMeta.searchSourceJSON.index","type":"index-pattern"}]}
{"id":"vis-tool-execution","type":"visualization","attributes":{"title":"Tool Execution Duration","visState":"{\"title\":\"Tool Execution Duration\",\"type\":\"horizontal_bar\",\"aggs\":[{\"id\":\"1\",\"enabled\":true,\"type\":\"avg\",\"params\":{\"field\":\"durationMs\"},\"schema\":\"metric\"},{\"id\":\"2\",\"enabled\":true,\"type\":\"terms\",\"params\":{\"field\":\"toolName\",\"orderBy\":\"1\",\"order\":\"desc\",\"size\":25},\"schema\":\"segment\"}],\"params\":{\"type\":\"horizontal_bar\",\"grid\":{\"categoryLines\":false},\"categoryAxes\":[{\"id\":\"CategoryAxis-1\",\"type\":\"category\",\"position\":\"left\"}],\"valueAxes\":[{\"id\":\"ValueAxis-1\",\"name\":\"BottomAxis-1\",\"type\":\"value\",\"position\":\"bottom\",\"title\":{\"text\":\"Avg Duration (ms)\"}}],\"seriesParams\":[{\"show\":true,\"type\":\"histogram\",\"mode\":\"normal\",\"valueAxis\":\"ValueAxis-1\",\"data\":{\"label\":\"Avg Duration (ms)\",\"id\":\"1\"}}]}}","uiStateJSON":"{}","description":"Average tool execution duration by tool name","kibanaSavedObjectMeta":{"searchSourceJSON":"{\"index\":\"tamma-logs-*\",\"query\":{\"query\":\"toolName:* AND durationMs:>0\",\"language\":\"kuery\"},\"filter\":[]}"}},"references":[{"id":"tamma-logs-*","name":"kibanaSavedObjectMeta.searchSourceJSON.index","type":"index-pattern"}]}
{"id":"vis-log-volume","type":"visualization","attributes":{"title":"Log Volume Over Time","visState":"{\"title\":\"Log Volume Over Time\",\"type\":\"area\",\"aggs\":[{\"id\":\"1\",\"enabled\":true,\"type\":\"count\",\"params\":{},\"schema\":\"metric\"},{\"id\":\"2\",\"enabled\":true,\"type\":\"date_histogram\",\"params\":{\"field\":\"@timestamp\",\"calendar_interval\":\"10m\",\"min_doc_count\":0},\"schema\":\"segment\"},{\"id\":\"3\",\"enabled\":true,\"type\":\"terms\",\"params\":{\"field\":\"level\",\"orderBy\":\"1\",\"order\":\"desc\",\"size\":5},\"schema\":\"group\"}],\"params\":{\"type\":\"area\",\"grid\":{\"categoryLines\":false},\"categoryAxes\":[{\"id\":\"CategoryAxis-1\",\"type\":\"category\",\"position\":\"bottom\"}],\"valueAxes\":[{\"id\":\"ValueAxis-1\",\"name\":\"LeftAxis-1\",\"type\":\"value\",\"position\":\"left\",\"title\":{\"text\":\"Log Count\"}}],\"seriesParams\":[{\"show\":true,\"type\":\"area\",\"mode\":\"stacked\",\"valueAxis\":\"ValueAxis-1\",\"data\":{\"label\":\"Count\",\"id\":\"1\"}}]}}","uiStateJSON":"{}","description":"Total log volume over time, stacked by level","kibanaSavedObjectMeta":{"searchSourceJSON":"{\"index\":\"tamma-logs-*\",\"query\":{\"query\":\"\",\"language\":\"kuery\"},\"filter\":[]}"}},"references":[{"id":"tamma-logs-*","name":"kibanaSavedObjectMeta.searchSourceJSON.index","type":"index-pattern"}]}
{"id":"dashboard-tamma-overview","type":"dashboard","attributes":{"title":"Tamma Platform Logs Overview","description":"Central dashboard for monitoring all Tamma services","panelsJSON":"[{\"version\":\"2.19.0\",\"type\":\"visualization\",\"gridData\":{\"x\":0,\"y\":0,\"w\":48,\"h\":8,\"i\":\"1\"},\"panelIndex\":\"1\",\"embeddableConfig\":{},\"panelRefName\":\"panel_0\"},{\"version\":\"2.19.0\",\"type\":\"visualization\",\"gridData\":{\"x\":0,\"y\":8,\"w\":24,\"h\":12,\"i\":\"2\"},\"panelIndex\":\"2\",\"embeddableConfig\":{},\"panelRefName\":\"panel_1\"},{\"version\":\"2.19.0\",\"type\":\"visualization\",\"gridData\":{\"x\":24,\"y\":8,\"w\":24,\"h\":12,\"i\":\"3\"},\"panelIndex\":\"3\",\"embeddableConfig\":{},\"panelRefName\":\"panel_2\"},{\"version\":\"2.19.0\",\"type\":\"visualization\",\"gridData\":{\"x\":0,\"y\":20,\"w\":24,\"h\":12,\"i\":\"4\"},\"panelIndex\":\"4\",\"embeddableConfig\":{},\"panelRefName\":\"panel_3\"},{\"version\":\"2.19.0\",\"type\":\"visualization\",\"gridData\":{\"x\":24,\"y\":20,\"w\":24,\"h\":12,\"i\":\"5\"},\"panelIndex\":\"5\",\"embeddableConfig\":{},\"panelRefName\":\"panel_4\"}]","optionsJSON":"{\"useMargins\":true,\"hidePanelTitles\":false}","timeRestore":true,"timeTo":"now","timeFrom":"now-24h","kibanaSavedObjectMeta":{"searchSourceJSON":"{\"query\":{\"query\":\"\",\"language\":\"kuery\"},\"filter\":[]}"}},"references":[{"id":"vis-log-volume","name":"panel_0","type":"visualization"},{"id":"vis-errors-by-service","name":"panel_1","type":"visualization"},{"id":"vis-workflow-timeline","name":"panel_2","type":"visualization"},{"id":"vis-llm-latency","name":"panel_3","type":"visualization"},{"id":"vis-tool-execution","name":"panel_4","type":"visualization"}]}
```

---

### Task 2: Modify Docker Compose Files

#### 2a. `docker/docker-compose.yml` — Add Services

Add the following three services after the existing `nginx-proxy` service, before the `volumes:` section.

```yaml
  # ---------------------------------------------------------------------------
  # Infrastructure: OpenSearch (log aggregation)
  # ---------------------------------------------------------------------------
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

  # ---------------------------------------------------------------------------
  # Infrastructure: OpenSearch Dashboards (log visualization)
  # ---------------------------------------------------------------------------
  opensearch-dashboards:
    image: opensearchproject/opensearch-dashboards:2.19.0
    environment:
      OPENSEARCH_HOSTS: '["http://opensearch:9200"]'
      DISABLE_SECURITY_DASHBOARDS_PLUGIN: "true"
      NODE_OPTIONS: "--max-old-space-size=1536"
    depends_on:
      opensearch:
        condition: service_healthy
    healthcheck:
      test: ["CMD-SHELL", "curl -sf http://localhost:5601/api/status | grep -q 'overall'"]
      interval: 15s
      timeout: 10s
      start_period: 45s
      retries: 5
    networks:
      - tamma-net

  # ---------------------------------------------------------------------------
  # Init: OpenSearch bootstrap (index template, ISM policy, saved objects)
  # ---------------------------------------------------------------------------
  opensearch-setup:
    image: curlimages/curl:8.12.1
    volumes:
      - ./opensearch:/setup:ro
    entrypoint: ["sh", "/setup/setup.sh"]
    depends_on:
      opensearch:
        condition: service_healthy
      opensearch-dashboards:
        condition: service_healthy
    restart: "no"
    networks:
      - tamma-net
```

Add to the `volumes:` section:
```yaml
  tamma-os-data:
```

Add `opensearch` dependency to the services that log to it:
- `elsa-server`: add `opensearch: condition: service_started` (not `service_healthy` -- Serilog buffers, so the app can start before OpenSearch is ready)
- `tamma-api-dotnet`: same
- `tamma-api`: same
- `tamma-engine`: same

**Important**: Use `service_started`, not `service_healthy`. The Serilog buffer and pino-elasticsearch retry logic handle OpenSearch startup delay. This avoids delaying application startup.

#### 2b. `docker/docker-compose.prod.yml` — Add Resource Limits

```yaml
  opensearch:
    deploy:
      resources:
        limits:
          cpus: "2.0"
          memory: 5G
    # In production, do not expose port 9200 — internal only

  opensearch-dashboards:
    deploy:
      resources:
        limits:
          cpus: "0.5"
          memory: 2G
    # In production, do not expose port 5601 — accessed via nginx

  opensearch-setup:
    deploy:
      resources:
        limits:
          cpus: "0.25"
          memory: 128M
```

Note: OpenSearch container memory limit is 5 GB (not 4 GB) because the JVM heap is 4 GB but the process needs additional native memory for memory-mapped files, thread stacks, and direct buffers. The 1 GB overhead prevents OOM kills.

---

### Task 3: Configure Serilog OpenSearch Sink (C# — ELSA Server)

#### 3a. Add NuGet Package

**File to modify:** `apps/tamma-elsa/src/Tamma.ElsaServer/Tamma.ElsaServer.csproj`

Add to the `<ItemGroup>` with logging packages:

```xml
    <PackageReference Include="Serilog.Sinks.Elasticsearch" Version="10.0.0" />
```

#### 3b. Modify Program.cs

**File to modify:** `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs`

Replace the Serilog configuration block (lines 12-18) with:

```csharp
using Serilog.Sinks.Elasticsearch;

// ... (after var builder = WebApplication.CreateBuilder(args);)

// Configure Serilog
var opensearchUrl = builder.Configuration["OpenSearch:Url"] ?? "http://opensearch:9200";
var opensearchEnabled = builder.Configuration.GetValue<bool>("OpenSearch:Enabled", true);
var logIndexPrefix = builder.Configuration["OpenSearch:IndexPrefix"] ?? "tamma-elsa";

var logConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "tamma-elsa")
    .Enrich.WithProperty("environment", builder.Environment.EnvironmentName)
    .Enrich.WithMachineName()
    .WriteTo.Console()
    .WriteTo.File("logs/tamma-elsa-.log", rollingInterval: RollingInterval.Day);

if (opensearchEnabled)
{
    logConfig.WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(opensearchUrl))
    {
        AutoRegisterTemplate = false,
        IndexFormat = $"{logIndexPrefix}-{{0:yyyy.MM.dd}}",
        BatchAction = ElasticOpType.Create,
        ModifyConnectionSettings = conn =>
            conn.ServerCertificateValidationCallback((_, _, _, _) => true),
        EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog,
        FailureCallback = e => Console.Error.WriteLine(
            $"[Serilog-OpenSearch] Failed to submit: {e.MessageTemplate}"),
        BufferBaseFilename = "./logs/opensearch-buffer",
        BufferFileSizeLimitBytes = 50_000_000,
        Period = TimeSpan.FromSeconds(2),
        BatchPostingLimit = 500,
    });
    Serilog.Debugging.SelfLog.Enable(Console.Error);
}

Log.Logger = logConfig.CreateLogger();
```

Add the OpenSearch environment variables to `docker-compose.yml` for `elsa-server`:
```yaml
      OpenSearch__Url: http://opensearch:9200
      OpenSearch__Enabled: "true"
      OpenSearch__IndexPrefix: tamma-elsa
```

---

### Task 4: Configure Serilog OpenSearch Sink (C# — Tamma.Api)

#### 4a. Add NuGet Package

**File to modify:** `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj`

Add to the `<ItemGroup>`:

```xml
    <PackageReference Include="Serilog.Sinks.Elasticsearch" Version="10.0.0" />
```

#### 4b. Modify Program.cs

**File to modify:** `apps/tamma-elsa/src/Tamma.Api/Program.cs`

Replace the Serilog configuration block (lines 10-16) with:

```csharp
using Serilog.Sinks.Elasticsearch;

// ... (after var builder = WebApplication.CreateBuilder(args);)

// Configure Serilog
var opensearchUrl = builder.Configuration["OpenSearch:Url"] ?? "http://opensearch:9200";
var opensearchEnabled = builder.Configuration.GetValue<bool>("OpenSearch:Enabled", true);
var logIndexPrefix = builder.Configuration["OpenSearch:IndexPrefix"] ?? "tamma-api-dotnet";

var logConfig = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "tamma-api-dotnet")
    .Enrich.WithProperty("environment", builder.Environment.EnvironmentName)
    .Enrich.WithMachineName()
    .WriteTo.Console()
    .WriteTo.File("logs/tamma-api-.log", rollingInterval: RollingInterval.Day);

if (opensearchEnabled)
{
    logConfig.WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(opensearchUrl))
    {
        AutoRegisterTemplate = false,
        IndexFormat = $"{logIndexPrefix}-{{0:yyyy.MM.dd}}",
        BatchAction = ElasticOpType.Create,
        ModifyConnectionSettings = conn =>
            conn.ServerCertificateValidationCallback((_, _, _, _) => true),
        EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog,
        FailureCallback = e => Console.Error.WriteLine(
            $"[Serilog-OpenSearch] Failed to submit: {e.MessageTemplate}"),
        BufferBaseFilename = "./logs/opensearch-buffer",
        BufferFileSizeLimitBytes = 50_000_000,
        Period = TimeSpan.FromSeconds(2),
        BatchPostingLimit = 500,
    });
    Serilog.Debugging.SelfLog.Enable(Console.Error);
}

Log.Logger = logConfig.CreateLogger();
```

Add the OpenSearch environment variables to `docker-compose.yml` for `tamma-api-dotnet`:
```yaml
      OpenSearch__Url: http://opensearch:9200
      OpenSearch__Enabled: "true"
      OpenSearch__IndexPrefix: tamma-api-dotnet
```

---

### Task 5: Configure Pino OpenSearch Transport (TypeScript)

#### 5a. Add npm Dependency

**File to modify:** `packages/observability/package.json`

Add to `dependencies`:
```json
    "pino-elasticsearch": "^8.0.0"
```

#### 5b. Rewrite logger.ts

**File to modify:** `packages/observability/src/logger.ts`

Replace the entire file:

```typescript
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
 * Supports child() for creating scoped loggers with bound context.
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
```

#### 5c. Add OpenSearch env vars to Docker Compose

For `tamma-api` and `tamma-engine` services in `docker-compose.yml`, add:

```yaml
      OPENSEARCH_URL: http://opensearch:9200
      OPENSEARCH_ENABLED: "true"
      LOG_INDEX_PREFIX: tamma-ts
      SERVICE_NAME: tamma-api   # or tamma-engine
```

---

### Task 6: Modify nginx Proxy Configuration

**File to modify:** `docker/nginx-proxy.conf`

Add `logs.tamma.dev` to the HTTP redirect block (line 17):
```nginx
    server_name app.tamma.dev api.tamma.dev elsa.tamma.dev logs.tamma.dev;
```

Add a new server block at the end of the file (before the final `}`):

```nginx
# logs.tamma.dev — OpenSearch Dashboards (internal access only via Cloudflare)
server {
    listen 443 ssl;
    server_name logs.tamma.dev;

    location / {
        proxy_pass http://opensearch-dashboards:5601;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";

        # Dashboards can return large responses (saved objects, visualizations)
        proxy_buffer_size 128k;
        proxy_buffers 4 256k;
        proxy_busy_buffers_size 256k;
    }
}
```

Add the `opensearch-dashboards` dependency to nginx-proxy in `docker-compose.yml`:
```yaml
  nginx-proxy:
    depends_on:
      # ... existing deps ...
      opensearch-dashboards:
        condition: service_started
```

---

### Task 7: Update .env.example

**File to modify:** `docker/.env.example`

Add at the end:

```bash
# ===== OpenSearch Log Aggregation =====
# OPENSEARCH_URL=http://opensearch:9200
# OPENSEARCH_ENABLED=true
# LOG_INDEX_PREFIX=tamma-ts
```

---

### Task 8: Configure Alerting Monitors

The alerting monitors are created via the OpenSearch Alerting REST API. Add them to `setup.sh`.

Add the following to `docker/opensearch/setup.sh` before the final "Bootstrap complete" line:

```bash
# ---- 4. Create alerting monitors ----
echo "[opensearch-setup] Creating alerting monitors..."

# Monitor: Error Spike (>50 errors in 5 minutes for any service)
curl -sf -X POST "${OPENSEARCH_URL}/_plugins/_alerting/monitors" \
  -H "Content-Type: application/json" \
  -d '{
  "name": "tamma-error-spike",
  "type": "monitor",
  "monitor_type": "query_level_monitor",
  "enabled": true,
  "schedule": {
    "period": {
      "interval": 5,
      "unit": "MINUTES"
    }
  },
  "inputs": [
    {
      "search": {
        "indices": ["tamma-*"],
        "query": {
          "size": 0,
          "query": {
            "bool": {
              "filter": [
                {
                  "bool": {
                    "should": [
                      { "term": { "level": "ERROR" } },
                      { "term": { "level": "error" } },
                      { "range": { "levelNum": { "gte": 50 } } }
                    ]
                  }
                },
                {
                  "range": {
                    "@timestamp": {
                      "gte": "now-5m",
                      "lte": "now"
                    }
                  }
                }
              ]
            }
          },
          "aggs": {
            "error_count_by_service": {
              "terms": {
                "field": "service",
                "size": 20
              }
            }
          }
        }
      }
    }
  ],
  "triggers": [
    {
      "query_level_trigger": {
        "name": "Error spike detected",
        "severity": "2",
        "condition": {
          "script": {
            "source": "ctx.results[0].hits.total.value > 50",
            "lang": "painless"
          }
        },
        "actions": [
          {
            "name": "Log alert",
            "destination_id": "",
            "message_template": {
              "source": "Error spike: {{ctx.results[0].hits.total.value}} errors in last 5 minutes across tamma-* indices.",
              "lang": "mustache"
            },
            "throttle_enabled": true,
            "throttle": {
              "value": 30,
              "unit": "MINUTES"
            }
          }
        ]
      }
    }
  ]
}' > /dev/null 2>&1 && echo "[opensearch-setup] Monitor 'tamma-error-spike' created." \
  || echo "[opensearch-setup] WARNING: Failed to create error-spike monitor (may already exist)."

# Monitor: Workflow Failure
curl -sf -X POST "${OPENSEARCH_URL}/_plugins/_alerting/monitors" \
  -H "Content-Type: application/json" \
  -d '{
  "name": "tamma-workflow-failure",
  "type": "monitor",
  "monitor_type": "query_level_monitor",
  "enabled": true,
  "schedule": {
    "period": {
      "interval": 2,
      "unit": "MINUTES"
    }
  },
  "inputs": [
    {
      "search": {
        "indices": ["tamma-*"],
        "query": {
          "size": 5,
          "query": {
            "bool": {
              "filter": [
                { "exists": { "field": "workflowInstanceId" } },
                {
                  "bool": {
                    "should": [
                      { "term": { "level": "ERROR" } },
                      { "term": { "level": "error" } },
                      { "range": { "levelNum": { "gte": 50 } } }
                    ]
                  }
                },
                {
                  "bool": {
                    "should": [
                      { "match_phrase": { "message": "workflow failed" } },
                      { "match_phrase": { "message": "unhandled exception" } },
                      { "match_phrase": { "message": "Workflow faulted" } },
                      { "match_phrase": { "message": "activity failed" } }
                    ]
                  }
                },
                {
                  "range": {
                    "@timestamp": {
                      "gte": "now-2m",
                      "lte": "now"
                    }
                  }
                }
              ]
            }
          }
        }
      }
    }
  ],
  "triggers": [
    {
      "query_level_trigger": {
        "name": "Workflow failure detected",
        "severity": "1",
        "condition": {
          "script": {
            "source": "ctx.results[0].hits.total.value > 0",
            "lang": "painless"
          }
        },
        "actions": [
          {
            "name": "Log alert",
            "destination_id": "",
            "message_template": {
              "source": "Workflow failure: {{ctx.results[0].hits.total.value}} failure(s) detected in the last 2 minutes. Check logs for workflowInstanceId details.",
              "lang": "mustache"
            },
            "throttle_enabled": true,
            "throttle": {
              "value": 10,
              "unit": "MINUTES"
            }
          }
        ]
      }
    }
  ]
}' > /dev/null 2>&1 && echo "[opensearch-setup] Monitor 'tamma-workflow-failure' created." \
  || echo "[opensearch-setup] WARNING: Failed to create workflow-failure monitor (may already exist)."
```

Note: The `destination_id` is empty because we are logging alerts to the OpenSearch alert history index. To send alerts to Slack/email, create a destination first and reference its ID. This can be done later via the Dashboards UI.

---

### Task 9: Correlation ID Propagation

#### 9a. C# Side — Serilog LogContext

ELSA activities already have access to `workflowInstanceId` via the execution context. Add enrichment to the common activity execution path.

The `Enrich.FromLogContext()` call in Program.cs means any `LogContext.PushProperty()` call in the request/activity scope is automatically included in all log events sent to all sinks (Console, File, OpenSearch).

**Pattern for existing activities** (already injecting ILogger):
```csharp
// In any Activity's ExecuteAsync method:
using (LogContext.PushProperty("workflowInstanceId", context.WorkflowExecutionContext.Id))
using (LogContext.PushProperty("workflowDefinitionId", context.WorkflowExecutionContext.WorkflowState.DefinitionId))
using (LogContext.PushProperty("activityId", context.Activity.Id))
using (LogContext.PushProperty("activityType", context.Activity.Type))
{
    _logger.LogInformation("Activity {ActivityName} started", context.Activity.Name);
    // ... activity logic ...
}
```

For Tamma.Api HTTP requests, use `UseSerilogRequestLogging()` (already configured) which automatically logs method, path, status code, and duration. To add correlation IDs, configure the enrichment:

```csharp
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        // Pull correlation IDs from request headers
        if (httpContext.Request.Headers.TryGetValue("X-Workflow-Instance-Id", out var wfId))
        {
            diagnosticContext.Set("workflowInstanceId", wfId.ToString());
        }
        if (httpContext.Request.Headers.TryGetValue("X-Session-Id", out var sessId))
        {
            diagnosticContext.Set("sessionId", sessId.ToString());
        }
        diagnosticContext.Set("clientIp", httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    };
});
```

#### 9b. TypeScript Side — Pino Child Loggers

The `child()` method on the ILogger creates a scoped logger. All fields passed to `child()` appear in every subsequent log entry.

```typescript
// In the engine, when processing an issue:
const scopedLogger = logger.child({
  workflowInstanceId: context.workflowInstanceId,
  issueNumber: context.issueNumber,
  sessionId: context.sessionId,
  repository: context.repository,
});

// All logs from this scoped logger include the correlation fields
scopedLogger.info('Starting issue processing');
scopedLogger.info('LLM call completed', { provider: 'anthropic', durationMs: 1234 });
```

#### 9c. Cross-Service Propagation

When the TypeScript API calls the ELSA server (or vice versa), pass correlation IDs as HTTP headers:

```typescript
// TypeScript → ELSA (in packages/orchestrator/src/elsa-client.ts)
const headers: Record<string, string> = {
  'Content-Type': 'application/json',
  'Authorization': `ApiKey ${this.apiKey}`,
};
if (context.workflowInstanceId) {
  headers['X-Workflow-Instance-Id'] = context.workflowInstanceId;
}
if (context.sessionId) {
  headers['X-Session-Id'] = context.sessionId;
}
```

```csharp
// C# → TypeScript (in any HttpClient call)
request.Headers.Add("X-Workflow-Instance-Id", workflowInstanceId);
request.Headers.Add("X-Session-Id", sessionId);
```

---

### Task 10: Update Existing Docker Service Environment Variables

Add OpenSearch connection info to all application services in `docker/docker-compose.yml`:

```yaml
  elsa-server:
    environment:
      # ... existing env vars ...
      OpenSearch__Url: http://opensearch:9200
      OpenSearch__Enabled: "true"
      OpenSearch__IndexPrefix: tamma-elsa

  tamma-api-dotnet:
    environment:
      # ... existing env vars ...
      OpenSearch__Url: http://opensearch:9200
      OpenSearch__Enabled: "true"
      OpenSearch__IndexPrefix: tamma-api-dotnet

  tamma-api:
    environment:
      # ... existing env vars ...
      OPENSEARCH_URL: http://opensearch:9200
      OPENSEARCH_ENABLED: "true"
      LOG_INDEX_PREFIX: tamma-ts-api
      SERVICE_NAME: tamma-api

  tamma-engine:
    environment:
      # ... existing env vars ...
      OPENSEARCH_URL: http://opensearch:9200
      OPENSEARCH_ENABLED: "true"
      LOG_INDEX_PREFIX: tamma-ts-engine
      SERVICE_NAME: tamma-engine
```

---

## Verification Steps

After deployment, run these commands from any machine with access to the Docker network (or from the VPS itself):

### 1. Verify OpenSearch is Running

```bash
# Cluster health (should return green for single-node)
curl -sf http://localhost:9200/_cluster/health | python3 -m json.tool

# Expected: {"cluster_name":"tamma-logs","status":"green","number_of_nodes":1,...}
```

### 2. Verify Index Template Exists

```bash
curl -sf http://localhost:9200/_index_template/tamma-logs | python3 -m json.tool

# Expected: template with tamma-* pattern and all field mappings
```

### 3. Verify ISM Policy Exists

```bash
curl -sf http://localhost:9200/_plugins/_ism/policies/tamma-log-retention | python3 -m json.tool

# Expected: policy with hot → warm → delete states
```

### 4. Verify Indices Are Being Created

```bash
# List all tamma indices
curl -sf http://localhost:9200/_cat/indices/tamma-*?v&s=index

# Expected output like:
# health status index                        uuid   pri rep docs.count docs.deleted store.size pri.store.size
# green  open   tamma-elsa-2026.03.28       ...    1   0   1234       0            2.1mb      2.1mb
# green  open   tamma-api-dotnet-2026.03.28 ...    1   0   567        0            1.0mb      1.0mb
# green  open   tamma-ts-api-2026.03.28     ...    1   0   890        0            1.5mb      1.5mb
```

### 5. Verify Logs Contain Expected Fields

```bash
# Query latest 3 logs from ELSA
curl -sf http://localhost:9200/tamma-elsa-*/_search?size=3&sort=@timestamp:desc | python3 -m json.tool

# Verify fields: @timestamp, level, message, service, workflowInstanceId (when applicable)
```

### 6. Verify Dashboards Saved Objects

```bash
# List all saved objects
curl -sf http://localhost:5601/api/saved_objects/_find?type=dashboard | python3 -m json.tool

# Expected: "total":1 with title "Tamma Platform Logs Overview"
```

### 7. Verify Alerting Monitors

```bash
curl -sf http://localhost:9200/_plugins/_alerting/monitors/_search -H 'Content-Type: application/json' -d '{"size":10}' | python3 -m json.tool

# Expected: 2 monitors (tamma-error-spike, tamma-workflow-failure)
```

### 8. Verify Non-Blocking Behavior

```bash
# Stop OpenSearch
docker compose stop opensearch

# Verify ELSA server is still running and logging to console
curl -sf http://localhost:5000/health
# Expected: 200 OK

# Check ELSA logs — should see Serilog SelfLog messages about failed OpenSearch writes
docker compose logs elsa-server --tail=20

# Restart OpenSearch
docker compose start opensearch

# Wait for health check to pass, then verify buffered logs are delivered
sleep 30
curl -sf http://localhost:9200/tamma-elsa-*/_count
# Expected: count includes logs from when OpenSearch was down (buffered)
```

### 9. Verify Dashboards UI Access

```bash
# Via nginx proxy (production path)
curl -sf -o /dev/null -w "%{http_code}" https://logs.tamma.dev/api/status
# Expected: 200

# Direct (dev/testing)
curl -sf -o /dev/null -w "%{http_code}" http://localhost:5601/api/status
# Expected: 200
```

---

## Risks and Edge Cases

### 1. Memory Pressure on Hetzner CPX42 (16 GB)

**Risk**: Total memory usage (existing services + OpenSearch + Dashboards) exceeds 16 GB.

**Mitigation**:
- OpenSearch JVM heap: 4 GB (hard limit via `OPENSEARCH_JAVA_OPTS`)
- OpenSearch container limit: 5 GB (includes native overhead)
- Dashboards container limit: 2 GB
- Total new memory: ~7 GB. With existing ~8.4 GB, total is ~15.4 GB
- Leave ~600 MB for OS. If this is too tight, reduce OpenSearch heap to 3 GB (`-Xms3g -Xmx3g`) and container limit to 4 GB
- Monitor with `docker stats` after deployment

### 2. Disk Usage Growth

**Risk**: Log indices grow unbounded and fill the VPS disk.

**Mitigation**:
- ISM policy deletes indices after 30 days
- Force-merge to 1 segment at 7 days reduces storage by ~30%
- `best_compression` codec in index template reduces storage by ~15%
- Estimate: 10 GB/day log volume at moderate usage = ~300 GB/month before deletion
- If disk is tight, reduce retention to 14 days or add a `hot` rollover action at 5 GB per index

### 3. OpenSearch Startup Time

**Risk**: OpenSearch takes 30-60 seconds to start. Other services may log to OpenSearch before it is ready.

**Mitigation**:
- Serilog sink has `BufferBaseFilename` — logs are buffered to disk and delivered when OpenSearch comes up
- Pino `pino-elasticsearch` retries failed connections automatically
- Docker Compose uses `service_started` (not `service_healthy`) for app dependencies on OpenSearch — apps start immediately and buffer

### 4. Serilog.Sinks.Elasticsearch Version Compatibility

**Risk**: The NuGet package `Serilog.Sinks.Elasticsearch` 10.x targets Elasticsearch 8.x client internally, but OpenSearch uses the ES 7.x compatible API.

**Mitigation**:
- Version 10.0.0 works with OpenSearch because it uses the HTTP bulk API directly, not the typed client
- `AutoRegisterTemplate = false` avoids any version-specific template API calls
- If issues arise, fall back to `Serilog.Sinks.Elasticsearch` version 9.0.3 which explicitly targets ES 7.x
- Test the specific version during implementation by verifying logs appear in OpenSearch

### 5. pino-elasticsearch Connection Errors

**Risk**: `pino-elasticsearch` throws unhandled errors on connection failure, crashing the Node.js process.

**Mitigation**:
- The implementation attaches `.on('error')` and `.on('insertError')` handlers to the stream
- Errors are written to stderr, not propagated
- The `try/catch` around `require('pino-elasticsearch')` handles the case where the package is not installed
- If persistent issues occur, set `OPENSEARCH_ENABLED=false` to disable without code changes

### 6. Index Mapping Conflicts

**Risk**: If a log entry has a field with a type that conflicts with the template mapping (e.g., `durationMs` sent as string), OpenSearch rejects the document.

**Mitigation**:
- The `dynamic_templates` section maps all unmapped strings to `keyword`, preventing text/keyword conflicts
- Numeric fields in the template have explicit types — ensure all log producers use correct types
- Serilog automatically serializes .NET types correctly (int → number, string → string)
- Pino serializes JavaScript types correctly (number → number)
- If conflicts occur, check the `_bulk` API response for rejected documents

### 7. Network Partition Between Services and OpenSearch

**Risk**: If the Docker network has issues, logs may be lost.

**Mitigation**:
- Serilog buffer file (`./logs/opensearch-buffer`) persists across container restarts
- Pino `pino-elasticsearch` has built-in retry logic
- Logs always go to stdout/file in addition to OpenSearch — no single point of failure

### 8. Cloudflare DNS for logs.tamma.dev

**Risk**: `logs.tamma.dev` needs to be added to Cloudflare DNS before the nginx proxy works.

**Mitigation**:
- Add an A record for `logs.tamma.dev` pointing to the VPS IP (204.168.131.39)
- Enable Cloudflare proxy (orange cloud) for TLS termination
- Until DNS is configured, access Dashboards directly via `http://<vps-ip>:5601` in development

---

## Implementation Checklist

- [ ] Create `docker/opensearch/` directory
- [ ] Create `docker/opensearch/index-template.json`
- [ ] Create `docker/opensearch/ism-policy.json`
- [ ] Create `docker/opensearch/dashboards-saved-objects.ndjson`
- [ ] Create `docker/opensearch/setup.sh` (and `chmod +x`)
- [ ] Modify `docker/docker-compose.yml` — add opensearch, opensearch-dashboards, opensearch-setup services
- [ ] Modify `docker/docker-compose.yml` — add `tamma-os-data` volume
- [ ] Modify `docker/docker-compose.yml` — add OpenSearch env vars to elsa-server, tamma-api-dotnet, tamma-api, tamma-engine
- [ ] Modify `docker/docker-compose.prod.yml` — add resource limits for opensearch, opensearch-dashboards, opensearch-setup
- [ ] Modify `docker/nginx-proxy.conf` — add `logs.tamma.dev` server block
- [ ] Modify `docker/.env.example` — add OpenSearch variables
- [ ] Add `Serilog.Sinks.Elasticsearch` 10.0.0 to `Tamma.ElsaServer.csproj`
- [ ] Modify `Tamma.ElsaServer/Program.cs` — add OpenSearch sink
- [ ] Add `Serilog.Sinks.Elasticsearch` 10.0.0 to `Tamma.Api.csproj`
- [ ] Modify `Tamma.Api/Program.cs` — add OpenSearch sink
- [ ] Add `pino-elasticsearch` ^8.0.0 to `packages/observability/package.json`
- [ ] Rewrite `packages/observability/src/logger.ts` with OpenSearch transport
- [ ] Run `pnpm install` to update lockfile
- [ ] Run `dotnet restore` for both .NET projects
- [ ] Add Cloudflare DNS record for `logs.tamma.dev`
- [ ] Deploy with `docker compose up -d`
- [ ] Run verification steps 1-9
- [ ] Verify dashboards load in browser at `https://logs.tamma.dev`

---

**Last Updated**: 2026-03-28
**Implementation Owner**: Platform Engineering
