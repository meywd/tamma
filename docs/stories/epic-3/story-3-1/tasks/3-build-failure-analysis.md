# Task 3: Build Failure Analysis System

**Story**: 3.1 - Build Automation Gate Implementation  
**Phase**: Core MVP  
**Priority**: High  
**Estimated Time**: 3-4 days

## 🎯 Objective

Implement system to analyze build failures, extract error messages and logs, and categorize failure types for intelligent fix suggestions.

## ✅ Acceptance Criteria

- [ ] System retrieves build logs and error messages on failure
- [ ] Parse and categorize build errors (compilation, dependency, configuration, etc.)
- [ ] Extract relevant error context (file names, line numbers, error codes)
- [ ] Identify root cause of build failures
- [ ] Support multiple build systems and languages
- [ ] Store failure analysis for future reference
- [ ] Provide structured failure data for AI analysis

## 🔧 Technical Implementation

### Core Interfaces

```typescript
interface IBuildFailureAnalyzer {
  analyzeFailure(buildResult: BuildResult): Promise<BuildFailureAnalysis>;
  categorizeError(error: BuildError): ErrorCategory;
  extractContext(error: BuildError): ErrorContext;
  findSimilarFailures(analysis: BuildFailureAnalysis): Promise<SimilarFailure[]>;
}

interface BuildFailureAnalysis {
  buildId: string;
  primaryError: BuildError;
  errorCategory: ErrorCategory;
  rootCause: string;
  confidence: number;
  context: ErrorContext;
  suggestedActions: string[];
  similarFailures: SimilarFailure[];
  analysisTimestamp: Date;
}

interface BuildError {
  type: ErrorType;
  message: string;
  file?: string;
  line?: number;
  column?: number;
  code?: string;
  stack?: string;
  severity: ErrorSeverity;
  timestamp?: Date;
}

enum ErrorCategory {
  COMPILATION = 'compilation',
  DEPENDENCY = 'dependency',
  CONFIGURATION = 'configuration',
  TEST_FAILURE = 'test_failure',
  RUNTIME = 'runtime',
  NETWORK = 'network',
  PERMISSION = 'permission',
  TIMEOUT = 'timeout',
  RESOURCE = 'resource',
  UNKNOWN = 'unknown',
}

enum ErrorType {
  SYNTAX_ERROR = 'syntax_error',
  TYPE_ERROR = 'type_error',
  IMPORT_ERROR = 'import_error',
  MODULE_NOT_FOUND = 'module_not_found',
  VERSION_CONFLICT = 'version_conflict',
  MISSING_DEPENDENCY = 'missing_dependency',
  CONFIG_ERROR = 'config_error',
  BUILD_SCRIPT_ERROR = 'build_script_error',
  TEST_ASSERTION_FAILED = 'test_assertion_failed',
  NETWORK_TIMEOUT = 'network_timeout',
  PERMISSION_DENIED = 'permission_denied',
  OUT_OF_MEMORY = 'out_of_memory',
  DISK_SPACE = 'disk_space',
}

interface ErrorContext {
  language: string;
  buildSystem: string;
  framework?: string;
  dependencies: DependencyInfo[];
  environment: EnvironmentInfo;
  recentChanges: FileChange[];
  buildConfiguration: BuildConfiguration;
}
```

### Failure Analysis Implementation

