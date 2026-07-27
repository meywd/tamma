/**
 * Cost Breakdown
 *
 * Displays cost analytics with category breakdown.
 */

import { type JSX } from 'react';
import type { CostAnalytics } from '@tamma/shared';

export interface CostBreakdownProps {
  analytics: CostAnalytics;
}

export function CostBreakdown({ analytics }: CostBreakdownProps): JSX.Element {
  return (
    <div data-testid="cost-breakdown" style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
      <h3 style={{ margin: '0 0 16px 0', fontSize: '16px', fontWeight: 600 }}>Cost Analysis</h3>

      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: '16px', marginBottom: '20px' }}>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Total Cost</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>${analytics.totalCostUsd.toFixed(2)}</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Embedding Cost</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>${analytics.embeddingCostUsd.toFixed(2)}</div>
        </div>
        <div style={{ padding: '12px', backgroundColor: '#f9fafb', borderRadius: '6px' }}>
          <div style={{ fontSize: '12px', color: '#6b7280' }}>Indexing Cost</div>
          <div style={{ fontSize: '24px', fontWeight: 700 }}>${analytics.indexingCostUsd.toFixed(2)}</div>
        </div>
      </div>

      <h4 style={{ margin: '0 0 12px 0', fontSize: '14px', fontWeight: 600, color: '#374151' }}>Breakdown</h4>
      <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '14px' }}>
        <thead>
          <tr style={{ borderBottom: '2px solid #e5e7eb' }}>
            <th style={{ textAlign: 'left', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Category</th>
            <th style={{ textAlign: 'right', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Units</th>
            <th style={{ textAlign: 'right', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Unit Cost</th>
            <th style={{ textAlign: 'right', padding: '8px', color: '#6b7280', fontWeight: 500 }}>Total</th>
          </tr>
        </thead>
        <tbody>
          {analytics.breakdown.map((item) => (
            <tr key={item.category} data-testid="cost-row" style={{ borderBottom: '1px solid #f3f4f6' }}>
              <td style={{ padding: '8px' }}>{item.category}</td>
              <td style={{ padding: '8px', textAlign: 'right' }}>{item.units.toLocaleString()}</td>
              <td style={{ padding: '8px', textAlign: 'right' }}>${item.unitCostUsd.toFixed(5)}</td>
              <td style={{ padding: '8px', textAlign: 'right', fontWeight: 600 }}>${item.costUsd.toFixed(2)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
