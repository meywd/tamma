# Story 2.3: Development Plan Generation with Approval Checkpoint

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 2.2 (issue context analysis must complete first)

---

## ⚠️ MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

📖 **[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)**

This mandatory guide includes:
- 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling)
- Knowledge Base Usage (.dev/ directory: spikes, bugs, findings, decisions)
- TRACE/DEBUG Logging Requirements for all functions
- Test-Driven Development (TDD) mandatory workflow
- 100% Test Coverage requirement
- Build Success enforcement
- Automatic retry and developer alert procedures

**Failure to follow this process will result in rework.**

---

## User Story

As a **developer**,
I want the system to generate a development plan and wait for my approval,
So that I can review the approach before code is written.

---

## Acceptance Criteria

1. System generates development plan based on issue context and requirements
2. Plan includes implementation approach, file changes, and testing strategy
3. System detects ambiguity in requirements and flags for clarification
4. System provides multiple implementation options when appropriate
5. System waits for human approval before proceeding to implementation
6. Approval can be granted via CLI command, API call, or webhook response
7. Plan and approval status logged to event trail for audit
8. Integration test validates plan generation and approval workflow

---

## Technical Context

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**
1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)


### Core Components

**DevelopmentPlanGenerator Service**:

```typescript
interface IDevelopmentPlanGenerator {
  generatePlan(context: IssueContext): Promise<DevelopmentPlan>;
  detectAmbiguity(context: IssueContext): Promise<AmbiguityReport>;
  generateOptions(context: IssueContext): Promise<ImplementationOption[]>;
  requestApproval(plan: DevelopmentPlan): Promise<ApprovalRequest>;
  waitForApproval(requestId: string): Promise<ApprovalResult>;
}

interface DevelopmentPlan {
  id: string;
  issueId: string;
  issueNumber: number;
  title: string;
  summary: string;
  approach: ImplementationApproach;
  files: FileChange[];
  testing: TestingStrategy;
  risks: Risk[];
  estimatedEffort: EffortEstimate;
  ambiguityReport: AmbiguityReport;
  options: ImplementationOption[];
  selectedOption?: number;
  generatedAt: Date;
  status: 'pending' | 'approved' | 'rejected' | 'modified';
}

interface ImplementationApproach {
  description: string;
  methodology: 'tdd' | 'feature-first' | 'spike' | 'refactor';
  phases: PlanPhase[];
  dependencies: string[];
  considerations: string[];
}

interface PlanPhase {
  name: string;
  description: string;
  estimatedMinutes: number;
  deliverables: string[];
  dependencies: string[];
}

interface FileChange {
  path: string;
  action: 'create' | 'modify' | 'delete';
  description: string;
  estimatedComplexity: 'low' | 'medium' | 'high';
  testsRequired: boolean;
}

interface TestingStrategy {
  approach: 'unit' | 'integration' | 'e2e' | 'performance';
  testFiles: string[];
  coverage: CoverageTarget;
  testTypes: TestType[];
}

interface CoverageTarget {
  unit: number; // percentage
  integration: number;
  overall: number;
}

interface TestType {
  type: 'unit' | 'integration' | 'e2e' | 'performance';
  description: string;
  priority: 'required' | 'recommended' | 'optional';
}

interface Risk {
  description: string;
  probability: 'low' | 'medium' | 'high';
  impact: 'low' | 'medium' | 'high';
  mitigation: string;
}

interface EffortEstimate {
  totalMinutes: number;
  confidence: number; // 0-1
  breakdown: {
    analysis: number;
    implementation: number;
    testing: number;
    review: number;
  };
}

interface AmbiguityReport {
  score: number; // 0-1, higher = more ambiguous
  items: AmbiguityItem[];
  requiresClarification: boolean;
}

interface AmbiguityItem {
  type: 'requirement' | 'technical' | 'scope' | 'acceptance';
  description: string;
  severity: 'low' | 'medium' | 'high';
  suggestedQuestion: string;
  context: string;
}

interface ImplementationOption {
  id: number;
  title: string;
  description: string;
  pros: string[];
  cons: string[];
  estimatedEffort: EffortEstimate;
  riskLevel: 'low' | 'medium' | 'high';
  recommended: boolean;
}

interface ApprovalRequest {
  id: string;
  planId: string;
  issueId: string;
  issueNumber: number;
  plan: DevelopmentPlan;
  requestedAt: Date;
  expiresAt: Date;
  status: 'pending' | 'approved' | 'rejected' | 'expired';
  approvedBy?: string;
  approvedAt?: Date;
  rejectionReason?: string;
  modifications?: PlanModification[];
}

interface ApprovalResult {
  approved: boolean;
  approvedBy: string;
  approvedAt: Date;
  modifications?: PlanModification[];
  comments?: string;
}

interface PlanModification {
  type: 'add_file' | 'remove_file' | 'modify_approach' | 'add_test' | 'remove_risk';
  description: string;
  reason: string;
}
```

