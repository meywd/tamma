# Story 1.6 Task 3: Integrate GitLab CI/CD API

## Task Overview

Implement comprehensive GitLab CI/CD API integration to enable pipeline monitoring, triggering, and status tracking capabilities within the Tamma platform. This integration will allow autonomous workflows to monitor build status, trigger deployments, and make decisions based on CI/CD pipeline outcomes.

## Acceptance Criteria

### 3.1 Pipeline Status Monitoring

- [ ] Implement pipeline status retrieval for projects and merge requests
- [ ] Support real-time pipeline status updates via webhooks
- [ ] Implement pipeline job status tracking and log access
- [ ] Support pipeline artifact retrieval and analysis
- [ ] Add pipeline duration and performance metrics collection

### 3.2 Pipeline Triggering and Control

- [ ] Implement pipeline triggering for branches and tags
- [ ] Support manual pipeline triggering with custom variables
- [ ] Implement pipeline cancellation and retry capabilities
- [ ] Support pipeline variable management and injection
- [ ] Add pipeline scheduling and automated triggering

### 3.3 CI/CD Configuration Management

- [ ] Implement GitLab CI/CD configuration file (`.gitlab-ci.yml`) parsing
- [ ] Support CI/CD template discovery and application
- [ ] Implement pipeline configuration validation
- [ ] Support dynamic pipeline configuration generation
- [ ] Add CI/CD environment and deployment tier management

### 3.4 Integration with Autonomous Workflows

- [ ] Implement CI/CD status-based decision making for workflows
- [ ] Support pipeline failure analysis and recovery recommendations
- [ ] Implement automated quality gate integration with pipeline results
- [ ] Support deployment approval workflows based on pipeline status
- [ ] Add pipeline performance impact analysis on development decisions

### 3.5 Error Handling and Resilience

- [ ] Implement robust error handling for CI/CD API failures
- [ ] Support rate limiting and quota management for CI/CD endpoints
- [ ] Implement retry logic with exponential backoff for pipeline operations
- [ ] Add comprehensive logging and monitoring for CI/CD interactions
- [ ] Implement graceful degradation when CI/CD services are unavailable

### 3.6 Testing and Validation

- [ ] Implement comprehensive unit tests for all CI/CD operations
- [ ] Add integration tests with GitLab CI/CD test projects
- [ ] Implement end-to-end tests for pipeline workflows
- [ ] Add performance tests for CI/CD API interactions
- [ ] Implement security tests for CI/CD access and permissions

## Implementation Details

### 3.1 CI/CD API Interface Design

