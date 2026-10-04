# Release status and remaining production work

## Implemented project scope

The SDK, React UI, BFF, private API, account workflows, ownership authorization, two-hop signatures, SQL replay/rate/session state, encrypted BFF session storage, application idempotency, staff review, migrations and deployment files are implemented. A build-only CI workflow packages releases. Builds do not establish security correctness or production readiness.

## Local integration evidence

On 4 October 2026, `scripts/Smoke.ps1` passed against the running local integrated app. It exercised registration, email verification, login, a parent-owned student and balance, application submission, an invalid browser signature, replay rejection, cross-account record denial, staff action denial for a parent, operator staff grant, staff approval and stale-version rejection. The test used disposable local accounts and the protected local mail sink. It did not exercise PostgreSQL, cloud hosting, live email, Infisical, load, or a hostile security review.

The older signing PoC's 38-row script cannot run unchanged against this product: its off/shadow API modes, Echo endpoint and unsigned upload route are absent. A new local `scripts/SigningMatrix.ps1` checks 39 current-contract cases, including changed method/path/body, duplicate/missing signatures, stale/future timestamps, replay, wrong key, CSRF, invalid media/body/nonce, forbidden HTTP methods and direct API bypass attempts. The initial run found TRACE returning 404; the BFF was changed to return 405 without forwarding. The rerun passed 39/39, followed by a passing `Smoke.ps1`. This is local security regression evidence, not a penetration test or a claim that every old PoC row has a one-to-one equivalent.

Commit `11d40f7` deployed successfully. On the hosted route, an unauthenticated read returned 401, a query-bearing route returned 400, a foreign-origin login returned 403, and an unsupported `HEAD` returned the BFF's 405 problem response with `Allow: GET` and a correlation ID. The edge blocks `TRACE` itself with an HTML 405, so the BFF's `TRACE` response is verified locally. Hosted tests did not use direct access to the private API or a production signing key.

## Hosted end-to-end evidence

On 4 October 2026, the live Render/Neon/Infisical/Resend demo accepted a browser registration request, Resend reported delivery of its verification email to the account owner's test address, and the verification link activated a sample parent account. Browser sign-in, sample student creation, zero balance, school application submission, application listing and session-context recovery after reload succeeded. The browser's request activity showed successful signed reads and context recovery. This exercises the positive browser → BFF → API → database path with live email. It does not prove hostile-request resistance on the hosted ingress, email delivery to other recipients, or production availability. The local smoke script was rerun and passed its negative cases on the same date.

## Before public release

| Item | Current state | Required evidence/configuration |
|---|---|---|
| .NET 10 | Installed locally; release builds pass | Patch policy and compatible runtime on host |
| SDK | `1.0.2` with configurable timeouts is public on npm with MIT license; the deployed app bundles it | Verify compatibility for each future release |
| React/BFF/API | Deployed on Render Free; public page and SDK returned HTTP 200 | Full real-host behavior and security verification; free service cold-start availability |
| Infisical | Production keyrings and Viewer machine identity configured; app startup succeeded | Restrict identity beyond project-wide Viewer if plan permits, test revocation and rotation |
| Hosted database | Neon Free PostgreSQL in Singapore; initial migration and readiness check succeeded | Separate runtime/migration roles, TLS evidence, backup/restore exercise |
| Email | Resend sending key configured with test sender | Verified domain, delivery verification, bounce/abuse policy |
| Security acceptance | Local integration smoke passed; independent review pending | Remaining negative/concurrency/end-to-end checks listed below |
| Availability | One-container free deployment configuration | Explicit service objectives; redundancy if required |
| Real school data | Sample school catalog, new parent-owned profiles, zero balances | Approved school catalog, parent relationship validation, privacy and retention rules |
| Privileged accounts | Explicit operator staff grant | Organization's MFA/SSO requirement and lifecycle controls |

The first public readiness check returned HTTP 200. After a redeploy, one check briefly returned HTTP 504 during restart and the next returned HTTP 200. Cold starts and this transient failure remain availability limits of the free deployment.

On 4 October 2026, live logs showed an API login complete in about 7.5 seconds while the BFF's former 8-second upstream deadline returned 504 to the browser. A free-host timeout profile and a configurable SDK timeout were added to allow for Render/Neon wake-up. A separate `SIGNATURE_INVALID` was also observed; the UI now clears its stale account view and asks the user to reload or sign in again, without automatically retrying a write. Commit `8fd1896` was deployed successfully. A hosted invalid-credential login then returned 401 in about 20.7 seconds, with BFF and API lines linked by the BFF response trace and API `ParentTrace`; it did not time out at the old 8-second limit. The demo account subsequently signed in with HTTP 200; the authenticated applications list, students, schools and balances returned HTTP 200. A new application for an existing demo student to a second sample school returned HTTP 200 in about 0.8 seconds and appeared in the list with `In review` status. This checks the previously failing read and write paths on the new deployment. A 30-minute idle session still expires by design and requires sign-in again.

## Acceptance cases to execute before production

The smoke script covers a subset of these cases. Before release, implement and execute coverage for:

- Register/verify/login/context-recovery/logout/reset; expired/consumed links and resistance to prior unverified registrations.
- Another parent cannot list/read/apply for another parent's student, even with a valid signature.
- Parents cannot call staff decisions; disabled accounts and revoked sessions fail immediately.
- Changed body/path/method/token invalidates the appropriate signature; missing/duplicate headers fail.
- Replay races on two replicas allow exactly one winner; scopes do not interfere; database outages fail closed.
- Timestamp boundaries, clock skew and key/client binding; service v2 and browser v1 exact byte interoperability.
- Fixed-time compare, key overlap/revocation, encrypted session corruption, DB-restore session invalidation.
- Fake cookies cannot select arbitrary rate-limit buckets; login/account/source limits hold.
- Foreign-origin login, recovery and writes fail; no CORS credential leak; CSP and cookie settings on actual TLS ingress.
- Service 403 maps to browser 502; user-token 401 revokes session; invisible record is 404.
- Duplicate application retries return the recorded result; different content conflicts; staff decisions reject stale versions.
- Chunked/excessive bodies/responses, wrong JSON types/fields, route/method errors, cancellation and timeout behavior.
- PostgreSQL SQL/upsert/migration behavior, restore/recovery, live Infisical and real email delivery.
- Keyboard/screen-reader/mobile experience, dependency scanning, adversarial security review and load measurements.

## Extensions before broader use

- Replace the sample school catalog/academic year with managed admissions data.
- Add pagination beyond the current 100-row response cap.
- Decide staff school/tenant assignments before multi-organization use. This deployment is intentionally one organization; it does not claim tenant isolation for multiple districts.
- Add asynchronous encrypted email outbox with retries/operational visibility when delivery reliability and anti-enumeration timing are required. Current registration/reset sends synchronously and may reveal existence through timing.
- Add supported identity-provider MFA/SSO for staff according to policy.
- Separate BFF/API runtime identities/database permissions on production hosts. The combined free container and shared database credentials are not strong workload isolation.
- Configure incident procedures, audit retention, alerts, ingress identity and quota monitoring.

The earlier plan also discussed incident evidence, existing Angular/mobile behavior, payment flows, uploads, support impersonation and compatibility migrations. Those involve systems absent from this new repository. They cannot be marked complete by building a new sample school application. This repository provides the implemented foundation and explicit integration boundaries for that work.
