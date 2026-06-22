import { useEffect, useState } from 'react';
import { kindOf } from './kinds';
import type { WorkflowNode } from './types';

export interface StationDetailPopupProps {
  node: WorkflowNode;
  /** Called when the popup is dismissed (X, backdrop, or Esc). */
  onClose: () => void;
  /** Follow a sub-workflow link (plain left-click → in-app navigate). */
  onOpenSubWorkflow?: (workflowId: string) => void;
  /**
   * Real href for a sub-workflow's page, so the name pill is a true `<a>` link:
   * plain click navigates in-app, middle/ctrl/cmd-click opens a new tab natively.
   * Returns undefined when the target isn't in the dataset.
   */
  subWorkflowHref?: ((workflowId: string) => string | undefined) | undefined;
}

type Tab = 'overview' | 'api' | 'code';

/**
 * Floating detail popup for a station (NOT a docked side panel).
 *
 * Responsive by viewport width:
 *  - small screens  → FULL-SCREEN overlay (covers the whole viewport, own
 *                     internal scroll, fixed close button top-right).
 *  - large screens  → centred floating modal (max-width ~500px, max-height
 *                     ~75vh, backdrop-tap + Esc to dismiss).
 *
 * The presentation switch is driven by a `matchMedia` width check; the same
 * component renders both, toggling a `data-variant` attribute the CSS keys off.
 *
 * All content is React-escaped text — no `dangerouslySetInnerHTML` anywhere.
 */
export function StationDetailPopup({
  node,
  onClose,
  onOpenSubWorkflow,
  subWorkflowHref,
}: StationDetailPopupProps) {
  const k = kindOf(node.kind);
  const isApi = node.kind === 'api-call' || Boolean(node.api);
  const hasCode = Boolean(node.code);

  const [variant, setVariant] = useState<'fullscreen' | 'modal'>('modal');
  const [tab, setTab] = useState<Tab>('overview');

  // Pick presentation by viewport width; keep in sync on resize/orientation.
  useEffect(() => {
    if (typeof window === 'undefined' || !window.matchMedia) return;
    const mq = window.matchMedia('(max-width: 640px)');
    const apply = () => setVariant(mq.matches ? 'fullscreen' : 'modal');
    apply();
    mq.addEventListener('change', apply);
    return () => mq.removeEventListener('change', apply);
  }, []);

  // Esc to close; lock body scroll while open.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.removeEventListener('keydown', onKey);
      document.body.style.overflow = prev;
    };
  }, [onClose]);

  // Reset to overview whenever a different station is opened.
  useEffect(() => {
    setTab('overview');
  }, [node.id]);

  return (
    <div
      className="twv-popup-backdrop"
      data-variant={variant}
      onClick={(e) => {
        // Backdrop tap closes (modal only — full-screen has no backdrop gap).
        if (e.target === e.currentTarget) onClose();
      }}
    >
      <div
        className="twv-popup"
        data-variant={variant}
        role="dialog"
        aria-modal="true"
        aria-label={`Details for ${node.name}`}
      >
        <div className="twv-popup-header">
          <div className="twv-popup-title">
            <span
              className="twv-panel-kind-dot"
              style={{ background: k.color }}
              aria-hidden="true"
            />
            <div className="twv-popup-title-text">
              <div className="twv-panel-name">{node.name}</div>
              <code className="twv-panel-class">{node.className}</code>
            </div>
          </div>
          <button
            type="button"
            className="twv-popup-close"
            onClick={onClose}
            aria-label="Close details"
          >
            <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>

        {/* Tabs */}
        <div className="twv-popup-tabs" role="tablist" aria-label="Detail sections">
          <TabButton id="overview" active={tab} onSelect={setTab}>Overview</TabButton>
          <TabButton id="api" active={tab} onSelect={setTab} disabled={!isApi}>API</TabButton>
          <TabButton id="code" active={tab} onSelect={setTab}>Code</TabButton>
        </div>

        <div className="twv-popup-body">
          {tab === 'overview' && <OverviewTab node={node} onOpenSubWorkflow={onOpenSubWorkflow} subWorkflowHref={subWorkflowHref} k={k} />}
          {tab === 'api' && <ApiTab node={node} />}
          {tab === 'code' && <CodeTab node={node} hasCode={hasCode} />}
        </div>
      </div>
    </div>
  );
}

function TabButton({
  id,
  active,
  onSelect,
  disabled,
  children,
}: {
  id: Tab;
  active: Tab;
  onSelect: (t: Tab) => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active === id}
      className="twv-popup-tab"
      data-active={active === id ? 'true' : 'false'}
      disabled={disabled}
      onClick={() => onSelect(id)}
    >
      {children}
    </button>
  );
}

