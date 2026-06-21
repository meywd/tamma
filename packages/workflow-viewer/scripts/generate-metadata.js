#!/usr/bin/env node
// @ts-check
/**
 * generate-metadata.js  (@tamma/workflow-viewer)
 * -----------------------------------------------------------------------------
 * Static extractor that turns Tamma's code-first Elsa workflows into a single
 * `workflows.json` — the data contract consumed by `<WorkflowViewer />`.
 *
 * WHY STATIC PARSING (and not Elsa's runtime JSON export):
 *   Elsa can serialize registered workflow definitions to JSON, but that path
 *   requires a running ElsaServer + Postgres + published definitions. The
 *   workflow graph, however, is *fully* declared in the C# builder source as
 *   string literals (activity `Id`, `Name`, `WorkflowDefinitionId = new("...")`,
 *   `Connect(a,b)` / `ConnectOutcome(a,"o",b)`), and the per-activity metadata
 *   lives in `[Activity]`, `[Input(Description=...)]`, `[Output]`, `[FlowNode]`
 *   attributes. That makes a deterministic, offline, zero-infra static parse the
 *   most reliable + lowest-fragility source of truth. This script:
 *     1. Reflects every `*Activity.cs` -> activity metadata registry
 *        (kind, description, inputs, outputs, outcomes, interactions, api info).
 *     2. Parses each `*Workflow.cs` builder -> nodes + edges + sub-workflow links.
 *     3. Cross-references `wiki/Workflows.md` for canonical name/desc/wikiPage and
 *        to scope output to the documented 30-workflow inventory.
 *     4. Emits a `workflows.json` matching the `WorkflowDataset` TS contract.
 *
 * Output path:
 *   Defaults to `packages/workflow-viewer/workflows.json`. Override with the
 *   `WORKFLOW_METADATA_OUT` env var or a first CLI arg, e.g. so the wiki's
 *   pre-build (`sync-content.ts`) can write it straight into its public dir:
 *     node generate-metadata.js apps/wiki-site/public/workflows.json
 *
 * Re-run with:  node packages/workflow-viewer/scripts/generate-metadata.js
 *           or:  pnpm --filter @tamma/workflow-viewer generate
 * -----------------------------------------------------------------------------
 */

import { readFileSync, writeFileSync, readdirSync, mkdirSync } from 'node:fs';
import { join, dirname, basename, isAbsolute, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
// scripts/ -> workflow-viewer/ -> packages/ -> repo root
const REPO_ROOT = join(__dirname, '..', '..', '..');
const WORKFLOWS_DIR = join(REPO_ROOT, 'apps', 'tamma-elsa', 'src', 'Tamma.ElsaServer', 'Workflows');
const ACTIVITIES_DIR = join(REPO_ROOT, 'apps', 'tamma-elsa', 'src', 'Tamma.Activities');
const WIKI_DIR = join(REPO_ROOT, 'wiki');
// Base for the "open on GitHub" permalinks emitted into node.code.githubUrl.
const GITHUB_BLOB_BASE = 'https://github.com/Tam-ma/tamma/blob/main';

/** Resolve the output path: CLI arg > env var > package-local default. */
function resolveOutFile() {
  const override = process.argv[2] || process.env.WORKFLOW_METADATA_OUT;
  if (override) {
    return isAbsolute(override) ? override : resolve(process.cwd(), override);
  }
  return join(__dirname, '..', 'workflows.json');
}
const OUT_FILE = resolveOutFile();

// ---------------------------------------------------------------------------
// Small utilities
// ---------------------------------------------------------------------------

/** Recursively collect *.cs files under a directory, skipping build outputs. */
function collectCs(dir) {
  const out = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (entry.name === 'bin' || entry.name === 'obj') continue;
      out.push(...collectCs(join(dir, entry.name)));
    } else if (entry.name.endsWith('.cs')) {
      out.push(join(dir, entry.name));
    }
  }
  return out;
}

