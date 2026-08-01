---
variables: role, itemsJson, repoContext, evidence
enableTools: false
maxTokens: 8192
version: 2
---
You are a {{role}} grooming a backlog. Rank the ENTIRE candidate set below into a single total order — first to last — and justify every placement. This is NOT a triage classification: you are not assigning a severity or a priority band to one issue, you are deciding the sequence in which this whole set should be worked.

## Candidate items
Each entry carries an `itemId` (the identity you must echo back), an `issueId`, a `title` and a `summary`.
{{itemsJson}}

## Repository context
{{repoContext}}

## Ranking evidence
Accepted upstream triage decisions and findings for these items, each block labelled with the document type and the exact anchor it was read from. An item with no block here has no upstream evidence — rank it from its title and summary, and say so in its rationale. Never invent evidence that is not written below.
{{evidence}}

Weigh value against effort, and let the evidence move an item when it says something the title does not: a triage decision that names an item urgent or a finding that shows it unblocks other work belongs in that item's rationale. Rank every item in the set — including ones you would rather drop — and rank ONLY items in the set.

Return ONLY a single JSON object (no prose outside it) of this EXACT shape:
```json
{
  "items": [
    {
      "itemId": "meywd/tamma#42",
      "rank": 1,
      "rationale": "Blocks the rate-limit work two other items depend on; the accepted triage decision names it urgent and the fix is contained.",
      "value": "high",
      "effort": "1d"
    },
    {
      "itemId": "meywd/tamma#57",
      "rank": 2,
      "rationale": "Customer-visible 500 on the export path; no upstream evidence, ranked from the summary alone.",
      "value": "high",
      "effort": "3d"
    },
    {
      "itemId": "meywd/tamma#13",
      "rank": 3,
      "rationale": "Cosmetic alignment on an internal admin page; no dependants and no deadline.",
      "value": "low",
      "effort": "2d"
    }
  ]
}
```

Rules:
- Echo each `itemId` EXACTLY as it appears in the candidate set above — do not translate, renumber or invent ids. An ordering that names an item nobody supplied cannot be applied.
- Every supplied item appears EXACTLY ONCE. A repeated `itemId` is rejected: a total order gives each item one position.
- `rank` is an integer and the ranks are the unique, gap-free `1..N` sequence over the set — no ties, no gaps, no zero, no duplicates. Two items at the same rank is rejected.
- Every item states a non-empty `rationale` saying why it sits where it sits — not what the item is. "Important" is not a rationale; "blocks #42 and #57, one day of work" is.
- Every item states BOTH a non-empty `value` and a non-empty `effort` estimate. Use your team's own units (`high`/`medium`/`low`, story points, `1d`/`3d`, …) and use them consistently across the whole ordering; both fields are required on every item.
