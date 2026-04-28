import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: 'dist',
    // Pin to vite 6's previous "modules" baseline so we don't tighten the
    // supported browser floor when bumping vite to 8 (which now defaults to
    // baseline-widely-available = Chrome 111/Safari 16.4).
    target: ['es2020', 'edge88', 'firefox78', 'chrome87', 'safari14'],
  },
  assetsInclude: ['**/*.md'],
});
