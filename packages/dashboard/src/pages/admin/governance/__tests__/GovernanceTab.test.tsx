// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { GovernanceTab } from '../GovernanceTab.js';

// Stub the panels — each has its own tests; this file covers the shell.
vi.mock('../DialPanel.js', () => ({
  DialPanel: () => <div data-testid="dial-panel">Dial Panel</div>,
}));
vi.mock('../OverridesPanel.js', () => ({
  OverridesPanel: () => <div data-testid="overrides-panel">Overrides Panel</div>,
}));
vi.mock('../AuthorizationsPanel.js', () => ({
  AuthorizationsPanel: () => <div data-testid="authorizations-panel">Authorizations Panel</div>,
}));

describe('GovernanceTab', () => {
  const user = userEvent.setup();

  it('defaults to the Dial & catalog section', () => {
    render(<GovernanceTab />);
    expect(screen.getByTestId('dial-panel')).toBeInTheDocument();
    expect(screen.queryByTestId('overrides-panel')).not.toBeInTheDocument();
  });

  it('switches to Overrides', async () => {
    render(<GovernanceTab />);
    await user.click(screen.getByText('Overrides'));
    expect(screen.getByTestId('overrides-panel')).toBeInTheDocument();
    expect(screen.queryByTestId('dial-panel')).not.toBeInTheDocument();
  });

  it('switches to Pending authorizations', async () => {
    render(<GovernanceTab />);
    await user.click(screen.getByText('Pending authorizations'));
    expect(screen.getByTestId('authorizations-panel')).toBeInTheDocument();
  });

  it('sub-tab navigation has an aria-label', () => {
    render(<GovernanceTab />);
    expect(
      document.querySelector('nav[aria-label="Governance sub-tabs"]'),
    ).toBeInTheDocument();
  });
});
