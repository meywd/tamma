import { readFileSync } from 'node:fs';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import type {
  TammaConfig,
  GitHubConfig,
  GitHubPATConfig,
  GitHubAppConfig,
  GitHubSaaSConfig,
  GitHubRepoConfig,
  AgentConfig,
  EngineConfig,
  SecurityConfig,
  IProvidersConfig,
  IRepoConfig,
} from '@tamma/shared';
import { resolveConfig, validateProvidersConfig, validateRepoConfig } from '@tamma/shared';
import type { GitPlatformConfig } from '@tamma/platforms';

// Re-export normalizeAgentsConfig from @tamma/shared for CLI import convenience
export { normalizeAgentsConfig } from '@tamma/shared';

/**
 * Read a secret from a Docker secret file (via `<ENV_VAR>_FILE`) or fall back
 * to the plain environment variable. This allows containers to consume secrets
 * mounted at `/run/secrets/` without baking values into the environment.
 *
 * Example: if `GITHUB_TOKEN_FILE=/run/secrets/github_token` is set, the
 * contents of that file are returned instead of `process.env.GITHUB_TOKEN`.
 */
export function readSecretOrEnv(envVar: string): string | undefined {
  const filePath = process.env[`${envVar}_FILE`];
  if (filePath) {
    try {
      return readFileSync(filePath, 'utf-8').trim();
    } catch {
      return undefined;
    }
  }
  return process.env[envVar];
}

/** Options from CLI flags that override config file and env vars. */
export interface CLIOptions {
  config?: string | undefined;
  dryRun?: boolean | undefined;
  approval?: 'cli' | 'auto' | undefined;
  once?: boolean | undefined;
  verbose?: boolean | undefined;
  interactive?: boolean | undefined;
  debug?: boolean | undefined;
  mode?: 'interactive' | 'service' | undefined;
}

const DEFAULT_CONFIG: TammaConfig = {
  mode: 'standalone',
  logLevel: 'info',
  github: {
    authMode: 'pat' as const,
    token: '',
    owner: '',
    repo: '',
    issueLabels: ['tamma'],
    excludeLabels: ['wontfix'],
    botUsername: 'tamma-bot',
  },
  agent: {
    model: 'claude-sonnet-4-5',
    maxBudgetUsd: 1.0,
    allowedTools: ['Read', 'Write', 'Edit', 'Bash', 'Glob', 'Grep'],
    permissionMode: 'default',
  },
  engine: {
    pollIntervalMs: 300_000,
    workingDirectory: process.cwd(),
    approvalMode: 'cli',
    ciPollIntervalMs: 30_000,
    ciMonitorTimeoutMs: 3_600_000,
  },
};

/**
 * Load configuration from a JSON file if it exists.
 * Returns undefined if the file does not exist.
 */
function loadConfigFile(configPath: string): Partial<TammaConfig> | undefined {
  const resolved = path.resolve(configPath);
  if (!fs.existsSync(resolved)) {
    return undefined;
  }
  const raw = fs.readFileSync(resolved, 'utf-8');
  try {
    return JSON.parse(raw) as Partial<TammaConfig>;
  } catch {
    throw new Error(`Failed to parse config file at ${resolved}. Ensure it contains valid JSON.`);
  }
}

/**
 * Extract configuration from environment variables.
 * Only returns fields that are actually set.
 */