### Implementation Strategy

**1. Plan Generation Engine**:

```typescript
class DevelopmentPlanGenerator implements IDevelopmentPlanGenerator {
  constructor(
    private aiProvider: IAIProvider,
    private ambiguityDetector: IAmbiguityDetector,
    private approvalManager: IApprovalManager,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async generatePlan(context: IssueContext): Promise<DevelopmentPlan> {
    const startTime = Date.now();

    try {
      // Detect ambiguity first
      const ambiguityReport = await this.detectAmbiguity(context);

      // Generate implementation options
      const options = await this.generateOptions(context);

      // Generate primary plan
      const plan = await this.generatePrimaryPlan(context, ambiguityReport, options);

      // Assign plan ID and set initial status
      const developmentPlan: DevelopmentPlan = {
        ...plan,
        id: this.generatePlanId(),
        ambiguityReport,
        options,
        generatedAt: new Date(),
        status: 'pending',
      };

      // Log plan generation
      await this.eventStore.append({
        type: 'PLAN.GENERATED.SUCCESS',
        tags: {
          issueId: context.issue.id,
          issueNumber: context.issue.number,
          planId: developmentPlan.id,
          ambiguityScore: ambiguityReport.score.toString(),
        },
        data: {
          fileCount: developmentPlan.files.length,
          estimatedMinutes: developmentPlan.estimatedEffort.totalMinutes,
          optionsCount: options.length,
          requiresClarification: ambiguityReport.requiresClarification,
          generationTime: Date.now() - startTime,
        },
      });

      return developmentPlan;
    } catch (error) {
      await this.eventStore.append({
        type: 'PLAN.GENERATION.FAILED',
        tags: {
          issueId: context.issue.id,
          issueNumber: context.issue.number,
        },
        data: {
          error: error.message,
          generationTime: Date.now() - startTime,
        },
      });

      throw error;
    }
  }

  private async generatePrimaryPlan(
    context: IssueContext,
    ambiguityReport: AmbiguityReport,
    options: ImplementationOption[]
  ): Promise<
    Omit<DevelopmentPlan, 'id' | 'ambiguityReport' | 'options' | 'generatedAt' | 'status'>
  > {
    const prompt = this.buildPlanGenerationPrompt(context, ambiguityReport, options);

    try {
      const response = await this.aiProvider.sendMessage({
        messages: [{ role: 'user', content: prompt }],
        maxTokens: 2000,
        temperature: 0.3,
        responseFormat: { type: 'json_object' },
      });

      const planData = JSON.parse(response.content);

      return this.validateAndNormalizePlan(planData, context);
    } catch (error) {
      this.logger.error('AI plan generation failed, using fallback', { error });
      return this.generateFallbackPlan(context, ambiguityReport, options);
    }
  }

  private buildPlanGenerationPrompt(
    context: IssueContext,
    ambiguityReport: AmbiguityReport,
    options: ImplementationOption[]
  ): string {
    return `
Generate a comprehensive development plan for the following GitHub issue.

Issue Details:
- Title: ${context.issue.title}
- Number: ${context.issue.number}
- Labels: ${context.issue.labels.join(', ')}
- Body: ${context.issue.body}

Context Summary:
${context.summary}

Ambiguity Analysis:
- Score: ${ambiguityReport.score}
- Requires Clarification: ${ambiguityReport.requiresClarification}
- Ambiguities: ${ambiguityReport.items.map((item) => `- ${item.description}`).join('\n')}

Implementation Options:
${options
  .map(
    (option) => `
Option ${option.id}: ${option.title}
${option.description}
Pros: ${option.pros.join(', ')}
Cons: ${option.cons.join(', ')}
Recommended: ${option.recommended}
`
  )
  .join('\n')}

Repository Context:
- Language: ${context.repository.primaryLanguage}
- Structure: ${context.repository.structure.directories.join(', ')}
- Test Directories: ${context.repository.structure.testDirectories.join(', ')}

