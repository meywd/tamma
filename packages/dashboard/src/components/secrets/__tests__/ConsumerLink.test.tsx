// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { ConsumerLink } from '../ConsumerLink.js';

describe('ConsumerLink', () => {
  it('renders postgres consumers with the RLS runbook link', () => {
    render(
      <ConsumerLink
        consumer={{ type: 'postgres', target: 'tamma_app' }}
      />,
    );

    expect(screen.getByText(/Postgres role/)).toBeInTheDocument();
    expect(screen.getByText('tamma_app')).toBeInTheDocument();
    const link = screen.getByRole('link', { name: /RLS runbook/ });
    expect(link).toHaveAttribute('href', '/admin/runtime/dbcontexts');
  });

  it('renders cranl consumers with a link to the tenant page when tenantId is provided', () => {
    render(
      <ConsumerLink
        consumer={{ type: 'cranl', target: 'app_abc123' }}
        tenantId="11111111-1111-1111-1111-111111111111"
      />,
    );

    expect(screen.getByText(/Cranl app/)).toBeInTheDocument();
    expect(screen.getByText('app_abc123')).toBeInTheDocument();
    const link = screen.getByRole('link', { name: /tenant page/ });
    expect(link).toHaveAttribute(
      'href',
      '/admin/tenants/11111111-1111-1111-1111-111111111111',
    );
  });

  it('renders github_webhook consumers without the tenant link', () => {
    render(
      <ConsumerLink
        consumer={{ type: 'github_webhook', target: '12345' }}
      />,
    );

    expect(screen.getByText(/GitHub installation/)).toBeInTheDocument();
    expect(screen.getByText('12345')).toBeInTheDocument();
  });

  it('renders hmac_shared consumers', () => {
    render(
      <ConsumerLink
        consumer={{ type: 'hmac_shared', target: 'tamma-engine:sign' }}
      />,
    );

    expect(screen.getByText(/HMAC shared with/)).toBeInTheDocument();
    expect(screen.getByText('tamma-engine:sign')).toBeInTheDocument();
  });

  it('falls through to the generic label for unknown types', () => {
    render(
      <ConsumerLink
        consumer={{ type: 'generic', target: 'x', label: 'Custom label' }}
      />,
    );

    expect(screen.getByText('Custom label')).toBeInTheDocument();
  });
});
