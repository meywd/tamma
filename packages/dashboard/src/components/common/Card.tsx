import type { ReactNode, JSX } from 'react';

interface CardProps {
  title?: string;
  children: ReactNode;
  className?: string;
  actions?: ReactNode;
}

export function Card({ title, children, className = '', actions }: CardProps): JSX.Element {
  return (
    <div className={`bg-white rounded-lg border border-gray-200 shadow-sm ${className}`}>
      {(title || actions) && (
        <div className="flex items-center justify-between px-6 py-4 border-b border-gray-100">
          {title && <h3 className="text-lg font-semibold text-gray-900">{title}</h3>}
          {actions && <div className="flex items-center gap-2">{actions}</div>}
        </div>
      )}
      <div className="px-6 py-4">{children}</div>
    </div>
  );
}
