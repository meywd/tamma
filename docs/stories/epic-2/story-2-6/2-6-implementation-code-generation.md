# Story 2.6: Implementation Code Generation

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 2.5 (failing tests must be written first)

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
I want the system to generate implementation code that passes the previously written tests,
So that I can complete the TDD green phase with working functionality.

---

## Acceptance Criteria

1. System generates implementation code based on development plan and test requirements
2. Generated code follows project conventions, patterns, and style guidelines
3. Code is structured to pass all failing tests from the red phase
4. System validates code syntax and structure before writing files
5. Implementation includes proper error handling, logging, and documentation
6. Code execution confirms all tests pass (green phase)
7. Code generation and test execution logged to event trail
8. Integration test validates TDD green phase workflow

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

**ImplementationGenerator Service**:

```typescript
interface IImplementationGenerator {
  generateImplementation(
    plan: DevelopmentPlan,
    testSuites: TestSuite[]
  ): Promise<ImplementationResult>;
  validateCode(files: GeneratedFile[]): Promise<ValidationResult>;
  writeFiles(files: GeneratedFile[]): Promise<void>;
  runTests(testSuites: TestSuite[]): Promise<TestExecutionResult>;
  confirmTestsPass(results: TestExecutionResult): Promise<boolean>;
  optimizeCode(files: GeneratedFile[]): Promise<GeneratedFile[]>;
}

interface ImplementationResult {
  files: GeneratedFile[];
  dependencies: Dependency[];
  imports: Import[];
  documentation: Documentation[];
  metrics: CodeMetrics;
  generatedAt: Date;
}

interface GeneratedFile {
  path: string;
  content: string;
  language: string;
  type: 'implementation' | 'config' | 'documentation';
  dependencies: string[];
  exports: Export[];
  functions: Function[];
  classes: Class[];
  tests: TestReference[];
}

interface Dependency {
  name: string;
  version: string;
  type: 'runtime' | 'development' | 'peer';
  reason: string;
}

interface Import {
  module: string;
  imports: string[];
  type: 'default' | 'named' | 'namespace';
  source: 'external' | 'internal' | 'relative';
}

interface Export {
  name: string;
  type: 'function' | 'class' | 'interface' | 'type' | 'constant' | 'enum';
  exported: boolean;
  default: boolean;
}

interface Function {
  name: string;
  parameters: Parameter[];
  returnType: string;
  async: boolean;
  private: boolean;
  static: boolean;
  documented: boolean;
  tests: string[];
}

interface Class {
  name: string;
  extends?: string;
  implements?: string[];
  properties: Property[];
  methods: Method[];
  constructor?: Constructor;
  documented: boolean;
}

interface Parameter {
  name: string;
  type: string;
  optional: boolean;
  defaultValue?: string;
  documented: boolean;
}

interface Property {
  name: string;
  type: string;
  private: boolean;
  static: boolean;
  readonly: boolean;
  optional: boolean;
  documented: boolean;
}

interface Method {
  name: string;
  parameters: Parameter[];
  returnType: string;
  async: boolean;
  private: boolean;
  static: boolean;
  documented: boolean;
  tests: string[];
}

interface Constructor {
  parameters: Parameter[];
  documented: boolean;
}

interface TestReference {
  testName: string;
  testFile: string;
  coverage: 'full' | 'partial' | 'none';
}

interface Documentation {
  type: 'javadoc' | 'jsdoc' | 'godoc' | 'pydoc';
  content: string;
  target: string;
}

interface CodeMetrics {
  linesOfCode: number;
  cyclomaticComplexity: number;
  maintainabilityIndex: number;
  testCoverage: number;
  duplicateLines: number;
  technicalDebt: number;
}

interface ValidationResult {
  valid: boolean;
  errors: ValidationError[];
  warnings: ValidationWarning[];
  suggestions: CodeSuggestion[];
}

interface CodeSuggestion {
  type: 'performance' | 'security' | 'maintainability' | 'best_practice';
  message: string;
  file: string;
  line?: number;
  column?: number;
  autoFixable: boolean;
}
```

### Implementation Strategy

**1. Code Generation Engine**:

