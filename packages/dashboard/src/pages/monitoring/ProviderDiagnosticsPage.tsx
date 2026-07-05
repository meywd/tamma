/**
 * Provider Diagnostics page. Scaffold from Story 23-12; full dashboard arrives
 * in Story 23-6.
 */

import type { JSX } from 'react';
import { MonitoringPlaceholder } from './MonitoringPlaceholder.js';
import { getMonitoringNavItem } from './monitoring-nav.js';

const NAV = getMonitoringNavItem('/monitoring/providers');

export function ProviderDiagnosticsPage(): JSX.Element {
  return (
    <MonitoringPlaceholder
      title={NAV.label}
      description={NAV.description}
      storyRef={NAV.story}
      storageKey={NAV.storageKey}
    />
  );
}
