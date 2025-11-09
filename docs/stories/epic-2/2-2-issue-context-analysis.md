# Story 2.2: Issue Context Analysis

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 2.1 (issue selection must complete first)

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
I want the system to analyze selected issue content and related context,
So that code generation has complete understanding of requirements.

---

## Acceptance Criteria

1. System reads issue title, body, labels, and comments
2. System identifies related issues via issue references (#123 format)
3. System loads recent commit history (last 10 commits) for project context
4. System loads relevant file paths mentioned in issue body
5. System constructs context summary (500-1000 words) for AI provider
6. Context summary logged to event trail for transparency
7. Unit test validates context extraction from mock issue data

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

**ContextAnalyzer Service**:

```typescript
interface IContextAnalyzer {
  analyzeIssue(issue: SelectedIssue): Promise<IssueContext>;
  extractRelatedIssues(issueBody: string): Promise<RelatedIssue[]>;
  getCommitHistory(repository: RepositoryConfig, limit?: number): Promise<Commit[]>;
  extractFilePaths(content: string): Promise<string[]>;
  generateContextSummary(context: IssueContext): Promise<string>;
}

interface IssueContext {
  issue: {
    id: string;
    number: number;
    title: string;
    body: string;
    labels: string[];
    comments: Comment[];
    author: string;
    assignee: string;
    createdAt: Date;
    updatedAt: Date;
  };
  relatedIssues: RelatedIssue[];
  commitHistory: Commit[];
  mentionedFiles: string[];
  repository: RepositoryContext;
  summary: string;
}

interface RelatedIssue {
  id: string;
  number: number;
  title: string;
  state: 'open' | 'closed';
  labels: string[];
  url: string;
  relevanceScore: number;
}

interface Commit {
  sha: string;
  message: string;
  author: string;
  date: Date;
  files: string[];
  url: string;
}

interface RepositoryContext {
  name: string;
  description: string;
  primaryLanguage: string;
  structure: {
    directories: string[];
    mainFiles: string[];
    testDirectories: string[];
  };
  recentActivity: {
    commitsLastWeek: number;
    issuesLastWeek: number;
    prsLastWeek: number;
  };
}
```

### Implementation Strategy

**1. Issue Content Extraction**:

```typescript
class ContextAnalyzer implements IContextAnalyzer {
  constructor(
    private gitPlatform: IGitPlatform,
    private fileSystem: IFileSystem,
    private aiProvider: IAIProvider,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async analyzeIssue(issue: SelectedIssue): Promise<IssueContext> {
    const startTime = Date.now();

    try {
      // Extract basic issue information
      const issueDetails = await this.gitPlatform.getIssue(issue.id);
      const comments = await this.gitPlatform.getIssueComments(issue.id);

      // Extract related issues from body and comments
      const relatedIssues = await this.extractRelatedIssues(issueDetails.body);

      // Get recent commit history
      const commitHistory = await this.getCommitHistory(
        issue.repository,
        10 // Last 10 commits
      );

      // Extract file paths mentioned in issue
      const mentionedFiles = await this.extractFilePaths(issueDetails.body);

      // Get repository context
      const repository = await this.getRepositoryContext(issue.repository);

      // Generate AI-powered context summary
      const context: IssueContext = {
        issue: {
          id: issueDetails.id,
          number: issueDetails.number,
          title: issueDetails.title,
          body: issueDetails.body,
          labels: issueDetails.labels,
          comments,
          author: issueDetails.author,
          assignee: issueDetails.assignee,
          createdAt: issueDetails.createdAt,
          updatedAt: issueDetails.updatedAt,
        },
        relatedIssues,
        commitHistory,
        mentionedFiles,
        repository,
        summary: '', // Will be filled by AI
      };

      context.summary = await this.generateContextSummary(context);

      // Log context analysis completion
      await this.eventStore.append({
        type: 'CONTEXT.ANALYSIS.SUCCESS',
        tags: {
          issueId: issue.id,
          issueNumber: issue.number,
          repository: `${issue.repository.owner}/${issue.repository.name}`,
        },
        data: {
          relatedIssuesCount: relatedIssues.length,
          commitHistoryCount: commitHistory.length,
          mentionedFilesCount: mentionedFiles.length,
          summaryLength: context.summary.length,
          analysisTime: Date.now() - startTime,
        },
      });

      return context;
    } catch (error) {
      await this.eventStore.append({
        type: 'CONTEXT.ANALYSIS.FAILED',
        tags: {
          issueId: issue.id,
          issueNumber: issue.number,
        },
        data: {
          error: error.message,
          analysisTime: Date.now() - startTime,
        },
      });

      throw error;
    }
  }

  async extractRelatedIssues(issueBody: string): Promise<RelatedIssue[]> {
    // Find issue references in format #123, #456, etc.
    const issueRefs = issueBody.match(/#\d+/g) || [];
    const uniqueRefs = [...new Set(issueRefs)];

    const relatedIssues: RelatedIssue[] = [];

    for (const ref of uniqueRefs) {
      const issueNumber = parseInt(ref.substring(1));

      try {
        const issue = await this.gitPlatform.getIssueByNumber(issueNumber);

        // Calculate relevance score based on labels and content similarity
        const relevanceScore = this.calculateRelevanceScore(issueBody, issue);

        relatedIssues.push({
          id: issue.id,
          number: issue.number,
          title: issue.title,
          state: issue.state,
          labels: issue.labels,
          url: issue.url,
          relevanceScore,
        });
      } catch (error) {
        this.logger.warn(`Failed to fetch related issue ${ref}`, { error });
      }
    }

    // Sort by relevance score and return top 10
    return relatedIssues.sort((a, b) => b.relevanceScore - a.relevanceScore).slice(0, 10);
  }

  private calculateRelevanceScore(sourceBody: string, relatedIssue: any): number {
    let score = 0;

    // Label overlap scoring
    const sourceLabels = new Set(this.extractLabels(sourceBody));
    const relatedLabels = new Set(relatedIssue.labels);
    const labelOverlap = [...sourceLabels].filter((label) => relatedLabels.has(label));
    score += labelOverlap.length * 0.3;

    // Title similarity (simple keyword matching)
    const sourceKeywords = this.extractKeywords(sourceBody);
    const titleKeywords = this.extractKeywords(relatedIssue.title);
    const keywordOverlap = sourceKeywords.filter((keyword) => titleKeywords.includes(keyword));
    score += keywordOverlap.length * 0.2;

    // State preference (open issues more relevant)
    if (relatedIssue.state === 'open') {
      score += 0.1;
    }

    return Math.min(score, 1.0); // Cap at 1.0
  }

  async getCommitHistory(repository: RepositoryConfig, limit = 10): Promise<Commit[]> {
    try {
      const commits = await this.gitPlatform.getCommits(repository, {
        limit,
        includeFiles: true,
      });

      return commits.map((commit) => ({
        sha: commit.sha,
        message: commit.message,
        author: commit.author.name,
        date: commit.author.date,
        files: commit.files.map((f) => f.filename),
        url: commit.url,
      }));
    } catch (error) {
      this.logger.warn('Failed to fetch commit history', { repository, error });
      return [];
    }
  }

  async extractFilePaths(content: string): Promise<string[]> {
    // Extract file paths from various formats:
    // - `src/file.ts`
    // - "src/file.ts"
    // - ./src/file.ts
    // - /src/file.ts
    const filePatterns = [
      /`([^`]+\.(ts|js|tsx|jsx|py|java|cpp|c|h|go|rs|rb|php|cs|swift|kt))`/g,
      /"([^"]+\.(ts|js|tsx|jsx|py|java|cpp|c|h|go|rs|rb|php|cs|swift|kt))"/g,
      /'([^']+\.(ts|js|tsx|jsx|py|java|cpp|c|h|go|rs|rb|php|cs|swift|kt))'/g,
      /(?:\.?\/)?([a-zA-Z0-9_\-\/]+\.(ts|js|tsx|jsx|py|java|cpp|c|h|go|rs|rb|php|cs|swift|kt))/g,
    ];

    const filePaths = new Set<string>();

    for (const pattern of filePatterns) {
      let match;
      while ((match = pattern.exec(content)) !== null) {
        const filePath = match[1];
        // Normalize path
        const normalizedPath = filePath.replace(/^\.?\//, '');
        filePaths.add(normalizedPath);
      }
    }

    return Array.from(filePaths);
  }

  async generateContextSummary(context: IssueContext): Promise<string> {
    const prompt = `
Analyze the following GitHub issue and generate a comprehensive context summary (500-1000 words) for an AI developer.

Issue Details:
- Title: ${context.issue.title}
- Labels: ${context.issue.labels.join(', ')}
- State: ${context.issue.state}

Issue Body:
${context.issue.body}

Related Issues:
${context.relatedIssues
  .map((issue) => `- #${issue.number}: ${issue.title} (${issue.state})`)
  .join('\n')}

