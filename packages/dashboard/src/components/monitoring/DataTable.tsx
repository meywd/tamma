/**
 * DataTable — generic, client-side sortable / filterable table with pagination
 * and a column-visibility toggle. Story 23-12 (AC4).
 *
 * Kept dependency-free and fully controlled internally so monitoring pages only
 * supply columns + rows. Sorting/filtering use each column's `accessor`.
 */

import { useMemo, useState, type JSX, type ReactNode } from 'react';
import { EmptyState } from './EmptyState.js';

export type CellValue = string | number | boolean | null | undefined;

export interface DataTableColumn<T> {
  key: string;
  header: string;
  /** Extracts a primitive used for sorting and text filtering. */
  accessor?: (row: T) => CellValue;
  /** Custom cell renderer; falls back to the accessor's string value. */
  render?: (row: T) => ReactNode;
  sortable?: boolean;
  align?: 'left' | 'right' | 'center';
  /** Allow the user to hide this column. Defaults to true. */
  hideable?: boolean;
  defaultHidden?: boolean;
}

interface DataTableProps<T> {
  columns: DataTableColumn<T>[];
  rows: T[];
  getRowId?: (row: T, index: number) => string;
  pageSize?: number;
  filterable?: boolean;
  filterPlaceholder?: string;
  initialSort?: { key: string; direction: 'asc' | 'desc' };
  emptyTitle?: string;
  emptyMessage?: string;
  onRowClick?: (row: T) => void;
  className?: string;
}

type SortDirection = 'asc' | 'desc';

const ALIGN_CLASS: Record<'left' | 'right' | 'center', string> = {
  left: 'text-left',
  right: 'text-right',
  center: 'text-center',
};

function compareCells(a: CellValue, b: CellValue): number {
  if (a == null && b == null) return 0;
  if (a == null) return -1;
  if (b == null) return 1;
  if (typeof a === 'number' && typeof b === 'number') return a - b;
  return String(a).localeCompare(String(b));
}

