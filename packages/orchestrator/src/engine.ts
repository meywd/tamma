import * as readline from 'node:readline';
import { randomUUID } from 'node:crypto';
import type { ILogger } from '@tamma/shared/contracts';
import type {
  TammaConfig,
  IssueData,
  DevelopmentPlan,
  AgentTaskResult,
  PullRequestInfo,
  IEventStore,
  WorkflowPhase,
} from '@tamma/shared';
import { EngineState, EngineEventType, sleep, slugify, extractIssueReferences, DEFAULT_TENANT_ID } from '@tamma/shared';
import { WorkflowError, EngineError } from '@tamma/shared';
import type { IAgentProvider, AgentTaskConfig, IRoleBasedAgentResolver } from '@tamma/providers';
import type { IGitPlatform } from '@tamma/platforms';
/** Statistics tracked by the engine across its lifetime. */
export interface EngineStats {
  issuesProcessed: number;
  totalCostUsd: number;
  startedAt: number;
}

/** Callback invoked on every state transition. */
export type OnStateChangeCallback = (
  newState: EngineState,
  issue: IssueData | null,
  stats: EngineStats,
) => void;

/** Callback that replaces the default readline approval prompt. */
export type ApprovalHandler = (plan: DevelopmentPlan) => Promise<'approve' | 'reject' | 'skip'>;

/** Dependencies injected into TammaEngine at construction time. */
export interface EngineContext {
  config: TammaConfig;
  platform: IGitPlatform;
  agent?: IAgentProvider;
  logger: ILogger;
  eventStore?: IEventStore;
  onStateChange?: OnStateChangeCallback;
  approvalHandler?: ApprovalHandler;
  agentResolver?: IRoleBasedAgentResolver;
  /** Tenant ID for event scoping. Defaults to DEFAULT_TENANT_ID. */
  tenantId?: string;
}

/**
 * Core autonomous development engine.
 *
 * Polls a GitHub repository for labeled issues, generates a development plan
 * via the Claude Agent SDK, implements changes, opens a PR, and merges it
 * once CI passes. Each cycle follows the state machine defined by
 * {@link EngineState}.
 *
 * ## Lifecycle
 * 1. Construct with {@link EngineContext}.
 * 2. Call {@link initialize} to verify provider availability.
 * 3. Call {@link run} for the continuous poll loop, or {@link processOneIssue}
 *    for a single-shot execution.
 * 4. Call {@link dispose} to tear down resources.
 *
 * ## Pipeline (per issue)
 * selectIssue → analyzeIssue → generatePlan → awaitApproval →
 * createBranch → implementCode → createPR → monitorAndMerge
 *
 * ## Error handling
 * - On failure the state transitions to {@link EngineState.ERROR} and stays
 *   there until the {@link run} loop resets it. This preserves diagnostic info
 *   for callers inspecting {@link getState}.
 * - {@link WorkflowError} with `retryable: true` signals transient failures.
 */
export class TammaEngine {
  private state: EngineState = EngineState.IDLE;
  private currentIssue: IssueData | null = null;
  private currentPlan: DevelopmentPlan | null = null;
  private currentBranch: string | null = null;
  private currentPR: PullRequestInfo | null = null;
  private running = false;
  private processing = false;
  private currentPipelinePromise: Promise<void> | null = null;
  private issuesProcessed = 0;
  private totalCostUsd = 0;
  private readonly startedAt: number;

  private readonly config: TammaConfig;
  private readonly platform: IGitPlatform;
  private readonly agent: IAgentProvider | undefined;
  private readonly agentResolver: IRoleBasedAgentResolver | undefined;
  private readonly engineId = randomUUID();
  private readonly logger: ILogger;
  private readonly eventStore: IEventStore | undefined;
  private readonly tenantId: string;
  private readonly onStateChange: OnStateChangeCallback | undefined;
  private readonly approvalHandler: ApprovalHandler | undefined;

  constructor(ctx: EngineContext) {
    if (!ctx.agent && !ctx.agentResolver) {
      throw new EngineError('Either agent or agentResolver must be provided in EngineContext');
    }
    if (ctx.agent && ctx.agentResolver) {
      ctx.logger.warn('Both agent and agentResolver provided; resolver takes precedence for phase resolution');
    }

    this.config = ctx.config;
    this.platform = ctx.platform;
    if (ctx.agent !== undefined) {
      this.agent = ctx.agent;
    }
    if (ctx.agentResolver !== undefined) {
      this.agentResolver = ctx.agentResolver;
    }
    this.logger = ctx.logger;
    this.eventStore = ctx.eventStore;
    this.tenantId = ctx.tenantId ?? DEFAULT_TENANT_ID;
    this.onStateChange = ctx.onStateChange;
    this.approvalHandler = ctx.approvalHandler;
    this.startedAt = Date.now();
  }

