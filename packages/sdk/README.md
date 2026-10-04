# School Portal browser SDK

Typed client for the same-origin School Portal BFF. Requires a secure browser context (HTTPS, or localhost for development).

```ts
import { SchoolClient } from '@ganeshcreatives/browser-sdk';
const school = new SchoolClient();
await school.login('parent@example.com', 'your-long-passphrase');
const students = await school.students();
const balance = await school.balance(students[0].id);
```

After a reload call `restore()` once. Handle `SESSION_REQUIRED`/`SESSION_EXPIRED` by showing login. Use `SchoolError.code`, `status`, `traceId`, and `retryAfter` for safe error handling. Do not automatically retry writes; retain the same `submissionId` and payload if a user explicitly retries an uncertain application submission.

Keys remain in tab memory. The browser signing value is visible to the user and to injected scripts. It is not an API service secret, identity proof, or replacement for API authorization. The SDK never receives API access tokens, allows arbitrary destinations, or forwards credentials to other origins.

The package uses the owned `@ganeshcreatives` npm scope. The SDK can be built and packed locally; public npm publication is a separate release step.
