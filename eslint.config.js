import eslint from '@eslint/js';
import tseslint from '@typescript-eslint/eslint-plugin';
import tsparser from '@typescript-eslint/parser';
import prettierConfig from 'eslint-config-prettier/flat';

export default [
  eslint.configs.recommended,
  {
    files: ['**/*.ts', '**/*.tsx'],
    languageOptions: {
      parser: tsparser,
      parserOptions: {
        ecmaVersion: 'latest',
        sourceType: 'module',
        project: './tsconfig.json',
      },
      globals: {
        NodeJS: true,
        console: true,
        process: true,
        Buffer: true,
      },
    },
    plugins: {
      '@typescript-eslint': tseslint,
    },
    rules: {
      ...tseslint.configs['recommended'].rules,
      ...tseslint.configs['recommended-requiring-type-checking'].rules,

      '@typescript-eslint/explicit-function-return-type': ['error', {
        allowExpressions: true,
        allowTypedFunctionExpressions: true,
      }],
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/no-unused-vars': ['error', {
        argsIgnorePattern: '^_',
        varsIgnorePattern: '^_',
      }],
      '@typescript-eslint/no-floating-promises': 'error',
      '@typescript-eslint/await-thenable': 'error',
      '@typescript-eslint/no-misused-promises': 'error',
      '@typescript-eslint/promise-function-async': 'error',
      '@typescript-eslint/strict-boolean-expressions': ['error', {
        allowString: false,
        allowNumber: false,
        allowNullableObject: false,
      }],

      'no-console': ['warn', { allow: ['warn', 'error'] }],
      'prefer-const': 'error',
      'no-var': 'error',
      'eqeqeq': ['error', 'always'],
      'curly': ['error', 'all'],
    },
  },
  {
    files: ['**/*.test.ts', '**/*.spec.ts'],
    rules: {
      '@typescript-eslint/no-explicit-any': 'off',
      '@typescript-eslint/no-non-null-assertion': 'off',
    },
  },
  // Forbid raw fetch('/api/...') in dashboard pages and components. The
  // shape of bug we keep finding (path drift, response-shape drift, header
  // drift) is *always* a page or component that bypasses the typed client
  // in services/. Funneling every API call through services/ means the
  // contract lives in one place, types catch shape changes, and a path
  // rename is a single edit instead of a hunt across pages. Tests, services,
  // and hooks are fine to fetch directly.
  {
    files: [
      'packages/dashboard/src/pages/**/*.{ts,tsx}',
      'packages/dashboard/src/components/**/*.{ts,tsx}',
      'packages/dashboard-user/src/pages/**/*.{ts,tsx}',
      'packages/dashboard-user/src/components/**/*.{ts,tsx}',
    ],
    ignores: [
      '**/*.test.{ts,tsx}',
      '**/*.spec.{ts,tsx}',
    ],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector:
            "CallExpression[callee.name='fetch'][arguments.0.type='Literal'][arguments.0.value=/^\\/api\\//]",
          message:
            'Raw fetch("/api/...") is not allowed in pages/components. Route through a typed client in services/ — the page becomes thin presentation, and path/shape drift gets caught at the boundary instead of in production.',
        },
        {
          selector:
            "CallExpression[callee.name='fetch'][arguments.0.type='TemplateLiteral'][arguments.0.quasis.0.value.raw=/^\\/api\\//]",
          message:
            'Raw fetch(`/api/...`) is not allowed in pages/components. Route through a typed client in services/.',
        },
      ],
    },
  },
  prettierConfig,
  {
    ignores: [
      '**/node_modules/**',
      '**/dist/**',
      '**/coverage/**',
      '**/*.config.js',
      '**/.tsbuildinfo',
    ],
  },
];
