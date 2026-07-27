// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConventionSelector } from './ConventionSelector.js';

const CONVENTIONS = [
  { key: 'typescript-node', name: 'TypeScript/Node', description: 'Node.js + strict TS' },
  { key: 'python-fastapi', name: 'Python/FastAPI', description: 'FastAPI conventions' },
];

function mockFetch(ok = true) {
  return vi.fn().mockImplementation((url: string) => {
    if (url.endsWith('/api/convention-templates')) {
      return Promise.resolve({
        ok,
        json: () => Promise.resolve(CONVENTIONS),
      });
    }
    const key = url.split('/').pop() ?? '';
    return Promise.resolve({
      ok,
      json: () =>
        Promise.resolve({
          key,
          name: 'Test Template',
          description: 'desc',
          conventions: 'Use strict TypeScript. Prefer async/await.',
        }),
    });
  });
}

describe('ConventionSelector', () => {
  const user = userEvent.setup();
  const originalFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('lists convention templates in dropdown', async () => {
    globalThis.fetch = mockFetch() as typeof fetch;
    render(<ConventionSelector onInsert={vi.fn()} />);
    const select = screen.getByLabelText('Convention Template') as HTMLSelectElement;
    await waitFor(() => {
      const values = Array.from(select.options).map((o) => o.value);
      expect(values).toContain('typescript-node');
      expect(values).toContain('python-fastapi');
    });
  });

  it('calls onInsert with template text after selection and insert click', async () => {
    globalThis.fetch = mockFetch() as typeof fetch;
    const onInsert = vi.fn();
    render(<ConventionSelector onInsert={onInsert} />);
    const select = screen.getByLabelText('Convention Template') as HTMLSelectElement;
    await waitFor(() =>
      expect(
        Array.from(select.options).map((o) => o.value),
      ).toContain('typescript-node'),
    );
    await user.selectOptions(select, 'typescript-node');
    const insertBtn = await screen.findByRole('button', { name: /Insert into Template/i });
    await user.click(insertBtn);
    await waitFor(() =>
      expect(onInsert).toHaveBeenCalledWith(
        expect.stringContaining('Use strict TypeScript'),
      ),
    );
  });
});