function loadEnvConfig(): Partial<TammaConfig> {
  const env = process.env;
  const config: Partial<TammaConfig> = {};

  // GitHub auth mode
  const authMode = env['TAMMA_GITHUB_AUTH_MODE'] ?? 'pat';

  // Common GitHub fields from env
  const githubOwner = env['TAMMA_GITHUB_OWNER'];
  const githubRepo = env['TAMMA_GITHUB_REPO'];
  const botUsername = env['TAMMA_BOT_USERNAME'];
  const issueLabels = env['TAMMA_ISSUE_LABELS'];
  const excludeLabels = env['TAMMA_EXCLUDE_LABELS'];

  if (authMode === 'app') {
    const appId = env['TAMMA_GITHUB_APP_ID'];
    const privateKeyPath = env['TAMMA_GITHUB_PRIVATE_KEY_PATH'];
    const installationId = env['TAMMA_GITHUB_INSTALLATION_ID'];

    const githubOverrides: Partial<GitHubAppConfig> = { authMode: 'app' };
    if (appId !== undefined) githubOverrides.appId = parseInt(appId, 10);
    if (privateKeyPath !== undefined) githubOverrides.privateKeyPath = privateKeyPath;
    if (installationId !== undefined) githubOverrides.installationId = parseInt(installationId, 10);
    if (githubOwner !== undefined) githubOverrides.owner = githubOwner;
    if (githubRepo !== undefined) githubOverrides.repo = githubRepo;
    if (botUsername !== undefined) githubOverrides.botUsername = botUsername;
    if (issueLabels !== undefined) githubOverrides.issueLabels = issueLabels.split(',').map((s) => s.trim());
    if (excludeLabels !== undefined) githubOverrides.excludeLabels = excludeLabels.split(',').map((s) => s.trim());

    if (Object.keys(githubOverrides).length > 1) {
      config.github = githubOverrides as GitHubAppConfig;
    }
  } else if (authMode === 'saas') {
    const appId = env['TAMMA_GITHUB_APP_ID'];
    const privateKeyPath = env['TAMMA_GITHUB_PRIVATE_KEY_PATH'];
    const webhookSecret = env['TAMMA_GITHUB_WEBHOOK_SECRET'];

    const githubOverrides: Partial<GitHubSaaSConfig> = { authMode: 'saas' };
    if (appId !== undefined) githubOverrides.appId = parseInt(appId, 10);
    if (privateKeyPath !== undefined) githubOverrides.privateKeyPath = privateKeyPath;
    if (webhookSecret !== undefined) githubOverrides.webhookSecret = webhookSecret;
    if (botUsername !== undefined) githubOverrides.botUsername = botUsername;
    if (issueLabels !== undefined) githubOverrides.issueLabels = issueLabels.split(',').map((s) => s.trim());
    if (excludeLabels !== undefined) githubOverrides.excludeLabels = excludeLabels.split(',').map((s) => s.trim());
    // SaaS: owner/repo come from installations DB but can be set for testing
    if (githubOwner !== undefined) githubOverrides.owner = githubOwner;
    if (githubRepo !== undefined) githubOverrides.repo = githubRepo;

    config.github = githubOverrides as GitHubSaaSConfig;
  } else {
    // Default: PAT mode
    const githubToken = readSecretOrEnv('GITHUB_TOKEN') ?? readSecretOrEnv('TAMMA_GITHUB_TOKEN');

    const githubOverrides: Partial<GitHubPATConfig> = { authMode: 'pat' };
    if (githubToken !== undefined) githubOverrides.token = githubToken;
    if (githubOwner !== undefined) githubOverrides.owner = githubOwner;
    if (githubRepo !== undefined) githubOverrides.repo = githubRepo;
    if (botUsername !== undefined) githubOverrides.botUsername = botUsername;
    if (issueLabels !== undefined) githubOverrides.issueLabels = issueLabels.split(',').map((s) => s.trim());
    if (excludeLabels !== undefined) githubOverrides.excludeLabels = excludeLabels.split(',').map((s) => s.trim());

    if (Object.keys(githubOverrides).length > 1) {
      config.github = githubOverrides as GitHubPATConfig;
    }
  }

  // Agent config from env
  const model = env['TAMMA_MODEL'];
  const maxBudget = env['TAMMA_MAX_BUDGET_USD'];
  const permissionMode = env['TAMMA_PERMISSION_MODE'];

  const agentOverrides: Partial<AgentConfig> = {};
  if (model !== undefined) agentOverrides.model = model;
  if (maxBudget !== undefined) {
    const parsed = parseFloat(maxBudget);
    if (!Number.isNaN(parsed)) {
      agentOverrides.maxBudgetUsd = parsed;
    }
  }
  if (permissionMode === 'bypassPermissions' || permissionMode === 'default') {
    agentOverrides.permissionMode = permissionMode;
  }

  if (Object.keys(agentOverrides).length > 0) {
    config.agent = agentOverrides as AgentConfig;
  }

  // Engine config from env
  const pollInterval = env['TAMMA_POLL_INTERVAL_MS'];
  const workDir = env['TAMMA_WORKING_DIRECTORY'];
  const approvalMode = env['TAMMA_APPROVAL_MODE'];

  const engineOverrides: Partial<EngineConfig> = {};
  if (pollInterval !== undefined) {
    const parsed = parseInt(pollInterval, 10);
    if (!Number.isNaN(parsed)) {
      engineOverrides.pollIntervalMs = parsed;
    }
  }
  if (workDir !== undefined) engineOverrides.workingDirectory = workDir;
  if (approvalMode === 'cli' || approvalMode === 'auto') {
    engineOverrides.approvalMode = approvalMode;
  }

  if (Object.keys(engineOverrides).length > 0) {
    config.engine = engineOverrides as EngineConfig;
  }

  // Log level
  const logLevel = env['TAMMA_LOG_LEVEL'];
  if (logLevel === 'debug' || logLevel === 'info' || logLevel === 'warn' || logLevel === 'error') {
    config.logLevel = logLevel;
  }

  // Security config from env
  const sanitize = env['TAMMA_SANITIZE_CONTENT'];
  const validateUrls = env['TAMMA_VALIDATE_URLS'];
  const gateActions = env['TAMMA_GATE_ACTIONS'];
  const maxFetchSize = env['TAMMA_MAX_FETCH_SIZE_BYTES'];

  const securityOverrides: Partial<SecurityConfig> = {};
  if (sanitize === 'true' || sanitize === 'false') {
    securityOverrides.sanitizeContent = sanitize === 'true';
  }
  if (validateUrls === 'true' || validateUrls === 'false') {
    securityOverrides.validateUrls = validateUrls === 'true';
  }
  if (gateActions === 'true' || gateActions === 'false') {
    securityOverrides.gateActions = gateActions === 'true';
  }
  if (maxFetchSize !== undefined) {
    const parsed = parseInt(maxFetchSize, 10);
    if (!Number.isNaN(parsed)) {
      securityOverrides.maxFetchSizeBytes = parsed;
    }
  }
  if (Object.keys(securityOverrides).length > 0) {
    config.security = securityOverrides as SecurityConfig;
  }

  // Agent provider from env (maps to legacy agent.provider field)
  const agentProvider = env['TAMMA_AGENT_PROVIDER'];
  if (agentProvider === 'anthropic' || agentProvider === 'openai' || agentProvider === 'local') {
    if (!config.agent) {
      config.agent = {} as AgentConfig;
    }
    (config.agent as Partial<AgentConfig>).provider = agentProvider;
  }

  return config;
}

