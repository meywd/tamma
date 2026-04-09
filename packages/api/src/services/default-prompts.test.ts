/**
 * Tests for default prompt templates and prompt rendering.
 *
 * Validates:
 * - All 80 templates exist (8 roles × 10 actions)
 * - Template structure (non-empty fields, variables, maxTokens)
 * - Rendering produces no leftover {{placeholders}}
 */

import { describe, it, expect, beforeAll } from 'vitest';
import { getDefaultPrompts, VALID_ROLES, VALID_ACTIONS } from './default-prompts.js';
import type { PromptTemplate } from './default-prompts.js';
import { PromptStore } from './prompt-store.js';

let templates: PromptTemplate[];

beforeAll(() => {
  templates = getDefaultPrompts();
});

describe('Default Prompt Templates', () => {
  it('has exactly 80 templates (8 roles × 10 actions)', () => {
    expect(VALID_ROLES.length).toBe(8);
    expect(VALID_ACTIONS.length).toBe(10);
    expect(templates.length).toBe(80);
  });

  it('every template has non-empty systemPrompt', () => {
    for (const t of templates) {
      expect(t.systemPrompt.length, `${t.role}/${t.action} systemPrompt`).toBeGreaterThan(0);
    }
  });

  it('every template has non-empty template body', () => {
    for (const t of templates) {
      expect(t.template.length, `${t.role}/${t.action} template`).toBeGreaterThan(0);
    }
  });

  it('every template has at least 1 variable', () => {
    for (const t of templates) {
      expect(t.variables.length, `${t.role}/${t.action} variables`).toBeGreaterThanOrEqual(1);
    }
  });

  it('every declared variable appears as {{variable}} in template or systemPrompt', () => {
    for (const t of templates) {
      for (const v of t.variables) {
        const inTemplate = t.template.includes(`{{${v}}}`);
        const inSystem = t.systemPrompt.includes(`{{${v}}}`);
        expect(
          inTemplate || inSystem,
          `Variable '${v}' in ${t.role}/${t.action} not found as {{${v}}} in template or systemPrompt`,
        ).toBe(true);
      }
    }
  });

  it('no duplicate (role, action) pairs', () => {
    const keys = templates.map((t) => `${t.role}:${t.action}`);
    const unique = new Set(keys);
    expect(unique.size).toBe(templates.length);
  });

  it('every template has maxTokens > 0', () => {
    for (const t of templates) {
      expect(t.maxTokens, `${t.role}/${t.action} maxTokens`).toBeGreaterThan(0);
    }
  });

  it('every template has version === 1', () => {
    for (const t of templates) {
      expect(t.version, `${t.role}/${t.action} version`).toBe(1);
    }
  });

  it('every template has valid ISO 8601 timestamps', () => {
    for (const t of templates) {
      expect(Date.parse(t.createdAt), `${t.role}/${t.action} createdAt`).not.toBeNaN();
      expect(Date.parse(t.updatedAt), `${t.role}/${t.action} updatedAt`).not.toBeNaN();
    }
  });
});

describe('Prompt Rendering — all 80 combos', () => {
  let store: PromptStore;

  beforeAll(async () => {
    store = new PromptStore({ filePath: '/tmp/tamma-test-render-prompts.json' });
    await store.initialize();
  });

  for (const role of VALID_ROLES) {
    for (const action of VALID_ACTIONS) {
      it(`renders ${role}/${action} without leftover {{placeholders}}`, async () => {
        const template = await store.get(role, action);
        expect(template).toBeDefined();

        // Build sample variables from the template's declared variable list
        const variables: Record<string, string> = {};
        for (const v of template!.variables) {
          variables[v] = `sample-${v}`;
        }

        const rendered = await store.render(role, action, { variables });
        expect(rendered).toBeDefined();

        // No leftover {{placeholders}} should remain
        const leftoverMatches = rendered!.renderedTemplate.match(/\{\{[^}]+\}\}/g);
        expect(
          leftoverMatches,
          `Leftover placeholders in ${role}/${action}: ${leftoverMatches?.join(', ')}`,
        ).toBeNull();

        // System prompt should also be clean
        const systemLeftover = rendered!.renderedSystemPrompt.match(/\{\{[^}]+\}\}/g);
        expect(
          systemLeftover,
          `Leftover in systemPrompt ${role}/${action}: ${systemLeftover?.join(', ')}`,
        ).toBeNull();

        expect(rendered!.unresolvedVariables).toHaveLength(0);
      });
    }
  }
});
