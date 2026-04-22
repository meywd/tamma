/**
 * Common types and interfaces shared across the Tamma platform
 */

import type { ILogger } from '../contracts/index.js';
import type { AgentsConfig } from './agent-config.js';
import type { SecurityConfig } from './security-config.js';

// Re-export knowledge types
export * from './knowledge.js';

// Re-export knowledge base UI types
export * from './knowledge-base/index.js';

// Re-export agent configuration types
export * from './agent-config.js';

// Re-export security configuration types
export * from './security-config.js';

// Re-export tenant types
export * from './tenant.js';

// Re-export layered config types
export type { IProvidersConfig, IProviderDefinition } from './providers-config.js';
export type { IRepoConfig, IRepoRoleConfig } from './repo-config.js';

// --- AI Provider Types ---

/** Supported AI/LLM provider types */
export type AIProviderType = 'anthropic' | 'openai' | 'local';

/** Configuration for an AI provider */
export interface AIProviderConfig {
  type: AIProviderType;
  /** Model identifier (e.g., 'claude-sonnet-4', 'gpt-4o', 'llama3.1:70b') */
  model: string;
  /** API base URL (required for local, optional for cloud providers) */
  baseUrl?: string;
  /** API key (not needed for local providers) */
  apiKey?: string;
  /** Max tokens for context window */
  maxContextTokens?: number;
  /** Max tokens for output */
  maxOutputTokens?: number;
  /** Temperature (0-1) */
  temperature?: number;
}

/** Local AI provider configuration (Ollama, llama.cpp, vLLM, etc.) */
export interface LocalAIProviderConfig extends AIProviderConfig {
  type: 'local';
  /** Base URL for the local API (default: http://localhost:11434 for Ollama) */
  baseUrl: string;
  /** Runtime type for local inference */
  runtime: 'ollama' | 'llamacpp' | 'vllm' | 'lmstudio' | 'custom';
  /** GPU layers to offload (-1 = all) */
  gpuLayers?: number;
  /** Number of parallel requests supported */
  parallelRequests?: number;
}

// --- Merge Method ---

/** Merge strategy used when merging pull requests. */
export type MergeMethod = 'squash' | 'merge' | 'rebase';

// --- GitHub Config Variants ---

/** PAT-based GitHub authentication (self-hosted or personal use). */
export interface GitHubPATConfig {
  authMode: 'pat';
  token: string;
  owner: string;
  repo: string;
  issueLabels: string[];
  excludeLabels: string[];
  botUsername: string;
}

/** GitHub App installation authentication (self-hosted with App). */
export interface GitHubAppConfig {
  authMode: 'app';
  appId: number;
  privateKeyPath: string;
  installationId: number;
  owner: string;
  repo: string;
  issueLabels: string[];
  excludeLabels: string[];
  botUsername: string;
}

/**
 * SaaS mode — owner/repo are populated by the coordinator per-repo
 * from the installations DB, not from the config file.
 */
export interface GitHubSaaSConfig {
  authMode: 'saas';
  appId: number;
  privateKeyPath: string;
  webhookSecret: string;
  owner: string;
  repo: string;
  issueLabels: string[];
  excludeLabels: string[];
  botUsername: string;
}

/**
 * Discriminated union for GitHub authentication modes.
 * - `pat`: Personal access token (legacy, self-hosted)
 * - `app`: GitHub App installation (self-hosted with App)
 * - `saas`: SaaS mode with multi-tenant installations
 */
export type GitHubConfig = GitHubPATConfig | GitHubAppConfig | GitHubSaaSConfig;

/** Config variants that target a specific repo (not SaaS). */
export type GitHubRepoConfig = GitHubPATConfig | GitHubAppConfig;

// --- Configuration ---

export interface TammaConfig {
  mode: 'standalone' | 'orchestrator' | 'worker';
  logLevel: 'debug' | 'info' | 'warn' | 'error';
  github: GitHubConfig;
  agent: AgentConfig;
  engine: EngineConfig;
  /** AI provider configurations (supports multiple) */
  aiProviders?: AIProviderConfig[];
  /** Default AI provider to use */
  defaultProvider?: AIProviderType;
  /** ELSA workflow engine configuration */
  elsa?: ElsaConfig;
  /** API server configuration */
  server?: ServerConfig;
  /** Multi-agent provider chain configuration */
  agents?: AgentsConfig;
  /** Security settings for content sanitization, URL validation, and action gating */
  security?: SecurityConfig;
}

export interface AgentConfig {
  model: string;
  /** AI provider type (default: 'anthropic') */
  provider?: AIProviderType;
  /** Provider-specific config override */
  providerConfig?: AIProviderConfig;
  maxBudgetUsd: number;
  allowedTools: string[];
  permissionMode: 'bypassPermissions' | 'default';
}