  /** Verify the agent provider is reachable. Must be called before {@link run}. */
  async initialize(): Promise<void> {
    if (this.agent) {
      const available = await this.agent.isAvailable();
      if (!available) {
        throw new EngineError('Agent provider is not available. Check ANTHROPIC_API_KEY.');
      }
    }
    this.logger.info('TammaEngine initialized', {
      mode: this.config.mode,
      model: this.config.agent?.model ?? (this.agentResolver ? 'resolver-mode' : 'unknown'),
      approvalMode: this.config.engine.approvalMode,
    });
  }

  /** Stop the run loop and release provider resources. Awaits any in-flight pipeline cycle. */
  async dispose(): Promise<void> {
    this.running = false;
    if (this.currentPipelinePromise) {
      await this.currentPipelinePromise.catch(() => {
        // Swallow errors — dispose should not throw from an in-flight pipeline
      });
    }
    if (this.agent) {
      await this.agent.dispose();
    }
    await this.agentResolver?.dispose();
    await this.platform.dispose();
    this.logger.info('TammaEngine disposed');
  }

  /**
   * Continuous poll loop. Processes one issue per iteration, then sleeps for
   * {@link EngineConfig.pollIntervalMs} before polling again. Errors are
   * caught, logged, and the loop continues.
   *
   * Call {@link dispose} or send SIGINT/SIGTERM to break out.
   */
  async run(): Promise<void> {
    if (this.running) {
      this.logger.warn('Engine run loop already active, skipping concurrent call');
      return;
    }
    this.running = true;
    this.logger.info('TammaEngine run loop started');

    while (this.running) {
      try {
        await this.processOneIssue();
        // On success, reset to IDLE for next iteration
        await this.resetCurrentWork();
      } catch (err: unknown) {
        // processOneIssue already sets ERROR state and clears work references.
        // Do not call resetCurrentWork() here — it would overwrite ERROR → IDLE
        // before observers have a chance to see the error state.
        const message = err instanceof Error ? err.message : String(err);
        this.logger.error('Error processing issue', {
          error: message,
          state: this.state,
        });
        // Reset to IDLE only after logging, so the error state was observable
        await this.setState(EngineState.IDLE);
      }

      if (this.running) {
        this.logger.info('Polling for next issue', {
          intervalMs: this.config.engine.pollIntervalMs,
        });
        await sleep(this.config.engine.pollIntervalMs);
      }
    }
  }

  /**
   * Execute the full pipeline for a single issue: select → analyze → plan →
   * approve → branch → implement → PR → merge.
   *
   * On success, state ends at MERGING. On failure, state is set to ERROR and
   * the error is re-thrown. In both cases, work references (currentIssue, etc.)
   * are cleared in the finally block.
   */
  async processOneIssue(): Promise<void> {
    if (this.processing) {
      this.logger.warn('Pipeline already processing, skipping concurrent call');
      return;
    }
    this.processing = true;

    const pipeline = this.runPipeline();
    this.currentPipelinePromise = pipeline;

    try {
      await pipeline;
    } finally {
      this.currentPipelinePromise = null;
      this.processing = false;
    }
  }

  private async runPipeline(): Promise<void> {
    // Step 1: Select issue
    const issue = await this.selectIssue();
    if (issue === null) {
      this.logger.info('No issues found, staying idle');
      return;
    }

    try {
      // Step 2: Analyze
      const context = await this.analyzeIssue(issue);

      // Step 3: Generate plan
      const plan = await this.generatePlan(issue, context);

      // Step 4: Await approval
      await this.awaitApproval(plan);

      // Step 5: Create branch
      const branch = await this.createBranch(issue);

      // Step 6: Implement
      const implResult = await this.implementCode(issue, plan, branch);
      if (!implResult.success) {
        await this.recordEvent(EngineEventType.IMPLEMENTATION_FAILED, issue.number, { error: implResult.error });
        throw new WorkflowError(
          `Implementation failed: ${implResult.error ?? 'Unknown error'}`,
          { retryable: true, context: { issueNumber: issue.number } },
        );
      }

      // Step 7: Create PR
      const pr = await this.createPR(issue, plan, branch);

      // Step 8: Monitor and merge
      await this.monitorAndMerge(pr, issue);
    } catch (err: unknown) {
      await this.recordEvent(EngineEventType.ERROR_OCCURRED, this.currentIssue?.number, { error: err instanceof Error ? err.message : String(err) });
      await this.setState(EngineState.ERROR);
      throw err;
    } finally {
      // Clear work references but preserve ERROR state if set by catch block.
      // resetCurrentWork() would overwrite ERROR → IDLE which loses diagnostic info.
      this.currentIssue = null;
      this.currentPlan = null;
      this.currentBranch = null;
      this.currentPR = null;
    }
  }