```typescript
// CI/CD related types
interface GitLabPipeline {
  id: number;
  iid: number;
  project_id: number;
  sha: string;
  ref: string;
  status: 'pending' | 'running' | 'success' | 'failed' | 'canceled' | 'skipped';
  source: string;
  created_at: string;
  updated_at: string;
  web_url: string;
  duration?: number;
  queued_duration?: number;
  user: GitLabUser;
  variables: GitLabPipelineVariable[];
  config: GitLabPipelineConfig;
}

interface GitLabJob {
  id: number;
  name: string;
  stage: string;
  status: 'pending' | 'running' | 'success' | 'failed' | 'canceled' | 'skipped';
  created_at: string;
  started_at?: string;
  finished_at?: string;
  duration?: number;
  user?: GitLabUser;
  ref: string;
  commit: GitLabCommit;
  pipeline: GitLabPipeline;
  web_url: string;
  artifacts: GitLabArtifact[];
  retry_count: number;
  failure_reason?: string;
}

interface GitLabPipelineVariable {
  key: string;
  value: string;
  variable_type: 'env_var' | 'file';
  protected: boolean;
  masked: boolean;
  raw: boolean;
}

interface GitLabArtifact {
  name: string;
  path: string;
  size: number;
  file_type: string;
  file_format: string;
}

interface GitLabPipelineConfig {
  stages: string[];
  jobs: Record<string, GitLabJobConfig>;
  variables: GitLabPipelineVariable[];
  cache: GitLabCacheConfig;
  services: GitLabServiceConfig[];
}

interface GitLabJobConfig {
  stage: string;
  script: string[];
  extends?: string[];
  needs?: string[];
  dependencies?: string[];
  artifacts?: GitLabArtifactConfig;
  cache?: GitLabCacheConfig;
  variables?: GitLabPipelineVariable[];
  rules?: GitLabRule[];
  only?: string[];
  except?: string[];
  when?: 'on_success' | 'on_failure' | 'always' | 'manual' | 'delayed';
  allow_failure?: boolean;
  timeout?: string;
  retry?: GitLabRetryConfig;
}

// CI/CD Manager interface
interface IGitLabCICDManager {
  // Pipeline operations
  getPipeline(projectId: number, pipelineId: number): Promise<GitLabPipeline>;
  getPipelines(projectId: number, options?: PipelineListOptions): Promise<GitLabPipeline[]>;
  triggerPipeline(
    projectId: number,
    ref: string,
    variables?: GitLabPipelineVariable[]
  ): Promise<GitLabPipeline>;
  cancelPipeline(projectId: number, pipelineId: number): Promise<void>;
  retryPipeline(projectId: number, pipelineId: number): Promise<GitLabPipeline>;

  // Job operations
  getJob(projectId: number, jobId: number): Promise<GitLabJob>;
  getJobs(projectId: number, pipelineId: number): Promise<GitLabJob[]>;
  getJobLog(projectId: number, jobId: number): Promise<string>;
  downloadJobArtifacts(projectId: number, jobId: number): Promise<Buffer>;
  retryJob(projectId: number, jobId: number): Promise<GitLabJob>;
  cancelJob(projectId: number, jobId: number): Promise<void>;

  // Configuration operations
  getPipelineConfig(projectId: number, sha: string): Promise<GitLabPipelineConfig>;
  validatePipelineConfig(config: GitLabPipelineConfig): Promise<GitLabValidationResult>;
  generatePipelineConfig(template: string, variables: Record<string, string>): Promise<string>;

  // Monitoring and analytics
  getPipelineMetrics(projectId: number, timeRange: TimeRange): Promise<GitLabPipelineMetrics>;
  getJobMetrics(projectId: number, timeRange: TimeRange): Promise<GitLabJobMetrics>;
  subscribeToPipelineEvents(projectId: number, callback: PipelineEventCallback): Promise<void>;
}
```

### 3.2 CI/CD Manager Implementation

