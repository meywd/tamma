/**
 * @tamma/workflow-viewer
 *
 * A presentational React + React Flow package that renders interactive Tamma
 * workflow diagrams from workflow metadata. Metadata in → interactive diagram
 * out. No data-fetching is baked in — the host passes the dataset and a
 * workflow id, and drives selection/navigation via controlled props for
 * shareable deep-links.
 *
 * Metadata is produced by `scripts/generate-metadata.js` (a static parser of
 * the C# Elsa workflow builders + activity attributes).
 */
export { WorkflowViewer, default } from './WorkflowViewer';
export type { WorkflowViewerProps } from './WorkflowViewer';
export { NodeDetailPanel } from './NodeDetailPanel';
export type { NodeDetailPanelProps } from './NodeDetailPanel';
export { WorkflowMap } from './WorkflowMap';
export type { WorkflowMapProps } from './WorkflowMap';
export { StationDetailPopup } from './StationDetailPopup';
export type { StationDetailPopupProps } from './StationDetailPopup';
export { buildGraph } from './layout';
export type { LaidOutGraph } from './layout';
export { buildMapLayout } from './mapLayout';
export type { MapLayout, MapStation, MapRail } from './mapLayout';
export { KIND_DESCRIPTORS, KIND_ORDER, kindOf } from './kinds';
export type { KindDescriptor } from './kinds';
export type {
  WorkflowDataset,
  WorkflowMetadata,
  WorkflowNode,
  WorkflowEdge,
  WorkflowPort,
  WorkflowApiDetail,
  WorkflowCodeRef,
  WorkflowNodeKind,
} from './types';