  /**
   * Query the platform for open issues matching configured labels, pick the
   * oldest one (FIFO), assign it to the bot, and post a pickup comment.
   * Returns null when no eligible issues are found.
   */
  async selectIssue(): Promise<IssueData | null> {
    await this.setState(EngineState.SELECTING_ISSUE);
    const { owner, repo, issueLabels, excludeLabels, botUsername } =
      this.config.github;

    const result = await this.platform.listIssues(owner, repo, {
      state: 'open',
      labels: issueLabels,
      sort: 'created',
      direction: 'asc',
    });

    // Filter out issues with exclude labels
    const candidates = result.data.filter((issue) => {
      const hasExclude = issue.labels.some((label) =>
        excludeLabels.includes(label),
      );
      return !hasExclude;
    });

    if (candidates.length === 0) {
      await this.setState(EngineState.IDLE);
      return null;
    }

    const selected = candidates[0]!;

    // Assign to bot
    await this.platform.assignIssue(owner, repo, selected.number, [botUsername]);

    // Comment on issue
    await this.platform.addIssueComment(
      owner,
      repo,
      selected.number,
      `🤖 Tamma is picking up this issue and will begin working on it.`,
    );

    const issueData: IssueData = {
      number: selected.number,
      title: selected.title,
      body: selected.body,
      labels: selected.labels,
      url: selected.url,
      comments: selected.comments.map((c) => ({
        id: c.id,
        author: c.author,
        body: c.body,
        createdAt: c.createdAt,
      })),
      relatedIssueNumbers: extractIssueReferences(
        selected.body + ' ' + selected.comments.map((c) => c.body).join(' '),
      ),
      createdAt: selected.createdAt,
    };

    this.currentIssue = issueData;
    await this.recordEvent(EngineEventType.ISSUE_SELECTED, issueData.number, { title: issueData.title, url: issueData.url });
    this.logger.info('Issue selected', {
      number: issueData.number,
      title: issueData.title,
      url: issueData.url,
    });

    return issueData;
  }

  /**
   * Build a markdown context document from the issue body, comments, and any
   * related issues referenced via `#N` syntax. Used as input for plan
   * generation.
   */
  async analyzeIssue(issue: IssueData): Promise<string> {
    await this.setState(EngineState.ANALYZING);
    const { owner, repo } = this.config.github;

    // Fetch full issue with comments
    const fullIssue = await this.platform.getIssue(owner, repo, issue.number);

    // Fetch related issues
    const relatedContexts: string[] = [];
    for (const refNum of issue.relatedIssueNumbers) {
      try {
        const related = await this.platform.getIssue(owner, repo, refNum);
        relatedContexts.push(
          `Related Issue #${related.number}: ${related.title}\n${related.body}`,
        );
      } catch (err: unknown) {
        const msg = err instanceof Error ? err.message : String(err);
        this.logger.warn('Failed to fetch related issue', {
          issueNumber: refNum,
          error: msg,
        });
      }
    }

    // Build context summary
    const sections = [
      `## Issue #${issue.number}: ${issue.title}`,
      `**Labels:** ${issue.labels.join(', ')}`,
      `**Created:** ${issue.createdAt}`,
      '',
      '### Description',
      fullIssue.body,
      '',
    ];

    if (fullIssue.comments.length > 0) {
      sections.push('### Comments');
      for (const comment of fullIssue.comments) {
        sections.push(`**${comment.author}** (${comment.createdAt}):`);
        sections.push(comment.body);
        sections.push('');
      }
    }

    if (relatedContexts.length > 0) {
      sections.push('### Related Issues');
      sections.push(...relatedContexts);
      sections.push('');
    }

    // Fetch recent commits for additional context
    try {
      const commits = await this.platform.listCommits(owner, repo, { perPage: 10 });
      if (commits.length > 0) {
        sections.push('### Recent Commits');
        for (const commit of commits) {
          sections.push(`- \`${commit.sha.slice(0, 7)}\` ${(commit.message.split('\n')[0] ?? 'no message')} (${commit.author})`);
        }
        sections.push('');
      }
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : String(err);
      this.logger.warn('Failed to fetch recent commits', { error: msg });
    }

    const context = sections.join('\n');

    await this.recordEvent(EngineEventType.ISSUE_ANALYZED, issue.number, { contextLength: context.length, relatedIssues: issue.relatedIssueNumbers.length });
    this.logger.info('Issue analysis complete', {
      issueNumber: issue.number,
      contextLength: context.length,
      relatedIssues: issue.relatedIssueNumbers.length,
    });

    return context;
  }

