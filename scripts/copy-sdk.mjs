import { mkdir, copyFile } from 'node:fs/promises';
const target = new URL('../server/School.Bff/wwwroot/sdk/', import.meta.url);
await mkdir(target,{recursive:true});
await copyFile(new URL('../packages/sdk/dist/index.js',import.meta.url),new URL('browser-sdk.js',target));
await copyFile(new URL('../packages/sdk/dist/index.d.ts',import.meta.url),new URL('browser-sdk.d.ts',target));
