# The production-deploy approval gate routes on the autonomy dial

Date: 2026-08-18
Status: accepted (owner directive: "it needs to check automation level and then
go to orch or human" / "make sure it uses the same action engine")

## Decision

`deployment-pipeline`'s production-approval decision routes on the autonomy
gate's answer for `effect:deploy.prod`, evaluated by the SAME engine that gates
everything else (`CheckActionGateActivity` → `POST /governance/evaluate` →
`AutonomyGateService` → `AutonomyGateEvaluator` over the action catalog):

- `automated` (dial ≥ the action's level, or an admin action row, or an admin's
  observe-only `Enforce=false`) → production deploys under the orchestrator;
- below the dial → the existing human approval wait
  (`WaitForDeploymentApprovalActivity`);
- `denied` (action disabled / role-restricted) → refusal terminal, never an
  approvable wait;
- `unavailable` / unwritten → the human wait (fail closed);
- `Deployment:RequireProdApproval=true` → the human wait regardless (operator
  override; can only ADD a wait).

`mode == business` no longer forces the wait. Mode stays threaded for audit
rows and every other mode-sensitive consumer.

## What this replaces

Story 43-9 adopted the gate ADDITIVELY ("by OR, never by replacement"): the
business-mode term kept forcing a human no matter what the dial said, so the
deploy-control dial was advisory in exactly the deployments it exists to
govern. That was the right scope for 43-9 — removing an existing gate term was
outside a story's authority — and is now superseded by the owner's directive.

## Consequences

- At shipped defaults nothing auto-deploys: `effect:deploy.prod` is level 90,
  the dial defaults to 70, and enforcement is ON when no policy row has an
  opinion, so the evaluator answers an enforced `requires-human` in every mode.
  Automating production is a deliberate act (raise the dial to ≥90, lower the
  action's level, or set observe-only), in single-user and SaaS alike.
- The fail posture flips with the authority: while business mode was the
  backstop, an unreadable gate could fail open. With the dial as the decider
  there is nothing behind it, so an unreadable answer routes to the human wait
  (same rule as finding 36: absence of evidence is not a grant).
- Observe-only is honoured at the DECISION, not just the edge: the pipeline
  binds the gate's `Enforced` output (`ProdGateEnforced`, default true) so an
  admin's "report but do not block" passes production while a genuinely
  enforced block waits. An unavailable answer never reads as observe-only.

Pinned by `DeploymentPipelineGateTests` (mode-independence sweep, fail-closed
unavailable, observe-only, override, monotonicity, denied-is-a-hard-stop).
