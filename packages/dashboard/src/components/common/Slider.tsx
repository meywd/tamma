

import type { JSX } from "react";

interface SliderProps {
  label: string;
  value: number;
  min: number;
  max: number;
  step?: number;
  onChange: (value: number) => void;
  formatValue?: (value: number) => string;
  disabled?: boolean;
}

export function Slider({
  label,
  value,
  min,
  max,
  step = 1,
  onChange,
  formatValue,
  disabled = false,
}: SliderProps): JSX.Element {
  const display = formatValue ? formatValue(value) : String(value);

  return (
    <div className="py-3">
      <div className="flex items-center justify-between mb-2">
        <label className="text-sm font-medium text-gray-900">{label}</label>
        <span className="text-sm text-gray-500">{display}</span>
      </div>
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        onChange={(e) => onChange(Number(e.target.value))}
        disabled={disabled}
        className="w-full h-2 bg-gray-200 rounded-lg appearance-none cursor-pointer accent-blue-600 disabled:opacity-50"
      />
    </div>
  );
}