export interface EngineConfig {
  /** Delay between issue-selection polls (default: 300 000 ms / 5 min). */
  pollIntervalMs: number;
  /** Absolute path to the repo checkout the agent operates on. */
  workingDirectory: string;
  /** 'cli' blocks for human confirmation; 'auto' skips the gate. */
  approvalMode: 'cli' | 'auto';
  /** Delay between CI status polls inside monitorAndMerge (default: 30 000 ms). */
  ciPollIntervalMs: number;
  /**
   * Safety timeout for the CI monitoring loop. If checks neither pass nor fail
   * within this window, the engine throws a WorkflowError and moves on.
   * Default: 3 600 000 ms (1 hour).
   */
  ciMonitorTimeoutMs: number;
  /** Merge method to use when merging PRs. Default: 'squash'. */
  mergeStrategy?: MergeMethod;
  /** Whether to delete the feature branch after merge. Default: true. */
  deleteBranchOnMerge?: boolean;
}

export interface ElsaConfig {
  enabled: boolean;
  serverUrl: string;
  apiKey: string;
  callbackPort?: number;
}

export interface ServerConfig {
  port: number;
  host: string;
  jwtSecret: string;
  corsOrigins: string[];
  enableAuth: boolean;
}

// --- Engine State Machine (Story 1.5-1) ---

export enum EngineState {
  IDLE = 'IDLE',
  SELECTING_ISSUE = 'SELECTING_ISSUE',
  ANALYZING = 'ANALYZING',
  PLANNING = 'PLANNING',
  AWAITING_APPROVAL = 'AWAITING_APPROVAL',
  IMPLEMENTING = 'IMPLEMENTING',
  CREATING_PR = 'CREATING_PR',
  MONITORING = 'MONITORING',
  MERGING = 'MERGING',
  ERROR = 'ERROR',
}

// --- Issue Types ---

export interface IssueComment {
  id: number;
  author: string;
  body: string;
  createdAt: string;
}

export interface IssueData {
  number: number;
  title: string;
  body: string;
  labels: string[];
  url: string;
  comments: IssueComment[];
  relatedIssueNumbers: number[];
  createdAt: string;
}

// --- Development Plan (Story 2.3) ---

export interface PlannedFileChange {
  filePath: string;
  action: 'create' | 'modify' | 'delete';
  description: string;
}

export interface DevelopmentPlan {
  issueNumber: number;
  summary: string;
  approach: string;
  fileChanges: PlannedFileChange[];
  testingStrategy: string;
  estimatedComplexity: 'low' | 'medium' | 'high';
  risks: string[];
}

// --- Agent Task Result ---

export interface AgentTaskResult {
  success: boolean;
  output: string;
  costUsd: number;
  durationMs: number;
  error?: string;
}

// --- Pull Request Info ---

export interface PullRequestInfo {
  number: number;
  url: string;
  title: string;
  body: string;
  branch: string;
  status: 'open' | 'closed' | 'merged';
}

// --- Launch Context (Story 1.5-1 AC#4) ---

export interface LaunchContext {
  mode: 'cli' | 'service' | 'web' | 'worker';
  config: TammaConfig;
  logger: ILogger;
}

// --- Event Store (for audit trail) ---

export enum EngineEventType {
  ISSUE_SELECTED = 'ISSUE_SELECTED',
  ISSUE_ANALYZED = 'ISSUE_ANALYZED',
  PLAN_GENERATED = 'PLAN_GENERATED',
  PLAN_APPROVED = 'PLAN_APPROVED',
  PLAN_REJECTED = 'PLAN_REJECTED',
  BRANCH_CREATED = 'BRANCH_CREATED',
  IMPLEMENTATION_STARTED = 'IMPLEMENTATION_STARTED',
  IMPLEMENTATION_COMPLETED = 'IMPLEMENTATION_COMPLETED',
  IMPLEMENTATION_FAILED = 'IMPLEMENTATION_FAILED',
  PR_CREATED = 'PR_CREATED',
  CI_CHECK_STARTED = 'CI_CHECK_STARTED',
  CI_CHECK_PASSED = 'CI_CHECK_PASSED',
  CI_CHECK_FAILED = 'CI_CHECK_FAILED',
  PR_MERGED = 'PR_MERGED',
  ISSUE_CLOSED = 'ISSUE_CLOSED',
  BRANCH_DELETED = 'BRANCH_DELETED',
  ERROR_OCCURRED = 'ERROR_OCCURRED',
  STATE_TRANSITION = 'STATE_TRANSITION',
}

export interface EngineEvent {
  id: string;
  type: EngineEventType;
  timestamp: number;
  tenantId: string;
  issueNumber?: number;
  data: Record<string, unknown>;
}

export interface IEventStore {
  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): Promise<EngineEvent>;
  getEvents(tenantId: string, issueNumber?: number): Promise<EngineEvent[]>;
  getLastEvent(tenantId: string, type: EngineEventType): Promise<EngineEvent | undefined>;
  clear(tenantId: string): Promise<void>;
}
