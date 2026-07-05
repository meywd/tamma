/**
 * Story 34-9 (AC9) — the tenant cost-estimate widget.
 *
 * Form (provider, model, input/output tokens) → `GET /api/pricing/estimate`
 * (34-5) which returns ONLY the sell price + pricing mode. There is NO cost /
 * margin in the response or in this UI — the platform economics are never shown
 * to a tenant (AC6/AC7). An unknown provider/model surfaces the server's
 * `PRICING.UNKNOWN_MODEL` error inline (never a silent $0).
 */

import { useState, type JSX } from 'react';
import { tenantPricingApi, type EstimateResponse } from '../../api/pricing';
import { ApiError } from '../../api/client';

export function CostEstimateWidget(): JSX.Element {
  const [provider, setProvider] = useState('anthropic');
  const [model, setModel] = useState('claude-3-5-sonnet');
  const [inputTokens, setInputTokens] = useState('1000');
  const [outputTokens, setOutputTokens] = useState('1000');
  const [result, setResult] = useState<EstimateResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const run = async (): Promise<void> => {
    setError(null);
    setResult(null);
    setLoading(true);
    try {
      const estimate = await tenantPricingApi.estimate({
        provider: provider.trim(),
        model: model.trim(),
        inputTokens: Number(inputTokens) || 0,
        outputTokens: Number(outputTokens) || 0,
      });
      setResult(estimate);
    } catch (err) {
      if (err instanceof ApiError) {
        const body = err.body as { error?: string; message?: string } | null;
        setError(body?.message ?? body?.error ?? `Estimate failed (${err.status}).`);
      } else {
        setError(err instanceof Error ? err.message : 'Estimate failed.');
      }
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="bg-white border border-gray-200 rounded-md p-4 space-y-3">
      <h3 className="text-sm font-semibold text-gray-900">Cost estimate</h3>
      <p className="text-xs text-gray-500">
        Estimate the price of a usage line under your current plan. Shows the sell price you would
        be charged.
      </p>

      <div className="grid grid-cols-2 md:grid-cols-4 gap-2 items-end">
        <label className="flex flex-col text-xs text-gray-600">
          Provider
          <input
            aria-label="Provider"
            value={provider}
            onChange={(e) => setProvider(e.target.value)}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm"
          />
        </label>
        <label className="flex flex-col text-xs text-gray-600">
          Model
          <input
            aria-label="Model"
            value={model}
            onChange={(e) => setModel(e.target.value)}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm"
          />
        </label>
        <label className="flex flex-col text-xs text-gray-600">
          Input tokens
          <input
            aria-label="Input tokens"
            value={inputTokens}
            inputMode="numeric"
            onChange={(e) => setInputTokens(e.target.value)}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm"
          />
        </label>
        <label className="flex flex-col text-xs text-gray-600">
          Output tokens
          <input
            aria-label="Output tokens"
            value={outputTokens}
            inputMode="numeric"
            onChange={(e) => setOutputTokens(e.target.value)}
            className="mt-1 px-2 py-1 border border-gray-300 rounded text-sm"
          />
        </label>
      </div>

      <button
        type="button"
        disabled={loading}
        onClick={() => void run()}
        className="px-3 py-1.5 text-sm bg-gray-900 text-white rounded hover:bg-gray-800 disabled:opacity-50"
      >
        {loading ? 'Estimating…' : 'Estimate'}
      </button>

      {error !== null && (
        <div role="alert" className="p-2 text-sm text-red-700 bg-red-50 rounded">
          {error}
        </div>
      )}

      {result !== null && (
        <div className="border-t border-gray-100 pt-3 text-sm space-y-1">
          <div className="flex justify-between">
            <span className="text-gray-500">Pricing mode</span>
            <span className="font-medium text-gray-900">{result.pricingMode}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-gray-500">Sell price</span>
            <span className="font-semibold text-gray-900">${result.sellPriceUsd.toFixed(6)}</span>
          </div>
          <div className="flex justify-between">
            <span className="text-gray-500">Invoice amount</span>
            <span className="font-medium text-gray-900">
              ${result.invoice.sellPriceUsd.toFixed(2)}
            </span>
          </div>
        </div>
      )}
    </div>
  );
}
