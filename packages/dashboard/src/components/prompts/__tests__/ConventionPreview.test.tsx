// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ConventionPreview } from '../ConventionPreview.js';

const SUMMARIES = [
  { key: 'typescript', name: 'TypeScript + Node.js', description: 'Strict TS, ESM' },
  { key: 'go', name: 'Go', description: 'Stdlib, table-driven tests' },
];

const TS_FULL = {
  key: 'typescript',
  name: 'TypeScript + Node.js',
  description: 'Strict TS, ESM',
  conventions: 'Use TypeScript strict mode. Prefer async/await.',
};

describe('ConventionPreview', () => {
  const user = userEvent.setup();

  beforeEach(() => {
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (url.endsWith('/api/convention-templates')) {
        return new Response(JSON.stringify(SUMMARIES), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.endsWith('/api/convention-templates/typescript')) {
        return new Response(JSON.stringify(TS_FULL), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }
      if (url.endsWith('/api/convention-templates/go')) {
        return new Response(
          JSON.stringify({ ...TS_FULL, key: 'go', name: 'Go', conventions: 'Use stdlib.' }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      return new Response('not found', { status: 404 });
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('lists templates and previews the first one by default', async () => {
    render(<ConventionPreview />);
    await screen.findByText('TypeScript + Node.js');
    await screen.findByText('Go');
    // First template's conventions body loads automatically
    await screen.findByText(/Use TypeScript strict mode/);
  });

  it('switches preview when another template is clicked', async () => {
    render(<ConventionPreview />);
    await screen.findByText(/Use TypeScript strict mode/);
    await user.click(screen.getByRole('button', { name: /^Go/ }));
    await waitFor(() => expect(screen.getByText(/Use stdlib\./)).toBeInTheDocument());
  });

  it('copies the conventions body to clipboard when Copy is clicked', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      writable: true,
      value: { writeText, readText: vi.fn().mockResolvedValue('') },
    });
    render(<ConventionPreview />);
    await screen.findByText(/Use TypeScript strict mode/);
    await user.click(screen.getByRole('button', { name: /^copy$/i }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(TS_FULL.conventions));
    await screen.findByText(/copied/i);
  });
});
