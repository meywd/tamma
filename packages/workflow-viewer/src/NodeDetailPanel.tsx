import { kindOf } from './kinds';
import type { WorkflowNode } from './types';

export interface NodeDetailPanelProps {
  node: WorkflowNode;
  /** Called when the user closes the panel. */
  onClose: () => void;
  /**
   * Called when the user follows a sub-workflow link. Receives the target
   * workflow id. The host decides how to navigate (cross-navigation).
   */
  onOpenSubWorkflow?: (workflowId: string) => void;
}

/**
 * Side panel rendering EVERY piece of a node's metadata: typed inputs/outputs
 * with descriptions, branch outcomes, interactions, the sub-workflow link, and
 * (for api-call nodes) the endpoint detail.
 */
export function NodeDetailPanel({ node, onClose, onOpenSubWorkflow }: NodeDetailPanelProps) {
  const k = kindOf(node.kind);

  return (
    <aside className="twv-panel" aria-label={`Details for ${node.name}`}>
      <div className="twv-panel-header">
        <div className="twv-panel-title">
          <span
            className="twv-panel-kind-dot"
            style={{ background: k.color }}
            aria-hidden="true"
          />
          <div>
            <div className="twv-panel-name">{node.name}</div>
            <code className="twv-panel-class">{node.className}</code>
          </div>
        </div>
        <button type="button" className="twv-panel-close" onClick={onClose} aria-label="Close details">
          ×
        </button>
      </div>

      <div className="twv-panel-body">
        <div className="twv-panel-badges">
          <span className="twv-tag" data-kind={node.kind}>{k.label}</span>
          {node.isStart && <span className="twv-tag twv-tag-start">Start</span>}
        </div>

        {node.description && <p className="twv-panel-desc">{node.description}</p>}

        {/* Sub-workflow cross-navigation */}
        {node.subWorkflowId && (
          <Section title="Sub-workflow">
            <div className="twv-subwf">
              <code>{node.subWorkflowId}</code>
              {node.subWorkflowResolves && onOpenSubWorkflow ? (
                <button
                  type="button"
                  className="twv-subwf-link"
                  onClick={() => onOpenSubWorkflow(node.subWorkflowId!)}
                >
                  Open workflow →
                </button>
              ) : (
                <span className="twv-subwf-unresolved">(not in dataset)</span>
              )}
            </div>
          </Section>
        )}

        {/* API endpoint detail */}
        {node.api && (
          <Section title="API endpoint">
            <dl className="twv-kv">
              <Kv label="Service" value={node.api.service} />
              <Kv label="Method" value={node.api.method} mono />
              <Kv label="Route" value={node.api.route} mono />
              {node.api.purpose && <Kv label="Purpose" value={node.api.purpose} />}
            </dl>
          </Section>
        )}

        {/* Inputs */}
        {node.inputs.length > 0 && (
          <Section title={`Inputs (${node.inputs.length})`}>
            <ul className="twv-ports">
              {node.inputs.map((p) => (
                <li key={p.name} className="twv-port">
                  <div className="twv-port-head">
                    <span className="twv-port-name">{p.name}</span>
                    <code className="twv-port-type">{p.type}</code>
                  </div>
                  {p.description && <div className="twv-port-desc">{p.description}</div>}
                </li>
              ))}
            </ul>
          </Section>
        )}

        {/* Outputs */}
        {node.outputs.length > 0 && (
          <Section title={`Outputs (${node.outputs.length})`}>
            <ul className="twv-ports">
              {node.outputs.map((p) => (
                <li key={p.name} className="twv-port">
                  <div className="twv-port-head">
                    <span className="twv-port-name">{p.name}</span>
                    <code className="twv-port-type">{p.type}</code>
                  </div>
                  {p.description && <div className="twv-port-desc">{p.description}</div>}
                </li>
              ))}
            </ul>
          </Section>
        )}

        {/* Outcomes */}
        {node.outcomes.length > 0 && (
          <Section title="Outcomes">
            <div className="twv-chips">
              {node.outcomes.map((o) => (
                <span key={o} className="twv-chip">{o}</span>
              ))}
            </div>
          </Section>
        )}

        {/* Interactions */}
        {node.interactions.length > 0 && (
          <Section title="Interactions">
            <ul className="twv-interactions">
              {node.interactions.map((it) => (
                <li key={it}>{it}</li>
              ))}
            </ul>
          </Section>
        )}

        {node.inputs.length === 0 &&
          node.outputs.length === 0 &&
          node.outcomes.length === 0 &&
          node.interactions.length === 0 &&
          !node.api &&
          !node.subWorkflowId && (
            <p className="twv-panel-empty">No additional metadata for this node.</p>
          )}
      </div>
    </aside>
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
