# School Portal browser SDK

Typed client for the same-origin School Portal BFF. Requires a secure browser context (HTTPS, or localhost for development).

```ts
import { SchoolClient } from '@school-portal/browser-sdk';
const school = new SchoolClient();
await school.login('parent@example.com', 'your-long-passphrase');
const students = await school.students();
const balance = await school.balance(students[0].id);
```

After a reload call `restore()` once. Handle `SESSION_REQUIRED`/`SESSION_EXPIRED` by showing login. Use `SchoolError.code`, `status`, `traceId`, and `retryAfter` for safe error handling. Do not automatically retry writes; retain the same `submissionId` and payload if a user explicitly retries an uncertain application submission.

Keys remain in tab memory. The browser signing value is visible to the user and to injected scripts. It is not an API service secret, identity proof, or replacement for API authorization. The SDK never receives API access tokens, allows arbitrary destinations, or forwards credentials to other origins.

Public npm scope `@school-portal` is a placeholder. Rename to a scope you own in both workspace package files and imports before publishing. The SDK can be built and packed locally without an npm account. No npm publication has been performed.
