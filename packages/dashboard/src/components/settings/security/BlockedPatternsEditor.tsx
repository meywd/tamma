import { useState, type JSX } from 'react';

interface BlockedPatternsEditorProps {
  patterns: string[];
  onChange: (patterns: string[]) => void;
}

function isValidRegex(pattern: string): boolean {
  try {
    new RegExp(pattern);
    return true;
  } catch {
    return false;
  }
}

export function BlockedPatternsEditor({ patterns, onChange }: BlockedPatternsEditorProps): JSX.Element {
  const [newPattern, setNewPattern] = useState('');
  const [patternError, setPatternError] = useState<string | null>(null);

  const handleAdd = () => {
    const trimmed = newPattern.trim();
    if (!trimmed) return;

    if (!isValidRegex(trimmed)) {
      setPatternError('Invalid regex pattern');
      return;
    }

    if (patterns.includes(trimmed)) {
      setPatternError('Pattern already exists');
      return;
    }

    onChange([...patterns, trimmed]);
    setNewPattern('');
    setPatternError(null);
  };

  const handleRemove = (index: number) => {
    onChange(patterns.filter((_, i) => i !== index));
  };

  return (
    <div>
      <div className="space-y-2 mb-4">
        {patterns.length === 0 && (
          <p className="text-sm text-gray-500 italic dark:text-gray-400">No blocked patterns configured</p>
        )}
        {patterns.map((pattern, index) => (
          <div
            key={index}
            className="flex items-center justify-between px-3 py-2 bg-gray-50 rounded-md border border-gray-200 dark:bg-gray-900 dark:border-gray-700"
          >
            <code className="text-sm text-gray-800 font-mono dark:text-gray-200">{pattern}</code>
            <button
              onClick={() => handleRemove(index)}
              className="text-red-500 hover:text-red-700 text-sm font-medium ml-4"
            >
              Remove
            </button>
          </div>
        ))}
      </div>

      <div className="flex gap-2">
        <input
          type="text"
          value={newPattern}
          onChange={(e) => {
            setNewPattern(e.target.value);
            setPatternError(null);
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter') handleAdd();
          }}
          placeholder="Enter regex pattern..."
          className="flex-1 px-3 py-2 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 font-mono dark:border-gray-600"
        />
        <button
          onClick={handleAdd}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md"
        >
          Add
        </button>
      </div>
      {patternError && <p className="mt-1 text-sm text-red-600 dark:text-red-400">{patternError}</p>}
    </div>
  );
}
