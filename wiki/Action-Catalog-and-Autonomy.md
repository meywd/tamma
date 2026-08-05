# Action Catalog & Autonomy

How Tamma decides — for every consequential thing it can do — whether to do it on its own or stop and ask a person.

> Figures on this page were derived from the code on `main` (catalog totals, dial rows, enforced-route counts). They are pinned by tests, so if the code changes and this page is not updated, those tests go red first.

## The idea in one paragraph

Every action Tamma can take is written down in a **catalog**, and every catalogued action carries a **level** from 1 to 100 — roughly "how consequential is this?". Separately there is a single **autonomy dial**, also 1–100. The rule is simply:

> **Tamma acts on its own when the action's level is at or below the dial. Otherwise it stops and asks a human.**

Turn the dial down and more things need approval. Turn it up and Tamma acts more independently. One number, one comparison.

## What is in the catalog

**219 catalogued members** across six planes:

| Plane | What it covers |
|---|---|
| `agent-action:*` | The 96 things an agent role can be asked to do (write a design, triage an issue, …) |
| `document-type:*` | The 17 typed documents the system produces |
| `tool:*` | The 8 tools an agent can call in its tool loop |
| `effect:*` | The **61** consequential side effects on the outside world — merge a PR, deploy, send mail, read a secret |
| `automation:*` | The 29 background workers |
| `platform-task:*` | The 8 platform task kinds |

Of these, **177 are "dial rows"** — rows the dial actually governs. The rest are *machinery*: internal plumbing that is off the dial entirely (see below).

The catalog is not documentation that drifts. It is enforced: startup refuses to boot if a catalogued member has no descriptor, and test sweeps walk the **running** application to check that what the catalog claims matches the routes that actually exist.

## The dial

| | Value |
|---|---|
| Minimum | 1 |
| Maximum | 100 |
| **Shipped default** | **70** |
| `AlwaysHuman` | 101 — above the maximum, so it can never be satisfied at any dial position |

Levels are assigned in **zones** at 5-point steps, so related actions cluster. Roughly:

| Level | Examples |
|---|---|
| 20–30 | Call a model, trigger CI, run a task |
| 35–40 | Open/close/comment on a PR, update an issue, post a review comment |
| 50–65 | Bypass required checks; merge to `dev` (55), `qa` (60), `main` (65) |
| 70–90 | Deploy to environments (dev/qa/uat/staging/prod), read a secret value (90) |

At the shipped dial of **70**, everything at or below 70 is automated and everything above needs a person. That is why a production deploy (90) stops for approval while opening a PR (35) does not.

### A consequence worth knowing

Most catalogued actions sit **below** 70. The dial's expressive range is therefore mostly *downward* — lowering it is how you tighten the system. The dashboard slider currently has a floor of 70, so the lower range is not yet reachable from the UI. That is a deliberate, recorded decision rather than an oversight, and it is expected to change.

## Machinery is off the dial

Not everything Tamma does is a judgement call. Sweepers, outbox drains, event appends and similar plumbing are marked **machinery** and never consult the dial. Gating them would mean a control-plane hiccup could stop the platform's own housekeeping — which is not a safety property, just an outage.

This is why the catalog is bigger than the set of dial rows: 219 members, 177 of them governed.

## Who is asking? (caller kinds)

The dial governs **the LLM**, not everyone. When a request arrives, Tamma grades the caller:

| Caller | Treatment |
|---|---|
| Authenticated human | Not gated — a person is already deciding |
| Machinery | Off the dial |
| Anonymous / service credential | **Treated as the LLM** — fail-closed |

The fail-closed default matters: an unauthenticated or service-token call is assumed to be the model, so a missing credential can never accidentally buy a caller *more* freedom than it should have.

## Enforcement is opt-in, per route, and written down

Binding a route to a catalogued action and *enforcing* that binding are two separate lines of code:

```csharp
.Governs(new ActionKey(ActionNamespace.Effect, ExternalEffect.GitPullRequestClose.ToWire()))
.EnforcesGovernance();
```

The first line says what the route does — that feeds the admin view and the drift sweeps. The second turns on blocking. They are separate on purpose: switching a route to "can hard-refuse" is a behaviour change and must be a reviewed line, never something inherited from a helper.

**30 routes currently enforce.** The set is pinned exactly, in *both* directions — adding an opt-in fails the build, and so does silently removing one (which would ungovern a route while every binding test stayed green).

### The ratchet

Routes that mutate something but carry no catalog member sit on a **shrink-only baseline**: currently **208**, out of an in-scope surface of **245**. The count may only go down. A new ungoverned route is not a reason to raise the number — it is the signal the ratchet exists to produce.

## What a refusal looks like

When the gate declines, the caller gets **409 Conflict**, not 403:

```json
{
  "code": "ACTION.GATE.REQUIRES_HUMAN",
  "action": "effect:deploy.prod",
  "effectiveMinAutonomy": 90,
  "autonomyLevel": 70,
  "authorizationId": "…"
}
```

403 would be wrong — the *caller* is authorized. The **system** is not yet permitted to act by itself. And it is never 202, because 202 already means success to the engine, which would then carry on as though the effect had happened.

The `authorizationId` names a real pending row a human can act on, so a refusal is always actionable rather than a dead end.

## Approvals: single-use vs standing

An approval can be scoped two ways:

- **single-use** — consumed by one action, then spent.
- **correlation-standing** — covers a whole run. Granted once, it applies to every subsequent step of that same run and is never consumed.

Standing grants exist because an agent run can hit the same gated action repeatedly; asking a person once per run is reasonable, asking twenty times is not. Standing grants are scoped to one correlation — they never leak across runs.

## Document acceptance is derived, not stored

Whether a produced document (a design, a threat model, a sprint plan) needs a **human** to accept it is *computed*, not saved:

> A document type requires human acceptance when the dial sits **below that document type's level**.

There is no stored "requires human" flag to be accidentally overwritten. Lower the dial and more document types require a person; raise it and fewer do. This is worth stating plainly because the older, stored-flag design is still described in some places.

## Where to look in the code

| Concern | Location |
|---|---|
| The catalog and its descriptors | `Tamma.Core/Actions/ActionCatalog.Descriptors.cs` |
| Effect vocabulary | `Tamma.Core/Actions/ExternalEffect.cs` |
| Dial constants | `Tamma.Core/Documents/Policy/AutonomyDial.cs` |
| Document acceptance floors | `Tamma.Core/Documents/Policy/AcceptanceFloors.cs` |
| Route enforcement filter | `Tamma.Api/Infrastructure` (`AutonomyGateEndpointFilter`) |
| Route bindings | `Tamma.Api/Program.cs` (`.Governs` / `.EnforcesGovernance`) |

## Related

- [Security](/security/) — authentication, credentials, secret handling
- [Multi-Git-Platform](/multi-git-platform/) — which git platforms support which operations
- [Event Schema and Catalog](/event-schema-and-catalog/) — the audit trail every gated action writes
