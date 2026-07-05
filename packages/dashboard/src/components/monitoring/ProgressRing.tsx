/**
 * ProgressRing — circular percentage indicator (e.g. disk usage, cache hit
 * rate). Story 23-12 (AC4). Pure SVG, no external charting dependency.
 */

import type { JSX } from 'react';
import type { StatusTone } from './StatusBadge.js';

interface ProgressRingProps {
  /** 0-100. Values outside the range are clamped. */
  value: number;
  size?: number;
  strokeWidth?: number;
  label?: string;
  showValue?: boolean;
  /** Ring color. When omitted, colored automatically by threshold. */
  tone?: StatusTone;
  className?: string;
}

const TONE_HEX: Record<StatusTone, string> = {
  green: '#22c55e',
  yellow: '#f59e0b',
  red: '#ef4444',
  gray: '#9ca3af',
  blue: '#3b82f6',
};

function autoTone(value: number): StatusTone {
  if (value >= 90) return 'red';
  if (value >= 70) return 'yellow';
  return 'green';
}

export function ProgressRing({
  value,
  size = 96,
  strokeWidth = 8,
  label,
  showValue = true,
  tone,
  className = '',
}: ProgressRingProps): JSX.Element {
  const clamped = Math.max(0, Math.min(100, value));
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference * (1 - clamped / 100);
  const color = TONE_HEX[tone ?? autoTone(clamped)];

  return (
    <div
      data-testid="progress-ring"
      className={`inline-flex flex-col items-center ${className}`}
      role="img"
      aria-label={`${label ? `${label}: ` : ''}${Math.round(clamped)}%`}
    >
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          strokeWidth={strokeWidth}
          className="stroke-gray-200 dark:stroke-gray-700"
        />
        <circle
          cx={size / 2}
          cy={size / 2}
          r={radius}
          fill="none"
          stroke={color}
          strokeWidth={strokeWidth}
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
          transform={`rotate(-90 ${size / 2} ${size / 2})`}
        />
        {showValue && (
          <text
            x="50%"
            y="50%"
            dominantBaseline="central"
            textAnchor="middle"
            className="fill-gray-900 text-lg font-bold dark:fill-gray-100"
            style={{ fontSize: size * 0.22 }}
          >
            {Math.round(clamped)}%
          </text>
        )}
      </svg>
      {label && <span className="mt-1 text-xs text-gray-500 dark:text-gray-400">{label}</span>}
    </div>
  );
}