function OverviewTab({
  node,
  onOpenSubWorkflow,
  subWorkflowHref,
  k,
}: {
  node: WorkflowNode;
  onOpenSubWorkflow?: ((workflowId: string) => void) | undefined;
  subWorkflowHref?: ((workflowId: string) => string | undefined) | undefined;
  k: ReturnType<typeof kindOf>;
}) {
  const empty =
    node.inputs.length === 0 &&
    node.outputs.length === 0 &&
    node.outcomes.length === 0 &&
    node.interactions.length === 0 &&
    !node.subWorkflowId &&
    !node.description;

  return (
    <>
      <div className="twv-panel-badges">
        <span className="twv-tag" data-kind={node.kind}>{k.label}</span>
        {node.isStart && <span className="twv-tag twv-tag-start">Start</span>}
      </div>

      {node.description && <p className="twv-panel-desc">{node.description}</p>}

      {node.subWorkflowId && (
        <Section title="Sub-workflow">
          <div className="twv-subwf">
            <SubWorkflowPill
              workflowId={node.subWorkflowId}
              href={node.subWorkflowResolves ? subWorkflowHref?.(node.subWorkflowId) : undefined}
              onOpen={onOpenSubWorkflow}
            />
          </div>
        </Section>
      )}

      {node.inputs.length > 0 && (
        <Section title={`Inputs (${node.inputs.length})`}>
          <PortList ports={node.inputs} />
        </Section>
      )}

      {node.outputs.length > 0 && (
        <Section title={`Outputs (${node.outputs.length})`}>
          <PortList ports={node.outputs} />
        </Section>
      )}

      {node.outcomes.length > 0 && (
        <Section title="Outcomes / branches">
          <div className="twv-chips">
            {node.outcomes.map((o) => (
              <span key={o} className="twv-chip">{o}</span>
            ))}
          </div>
        </Section>
      )}

      {node.interactions.length > 0 && (
        <Section title="Interactions">
          <ul className="twv-interactions">
            {node.interactions.map((it) => (
              <li key={it}>{it}</li>
            ))}
          </ul>
        </Section>
      )}

      {empty && <p className="twv-panel-empty">No additional metadata for this station.</p>}
    </>
  );
}

function ApiTab({ node }: { node: WorkflowNode }) {
  if (!node.api) {
    return <p className="twv-panel-empty">This station does not call an API endpoint.</p>;
  }
  return (
    <Section title="API endpoint">
      <dl className="twv-kv">
        <Kv label="Service" value={node.api.service} />
        <Kv label="Method" value={node.api.method} mono />
        <Kv label="Route" value={node.api.route} mono />
        {node.api.purpose && <Kv label="Purpose" value={node.api.purpose} />}
      </dl>
    </Section>
  );
}

function CodeTab({ node, hasCode }: { node: WorkflowNode; hasCode: boolean }) {
  if (!hasCode) {
    // No resolvable source (sub-workflow dispatch / synthetic node): fall back
    // to the structured identity we do have.
    return (
      <>
        <Section title="Activity">
          <dl className="twv-kv">
            <Kv label="Class" value={node.className} mono />
            {node.subWorkflowId && <Kv label="Dispatches" value={node.subWorkflowId} mono />}
          </dl>
        </Section>
        <p className="twv-panel-empty">No source file resolvable for this station.</p>
      </>
    );
  }
  const code = node.code!;
  return (
    <>
      <Section title="Activity">
        <dl className="twv-kv">
          <Kv label="Class" value={node.className} mono />
          {code.namespace && <Kv label="Namespace" value={code.namespace} mono />}
        </dl>
      </Section>
      <Section title="Source">
        <dl className="twv-kv">
          <Kv label="File" value={code.line ? `${code.file}:${code.line}` : code.file} mono />
        </dl>
        {code.githubUrl && (
          <a
            className="twv-code-gh"
            href={code.githubUrl}
            target="_blank"
            rel="noopener noreferrer"
          >
            Open on GitHub
            <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" strokeWidth={2} aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="M14 5h5v5M19 5l-9 9M5 7v12h12" />
            </svg>
          </a>
        )}
      </Section>
      {code.snippet && (
        <Section title="Snippet">
          <pre className="twv-code-snippet"><code>{code.snippet}</code></pre>
        </Section>
      )}
    </>
  );
}

/**
 * The sub-workflow NAME pill — a real link when the target resolves.
 * Plain left-click navigates in-app (via `onOpen`); middle-click and
 * ctrl/cmd/shift-click open a new tab natively (the `<a href>` default).
 * Unresolvable targets render as a plain, non-clickable pill.
 */
function SubWorkflowPill({
  workflowId,
  href,
  onOpen,
}: {
  workflowId: string;
  href?: string | undefined;
  onOpen?: ((workflowId: string) => void) | undefined;
}) {
  if (href) {
    return (
      <a
        className="twv-subwf-pill"
        href={href}
        title={`Open ${workflowId}`}
        onClick={(e) => {
          // Let the browser handle modified / non-left clicks → new tab/window.
          if (e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || e.button !== 0) return;
          e.preventDefault();
          onOpen?.(workflowId);
        }}
      >
        <code>{workflowId}</code>
        <span className="twv-subwf-arrow" aria-hidden="true">→</span>
      </a>
    );
  }
  return (
    <span className="twv-subwf-pill twv-subwf-pill-disabled" title="Not in this dataset">
      <code>{workflowId}</code>
      <span className="twv-subwf-unresolved">(not in dataset)</span>
    </span>
  );
}

function PortList({ ports }: { ports: WorkflowNode['inputs'] }) {
  return (
    <ul className="twv-ports">
      {ports.map((p) => (
        <li key={p.name} className="twv-port">
          <div className="twv-port-head">
            <span className="twv-port-name">{p.name}</span>
            <code className="twv-port-type">{p.type}</code>
          </div>
          {p.description && <div className="twv-port-desc">{p.description}</div>}
        </li>
      ))}
    </ul>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="twv-section">
      <h4 className="twv-section-title">{title}</h4>
      {children}
    </section>
  );
}

function Kv({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <>
      <dt className="twv-kv-key">{label}</dt>
      <dd className={mono ? 'twv-kv-val twv-mono' : 'twv-kv-val'}>{value}</dd>
    </>
  );
}
