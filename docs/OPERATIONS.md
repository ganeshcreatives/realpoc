# Operations

## Start, stop and logs

Windows: `scripts/Setup.ps1`, `scripts/Start.ps1`, `scripts/Stop.ps1`. Startup records process IDs and start times, so shutdown checks identity before stopping processes. Local stdout/stderr are `.local/api.log`, `.local/api.error.log`, `.local/bff.log` and `.local/bff.error.log`. Do not attach `.local` to support tickets: it contains secrets, the database, PII and verification links.

Use only `http://localhost:5080` for the local integrated application. HTTPS is required outside loopback development. The current host has project-local SDK 10.0.401; commands use its full path so an existing global .NET 9 installation is not replaced.

## Database migrations

SQLite and PostgreSQL have independent migration projects and snapshots. Never edit an already applied migration. Scaffold a new migration for both providers whenever the shared model changes:

```powershell
$env:DOTNET_ROOT = "$PWD\.tools\dotnet"
$env:Path = "$env:DOTNET_ROOT;$env:Path"
.\.tools\dotnet\dotnet.exe tool install dotnet-ef --tool-path .tools\ef --version 10.0.12

$env:Database__Provider = 'Sqlite'
.\.tools\ef\dotnet-ef.exe migrations add DescribeChange --project server\School.Migrations.Sqlite --startup-project server\School.Api --context SchoolDb

$env:Database__Provider = 'Postgres'
.\.tools\ef\dotnet-ef.exe migrations add DescribeChange --project server\School.Migrations.Postgres --startup-project server\School.Api --context SchoolDb
```

If the tool is already installed, skip the installation command. The design-time factory uses placeholder local connections and needs no credentials. Review generated SQL with `migrations script` using the same project/provider arguments. Runtime migration command is `dotnet School.Api.dll --migrate-db` with the target's secret configuration/environment. Do not run it concurrently with a write-heavy old version; use a planned deployment and backward-compatible schema changes.

Never convert an existing `EnsureCreated` database to migrations by rerunning initialization. Import its data into an independently migrated database under a reviewed migration procedure.

## Grant staff access

Create/verify the person’s account first. A trusted operator runs `scripts/Grant-Staff.ps1 -Email ...` locally or the equivalent API command `--grant-staff email` with deployment configuration. The account becomes staff; existing sessions/tokens are revoked and an operator audit entry is recorded. This grants organization-wide admissions access. Keep operator credentials separate from normal users.

For immediate emergency revocation while administration is being expanded, revoke the user's sessions/tokens and set `Accounts.Active=false` in one reviewed SQL transaction. API and BFF both check Active. Restrict database administration to authorized operators and preserve an incident audit record. No self-service role change endpoint exists.

## Rotate signing keys

1. Generate a new 32-byte service secret and unique key ID. Do not change the session encryption key during ordinary service rotation.
2. Add the new key/client binding to API keyring while retaining the old key. Restart API instances so both verify.
3. Add the new key to BFF keyring and select its `activeKeyId`. Restart BFF instances.
4. Observe service-auth failure rate. Allow at least the in-flight/replay window for routine rotation.
5. Remove old verifier key and restart API. Remove obsolete BFF key material.

The combined container restarts both services; update API's keyring first and retain overlap until BFF has switched. Infisical changes do not hot-reload. Emergency compromise revocation overrides graceful overlap and may interrupt requests. Do not roll back to compromised key material.

Session encryption rotation currently invalidates existing encrypted sessions. Revoke sessions first, replace the BFF encryption key, restart BFF, then require login. Use a separate retained/decrypt-only encryption-key ring before attempting zero-logout encryption rotation.

## Restore, backup and rollback

- Back up PostgreSQL and verify restore into an isolated database before go-live. Free provider history is not a substitute for the agreed RPO.
- Protect vault/bootstrap credentials and encryption-key recovery separately from database backups.
- After an incident restore, revoke all sessions/tokens/action links. Do not resurrect earlier credentials or replay claims blindly.
- Preserve audit data under an approved retention/privacy schedule. Audit tables do not grow without cost.
- Keep prior signed/versioned artifacts. Roll back application code only while schema compatibility holds. Do not automatically reverse a destructive migration.
- If service authorization breaks, use a known hardened release or maintenance mode; never bypass signature middleware or re-enable unsigned paths.

## Alerts to configure

Sustained service-signature failures, security-state outages, SQL latency/failures, repeated rate-limit violations, elevated 401/403/5xx, email delivery failures, database size, state-cleanup failures, and readiness failures. Store logs in an approved service with retention/access controls. Only log route templates, outcomes, timing and generated trace IDs. Avoid proxy/body logging that would reintroduce credentials.

Review operating limits against measured traffic. Source rate limits currently ignore forwarded headers, so shared proxies can throttle many users as one source. Adopt a trusted proxy configuration with verified address ranges before tuning. Add load testing only in an authorized environment, then choose SLO/RTO/RPO and instance count.
