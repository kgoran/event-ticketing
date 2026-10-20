# 0052. Email: SMTP with MailKit, Mailpit locally, Razor templates, a delivery log

- **Status:** Agreed
- **Date:** 2026-10-20
- **Related:** [0005](0005-no-paid-services-local-first.md) · [0006](0006-ticketing-modules.md) · [0007](0007-module-communication.md) · [0014](0014-outbox-inbox-scheduler-on-postgresql.md) · [M1b plan](../milestones/m1b.md) (D12)

## Context

Notifications sends the fan their tickets and tells them about refunds. Everything must run
locally and free ([0005](0005-no-paid-services-local-first.md)), and sending an email can't be
part of a database transaction.

## Decision

- **SMTP through MailKit** (MIT). Locally, **Mailpit** catches every email and shows it in a web
  inbox; it runs as a container under Aspire. Any SMTP server works in other environments.
- **Templates are Razor components** rendered to HTML with .NET's `HtmlRenderer`: typed models,
  no template engine. Each email also has a plain-text part.
- QR codes go in the email as inline images.
- **A delivery log** in Notifications' schema records each email by the message that caused it
  (one email per event and recipient). The log entry is written, then the email is sent, then the
  entry is marked sent. A crash between sending and marking can send a second copy: at least
  once, and the log makes that visible.

## Alternatives considered

| Alternative | Why not |
|---|---|
| `System.Net.Mail.SmtpClient` | Microsoft advises against it for new code |
| A template engine (Scriban, Fluid) | Another syntax to learn; Razor is what the team writes anyway |
| An email service (SendGrid, Postmark) | Paid; Mailpit keeps the demo offline |

## Consequences

- The demo shows real emails, offline, in Mailpit's inbox.
- Emails are at least once, like every other message here; the delivery log stops redelivered
  events from sending again, but not a crash in the middle of a send.
