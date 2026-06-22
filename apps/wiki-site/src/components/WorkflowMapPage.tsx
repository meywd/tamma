import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { WorkflowMap, type WorkflowDataset, type WorkflowMetadata } from '@tamma/workflow-viewer';

/**
 * Immersive, full-bleed route for the subway/transit MAP view of a workflow.
 *
 * Rendered OUTSIDE the wiki <Layout> chrome (no sidebar) — the page is only a
 * minimal fixed top bar + the diagram + the floating popup. Mirrors the React
 * Flow adapter's host responsibilities:
 *  - fetch the generated metadata dataset (`/workflows.json`)
 *  - resolve the wiki URL slug → a workflow in the dataset
 *  - sync the open station into the URL (`?step=…`) for shareable deep-links
 *  - cross-navigate to a sub-workflow's MAP when a dispatch station links out
 */

let datasetCache: Promise<WorkflowDataset> | null = null;
function loadDataset(): Promise<WorkflowDataset> {
  if (!datasetCache) {
    datasetCache = fetch('/workflows.json').then((r) => {
      if (!r.ok) throw new Error('workflows.json not found');
      return r.json() as Promise<WorkflowDataset>;
    });
  }
  return datasetCache;
}

function wikiSlugFor(w: WorkflowMetadata): string {
  if (w.wikiPage) return w.wikiPage.replace(/^Workflow-/, '').toLowerCase();
  return w.inventoryId ?? w.id;
}

function resolveWorkflow(
  dataset: WorkflowDataset,
  slug: string,
): WorkflowMetadata | undefined {
  const wfs = dataset.workflows;
  return (
    wfs.find((w) => w.id === slug) ??
    wfs.find((w) => w.inventoryId === slug) ??
    wfs.find(
      (w) =>
        w.wikiPage &&
        w.wikiPage.replace(/^Workflow-/, '').toLowerCase() === slug,
    )
  );
}

export default function WorkflowMapPage() {
  const { slug } = useParams();
  const [dataset, setDataset] = useState<WorkflowDataset | null>(null);
  const [failed, setFailed] = useState(false);
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  useEffect(() => {
    let active = true;
    loadDataset()
      .then((d) => active && setDataset(d))
      .catch(() => active && setFailed(true));
    return () => {
      active = false;
    };
  }, []);

  const workflow = useMemo(
    () => (dataset && slug ? resolveWorkflow(dataset, slug) : undefined),
    [dataset, slug],
  );

  const workflowName = workflow ? workflow.name.replace(/^Workflow:\s*/, '') : slug;

  useEffect(() => {
    if (workflowName) document.title = `${workflowName} — Map — Tamma Docs`;
  }, [workflowName]);

  const stepId = searchParams.get('step') ?? undefined;

  const handleStepChange = useCallback(
    (next: string | null) => {
      setSearchParams(
        (prev) => {
          const params = new URLSearchParams(prev);
          if (next) params.set('step', next);
          else params.delete('step');
          return params;
        },
        { replace: true },
      );
    },
    [setSearchParams],
  );

  const handleNavigate = useCallback(
    (targetWorkflowId: string, targetStep?: string) => {
      if (!dataset) return;
      const target = dataset.workflows.find(
        (w) => w.id === targetWorkflowId || w.inventoryId === targetWorkflowId,
      );
      if (!target) return;
      const targetSlug = wikiSlugFor(target);
      const search = targetStep ? `?step=${encodeURIComponent(targetStep)}` : '';
      navigate(`/workflows/${targetSlug}/map${search}`);
    },
    [dataset, navigate],
  );

  // Real href for a sub-workflow's map page, so the popup's name pill is a true
  // <a> link (middle/ctrl-click → new tab). Same resolution as handleNavigate.
  const subWorkflowHref = useCallback(
    (targetWorkflowId: string): string | undefined => {
      if (!dataset) return undefined;
      const target = dataset.workflows.find(
        (w) => w.id === targetWorkflowId || w.inventoryId === targetWorkflowId,
      );
      if (!target) return undefined;
      return `/workflows/${wikiSlugFor(target)}/map`;
    },
    [dataset],
  );

  return (
    <div className="fixed inset-0 flex flex-col bg-[#0c0c0e] text-zinc-200">
      {/* Minimal fixed top bar */}
      <header className="flex-none flex items-center gap-3 h-12 px-3 sm:px-4 border-b border-zinc-800 bg-[#09090b]">
        <Link
          to={slug ? `/workflows/${slug}` : '/workflows'}
          aria-label="Back to workflow detail"
          className="inline-flex items-center justify-center w-9 h-9 -ml-1 rounded-md text-zinc-400 hover:text-white hover:bg-white/5 transition-colors"
        >
          <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2} aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
          </svg>
        </Link>
        <div className="min-w-0 flex-1">
          <div className="text-[11px] uppercase tracking-wider text-zinc-600 leading-none">
            Map view
          </div>
          <h1 className="text-[14px] font-semibold text-white truncate leading-tight">
            {workflowName}
          </h1>
        </div>
        {slug && (
          <Link
            to={`/workflows/${slug}`}
            className="flex-none inline-flex items-center gap-1.5 text-[12px] text-blue-400 hover:text-blue-300 border border-blue-500/30 hover:border-blue-500/50 rounded-md px-2.5 py-1.5 transition-colors"
          >
            <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.8} aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 17v-6h13M9 11V5l-6 6 6 6" />
            </svg>
            <span className="hidden xs:inline sm:inline">Flow view</span>
          </Link>
        )}
      </header>

      {/* Diagram surface */}
      <div className="flex-1 min-h-0">
        {failed || (dataset && !workflow) ? (
          <div className="h-full flex flex-col items-center justify-center gap-4 px-6 text-center">
            <div className="text-zinc-500">No diagram metadata for this workflow.</div>
            <Link to="/workflows" className="text-sm text-blue-400 hover:text-blue-300">
              Back to Workflows
            </Link>
          </div>
        ) : !dataset || !workflow ? (
          <div className="h-full bg-zinc-900/40 animate-pulse" />
        ) : (
          <WorkflowMap
            metadata={dataset}
            workflowId={workflow.id}
            stepId={stepId}
            onStepChange={handleStepChange}
            onNavigate={handleNavigate}
            subWorkflowHref={subWorkflowHref}
          />
        )}
      </div>
    </div>
  );
}