```typescript
class BuildFailureAnalyzer implements IBuildFailureAnalyzer {
  constructor(
    private errorParsers: Map<string, IErrorParser>,
    private contextExtractor: IContextExtractor,
    private failureRepository: IFailureRepository,
    private logger: Logger
  ) {}

  async analyzeFailure(buildResult: BuildResult): Promise<BuildFailureAnalysis> {
    // Extract errors from build logs
    const errors = await this.extractErrors(buildResult.logs);

    if (errors.length === 0) {
      return this.createUnknownFailureAnalysis(buildResult);
    }

    // Identify primary error (most severe or first occurrence)
    const primaryError = this.identifyPrimaryError(errors);

    // Categorize the error
    const errorCategory = this.categorizeError(primaryError);

    // Extract context around the error
    const context = await this.extractContext(primaryError, buildResult);

    // Determine root cause
    const rootCause = await this.determineRootCause(primaryError, context);

    // Find similar past failures
    const similarFailures = await this.findSimilarFailures({
      buildId: buildResult.buildId,
      primaryError,
      errorCategory,
      rootCause,
      context,
      confidence: 0,
      suggestedActions: [],
      similarFailures: [],
      analysisTimestamp: new Date(),
    });

    // Generate suggested actions
    const suggestedActions = this.generateSuggestedActions(
      primaryError,
      errorCategory,
      context,
      similarFailures
    );

    const analysis: BuildFailureAnalysis = {
      buildId: buildResult.buildId,
      primaryError,
      errorCategory,
      rootCause,
      confidence: this.calculateConfidence(primaryError, context),
      context,
      suggestedActions,
      similarFailures,
      analysisTimestamp: new Date(),
    };

    // Store analysis for future reference
    await this.failureRepository.storeAnalysis(analysis);

    return analysis;
  }

  private async extractErrors(logs: BuildLog[]): Promise<BuildError[]> {
    const errors: BuildError[] = [];

    for (const log of logs) {
      const parser = this.errorParsers.get(log.language);
      if (!parser) {
        continue; // Skip unsupported languages
      }

      const logErrors = await parser.parseErrors(log.content);
      errors.push(...logErrors);
    }

    // Sort by severity and timestamp
    return errors.sort((a, b) => {
      const severityOrder = {
        [ErrorSeverity.CRITICAL]: 0,
        [ErrorSeverity.HIGH]: 1,
        [ErrorSeverity.MEDIUM]: 2,
        [ErrorSeverity.LOW]: 3,
      };

      return severityOrder[a.severity] - severityOrder[b.severity];
    });
  }

  private identifyPrimaryError(errors: BuildError[]): BuildError {
    // Return the most severe error
    // If multiple have same severity, return the first one
    return errors.reduce((primary, current) => {
      const severityOrder = {
        [ErrorSeverity.CRITICAL]: 0,
        [ErrorSeverity.HIGH]: 1,
        [ErrorSeverity.MEDIUM]: 2,
        [ErrorSeverity.LOW]: 3,
      };

      return severityOrder[current.severity] < severityOrder[primary.severity] ? current : primary;
    });
  }

  categorizeError(error: BuildError): ErrorCategory {
    // Use pattern matching and heuristics to categorize
    const message = error.message.toLowerCase();
    const file = error.file?.toLowerCase() || '';

    // Compilation errors
    if (
      this.matchesPatterns(message, [
        /syntax error/i,
        /unexpected token/i,
        /parse error/i,
        /invalid syntax/i,
        /cannot find symbol/i,
        /type error/i,
      ])
    ) {
      return ErrorCategory.COMPILATION;
    }

    // Dependency errors
    if (
      this.matchesPatterns(message, [
        /module not found/i,
        /cannot resolve module/i,
        /dependency not found/i,
        /version conflict/i,
        /peer dependency/i,
        /missing dependency/i,
      ])
    ) {
      return ErrorCategory.DEPENDENCY;
    }

    // Configuration errors
    if (
      this.matchesPatterns(message, [
        /config.*error/i,
        /invalid configuration/i,
        /missing config/i,
        /build config/i,
        /webpack/i,
        /babel/i,
        /tsconfig/i,
      ])
    ) {
      return ErrorCategory.CONFIGURATION;
    }

    // Test failures
    if (
      this.matchesPatterns(message, [
        /test.*failed/i,
        /assertion.*failed/i,
        /expect.*to/i,
        /should.*but/i,
        /spec.*failed/i,
      ])
    ) {
      return ErrorCategory.TEST_FAILURE;
    }

    // Permission errors
    if (
      this.matchesPatterns(message, [
        /permission denied/i,
        /access denied/i,
        /unauthorized/i,
        /eacces/i,
        /eperm/i,
      ])
    ) {
      return ErrorCategory.PERMISSION;
    }

    // Resource errors
    if (
      this.matchesPatterns(message, [
        /out of memory/i,
        /memory limit/i,
        /disk space/i,
        /no space left/i,
        /resource exhausted/i,
      ])
    ) {
      return ErrorCategory.RESOURCE;
    }

    // Network errors
    if (
      this.matchesPatterns(message, [
        /network/i,
        /connection/i,
        /timeout/i,
        /econnrefused/i,
        /etimedout/i,
      ])
    ) {
      return ErrorCategory.NETWORK;
    }

    return ErrorCategory.UNKNOWN;
  }

  private async determineRootCause(error: BuildError, context: ErrorContext): Promise<string> {
    // Use heuristics and context to determine root cause
    const category = this.categorizeError(error);

    switch (category) {
      case ErrorCategory.COMPILATION:
        return this.analyzeCompilationRootCause(error, context);

      case ErrorCategory.DEPENDENCY:
        return this.analyzeDependencyRootCause(error, context);

      case ErrorCategory.CONFIGURATION:
        return this.analyzeConfigurationRootCause(error, context);

      default:
        return `Build failed due to ${category}: ${error.message}`;
    }
  }

  private analyzeCompilationRootCause(error: BuildError, context: ErrorContext): string {
    if (error.type === ErrorType.SYNTAX_ERROR) {
      return `Syntax error in ${error.file}:${error.line} - invalid code structure`;
    }

    if (error.type === ErrorType.TYPE_ERROR) {
      return `Type mismatch in ${error.file}:${error.line} - incompatible types`;
    }

    if (error.type === ErrorType.IMPORT_ERROR) {
      return `Import error in ${error.file} - module or path not found`;
    }

    return `Compilation error in ${error.file || 'unknown file'}`;
  }

  private generateSuggestedActions(
    error: BuildError,
    category: ErrorCategory,
    context: ErrorContext,
    similarFailures: SimilarFailure[]
  ): string[] {
    const actions: string[] = [];

    // Base actions by category
    switch (category) {
      case ErrorCategory.COMPILATION:
        actions.push(
          'Check syntax in the affected file',
          'Verify all imports and exports',
          'Run linter to catch syntax issues'
        );
        break;

      case ErrorCategory.DEPENDENCY:
        actions.push(
          'Run dependency installation (npm install, pip install, etc.)',
          'Check for version conflicts',
          'Clear package manager cache'
        );
        break;

      case ErrorCategory.CONFIGURATION:
        actions.push(
          'Review build configuration files',
          'Check environment variables',
          'Validate configuration syntax'
        );
        break;
    }

    // Add actions from similar failures
    for (const similar of similarFailures.slice(0, 3)) {
      if (similar.resolution) {
        actions.push(similar.resolution);
      }
    }

    return actions;
  }
}
```

