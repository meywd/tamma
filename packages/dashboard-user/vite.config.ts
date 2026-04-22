import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [tailwindcss(), react()],
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
  server: {
    port: 3002,
    open: false,
    proxy: {
      // During local dev, send API calls to the .NET API directly.
      '/api': {
        target: 'http://localhost:5001',
        changeOrigin: true,
      },
    },
  },
});
