
import { AgentsOverview } from '../../components/settings/agents/AgentsOverview.js';

import type { JSX } from "react";

export function AgentsPage(): JSX.Element {
  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6">Agent Configuration</h1>
      <AgentsOverview />
    </div>
  );
}