### Error Parsers

#### TypeScript/JavaScript Parser

```typescript
class TypeScriptErrorParser implements IErrorParser {
  parseErrors(logContent: string): Promise<BuildError[]> {
    const errors: BuildError[] = [];
    const lines = logContent.split('\n');

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];

      // TypeScript error pattern: file(line,column): error TScode: message
      const tsErrorMatch = line.match(/^(.+)\((\d+),(\d+)\):\s*error\s+TS(\d+):\s*(.+)$/);
      if (tsErrorMatch) {
        errors.push({
          type: this.mapTSErrorToType(tsErrorMatch[4]),
          message: tsErrorMatch[5],
          file: tsErrorMatch[1],
          line: parseInt(tsErrorMatch[2]),
          column: parseInt(tsErrorMatch[3]),
          code: `TS${tsErrorMatch[4]}`,
          severity: this.mapTSErrorToSeverity(tsErrorMatch[4]),
        });
        continue;
      }

      // JavaScript error pattern: Error: message at file:line:column
      const jsErrorMatch = line.match(/^Error:\s*(.+)\s+at\s+(.+):(\d+):(\d+)$/);
      if (jsErrorMatch) {
        errors.push({
          type: ErrorType.RUNTIME_ERROR,
          message: jsErrorMatch[1],
          file: jsErrorMatch[2],
          line: parseInt(jsErrorMatch[3]),
          column: parseInt(jsErrorMatch[4]),
          severity: ErrorSeverity.HIGH,
        });
      }
    }

    return errors;
  }

  private mapTSErrorToType(tsCode: string): ErrorType {
    const code = parseInt(tsCode);

    // Syntax errors (1000-1999)
    if (code >= 1000 && code < 2000) {
      return ErrorType.SYNTAX_ERROR;
    }

    // Type errors (2000-2999)
    if (code >= 2000 && code < 3000) {
      return ErrorType.TYPE_ERROR;
    }

    // Module errors (2300-2499)
    if (code >= 2300 && code < 2500) {
      return ErrorType.IMPORT_ERROR;
    }

    return ErrorType.UNKNOWN;
  }
}
```

#### Java/Maven Parser