  /**
   * Resolve an agent provider for the given workflow phase.
   *
   * When an agentResolver is configured, delegates to it. Otherwise, returns
   * the single injected agent (with a runtime guard to avoid non-null assertions).
   *
   * IMPORTANT: Providers returned from the resolver are NOT pooled. Callers must
   * dispose them after use via try/finally.
   */
  private async getAgentForPhase(phase: WorkflowPhase): Promise<IAgentProvider> {
    if (this.agentResolver) {
      const projectId = `${this.config.github.owner}/${this.config.github.repo}`;
      try {
        return await this.agentResolver.getAgentForPhase(phase, {
          projectId,
          engineId: this.engineId,
        });
      } catch (err: unknown) {
        throw new EngineError(
          `Failed to resolve agent for phase "${phase}": ${err instanceof Error ? err.message : String(err)}`,
        );
      }
    }

    if (!this.agent) {
      throw new EngineError('No agent available for phase resolution');
    }
    return this.agent;
  }

  /**
   * Extract engine-level task config overrides from `this.config.agent` (if present).
   * Returns an empty object when no agent config is available.
   */
  private getEngineTaskOverrides(): Partial<AgentTaskConfig> {
    const agentCfg = this.config.agent;
    if (!agentCfg) {
      return {};
    }
    const overrides: Partial<AgentTaskConfig> = {};
    if (agentCfg.model !== undefined) {
      overrides.model = agentCfg.model;
    }
    if (agentCfg.maxBudgetUsd !== undefined) {
      overrides.maxBudgetUsd = agentCfg.maxBudgetUsd;
    }
    if (agentCfg.allowedTools !== undefined) {
      overrides.allowedTools = agentCfg.allowedTools;
    }
    if (agentCfg.permissionMode !== undefined) {
      overrides.permissionMode = agentCfg.permissionMode;
    }
    return overrides;
  }

  /**
   * Use the agent provider to produce a structured {@link DevelopmentPlan} from
   * the issue context. The plan JSON is validated for required fields before
   * being returned.
   *
   * @throws {WorkflowError} If the agent fails, returns invalid JSON, or omits
   *   required fields.
   */
  async generatePlan(
    issue: IssueData,
    context: string,
  ): Promise<DevelopmentPlan> {
    await this.setState(EngineState.PLANNING);

    const planPrompt = `You are analyzing a GitHub issue to create a development plan.

${context}

Generate a structured development plan as JSON with the following fields:
- issueNumber: ${issue.number}
- summary: A brief summary of what needs to be done
- approach: Detailed implementation approach
- fileChanges: Array of { filePath, action ("create"|"modify"|"delete"), description }
- testingStrategy: How to test the changes
- estimatedComplexity: "low", "medium", or "high"
- risks: Array of potential risks or concerns

If requirements are ambiguous, note the ambiguity in the risks array and make reasonable assumptions.
Consider multiple implementation options where appropriate and choose the best one, noting alternatives in the approach.

Return ONLY valid JSON matching the schema.`;

    const agent = await this.getAgentForPhase('PLAN_GENERATION');

    // Build task config: resolver config (allowlisted) + engine-owned fields
    const resolverConfig = this.agentResolver?.getTaskConfig('architect', this.getEngineTaskOverrides()) ?? {};

    // Field allowlisting: only pick allowedTools, maxBudgetUsd, permissionMode from resolver result
    const taskConfig: AgentTaskConfig = {
      // Engine always sets prompt and cwd
      prompt: planPrompt,
      cwd: this.config.engine.workingDirectory,
      // Resolver-provided (allowlisted) fields, falling back to direct config
      ...(resolverConfig.allowedTools !== undefined ? { allowedTools: resolverConfig.allowedTools } : {}),
      ...(resolverConfig.maxBudgetUsd !== undefined ? { maxBudgetUsd: resolverConfig.maxBudgetUsd } : (this.config.agent?.maxBudgetUsd !== undefined ? { maxBudgetUsd: this.config.agent.maxBudgetUsd } : {})),
      ...(resolverConfig.permissionMode !== undefined ? { permissionMode: resolverConfig.permissionMode } : (this.config.agent?.permissionMode !== undefined ? { permissionMode: this.config.agent.permissionMode } : {})),
      // Model from resolver or direct config
      ...(resolverConfig.model !== undefined ? { model: resolverConfig.model } : (this.config.agent?.model !== undefined ? { model: this.config.agent.model } : {})),
      outputFormat: {
        type: 'json_schema',
        schema: {
          type: 'object',
          properties: {
            issueNumber: { type: 'number' },
            summary: { type: 'string' },
            approach: { type: 'string' },
            fileChanges: {
              type: 'array',
              items: {
                type: 'object',
                properties: {
                  filePath: { type: 'string' },
                  action: {
                    type: 'string',
                    enum: ['create', 'modify', 'delete'],
                  },
                  description: { type: 'string' },
                },
                required: ['filePath', 'action', 'description'],
              },
            },
            testingStrategy: { type: 'string' },
            estimatedComplexity: {
              type: 'string',
              enum: ['low', 'medium', 'high'],
            },
            risks: { type: 'array', items: { type: 'string' } },
          },
          required: [
            'issueNumber',
            'summary',
            'approach',
            'fileChanges',
            'testingStrategy',
            'estimatedComplexity',
            'risks',
          ],
        },
      },
    };

    let result: AgentTaskResult;
    try {
      result = await agent.executeTask(
        taskConfig,
        (event) => {
          this.logger.debug('Plan generation progress', {
            type: event.type,
            message: event.message,
          });
        },
      );
    } finally {
      // Providers from resolver are NOT pooled — dispose after each phase
      if (this.agentResolver) {
        await agent.dispose();
      }
    }

    if (!result.success) {
      throw new WorkflowError(
        `Plan generation failed: ${result.error ?? 'Unknown error'}`,
        { retryable: true },
      );
    }

    // Track plan generation cost
    this.totalCostUsd += result.costUsd ?? 0;

    let plan: DevelopmentPlan;
    try {
      const parsed = JSON.parse(result.output);
      if (!parsed.issueNumber || !parsed.summary || !Array.isArray(parsed.fileChanges)) {
        throw new WorkflowError('Invalid plan structure returned from agent', { retryable: true });
      }
      plan = parsed as DevelopmentPlan;
    } catch (err) {
      if (err instanceof WorkflowError) throw err;
      throw new WorkflowError(
        `Failed to parse plan: ${err instanceof Error ? err.message : String(err)}`,
        { retryable: true },
      );
    }

    this.currentPlan = plan;

    await this.recordEvent(EngineEventType.PLAN_GENERATED, plan.issueNumber, { summary: plan.summary, complexity: plan.estimatedComplexity, fileChanges: plan.fileChanges.length });
    this.logger.info('Plan generated', {
      issueNumber: plan.issueNumber,
      summary: plan.summary,
      fileChanges: plan.fileChanges.length,
      complexity: plan.estimatedComplexity,
    });

    return plan;
  }