/** Default empty providers config when no providers.json is found. */
const EMPTY_PROVIDERS: Readonly<IProvidersConfig> = Object.freeze({ providers: Object.freeze({}) });

/**
 * Load user-level provider settings from ~/.tamma/providers.json.
 * Returns empty config if file doesn't exist or is invalid.
 */
export function loadProvidersConfig(): { config: IProvidersConfig; warnings: string[] } {
  const homePath = path.join(os.homedir(), '.tamma', 'providers.json');
  if (!fs.existsSync(homePath)) {
    return { config: EMPTY_PROVIDERS, warnings: [] };
  }

  let raw: string;
  try {
    raw = fs.readFileSync(homePath, 'utf-8');
  } catch {
    return { config: EMPTY_PROVIDERS, warnings: [`Could not read ${homePath}`] };
  }

  let parsed: IProvidersConfig;
  try {
    parsed = JSON.parse(raw) as IProvidersConfig;
  } catch {
    return { config: EMPTY_PROVIDERS, warnings: [`Failed to parse ${homePath} as JSON`] };
  }

  // If the file doesn't look like a providers config (no providers field), skip it
  if (!parsed.providers || typeof parsed.providers !== 'object') {
    return { config: EMPTY_PROVIDERS, warnings: [] };
  }

  try {
    const warnings = validateProvidersConfig(parsed);
    return { config: parsed, warnings };
  } catch (error) {
    const msg = error instanceof Error ? error.message : 'Unknown validation error';
    return { config: EMPTY_PROVIDERS, warnings: [`Invalid providers config at ${homePath}: ${msg}`] };
  }
}

