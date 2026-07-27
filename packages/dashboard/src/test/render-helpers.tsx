import { render, type RenderOptions, type RenderResult } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import type { ReactElement } from 'react';

/**
 * Render helper that wraps component in MemoryRouter.
 */
export function renderWithRouter(
  ui: ReactElement,
  options?: RenderOptions & { initialEntries?: string[] },
): RenderResult {
  const { initialEntries = ['/admin'], ...renderOptions } = options ?? {};
  return render(ui, {
    wrapper: ({ children }) => (
      <MemoryRouter initialEntries={initialEntries}>{children}</MemoryRouter>
    ),
    ...renderOptions,
  });
}