  /**
   * Gate that pauses execution until a human approves the plan.
   * In `auto` approval mode this is a no-op. In `cli` mode it prints the plan
   * to stdout and waits for `y` via readline.
   *
   * @throws {WorkflowError} If the user rejects the plan (non-retryable).
   */
  async awaitApproval(plan: DevelopmentPlan): Promise<void> {
    await this.setState(EngineState.AWAITING_APPROVAL);

    if (this.config.engine.approvalMode === 'auto') {
      await this.recordEvent(EngineEventType.PLAN_APPROVED, plan.issueNumber, {});
      this.logger.info('Auto-approval mode, skipping approval gate');
      return;
    }

    // Use custom approval handler if provided (e.g. Ink UI)
    if (this.approvalHandler !== undefined) {
      const decision = await this.approvalHandler(plan);
      if (decision === 'reject') {
        await this.recordEvent(EngineEventType.PLAN_REJECTED, plan.issueNumber, {});
        throw new WorkflowError('Plan rejected by user', { retryable: false });
      }
      if (decision === 'skip') {
        await this.recordEvent(EngineEventType.PLAN_REJECTED, plan.issueNumber, { skipped: true });
        throw new WorkflowError('Plan skipped by user', { retryable: false });
      }
      await this.recordEvent(EngineEventType.PLAN_APPROVED, plan.issueNumber, {});
      this.logger.info('Plan approved via handler');
      return;
    }

    // Fallback: CLI readline approval
    const planDisplay = [
      `\n${'='.repeat(60)}`,
      `Development Plan for Issue #${plan.issueNumber}`,
      `${'='.repeat(60)}`,
      `\nSummary: ${plan.summary}`,
      `\nApproach: ${plan.approach}`,
      `\nFile Changes:`,
      ...plan.fileChanges.map(
        (fc) => `  - [${fc.action}] ${fc.filePath}: ${fc.description}`,
      ),
      `\nTesting Strategy: ${plan.testingStrategy}`,
      `Complexity: ${plan.estimatedComplexity}`,
      `Risks: ${plan.risks.length > 0 ? plan.risks.join(', ') : 'None identified'}`,
      `\n${'='.repeat(60)}`,
    ].join('\n');

    this.logger.info(planDisplay);

    const approved = await this.promptUser('Approve this plan? (y/n): ');
    if (approved.toLowerCase() !== 'y') {
      await this.recordEvent(EngineEventType.PLAN_REJECTED, plan.issueNumber, {});
      throw new WorkflowError('Plan rejected by user', { retryable: false });
    }

    await this.recordEvent(EngineEventType.PLAN_APPROVED, plan.issueNumber, {});
    this.logger.info('Plan approved');
  }