/**
 * Load repo-level config from <cwd>/.tamma/config.json.
 * Falls back to tamma.config.json with a deprecation warning.
 * Returns empty {} if neither exists.
 */
export function loadRepoConfig(customPath?: string): { config: IRepoConfig; warnings: string[]; legacyFallback: boolean } {
  const warnings: string[] = [];

  // If custom path explicitly provided, caller wants the legacy TammaConfig path
  if (customPath !== undefined) {
    return { config: {}, warnings: [], legacyFallback: true };
  }

  // Try new location: .tamma/config.json
  const newPath = path.resolve('.tamma', 'config.json');
  if (fs.existsSync(newPath)) {
    let raw: string;
    try {
      raw = fs.readFileSync(newPath, 'utf-8');
    } catch {
      // Can't read — skip
      return { config: {}, warnings, legacyFallback: false };
    }
    let parsed: Record<string, unknown>;
    try {
      parsed = JSON.parse(raw) as Record<string, unknown>;
    } catch {
      throw new Error(`Failed to parse repo config at ${newPath}. Ensure it contains valid JSON.`);
    }
    // Detect if this is actually a legacy TammaConfig (has github, agent, mode)
    // rather than the new IRepoConfig format
    if ('github' in parsed || 'agent' in parsed || 'mode' in parsed) {
      // This looks like a full TammaConfig, not an IRepoConfig.
      // Fall through to legacy path.
      warnings.push(
        `.tamma/config.json appears to be a full TammaConfig, not a repo config. ` +
        `Consider migrating to the new layered format.`,
      );
      return { config: {}, warnings, legacyFallback: true };
    }
    const repoConfig = parsed as IRepoConfig;
    validateRepoConfig(repoConfig);
    return { config: repoConfig, warnings, legacyFallback: false };
  }

  // Fallback: tamma.config.json (deprecated)
  const legacyPath = path.resolve('tamma.config.json');
  if (fs.existsSync(legacyPath)) {
    warnings.push(
      `[DEPRECATED] Using tamma.config.json — migrate to .tamma/config.json. ` +
      `Run "tamma init" to generate the new format, or manually split config into ` +
      `~/.tamma/providers.json (credentials) and .tamma/config.json (project settings).`,
    );
    // Legacy file is a full TammaConfig, not IRepoConfig — signal caller to use legacy path
    return { config: {}, warnings, legacyFallback: true };
  }

  return { config: {}, warnings: [], legacyFallback: false };
}

/**
 * Load configuration with layered resolution:
 *   defaults → ~/.tamma/providers.json → .tamma/config.json → env vars → CLI flags
 *
 * Falls back to legacy tamma.config.json loading if no new-format files exist.
 */
export function loadConfig(cliOptions: CLIOptions): TammaConfig {
  // Load layered config sources
  const { config: providersConfig, warnings: providerWarnings } = loadProvidersConfig();
  const { config: repoConfig, warnings: repoWarnings, legacyFallback } = loadRepoConfig(cliOptions.config);
  const envConfig = loadEnvConfig();

  // Log warnings
  for (const w of [...providerWarnings, ...repoWarnings]) {
    console.warn(w);
  }

  // If we have providers.json OR .tamma/config.json, use the new layered path
  const hasNewConfig = Object.keys(providersConfig.providers).length > 0 || Object.keys(repoConfig).length > 0;

  if (hasNewConfig && !legacyFallback) {
    // New layered resolution
    const { config, warnings } = resolveConfig(providersConfig, repoConfig, envConfig);
    for (const w of warnings) {
      console.warn(w);
    }

    // CLI flag overrides (highest priority)
    if (cliOptions.approval !== undefined) {
      config.engine.approvalMode = cliOptions.approval;
    }
    if (cliOptions.verbose === true) {
      config.logLevel = 'debug';
    }

    return config;
  }

  // Legacy path: load from tamma.config.json or custom path
  const configPath = cliOptions.config ?? './tamma.config.json';
  const fileConfig = loadConfigFile(configPath);

  let config = structuredClone(DEFAULT_CONFIG);

  if (fileConfig !== undefined) {
    config = mergeConfig(config, fileConfig);
  }

  config = mergeConfig(config, envConfig);

  if (cliOptions.approval !== undefined) {
    config.engine.approvalMode = cliOptions.approval;
  }
  if (cliOptions.verbose === true) {
    config.logLevel = 'debug';
  }

  return config;
}

