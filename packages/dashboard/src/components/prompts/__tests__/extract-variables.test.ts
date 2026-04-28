import { extractVariables } from '../extract-variables.js';

describe('extractVariables', () => {
  it('returns variables in first-seen order, deduped', () => {
    const tmpl =
      'Hello {{name}}, your role is {{role}}. Reminder: {{name}} works on {{repo}}.';
    expect(extractVariables(tmpl)).toEqual(['name', 'role', 'repo']);
  });

  it('returns empty array when no tokens present', () => {
    expect(extractVariables('plain text with no tokens')).toEqual([]);
  });

  it('skips empty {{}} and trims whitespace inside', () => {
    expect(extractVariables('a {{}} b {{ name }} c')).toEqual(['name']);
  });

  it('honours the 64-character upper bound from the C# regex', () => {
    const huge = 'x'.repeat(80);
    // 80 chars >64 means the inner [^}]{1,64} bound rejects it — no match.
    expect(extractVariables(`prefix {{${huge}}} suffix`)).toEqual([]);
  });

  it('extracts from multi-line templates', () => {
    const tmpl = `# Plan\n\nIssue: {{issue_body}}\n\nFiles:\n{{file_list}}`;
    expect(extractVariables(tmpl)).toEqual(['issue_body', 'file_list']);
  });
});