Recent Commits (last 10):
${context.commitHistory
  .map((commit) => `- ${commit.sha.substring(0, 7)}: ${commit.message.split('\n')[0]}`)
  .join('\n')}

Mentioned Files:
${context.mentionedFiles.join('\n')}

Repository Context:
- Language: ${context.repository.primaryLanguage}
- Description: ${context.repository.description}
- Recent Activity: ${context.repository.recentActivity.commitsLastWeek} commits/week

Generate a summary that includes:
1. Problem statement and requirements
2. Technical context and constraints
3. Related work and dependencies
4. Implementation considerations
5. Testing requirements
6. Potential risks or challenges

Focus on providing actionable context for code generation.
`;

    try {
      const response = await this.aiProvider.sendMessage({
        messages: [{ role: 'user', content: prompt }],
        maxTokens: 1500,
        temperature: 0.3,
      });

      return response.content;
    } catch (error) {
      this.logger.error('Failed to generate AI context summary', { error });

      // Fallback to basic summary
      return this.generateBasicSummary(context);
    }
  }

  private generateBasicSummary(context: IssueContext): string {
    return `
Issue: ${context.issue.title}

Description: ${context.issue.body.substring(0, 500)}...

Labels: ${context.issue.labels.join(', ')}

Related Issues: ${context.relatedIssues.length} issues referenced
Recent Commits: ${context.commitHistory.length} commits in history
Mentioned Files: ${context.mentionedFiles.join(', ')}