/** Strip C# string escapes minimally for display. */
function unesc(s) {
  return s.replace(/\\"/g, '"').replace(/\\\\/g, '\\');
}

/** Repo-relative POSIX path for an absolute file under the repo root. */
function repoRel(absPath) {
  return absPath.slice(REPO_ROOT.length + 1).split('\\').join('/');
}

/** 1-based line number of a character offset within a source string. */
function lineAtOffset(src, offset) {
  if (offset < 0) return undefined;
  let line = 1;
  for (let i = 0; i < offset && i < src.length; i++) {
    if (src[i] === '\n') line++;
  }
  return line;
}

/**
 * Build a sanitized code reference for an activity class found in `src`/`file`.
 * Returns { file, line, namespace, githubUrl, snippet } or undefined.
 * IMPORTANT: any C# text surfaced here is run through stripDocTags so no raw
 * `<...>` can ever reach the (React-escaped) consumer.
 */
function codeRefFor(src, file, className) {
  const decl = src.search(
    new RegExp(`public\\s+(?:sealed\\s+|abstract\\s+|static\\s+|partial\\s+)*class\\s+${className}\\b`),
  );
  if (decl < 0) return undefined;
  const line = lineAtOffset(src, decl);
  const nsM = src.match(/^\s*namespace\s+([\w.]+)/m);
  const namespace = nsM ? nsM[1] : undefined;
  const relPath = repoRel(file);

  // Cheap snippet: the class declaration line plus a few following lines,
  // doc-tag-stripped and length-capped. Never includes method bodies.
  const declLineStart = src.lastIndexOf('\n', decl) + 1;
  const lines = src.slice(declLineStart).split('\n').slice(0, 4);
  const snippetRaw = lines.join('\n');
  const snippet = stripDocTags(snippetRaw).slice(0, 300).trimEnd();

  const ref = {
    file: relPath,
    githubUrl: `${GITHUB_BLOB_BASE}/${relPath}${line ? `#L${line}` : ''}`,
  };
  if (line) ref.line = line;
  if (namespace) ref.namespace = stripDocTags(namespace);
  if (snippet) ref.snippet = snippet;
  return ref;
}

/**
 * Strip XML-doc / HTML tags from doc-comment text in an injection-safe way.
 * A single-pass `.replace(/<[^>]+>/g, '')` is incomplete: a nested/reconstituting
 * input like `<scr<script>ipt>` survives one pass (CodeQL js/incomplete-multi-
 * character-sanitization). So we loop until the string is stable, then drop any
 * residual stray angle brackets — guaranteeing the result can never contain a
 * `<tag` fragment regardless of how the consumer renders it.
 */
function stripDocTags(s) {
  let prev;
  do {
    prev = s;
    s = s.replace(/<[^<>]*>/g, '');
  } while (s !== prev);
  return s.replace(/[<>]/g, '');
}

/** Pull the leading /// <summary> ... </summary> doc comment for a class. */
function extractClassSummary(src, className) {
  const idx = src.indexOf(`class ${className}`);
  if (idx < 0) return '';
  // Walk backwards collecting contiguous /// lines (and attribute lines between).
  const before = src.slice(0, idx);
  const lines = before.split('\n');
  const docLines = [];
  for (let i = lines.length - 1; i >= 0; i--) {
    const t = lines[i].trim();
    if (t.startsWith('///')) {
      docLines.unshift(t.replace(/^\/\/\/\s?/, ''));
    } else if (t.startsWith('[') || t === '' || t.startsWith('//')) {
      // attribute / blank / regular comment line between doc and class — keep scanning
      if (t.startsWith('[')) continue;
      if (t === '') continue;
      break;
    } else {
      break;
    }
  }
  const joined = docLines
    .filter((l) => !l.includes('<summary>') && !l.includes('</summary>'))
    .join(' ');
  return stripDocTags(joined).replace(/\s+/g, ' ').trim();
}

// ---------------------------------------------------------------------------
// Phase 1: Activity metadata registry
// ---------------------------------------------------------------------------

/**
 * @typedef {Object} ActivityMeta
 * @property {string} className
 * @property {string} [namespace]
 * @property {string} [activityType]   // "Tamma.ADL.CreatePullRequest" style label
 * @property {string} [displayName]    // 2nd [Activity] arg
 * @property {string} description
 * @property {string} kind             // detected kind
 * @property {{name:string,type:string,description:string}[]} inputs
 * @property {{name:string,type:string,description:string}[]} outputs
 * @property {string[]} outcomes       // from [FlowNode("a","b")]
 * @property {string[]} interactions
 * @property {{service:string}[]} apiHints
 */

/** Parse `[Input(...)]`/`[Output(...)]` decorated properties. */
function parsePorts(src, kind /* 'Input' | 'Output' */) {
  const ports = [];
  // Match an attribute block possibly spanning lines, then the property.
  const re = new RegExp(
    `\\[${kind}\\b([^\\]]*)\\]\\s*public\\s+${kind}<([^>]+(?:<[^>]+>)?[^>]*)>\\s+(\\w+)`,
    'gs'
  );
  let m;
  while ((m = re.exec(src)) !== null) {
    const attrBody = m[1] || '';
    const type = m[2].trim();
    const name = m[3];
    const dMatch = attrBody.match(/Description\s*=\s*"((?:[^"\\]|\\.)*)"/s);
    ports.push({
      name,
      type,
      description: dMatch ? unesc(dMatch[1]) : '',
    });
  }
  return ports;
}

