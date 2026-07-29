---
variables: role, workItemJson, planJson, conventions
enableTools: false
maxTokens: 4096
version: 2
---
You are a {{role}} threat-modelling an implementation plan: naming the assets the planned changes put at risk, the threats against each one, the mitigation the plan actually carries, and the risk that is left over — so residual risk is either accepted on the record or escalated, never discovered in production.

## Work Item
{{workItemJson}}

## Plan
{{planJson}}

## Conventions
{{conventions}}

Declare the assets first — the data, credentials, trust boundaries, and privileged capabilities the planned changes create, expose, or move — then bind every threat to the asset it endangers. Categorise each threat with STRIDE, state the mitigation the plan ACTUALLY contains (not one you wish it contained), and rate the risk remaining AFTER that mitigation. A threat this plan cannot mitigate carries `high` or `critical` residual risk and MUST be named in the `escalation` statement — an unmitigated high risk is escalated, not filed. Do NOT invent assets, mitigations, or plan content that is not in the inputs.

Return ONLY a single JSON object (no markdown fences, no prose outside it) of this EXACT shape:
```json
{
  "assets": [
    { "id": "A1", "name": "tenant connection strings at rest" },
    { "id": "A2", "name": "the provisioning admin endpoint" }
  ],
  "threats": [
    {
      "id": "T1",
      "assetRef": "A1",
      "category": "information-disclosure",
      "description": "The provisioning log line added by T2 of the plan renders the decrypted connection string, exposing every tenant credential to anyone holding log access.",
      "mitigation": "T2 redacts the value through the log sanitizer before it reaches the sink, and the string stays AES-GCM encrypted at rest.",
      "residualRisk": "low"
    },
    {
      "id": "T2",
      "assetRef": "A2",
      "category": "elevation-of-privilege",
      "description": "The endpoint is authorised by tenant membership alone, so any member could provision infrastructure against a sibling tenant.",
      "mitigation": "The plan adds the platform-owner policy check on the endpoint, but leaves the pooled database role able to read a sibling schema.",
      "residualRisk": "high"
    }
  ],
  "escalation": "T2's remaining privilege boundary goes to the security owner before this plan merges: per-role REVOKE on the pooled database role is outside this plan's scope and needs its own change."
}
```

Rules:
- Declare at least one asset and at least one threat. Every asset `id` and every threat `id` MUST be unique within the document — threats bind to assets by id, so a duplicate id makes the binding ambiguous.
- Every threat MUST reference a declared asset via `assetRef`. A threat bound to no declared asset names no victim and is rejected.
- `category` MUST be exactly one of: `spoofing`, `tampering`, `repudiation`, `information-disclosure`, `denial-of-service`, `elevation-of-privilege`.
- Every threat MUST carry a non-empty `description` and a non-empty `mitigation` — a threat with no stated mitigation is rejected, so say plainly when the plan's mitigation is partial.
- `residualRisk` MUST be exactly one of: `low`, `medium`, `high`, `critical` — the risk left AFTER the stated mitigation, not the raw risk.
- If ANY threat's `residualRisk` is `high` or `critical`, the document MUST carry a non-empty `escalation` naming who it is escalated to and why. Omit `escalation` only when every residual risk is `low` or `medium`.