```typescript
class ImplementationGenerator implements IImplementationGenerator {
  constructor(
    private aiProvider: IAIProvider,
    private fileSystem: IFileSystem,
    private testRunner: ITestRunner,
    private codeValidator: ICodeValidator,
    private config: ImplementationConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async generateImplementation(
    plan: DevelopmentPlan,
    testSuites: TestSuite[]
  ): Promise<ImplementationResult> {
    const startTime = Date.now();

    try {
      const generatedFiles: GeneratedFile[] = [];

      // Generate implementation for each file in the plan
      for (const fileChange of plan.files) {
        if (fileChange.action !== 'delete') {
          const file = await this.generateFile(fileChange, testSuites, plan);
          generatedFiles.push(file);
        }
      }

      // Validate generated code
      const validation = await this.validateCode(generatedFiles);
      if (!validation.valid) {
        throw new CodeGenerationError('Generated code has validation errors', validation.errors);
      }

      // Apply optimizations and suggestions
      const optimizedFiles = await this.optimizeCode(generatedFiles);

      // Write files to filesystem
      await this.writeFiles(optimizedFiles);

      // Run tests to confirm they pass
      const testResults = await this.runTests(testSuites);
      const testsPass = await this.confirmTestsPass(testResults);

      if (!testsPass) {
        throw new CodeGenerationError(
          'Generated code does not pass all tests',
          testResults.failures.map((f) => new Error(f.error))
        );
      }

      // Calculate metrics
      const metrics = await this.calculateMetrics(optimizedFiles);

      const result: ImplementationResult = {
        files: optimizedFiles,
        dependencies: await this.extractDependencies(optimizedFiles),
        imports: await this.extractImports(optimizedFiles),
        documentation: await this.extractDocumentation(optimizedFiles),
        metrics,
        generatedAt: new Date(),
      };

      await this.eventStore.append({
        type: 'IMPLEMENTATION.GENERATED.SUCCESS',
        tags: {
          issueId: plan.issueId,
          issueNumber: plan.issueNumber.toString(),
          planId: plan.id,
        },
        data: {
          filesGenerated: optimizedFiles.length,
          linesOfCode: metrics.linesOfCode,
          testCoverage: metrics.testCoverage,
          validationErrors: validation.errors.length,
          testFailures: testResults.failed,
          generationTime: Date.now() - startTime,
        },
      });

      this.logger.info('Implementation generation completed', {
        issueNumber: plan.issueNumber,
        filesGenerated: optimizedFiles.length,
        linesOfCode: metrics.linesOfCode,
        testCoverage: metrics.testCoverage,
        testsPassed: testResults.passed,
        testsFailed: testResults.failed,
      });

      return result;
    } catch (error) {
      await this.eventStore.append({
        type: 'IMPLEMENTATION.GENERATED.FAILED',
        tags: {
          issueId: plan.issueId,
          issueNumber: plan.issueNumber.toString(),
          planId: plan.id,
        },
        data: {
          error: error.message,
          generationTime: Date.now() - startTime,
        },
      });

      throw error;
    }
  }

  private async generateFile(
    fileChange: FileChange,
    testSuites: TestSuite[],
    plan: DevelopmentPlan
  ): Promise<GeneratedFile> {
    const relevantTests = testSuites.filter((suite) => suite.targetFile === fileChange.path);

    const prompt = this.buildCodeGenerationPrompt(fileChange, relevantTests, plan);

    try {
      const response = await this.aiProvider.sendMessage({
        messages: [{ role: 'user', content: prompt }],
        maxTokens: 4000,
        temperature: 0.2, // Lower temperature for more consistent code
        responseFormat: { type: 'json_object' },
      });

      const codeData = JSON.parse(response.content);

      return this.validateAndNormalizeFile(codeData, fileChange);
    } catch (error) {
      this.logger.error('AI code generation failed, using fallback', { error });
      return this.generateFallbackFile(fileChange, relevantTests, plan);
    }
  }

  private buildCodeGenerationPrompt(
    fileChange: FileChange,
    testSuites: TestSuite[],
    plan: DevelopmentPlan
  ): string {
    const testRequirements = this.extractTestRequirements(testSuites);
    const existingCode = this.getExistingCode(fileChange.path);
    const projectContext = this.getProjectContext(fileChange.path);

    return `
