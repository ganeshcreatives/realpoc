# School Portal

A full-stack school application project with a reusable TypeScript SDK, React UI, .NET 10 BFF, private .NET 10 API, and persistent SQL state.

Repository: [ganeshcreatives/realpoc](https://github.com/ganeshcreatives/realpoc) (public source). Never commit keys or local data.

**Delivery status:** implemented and built locally. Public hosting, npm publication, live Infisical/Resend connections, hosted PostgreSQL execution and production security acceptance are pending. This repository is not a certification that the earlier production incidents are resolved.

## Start locally on Windows

```powershell
cd C:\ganesh-work\realpoc
.\scripts\Setup.ps1
.\scripts\Start.ps1
```

Open **http://localhost:5080**. Stop with `.\scripts\Stop.ps1`.

The setup uses a project-local .NET 10 SDK, installs locked npm dependencies, builds all components, applies SQLite migrations, and packs the SDK. Development secrets/database/email links are under `.local`, protected by Windows ACLs and excluded from Git. The startup scripts open no terminal windows. API listens only on `127.0.0.1:5081`; BFF only on `127.0.0.1:5080` in local development.

### First account

1. Create a parent account using a passphrase of at least 15 characters.
2. Open the latest file in `.local\mail`, follow its verification link, and choose your name and password. Local mode does not send email.
3. Sign in, add a student, view their zero starting balance, and submit an application.
4. To explore staff review, register and verify a second account, then run:

```powershell
.\scripts\Grant-Staff.ps1 -Email 'staff@example.com'
```

Sign in again with that account to review applications. There are no seeded passwords or public role-assignment endpoints. Staff access covers the single organization represented by this deployment. Schools are a sample catalog; no real student enrollment or payment system is connected.

## Project map

| Directory | Responsibility |
|---|---|
| `packages/sdk` | Typed browser API, session-context recovery, SHA-256 + HMAC, safe errors |
| `apps/web` | Parent dashboard, account flows, students, applications, school catalog, staff review |
| `server/School.Bff` | Browser session boundary, browser signature verification, private API signing, response allowlists |
| `server/School.Api` | Account authentication, authorization, ownership checks, business writes and audit events |
| `server/School.Shared` | Contracts, crypto, relational state, limits, secret-store integration |
| `server/School.Migrations.*` | Separate provider-specific EF migration histories |
| `scripts` | Local setup/start/stop, staff grants, SDK packaging, key generation |
| `deploy`, `render.yaml`, `Dockerfile` | Hosted deployment configuration |
| `docs` | Security contract, hosting, operations, release requirements |

## Included workflows

- Registration with email ownership verification; login and logout.
- Password reset with single-use, expiring links and session/token revocation.
- Per-tab signing context recovery after reload.
- Parent-owned student creation and balance reads.
- School applications with submission idempotency and uniqueness constraints.
- Staff decisions with explicit role checks, version checks and audit events.
- Safe request activity view showing statuses and support references, never secrets/payloads.
- Browser-to-BFF and BFF-to-API signatures with separate keys and shared atomic replay checks.
- Server-side access tokens, encrypted browser session payloads, hashed cookie/token identifiers.
- Persistent rate limits, bounded payloads/responses/deadlines, structured safe errors.
- Infisical integration and production PostgreSQL provider.

## SDK

```powershell
npm run pack:sdk
```

Package: `artifacts\school-portal-browser-sdk-1.0.0.tgz`. Install from that file in another application. The built app also serves an ESM SDK module at `/sdk/browser-sdk.js`; JavaScript declarations remain in the npm package. See `docs/HOSTING.md` for free public hosting/publication. `@school-portal` is a placeholder scope, not a claimed npm account.

## Documentation

- [Architecture and signing contract](docs/ARCHITECTURE.md)
- [Hosting, Infisical and npm publication](docs/HOSTING.md)
- [Operating the application](docs/OPERATIONS.md)
- [Production release requirements](docs/RELEASE.md)

## Development commands

```powershell
npm run build
.\.tools\dotnet\dotnet.exe build server\School.Api -c Release
.\.tools\dotnet\dotnet.exe build server\School.Bff -c Release
```

For Vite hot reload, change `PublicOrigin` in both local configs to `http://localhost:5173`, restart the services, then `npm run dev`. Browse that exact origin. Restore `http://localhost:5080` for the integrated build.

This application currently targets one organization and a sample 2026–2027 admission cycle. Multi-district tenancy, importing existing student relationships, real balances/payment processing, document uploads, support impersonation and mobile clients require their own reviewed contracts. They are not silently represented by the sample workflows here.
