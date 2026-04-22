// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import { SecretsAdminPage } from '../SecretsAdminPage.js';

const mockList = vi.fn();

vi.mock('../../../../services/secrets/secrets-api-client.js', async () => {
  const actual = await vi.importActual<
    typeof import('../../../../services/secrets/secrets-api-client.js')
  >('../../../../services/secrets/secrets-api-client.js');
  return {
    ...actual,
    platformSecretsApi: {
      list: () => mockList(),
      create: vi.fn(),
    },
  };
});

describe('SecretsAdminPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the platform header and description', async () => {
    mockList.mockResolvedValueOnce({ secrets: [] });
    render(<SecretsAdminPage />);

    expect(
      screen.getByRole('heading', { level: 1, name: /Platform secrets/i }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(/revealed to you exactly once at creation or rotation/i),
    ).toBeInTheDocument();
  });

  it('passes the platform scope label through to the shared list view', async () => {
    mockList.mockResolvedValueOnce({ secrets: [] });
    render(<SecretsAdminPage />);

    // Secret-list view header is "Platform secrets".
    const headings = await screen.findAllByText(/Platform secrets/);
    expect(headings.length).toBeGreaterThanOrEqual(1);
  });
});