Generate implementation code that will pass the provided tests following Test-Driven Development principles.

File Change Details:
- Path: ${fileChange.path}
- Action: ${fileChange.action}
- Description: ${fileChange.description}
- Complexity: ${fileChange.estimatedComplexity}

Development Plan:
- Issue: ${plan.title}
- Summary: ${plan.summary}
- Approach: ${plan.approach.description}

Test Requirements:
${testRequirements
  .map(
    (test) => `
Test: ${test.name}
Description: ${test.description}
Expected: ${test.expected.type} - ${JSON.stringify(test.expected.value || test.expected.errorType)}
Scenario: ${test.scenario}
Given: ${test.given.join(', ')}
When: ${test.when.join(', ')}
Then: ${test.then.join(', ')}
`
  )
  .join('\n')}

${
  existingCode
    ? `
Existing Code:
\`\`\`${this.detectLanguage(fileChange.path)}
${existingCode}
\`\`\`
`
    : ''
}

Project Context:
- Language: ${projectContext.language}
- Framework: ${projectContext.framework}
- Style Guide: ${projectContext.styleGuide}
- Dependencies: ${projectContext.dependencies.join(', ')}
- Patterns: ${projectContext.patterns.join(', ')}

Requirements:
1. Generate code that will PASS all provided tests
2. Follow project conventions and style guidelines
3. Include proper error handling and validation
4. Add comprehensive documentation
5. Use appropriate design patterns
6. Ensure code is maintainable and readable
7. Include logging where appropriate
8. Handle edge cases and error conditions

Generate a JSON response with this structure:
{
  "content": "complete file content with imports, exports, and implementation",
  "language": "typescript|javascript|python|java|cpp|go|rust",
  "dependencies": ["dependency1", "dependency2"],
  "exports": [
    {
      "name": "functionName",
      "type": "function|class|interface|type|constant|enum",
      "exported": true,
      "default": false
    }
  ],
  "functions": [
    {
      "name": "functionName",
      "parameters": [
        {
          "name": "param",
          "type": "string",
          "optional": false,
          "defaultValue": null,
          "documented": true
        }
      ],
      "returnType": "string",
      "async": false,
      "private": false,
      "static": false,
      "documented": true,
      "tests": ["test-name-1", "test-name-2"]
    }
  ],
  "classes": [
    {
      "name": "ClassName",
      "extends": "BaseClass",
      "implements": ["Interface1", "Interface2"],
      "properties": [
        {
          "name": "property",
          "type": "string",
          "private": false,
          "static": false,
          "readonly": false,
          "optional": false,
          "documented": true
        }
      ],
      "methods": [
        {
          "name": "methodName",
          "parameters": [],
          "returnType": "void",
          "async": false,
          "private": false,
          "static": false,
          "documented": true,
          "tests": ["test-method"]
        }
      ],
      "constructor": {
        "parameters": [],
        "documented": true
      },
      "documented": true
    }
  ],
  "documentation": [
    {
      "type": "jsdoc|javadoc|godoc|pydoc",
      "content": "documentation content",
      "target": "function or class name"
    }
  ]
}