```typescript
class MavenErrorParser implements IErrorParser {
  parseErrors(logContent: string): Promise<BuildError[]> {
    const errors: BuildError[] = [];
    const lines = logContent.split('\n');

    for (const line of lines) {
      // Compilation error pattern: [ERROR] /path/to/file.java:[line,column] error: message
      const compileErrorMatch = line.match(/^\[ERROR\]\s+(.+\.java):(\d+),(\d+)\s+error:\s*(.+)$/);
      if (compileErrorMatch) {
        errors.push({
          type: ErrorType.COMPILATION_ERROR,
          message: compileErrorMatch[4],
          file: compileErrorMatch[1],
          line: parseInt(compileErrorMatch[2]),
          column: parseInt(compileErrorMatch[3]),
          severity: ErrorSeverity.HIGH,
        });
        continue;
      }

      // Dependency error pattern: [ERROR] Failed to execute goal
      const dependencyErrorMatch = line.match(/^\[ERROR\]\s+Failed to execute goal.+:\s*(.+)$/);
      if (dependencyErrorMatch) {
        errors.push({
          type: ErrorType.DEPENDENCY_ERROR,
          message: dependencyErrorMatch[1],
          severity: ErrorSeverity.HIGH,
        });
      }
    }

    return errors;
  }
}
```

### Context Extraction

```typescript
class ContextExtractor implements IContextExtractor {
  async extractContext(error: BuildError, buildResult: BuildResult): Promise<ErrorContext> {
    const language = this.detectLanguage(error.file || '');
    const buildSystem = buildResult.buildSystem;

    return {
      language,
      buildSystem,
      framework: await this.detectFramework(buildResult.projectPath),
      dependencies: await this.extractDependencies(buildResult.projectPath, language),
      environment: await this.getEnvironmentInfo(buildResult),
      recentChanges: await this.getRecentChanges(buildResult),
      buildConfiguration: buildResult.configuration,
    };
  }

  private detectLanguage(filePath: string): string {
    const ext = path.extname(filePath).toLowerCase();

    const languageMap: Record<string, string> = {
      '.ts': 'typescript',
      '.js': 'javascript',
      '.jsx': 'react',
      '.tsx': 'react-typescript',
      '.java': 'java',
      '.py': 'python',
      '.go': 'go',
      '.rs': 'rust',
      '.cpp': 'cpp',
      '.c': 'c',
    };

    return languageMap[ext] || 'unknown';
  }

  private async extractDependencies(
    projectPath: string,
    language: string
  ): Promise<DependencyInfo[]> {
    switch (language) {
      case 'typescript':
      case 'javascript':
        return this.extractNpmDependencies(projectPath);
      case 'java':
        return this.extractMavenDependencies(projectPath);
      case 'python':
        return this.extractPythonDependencies(projectPath);
      default:
        return [];
    }
  }

  private async extractNpmDependencies(projectPath: string): Promise<DependencyInfo[]> {
    try {
      const packageJsonPath = path.join(projectPath, 'package.json');
      const packageJson = JSON.parse(await fs.readFile(packageJsonPath, 'utf-8'));

      const dependencies: DependencyInfo[] = [];

      for (const [name, version] of Object.entries({
        ...packageJson.dependencies,
        ...packageJson.devDependencies,
      })) {
        dependencies.push({
          name,
          version: version as string,
          type: 'runtime',
        });
      }

      return dependencies;
    } catch (error) {
      return [];
    }
  }
}
```

## 🧪 Testing Strategy

### Unit Tests

```typescript
describe('BuildFailureAnalyzer', () => {
  let analyzer: BuildFailureAnalyzer;
  let mockErrorParser: jest.Mocked<IErrorParser>;
  let mockContextExtractor: jest.Mocked<IContextExtractor>;

  beforeEach(() => {
    mockErrorParser = createMockErrorParser();
    mockContextExtractor = createMockContextExtractor();
    analyzer = new BuildFailureAnalyzer(
      new Map([['typescript', mockErrorParser]]),
      mockContextExtractor,
      mockFailureRepository,
      mockLogger
    );
  });

  it('should analyze TypeScript compilation error', async () => {
    const buildResult = createMockBuildResult({
      logs: [
        {
          language: 'typescript',
          content:
            "src/file.ts(10,5): error TS2322: Type 'string' is not assignable to type 'number'.",
        },
      ],
    });

    mockErrorParser.parseErrors.mockResolvedValue([
      {
        type: ErrorType.TYPE_ERROR,
        message: "Type 'string' is not assignable to type 'number'.",
        file: 'src/file.ts',
        line: 10,
        column: 5,
        code: 'TS2322',
        severity: ErrorSeverity.HIGH,
      },
    ]);

    const analysis = await analyzer.analyzeFailure(buildResult);

    expect(analysis.errorCategory).toBe(ErrorCategory.COMPILATION);
    expect(analysis.primaryError.file).toBe('src/file.ts');
    expect(analysis.primaryError.line).toBe(10);
    expect(analysis.suggestedActions).toContain('Check syntax in the affected file');
  });

  it('should categorize dependency errors correctly', async () => {
    const buildResult = createMockBuildResult({
      logs: [
        {
          language: 'javascript',
          content: "Error: Cannot find module 'lodash'",
        },
      ],
    });

    mockErrorParser.parseErrors.mockResolvedValue([
      {
        type: ErrorType.MODULE_NOT_FOUND,
        message: "Cannot find module 'lodash'",
        severity: ErrorSeverity.HIGH,
      },
    ]);

    const analysis = await analyzer.analyzeFailure(buildResult);

    expect(analysis.errorCategory).toBe(ErrorCategory.DEPENDENCY);
    expect(analysis.suggestedActions).toContain('Run dependency installation');
  });
});
```