Generate a JSON development plan with the following structure:
{
  "summary": "Brief overview of the implementation approach",
  "approach": {
    "description": "Detailed implementation methodology",
    "methodology": "tdd|feature-first|spike|refactor",
    "phases": [
      {
        "name": "phase name",
        "description": "phase description",
        "estimatedMinutes": 30,
        "deliverables": ["deliverable1", "deliverable2"],
        "dependencies": ["dependency1"]
      }
    ],
    "dependencies": ["dependency1", "dependency2"],
    "considerations": ["consideration1", "consideration2"]
  },
  "files": [
    {
      "path": "path/to/file.ts",
      "action": "create|modify|delete",
      "description": "what will be changed",
      "estimatedComplexity": "low|medium|high",
      "testsRequired": true
    }
  ],
  "testing": {
    "approach": "unit|integration|e2e|performance",
    "testFiles": ["test/file.test.ts"],
    "coverage": {
      "unit": 80,
      "integration": 60,
      "overall": 75
    },
    "testTypes": [
      {
        "type": "unit",
        "description": "unit test description",
        "priority": "required"
      }
    ]
  },
  "risks": [
    {
      "description": "risk description",
      "probability": "low|medium|high",
      "impact": "low|medium|high",
      "mitigation": "mitigation strategy"
    }
  ],
  "estimatedEffort": {
    "totalMinutes": 120,
    "confidence": 0.8,
    "breakdown": {
      "analysis": 15,
      "implementation": 60,
      "testing": 30,
      "review": 15
    }
  },
  "selectedOption": 1
}

Focus on:
1. Clear, actionable implementation steps
2. Comprehensive testing strategy
3. Realistic effort estimates
4. Risk identification and mitigation
5. Alignment with existing codebase patterns
    `.trim();
  }

  private validateAndNormalizePlan(
    planData: any,
    context: IssueContext
  ): Omit<DevelopmentPlan, 'id' | 'ambiguityReport' | 'options' | 'generatedAt' | 'status'> {
    // Validate required fields
    if (!planData.summary || !planData.approach || !planData.files) {
      throw new Error('Generated plan missing required fields');
    }

    // Normalize and validate file paths
    const normalizedFiles = planData.files.map((file: any) => ({
      path: this.normalizeFilePath(file.path, context),
      action: this.validateFileAction(file.action),
      description: file.description || '',
      estimatedComplexity: this.validateComplexity(file.estimatedComplexity),
      testsRequired: Boolean(file.testsRequired),
    }));

    // Validate effort estimates
    const effort = this.validateEffortEstimate(planData.estimatedEffort);

    return {
      issueId: context.issue.id,
      issueNumber: context.issue.number,
      title: context.issue.title,
      summary: planData.summary,
      approach: this.validateApproach(planData.approach),
      files: normalizedFiles,
      testing: this.validateTestingStrategy(planData.testing),
      risks: this.validateRisks(planData.risks || []),
      estimatedEffort: effort,
      selectedOption: planData.selectedOption,
    };
  }

  async detectAmbiguity(context: IssueContext): Promise<AmbiguityReport> {
    return await this.ambiguityDetector.analyze(context);
  }

  async generateOptions(context: IssueContext): Promise<ImplementationOption[]> {
    const prompt = `
Generate 2-3 different implementation approaches for the following issue:

Issue: ${context.issue.title}
Description: ${context.issue.body}

Context: ${context.summary}

For each option, provide:
- Clear title and description
- Pros and cons
- Estimated effort
- Risk level
- Whether it's recommended

Consider different approaches like:
- Different architectural patterns
- Different libraries or frameworks
- Different implementation strategies
- Different testing approaches

