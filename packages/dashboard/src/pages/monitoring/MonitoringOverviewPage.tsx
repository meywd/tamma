/**
 * Monitoring overview / landing page. Story 23-12.
 *
 * Simple index that links to every monitoring section. Individual sections are
 * filled in by the other Epic-23 stories.
 */

import type { JSX } from 'react';
import { Link } from 'react-router-dom';
import { MonitoringLayout } from '../../components/monitoring/MonitoringLayout.js';
import { MONITORING_NAV_ITEMS } from './monitoring-nav.js';

export function MonitoringOverviewPage(): JSX.Element {
  return (
    <MonitoringLayout
      title="Monitoring"
      description="Operator observability across services, agents, workflows and infrastructure."
      showTimeRange={false}
    >
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        {MONITORING_NAV_ITEMS.map((item) => (
          <Link
            key={item.to}
            to={item.to}
            className="group rounded-lg border border-gray-200 bg-white p-4 shadow-sm transition-shadow hover:shadow-md dark:border-gray-700 dark:bg-gray-800"
          >
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-semibold text-gray-900 group-hover:text-blue-600 dark:text-gray-100">
                {item.label}
              </h3>
              <span className="text-xs text-gray-400 dark:text-gray-500">{item.story}</span>
            </div>
            <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">{item.description}</p>
          </Link>
        ))}
      </div>
    </MonitoringLayout>
  );
}
