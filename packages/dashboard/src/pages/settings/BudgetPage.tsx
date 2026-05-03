
import { BudgetOverview } from '../../components/settings/budget/BudgetOverview.js';

import type { JSX } from "react";

export function BudgetPage(): JSX.Element {
  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6 dark:text-gray-100">Budget & Cost Tracking</h1>
      <BudgetOverview />
    </div>
  );
}