Respond with JSON array of options.
`;

    try {
      const response = await this.aiProvider.sendMessage({
        messages: [{ role: 'user', content: prompt }],
        maxTokens: 1500,
        temperature: 0.4,
        responseFormat: { type: 'json_object' },
      });

      const options = JSON.parse(response.content);
      return Array.isArray(options) ? options : [options];
    } catch (error) {
      this.logger.warn('Failed to generate options, using default', { error });
      return this.getDefaultOptions(context);
    }
  }

  async requestApproval(plan: DevelopmentPlan): Promise<ApprovalRequest> {
    return await this.approvalManager.createRequest(plan);
  }

  async waitForApproval(requestId: string): Promise<ApprovalResult> {
    return await this.approvalManager.waitForApproval(requestId);
  }

  private generatePlanId(): string {
    return `plan_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private normalizeFilePath(path: string, context: IssueContext): string {
    // Remove leading slashes, normalize path separators
    return path.replace(/^\/+/, '').replace(/\\/g, '/');
  }

  private validateFileAction(action: string): 'create' | 'modify' | 'delete' {
    const validActions = ['create', 'modify', 'delete'];
    const normalized = action?.toLowerCase();
    return validActions.includes(normalized) ? (normalized as any) : 'modify';
  }

  private validateComplexity(complexity: string): 'low' | 'medium' | 'high' {
    const validComplexities = ['low', 'medium', 'high'];
    const normalized = complexity?.toLowerCase();
    return validComplexities.includes(normalized) ? (normalized as any) : 'medium';
  }

  private validateEffortEstimate(effort: any): EffortEstimate {
    return {
      totalMinutes: Math.max(1, parseInt(effort?.totalMinutes) || 60),
      confidence: Math.min(1, Math.max(0, parseFloat(effort?.confidence) || 0.5)),
      breakdown: {
        analysis: parseInt(effort?.breakdown?.analysis) || 15,
        implementation: parseInt(effort?.breakdown?.implementation) || 30,
        testing: parseInt(effort?.breakdown?.testing) || 15,
        review: parseInt(effort?.breakdown?.review) || 10,
      },
    };
  }
}
```

**2. Approval Manager**:

```typescript
class ApprovalManager implements IApprovalManager {
  private pendingApprovals = new Map<string, ApprovalRequest>();