```typescript
class GitLabCICDManager implements IGitLabCICDManager {
  private httpClient: HttpClient;
  private instanceUrl: string;
  private eventEmitter: EventEmitter;
  private webhookManager: GitLabWebhookManager;

  constructor(instanceUrl: string, httpClient: HttpClient, webhookManager: GitLabWebhookManager) {
    this.instanceUrl = instanceUrl;
    this.httpClient = httpClient;
    this.eventEmitter = new EventEmitter();
    this.webhookManager = webhookManager;
  }

  // Pipeline Operations
  async getPipeline(projectId: number, pipelineId: number): Promise<GitLabPipeline> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/pipelines/${pipelineId}`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabPipeline;
  }

  async getPipelines(
    projectId: number,
    options: PipelineListOptions = {}
  ): Promise<GitLabPipeline[]> {
    const params = new URLSearchParams();

    if (options.ref) params.append('ref', options.ref);
    if (options.status) params.append('status', options.status);
    if (options.source) params.append('source', options.source);
    if (options.sha) params.append('sha', options.sha);
    if (options.username) params.append('username', options.username);
    if (options.updatedAfter) params.append('updated_after', options.updatedAfter.toISOString());
    if (options.updatedBefore) params.append('updated_before', options.updatedBefore.toISOString());
    if (options.orderBy) params.append('order_by', options.orderBy);
    if (options.sort) params.append('sort', options.sort);

    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/pipelines?${params.toString()}`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabPipeline[];
  }

  async triggerPipeline(
    projectId: number,
    ref: string,
    variables: GitLabPipelineVariable[] = []
  ): Promise<GitLabPipeline> {
    const payload = {
      ref,
      variables: variables.map((v) => ({
        key: v.key,
        value: v.value,
        variable_type: v.variable_type || 'env_var',
      })),
    };

    const response = await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/pipeline`,
      payload,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    const pipeline = response.data as GitLabPipeline;

    // Emit pipeline triggered event
    this.eventEmitter.emit('pipeline.triggered', { projectId, pipeline });

    return pipeline;
  }

  async cancelPipeline(projectId: number, pipelineId: number): Promise<void> {
    await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/pipelines/${pipelineId}/cancel`,
      {},
      {
        headers: await this.getAuthHeaders(),
      }
    );

    this.eventEmitter.emit('pipeline.cancelled', { projectId, pipelineId });
  }

  async retryPipeline(projectId: number, pipelineId: number): Promise<GitLabPipeline> {
    const response = await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/pipelines/${pipelineId}/retry`,
      {},
      {
        headers: await this.getAuthHeaders(),
      }
    );

    const pipeline = response.data as GitLabPipeline;
    this.eventEmitter.emit('pipeline.retried', { projectId, pipeline });

    return pipeline;
  }

  // Job Operations
  async getJob(projectId: number, jobId: number): Promise<GitLabJob> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/jobs/${jobId}`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabJob;
  }

  async getJobs(projectId: number, pipelineId: number): Promise<GitLabJob[]> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/pipelines/${pipelineId}/jobs`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabJob[];
  }

  async getJobLog(projectId: number, jobId: number): Promise<string> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/jobs/${jobId}/trace`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as string;
  }

  async downloadJobArtifacts(projectId: number, jobId: number): Promise<Buffer> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/jobs/${jobId}/artifacts`,
      {
        headers: await this.getAuthHeaders(),
        responseType: 'arraybuffer',
      }
    );

    return Buffer.from(response.data);
  }

  async retryJob(projectId: number, jobId: number): Promise<GitLabJob> {
    const response = await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/jobs/${jobId}/retry`,
      {},
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabJob;
  }

  async cancelJob(projectId: number, jobId: number): Promise<void> {
    await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/jobs/${jobId}/cancel`,
      {},
      {
        headers: await this.getAuthHeaders(),
      }
    );
  }

  // Configuration Operations
  async getPipelineConfig(projectId: number, sha: string): Promise<GitLabPipelineConfig> {
    // Get .gitlab-ci.yml content
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/repository/files/.gitlab-ci.yml/raw`,
      {
        headers: await this.getAuthHeaders(),
        params: { ref: sha },
      }
    );

    const yamlContent = response.data as string;
    return this.parsePipelineConfig(yamlContent);
  }

  async validatePipelineConfig(config: GitLabPipelineConfig): Promise<GitLabValidationResult> {
    const errors: string[] = [];
    const warnings: string[] = [];

    // Validate stages
    if (!config.stages || config.stages.length === 0) {
      errors.push('Pipeline must define at least one stage');
    }

    // Validate jobs
    const jobNames = Object.keys(config.jobs);
    if (jobNames.length === 0) {
      errors.push('Pipeline must define at least one job');
    }

    // Validate job stages
    for (const [jobName, jobConfig] of Object.entries(config.jobs)) {
      if (!config.stages.includes(jobConfig.stage)) {
        errors.push(`Job "${jobName}" references undefined stage "${jobConfig.stage}"`);
      }

      if (!jobConfig.script || jobConfig.script.length === 0) {
        errors.push(`Job "${jobName}" must define script`);
      }

      // Validate dependencies
      if (jobConfig.dependencies) {
        for (const dep of jobConfig.dependencies) {
          if (!jobNames.includes(dep)) {
            errors.push(`Job "${jobName}" depends on undefined job "${dep}"`);
          }
        }
      }
    }

    // Check for circular dependencies
    const circularDeps = this.detectCircularDependencies(config.jobs);
    if (circularDeps.length > 0) {
      errors.push(`Circular dependencies detected: ${circularDeps.join(' -> ')}`);
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
    };
  }

  async generatePipelineConfig(
    template: string,
    variables: Record<string, string>
  ): Promise<string> {
    // Load template
    const templateContent = await this.loadPipelineTemplate(template);

    // Replace variables
    let config = templateContent;
    for (const [key, value] of Object.entries(variables)) {
      const regex = new RegExp(`\\$\\{${key}\\}`, 'g');
      config = config.replace(regex, value);
    }

    return config;
  }

  // Monitoring and Analytics
  async getPipelineMetrics(
    projectId: number,
    timeRange: TimeRange
  ): Promise<GitLabPipelineMetrics> {
    const pipelines = await this.getPipelines(projectId, {
      updatedAfter: timeRange.start,
      updatedBefore: timeRange.end,
    });

    const metrics: GitLabPipelineMetrics = {
      total: pipelines.length,
      successful: pipelines.filter((p) => p.status === 'success').length,
      failed: pipelines.filter((p) => p.status === 'failed').length,
      canceled: pipelines.filter((p) => p.status === 'canceled').length,
      averageDuration: this.calculateAverageDuration(pipelines),
      successRate: this.calculateSuccessRate(pipelines),
      failureRate: this.calculateFailureRate(pipelines),
    };

    return metrics;
  }

  async getJobMetrics(projectId: number, timeRange: TimeRange): Promise<GitLabJobMetrics> {
    // This would require more complex API calls to get job data
    // For now, return basic structure
    return {
      total: 0,
      successful: 0,
      failed: 0,
      averageDuration: 0,
      successRate: 0,
      failureRate: 0,
    };
  }

  async subscribeToPipelineEvents(
    projectId: number,
    callback: PipelineEventCallback
  ): Promise<void> {
    // Set up webhook for pipeline events
    await this.webhookManager.subscribe(projectId, 'pipeline', callback);
  }

  // Private helper methods
  private parsePipelineConfig(yamlContent: string): GitLabPipelineConfig {
    const yaml = require('js-yaml');
    const config = yaml.load(yamlContent) as any;

    return {
      stages: config.stages || [],
      jobs: this.extractJobs(config),
      variables: config.variables || [],
      cache: config.cache || {},
      services: config.services || [],
    };
  }

  private extractJobs(config: any): Record<string, GitLabJobConfig> {
    const jobs: Record<string, GitLabJobConfig> = {};

    for (const [key, value] of Object.entries(config)) {
      if (key.startsWith('.')) continue; // Skip hidden keys
      if (['stages', 'variables', 'cache', 'services', 'include', 'extends'].includes(key))
        continue;

      if (typeof value === 'object' && value !== null) {
        jobs[key] = value as GitLabJobConfig;
      }
    }

    return jobs;
  }

  private detectCircularDependencies(jobs: Record<string, GitLabJobConfig>): string[] {
    const visited = new Set<string>();
    const recursionStack = new Set<string>();
    const path: string[] = [];

    const hasCycle = (jobName: string): boolean => {
      if (recursionStack.has(jobName)) {
        const cycleStart = path.indexOf(jobName);
        return path.slice(cycleStart).concat(jobName);
      }

      if (visited.has(jobName)) return [];

      visited.add(jobName);
      recursionStack.add(jobName);
      path.push(jobName);

      const job = jobs[jobName];
      if (job?.dependencies) {
        for (const dep of job.dependencies) {
          const cycle = hasCycle(dep);
          if (cycle.length > 0) return cycle;
        }
      }

      recursionStack.delete(jobName);
      path.pop();
      return [];
    };

    for (const jobName of Object.keys(jobs)) {
      if (!visited.has(jobName)) {
        const cycle = hasCycle(jobName);
        if (cycle.length > 0) return cycle;
      }
    }

    return [];
  }

  private calculateAverageDuration(pipelines: GitLabPipeline[]): number {
    const durations = pipelines.filter((p) => p.duration !== undefined).map((p) => p.duration!);

    if (durations.length === 0) return 0;

    return durations.reduce((sum, duration) => sum + duration, 0) / durations.length;
  }

  private calculateSuccessRate(pipelines: GitLabPipeline[]): number {
    if (pipelines.length === 0) return 0;

    const successful = pipelines.filter((p) => p.status === 'success').length;
    return (successful / pipelines.length) * 100;
  }

  private calculateFailureRate(pipelines: GitLabPipeline[]): number {
    if (pipelines.length === 0) return 0;

    const failed = pipelines.filter((p) => p.status === 'failed').length;
    return (failed / pipelines.length) * 100;
  }

  private async loadPipelineTemplate(template: string): Promise<string> {
    // Load template from predefined templates or custom location
    const templatePath = path.join(__dirname, 'templates', `${template}.yml`);

    try {
      return await fs.readFile(templatePath, 'utf-8');
    } catch (error) {
      throw new Error(`Template "${template}" not found`);
    }
  }

  private async getAuthHeaders(): Promise<Record<string, string>> {
    // This would be implemented by the authentication manager
    return {
      Authorization: 'Bearer token',
      'User-Agent': 'Tamma/1.0.0',
    };
  }
}
```

### 3.3 CI/CD Webhook Integration

```typescript
class GitLabWebhookManager {
  private httpClient: HttpClient;
  private instanceUrl: string;
  private eventHandlers: Map<string, Set<PipelineEventCallback>>;

  constructor(instanceUrl: string, httpClient: HttpClient) {
    this.instanceUrl = instanceUrl;
    this.httpClient = httpClient;
    this.eventHandlers = new Map();
  }

  async subscribe(
    projectId: number,
    eventType: 'pipeline' | 'job',
    callback: PipelineEventCallback
  ): Promise<void> {
    if (!this.eventHandlers.has(eventType)) {
      this.eventHandlers.set(eventType, new Set());
    }

    this.eventHandlers.get(eventType)!.add(callback);

    // Register webhook with GitLab if not already registered
    await this.ensureWebhookRegistered(projectId, eventType);
  }

  async unsubscribe(
    projectId: number,
    eventType: string,
    callback: PipelineEventCallback
  ): Promise<void> {
    const handlers = this.eventHandlers.get(eventType);
    if (handlers) {
      handlers.delete(callback);

      if (handlers.size === 0) {
        this.eventHandlers.delete(eventType);
        await this.removeWebhook(projectId, eventType);
      }
    }
  }

  handleWebhookEvent(eventType: string, payload: any): void {
    const handlers = this.eventHandlers.get(eventType);
    if (handlers) {
      for (const handler of handlers) {
        try {
          handler(payload);
        } catch (error) {
          console.error(`Error in webhook handler for ${eventType}:`, error);
        }
      }
    }
  }

  private async ensureWebhookRegistered(projectId: number, eventType: string): Promise<void> {
    const webhooks = await this.getProjectWebhooks(projectId);
    const webhookUrl = this.getWebhookUrl(eventType);

    const existingWebhook = webhooks.find((w) => w.url === webhookUrl);

    if (!existingWebhook) {
      await this.createWebhook(projectId, eventType);
    }
  }

  private async getProjectWebhooks(projectId: number): Promise<GitLabWebhook[]> {
    const response = await this.httpClient.get(
      `${this.instanceUrl}/api/v4/projects/${projectId}/hooks`,
      {
        headers: await this.getAuthHeaders(),
      }
    );

    return response.data as GitLabWebhook[];
  }

  private async createWebhook(projectId: number, eventType: string): Promise<void> {
    const webhookUrl = this.getWebhookUrl(eventType);
    const triggerEvents = this.getTriggerEvents(eventType);

    await this.httpClient.post(
      `${this.instanceUrl}/api/v4/projects/${projectId}/hooks`,
      {
        url: webhookUrl,
        push_events: false,
        merge_requests_events: false,
        tag_push_events: false,
        pipeline_events: eventType === 'pipeline',
        job_events: eventType === 'job',
        enable_ssl_verification: true,
      },
      {
        headers: await this.getAuthHeaders(),
      }
    );
  }

  private getWebhookUrl(eventType: string): string {
    // This would be configured based on the deployment
    return `https://tamma.example.com/webhooks/gitlab/${eventType}`;
  }

  private getTriggerEvents(eventType: string): string[] {
    switch (eventType) {
      case 'pipeline':
        return ['pipeline'];
      case 'job':
        return ['job'];
      default:
        return [];
    }
  }

  private async removeWebhook(projectId: number, eventType: string): Promise<void> {
    const webhooks = await this.getProjectWebhooks(projectId);
    const webhookUrl = this.getWebhookUrl(eventType);

    const webhook = webhooks.find((w) => w.url === webhookUrl);
    if (webhook) {
      await this.httpClient.delete(
        `${this.instanceUrl}/api/v4/projects/${projectId}/hooks/${webhook.id}`,
        {
          headers: await this.getAuthHeaders(),
        }
      );
    }
  }

  private async getAuthHeaders(): Promise<Record<string, string>> {
    return {
      Authorization: 'Bearer token',
      'User-Agent': 'Tamma/1.0.0',
    };
  }
}
```

### 3.4 Integration with Autonomous Workflows

```typescript
class GitLabCICDWorkflowIntegration {
  private cicdManager: IGitLabCICDManager;
  private eventStore: IEventStore;
  private logger: Logger;

