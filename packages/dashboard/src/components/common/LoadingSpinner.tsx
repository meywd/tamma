

import type { JSX } from "react";

interface LoadingSpinnerProps {
  size?: 'sm' | 'md' | 'lg';
  className?: string;
}

const SIZE_CLASSES = {
  sm: 'h-4 w-4',
  md: 'h-8 w-8',
  lg: 'h-12 w-12',
};

export function LoadingSpinner({ size = 'md', className = '' }: LoadingSpinnerProps): JSX.Element {
  return (
    <div className={`flex items-center justify-center ${className}`}>
      <div
        className={`${SIZE_CLASSES[size]} animate-spin rounded-full border-2 border-gray-300 border-t-blue-600 dark:border-gray-600`}
      />
    </div>
  );
}