export function DataTable<T>({
  columns,
  rows,
  getRowId,
  pageSize = 10,
  filterable = true,
  filterPlaceholder = 'Filter…',
  initialSort,
  emptyTitle = 'No results',
  emptyMessage = 'Nothing matches the current filters.',
  onRowClick,
  className = '',
}: DataTableProps<T>): JSX.Element {
  const [sortKey, setSortKey] = useState<string | null>(initialSort?.key ?? null);
  const [sortDir, setSortDir] = useState<SortDirection>(initialSort?.direction ?? 'asc');
  const [filter, setFilter] = useState('');
  const [page, setPage] = useState(1);
  const [hidden, setHidden] = useState<Set<string>>(
    () => new Set(columns.filter((c) => c.defaultHidden).map((c) => c.key)),
  );
  const [menuOpen, setMenuOpen] = useState(false);

  const visibleColumns = columns.filter((c) => !hidden.has(c.key));

  const filtered = useMemo(() => {
    const q = filter.trim().toLowerCase();
    if (q === '') return rows;
    const accessors = columns.map((c) => c.accessor).filter((a): a is (row: T) => CellValue => !!a);
    return rows.filter((row) =>
      accessors.some((accessor) => {
        const value = accessor(row);
        return value != null && String(value).toLowerCase().includes(q);
      }),
    );
  }, [rows, filter, columns]);

  const sorted = useMemo(() => {
    if (sortKey === null) return filtered;
    const col = columns.find((c) => c.key === sortKey);
    const accessor = col?.accessor;
    if (!accessor) return filtered;
    const copy = [...filtered];
    copy.sort((a, b) => {
      const cmp = compareCells(accessor(a), accessor(b));
      return sortDir === 'asc' ? cmp : -cmp;
    });
    return copy;
  }, [filtered, sortKey, sortDir, columns]);

  const totalPages = Math.max(1, Math.ceil(sorted.length / pageSize));
  const currentPage = Math.min(page, totalPages);
  const start = (currentPage - 1) * pageSize;
  const pageRows = sorted.slice(start, start + pageSize);

  const toggleSort = (col: DataTableColumn<T>): void => {
    if (!col.sortable || !col.accessor) return;
    if (sortKey === col.key) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortKey(col.key);
      setSortDir('asc');
    }
  };

  const toggleColumn = (key: string): void => {
    setHidden((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  };

  const hideableColumns = columns.filter((c) => c.hideable !== false);

  return (
    <div data-testid="data-table" className={className}>
      {(filterable || hideableColumns.length > 0) && (
        <div className="mb-3 flex items-center justify-between gap-3">
          {filterable ? (
            <input
              type="search"
              value={filter}
              onChange={(e) => {
                setFilter(e.target.value);
                setPage(1);
              }}
              placeholder={filterPlaceholder}
              aria-label="Filter table"
              className="w-56 rounded-md border border-gray-300 px-3 py-1.5 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500 dark:border-gray-600 dark:bg-gray-800 dark:text-gray-100"
            />
          ) : (
            <span />
          )}

          {hideableColumns.length > 0 && (
            <div className="relative">
              <button
                type="button"
                onClick={() => setMenuOpen((o) => !o)}
                aria-expanded={menuOpen}
                aria-haspopup="menu"
                className="rounded-md border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50 dark:border-gray-600 dark:text-gray-200 dark:hover:bg-gray-700"
              >
                Columns
              </button>
              {menuOpen && (
                <div
                  role="menu"
                  className="absolute right-0 z-10 mt-1 w-48 rounded-md border border-gray-200 bg-white p-2 shadow-lg dark:border-gray-700 dark:bg-gray-800"
                >
                  {hideableColumns.map((col) => (
                    <label
                      key={col.key}
                      className="flex cursor-pointer items-center gap-2 rounded px-2 py-1 text-sm text-gray-700 hover:bg-gray-50 dark:text-gray-200 dark:hover:bg-gray-700"
                    >
                      <input
                        type="checkbox"
                        checked={!hidden.has(col.key)}
                        onChange={() => toggleColumn(col.key)}
                      />
                      {col.header}
                    </label>
                  ))}
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {pageRows.length === 0 ? (
        <EmptyState title={emptyTitle} description={emptyMessage} />
      ) : (
        <div className="overflow-x-auto rounded-lg border border-gray-200 dark:border-gray-700">
          <table className="min-w-full divide-y divide-gray-200 text-sm dark:divide-gray-700">
            <thead className="bg-gray-50 dark:bg-gray-800">
              <tr>
                {visibleColumns.map((col) => {
                  const isSorted = sortKey === col.key;
                  const sortable = col.sortable && !!col.accessor;
                  return (
                    <th
                      key={col.key}
                      scope="col"
                      aria-sort={isSorted ? (sortDir === 'asc' ? 'ascending' : 'descending') : undefined}
                      className={`px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400 ${
                        ALIGN_CLASS[col.align ?? 'left']
                      }`}
                    >
                      {sortable ? (
                        <button
                          type="button"
                          onClick={() => toggleSort(col)}
                          className="inline-flex items-center gap-1 hover:text-gray-700 dark:hover:text-gray-200"
                        >
                          {col.header}
                          <span aria-hidden="true" className="text-[10px]">
                            {isSorted ? (sortDir === 'asc' ? '▲' : '▼') : '↕'}
                          </span>
                        </button>
                      ) : (
                        col.header
                      )}
                    </th>
                  );
                })}
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100 bg-white dark:divide-gray-800 dark:bg-gray-900">
              {pageRows.map((row, i) => (
                <tr
                  key={getRowId ? getRowId(row, start + i) : start + i}
                  onClick={onRowClick ? () => onRowClick(row) : undefined}
                  className={
                    onRowClick
                      ? 'cursor-pointer hover:bg-gray-50 dark:hover:bg-gray-800'
                      : undefined
                  }
                >
                  {visibleColumns.map((col) => (
                    <td
                      key={col.key}
                      className={`px-4 py-2.5 text-gray-700 dark:text-gray-300 ${
                        ALIGN_CLASS[col.align ?? 'left']
                      }`}
                    >
                      {col.render ? col.render(row) : String(col.accessor?.(row) ?? '')}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {sorted.length > pageSize && (
        <div className="mt-3 flex items-center justify-between text-sm text-gray-600 dark:text-gray-400">
          <span>
            {start + 1}–{Math.min(start + pageSize, sorted.length)} of {sorted.length}
          </span>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={currentPage <= 1}
              className="rounded-md border border-gray-300 px-3 py-1 disabled:opacity-40 dark:border-gray-600"
            >
              Prev
            </button>
            <span>
              Page {currentPage} of {totalPages}
            </span>
            <button
              type="button"
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={currentPage >= totalPages}
              className="rounded-md border border-gray-300 px-3 py-1 disabled:opacity-40 dark:border-gray-600"
            >
              Next
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
