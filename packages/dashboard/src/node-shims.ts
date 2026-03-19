// Empty shim for Node.js built-ins that leak through @tamma/shared barrel exports.
// The dashboard never calls these — they're pulled in by Vite's bundler following
// re-exports to server-side modules.
export default {};
export const randomBytes = () => new Uint8Array(0);
export const createHmac = () => ({ update: () => ({ digest: () => '' }) });
export const createHash = () => ({ update: () => ({ digest: () => '' }) });
export const randomUUID = () => '00000000-0000-0000-0000-000000000000';
