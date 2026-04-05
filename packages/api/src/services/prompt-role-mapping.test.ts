/**
 * Tests for prompt-role alignment.
 *
 * Validates that prompts exist for every workflow step that requires them.
 * This ensures the prompt registry is complete for the autonomous workflow.
 */

import { describe, it, expect, beforeAll } from 'vitest';
import { PromptStore } from './prompt-store.js';

let store: PromptStore;

beforeAll(async () => {
  store = new PromptStore({ filePath: '/tmp/tamma-test-role-mapping.json' });
  await store.initialize();
});

describe('Prompt-Role Alignment', () => {
  const scanningRoles = ['developer', 'tester', 'security', 'devops', 'architect'] as const;

  it('context-scan action exists for all 5 scanning roles', async () => {
    for (const role of scanningRoles) {
      const template = await store.get(role, 'context-scan');
      expect(template, `Missing context-scan for ${role}`).toBeDefined();
      expect(template!.template.length).toBeGreaterThan(0);
    }
  });

  it('plan action exists for architect role', async () => {
    const template = await store.get('architect', 'plan');
    expect(template).toBeDefined();
    expect(template!.variables).toContain('workItemJson');
  });

  it('plan action exists for all 8 roles', async () => {
    const roles = ['developer', 'tester', 'security', 'devops', 'architect', 'product_owner', 'senior_developer', 'tech_writer'] as const;
    for (const role of roles) {
      const template = await store.get(role, 'plan');
      expect(template, `Missing plan for ${role}`).toBeDefined();
    }
  });

  it('plan-review action exists for all 7 review roles', async () => {
    const reviewRoles = ['developer', 'tester', 'security', 'devops', 'architect', 'product_owner', 'senior_developer'] as const;
    for (const role of reviewRoles) {
      const template = await store.get(role, 'plan-review');
      expect(template, `Missing plan-review for ${role}`).toBeDefined();
      expect(template!.variables).toContain('planJson');
    }
  });

  it('implement action exists for developer role', async () => {
    const template = await store.get('developer', 'implement');
    expect(template).toBeDefined();
    expect(template!.variables).toContain('codeContext');
    expect(template!.enableTools).toBe(true);
  });

  it('write-tests action exists for tester role', async () => {
    const template = await store.get('tester', 'write-tests');
    expect(template).toBeDefined();
    expect(template!.variables).toContain('sourceCode');
  });

  it('code-review action exists for senior_developer role', async () => {
    const template = await store.get('senior_developer', 'code-review');
    expect(template).toBeDefined();
    expect(template!.variables).toContain('diff');
    expect(template!.enableTools).toBe(false);
  });

  it('triage action exists for security, developer, devops, tester roles', async () => {
    const triageRoles = ['security', 'developer', 'devops', 'tester'] as const;
    for (const role of triageRoles) {
      const template = await store.get(role, 'triage');
      expect(template, `Missing triage for ${role}`).toBeDefined();
      expect(template!.variables).toContain('issueJson');
    }
  });

  it('summarize action exists for product_owner role', async () => {
    const template = await store.get('product_owner', 'summarize');
    expect(template).toBeDefined();
    expect(template!.variables).toContain('findings');
    expect(template!.enableTools).toBe(false);
  });

  it('debug action exists for developer role', async () => {
    const template = await store.get('developer', 'debug');
    expect(template).toBeDefined();
    expect(template!.variables).toContain('stackTrace');
    expect(template!.enableTools).toBe(true);
  });

  it('refactor action exists for all 8 roles', async () => {
    const roles = ['developer', 'tester', 'security', 'devops', 'architect', 'product_owner', 'senior_developer', 'tech_writer'] as const;
    for (const role of roles) {
      const template = await store.get(role, 'refactor');
      expect(template, `Missing refactor for ${role}`).toBeDefined();
    }
  });
});
