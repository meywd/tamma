import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    include: ['**/*.test.ts'],
    setupFiles: ['./src/vitest.setup.ts'],
  },
})