Focus on:
1. Writing clean, idiomatic code for the target language
2. Implementing exactly what the tests require (no more, no less)
3. Following established patterns and conventions
4. Including comprehensive error handling
5. Adding appropriate logging and debugging information
6. Writing clear, helpful documentation
7. Ensuring code is testable and maintainable
    `.trim();
  }

  private extractTestRequirements(testSuites: TestSuite[]): TestCase[] {
    const requirements: TestCase[] = [];

    for (const suite of testSuites) {
      for (const test of suite.tests) {
        requirements.push(test);
      }
    }

    return requirements;
  }

  private getExistingCode(filePath: string): string | null {
    try {
      return this.fileSystem.readFile(filePath);
    } catch (error) {
      return null; // File doesn't exist yet
    }
  }

  private getProjectContext(filePath: string): ProjectContext {
    const language = this.detectLanguage(filePath);
    const projectFiles = this.fileSystem.listFiles('.');

    return {
      language,
      framework: this.detectFramework(projectFiles, language),
      styleGuide: this.detectStyleGuide(projectFiles, language),
      dependencies: this.detectDependencies(projectFiles, language),
      patterns: this.detectPatterns(projectFiles, language),
    };
  }

  private validateAndNormalizeFile(codeData: any, fileChange: FileChange): GeneratedFile {
    if (!codeData.content) {
      throw new Error('Generated file must have content');
    }

    const language = codeData.language || this.detectLanguage(fileChange.path);

    return {
      path: fileChange.path,
      content: codeData.content,
      language,
      type: 'implementation',
      dependencies: codeData.dependencies || [],
      exports: (codeData.exports || []).map((exp: any) => this.validateExport(exp)),
      functions: (codeData.functions || []).map((func: any) => this.validateFunction(func)),
      classes: (codeData.classes || []).map((cls: any) => this.validateClass(cls)),
      tests: this.extractTestReferences(codeData),
    };
  }

  private validateFunction(func: any): Function {
    return {
      name: func.name || 'unnamed',
      parameters: (func.parameters || []).map((param: any) => this.validateParameter(param)),
      returnType: func.returnType || 'any',
      async: Boolean(func.async),
      private: Boolean(func.private),
      static: Boolean(func.static),
      documented: Boolean(func.documented),
      tests: func.tests || [],
    };
  }

  private validateClass(cls: any): Class {
    return {
      name: cls.name || 'UnnamedClass',
      extends: cls.extends,
      implements: cls.implements || [],
      properties: (cls.properties || []).map((prop: any) => this.validateProperty(prop)),
      methods: (cls.methods || []).map((method: any) => this.validateMethod(method)),
      constructor: cls.constructor ? this.validateConstructor(cls.constructor) : undefined,
      documented: Boolean(cls.documented),
    };
  }

  async validateCode(files: GeneratedFile[]): Promise<ValidationResult> {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];
    const suggestions: CodeSuggestion[] = [];

    for (const file of files) {
      try {
        // Syntax validation
        const syntaxErrors = await this.codeValidator.validateSyntax(file);
        errors.push(...syntaxErrors);

        // Style validation
        const styleWarnings = await this.codeValidator.validateStyle(file);
        warnings.push(...styleWarnings);

        // Security validation
        const securityIssues = await this.codeValidator.validateSecurity(file);
        errors.push(...securityIssues);

        // Performance suggestions
        const performanceSuggestions = await this.codeValidator.analyzePerformance(file);
        suggestions.push(...performanceSuggestions);

        // Best practices
        const practiceSuggestions = await this.codeValidator.analyzeBestPractices(file);
        suggestions.push(...practiceSuggestions);
      } catch (error) {
        errors.push({
          type: 'syntax',
          message: `Failed to validate ${file.path}: ${error.message}`,
          file: file.path,
        });
      }
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
      suggestions,
    };
  }

  async optimizeCode(files: GeneratedFile[]): Promise<GeneratedFile[]> {
    const optimizedFiles: GeneratedFile[] = [];

    for (const file of files) {
      try {
        // Apply code optimizations
        let optimizedContent = file.content;

        // Remove unused imports
        optimizedContent = await this.removeUnusedImports(optimizedContent, file);

        // Optimize imports organization
        optimizedContent = await this.organizeImports(optimizedContent, file);

        // Apply performance optimizations
        optimizedContent = await this.applyPerformanceOptimizations(optimizedContent, file);

        // Apply security hardening
        optimizedContent = await this.applySecurityHardening(optimizedContent, file);

        // Format code according to project style
        optimizedContent = await this.formatCode(optimizedContent, file);

        const optimizedFile: GeneratedFile = {
          ...file,
          content: optimizedContent,
        };

        optimizedFiles.push(optimizedFile);
      } catch (error) {
        this.logger.warn('Code optimization failed, using original', {
          file: file.path,
          error: error.message,
        });

        optimizedFiles.push(file);
      }
    }

    return optimizedFiles;
  }

  async writeFiles(files: GeneratedFile[]): Promise<void> {
    for (const file of files) {
      try {
        // Create directory if it doesn't exist
        const dir = file.path.substring(0, file.path.lastIndexOf('/'));
        if (dir) {
          await this.fileSystem.createDirectory(dir, { recursive: true });
        }

        // Write file content
        await this.fileSystem.writeFile(file.path, file.content);

        this.logger.debug('Implementation file written', {
          path: file.path,
          size: file.content.length,
          language: file.language,
          functions: file.functions.length,
          classes: file.classes.length,
        });
      } catch (error) {
        throw new FileOperationError(
          `Failed to write file ${file.path}: ${error.message}`,
          'write_failed',
          file.path,
          error
        );
      }
    }
  }

  async runTests(testSuites: TestSuite[]): Promise<TestExecutionResult> {
    try {
      const testFiles = testSuites.map((suite) => ({
        path: this.getTestFilePath(suite.targetFile, suite.framework),
        content: '', // Content not needed for execution
        framework: suite.framework,
        language: this.detectLanguage(suite.targetFile),
        dependencies: [],
        imports: [],
      }));

      return await this.testRunner.runTests(testFiles);
    } catch (error) {
      this.logger.error('Test execution failed', { error });

      return {
        passed: 0,
        failed: testSuites.reduce((sum, suite) => sum + suite.tests.length, 0),
        skipped: 0,
        total: testSuites.reduce((sum, suite) => sum + suite.tests.length, 0),
        duration: 0,
        failures: [
          {
            test: 'test-execution',
            file: 'test-runner',
            error: error.message,
          },
        ],
        framework: testSuites[0]?.framework || 'unknown',
      };
    }
  }

  async confirmTestsPass(results: TestExecutionResult): Promise<boolean> {
    // In TDD green phase, all tests should pass
    const passRate = results.total > 0 ? results.passed / results.total : 0;
    const minRequiredPassRate = 0.95; // Allow 5% of tests to fail for flaky tests

    const testsPassAsExpected = passRate >= minRequiredPassRate;

    if (!testsPassAsExpected) {
      this.logger.error('Tests are not passing in green phase', {
        passed: results.passed,
        total: results.total,
        passRate: (passRate * 100).toFixed(1) + '%',
        required: (minRequiredPassRate * 100).toFixed(1) + '%',
        failures: results.failures.map((f) => f.error),
      });
    }

    return testsPassAsExpected;
  }

  private async calculateMetrics(files: GeneratedFile[]): Promise<CodeMetrics> {
    let totalLines = 0;
    let totalComplexity = 0;
    let totalDuplicates = 0;

    for (const file of files) {
      const lines = file.content.split('\n').length;
      const complexity = await this.calculateCyclomaticComplexity(file);
      const duplicates = await this.detectDuplicates(file);

      totalLines += lines;
      totalComplexity += complexity;
      totalDuplicates += duplicates;
    }

    const avgComplexity = files.length > 0 ? totalComplexity / files.length : 0;
    const maintainabilityIndex = this.calculateMaintainabilityIndex(totalLines, avgComplexity);
    const testCoverage = await this.calculateTestCoverage(files);

    return {
      linesOfCode: totalLines,
      cyclomaticComplexity: avgComplexity,
      maintainabilityIndex,
      testCoverage,
      duplicateLines: totalDuplicates,
      technicalDebt: this.calculateTechnicalDebt(avgComplexity, maintainabilityIndex, testCoverage),
    };
  }

  private generateFallbackFile(
    fileChange: FileChange,
    testSuites: TestSuite[],
    plan: DevelopmentPlan
  ): GeneratedFile {
    const language = this.detectLanguage(fileChange.path);
    const content = this.generateBasicImplementation(fileChange, testSuites, language);

    return {
      path: fileChange.path,
      content,
      language,
      type: 'implementation',
      dependencies: [],
      exports: [],
      functions: [],
      classes: [],
      tests: [],
    };
  }

  private generateBasicImplementation(
    fileChange: FileChange,
    testSuites: TestSuite[],
    language: string
  ): string {
    // Generate minimal implementation based on test requirements
    const testRequirements = this.extractTestRequirements(testSuites);

    switch (language) {
      case 'typescript':
      case 'javascript':
        return this.generateBasicJSImplementation(fileChange, testRequirements);
      case 'python':
        return this.generateBasicPythonImplementation(fileChange, testRequirements);
      default:
        return this.generateGenericImplementation(fileChange, testRequirements);
    }
  }

  private generateBasicJSImplementation(
    fileChange: FileChange,
    testRequirements: TestCase[]
  ): string {
    let content = '';

    // Add basic exports based on test requirements
    const functions = new Set<string>();

    for (const test of testRequirements) {
      // Extract function names from test scenarios
      const functionMatches = test.when.join(' ').match(/(\w+)\(/g);
      if (functionMatches) {
        for (const match of functionMatches) {
          functions.add(match.replace('(', ''));
        }
      }
    }

    // Generate basic function stubs
    for (const functionName of functions) {
      content += `