  /**
   * Create a feature branch named `feature/{number}-{slug}`. If the branch
   * already exists, appends a numeric suffix and retries up to 10 times.
   * Uses an atomic try-create-catch-retry pattern to avoid TOCTOU races.
   */
  async createBranch(issue: IssueData): Promise<string> {
    const { owner, repo } = this.config.github;
    const slug = slugify(issue.title);
    const repository = await this.platform.getRepository(owner, repo);
    const maxAttempts = 10;

    for (let attempt = 0; attempt < maxAttempts; attempt++) {
      const branchName =
        attempt === 0
          ? `feature/${issue.number}-${slug}`
          : `feature/${issue.number}-${slug}-${attempt}`;

      try {
        await this.platform.createBranch(
          owner,
          repo,
          branchName,
          repository.defaultBranch,
        );

        // Validate branch was actually created
        try {
          await this.platform.getBranch(owner, repo, branchName);
        } catch {
          this.logger.warn('Branch validation failed, branch may not be available yet', { branch: branchName });
        }

        this.currentBranch = branchName;
        await this.recordEvent(EngineEventType.BRANCH_CREATED, issue.number, { branch: branchName });
        this.logger.info('Branch created', {
          branch: branchName,
          issueNumber: issue.number,
        });

        return branchName;
      } catch (err: unknown) {
        const msg = err instanceof Error ? err.message : String(err);
        this.logger.warn('Branch creation failed, retrying with suffix', {
          branch: branchName,
          attempt: attempt + 1,
          error: msg,
        });
      }
    }

    throw new WorkflowError(
      `Failed to create branch after ${maxAttempts} attempts`,
      { retryable: false, context: { issueNumber: issue.number } },
    );
  }

  /**
   * Delegate autonomous code generation to the agent provider. The agent is
   * given the plan, told to implement, test, and push to the feature branch.
   */
  async implementCode(
    issue: IssueData,
    plan: DevelopmentPlan,
    branch: string,
  ): Promise<AgentTaskResult> {
    await this.setState(EngineState.IMPLEMENTING);
    const { owner, repo } = this.config.github;

    const implPrompt = `You are an autonomous coding agent. Implement the following plan for issue #${issue.number}.

## Issue: ${issue.title}
${issue.body}

## Plan
Summary: ${plan.summary}
Approach: ${plan.approach}

## File Changes
${plan.fileChanges.map((fc) => `- [${fc.action}] ${fc.filePath}: ${fc.description}`).join('\n')}

## Testing Strategy
${plan.testingStrategy}

## Instructions
1. Implement all the file changes described in the plan.
2. Write or update tests as described in the testing strategy.
3. Ensure TypeScript compiles without errors (run: npx tsc --noEmit).
4. Ensure all tests pass (run the project's test command).
5. Git add, commit, and push your changes to the branch: ${branch}
   - Use remote: origin
   - Repository: ${owner}/${repo}
   - Commit message should reference issue #${issue.number}

Follow existing project conventions and patterns.`;

    await this.recordEvent(EngineEventType.IMPLEMENTATION_STARTED, issue.number, { branch });

    const agent = await this.getAgentForPhase('CODE_GENERATION');

    // Build task config: resolver config (allowlisted) + engine-owned fields
    const resolverConfig = this.agentResolver?.getTaskConfig('implementer', this.getEngineTaskOverrides()) ?? {};

    // Field allowlisting: only pick allowedTools, maxBudgetUsd, permissionMode from resolver result
    const taskConfig: AgentTaskConfig = {
      // Engine always sets prompt and cwd
      prompt: implPrompt,
      cwd: this.config.engine.workingDirectory,
      // Resolver-provided (allowlisted) fields, falling back to direct config
      ...(resolverConfig.allowedTools !== undefined ? { allowedTools: resolverConfig.allowedTools } : (this.config.agent?.allowedTools !== undefined ? { allowedTools: this.config.agent.allowedTools } : {})),
      ...(resolverConfig.maxBudgetUsd !== undefined ? { maxBudgetUsd: resolverConfig.maxBudgetUsd } : (this.config.agent?.maxBudgetUsd !== undefined ? { maxBudgetUsd: this.config.agent.maxBudgetUsd } : {})),
      ...(resolverConfig.permissionMode !== undefined ? { permissionMode: resolverConfig.permissionMode } : (this.config.agent?.permissionMode !== undefined ? { permissionMode: this.config.agent.permissionMode } : {})),
      // Model from resolver or direct config
      ...(resolverConfig.model !== undefined ? { model: resolverConfig.model } : (this.config.agent?.model !== undefined ? { model: this.config.agent.model } : {})),
    };

    let result: AgentTaskResult;
    try {
      result = await agent.executeTask(
        taskConfig,
        (event) => {
          this.logger.debug('Implementation progress', {
            type: event.type,
            message: event.message,
            costSoFar: event.costSoFar,
          });
        },
      );
    } finally {
      // Providers from resolver are NOT pooled — dispose after each phase
      if (this.agentResolver) {
        await agent.dispose();
      }
    }

    if (result.success) {
      this.totalCostUsd += result.costUsd;
      await this.recordEvent(EngineEventType.IMPLEMENTATION_COMPLETED, issue.number, { costUsd: result.costUsd, durationMs: result.durationMs });
    }

    this.logger.info('Implementation complete', {
      issueNumber: issue.number,
      success: result.success,
      costUsd: result.costUsd,
      durationMs: result.durationMs,
    });

    return result;
  }

