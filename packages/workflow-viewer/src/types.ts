/**
 * Workflow metadata contract.
 *
 * This is the data contract produced by `scripts/generate-metadata.js`
 * (a static parser of the C# Elsa workflow builders + activity attributes)
 * and consumed by `<WorkflowViewer />`. The package is purely presentational:
 * metadata in → interactive diagram out. Nothing here fetches data.
 */

/** Node kinds. Each maps to a distinct color/icon in the viewer. */
export type WorkflowNodeKind =
  | 'activity'
  | 'dispatch-subworkflow'
  | 'api-call'
  | 'wait/bookmark'
  | 'gate'
  | 'decision'
  | 'terminal';

/** A single typed input/output port on an activity, with its description. */
export interface WorkflowPort {
  name: string;
  type: string;
  description: string;
}

/** Endpoint detail for an `api-call` node (service/method/route/purpose). */
export interface WorkflowApiDetail {
  service: string;
  method: string;
  route: string;
  purpose: string;
}

/**
 * Resolved source-code reference for a node (the "Code" tab). Present only when
 * the metadata generator could map the node's backing activity class to a file.
 */
export interface WorkflowCodeRef {
  /** Repo-relative path (e.g. `apps/tamma-elsa/src/Tamma.Activities/Foo.cs`). */
  file: string;
  /** 1-based line of the class declaration, if found. */
  line?: number;
  /** C# namespace of the activity class, if found. */
  namespace?: string;
  /** Permalink to the file (and line) on GitHub. */
  githubUrl?: string;
  /** Short, sanitized snippet around the declaration (doc-tag stripped). */
  snippet?: string;
}

/** A node in a workflow graph (one Elsa activity / control-flow node). */
export interface WorkflowNode {
  /** Stable id (the activity `Id` literal from the C# builder). */
  id: string;
  /** Human-readable display name. */
  name: string;
  /** C# class backing the node (e.g. `SelectWorkItemActivity`). */
  className: string;
  /** Visual + semantic kind. */
  kind: WorkflowNodeKind;
  /** Prose description (from `[Activity]`/doc-comment). */
  description: string;
  /** Typed inputs, each with a description. */
  inputs: WorkflowPort[];
  /** Typed outputs, each with a description. */
  outputs: WorkflowPort[];
  /** Named branch outcomes (e.g. `True`/`False`, `Approved`/`NeedsHuman`). */
  outcomes: string[];
  /** Free-text interaction hints (emits events, dispatches workflow, ...). */
  interactions: string[];
  /** True if this is the workflow's entry node. */
  isStart?: boolean;
  /** For dispatch nodes: the target workflow's id (cross-navigation link). */
  subWorkflowId?: string;
  /** Whether `subWorkflowId` resolves to a known workflow in the same dataset. */
  subWorkflowResolves?: boolean;
  /** For `api-call` nodes: the endpoint detail panel content. */
  api?: WorkflowApiDetail;
  /** Resolved backing source file/line (the "Code" tab). */
  code?: WorkflowCodeRef;
}

/** A directed edge between two nodes, optionally labelled by an outcome. */
export interface WorkflowEdge {
  from: string;
  to: string;
  /** Branch label (e.g. an outcome name) shown on the edge. */
  label?: string;
}

/** A single workflow: its graph plus catalogue metadata. */
export interface WorkflowMetadata {
  /** Workflow definition id (slug, e.g. `single-issue-cycle`). */
  id: string;
  /** Inventory id from the wiki catalogue (may differ via aliasing). */
  inventoryId?: string;
  /** Display name. */
  name: string;
  /** Short description. */
  description: string;
  /** Source wiki page slug (if catalogued). */
  wikiPage?: string | null;
  /** Sort order within the catalogue. */
  order?: number;
  /** Whether this workflow is part of the documented inventory. */
  inInventory?: boolean;
  /** Repo-relative path to the C# builder source file. */
  sourceFile?: string;
  /** Whether the graph was successfully parsed (vs. a metadata-only stub). */
  parsed?: boolean;
  /** Graph nodes. */
  nodes: WorkflowNode[];
  /** Graph edges. */
  edges: WorkflowEdge[];
}

/** Top-level dataset: the full `workflows.json` shape. */
export interface WorkflowDataset {
  generatedAt?: string;
  generator?: string;
  source?: string;
  kinds?: WorkflowNodeKind[];
  workflowCount?: number;
  inventoryCount?: number;
  workflows: WorkflowMetadata[];
}