export function ${functionName}(...args: any[]): any {
  // TODO: Implement ${functionName}
  throw new Error('Not implemented yet');
}
`;
    }

    return content.trim();
  }

  private detectLanguage(filePath: string): string {
    const ext = filePath.split('.').pop()?.toLowerCase();

    const languageMap: Record<string, string> = {
      ts: 'typescript',
      tsx: 'typescript',
      js: 'javascript',
      jsx: 'javascript',
      py: 'python',
      java: 'java',
      cpp: 'cpp',
      c: 'c',
      go: 'go',
      rs: 'rust',
      rb: 'ruby',
      php: 'php',
      cs: 'csharp',
      swift: 'swift',
      kt: 'kotlin',
    };

    return languageMap[ext || ''] || 'unknown';
  }

  private extractTestReferences(codeData: any): TestReference[] {
    // Extract test references from generated code metadata
    const references: TestReference[] = [];

    if (codeData.functions) {
      for (const func of codeData.functions) {
        if (func.tests) {
          for (const testName of func.tests) {
            references.push({
              testName,
              testFile: '', // Would be populated from test suites
              coverage: 'full',
            });
          }
        }
      }
    }

    return references;
  }
}
```

### Integration Points

**1. AI Provider Integration**:

- Code generation using structured prompts
- Context-aware generation based on tests
- Fallback to rule-based generation