  constructor(cicdManager: IGitLabCICDManager, eventStore: IEventStore, logger: Logger) {
    this.cicdManager = cicdManager;
    this.eventStore = eventStore;
    this.logger = logger;
  }

  async monitorPipelineForIssue(
    issueId: string,
    projectId: number,
    branchName: string
  ): Promise<void> {
    // Subscribe to pipeline events for this project
    await this.cicdManager.subscribeToPipelineEvents(projectId, async (event) => {
      await this.handlePipelineEvent(issueId, event);
    });

    // Get current pipeline status for the branch
    const pipelines = await this.cicdManager.getPipelines(projectId, {
      ref: branchName,
      orderBy: 'updated_at',
      sort: 'desc',
    });

    if (pipelines.length > 0) {
      await this.emitPipelineStatusEvent(issueId, pipelines[0]);
    }
  }

  async triggerPipelineForIssue(
    issueId: string,
    projectId: number,
    branchName: string,
    variables: GitLabPipelineVariable[] = []
  ): Promise<GitLabPipeline> {
    try {
      const pipeline = await this.cicdManager.triggerPipeline(projectId, branchName, [
        ...variables,
        {
          key: 'TAMMA_ISSUE_ID',
          value: issueId,
          variable_type: 'env_var',
          protected: false,
          masked: false,
          raw: false,
        },
      ]);

      await this.emitEvent('PIPELINE.TRIGGERED.SUCCESS', {
        issueId,
        projectId,
        pipelineId: pipeline.id,
        branchName,
        status: pipeline.status,
      });

      return pipeline;
    } catch (error) {
      await this.emitEvent('PIPELINE.TRIGGERED.FAILED', {
        issueId,
        projectId,
        branchName,
        error: error.message,
      });

      throw error;
    }
  }

