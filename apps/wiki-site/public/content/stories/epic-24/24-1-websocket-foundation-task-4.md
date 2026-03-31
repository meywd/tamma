---
title: "Task 4: Nginx WebSocket Proxy Configuration"
sidebar:
  order: 240
---

**Story:** 24-1-websocket-foundation - WebSocket Foundation
**Epic:** 24

## Task Description

Update the nginx reverse proxy configuration to support WebSocket connections for the voice endpoint at `/api/v1/voice`. The location block must handle WebSocket upgrade, disable buffering for real-time binary streaming, and set an extended timeout for long voice sessions.

## Acceptance Criteria

- New nginx location block for `/api/v1/voice` with WebSocket proxy
- `Upgrade` and `Connection` headers passed through for WebSocket handshake
- `proxy_buffering off` to prevent audio frame buffering
- `proxy_read_timeout` set to 3600s (1 hour max voice session)
- `proxy_send_timeout` set to 3600s
- Block placed in the `app.tamma.dev` server (443 SSL) before the generic `/api/` block
- Also placed in the bare IP server (port 80) for local development
- nginx config validates with `nginx -t`

## Implementation Details

### Technical Requirements

- [ ] Add WebSocket location block to `app.tamma.dev` server in `docker/nginx-proxy.conf`:

```nginx
    # Voice WebSocket (long-lived bidirectional connection for voice sessions)
    # Must appear before the generic /api/ location to take precedence.
    location /api/v1/voice {
        proxy_pass http://tamma-api:3100;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto https;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_buffering off;
    }
```

- [ ] Add matching location block to bare IP server (port 80) for local dev:

```nginx
    location /api/v1/voice {
        proxy_pass http://tamma-api:3100;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
        proxy_buffering off;
    }
```

- [ ] Placement: Insert the voice WebSocket block BEFORE the generic `/api/` block and BEFORE the SSE `location ~` regex block in each server. Nginx processes `location` blocks by specificity: exact (`=`) > prefix (longest match) > regex (`~`). Since `/api/v1/voice` is a longer prefix than `/api/`, it will match first without needing special ordering. But placing it before `/api/` in the config file makes the intent clear.

### Files to Modify

- MODIFY `docker/nginx-proxy.conf` -- add voice WebSocket location blocks

### Dependencies

- No code dependencies
- Requires nginx reload after deploy: `docker compose exec nginx nginx -s reload`

## Testing Strategy

### Validation Steps

1. [ ] Add the two location blocks to nginx-proxy.conf
2. [ ] Run `nginx -t` (or `docker compose exec nginx nginx -t`) to validate config syntax
3. [ ] Verify the voice block appears before generic `/api/` in both server blocks
4. [ ] Test WebSocket upgrade works through nginx:
   - `wscat -c wss://app.tamma.dev/api/v1/voice` (or via browser DevTools)
   - Verify the connection upgrades (101 Switching Protocols)
5. [ ] Test that binary frames pass through without buffering
6. [ ] Test that connections survive for >60s without timeout
7. [ ] Test that regular `/api/` routes still work correctly

## Notes & Considerations

- The existing SSE location (`location ~ ^/api/(engine/events|workflows/.*/events)`) uses a regex and sets `proxy_buffering off`. The voice WebSocket block uses a prefix match, which nginx evaluates before regex matches for the same path. Since `/api/v1/voice` does not match the SSE regex, there is no conflict.
- `proxy_read_timeout 3600s` is critical -- without it, nginx's default 60s timeout will kill idle voice sessions. The voice protocol includes JSON heartbeat messages to keep the connection alive through intermediate proxies (Cloudflare has its own 100s WebSocket idle timeout, which the application heartbeat at 30s intervals satisfies).
- `proxy_buffering off` is essential for real-time audio. Without it, nginx would buffer binary frames in memory/disk before forwarding, adding unacceptable latency to the audio stream.
- Cloudflare supports WebSocket connections natively when the origin responds with `101 Switching Protocols`. No Cloudflare-specific configuration is needed beyond ensuring the HTTPS origin cert is valid.

## Completion Checklist

- [ ] Voice WebSocket location block added to `app.tamma.dev` server (443)
- [ ] Voice WebSocket location block added to bare IP server (80)
- [ ] Both blocks have correct proxy headers, timeouts, and buffering settings
- [ ] nginx config validates with `nginx -t`
- [ ] Generic `/api/` routes unaffected
- [ ] SSE endpoints unaffected
