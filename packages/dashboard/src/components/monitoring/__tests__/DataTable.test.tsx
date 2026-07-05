// @vitest-environment jsdom
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DataTable, type DataTableColumn } from '../DataTable.js';

interface Row {
  name: string;
  count: number;
}

const columns: DataTableColumn<Row>[] = [
  { key: 'name', header: 'Name', accessor: (r) => r.name, sortable: true },
  { key: 'count', header: 'Count', accessor: (r) => r.count, sortable: true, align: 'right' },
];

const rows: Row[] = [
  { name: 'charlie', count: 2 },
  { name: 'alpha', count: 30 },
  { name: 'bravo', count: 1 },
];

function bodyText(): string[] {
  const table = screen.getByRole('table');
  const bodyRows = within(table).getAllByRole('row').slice(1); // drop header
  return bodyRows.map((r) => within(r).getAllByRole('cell')[0]?.textContent ?? '');
}

describe('DataTable', () => {
  it('renders all rows by default', () => {
    render(<DataTable columns={columns} rows={rows} />);
    expect(bodyText()).toEqual(['charlie', 'alpha', 'bravo']);
  });

  it('sorts ascending then descending when a sortable header is clicked', async () => {
    render(<DataTable columns={columns} rows={rows} />);
    await userEvent.click(screen.getByRole('button', { name: /Name/ }));
    expect(bodyText()).toEqual(['alpha', 'bravo', 'charlie']);
    await userEvent.click(screen.getByRole('button', { name: /Name/ }));
    expect(bodyText()).toEqual(['charlie', 'bravo', 'alpha']);
  });

  it('filters rows by the text query across accessors', async () => {
    render(<DataTable columns={columns} rows={rows} />);
    await userEvent.type(screen.getByLabelText('Filter table'), 'alph');
    expect(bodyText()).toEqual(['alpha']);
  });

  it('paginates and exposes prev/next controls', async () => {
    const many: Row[] = Array.from({ length: 12 }, (_, i) => ({ name: `n${i}`, count: i }));
    render(<DataTable columns={columns} rows={many} pageSize={5} />);
    expect(screen.getByText('Page 1 of 3')).toBeInTheDocument();
    const table = screen.getByRole('table');
    expect(within(table).getAllByRole('row')).toHaveLength(6); // header + 5
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument();
  });

  it('toggles column visibility', async () => {
    render(<DataTable columns={columns} rows={rows} />);
    expect(screen.getByRole('columnheader', { name: /Count/ })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Columns' }));
    await userEvent.click(screen.getByRole('checkbox', { name: 'Count' }));
    expect(screen.queryByRole('columnheader', { name: /Count/ })).not.toBeInTheDocument();
  });

  it('renders an empty state when there are no rows', () => {
    render(<DataTable columns={columns} rows={[]} emptyTitle="No data yet" />);
    expect(screen.getByTestId('empty-state')).toBeInTheDocument();
    expect(screen.getByText('No data yet')).toBeInTheDocument();
  });
});