  /**
   * Open a pull request from the feature branch to the default branch. The PR
   * body includes the plan summary, changes, testing strategy, and a
   * `Closes #N` footer for automatic issue linking.
   */
  async createPR(
    issue: IssueData,
    plan: DevelopmentPlan,
    branch: string,
  ): Promise<PullRequestInfo> {
    await this.setState(EngineState.CREATING_PR);
    const { owner, repo } = this.config.github;

    const repository = await this.platform.getRepository(owner, repo);

    const prBody = [
      `## Summary`,
      plan.summary,
      '',
      `## Approach`,
      plan.approach,
      '',
      `## Changes`,
      ...plan.fileChanges.map(
        (fc) => `- **${fc.action}** \`${fc.filePath}\`: ${fc.description}`,
      ),
      '',
      `## Testing`,
      plan.testingStrategy,
      '',
      `## Risks`,
      plan.risks.length > 0
        ? plan.risks.map((r) => `- ${r}`).join('\n')
        : 'None identified',
      '',
      `Closes #${issue.number}`,
      '',
      '---',
      '_This PR was automatically generated by Tamma._',
    ].join('\n');

    const pr = await this.platform.createPR(owner, repo, {
      title: `feat: ${issue.title} (#${issue.number})`,
      body: prBody,
      head: branch,
      base: repository.defaultBranch,
      labels: ['tamma-automated'],
    });

    // Validate PR was created
    const createdPR = await this.platform.getPR(owner, repo, pr.number);
    if (createdPR.state !== 'open') {
      this.logger.warn('PR was created but is not in open state', { prNumber: pr.number, state: createdPR.state });
    }

    // Comment on the issue
    await this.platform.addIssueComment(
      owner,
      repo,
      issue.number,
      `🤖 PR created: ${pr.url}`,
    );

    const prInfo: PullRequestInfo = {
      number: pr.number,
      url: pr.url,
      title: pr.title,
      body: pr.body,
      branch,
      status: 'open',
    };

    this.currentPR = prInfo;
    await this.recordEvent(EngineEventType.PR_CREATED, issue.number, { prNumber: pr.number, url: pr.url });
    this.logger.info('PR created', {
      prNumber: pr.number,
      url: pr.url,
      issueNumber: issue.number,
    });

    return prInfo;
  }