  async waitForPipelineCompletion(
    issueId: string,
    projectId: number,
    pipelineId: number,
    timeout: number = 30 * 60 * 1000 // 30 minutes
  ): Promise<GitLabPipeline> {
    const startTime = Date.now();

    while (Date.now() - startTime < timeout) {
      const pipeline = await this.cicdManager.getPipeline(projectId, pipelineId);

      if (['success', 'failed', 'canceled'].includes(pipeline.status)) {
        await this.emitPipelineCompletionEvent(issueId, pipeline);
        return pipeline;
      }

      // Wait 10 seconds before checking again
      await new Promise((resolve) => setTimeout(resolve, 10000));
    }

    throw new Error(`Pipeline ${pipelineId} did not complete within ${timeout}ms`);
  }

  async analyzePipelineFailure(
    issueId: string,
    projectId: number,
    pipelineId: number
  ): Promise<GitLabFailureAnalysis> {
    const pipeline = await this.cicdManager.getPipeline(projectId, pipelineId);
    const jobs = await this.cicdManager.getJobs(projectId, pipelineId);

    const failedJobs = jobs.filter((job) => job.status === 'failed');
    const analysis: GitLabFailureAnalysis = {
      pipeline,
      failedJobs,
      rootCause: await this.determineRootCause(failedJobs),
      recommendations: await this.generateRecommendations(failedJobs),
      canAutoRetry: this.canAutoRetry(failedJobs),
    };

    await this.emitEvent('PIPELINE.FAILURE_ANALYZED', {
      issueId,
      projectId,
      pipelineId,
      analysis,
    });

    return analysis;
  }