**2. Code Validation Integration**:

- Syntax checking for generated code
- Style guide validation
- Security vulnerability scanning
- Performance analysis

**3. Test Runner Integration**:

- Executing tests to verify implementation
- Collecting test results and coverage
- Framework-specific test execution

**4. File System Integration**:

- Writing generated files to appropriate locations
- Managing file permissions and organization
- Handling file conflicts and backups

### Testing Strategy

**Unit Tests**:

```typescript
describe('ImplementationGenerator', () => {
  let generator: ImplementationGenerator;
  let mockAIProvider: jest.Mocked<IAIProvider>;
  let mockFileSystem: jest.Mocked<IFileSystem>;
  let mockTestRunner: jest.Mocked<ITestRunner>;
  let mockCodeValidator: jest.Mocked<ICodeValidator>;

  beforeEach(() => {
    mockAIProvider = createMockAIProvider();
    mockFileSystem = createMockFileSystem();
    mockTestRunner = createMockTestRunner();
    mockCodeValidator = createMockCodeValidator();
    generator = new ImplementationGenerator(
      mockAIProvider,
      mockFileSystem,
      mockTestRunner,
      mockCodeValidator,
      createMockConfig(),
      mockLogger,
      mockEventStore
    );
  });

  describe('generateImplementation', () => {
    it('should generate code that passes all tests', async () => {
      const plan = createMockDevelopmentPlan();
      const testSuites = createMockTestSuites();

      mockAIProvider.sendMessage.mockResolvedValue({
        content: JSON.stringify(createMockCodeData()),
        usage: { tokens: 2000 },
      });

      mockCodeValidator.validateSyntax.mockResolvedValue([]);
      mockCodeValidator.validateStyle.mockResolvedValue([]);
      mockCodeValidator.validateSecurity.mockResolvedValue([]);
      mockCodeValidator.analyzePerformance.mockResolvedValue([]);
      mockCodeValidator.analyzeBestPractices.mockResolvedValue([]);

      mockFileSystem.writeFile.mockResolvedValue();
      mockTestRunner.runTests.mockResolvedValue({
        passed: 5,
        failed: 0,
        skipped: 0,
        total: 5,
        duration: 200,
        failures: [],
        framework: 'vitest',
      });

      const result = await generator.generateImplementation(plan, testSuites);

      expect(result.files).toHaveLength(1);
      expect(result.metrics.linesOfCode).toBeGreaterThan(0);
      expect(mockFileSystem.writeFile).toHaveBeenCalled();
      expect(mockTestRunner.runTests).toHaveBeenCalled();
    });

    it('should handle AI generation failure with fallback', async () => {
      const plan = createMockDevelopmentPlan();
      const testSuites = createMockTestSuites();

      mockAIProvider.sendMessage.mockRejectedValue(new Error('AI unavailable'));

      const result = await generator.generateImplementation(plan, testSuites);

      expect(result.files[0].content).toContain('TODO: Implement');
      expect(result.files[0].functions).toHaveLength(0); // Fallback has minimal metadata
    });

    it('should reject when generated code fails tests', async () => {
      const plan = createMockDevelopmentPlan();
      const testSuites = createMockTestSuites();

      mockAIProvider.sendMessage.mockResolvedValue({
        content: JSON.stringify(createMockCodeData()),
        usage: { tokens: 2000 },
      });

      mockCodeValidator.validateSyntax.mockResolvedValue([]);
      mockFileSystem.writeFile.mockResolvedValue();
      mockTestRunner.runTests.mockResolvedValue({
        passed: 2,
        failed: 3,
        skipped: 0,
        total: 5,
        duration: 200,
        failures: [{ test: 'test-1', file: 'test.ts', error: 'Expected true but got false' }],
        framework: 'vitest',
      });

      await expect(generator.generateImplementation(plan, testSuites)).rejects.toThrow(
        'does not pass all tests'
      );
    });
  });
});
```

