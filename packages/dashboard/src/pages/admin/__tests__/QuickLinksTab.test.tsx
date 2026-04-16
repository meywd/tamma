import { render, screen } from '@testing-library/react';
import { QuickLinksTab } from '../QuickLinksTab.js';

describe('QuickLinksTab', () => {
  it('renders all 4 link cards', () => {
    render(<QuickLinksTab />);
    expect(screen.getByText('ELSA Studio')).toBeInTheDocument();
    expect(screen.getByText('OpenSearch Dashboards')).toBeInTheDocument();
    expect(screen.getByText('GitHub Repository')).toBeInTheDocument();
    expect(screen.getByText('RabbitMQ Management')).toBeInTheDocument();
  });

  it('all links open in new tab with secure rel attribute', () => {
    render(<QuickLinksTab />);
    const links = screen.getAllByRole('link');
    for (const link of links) {
      expect(link).toHaveAttribute('target', '_blank');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    }
  });

  it('ELSA link points to elsa.tamma.dev', () => {
    render(<QuickLinksTab />);
    const elsaLink = screen.getByText('ELSA Studio').closest('a');
    expect(elsaLink).toHaveAttribute('href', 'https://elsa.tamma.dev');
  });

  it('GitHub link points to repository', () => {
    render(<QuickLinksTab />);
    const ghLink = screen.getByText('GitHub Repository').closest('a');
    expect(ghLink).toHaveAttribute('href', 'https://github.com/meywd/tamma');
  });
});