  constructor(
    private config: ApprovalConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async createRequest(plan: DevelopmentPlan): Promise<ApprovalRequest> {
    const request: ApprovalRequest = {
      id: this.generateRequestId(),
      planId: plan.id,
      issueId: plan.issueId,
      issueNumber: plan.issueNumber,
      plan,
      requestedAt: new Date(),
      expiresAt: new Date(Date.now() + this.config.expirationMinutes * 60 * 1000),
      status: 'pending',
    };

    this.pendingApprovals.set(request.id, request);

    // Log approval request
    await this.eventStore.append({
      type: 'APPROVAL.REQUESTED',
      tags: {
        issueId: plan.issueId,
        issueNumber: plan.issueNumber.toString(),
        planId: plan.id,
        requestId: request.id,
      },
      data: {
        expiresAt: request.expiresAt.toISOString(),
        estimatedMinutes: plan.estimatedEffort.totalMinutes,
        fileCount: plan.files.length,
        ambiguityScore: plan.ambiguityReport.score,
      },
    });

    // Send notification based on configured channels
    await this.sendApprovalNotification(request);

    return request;
  }

  async waitForApproval(requestId: string): Promise<ApprovalResult> {
    const request = this.pendingApprovals.get(requestId);
    if (!request) {
      throw new Error(`Approval request ${requestId} not found`);
    }

    return new Promise((resolve, reject) => {
      const checkInterval = setInterval(() => {
        const updatedRequest = this.pendingApprovals.get(requestId);

        if (!updatedRequest) {
          clearInterval(checkInterval);
          reject(new Error('Approval request disappeared'));
          return;
        }

        if (updatedRequest.status !== 'pending') {
          clearInterval(checkInterval);

          const result: ApprovalResult = {
            approved: updatedRequest.status === 'approved',
            approvedBy: updatedRequest.approvedBy || 'unknown',
            approvedAt: updatedRequest.approvedAt || new Date(),
            modifications: updatedRequest.modifications,
            comments: updatedRequest.rejectionReason,
          };

          resolve(result);
          return;
        }

        // Check expiration
        if (new Date() > updatedRequest.expiresAt) {
          clearInterval(checkInterval);
          updatedRequest.status = 'expired';

          reject(new Error('Approval request expired'));
          return;
        }
      }, this.config.pollIntervalSeconds * 1000);
    });
  }

  async approveRequest(
    requestId: string,
    approvedBy: string,
    modifications?: PlanModification[]
  ): Promise<void> {
    const request = this.pendingApprovals.get(requestId);
    if (!request) {
      throw new Error(`Approval request ${requestId} not found`);
    }

    request.status = 'approved';
    request.approvedBy = approvedBy;
    request.approvedAt = new Date();
    request.modifications = modifications;

    await this.eventStore.append({
      type: 'APPROVAL.GRANTED',
      tags: {
        issueId: request.issueId,
        issueNumber: request.issueNumber.toString(),
        planId: request.planId,
        requestId: request.id,
        approvedBy,
      },
      data: {
        approvedAt: request.approvedAt.toISOString(),
        modifications: modifications?.length || 0,
      },
    });

    this.logger.info('Plan approved', {
      requestId,
      issueNumber: request.issueNumber,
      approvedBy,
      modifications: modifications?.length || 0,
    });
  }

  async rejectRequest(requestId: string, rejectedBy: string, reason: string): Promise<void> {
    const request = this.pendingApprovals.get(requestId);
    if (!request) {
      throw new Error(`Approval request ${requestId} not found`);
    }

    request.status = 'rejected';
    request.rejectionReason = reason;

    await this.eventStore.append({
      type: 'APPROVAL.REJECTED',
      tags: {
        issueId: request.issueId,
        issueNumber: request.issueNumber.toString(),
        planId: request.planId,
        requestId: request.id,
        rejectedBy,
      },
      data: {
        rejectedAt: new Date().toISOString(),
        reason,
      },
    });

    this.logger.info('Plan rejected', {
      requestId,
      issueNumber: request.issueNumber,
      rejectedBy,
      reason,
    });
  }

  private async sendApprovalNotification(request: ApprovalRequest): Promise<void> {
    const message = this.formatApprovalMessage(request);

    switch (this.config.channel) {
      case 'cli':
        this.sendCLINotification(message);
        break;
      case 'webhook':
        await this.sendWebhookNotification(request, message);
        break;
      case 'slack':
        await this.sendSlackNotification(request, message);
        break;
      case 'email':
        await this.sendEmailNotification(request, message);
        break;
      default:
        this.logger.warn('Unknown approval channel', { channel: this.config.channel });
    }
  }

  private formatApprovalMessage(request: ApprovalRequest): string {
    return `
🚀 Development Plan Approval Required

Issue: #${request.issueNumber} - ${request.plan.title}
Plan ID: ${request.planId}
Estimated Effort: ${request.plan.estimatedEffort.totalMinutes} minutes
Files to Change: ${request.plan.files.length}
Ambiguity Score: ${request.plan.ambiguityReport.score}

Summary:
${request.plan.summary}

Files:
${request.plan.files
  .map((file) => `${file.action.toUpperCase()}: ${file.path} - ${file.description}`)
  .join('\n')}

Testing Strategy:
${request.plan.testing.approach} with ${request.plan.testing.coverage.overall}% target coverage

Risks:
${request.plan.risks
  .map((risk) => `- ${risk.description} (${risk.probability}/${risk.impact})`)
  .join('\n')}

Options:
${request.plan.options
  .map((option) => `${option.id}. ${option.title} ${option.recommended ? '(Recommended)' : ''}`)
  .join('\n')}

Approve with: tamma approve ${request.id}
Reject with: tamma reject ${request.id} "reason"

Expires: ${request.expiresAt.toLocaleString()}
    `.trim();
  }

  private sendCLINotification(message: string): void {
    console.log('\n' + '='.repeat(60));
    console.log(message);
    console.log('='.repeat(60) + '\n');
  }

  private async sendWebhookNotification(request: ApprovalRequest, message: string): Promise<void> {
    if (!this.config.webhookUrl) {
      return;
    }

    try {
      await fetch(this.config.webhookUrl, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          type: 'approval_request',
          requestId: request.id,
          issueNumber: request.issueNumber,
          plan: request.plan,
          message,
          expiresAt: request.expiresAt.toISOString(),
        }),
      });
    } catch (error) {
      this.logger.error('Failed to send webhook notification', { error });
    }
  }

  private generateRequestId(): string {
    return `approval_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }
}
```

### Integration Points

**1. AI Provider Integration**:

- Plan generation using structured prompts
- Ambiguity detection and analysis
- Implementation option generation
- Fallback to rule-based approaches

**2. CLI Integration**:

- Approval commands (`tamma approve`, `tamma reject`)
- Plan display commands (`tamma plan show`)
- Interactive approval workflow

**3. Event Store Integration**:

- `PLAN.GENERATED.SUCCESS/FAILED`
- `APPROVAL.REQUESTED/GRANTED/REJECTED`
- Complete audit trail for compliance

### Testing Strategy

**Unit Tests**:

```typescript
describe('DevelopmentPlanGenerator', () => {
  let generator: DevelopmentPlanGenerator;
  let mockAIProvider: jest.Mocked<IAIProvider>;
  let mockAmbiguityDetector: jest.Mocked<IAmbiguityDetector>;

  beforeEach(() => {
    mockAIProvider = createMockAIProvider();
    mockAmbiguityDetector = createMockAmbiguityDetector();
    generator = new DevelopmentPlanGenerator(
      mockAIProvider,
      mockAmbiguityDetector,
      mockApprovalManager,
      mockLogger,
      mockEventStore
    );
  });

  describe('generatePlan', () => {
    it('should generate comprehensive development plan', async () => {
      const context = createMockIssueContext();

      mockAmbiguityDetector.analyze.mockResolvedValue({
        score: 0.2,
        requiresClarification: false,
        items: [],
      });

      mockAIProvider.sendMessage.mockResolvedValue({
        content: JSON.stringify(createMockPlanData()),
        usage: { tokens: 1000 },
      });

      const plan = await generator.generatePlan(context);

      expect(plan.issueId).toBe(context.issue.id);
      expect(plan.files).toHaveLength(3);
      expect(plan.testing.coverage.overall).toBe(75);
      expect(plan.estimatedEffort.totalMinutes).toBeGreaterThan(0);
    });

    it('should handle AI generation failure gracefully', async () => {
      const context = createMockIssueContext();

      mockAmbiguityDetector.analyze.mockResolvedValue({
        score: 0.1,
        requiresClarification: false,
        items: [],
      });

      mockAIProvider.sendMessage.mockRejectedValue(new Error('AI unavailable'));

      const plan = await generator.generatePlan(context);

      expect(plan.files).toBeDefined();
      expect(plan.summary).toContain('fallback');
    });
  });

  describe('generateOptions', () => {
    it('should generate multiple implementation options', async () => {
      const context = createMockIssueContext();

      mockAIProvider.sendMessage.mockResolvedValue({
        content: JSON.stringify([
          {
            id: 1,
            title: 'Option 1',
            description: 'First approach',
            pros: ['Simple', 'Fast'],
            cons: ['Limited'],
            recommended: true,
          },
          {
            id: 2,
            title: 'Option 2',
            description: 'Second approach',
            pros: ['Flexible', 'Scalable'],
            cons: ['Complex'],
            recommended: false,
          },
        ]),
      });

      const options = await generator.generateOptions(context);

      expect(options).toHaveLength(2);
      expect(options[0].recommended).toBe(true);
      expect(options[1].pros).toContain('Flexible');
    });
  });
});
```

### Configuration Examples

**Development Plan Configuration**:

```yaml
development_planning:
  ai_generation:
    model: 'claude-3-sonnet'
    max_tokens: 2000
    temperature: 0.3
    fallback_enabled: true

  ambiguity_detection:
    enabled: true
    threshold: 0.3
    auto_clarify: false

  options_generation:
    enabled: true
    max_options: 3
    include_risk_analysis: true

  approval:
    channel: 'cli' # cli, webhook, slack, email
    expiration_minutes: 60
    poll_interval_seconds: 10
    webhook_url: '${APPROVAL_WEBHOOK_URL}'

  effort_estimation:
    include_confidence: true
    historical_data_weight: 0.3
    complexity_multiplier:
      low: 1.0
      medium: 1.5
      high: 2.0
```

---

## Implementation Notes

**Key Considerations**:

1. **Human-in-the-Loop**: Approval checkpoint is critical for maintaining control over autonomous development.

2. **Ambiguity Handling**: Early detection of unclear requirements prevents wasted implementation effort.

3. **Option Generation**: Multiple approaches give developers choice and improve plan quality.

4. **Effort Estimation**: Realistic time estimates help with planning and expectation management.

5. **Risk Assessment**: Identifying potential issues early enables proactive mitigation.

6. **Audit Trail**: Complete logging of plan generation and approval decisions for compliance.

**Performance Targets**:

- Plan generation: < 30 seconds
- Ambiguity detection: < 10 seconds
- Option generation: < 20 seconds
- Approval response: < 5 minutes (human factor)

**Security Considerations**:

- Validate approval requests to prevent unauthorized access
- Sanitize plan content to prevent injection attacks
- Use secure channels for approval notifications
- Implement proper authentication for approval endpoints

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