function mergeConfig(base: TammaConfig, override: Partial<TammaConfig>): TammaConfig {
  // GitHub config: if override provides a different authMode, take it wholesale.
  // Otherwise shallow-merge within the same authMode shape.
  let mergedGithub: GitHubConfig;
  if (override.github !== undefined) {
    const overrideAuthMode = (override.github as Partial<GitHubConfig>).authMode;
    if (overrideAuthMode !== undefined && overrideAuthMode !== base.github.authMode) {
      // Different auth mode — use override as base, fill missing shared fields from base
      mergedGithub = {
        ...base.github,
        ...override.github,
      } as GitHubConfig;
    } else {
      mergedGithub = { ...base.github, ...override.github } as GitHubConfig;
    }
  } else {
    mergedGithub = base.github;
  }

  const result: TammaConfig = {
    mode: override.mode ?? base.mode,
    logLevel: override.logLevel ?? base.logLevel,
    github: mergedGithub,
    agent: { ...base.agent, ...override.agent },
    engine: { ...base.engine, ...override.engine },
  };

  // Preserve optional top-level fields with shallow merge.
  // Without this, agents/security/etc. from config file or env are silently dropped.
  // Note: We use conditional assignment to respect exactOptionalPropertyTypes.
  // With that flag enabled, we cannot assign `undefined` to optional properties.
  if (base.agents !== undefined || override.agents !== undefined) {
    result.agents = { ...base.agents, ...override.agents } as NonNullable<TammaConfig['agents']>;
  }

  if (base.security !== undefined || override.security !== undefined) {
    result.security = { ...base.security, ...override.security } as NonNullable<TammaConfig['security']>;
  }

  const mergedAiProviders = override.aiProviders ?? base.aiProviders;
  if (mergedAiProviders !== undefined) {
    result.aiProviders = mergedAiProviders;
  }

  const mergedDefaultProvider = override.defaultProvider ?? base.defaultProvider;
  if (mergedDefaultProvider !== undefined) {
    result.defaultProvider = mergedDefaultProvider;
  }

  const mergedElsa = override.elsa ?? base.elsa;
  if (mergedElsa !== undefined) {
    result.elsa = mergedElsa;
  }

  const mergedServer = override.server ?? base.server;
  if (mergedServer !== undefined) {
    result.server = mergedServer;
  }

  return result;
}

/**
 * Validate that required fields are present in the config.
 * Returns an array of validation error messages (empty if valid).
 */
export function validateConfig(config: TammaConfig): string[] {
  const errors: string[] = [];
  const gh = config.github;

  if (gh.authMode === 'pat') {
    if (!gh.token) {
      errors.push('GitHub token is required (set GITHUB_TOKEN or TAMMA_GITHUB_TOKEN)');
    }
    if (!gh.owner) {
      errors.push('GitHub owner is required (set TAMMA_GITHUB_OWNER or use tamma.config.json)');
    }
    if (!gh.repo) {
      errors.push('GitHub repo is required (set TAMMA_GITHUB_REPO or use tamma.config.json)');
    }
  } else if (gh.authMode === 'app') {
    if (!gh.appId) {
      errors.push('GitHub App ID is required (set TAMMA_GITHUB_APP_ID)');
    }
    if (!gh.privateKeyPath) {
      errors.push('GitHub App private key path is required (set TAMMA_GITHUB_PRIVATE_KEY_PATH)');
    }
    if (!gh.installationId) {
      errors.push('GitHub installation ID is required (set TAMMA_GITHUB_INSTALLATION_ID)');
    }
    if (!gh.owner) {
      errors.push('GitHub owner is required (set TAMMA_GITHUB_OWNER)');
    }
    if (!gh.repo) {
      errors.push('GitHub repo is required (set TAMMA_GITHUB_REPO)');
    }
  } else if (gh.authMode === 'saas') {
    if (!gh.appId) {
      errors.push('GitHub App ID is required (set TAMMA_GITHUB_APP_ID)');
    }
    if (!gh.privateKeyPath) {
      errors.push('GitHub App private key path is required (set TAMMA_GITHUB_PRIVATE_KEY_PATH)');
    }
    if (!gh.webhookSecret) {
      errors.push('GitHub webhook secret is required (set TAMMA_GITHUB_WEBHOOK_SECRET)');
    }
  }

  return errors;
}

