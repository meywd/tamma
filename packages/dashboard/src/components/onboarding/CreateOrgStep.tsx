/**
 * Step 2 — Org creation.
 *
 * The dominant `/auth/register` flow auto-creates a personal tenant so
 * this step is rare in practice. It surfaces only for users whose
 * tenant memberships were pruned (e.g. removed from every org they
 * belonged to). For now we link out to the org-creation page rather
 * than embed the form; full embedded org creation lands in a follow-up.
 */

import { useNavigate } from 'react-router-dom';

export function CreateOrgStep(): JSX.Element {
  const navigate = useNavigate();
  return (
    <div className="space-y-4">
      <p className="text-sm text-slate-300">
        You don't belong to any organization yet. Create one or accept an
        existing invite to continue.
      </p>
      <div className="flex flex-col sm:flex-row gap-3">
        <button
          type="button"
          onClick={() => navigate('/settings/organization')}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-500 rounded-md text-center"
        >
          Manage organizations
        </button>
        <a
          href="mailto:?subject=Invite me to your Tamma org"
          className="px-4 py-2 text-sm font-medium text-slate-200 bg-slate-800 hover:bg-slate-700 border border-slate-700 rounded-md text-center"
        >
          Ask for an invite
        </a>
      </div>
    </div>
  );
}
