
import { ProviderHealthDashboard } from '../../components/settings/health/ProviderHealthDashboard.js';

import type { JSX } from "react";

export function ProviderHealthPage(): JSX.Element {
  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6 dark:text-gray-100">Provider Health</h1>
      <ProviderHealthDashboard />
    </div>
  );
}
