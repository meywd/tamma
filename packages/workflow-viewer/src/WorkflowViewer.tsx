import { useCallback, useEffect, useMemo } from 'react';
import {
  ReactFlow,
  ReactFlowProvider,
  Background,
  BackgroundVariant,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
  type NodeTypes,
  type NodeMouseHandler,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './styles.css';

import type { WorkflowDataset, WorkflowMetadata, WorkflowNode } from './types';
import { buildGraph } from './layout';
import { WorkflowNodeView, type WorkflowFlowNode } from './nodes';
import { NodeDetailPanel } from './NodeDetailPanel';
import { kindOf, KIND_ORDER, KIND_DESCRIPTORS } from './kinds';

const nodeTypes: NodeTypes = { workflowNode: WorkflowNodeView };

export interface WorkflowViewerProps {
  /** The full dataset (all workflows), enabling sub-workflow cross-navigation. */
  metadata: WorkflowDataset | WorkflowMetadata[];
  /** Which workflow to render (by id). Falls back to the first workflow. */
  workflowId?: string;
  /** Currently selected step/node id (controlled, for deep-links). */
  stepId?: string;
  /**
   * Called when the selected step changes (node clicked or panel closed),
   * so the host can sync the URL (`?step=…`). `null` means deselected.
   */
  onStepChange?: (stepId: string | null) => void;
  /**
   * Called when the user navigates to another workflow (sub-workflow link or
   * legend). The host updates `workflowId` (and URL `?workflow=…`).
   */
  onNavigate?: (workflowId: string, stepId?: string) => void;
  /** Diagram height (CSS value). Defaults to 600px. */
  height?: string | number;
  /** Render full-bleed (fills its container) instead of a fixed height. */
  fill?: boolean;
}

function normalizeDataset(
  metadata: WorkflowDataset | WorkflowMetadata[],
): WorkflowMetadata[] {
  if (Array.isArray(metadata)) return metadata;
  return metadata.workflows ?? [];
}

function InnerViewer({
  metadata,
  workflowId,
  stepId,
  onStepChange,
  onNavigate,
  height = 600,
  fill = false,
}: WorkflowViewerProps) {
  const workflows = useMemo(() => normalizeDataset(metadata), [metadata]);

  const workflow = useMemo<WorkflowMetadata | undefined>(() => {
    if (workflowId) {
      const byId = workflows.find(
        (w) => w.id === workflowId || w.inventoryId === workflowId,
      );
      if (byId) return byId;
    }
    return workflows[0];
  }, [workflows, workflowId]);

  const graph = useMemo(
    () => (workflow ? buildGraph(workflow) : { nodes: [], edges: [] }),
    [workflow],
  );

  const [nodes, setNodes, onNodesChange] = useNodesState<WorkflowFlowNode>(
    graph.nodes as WorkflowFlowNode[],
  );
  const [edges, setEdges, onEdgesChange] = useEdgesState(graph.edges);

  // Re-seed graph when the workflow changes.
  useEffect(() => {
    setNodes(graph.nodes as WorkflowFlowNode[]);
    setEdges(graph.edges);
  }, [graph, setNodes, setEdges]);

  // Selected node is derived from the controlled `stepId`.
  const selectedNode = useMemo<WorkflowNode | undefined>(() => {
    if (!stepId || !workflow) return undefined;
    return workflow.nodes.find((n) => n.id === stepId);
  }, [stepId, workflow]);

  // Reflect selection into React Flow node `selected` flags.
  useEffect(() => {
    setNodes((nds) =>
      nds.map((n) => ({ ...n, selected: n.id === stepId })),
    );
  }, [stepId, setNodes]);

  const handleNodeClick = useCallback<NodeMouseHandler<WorkflowFlowNode>>(
    (_evt, node) => {
      onStepChange?.(node.id === stepId ? null : node.id);
    },
    [onStepChange, stepId],
  );

  const handleClosePanel = useCallback(() => onStepChange?.(null), [onStepChange]);

  const handleOpenSub = useCallback(
    (targetId: string) => {
      onNavigate?.(targetId);
    },
    [onNavigate],
  );

  const containerStyle: React.CSSProperties = fill
    ? { position: 'relative', width: '100%', height: '100%' }
    : { position: 'relative', width: '100%', height: typeof height === 'number' ? `${height}px` : height };

  if (!workflow) {
    return <div className="twv-empty">No workflow metadata available.</div>;
  }

  const kindsPresent = new Set(workflow.nodes.map((n) => n.kind));

  return (
    <div className="twv-root" data-has-panel={selectedNode ? 'true' : 'false'}>
      <div className="twv-canvas-wrap" style={containerStyle}>
        <ReactFlow<WorkflowFlowNode>
          nodes={nodes}
          edges={edges}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onNodeClick={handleNodeClick}
          nodeTypes={nodeTypes}
          fitView
          fitViewOptions={{ padding: 0.2 }}
          minZoom={0.1}
          maxZoom={2.5}
          nodesConnectable={false}
          proOptions={{ hideAttribution: true }}
        >
          <Background color="#27272a" gap={20} variant={BackgroundVariant.Dots} />
          <Controls className="twv-controls" showInteractive={false} />
          <MiniMap
            className="twv-minimap"
            pannable
            zoomable
            nodeColor={(n) => kindOf((n.data as WorkflowNodeMiniData)?.node?.kind).color}
            maskColor="rgba(0,0,0,0.7)"
          />
        </ReactFlow>

        {/* Legend */}
        <div className="twv-legend" aria-label="Node kinds">
          {KIND_ORDER.filter((k) => kindsPresent.has(k)).map((k) => (
            <span key={k} className="twv-legend-item">
              <span className="twv-legend-dot" style={{ background: KIND_DESCRIPTORS[k].color }} />
              {KIND_DESCRIPTORS[k].label}
            </span>
          ))}
        </div>
      </div>

      {selectedNode && (
        <NodeDetailPanel
          node={selectedNode}
          onClose={handleClosePanel}
          onOpenSubWorkflow={handleOpenSub}
        />
      )}
    </div>
  );
}

interface WorkflowNodeMiniData {
  node?: { kind?: string };
}

/**
 * Interactive workflow diagram. Presentational: pass `metadata` (the full
 * dataset) and a `workflowId`; the component renders the React Flow graph with
 * dagre layout, click-to-detail side panel, sub-workflow cross-navigation,
 * api-call endpoint detail, kind-coded nodes, and a legend. Selection is
 * controlled via `stepId`/`onStepChange` so the host can drive deep-links.
 */
export function WorkflowViewer(props: WorkflowViewerProps) {
  return (
    <ReactFlowProvider>
      <InnerViewer {...props} />
    </ReactFlowProvider>
  );
}

export default WorkflowViewer;
