import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import path from 'path';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      'node:crypto': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:fs': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:fs/promises': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:path': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:os': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:child_process': path.resolve(__dirname, 'src/node-shims.ts'),
      'node:util': path.resolve(__dirname, 'src/node-shims.ts'),
    },
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    css: false,
  },
});