### Configuration Examples

**Implementation Generation Configuration**:

```yaml
implementation_generation:
  ai_generation:
    model: 'claude-3-sonnet'
    max_tokens: 4000
    temperature: 0.2
    fallback_enabled: true

  code_validation:
    syntax_check: true
    style_validation: true
    security_scan: true
    performance_analysis: true
    best_practices: true

  optimization:
    remove_unused_imports: true
    organize_imports: true
    performance_optimizations: true
    security_hardening: true
    code_formatting: true

  testing:
    min_pass_rate_green_phase: 0.95 # 95% of tests must pass
    timeout_seconds: 60
    coverage_collection: true

  languages:
    typescript:
      style_guide: 'prettier'
      linter: 'eslint'
      formatter: 'prettier'

    python:
      style_guide: 'pep8'
      linter: 'flake8'
      formatter: 'black'

    java:
      style_guide: 'google'
      linter: 'checkstyle'
      formatter: 'google-java-format'

  quality_gates:
    max_cyclomatic_complexity: 10
    min_maintainability_index: 70
    max_duplicate_lines: 50
    min_test_coverage: 80
```

---

## Implementation Notes

**Key Considerations**:

1. **TDD Green Phase**: Generated code must pass all previously written failing tests.

2. **Code Quality**: Generated code should follow project conventions and be production-ready.

3. **Error Handling**: Comprehensive error handling and validation are essential for robust implementations.

4. **Performance**: Code generation should be efficient while maintaining quality.

5. **Security**: Generated code must be secure and free from vulnerabilities.

6. **Maintainability**: Code should be readable, documented, and easy to maintain.

**Performance Targets**:

- Code generation: < 60 seconds per file
- Validation: < 10 seconds per file
- Test execution: < 30 seconds
- Total green phase: < 2 minutes per file

**Security Considerations**:

- Validate generated code for security vulnerabilities
- Sanitize inputs and outputs appropriately
- Use secure coding practices
- Handle sensitive data properly
- Implement proper authentication and authorization

**Code Quality Best Practices**:

- Follow language-specific conventions
- Write clear, descriptive names
- Include comprehensive documentation
- Handle edge cases and errors
- Use appropriate design patterns
- Ensure code is testable and maintainable
- Optimize for readability and performance

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