/** Detect external/internal interaction services referenced in an activity body. */
function detectInteractions(src) {
  const interactions = [];
  const apiHints = [];
  const add = (label) => {
    if (!interactions.includes(label)) interactions.push(label);
  };
  if (/IGitHubIntegrationService|IGitHubActionsClient|Octokit/.test(src)) {
    add('GitHub API (IGitHubIntegrationService)');
    apiHints.push({ service: 'github' });
  }
  if (/\bTammaApiClient\b|ITammaApiClient/.test(src)) {
    add('Tamma API (TammaApiClient)');
    apiHints.push({ service: 'tamma-api' });
  }
  if (/ILlmProvider|ILLMProvider|IAiProvider|IProviderRegistry|ProviderChain|_provider\b/.test(src)) {
    add('LLM provider (multi-provider chain)');
    apiHints.push({ service: 'llm-provider' });
  }
  if (/\bHttpClient\b|IHttpClientFactory/.test(src) && !/TammaApiClient/.test(src)) {
    add('HTTP call (HttpClient)');
    apiHints.push({ service: 'http' });
  }
  if (/CreateBookmark|IBookmark|context\.CreateBookmark/.test(src)) {
    add('Waits on a bookmark (resumed by webhook/event)');
  }
  if (/IWorkflowDispatcher|DispatchWorkflowDefinitionRequest|IWorkflowRuntime/.test(src)) {
    add('Dispatches another workflow');
  }
  if (/IEventStore|EmitEventAsync|EventType\b/.test(src)) {
    add('Emits audit-trail event(s)');
  }
  if (/ICodeIndex|UpdateCodeIndex|VectorStore|Embedding/.test(src)) {
    add('Updates code index / vector DB');
  }
  return { interactions, apiHints };
}

/** Best-effort kind for a standalone activity class (refined later in workflow context). */
function detectActivityKind(src, className, apiHints) {
  if (/IWorkflowDispatcher|DispatchWorkflowDefinitionRequest/.test(src)) return 'dispatch-subworkflow';
  if (/CreateBookmark/.test(src) || /^WaitFor/.test(className) || /^Monitor/.test(className))
    return 'wait/bookmark';
  if (/Approval/.test(className) && /CreateBookmark/.test(src)) return 'gate';
  if (apiHints.length > 0) return 'api-call';
  return 'activity';
}

/** Find the sub-workflow definition id a custom Dispatch*Activity targets. */
function dispatchTargetOf(src) {
  const m = src.match(/DispatchWorkflowDefinitionRequest\(\s*"([^"]+)"/);
  return m ? m[1] : undefined;
}

