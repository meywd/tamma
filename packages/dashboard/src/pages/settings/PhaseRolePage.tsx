
import { PhaseRoleMatrix } from '../../components/settings/phases/PhaseRoleMatrix.js';

import type { JSX } from "react";

export function PhaseRolePage(): JSX.Element {
  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6 dark:text-gray-100">Phase-Role Mapping</h1>
      <PhaseRoleMatrix />
    </div>
  );
}