### Integration Tests

```typescript
describe('BuildFailureAnalyzer Integration', () => {
  it('should analyze real build failure logs', async () => {
    const analyzer = new BuildFailureAnalyzer(
      new Map([
        ['typescript', new TypeScriptErrorParser()],
        ['javascript', new JavaScriptErrorParser()],
      ]),
      new ContextExtractor(),
      new FailureRepository(),
      new Logger()
    );

    const buildResult = createRealBuildFailureResult();
    const analysis = await analyzer.analyzeFailure(buildResult);

    expect(analysis.primaryError).toBeDefined();
    expect(analysis.errorCategory).not.toBe(ErrorCategory.UNKNOWN);
    expect(analysis.confidence).toBeGreaterThan(0.5);
  });
});
```

## 📊 Monitoring & Metrics

### Key Metrics

- Error categorization accuracy
- Root cause identification success rate
- Analysis processing time
- Similar failure match rate

### Events to Emit

```typescript
// Failure analysis events
BUILD.FAILURE_ANALYSIS_STARTED;
BUILD.FAILURE_ANALYSIS_COMPLETED;
BUILD.ERROR_CATEGORIZED;
BUILD.ROOT_CAUSE_IDENTIFIED;
BUILD.SIMILAR_FAILURES_FOUND;
```

## 🔧 Configuration

### Environment Variables

```bash
# Analysis configuration
FAILURE_ANALYSIS_TIMEOUT=30000
MAX_SIMILAR_FAILURES=10
CONFIDENCE_THRESHOLD=0.7

# Parser configuration
ENABLE_SMART_PARSING=true
EXTRACT_STACK_TRACES=true
PARSE_DEPENDENCY_INFO=true
```

### Configuration File

```yaml
failure_analysis:
  timeout: 30000
  max_similar_failures: 10
  confidence_threshold: 0.7

parsers:
  typescript:
    enabled: true
    extract_stack_traces: true

  javascript:
    enabled: true
    extract_stack_traces: true

  java:
    enabled: true
    maven_enabled: true
    gradle_enabled: true

  python:
    enabled: true
    pip_enabled: true
    poetry_enabled: true

error_patterns:
  compilation:
    - 'syntax error'
    - 'parse error'
    - 'type error'

  dependency:
    - 'module not found'
    - 'cannot resolve'
    - 'version conflict'
```

## 🚨 Error Handling

### Common Error Scenarios

1. **Unparseable log formats**
   - Fall back to generic error extraction
   - Log parsing failures for investigation

2. **Missing context information**
   - Use default values
   - Flag for manual review

3. **Large log files**
   - Implement streaming parsing
   - Use memory-efficient algorithms

### Recovery Strategies

- Graceful degradation for unknown error types
- Multiple parsing strategies per language
- Fallback to generic categorization

## 📝 Implementation Checklist

- [ ] Define core interfaces and enums
- [ ] Implement BuildFailureAnalyzer class
- [ ] Create language-specific error parsers
- [ ] Implement ContextExtractor
- [ ] Create FailureRepository for storage
- [ ] Add comprehensive error categorization
- [ ] Implement similar failure detection
- [ ] Write unit tests for all components
- [ ] Write integration tests with real build logs
- [ ] Add monitoring and metrics
- [ ] Create configuration management
- [ ] Document error patterns and handling

---

**Dependencies**: Task 2 (Build Status Polling)  
**Blocked By**: None  
**Blocks**: Task 4 (AI-Powered Fix Suggestions)