This issue requires implementation in ${context.repository.primaryLanguage} 
following the project's existing patterns and conventions.
    `.trim();
  }
}
```

**2. Repository Context Builder**:

```typescript
class RepositoryContextBuilder {
  constructor(
    private gitPlatform: IGitPlatform,
    private fileSystem: IFileSystem
  ) {}

  async buildContext(repository: RepositoryConfig): Promise<RepositoryContext> {
    const repo = await this.gitPlatform.getRepository(repository);
    const recentCommits = await this.gitPlatform.getCommits(repository, { limit: 100 });
    const recentIssues = await this.gitPlatform.getIssues(repository, {
      state: 'all',
      limit: 50,
      since: new Date(Date.now() - 7 * 24 * 60 * 60 * 1000), // Last week
    });
    const recentPRs = await this.gitPlatform.getPullRequests(repository, {
      state: 'all',
      limit: 50,
      since: new Date(Date.now() - 7 * 24 * 60 * 60 * 1000),
    });

    // Analyze repository structure
    const structure = await this.analyzeRepositoryStructure(repository);

    return {
      name: repo.name,
      description: repo.description || '',
      primaryLanguage: repo.primaryLanguage || 'unknown',
      structure,
      recentActivity: {
        commitsLastWeek: recentCommits.length,
        issuesLastWeek: recentIssues.filter(
          (i) => i.createdAt > new Date(Date.now() - 7 * 24 * 60 * 60 * 1000)
        ).length,
        prsLastWeek: recentPRs.filter(
          (pr) => pr.createdAt > new Date(Date.now() - 7 * 24 * 60 * 60 * 1000)
        ).length,
      },
    };
  }

  private async analyzeRepositoryStructure(repository: RepositoryConfig): Promise<{
    directories: string[];
    mainFiles: string[];
    testDirectories: string[];
  }> {
    try {
      const tree = await this.gitPlatform.getRepositoryTree(repository);

      const directories = tree
        .filter((item) => item.type === 'tree')
        .map((item) => item.path)
        .filter((path) => !path.startsWith('.') && !path.includes('node_modules'));

      const mainFiles = tree
        .filter((item) => item.type === 'blob')
        .map((item) => item.path)
        .filter((path) => this.isMainFile(path));

      const testDirectories = directories.filter((path) => this.isTestDirectory(path));

      return { directories, mainFiles, testDirectories };
    } catch (error) {
      this.logger.warn('Failed to analyze repository structure', { error });
      return {
        directories: [],
        mainFiles: [],
        testDirectories: [],
      };
    }
  }

  private isMainFile(filePath: string): boolean {
    const mainPatterns = [
      /src\/index\.(ts|js|tsx|jsx)$/,
      /src\/main\.(ts|js|tsx|jsx)$/,
      /src\/app\.(ts|js|tsx|jsx)$/,
      /lib\/[^\/]+\.(ts|js)$/,
      /package\.json$/,
      /README\.md$/,
      /Dockerfile$/,
      /docker-compose\.yml$/,
    ];

    return mainPatterns.some((pattern) => pattern.test(filePath));
  }

  private isTestDirectory(dirPath: string): boolean {
    const testPatterns = [/test/i, /tests/i, /__tests__/i, /spec/i];

    return testPatterns.some((pattern) => pattern.test(dirPath));
  }
}
```