  private async handlePipelineEvent(issueId: string, event: any): Promise<void> {
    const eventType = event.object_kind;

    switch (eventType) {
      case 'pipeline':
        await this.handlePipelineEvent(issueId, event.object_attributes);
        break;
      case 'job':
        await this.handleJobEvent(issueId, event.object_attributes);
        break;
    }
  }

  private async handlePipelineEvent(issueId: string, pipeline: any): Promise<void> {
    await this.emitPipelineStatusEvent(issueId, pipeline);

    // If pipeline failed, trigger analysis
    if (pipeline.status === 'failed') {
      await this.analyzePipelineFailure(issueId, pipeline.project_id, pipeline.id);
    }
  }

  private async handleJobEvent(issueId: string, job: any): Promise<void> {
    await this.emitEvent('JOB.STATUS_CHANGED', {
      issueId,
      projectId: job.project_id,
      jobId: job.id,
      jobName: job.name,
      status: job.status,
      stage: job.stage,
    });
  }

  private async emitPipelineStatusEvent(issueId: string, pipeline: GitLabPipeline): Promise<void> {
    await this.emitEvent('PIPELINE.STATUS_CHANGED', {
      issueId,
      projectId: pipeline.project_id,
      pipelineId: pipeline.id,
      status: pipeline.status,
      duration: pipeline.duration,
      webUrl: pipeline.web_url,
    });
  }

