# 0025. Object storage and secrets

- **Status:** Agreed
- **Date:** 2026-10-18
- **Related:** [0005](0005-no-paid-services-local-first.md) · [0013](0013-offline-first-check-in.md) · [0022](0022-disposable-lab-environment.md) · design-decisions §9

## Context

The stack had no object storage (needed for ticket PDFs and database backups) and no secrets
handling (Stripe keys, database passwords, the QR and admission-token signing keys).
Everything must be free and local ([0005](0005-no-paid-services-local-first.md)).

MinIO was the obvious local S3 server, but its community edition was moved to maintenance
mode in December 2025 and is distributed as source only, without published images (checked
2026-10-18).

## Decision

**Object storage**

- The product talks to object storage through the S3 API only, so any S3-compatible store
  works.
- Locally, **SeaweedFS** (Apache 2.0) serves S3 for ticket PDFs and for CloudNativePG backups
  (point-in-time restore drill in M6). This replaces MinIO and is to be confirmed in a spike in
  M3; RustFS (Apache 2.0) is the fallback.

**Secrets**

| Where | How |
|---|---|
| Development (Aspire) | .NET user-secrets and Aspire parameters |
| Lab | Kubernetes Secrets created by `lab up`: Stripe test keys from an untracked local file; database passwords and signing keys generated per session ([0022](0022-disposable-lab-environment.md)) |
| CI | GitHub Actions secrets |

- Secrets are never committed.
- Signed tokens (QR codes, admission tokens) carry a key ID, so keys can be rotated without
  invalidating tokens signed with the previous key.

## Alternatives considered

| Alternative | Why not |
|---|---|
| MinIO | Community edition in maintenance mode, source-only distribution |
| Garage | AGPL; narrower S3 API |
| PDFs stored in PostgreSQL | Bloats the database and its backups |
| External Secrets Operator with Vault | Needs a secrets backend to run and operate; worth it only with real environments |

## Consequences

- PDFs can be served through short-lived pre-signed URLs.
- With the lab disposable, every session gets fresh keys, which exercises key handling all the
  time.

## Updates

- **2026-10-18:** SeaweedFS confirmed by the project owner. The M3 spike now only verifies that
  CloudNativePG backups and restores work against it; RustFS remains the fallback.
- **2026-10-21 (D33):** In M2, `lab up` generates every secret into Kubernetes Secrets: the database
  password (by CloudNativePG), the client secrets, the webhook secret and the ticketing master key.
  Stripe stays off in the lab until someone provides the untracked keys file.
