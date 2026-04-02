# Wiki Diagram Layout Persistence

**When**: Wiki site enhancement
**Related**: apps/wiki-site

## Design

Users can drag nodes to improve workflow diagrams. Positions persist via the wiki Worker.

### Architecture
- Wiki Worker (already exists at src/worker.ts) handles API routes
- Storage: Cloudflare KV or R2 (key: `layout:{slug}`, value: JSON positions)
- React component saves on drag end, loads on mount

### API (server-side on the Worker)
```
GET  /api/layout/:slug    → returns saved positions or 404
PUT  /api/layout/:slug    → saves positions (body: { nodeId: { x, y } })
DELETE /api/layout/:slug  → clears saved layout (reset to auto)
```

### React Component Changes
- On mount: fetch `/api/layout/{slug}` → if exists, override ELK positions
- On node drag end: debounce → `PUT /api/layout/{slug}` with all positions
- "Reset Layout" button → `DELETE /api/layout/{slug}` → re-run ELK
- Edges are NOT draggable or editable — only node positions

### Wrangler Config
```jsonc
{
  "kv_namespaces": [
    { "binding": "LAYOUTS", "id": "..." }
  ]
}
```

### Constraints
- No auth needed — layout is a shared preference
- Debounce saves (500ms) to avoid spamming KV
- Max layout size: 50KB per slug
- Connections (edges) are never modified — only positions
