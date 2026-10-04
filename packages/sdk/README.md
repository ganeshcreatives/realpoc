# School Portal browser SDK

Typed client for the same-origin School Portal BFF. Requires a secure browser context (HTTPS, or localhost for development).

```ts
import { SchoolClient } from '@ganeshcreatives/browser-sdk';
const school = new SchoolClient();
await school.login('parent@example.com', 'your-long-passphrase');
const students = await school.students();
const balance = await school.balance(students[0].id);
```

After a reload call `restore()` once. Handle `SESSION_REQUIRED`/`SESSION_EXPIRED` by showing login. Use `SchoolError.code`, `status`, `traceId`, and `retryAfter` for safe error handling. The default request timeout is 15 seconds; a slow free host can use `new SchoolClient({ timeoutMs: 90000 })`. Do not automatically retry writes; retain the same `submissionId` and payload if a user explicitly retries an uncertain application submission.

Keys remain in tab memory. The browser signing value is visible to the user and to injected scripts. It is not an API service secret, identity proof, or replacement for API authorization. The SDK never receives API access tokens, allows arbitrary destinations, or forwards credentials to other origins.

Install the public package with `npm i @ganeshcreatives/browser-sdk`. The SDK can also be built and packed locally. Licensed under MIT; see `LICENSE` in this package.
