import { useCallback, useEffect, useMemo, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { WorkflowViewer, type WorkflowDataset, type WorkflowMetadata } from '@tamma/workflow-viewer';

/**
 * Wiki adapter around the reusable @tamma/workflow-viewer package.
 *
 * Responsibilities (host concerns the package deliberately does NOT own):
 *  - fetch the generated metadata dataset (`/workflows.json`)
 *  - resolve the wiki URL slug -> a workflow in the dataset
 *  - sync the selected step into the URL (`?step=…`) for shareable deep-links
 *  - cross-navigate to a sub-workflow's wiki page when a dispatch node links out
 *
 * The package itself is pure presentational: metadata in -> diagram out.
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

/** Map a dataset workflow back to its wiki page slug (for cross-navigation). */
function wikiSlugFor(w: WorkflowMetadata): string {
  if (w.wikiPage) return w.wikiPage.replace(/^Workflow-/, '').toLowerCase();
  return w.inventoryId ?? w.id;
}

/** Resolve a wiki URL slug to a workflow in the dataset. */
function resolveWorkflow(
  dataset: WorkflowDataset,
  slug: string,
): WorkflowMetadata | undefined {
  const wfs = dataset.workflows;
  return (
    wfs.find((w) => w.id === slug) ??
    wfs.find((w) => w.inventoryId === slug) ??
    wfs.find((w) => w.wikiPage && w.wikiPage.replace(/^Workflow-/, '').toLowerCase() === slug)
  );
}

interface Props {
  /** The wiki URL slug for the current workflow page. */
  slug: string;
  /** Diagram height. */
  height?: number;
}

export default function WorkflowDiagram({ slug, height = 600 }: Props) {
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
    () => (dataset ? resolveWorkflow(dataset, slug) : undefined),
    [dataset, slug],
  );

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
      navigate(`/workflows/${targetSlug}${search}`);
    },
    [dataset, navigate],
  );

  if (failed || (dataset && !workflow)) {
    // No metadata for this slug — render nothing (the page shows other sections).
    return null;
  }

  if (!dataset || !workflow) {
    return (
      <div
        className="my-6 bg-zinc-900/40 border border-zinc-800 rounded-xl animate-pulse"
        style={{ height }}
      />
    );
  }

  return (
    <div className="my-6" style={{ height }}>
      <WorkflowViewer
        metadata={dataset}
        workflowId={workflow.id}
        stepId={stepId}
        onStepChange={handleStepChange}
        onNavigate={handleNavigate}
        fill
      />
    </div>
  );
}
