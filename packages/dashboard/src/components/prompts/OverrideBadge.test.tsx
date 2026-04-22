// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { OverrideBadge } from './OverrideBadge.js';

describe('OverrideBadge', () => {
  it('renders "Override" label when source is user', () => {
    render(<OverrideBadge source="user" />);
    expect(screen.getByText('Override')).toBeInTheDocument();
  });

  it('renders "Default" label when source is system', () => {
    render(<OverrideBadge source="system" />);
    expect(screen.getByText('Default')).toBeInTheDocument();
  });

  it('applies blue styling for overrides', () => {
    render(<OverrideBadge source="user" />);
    const badge = screen.getByText('Override');
    expect(badge.className).toMatch(/bg-blue-100/);
  });

  it('applies gray styling for defaults', () => {
    render(<OverrideBadge source="system" />);
    const badge = screen.getByText('Default');
    expect(badge.className).toMatch(/bg-gray-100/);
  });
});