### Error Handling

**Context Analysis Errors**:

```typescript
class ContextAnalysisError extends Error {
  constructor(
    message: string,
    public readonly issueId: string,
    public readonly phase: 'extraction' | 'analysis' | 'summary',
    public readonly cause?: Error
  ) {
    super(message);
    this.name = 'ContextAnalysisError';
  }
}

class ContextAnalyzer {
  async analyzeIssueWithFallback(issue: SelectedIssue): Promise<IssueContext> {
    try {
      return await this.analyzeIssue(issue);
    } catch (error) {
      this.logger.error('Context analysis failed, using fallback', {
        issueId: issue.id,
        error,
      });

      // Fallback to minimal context
      return this.createMinimalContext(issue);
    }
  }

  private createMinimalContext(issue: SelectedIssue): IssueContext {
    return {
      issue: {
        id: issue.id,
        number: issue.number,
        title: issue.title,
        body: issue.body || '',
        labels: issue.labels,
        comments: [],
        author: 'unknown',
        assignee: 'unknown',
        createdAt: new Date(),
        updatedAt: new Date(),
      },
      relatedIssues: [],
      commitHistory: [],
      mentionedFiles: [],
      repository: {
        name: issue.repository.name,
        description: '',
        primaryLanguage: 'unknown',
        structure: { directories: [], mainFiles: [], testDirectories: [] },
        recentActivity: { commitsLastWeek: 0, issuesLastWeek: 0, prsLastWeek: 0 },
      },
      summary: `Issue: ${issue.title}\n\n${issue.body || 'No description provided.'}`,
    };
  }
}
```

### Integration Points

**1. Git Platform Integration**:

- `getIssue()` - Full issue details with comments
- `getIssueComments()` - All comments on the issue
- `getIssueByNumber()` - Related issues by number
- `getCommits()` - Recent commit history
- `getRepository()` - Repository metadata
- `getRepositoryTree()` - File structure analysis

**2. AI Provider Integration**:

- Used for intelligent context summarization
- Fallback to basic summarization if AI fails
- Configurable model and parameters

**3. Event Store Integration**:

- `CONTEXT.ANALYSIS.SUCCESS` - Successful analysis
- `CONTEXT.ANALYSIS.FAILED` - Analysis failures
- Includes timing and metadata for observability

### Testing Strategy

**Unit Tests**:

```typescript
describe('ContextAnalyzer', () => {
  let analyzer: ContextAnalyzer;
  let mockGitPlatform: jest.Mocked<IGitPlatform>;
  let mockAIProvider: jest.Mocked<IAIProvider>;

  beforeEach(() => {
    mockGitPlatform = createMockGitPlatform();
    mockAIProvider = createMockAIProvider();
    analyzer = new ContextAnalyzer(
      mockGitPlatform,
      mockFileSystem,
      mockAIProvider,
      mockLogger,
      mockEventStore
    );
  });

  describe('extractRelatedIssues', () => {
    it('should extract issue references from body', async () => {
      const issueBody = 'This relates to #123 and #456 but not #789';

      mockGitPlatform.getIssueByNumber
        .mockResolvedValueOnce(createMockIssue({ number: 123, title: 'Bug fix' }))
        .mockResolvedValueOnce(createMockIssue({ number: 456, title: 'Feature' }));

      const result = await analyzer.extractRelatedIssues(issueBody);

      expect(result).toHaveLength(2);
      expect(result[0].number).toBe(123);
      expect(result[1].number).toBe(456);
    });

    it('should handle missing related issues gracefully', async () => {
      const issueBody = 'This relates to #999';

      mockGitPlatform.getIssueByNumber.mockRejectedValue(new Error('Not found'));

      const result = await analyzer.extractRelatedIssues(issueBody);

      expect(result).toHaveLength(0);
    });
  });

  describe('extractFilePaths', () => {
    it('should extract file paths from various formats', async () => {
      const content = `
        Fix the bug in src/utils.ts and update tests/test-utils.ts.
        Also check "src/components/Button.tsx" and './src/App.tsx'.
        Reference /src/index.js for the main entry point.
      `;

      const result = await analyzer.extractFilePaths(content);

      expect(result).toContain('src/utils.ts');
      expect(result).toContain('tests/test-utils.ts');
      expect(result).toContain('src/components/Button.tsx');
      expect(result).toContain('src/App.tsx');
      expect(result).toContain('src/index.js');
    });
  });

  describe('generateContextSummary', () => {
    it('should use AI provider for summarization', async () => {
      const context = createMockIssueContext();

      mockAIProvider.sendMessage.mockResolvedValue({
        content: 'AI-generated summary',
        usage: { tokens: 100 },
      });

      const result = await analyzer.generateContextSummary(context);

      expect(result).toBe('AI-generated summary');
      expect(mockAIProvider.sendMessage).toHaveBeenCalledWith({
        messages: expect.arrayContaining([
          expect.objectContaining({
            role: 'user',
            content: expect.stringContaining('Analyze the following GitHub issue'),
          }),
        ]),
        maxTokens: 1500,
        temperature: 0.3,
      });
    });

    it('should fallback to basic summary on AI failure', async () => {
      const context = createMockIssueContext();

      mockAIProvider.sendMessage.mockRejectedValue(new Error('AI unavailable'));

      const result = await analyzer.generateContextSummary(context);

      expect(result).toContain('Issue:');
      expect(result).toContain(context.issue.title);
    });
  });
});
```

