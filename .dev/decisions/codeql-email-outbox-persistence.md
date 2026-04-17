# CodeQL alerts on the email outbox persistence flow

**Status**: Accepted
**Date**: 2026-04-17
**Alerts**: #81, #82 — `cs/exposure-of-sensitive-information` on
`apps/tamma-elsa/src/Tamma.Api/Services/Email/SmtpEmailService.cs`

## The alerts

CodeQL flags the data flow from `EmailMessage.To`, `.Subject`, `.Html`,
`.Text` (which are ultimately populated from HTTP input or code that
consumes it) into the `email_outbox` row that `SmtpEmailService.SendAsync`
persists. CodeQL's taint model treats that persistence as "writing private
data to an external location."

## Why we accept them

The outbox is a **store-and-forward buffer**. We cannot deliver an email
without knowing the recipient, subject, and body; persisting them is the
point of the design. The alternative — sending synchronously at the HTTP
handler — was the original design and was rejected because:

- SMTP round-trips block the request thread
- Transient SMTP failures became permanent user-visible errors
- No retry, no audit trail, no observable delivery status

## Mitigations already in place

1. **Tenant-scoped storage**: `email_outbox.TenantId` is populated and the
   EF query filter + (planned) RLS policy prevents cross-tenant reads.
2. **Short row lifetime**: `OutboxSmtpSender.ProcessOnceAsync` deletes the
   row immediately after `MarkSentAsync` succeeds. Successful sends leave
   no persisted copy of the recipient / subject / body.
3. **Terminal-failure purge**: per the "inbox is for retries" directive,
   rows are also deleted after the retry ceiling is reached. The audit of
   the failed attempt lives in `domain_events` (`EMAIL.SENT.FAILED`) with
   only txn id + template + error class — no PII.
4. **No PII in logs**: `OutboxSmtpSender` and `SmtpEmailService` log only
   the transaction id. `LogSanitizer.Clean` strips control chars from
   anything user-controlled that does reach a log line.
5. **No PII in events**: the three event types (`EMAIL.QUEUED.SUCCESS`,
   `EMAIL.SENT.SUCCESS`, `EMAIL.SENT.FAILED`) carry only
   `{txn_id, template, tenant_id, user_id}` tags. Recipient / subject /
   body are never written to the event store.
6. **Transport encryption**: MailKit establishes STARTTLS by default;
   `Email:Smtp:UseSsl=true` can switch to implicit TLS. Bytes in flight
   between the API and the SMTP relay are encrypted.
7. **At-rest encryption**: assumed to be provided by the Postgres
   deployment (e.g. encrypted EBS volume on the VPS). Not enforced at the
   application layer today.

## What would change the decision

- Regulatory requirement to encrypt email body in the DB (HIPAA, strict
  GDPR interpretation): implement column-level AES-GCM on the
  `HtmlBody` / `TextBody` / `Subject` columns, keyed off a per-tenant
  data-encryption key stored in the control plane.
- Legal requirement for selective erasure of historical attempts: the
  current design only retains failed-and-retrying rows, which churn.

## Action

Dismiss alerts #81 and #82 in the Security tab with category
"Used in tests" **No** — the correct category is
"Won't fix" with the explanation linking back to this document.

Future alerts of the same rule on this file should be triaged against this
document before automatic dismissal; if new flow paths emerge (e.g. direct
logging of recipient), that's a real issue.