/**
 * Build a platform-level GitPlatformConfig from the app-level GitHubRepoConfig.
 * Reads the private key file for App mode.
 */
export function buildPlatformConfig(gh: GitHubRepoConfig): GitPlatformConfig {
  if (gh.authMode === 'app') {
    const privateKey = readFileSync(gh.privateKeyPath, 'utf-8');
    return {
      type: 'app',
      appId: gh.appId,
      privateKey,
      installationId: gh.installationId,
    };
  }
  return {
    type: 'pat',
    token: gh.token,
  };
}

/**
 * Generate a tamma.config.json content from answers (legacy format).
 * Token is NOT stored in the config file — it belongs in .env.
 */
export function generateConfigFile(answers: {
  owner: string;
  repo: string;
  labels: string;
  approvalMode: string;
  model?: string;
  maxBudgetUsd?: number;
  workingDirectory?: string;
}): string {
  const config: Partial<TammaConfig> = {
    mode: 'standalone',
    logLevel: 'info',
    github: {
      authMode: 'pat' as const,
      token: '',
      owner: answers.owner,
      repo: answers.repo,
      issueLabels: answers.labels.split(',').map((s) => s.trim()),
      excludeLabels: ['wontfix'],
      botUsername: 'tamma-bot',
    },
    agent: {
      model: answers.model ?? 'claude-sonnet-4-5',
      maxBudgetUsd: answers.maxBudgetUsd ?? 1.0,
      allowedTools: ['Read', 'Write', 'Edit', 'Bash', 'Glob', 'Grep'],
      permissionMode: 'default',
    },
    engine: {
      pollIntervalMs: 300_000,
      workingDirectory: answers.workingDirectory ?? '.',
      approvalMode: answers.approvalMode === 'auto' ? 'auto' : 'cli',
      ciPollIntervalMs: 30_000,
      ciMonitorTimeoutMs: 3_600_000,
    },
  };

  return JSON.stringify(config, null, 2);
}

/**
 * Generate a .tamma/config.json (repo config, new layered format).
 * Contains only project settings — no credentials.
 */
export function generateRepoConfigFile(answers: {
  owner: string;
  repo: string;
  labels: string;
  approvalMode: string;
  model?: string;
  maxBudgetUsd?: number;
  workingDirectory?: string;
  providerName?: string;
}): string {
  const repoConfig: IRepoConfig = {
    engine: {
      approvalMode: answers.approvalMode === 'auto' ? 'auto' : 'cli',
    },
    github: {
      issueLabels: answers.labels.split(',').map((s) => s.trim()),
      excludeLabels: ['wontfix'],
      botUsername: 'tamma-bot',
    },
  };

  // If a role configuration is provided, add a default implementer role
  if (answers.providerName) {
    const roleEntry: { provider: string; model?: string; maxBudgetUsd?: number } = {
      provider: answers.providerName,
    };
    if (answers.model) {
      roleEntry.model = answers.model;
    }
    if (answers.maxBudgetUsd !== undefined) {
      roleEntry.maxBudgetUsd = answers.maxBudgetUsd;
    }
    repoConfig.roles = { implementer: roleEntry };
  }

  return JSON.stringify(repoConfig, null, 2);
}

/**
 * Generate a ~/.tamma/providers.json (user-level provider credentials).
 * Contains API keys and provider-specific settings.
 */
