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

echo "[opensearch-setup] Bootstrap complete."
