# Hosting and publishing

## What is available now

The app runs locally and at [the public Render demo](https://school-portal-yi3f.onrender.com). The SDK is a distributable `.tgz` package and is served as an ESM module by the hosted app. The public source repository is `ganeshcreatives/realpoc`. Render Free, Neon Free (Singapore), the Infisical School Portal project and a Resend sending key are provisioned. Public npm publication and a verified email domain remain pending. Resend's test sender is restricted to the account owner's address. Secret values belong in provider dashboards, never in chat or Git.

## Suggested free starting deployment

| Component | Option | Important limit |
|---|---|---|
| SDK package | Public npm package | You must own the scope; public source is downloadable by everyone |
| React + BFF + API | One Render free Docker web service | Sleeps when idle, cold starts, shared container, no high availability |
| Persistent application/security state | Neon free PostgreSQL | Compute, storage and transfer quotas; check current limits |
| Signing keys | Infisical free secrets management | Plan/identity limits; runtime memory still holds loaded keys |
| Verification/reset email | Resend free transactional email | Sending quota and verified sender/domain requirements |

This can have zero service subscription cost while usage fits free quotas. It is not a promise of zero total cost: custom domains, email domain ownership, backups, engineering and production availability can cost money. Do not use temporary free Render Postgres for durable customer records; it expires. Do not store SQLite data on the free web container's ephemeral filesystem.

Provider details checked 4 October 2026: [Render free service restrictions](https://render.com/docs/free), [Neon free plan](https://neon.com/blog/neon-free-plan-1-gb-per-project), [Infisical pricing](https://infisical.com/pricing), [Resend pricing](https://resend.com/pricing), [npm public packages](https://docs.npmjs.com/about-public-packages/). Confirm quotas at deployment time. These are hosting instructions for this project, not the source documents behind the earlier security plan.

## 1. Create owned resources

Use the existing Git repository under your account. The Neon `school-portal` project and Infisical `School Portal` project with a `prod` environment are created. Keep development and production secrets/databases separate. A verified email domain is still needed to send to arbitrary users; the current Resend test sender permits only the account owner's address.

The repository is published with `.local`, `.tools`, `node_modules`, artifacts and database files excluded. Keep those paths out of future commits.

## 2. Store signing secrets in Infisical

The two production keyrings are already stored as JSON-valued secrets in the Infisical `prod` environment. To rotate them later, generate new key material in a protected local directory:

```powershell
.\scripts\New-Keyring.ps1 -OutputDirectory '.local\production-key-transfer'
```

Store two JSON-valued secrets at path `/` in the `prod` environment:

- `SCHOOL_BFF_KEYRING`: contents of the generated BFF file, including the session encryption key.
- `SCHOOL_API_KEYRING`: contents of the generated API file. Its `sessionEncryptionKey` is empty.

Both share the service HMAC key and key/client identifiers. Session encryption uses a different random key. The browser never contacts Infisical and never receives either keyring.

The `school-portal-render-prod` machine identity has the project's Viewer role and Universal Auth. Its client ID/secret and project ID are in Render's private environment settings. Viewer is read-only across this project; it is not limited to only the two named secrets or only `prod`. The integration logs in at `/api/v1/auth/universal-auth/login`, then reads the named secret at `/api/v4/secrets/{name}`. Use the correct Infisical region/base URL. The files are a transfer mechanism, not a production key backup policy; store a protected recovery copy under your operational process.

The combined free container uses one bootstrap identity/environment for two processes. Separate paid hosts should use two identities: API can read only API verifier keys; BFF can read only its signing/session keyring. This application uses Infisical to store/retrieve secrets, not a remote HSM signing API. SHA-256 payload hashing needs no secret at all; HMAC signing does.

## 3. Create the Render service

Import `render.yaml` from the owned repository, or create a free Docker web service from the public repository. The image builds React and both .NET 10 services. Only the BFF port is public; the API listens on container loopback. For a manual Render service, the startup script uses Render's `RENDER_EXTERNAL_URL` as `PublicOrigin`. Set `PublicOrigin` explicitly when using a custom domain or another host.

Set these private environment values:

| Setting | Value |
|---|---|
| `PublicOrigin` | Exact public HTTPS origin for a custom domain or non-Render host; optional for a manual Render web service |
| `Database__Connection` | Npgsql-style Neon connection string, e.g. `Host=...;Database=...;Username=...;Password=...;SSL Mode=VerifyFull;Timeout=3;Command Timeout=3;Maximum Pool Size=10` |
| `Secrets__Provider` | `Infisical` |
| `Infisical__Url` | Your regional HTTPS base URL, e.g. `https://us.infisical.com` |
| `Infisical__ClientId`, `Infisical__ClientSecret` | Machine identity bootstrap credentials |
| `Infisical__ProjectId` | Your project ID |
| `Infisical__Environment` | `prod` |
| `Mail__Mode` | `Resend` |
| `Mail__From` | Current test sender `onboarding@resend.dev`; replace with a verified sender for public users |
| `Mail__ApiKey` | Restricted sending key |

The launch script selects Postgres, points the BFF at loopback API and respects Render's `PORT`. Database and email credentials are host secrets; optionally sync them from Infisical using your provider integration. They are not stored in the browser bundle.

Standard SMTP ports are blocked on Render free services, so the supplied blueprint uses the Resend HTTPS API. SMTP remains available for other hosts using `Mail__Mode=Smtp`, `Mail__Host`, `Mail__Port`, `Mail__Username`, `Mail__Password` and `Mail__From` with TLS enabled.

## 4. Apply migrations deliberately

The initial deployment used `INITIALIZE_DATABASE=true` and the flag was removed after the migration succeeded. For a new empty database, set the flag only for its first controlled deployment, then remove it. Normal restarts must not be schema changes.

For later releases, back up the database, review the migration SQL, and apply with a temporary migration identity in a separate deployment job. The runtime identity should not own schema changes. The first-deploy shortcut shares credentials; narrow those permissions after provisioning.

Health check: `/health/ready` checks the database and private API. `/health/live` checks process reachability only. Neither exposes keys/configuration. Both processes terminate together if one fails; Render can restart the container. There is no multi-host failover in this deployment.

## 5. Publish the SDK for free

The SDK is named `@ganeshcreatives/browser-sdk` under the owned npm account. Keep the package version synchronized with the app dependency.

```powershell
npm run pack:sdk
npm login
npm publish --workspace '@ganeshcreatives/browser-sdk' --access public
```

Use npm's supported authentication/2FA or trusted publishing process. Never commit npm tokens. Public publication exposes SDK source/contracts but not BFF/API secrets. Review the tarball before release. The existing SDK `.tgz` contains only compiled JavaScript, types, README and package metadata.

After app hosting, the same SDK is also reachable free at `https://YOUR_APP/sdk/browser-sdk.js`. Example served by the same application origin:

```js
import { SchoolClient } from '/sdk/browser-sdk.js';
const client = new SchoolClient();
await client.restore();
const students = await client.students();
```

The SDK always calls the consuming page's origin. Merely loading it from another host does not grant cross-origin API access. Another frontend needs its own same-origin BFF routing/configuration; do not loosen CORS or expose service secrets to make it work.

## 6. Production progression

Free hosting is a low-traffic starting environment. Before storing real family/student records, satisfy `RELEASE.md`: security behavior verification, restored backups, monitoring, lifecycle/privacy rules, staff authentication policy, trusted ingress, data residency, and service objectives. Move to paid persistent/redundant services when these requirements demand it. A single free container is not highly available.
