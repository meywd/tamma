
import { SecuritySettingsPanel } from '../../components/settings/security/SecuritySettingsPanel.js';

import type { JSX } from "react";

export function SecurityPage(): JSX.Element {
  return (
    <div>
      <h1 className="text-2xl font-bold text-gray-900 mb-6">Security Settings</h1>
      <SecuritySettingsPanel />
    </div>
  );
}