**Integration Tests**:

```typescript
describe('ContextAnalyzer Integration', () => {
  it('should analyze real GitHub issue', async () => {
    if (!process.env.GITHUB_TOKEN_TEST) {
      return; // Skip without test credentials
    }

    const githubPlatform = new GitHubPlatform({
      token: process.env.GITHUB_TOKEN_TEST,
    });

    const analyzer = new ContextAnalyzer(
      githubPlatform,
      fileSystem,
      aiProvider,
      logger,
      eventStore
    );

    const issue = {
      id: 'test-issue-id',
      number: 123,
      title: 'Test Issue',
      repository: { owner: 'tamma', name: 'test-repo', platform: 'github' },
    };

    const context = await analyzer.analyzeIssue(issue);

    expect(context.issue).toBeDefined();
    expect(context.issue.number).toBe(123);
    expect(context.summary).toBeTruthy();
    expect(context.summary.length).toBeGreaterThan(100);
  });
});
```

### Monitoring and Observability

**Metrics to Track**:

- Context analysis success rate
- Average analysis time
- Related issues found per issue
- File paths extracted per issue
- AI summary generation success rate
- Fallback usage frequency

**Logging Strategy**:

```typescript
logger.info('Context analysis completed', {
  issueId: context.issue.id,
  issueNumber: context.issue.number,
  relatedIssuesCount: context.relatedIssues.length,
  commitHistoryCount: context.commitHistory.length,
  mentionedFilesCount: context.mentionedFiles.length,
  summaryLength: context.summary.length,
  analysisTime: Date.now() - startTime,
  aiGenerated: !context.summary.includes('Issue:'),
});

logger.warn('Related issue fetch failed', {
  issueRef,
  error: error.message,
  issueId: context.issue.id,
});
```

### Configuration Examples

**Context Analysis Configuration**:

```yaml
context_analysis:
  ai_summary:
    enabled: true
    model: 'claude-3-sonnet'
    max_tokens: 1500
    temperature: 0.3
    fallback_enabled: true

  related_issues:
    max_related: 10
    relevance_threshold: 0.1
    include_closed: true

  commit_history:
    default_limit: 10
    max_limit: 50

  file_extraction:
    enabled: true
    extensions:
      [
        'ts',
        'js',
        'tsx',
        'jsx',
        'py',
        'java',
        'cpp',
        'c',
        'h',
        'go',
        'rs',
        'rb',
        'php',
        'cs',
        'swift',
        'kt',
      ]

  repository_structure:
    cache_duration_minutes: 60
    exclude_patterns: ['.*', 'node_modules', 'dist', 'build']
```

---

## Implementation Notes

**Key Considerations**:

1. **Performance**: Context analysis should complete within 10-15 seconds for typical issues to maintain workflow velocity.

2. **AI Cost Management**: Use efficient prompting and consider caching summaries for similar issues.

3. **Rate Limiting**: Respect Git platform API limits when fetching related issues and commit history.

4. **Error Resilience**: Implement graceful degradation when AI services or Git APIs are unavailable.

5. **Data Privacy**: Ensure sensitive information in issue comments is handled appropriately.

6. **Scalability**: Design for handling large repositories with thousands of issues and commits.

**Performance Targets**:

- Context analysis: < 15 seconds total
- Related issue extraction: < 3 seconds
- Commit history fetching: < 5 seconds
- AI summary generation: < 10 seconds
- Memory usage: < 100MB for context data

**Security Considerations**:

- Sanitize issue content to prevent prompt injection attacks
- Validate file paths to prevent directory traversal
- Handle sensitive information in comments appropriately
- Use read-only API tokens for Git platform access

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
