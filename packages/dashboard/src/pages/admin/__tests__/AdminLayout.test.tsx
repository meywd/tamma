import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AdminLayout } from '../AdminLayout.js';

// Mock all tab components to simple stubs
vi.mock('../UsersTab.js', () => ({
  UsersTab: () => <div data-testid="users-tab">Users Tab Content</div>,
}));
vi.mock('../ApiKeysTab.js', () => ({
  ApiKeysTab: () => <div data-testid="api-keys-tab">API Keys Tab Content</div>,
}));
vi.mock('../HealthTab.js', () => ({
  HealthTab: () => <div data-testid="health-tab">Health Tab Content</div>,
}));
vi.mock('../QuickLinksTab.js', () => ({
  QuickLinksTab: () => <div data-testid="quick-links-tab">Quick Links Tab Content</div>,
}));
vi.mock('../AuditLogTab.js', () => ({
  AuditLogTab: () => <div data-testid="audit-log-tab">Audit Log Tab Content</div>,
}));

describe('AdminLayout', () => {
  const user = userEvent.setup();

  it('defaults to Users tab', () => {
    render(<AdminLayout />);
    expect(screen.getByTestId('users-tab')).toBeInTheDocument();
    expect(screen.queryByTestId('api-keys-tab')).not.toBeInTheDocument();
  });

  it('clicking API Keys tab switches content', async () => {
    render(<AdminLayout />);
    await user.click(screen.getByText('API Keys'));
    expect(screen.getByTestId('api-keys-tab')).toBeInTheDocument();
    expect(screen.queryByTestId('users-tab')).not.toBeInTheDocument();
  });

  it('clicking System Health tab switches content', async () => {
    render(<AdminLayout />);
    await user.click(screen.getByText('System Health'));
    expect(screen.getByTestId('health-tab')).toBeInTheDocument();
  });

  it('clicking Quick Links tab switches content', async () => {
    render(<AdminLayout />);
    await user.click(screen.getByText('Quick Links'));
    expect(screen.getByTestId('quick-links-tab')).toBeInTheDocument();
  });

  it('active tab has border-blue-500 class', () => {
    render(<AdminLayout />);
    // Users tab should be active by default
    const usersBtn = screen.getByText('Users');
    expect(usersBtn.className).toContain('border-blue-500');
    expect(usersBtn.className).toContain('text-blue-600');
  });

  it('inactive tab has border-transparent class', () => {
    render(<AdminLayout />);
    const apiKeysBtn = screen.getByText('API Keys');
    expect(apiKeysBtn.className).toContain('border-transparent');
  });

  it('tab navigation has aria-label', () => {
    render(<AdminLayout />);
    const nav = document.querySelector('nav[aria-label="Admin tabs"]');
    expect(nav).toBeInTheDocument();
  });

  it('renders Admin Panel heading', () => {
    render(<AdminLayout />);
    expect(screen.getByText('Admin Panel')).toBeInTheDocument();
  });

  it('shows Audit Log tab', async () => {
    render(<AdminLayout />);
    const auditLogBtn = screen.getByText('Audit Log');
    expect(auditLogBtn).toBeInTheDocument();
    await user.click(auditLogBtn);
    expect(screen.getByTestId('audit-log-tab')).toBeInTheDocument();
  });
});
