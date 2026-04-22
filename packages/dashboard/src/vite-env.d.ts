/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_BASE_URL?: string;
  readonly VITE_FEATURE_ADMIN_AUDIT_LOG?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
