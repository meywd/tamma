import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import path from 'path';

export default defineConfig({
  plugins: [tailwindcss(), react()],
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
  resolve: {
    alias: {
      // Stub Node.js built-ins that leak through @tamma/shared barrel exports.
      // Dashboard only uses types/constants from shared, but Vite follows
      // re-exports into server-side modules that import node:crypto etc.
      'node:crypto': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:fs': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:fs/promises': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:path': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:os': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:child_process': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:util': path.resolve(__dirname, 'src/node-shims.ts'),
    },
  },
  server: {
    port: 3000,
    open: true,
  },
});