export function generateProvidersFile(providers: {
  name: string;
  apiKey: string;
  defaultModel?: string;
}[]): string {
  const config: IProvidersConfig = { providers: {} };

  for (const p of providers) {
    config.providers[p.name] = {};
    if (p.apiKey) {
      config.providers[p.name]!.apiKey = p.apiKey;
    }
    if (p.defaultModel) {
      config.providers[p.name]!.defaultModel = p.defaultModel;
    }
  }

  return JSON.stringify(config, null, 2);
}

/**
 * Generate a .env file with credentials.
 * Empty values are written as commented placeholders.
 */
export function generateEnvFile(credentials: {
  token: string;
  anthropicKey: string;
}): string {
  const lines: string[] = ['# Tamma credentials — DO NOT COMMIT'];

  if (credentials.token) {
    lines.push(`GITHUB_TOKEN=${credentials.token}`);
  } else {
    lines.push('# GITHUB_TOKEN=ghp_your_token_here');
  }

  if (credentials.anthropicKey) {
    lines.push(`ANTHROPIC_API_KEY=${credentials.anthropicKey}`);
  } else {
    lines.push('# ANTHROPIC_API_KEY=sk-ant-your_key_here');
  }

  lines.push(''); // trailing newline
  return lines.join('\n');
}

/**
 * Merge credentials into an existing .env file.
 * Only updates keys the user actually provided (non-empty).
 * Existing content is preserved for keys not being updated.
 */
export function mergeIntoEnvFile(existingContent: string, credentials: {
  token: string;
  anthropicKey: string;
}): string {
  let content = existingContent;

  if (credentials.token) {
    if (/^#?\s*GITHUB_TOKEN=/m.test(content)) {
      content = content.replace(/^#?\s*GITHUB_TOKEN=.*/m, `GITHUB_TOKEN=${credentials.token}`);
    } else {
      content = content.trimEnd() + `\nGITHUB_TOKEN=${credentials.token}\n`;
    }
  }

  if (credentials.anthropicKey) {
    if (/^#?\s*ANTHROPIC_API_KEY=/m.test(content)) {
      content = content.replace(/^#?\s*ANTHROPIC_API_KEY=.*/m, `ANTHROPIC_API_KEY=${credentials.anthropicKey}`);
    } else {
      content = content.trimEnd() + `\nANTHROPIC_API_KEY=${credentials.anthropicKey}\n`;
    }
  }

  return content;
}

/**
 * Generate a .env.example template with all TAMMA_ environment variables.
 */
export function generateEnvExample(): string {
  return `# Tamma Environment Variables
# Copy this file to .env and fill in the values.

# GitHub auth mode: pat (default), app, or saas
# TAMMA_GITHUB_AUTH_MODE=pat

# GitHub — required for PAT mode
GITHUB_TOKEN=
TAMMA_GITHUB_OWNER=
TAMMA_GITHUB_REPO=

# GitHub — required for App mode
# TAMMA_GITHUB_APP_ID=
# TAMMA_GITHUB_PRIVATE_KEY_PATH=
# TAMMA_GITHUB_INSTALLATION_ID=

# GitHub — required for SaaS mode
# TAMMA_GITHUB_WEBHOOK_SECRET=

# GitHub — optional
TAMMA_BOT_USERNAME=tamma-bot
TAMMA_ISSUE_LABELS=tamma
TAMMA_EXCLUDE_LABELS=wontfix

# Agent
TAMMA_MODEL=claude-sonnet-4-5
TAMMA_MAX_BUDGET_USD=1.00
TAMMA_PERMISSION_MODE=default
# TAMMA_AGENT_PROVIDER=anthropic

# Security
# TAMMA_SANITIZE_CONTENT=true
# TAMMA_VALIDATE_URLS=true
# TAMMA_GATE_ACTIONS=false
# TAMMA_MAX_FETCH_SIZE_BYTES=10485760

# Engine
TAMMA_POLL_INTERVAL_MS=300000
TAMMA_WORKING_DIRECTORY=.
TAMMA_APPROVAL_MODE=cli

# Logging
TAMMA_LOG_LEVEL=info

# Claude CLI agent — required
ANTHROPIC_API_KEY=
`;
}