/** Build the registry keyed by class name. */
function buildActivityRegistry() {
  /** @type {Record<string, ActivityMeta>} */
  const registry = {};
  const files = collectCs(ACTIVITIES_DIR);
  for (const file of files) {
    const src = readFileSync(file, 'utf8');
    // A file may declare multiple activity classes; iterate each public class.
    const classRe = /public\s+(?:sealed\s+|abstract\s+)?class\s+(\w+)\s*:\s*([\w<>, .]+)/g;
    let cm;
    while ((cm = classRe.exec(src)) !== null) {
      const className = cm[1];
      const baseList = cm[2];
      // Only treat as an Elsa activity if it derives from an Activity base or has [Activity]
      const hasActivityAttr = new RegExp(`\\[Activity\\b[\\s\\S]*?class\\s+${className}\\b`).test(src);
      const looksLikeActivity =
        /Activity\b/.test(baseList) || hasActivityAttr;
      if (!looksLikeActivity) continue;
      if (!className.endsWith('Activity')) {
        // Skip helper/base/service classes that merely mention Activity.
        if (!hasActivityAttr) continue;
      }

      // [Activity("Group","Display Name","Description", Kind=...)]
      let activityType, displayName, attrDescription;
      const actAttr = src.match(
        new RegExp(`\\[Activity\\(([\\s\\S]*?)\\)\\]\\s*(?:\\[[^\\]]*\\]\\s*)*public\\s+(?:sealed\\s+|abstract\\s+)?class\\s+${className}\\b`)
      );
      if (actAttr) {
        const args = actAttr[1];
        const strs = [...args.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map((x) => unesc(x[1]));
        if (strs.length >= 1) activityType = strs.length >= 2 ? strs[0] : undefined;
        if (strs.length >= 2) displayName = strs[strs.length >= 3 ? 1 : 0];
        if (strs.length >= 3) attrDescription = strs[2];
      }

      const inputs = parsePorts(src, 'Input');
      const outputs = parsePorts(src, 'Output');
      const fnMatch = src.match(new RegExp(`\\[FlowNode\\(([^\\]]*)\\)\\][\\s\\S]{0,400}?class\\s+${className}\\b`));
      const outcomes = fnMatch
        ? [...fnMatch[1].matchAll(/"([^"]+)"/g)].map((x) => x[1])
        : [];
      const { interactions, apiHints } = detectInteractions(src);
      const summary = extractClassSummary(src, className);
      const kind = detectActivityKind(src, className, apiHints);
      const target = dispatchTargetOf(src);
      const code = codeRefFor(src, file, className);

      registry[className] = {
        className,
        activityType,
        displayName,
        description: attrDescription || summary || '',
        summary,
        kind: target ? 'dispatch-subworkflow' : kind,
        inputs,
        outputs,
        outcomes,
        interactions,
        apiHints,
        dispatchTarget: target,
        code,
      };
    }
  }
  return registry;
}

// ---------------------------------------------------------------------------
// Phase 2: Canonical inventory from wiki/Workflows.md
// ---------------------------------------------------------------------------

function parseInventory() {
  const md = readFileSync(join(WIKI_DIR, 'Workflows.md'), 'utf8');
  /** @type {Record<string,{name:string,description:string,wikiPage:string,order:number}>} */
  const inv = {};
  const rowRe = /^\|\s*(\d+)\s*\|\s*\*\*(.+?)\*\*\s*\|\s*`([^`]+)`\s*\|\s*(.+?)\s*\|\s*\[Details\]\(([^)]+)\)\s*\|/gm;
  let m;
  while ((m = rowRe.exec(md)) !== null) {
    inv[m[3]] = {
      name: m[2].trim(),
      description: m[4].trim(),
      wikiPage: m[5].split('#')[0].trim(),
      order: parseInt(m[1], 10),
    };
  }
  return inv;
}

// ---------------------------------------------------------------------------
// Phase 3: Workflow builder parsing
// ---------------------------------------------------------------------------

/**
 * For a workflow source, build:
 *  - varToNode: variable name -> { id, name, className, dispatchTarget? }
 *  - nodes, edges
 */
function parseWorkflow(src, registry) {
  const defId = (src.match(/builder\.DefinitionId\s*=\s*"([^"]+)"/) ||
    src.match(/builder\.DefinitionId\s*=\s*DefinitionId/) && src.match(/DefinitionId\s*=\s*"([^"]+)"/)) || [];
  const definitionId = defId[1];
  const nameMatch = src.match(/builder\.Name\s*=\s*"((?:[^"\\]|\\.)*)"/);
  const descMatch = src.match(/builder\.Description\s*=\s*"((?:[^"\\]|\\.)*)"/);

  // ---- collect node declarations: var <ident> = ...
  // Strategy: find every `var X = new Type {  ... Id = "...", ... }` and helper-built nodes.
  /** @type {Record<string, any>} */
  const varToNode = {};

  // (a) Direct `new` declarations with an explicit Id.
  //     var x = new SomeType(...)? { ... Id = "Foo", Name = "Bar", WorkflowDefinitionId = new("def") ... };
  //     Handles single-line and multi-line initializers, including nested `{}` inside
  //     Input lambdas, by balance-matching the object-initializer braces.
  const declStartRe = /var\s+(\w+)\s*=\s*new\s+([\w<>]+)\s*(\([\s\S]*?\))?\s*\{/g;
  let m;
  while ((m = declStartRe.exec(src)) !== null) {
    const varName = m[1];
    let type = m[2].replace(/<.*$/, '');
    // balance-match from the `{` at end of this match
    const openIdx = declStartRe.lastIndex - 1;
    let depth = 0;
    let end = -1;
    for (let i = openIdx; i < src.length; i++) {
      const ch = src[i];
      if (ch === '{') depth++;
      else if (ch === '}') {
        depth--;
        if (depth === 0) {
          end = i;
          break;
        }
      }
    }
    if (end < 0) continue;
    const body = src.slice(openIdx + 1, end);
    // advance past the consumed initializer so the next exec doesn't re-scan its body
    declStartRe.lastIndex = end + 1;
    const idM = body.match(/\bId\s*=\s*"([^"]+)"/);
    const nameM = body.match(/\bName\s*=\s*(?:\$?)"((?:[^"\\]|\\.)*)"/);
    const wfM = body.match(/WorkflowDefinitionId\s*=\s*new\(\s*"([^"]+)"\s*\)/);
    if (!idM) {
      // Some inline control-flow nodes (FlowDecision/FlowSwitch) put Id inside body too — handled above.
      // If still no Id, skip (it's a non-node object literal).
      continue;
    }
    varToNode[varName] = {
      varName,
      id: idM[1],
      name: nameM ? unesc(nameM[1]) : idM[1],
      className: type,
      dispatchTarget: wfM ? wfM[1] : undefined,
    };
  }

  // (b) `new FlowDecision(...)` / `new FlowSwitch(...)` with Id in the object initializer
  //     handled by (a) when they use `{ Id = ... }`. But constructor-arg form:
  //     var x = new FlowDecision(ctx => ...) { Id = "X", Name = "Y" };
  //     The regex above already matches because `new Type (...) { ... }`.

  // (c) Helper-method-built nodes used in this repo:
  //     NotifyIssue("Id", repo, issue, "msg"...) -> DispatchWorkflow to update-issue-status
  const notifyRe = /var\s+(\w+)\s*=\s*NotifyIssue\(\s*"([^"]+)"[\s\S]*?,\s*"((?:[^"\\]|\\.)*)"/g;
  while ((m = notifyRe.exec(src)) !== null) {
    const varName = m[1];
    if (varToNode[varName]) continue;
    varToNode[varName] = {
      varName,
      id: m[2],
      name: 'Notify: ' + unesc(m[3]).slice(0, 40),
      className: 'DispatchWorkflow',
      dispatchTarget: 'update-issue-status',
      synthetic: 'notify',
    };
  }

  // (d) Assign(var, lambda, "Id", "Name") -> SetVariable
  const assignRe = /var\s+(\w+)\s*=\s*Assign\([\s\S]*?,\s*"([^"]+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)/g;
  while ((m = assignRe.exec(src)) !== null) {
    const varName = m[1];
    if (varToNode[varName]) continue;
    varToNode[varName] = {
      varName,
      id: m[2],
      name: unesc(m[3]),
      className: 'SetVariable',
      synthetic: 'assign',
    };
  }

  // (e) GENERIC file-local factory helpers used across several workflows, e.g.
  //       var x = StageDeployDispatch("Id", "Name", ...)
  //       var x = ExtractStageResult("Id", "Name", ...)
  //       var x = CreateFailureNode("Id", "Name", ...)
  //     Convention in this codebase: `private static <ReturnType> Helper(string id, string displayName, ...)`.
  //     We map the var to {id, name} from the first two string args, infer the class
  //     from the helper's declared return type, and scan the helper body for a
  //     sub-workflow target. This generalizes without hard-coding each helper.
  const helperRe = /var\s+(\w+)\s*=\s*([A-Z]\w+)\(\s*"([^"]+)"\s*,\s*(?:\$?)"((?:[^"\\]|\\.)*)"/g;
  while ((m = helperRe.exec(src)) !== null) {
    const varName = m[1];
    const helper = m[2];
    if (varToNode[varName]) continue;
    if (helper === 'NotifyIssue' || helper === 'Assign') continue; // handled above
    // exclude direct `new` (helper would be a Type already handled) and Connect helpers
    if (/^(Connect|ConnectOutcome|WithLabel|WithVariable)$/.test(helper)) continue;
    // find the helper definition's return type + body
    const defM = src.match(
      new RegExp(`private\\s+static\\s+([\\w<>]+)\\s+${helper}\\s*\\(`)
    );
    let retType = defM ? defM[1].replace(/<.*$/, '') : 'Activity';
    // body for sub-workflow target detection
    let target;
    const defIdx = src.search(new RegExp(`private\\s+static\\s+[\\w<>]+\\s+${helper}\\s*\\(`));
    if (defIdx >= 0) {
      const slice = src.slice(defIdx, defIdx + 1200);
      const wfM = slice.match(/WorkflowDefinitionId\s*=\s*new\(\s*"([^"]+)"\s*\)/) ||
        slice.match(/DispatchWorkflowDefinitionRequest\(\s*"([^"]+)"/);
      if (wfM) target = wfM[1];
    }
    varToNode[varName] = {
      varName,
      id: m[3],
      name: unesc(m[4]),
      className: retType,
      dispatchTarget: target,
      synthetic: 'helper:' + helper,
    };
  }

  // ---- determine which vars are placed in the flowchart's Activities list.
  // The Flowchart Activities list is the one whose surrounding `new Flowchart {`
  // is `builder.Root`. Find it by locating `builder.Root = new Flowchart` and the
  // first `Activities =` after it; balance-match that list. Fall back to any
  // top-level `Activities =` if no explicit Flowchart Root is present.
  const activitiesBlock = (() => {
    const balanced = (from) => {
      const open = src.indexOf('{', from);
      if (open < 0) return '';
      let depth = 0;
      for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') {
          depth--;
          if (depth === 0) return src.slice(open + 1, i);
        }
      }
      return '';
    };
    const rootIdx = src.search(/builder\.Root\s*=\s*new\s+(?:Flowchart|Sequence)/);
    if (rootIdx >= 0) {
      const aIdx = src.indexOf('Activities =', rootIdx);
      if (aIdx >= 0) return balanced(aIdx + 'Activities ='.length);
    }
    const aIdx = src.indexOf('Activities =');
    return aIdx >= 0 ? balanced(aIdx + 'Activities ='.length) : '';
  })();
  // Ordered list of node vars in the root Activities list (for implicit Sequence edges).
  const activityVarOrder = [...activitiesBlock.matchAll(/\b(\w+)\b/g)]
    .map((x) => x[1])
    .filter((v) => varToNode[v]);
  const activityVars = new Set(activityVarOrder);

  // builder.Root = <singleVar>  (single-activity workflows, e.g. update-issue-status)
  const singleRootM = src.match(/builder\.Root\s*=\s*(\w+)\s*;/);
  if (singleRootM && varToNode[singleRootM[1]]) {
    activityVars.add(singleRootM[1]);
    activityVarOrder.push(singleRootM[1]);
  }

  // ---- edges. Tamma workflows use several connection-construction styles; cover all.
  const edges = [];
  const pushEdge = (av, bv, label) => {
    const a = varToNode[av];
    const b = varToNode[bv];
    if (a && b) edges.push(label ? { from: a.id, to: b.id, label } : { from: a.id, to: b.id });
  };

  // (1) inline helpers: Connect(a, b) / ConnectOutcome(a, "o", b)
  let re = /\bConnect\(\s*(\w+)\s*,\s*(\w+)\s*\)/g;
  while ((m = re.exec(src)) !== null) pushEdge(m[1], m[2]);
  re = /\bConnectOutcome\(\s*(\w+)\s*,\s*"([^"]+)"\s*,\s*(\w+)\s*\)/g;
  while ((m = re.exec(src)) !== null) pushEdge(m[1], m[3], m[2]);

  // (2) flowchart-arg helpers (TestingWorkflow):
  //     Connect(flowchart, a, b)  /  Connect(flowchart, a, b, "outcome")
  re = /\bConnect\(\s*\w+\s*,\s*(\w+)\s*,\s*(\w+)\s*(?:,\s*"([^"]+)")?\s*\)/g;
  while ((m = re.exec(src)) !== null) {
    // skip the 2-arg form already captured above (this regex requires >=2 node args after flowchart)
    pushEdge(m[1], m[2], m[3]);
  }

  // (3) raw FlowConnection `new(...)` (CodeReviewWorkflow, MentorshipWorkflow):
  //       new(a, b)
  //       new(new FlowEndpoint(a [, "outcome"]), new FlowEndpoint(b [, "outcome"]))
  // Only consider these inside a `Connections = { ... }` block to avoid matching
  // unrelated `new(...)` object construction.
  const connBlocks = [];
  {
    let cIdx = 0;
    while ((cIdx = src.indexOf('Connections =', cIdx)) >= 0) {
      const open = src.indexOf('{', cIdx);
      let depth = 0;
      for (let i = open; i < src.length; i++) {
        if (src[i] === '{') depth++;
        else if (src[i] === '}') {
          depth--;
          if (depth === 0) {
            connBlocks.push(src.slice(open + 1, i));
            cIdx = i + 1;
            break;
          }
        }
      }
      if (depth !== 0) break;
    }
  }
  for (const block of connBlocks) {
    // new(new FlowEndpoint(a, "o"?), new FlowEndpoint(b, "o"?))
    let er = /new\(\s*new\s+FlowEndpoint\(\s*(\w+)\s*(?:,\s*"([^"]+)")?\s*\)\s*,\s*new\s+FlowEndpoint\(\s*(\w+)\s*(?:,\s*"[^"]*")?\s*\)\s*\)/g;
    while ((m = er.exec(block)) !== null) pushEdge(m[1], m[3], m[2]);
    // bare new(a, b)  — exclude the FlowEndpoint form already handled
    er = /new\(\s*(\w+)\s*,\s*(\w+)\s*\)/g;
    while ((m = er.exec(block)) !== null) {
      if (varToNode[m[1]] && varToNode[m[2]]) pushEdge(m[1], m[2]);
    }
  }

  // (4) Implicit sequential edges for `builder.Root = new Sequence { Activities = {...} }`
  //     workflows (tenant lifecycle, secret rotation) that have no Connections block:
  //     each activity runs after the previous in list order.
  const isSequenceRoot = /builder\.Root\s*=\s*new\s+Sequence/.test(src);
  if (edges.length === 0 && (isSequenceRoot || connBlocks.length === 0) && activityVarOrder.length > 1) {
    for (let i = 0; i < activityVarOrder.length - 1; i++) {
      pushEdge(activityVarOrder[i], activityVarOrder[i + 1]);
    }
  }

  // de-duplicate identical edges
  {
    const seen = new Set();
    const deduped = [];
    for (const e of edges) {
      const key = e.from + '>' + e.to + '>' + (e.label || '');
      if (!seen.has(key)) {
        seen.add(key);
        deduped.push(e);
      }
    }
    edges.length = 0;
    edges.push(...deduped);
  }

  // ---- materialize node objects, enriching from the activity registry
  /** @type {Record<string, any>} */
  const nodeById = {};
  const ensureNode = (n) => {
    if (nodeById[n.id]) return nodeById[n.id];
    const meta = registry[n.className];
    let kind = classifyNodeKind(n, meta);
    const node = {
      id: n.id,
      name: n.name,
      className: n.className,
      kind,
      description: meta?.description || builtinDescription(n.className) || '',
      inputs: meta?.inputs || [],
      outputs: meta?.outputs || [],
      outcomes: meta?.outcomes || builtinOutcomes(n.className),
      interactions: meta?.interactions || [],
    };
    // sub-workflow target: from the in-builder declaration, else from the custom
    // Dispatch*Activity's own DispatchWorkflowDefinitionRequest("...") string.
    const subTarget = n.dispatchTarget || meta?.dispatchTarget;
    if (subTarget) node.subWorkflowId = subTarget;
    if (meta?.apiHints?.length) {
      node.api = apiDetail(meta, n);
    }
    // Source reference for the "Code" tab — only when the node maps to a real
    // activity class we found a file for (built-in Elsa control nodes /
    // synthetic dispatch nodes have no resolvable source).
    if (meta?.code) {
      node.code = meta.code;
    }
    nodeById[n.id] = node;
    return node;
  };

  // Start node
  const startM = src.match(/Start\s*=\s*(\w+)\b/);
  const startVar = startM ? startM[1] : undefined;

  for (const v of activityVars) ensureNode(varToNode[v]);
  // also ensure edge endpoints exist (some helper nodes appear only via var in edges)
  for (const [, n] of Object.entries(varToNode)) {
    const referenced = edges.some((e) => e.from === n.id || e.to === n.id) || activityVars.has(n.varName);
    if (referenced) ensureNode(n);
  }

  const nodes = Object.values(nodeById);
  if (startVar && varToNode[startVar] && nodeById[varToNode[startVar].id]) {
    nodeById[varToNode[startVar].id].isStart = true;
  }

  return {
    id: definitionId,
    name: nameMatch ? unesc(nameMatch[1]) : definitionId,
    description: descMatch ? unesc(descMatch[1]) : '',
    nodes,
    edges,
  };
}

/** Refine node kind using class name + registry meta. */
function classifyNodeKind(n, meta) {
  if (n.dispatchTarget) return 'dispatch-subworkflow';
  const cn = n.className;
  if (cn === 'DispatchWorkflow' || cn === 'RunWorkflow') return 'dispatch-subworkflow';
  if (cn === 'FlowDecision' || cn === 'If' || cn === 'FlowSwitch' || cn === 'Switch') return 'decision';
  if (cn === 'ForEach' || cn === 'While' || cn === 'For') return 'decision';
  if (cn === 'Finish') return 'terminal';
  if (cn === 'Delay' || cn === 'Timer') return 'wait/bookmark';
  if (/^WaitFor/.test(cn) || /^Monitor/.test(cn)) {
    return /Approval|Merge/.test(cn) ? 'gate' : 'wait/bookmark';
  }
  if (meta) return meta.kind;
  if (cn === 'SetVariable' || cn === 'SetOutput') return 'activity';
  return 'activity';
}

function builtinDescription(cn) {
  const map = {
    DispatchWorkflow: 'Dispatches a sub-workflow (Elsa DispatchWorkflow). May wait for completion or fire-and-forget.',
    RunWorkflow: 'Runs a sub-workflow inline and waits for its result.',
    SetVariable: 'Assigns a value to a workflow variable.',
    SetOutput: 'Sets a workflow output value returned to the caller.',
    FlowDecision: 'Boolean branch — routes to True / False based on a condition.',
    FlowSwitch: 'Multi-way branch — routes to one of several named outcomes.',
    If: 'Conditional branch (Then / Else).',
    ForEach: 'Iterates over a collection, running the body per item.',
    While: 'Repeats the body while a condition holds.',
    Finish: 'Terminal node — completes the workflow.',
    Sequence: 'Runs child activities in order.',
    Delay: 'Pauses execution for a fixed duration.',
  };
  return map[cn] || '';
}

function builtinOutcomes(cn) {
  const map = {
    FlowDecision: ['True', 'False'],
    If: ['True', 'False'],
    ForEach: ['Done'],
    While: ['Done'],
  };
  return map[cn] || [];
}

function apiDetail(meta, n) {
  const svc = meta.apiHints[0]?.service;
  const purpose = meta.description || meta.summary || '';
  if (svc === 'github') {
    return { service: 'GitHub', method: 'REST', route: 'GitHub API (via IGitHubIntegrationService)', purpose };
  }
  if (svc === 'tamma-api') {
    return { service: 'Tamma API', method: 'HTTP', route: 'tamma-api (via TammaApiClient)', purpose };
  }
  if (svc === 'llm-provider') {
    return { service: 'LLM Provider', method: 'HTTP', route: 'Provider chain (Anthropic / OpenAI / OpenRouter / ...)', purpose };
  }
  if (svc === 'http') {
    return { service: 'HTTP', method: 'HTTP', route: 'Outbound HTTP (HttpClient)', purpose };
  }
  return undefined;
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

function main() {
  const registry = buildActivityRegistry();
  const inventory = parseInventory();

  const wfFiles = collectCs(WORKFLOWS_DIR).filter((f) => /Workflow\.cs$/.test(basename(f)));
  /** @type {Record<string, any>} */
  const byId = {};
  const parsedExtra = [];
  for (const file of wfFiles) {
    const src = readFileSync(file, 'utf8');
    if (!/:\s*WorkflowBase/.test(src)) continue; // only real workflow definitions
    try {
      const wf = parseWorkflow(src, registry);
      if (!wf.id) continue;
      wf.sourceFile = 'apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/' + basename(file);
      byId[wf.id] = wf;
    } catch (e) {
      console.error(`! Failed to parse ${basename(file)}: ${e.message}`);
    }
  }

  // The wiki inventory lists `merge-complete` for the workflow whose builder
  // actually publishes DefinitionId `merge` (MergeWorkflow, name "Merge Complete").
  // Alias so the inventory row picks up the parsed `merge` graph.
  const inventoryAlias = { 'merge-complete': 'merge' };

  // Merge inventory metadata (canonical name/description/wikiPage/order) onto parsed graphs.
  const workflows = [];
  for (const [defId, inv] of Object.entries(inventory)) {
    const realId = byId[defId] ? defId : inventoryAlias[defId];
    const parsed = byId[defId] || byId[inventoryAlias[defId]];
    workflows.push({
      id: realId || defId,
      inventoryId: defId,
      name: inv.name,
      description: inv.description || parsed?.description || '',
      wikiPage: inv.wikiPage,
      order: inv.order,
      inInventory: true,
      sourceFile: parsed?.sourceFile,
      nodes: parsed?.nodes || [],
      edges: parsed?.edges || [],
      parsed: !!parsed,
    });
  }
  // Include parsed workflows that aren't in the 30-inventory (schedulers, tenant lifecycle...)
  const aliasedReal = new Set(Object.values(inventoryAlias));
  for (const [defId, wf] of Object.entries(byId)) {
    if (inventory[defId]) continue;
    if (aliasedReal.has(defId)) continue; // already represented by an inventory row
    workflows.push({
      id: defId,
      name: wf.name,
      description: wf.description,
      wikiPage: null,
      order: 1000 + workflows.length,
      inInventory: false,
      sourceFile: wf.sourceFile,
      nodes: wf.nodes,
      edges: wf.edges,
      parsed: true,
    });
  }

  workflows.sort((a, b) => a.order - b.order);

  // Build a quick set of valid subworkflow ids for the explorer to know which links resolve.
  const known = new Set(workflows.map((w) => w.id));
  for (const w of workflows) {
    for (const n of w.nodes) {
      if (n.subWorkflowId) {
        n.subWorkflowResolves = known.has(n.subWorkflowId);
      }
    }
  }

  const out = {
    generatedAt: new Date().toISOString(),
    generator: 'packages/workflow-viewer/scripts/generate-metadata.js',
    source: 'apps/tamma-elsa/src/Tamma.ElsaServer/Workflows + Tamma.Activities (static parse)',
    kinds: ['activity', 'dispatch-subworkflow', 'api-call', 'wait/bookmark', 'gate', 'decision', 'terminal'],
    workflowCount: workflows.length,
    inventoryCount: workflows.filter((w) => w.inInventory).length,
    workflows,
  };

  mkdirSync(dirname(OUT_FILE), { recursive: true });
  writeFileSync(OUT_FILE, JSON.stringify(out, null, 2));

  // Report
  const parsedInv = workflows.filter((w) => w.inInventory && w.parsed).length;
  const totalInv = workflows.filter((w) => w.inInventory).length;
  console.log(`Activity registry: ${Object.keys(registry).length} activities`);
  console.log(`Inventory workflows: ${totalInv} (graph-parsed: ${parsedInv})`);
  console.log(`Total workflows in JSON: ${workflows.length}`);
  console.log(`Wrote ${OUT_FILE}`);
  const missing = workflows.filter((w) => w.inInventory && !w.parsed).map((w) => w.id);
  if (missing.length) console.log(`Inventory ids without a parsed graph: ${missing.join(', ')}`);
  const subwfLinks = workflows.flatMap((w) => w.nodes.filter((n) => n.subWorkflowId)).length;
  const subwfResolved = workflows.flatMap((w) => w.nodes.filter((n) => n.subWorkflowResolves)).length;
  console.log(`Sub-workflow links: ${subwfLinks} (resolve to a known workflow: ${subwfResolved})`);
  const apiNodes = workflows.flatMap((w) => w.nodes.filter((n) => n.api)).length;
  console.log(`API-call nodes with endpoint detail: ${apiNodes}`);
}

main();