  private async emitPipelineCompletionEvent(
    issueId: string,
    pipeline: GitLabPipeline
  ): Promise<void> {
    await this.emitEvent('PIPELINE.COMPLETED', {
      issueId,
      projectId: pipeline.project_id,
      pipelineId: pipeline.id,
      status: pipeline.status,
      duration: pipeline.duration,
      success: pipeline.status === 'success',
    });
  }

  private async determineRootCause(failedJobs: GitLabJob[]): Promise<string> {
    // Analyze job logs and failure patterns
    const failureReasons = failedJobs.map((job) => job.failure_reason).filter(Boolean);

    if (failureReasons.includes('script_failure')) {
      return 'Script execution failed - likely code or configuration issue';
    }

    if (failureReasons.includes('api_failure')) {
      return 'API failure - external service or dependency issue';
    }

    if (failureReasons.includes('stuck_or_timeout_failure')) {
      return 'Job timeout - performance or resource issue';
    }

    return 'Unknown failure - requires manual investigation';
  }

  private async generateRecommendations(failedJobs: GitLabJob[]): Promise<string[]> {
    const recommendations: string[] = [];

    for (const job of failedJobs) {
      if (job.failure_reason === 'script_failure') {
        recommendations.push(`Review script in job "${job.name}" for syntax or logic errors`);
      }

      if (job.failure_reason === 'api_failure') {
        recommendations.push(`Check external service dependencies for job "${job.name}"`);
      }

      if (job.retry_count < 3) {
        recommendations.push(
          `Consider retrying job "${job.name}" (${job.retry_count} retries used)`
        );
      }
    }

    return recommendations;
  }

  private canAutoRetry(failedJobs: GitLabJob[]): boolean {
    return failedJobs.every(
      (job) => job.retry_count < 3 && job.failure_reason !== 'script_failure'
    );
  }