  /**
   * Poll CI status until checks pass, then squash-merge the PR, delete the
   * feature branch, and close the issue.
   *
   * The loop runs indefinitely until one of:
   * - CI passes → merge and return
   * - CI fails → throw WorkflowError
   * - PR closed/merged externally → return/break
   * - Timeout exceeded ({@link EngineConfig.ciMonitorTimeoutMs}) → throw
   *
   * Poll interval is configurable via {@link EngineConfig.ciPollIntervalMs}.
   */
  async monitorAndMerge(
    pr: PullRequestInfo,
    issue: IssueData,
  ): Promise<void> {
    await this.setState(EngineState.MONITORING);
    const { owner, repo } = this.config.github;
    const pollInterval = this.config.engine.ciPollIntervalMs;
    const timeout = this.config.engine.ciMonitorTimeoutMs;
    const startTime = Date.now();
    let ciPollCount = 0;

     
    while (true) {
      if (Date.now() - startTime > timeout) {
        throw new WorkflowError(
          `CI monitoring timed out after ${Math.round(timeout / 60000)} minutes`,
          { retryable: false, context: { prNumber: pr.number, timeoutMs: timeout } },
        );
      }

      ciPollCount++;
      if (ciPollCount > 500) {
        throw new WorkflowError('CI monitoring exceeded maximum poll attempts', { retryable: false, context: { prNumber: pr.number, pollCount: ciPollCount } });
      }

      const prData = await this.platform.getPR(owner, repo, pr.number);

      if (prData.state === 'closed') {
        this.logger.warn('PR was closed externally', { prNumber: pr.number });
        return;
      }

      if (prData.state === 'merged') {
        this.logger.info('PR was merged externally', { prNumber: pr.number });
        break;
      }

      // Check CI status
      const ciStatus = await this.platform.getCIStatus(
        owner,
        repo,
        pr.branch,
      );

      this.logger.debug('CI status check', {
        prNumber: pr.number,
        state: ciStatus.state,
        success: ciStatus.successCount,
        failure: ciStatus.failureCount,
        pending: ciStatus.pendingCount,
      });

      if (ciStatus.state === 'failure' || ciStatus.state === 'error') {
        this.logger.error('CI checks failed', {
          prNumber: pr.number,
          failures: ciStatus.failureCount,
        });
        throw new WorkflowError('CI checks failed for PR', {
          retryable: false,
          context: { prNumber: pr.number },
        });
      }

      if (ciStatus.state === 'success') {
        // Merge
        await this.setState(EngineState.MERGING);
        this.logger.info('CI checks passed, merging PR', {
          prNumber: pr.number,
        });

        const mergeResult = await this.platform.mergePR(owner, repo, pr.number, {
          mergeMethod: this.config.engine.mergeStrategy ?? 'squash',
        });

        if (!mergeResult.merged) {
          throw new WorkflowError(
            `Failed to merge PR: ${mergeResult.message}`,
            { retryable: true, context: { prNumber: pr.number } },
          );
        }

        await this.recordEvent(EngineEventType.PR_MERGED, issue.number, { prNumber: pr.number, sha: mergeResult.sha });

        // Cleanup branch (configurable)
        const shouldDeleteBranch = this.config.engine.deleteBranchOnMerge !== false;
        if (shouldDeleteBranch) {
          try {
            await this.platform.deleteBranch(owner, repo, pr.branch);
            await this.recordEvent(EngineEventType.BRANCH_DELETED, issue.number, { branch: pr.branch });
            this.logger.info('Branch deleted', { branch: pr.branch });
          } catch (err: unknown) {
            const msg = err instanceof Error ? err.message : String(err);
            this.logger.warn('Failed to delete branch', {
              branch: pr.branch,
              error: msg,
            });
          }
        } else {
          this.logger.info('Branch deletion skipped (deleteBranchOnMerge=false)', { branch: pr.branch });
        }

        // Close issue with comment
        await this.platform.addIssueComment(
          owner,
          repo,
          issue.number,
          `✅ Resolved via PR #${pr.number}`,
        );
        await this.platform.updateIssue(owner, repo, issue.number, {
          state: 'closed',
        });
        await this.recordEvent(EngineEventType.ISSUE_CLOSED, issue.number, { prNumber: pr.number });

        // Completion checkpoint
        this.issuesProcessed++;
        this.logger.info('Issue completed', {
          issueNumber: issue.number,
          prNumber: pr.number,
          mergeSha: mergeResult.sha,
        });

        return;
      }

      // Wait before next poll
      await sleep(pollInterval);
    }
  }

  getState(): EngineState {
    return this.state;
  }

  getCurrentIssue(): IssueData | null {
    return this.currentIssue;
  }

  getCurrentPlan(): DevelopmentPlan | null {
    return this.currentPlan;
  }

  getCurrentBranch(): string | null {
    return this.currentBranch;
  }

  getCurrentPR(): PullRequestInfo | null {
    return this.currentPR;
  }

  getEventStore(): IEventStore | undefined {
    return this.eventStore;
  }

  getStats(): EngineStats {
    return {
      issuesProcessed: this.issuesProcessed,
      totalCostUsd: this.totalCostUsd,
      startedAt: this.startedAt,
    };
  }

  private async recordEvent(type: EngineEventType, issueNumber?: number, data: Record<string, unknown> = {}): Promise<void> {
    await this.eventStore?.record({
      type,
      tenantId: this.tenantId,
      ...(issueNumber !== undefined ? { issueNumber } : {}),
      data,
    });
  }

  private async setState(state: EngineState): Promise<void> {
    const prev = this.state;
    this.state = state;
    await this.recordEvent(EngineEventType.STATE_TRANSITION, this.currentIssue?.number, { from: prev, to: state });
    this.logger.debug('State transition', { from: prev, to: state });
    this.onStateChange?.(state, this.currentIssue, this.getStats());
  }

  private async resetCurrentWork(): Promise<void> {
    this.currentIssue = null;
    this.currentPlan = null;
    this.currentBranch = null;
    this.currentPR = null;
    await this.setState(EngineState.IDLE);
  }

  private async promptUser(question: string): Promise<string> {
    const rl = readline.createInterface({
      input: process.stdin,
      output: process.stdout,
    });

    try {
      return await new Promise<string>((resolve, reject) => {
        rl.question(question, (answer) => {
          resolve(answer);
        });

        rl.on('error', (err) => reject(err));
        rl.on('close', () => reject(new Error('Input closed')));
      });
    } finally {
      rl.close();
    }
  }
}