  private async emitEvent(eventType: string, data: any): Promise<void> {
    await this.eventStore.append({
      type: eventType,
      timestamp: new Date().toISOString(),
      tags: {
        issueId: data.issueId,
        projectId: data.projectId?.toString(),
      },
      metadata: {
        workflowVersion: '1.0.0',
        eventSource: 'gitlab-cicd-integration',
      },
      data,
    });
  }
}
```

## Testing Strategy

### 3.1 Unit Tests

```typescript
describe('GitLabCICDManager', () => {
  let cicdManager: GitLabCICDManager;
  let mockHttpClient: jest.Mocked<HttpClient>;
  let mockWebhookManager: jest.Mocked<GitLabWebhookManager>;

  beforeEach(() => {
    mockHttpClient = createMockHttpClient();
    mockWebhookManager = createMockWebhookManager();
    cicdManager = new GitLabCICDManager('https://gitlab.com', mockHttpClient, mockWebhookManager);
  });

  describe('Pipeline Operations', () => {
    it('should get pipeline details', async () => {
      const mockPipeline = createMockPipeline();
      mockHttpClient.get.mockResolvedValue({ data: mockPipeline });

      const result = await cicdManager.getPipeline(123, 456);

      expect(result).toEqual(mockPipeline);
      expect(mockHttpClient.get).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/pipelines/456',
        expect.any(Object)
      );
    });

    it('should trigger pipeline with variables', async () => {
      const mockPipeline = createMockPipeline();
      mockHttpClient.post.mockResolvedValue({ data: mockPipeline });

      const variables = [
        { key: 'TEST_VAR', value: 'test_value', variable_type: 'env_var' as const },
      ];

      const result = await cicdManager.triggerPipeline(123, 'main', variables);

      expect(result).toEqual(mockPipeline);
      expect(mockHttpClient.post).toHaveBeenCalledWith(
        'https://gitlab.com/api/v4/projects/123/pipeline',
        expect.objectContaining({
          ref: 'main',
          variables: expect.arrayContaining([
            expect.objectContaining({ key: 'TEST_VAR', value: 'test_value' }),
          ]),
        }),
        expect.any(Object)
      );
    });
  });

  describe('Configuration Validation', () => {
    it('should validate valid pipeline configuration', async () => {
      const validConfig: GitLabPipelineConfig = {
        stages: ['build', 'test', 'deploy'],
        jobs: {
          build: {
            stage: 'build',
            script: ['npm install', 'npm run build'],
          },
          test: {
            stage: 'test',
            script: ['npm test'],
          },
        },
        variables: [],
        cache: {},
        services: [],
      };

      const result = await cicdManager.validatePipelineConfig(validConfig);

      expect(result.valid).toBe(true);
      expect(result.errors).toHaveLength(0);
    });

    it('should detect circular dependencies', async () => {
      const configWithCircularDeps: GitLabPipelineConfig = {
        stages: ['build', 'test'],
        jobs: {
          job1: {
            stage: 'build',
            script: ['echo "job1"'],
            dependencies: ['job2'],
          },
          job2: {
            stage: 'test',
            script: ['echo "job2"'],
            dependencies: ['job1'],
          },
        },
        variables: [],
        cache: {},
        services: [],
      };

      const result = await cicdManager.validatePipelineConfig(configWithCircularDeps);

      expect(result.valid).toBe(false);
      expect(result.errors).toContain(expect.stringContaining('Circular dependencies detected'));
    });
  });
});
```

### 3.2 Integration Tests

```typescript
describe('GitLab CI/CD Integration', () => {
  let cicdManager: GitLabCICDManager;
  const testProjectId = parseInt(process.env.GITLAB_TEST_PROJECT_ID || '0');

  beforeAll(async () => {
    if (!testProjectId) {
      throw new Error('GITLAB_TEST_PROJECT_ID environment variable required');
    }

    cicdManager = new GitLabCICDManager(
      process.env.GITLAB_TEST_URL || 'https://gitlab.com',
      new HttpClient(),
      new GitLabWebhookManager(
        process.env.GITLAB_TEST_URL || 'https://gitlab.com',
        new HttpClient()
      )
    );
  });

  it('should trigger and monitor pipeline', async () => {
    // Trigger pipeline
    const pipeline = await cicdManager.triggerPipeline(testProjectId, 'main', [
      { key: 'TEST', value: 'integration', variable_type: 'env_var' as const },
    ]);

    expect(pipeline.id).toBeDefined();
    expect(pipeline.status).toMatch(/pending|running/);

    // Wait for pipeline to complete (with timeout)
    const completedPipeline = await cicdManager.waitForPipelineCompletion(
      testProjectId,
      pipeline.id,
      5 * 60 * 1000 // 5 minutes
    );

    expect(['success', 'failed', 'canceled']).toContain(completedPipeline.status);
  });

  it('should retrieve pipeline jobs and logs', async () => {
    const pipelines = await cicdManager.getPipelines(testProjectId, {
      status: 'success',
      orderBy: 'updated_at',
      sort: 'desc',
    });

    if (pipelines.length > 0) {
      const jobs = await cicdManager.getJobs(testProjectId, pipelines[0].id);
      expect(jobs.length).toBeGreaterThan(0);

      // Get logs for first job
      const logs = await cicdManager.getJobLog(testProjectId, jobs[0].id);
      expect(typeof logs).toBe('string');
    }
  });
});
```

## Completion Checklist

- [ ] Implement GitLabCICDManager class with all CI/CD operations
- [ ] Implement GitLabWebhookManager for real-time event handling
- [ ] Implement GitLabCICDWorkflowIntegration for autonomous workflow support
- [ ] Add comprehensive unit tests for all CI/CD functionality
- [ ] Add integration tests with GitLab CI/CD test projects
- [ ] Add end-to-end tests for pipeline workflows
- [ ] Implement error handling and retry logic for CI/CD operations
- [ ] Add performance monitoring and metrics collection
- [ ] Update documentation with CI/CD integration instructions
- [ ] Verify all acceptance criteria are met
- [ ] Ensure code coverage targets are achieved
- [ ] Validate webhook event handling and processing

## Dependencies

- Task 1: GitLabPlatform class implementation (for base integration)
- Task 2: Authentication handling (for API access)
- GitLab test project with CI/CD configuration
- Webhook endpoint configuration for real-time events
- Test credentials with CI/CD permissions

## Estimated Time

**Implementation**: 4-5 days
**Testing**: 3-4 days
**Documentation**: 1 day
**Total**: 8-10 days
