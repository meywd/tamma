# Story 2.5: Test-First Development - Write Failing Tests

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: High  
**Prerequisites**: Story 2.4 (Git branch creation must complete first)

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
I want the system to write failing tests first (TDD red phase),
So that I can ensure test coverage before implementation and validate requirements.

---

## Acceptance Criteria

1. System generates test files based on development plan requirements
2. Tests are written to fail initially (no implementation exists yet)
3. Test structure follows project conventions and testing framework
4. Tests cover edge cases, error conditions, and happy paths
5. System validates test syntax and structure
6. Test execution confirms tests fail as expected
7. Test generation and execution logged to event trail
8. Integration test validates TDD red phase workflow

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

**TestFirstGenerator Service**:

```typescript
interface ITestFirstGenerator {
  generateTests(plan: DevelopmentPlan): Promise<TestSuite[]>;
  validateTestSyntax(testFiles: TestFile[]): Promise<ValidationResult>;
  executeTests(testFiles: TestFile[]): Promise<TestExecutionResult>;
  confirmTestsFail(results: TestExecutionResult): Promise<boolean>;
  organizeTestFiles(tests: TestSuite[]): Promise<TestFile[]>;
}

interface TestSuite {
  id: string;
  name: string;
  description: string;
  targetFile: string;
  testType: 'unit' | 'integration' | 'e2e' | 'performance';
  framework: string;
  tests: TestCase[];
  setup: string[];
  teardown: string[];
  mocks: MockDefinition[];
  fixtures: FixtureDefinition[];
}

interface TestCase {
  id: string;
  name: string;
  description: string;
  scenario: string;
  given: string[];
  when: string[];
  then: string[];
  expected: ExpectedResult;
  tags: string[];
  priority: 'critical' | 'high' | 'medium' | 'low';
}

interface ExpectedResult {
  type: 'throws' | 'returns' | 'equals' | 'contains' | 'matches' | 'truthy' | 'falsy';
  value?: any;
  errorType?: string;
  message?: string;
  properties?: Record<string, any>;
}

interface TestFile {
  path: string;
  content: string;
  framework: string;
  language: string;
  dependencies: string[];
  imports: string[];
}

interface MockDefinition {
  name: string;
  type: 'function' | 'class' | 'module';
  implementation: string;
  behavior: MockBehavior[];
}

interface MockBehavior {
  input: any;
  output: any;
  throws?: string;
  times?: number;
}

interface FixtureDefinition {
  name: string;
  data: any;
  description: string;
}

interface ValidationResult {
  valid: boolean;
  errors: ValidationError[];
  warnings: ValidationWarning[];
  framework: string;
}

interface ValidationError {
  type: 'syntax' | 'structure' | 'import' | 'dependency';
  message: string;
  line?: number;
  column?: number;
  file: string;
}

interface ValidationWarning {
  type: 'coverage' | 'best_practice' | 'performance';
  message: string;
  file: string;
}

interface TestExecutionResult {
  passed: number;
  failed: number;
  skipped: number;
  total: number;
  duration: number;
  failures: TestFailure[];
  coverage?: CoverageReport;
  framework: string;
}

interface TestFailure {
  test: string;
  file: string;
  error: string;
  stack?: string;
  expected?: any;
  actual?: any;
}

interface CoverageReport {
  lines: CoverageMetric;
  functions: CoverageMetric;
  branches: CoverageMetric;
  statements: CoverageMetric;
}

interface CoverageMetric {
  total: number;
  covered: number;
  percentage: number;
}
```

### Implementation Strategy

**1. Test Generation Engine**:

```typescript
class TestFirstGenerator implements ITestFirstGenerator {
  constructor(
    private aiProvider: IAIProvider,
    private fileSystem: IFileSystem,
    private testRunner: ITestRunner,
    private config: TestFirstConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async generateTests(plan: DevelopmentPlan): Promise<TestSuite[]> {
    const startTime = Date.now();

    try {
      const testSuites: TestSuite[] = [];

      // Generate test suites for each file in the plan
      for (const fileChange of plan.files) {
        if (fileChange.testsRequired) {
          const testSuite = await this.generateTestSuite(fileChange, plan);
          testSuites.push(testSuite);
        }
      }

      // Organize test suites into files
      const testFiles = await this.organizeTestFiles(testSuites);

      // Write test files to filesystem
      await this.writeTestFiles(testFiles);

      // Validate test syntax
      const validation = await this.validateTestSyntax(testFiles);
      if (!validation.valid) {
        throw new TestGenerationError('Generated tests have validation errors', validation.errors);
      }

      // Execute tests to confirm they fail
      const executionResult = await this.executeTests(testFiles);
      const testsFail = await this.confirmTestsFail(executionResult);

      if (!testsFail) {
        throw new TestGenerationError(
          'Tests should fail but some passed - implementation may already exist',
          []
        );
      }

      await this.eventStore.append({
        type: 'TESTS.GENERATED.SUCCESS',
        tags: {
          issueId: plan.issueId,
          issueNumber: plan.issueNumber.toString(),
          planId: plan.id,
        },
        data: {
          testSuites: testSuites.length,
          testFiles: testFiles.length,
          totalTests: testSuites.reduce((sum, suite) => sum + suite.tests.length, 0),
          validationErrors: validation.errors.length,
          failedTests: executionResult.failed,
          generationTime: Date.now() - startTime,
        },
      });

      this.logger.info('Test-first generation completed', {
        issueNumber: plan.issueNumber,
        testSuites: testSuites.length,
        totalTests: testSuites.reduce((sum, suite) => sum + suite.tests.length, 0),
        failedTests: executionResult.failed,
      });

      return testSuites;
    } catch (error) {
      await this.eventStore.append({
        type: 'TESTS.GENERATED.FAILED',
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

  private async generateTestSuite(
    fileChange: FileChange,
    plan: DevelopmentPlan
  ): Promise<TestSuite> {
    const prompt = this.buildTestGenerationPrompt(fileChange, plan);

    try {
      const response = await this.aiProvider.sendMessage({
        messages: [{ role: 'user', content: prompt }],
        maxTokens: 2000,
        temperature: 0.3,
        responseFormat: { type: 'json_object' },
      });

      const suiteData = JSON.parse(response.content);

      return this.validateAndNormalizeTestSuite(suiteData, fileChange);
    } catch (error) {
      this.logger.error('AI test generation failed, using fallback', { error });
      return this.generateFallbackTestSuite(fileChange, plan);
    }
  }

  private buildTestGenerationPrompt(fileChange: FileChange, plan: DevelopmentPlan): string {
    return `
Generate comprehensive test cases for the following file change using Test-Driven Development principles.

File Change Details:
- Path: ${fileChange.path}
- Action: ${fileChange.action}
- Description: ${fileChange.description}
- Complexity: ${fileChange.estimatedComplexity}

Development Plan:
- Issue: ${plan.title}
- Summary: ${plan.summary}
- Approach: ${plan.approach.description}

Repository Context:
- Language: ${this.detectLanguage(fileChange.path)}
- Testing Framework: ${this.detectTestFramework(fileChange.path)}
- Test Directory: ${this.getTestDirectory(fileChange.path)}

Requirements:
1. Generate tests that WILL FAIL initially (no implementation exists)
2. Follow TDD red-green-refactor cycle
3. Cover all scenarios: happy path, edge cases, error conditions
4. Include proper setup, teardown, and mocking
5. Use descriptive test names and documentation
6. Follow project conventions and best practices

Generate a JSON test suite with this structure:
{
  "name": "Test suite name",
  "description": "What this test suite covers",
  "testType": "unit|integration|e2e|performance",
  "framework": "jest|vitest|mocha|jasmine|pytest",
  "tests": [
    {
      "id": "test-1",
      "name": "should handle valid input correctly",
      "description": "Test the main success scenario",
      "scenario": "When valid input is provided",
      "given": ["system is initialized", "valid input data"],
      "when": ["function is called with valid input"],
      "then": ["function returns expected result", "no errors are thrown"],
      "expected": {
        "type": "equals",
        "value": "expected result"
      },
      "tags": ["happy-path", "critical"],
      "priority": "critical"
    }
  ],
  "setup": ["before each test setup"],
  "teardown": ["after each test cleanup"],
  "mocks": [
    {
      "name": "mockService",
      "type": "function",
      "implementation": "jest.fn()",
      "behavior": [
        {
          "input": ["valid input"],
          "output": "mocked response"
        }
      ]
    }
  ],
  "fixtures": [
    {
      "name": "validInput",
      "data": { "key": "value" },
      "description": "Valid input for testing"
    }
  ]
}

Focus on:
1. Clear test scenarios that match requirements
2. Comprehensive edge case coverage
3. Proper error handling tests
4. Realistic mock implementations
5. Test data fixtures for consistency
6. Appropriate test organization and structure
    `.trim();
  }

  private validateAndNormalizeTestSuite(suiteData: any, fileChange: FileChange): TestSuite {
    if (!suiteData.tests || !Array.isArray(suiteData.tests)) {
      throw new Error('Test suite must contain tests array');
    }

    const framework = suiteData.framework || this.detectTestFramework(fileChange.path);
    const testType = suiteData.testType || this.inferTestType(fileChange);

    return {
      id: this.generateSuiteId(fileChange.path),
      name: suiteData.name || `Tests for ${fileChange.path}`,
      description: suiteData.description || `Test suite for ${fileChange.path}`,
      targetFile: fileChange.path,
      testType,
      framework,
      tests: suiteData.tests.map((test: any) => this.validateTestCase(test)),
      setup: suiteData.setup || [],
      teardown: suiteData.teardown || [],
      mocks: (suiteData.mocks || []).map((mock: any) => this.validateMock(mock)),
      fixtures: (suiteData.fixtures || []).map((fixture: any) => this.validateFixture(fixture)),
    };
  }

  private validateTestCase(test: any): TestCase {
    if (!test.name || !test.expected) {
      throw new Error('Test case must have name and expected result');
    }

    return {
      id: test.id || this.generateTestId(),
      name: test.name,
      description: test.description || '',
      scenario: test.scenario || '',
      given: Array.isArray(test.given) ? test.given : [],
      when: Array.isArray(test.when) ? test.when : [],
      then: Array.isArray(test.then) ? test.then : [],
      expected: this.validateExpectedResult(test.expected),
      tags: Array.isArray(test.tags) ? test.tags : [],
      priority: test.priority || 'medium',
    };
  }

  private validateExpectedResult(expected: any): ExpectedResult {
    const validTypes = ['throws', 'returns', 'equals', 'contains', 'matches', 'truthy', 'falsy'];

    if (!validTypes.includes(expected.type)) {
      throw new Error(`Invalid expected result type: ${expected.type}`);
    }

    return {
      type: expected.type,
      value: expected.value,
      errorType: expected.errorType,
      message: expected.message,
      properties: expected.properties || {},
    };
  }

  async organizeTestFiles(testSuites: TestSuite[]): Promise<TestFile[]> {
    const testFiles: TestFile[] = [];
    const filesByPath = new Map<string, TestSuite[]>();

    // Group test suites by target file
    for (const suite of testSuites) {
      const testFilePath = this.getTestFilePath(suite.targetFile, suite.framework);

      if (!filesByPath.has(testFilePath)) {
        filesByPath.set(testFilePath, []);
      }

      filesByPath.get(testFilePath)!.push(suite);
    }

    // Generate test file content for each group
    for (const [testFilePath, suites] of filesByPath) {
      const content = await this.generateTestFileContent(suites);
      const language = this.detectLanguage(testFilePath);

      testFiles.push({
        path: testFilePath,
        content,
        framework: suites[0].framework,
        language,
        dependencies: this.extractDependencies(suites),
        imports: this.extractImports(suites),
      });
    }

    return testFiles;
  }

  private async generateTestFileContent(suites: TestSuite[]): Promise<string> {
    const framework = suites[0].framework;
    const language = this.detectLanguage(suites[0].targetFile);

    switch (framework) {
      case 'jest':
      case 'vitest':
        return this.generateJestTestFile(suites, language);
      case 'mocha':
        return this.generateMochaTestFile(suites, language);
      case 'pytest':
        return this.generatePytestTestFile(suites, language);
      default:
        throw new Error(`Unsupported test framework: ${framework}`);
    }
  }

  private generateJestTestFile(suites: TestSuite[], language: string): string {
    const imports = new Set<string>();
    const mocks = new Map<string, MockDefinition>();
    const fixtures = new Map<string, FixtureDefinition>();

    let content = '';

    // Add imports
    for (const suite of suites) {
      for (const mock of suite.mocks) {
        mocks.set(mock.name, mock);
      }
      for (const fixture of suite.fixtures) {
        fixtures.set(fixture.name, fixture);
      }
    }

    // Generate imports
    if (language === 'typescript') {
      content += `import { describe, it, expect, beforeEach, afterEach, jest } from '@jest/globals';\n`;
    } else {
      content += `const { describe, it, expect, beforeEach, afterEach, jest } = require('@jest/globals');\n`;
    }

    // Add target module imports (these will fail initially)
    for (const suite of suites) {
      const importPath = this.getImportPath(suite.targetFile);
      content += `import { ${this.getExportNames(suite.targetFile)} } from '${importPath}';\n`;
    }

    content += '\n';

    // Add fixtures
    for (const [name, fixture] of fixtures) {
      content += `const ${name} = ${JSON.stringify(fixture.data, null, 2)}; // ${fixture.description}\n`;
    }

    content += '\n';

    // Generate test suites
    for (const suite of suites) {
      content += `describe('${suite.name}', () => {\n`;
      content += `  // ${suite.description}\n\n`;

      // Add setup
      for (const setup of suite.setup) {
        content += `  beforeEach(() => {\n    ${setup}\n  });\n`;
      }

      // Add teardown
      for (const teardown of suite.teardown) {
        content += `  afterEach(() => {\n    ${teardown}\n  });\n`;
      }

      content += '\n';

      // Add tests
      for (const test of suite.tests) {
        content += `  it('${test.name}', () => {\n`;
        content += `    // ${test.description}\n`;
        content += `    // Scenario: ${test.scenario}\n`;

        if (test.given.length > 0) {
          content += `    // Given: ${test.given.join(', ')}\n`;
        }

        if (test.when.length > 0) {
          content += `    // When: ${test.when.join(', ')}\n`;
        }

        if (test.then.length > 0) {
          content += `    // Then: ${test.then.join(', ')}\n`;
        }

        content += '\n';

        // Generate test implementation based on expected result
        content += this.generateTestImplementation(test, suite);

        content += `  });\n\n`;
      }

      content += `});\n\n`;
    }

    return content;
  }

  private generateTestImplementation(test: TestCase, suite: TestSuite): string {
    const { expected } = test;

    switch (expected.type) {
      case 'throws':
        return `    expect(() => {
      // TODO: Implement function call that should throw
      // throw new Error('${expected.errorType || 'Expected error'}');
    }).${expected.errorType ? `toThrow('${expected.errorType}')` : 'toThrow()'};`;

      case 'equals':
        return `    const result = // TODO: Call function under test
      // functionUnderTest(${test.given.join(', ')});
      expect(result).${expected.value !== undefined ? `toBe(${JSON.stringify(expected.value)})` : 'toBeDefined()'};`;

      case 'truthy':
        return `    const result = // TODO: Call function under test
      // functionUnderTest(${test.given.join(', ')});
      expect(result).toBeTruthy();`;

      case 'falsy':
        return `    const result = // TODO: Call function under test
      // functionUnderTest(${test.given.join(', ')});
      expect(result).toBeFalsy();`;

      case 'contains':
        return `    const result = // TODO: Call function under test
      // functionUnderTest(${test.given.join(', ')});
      expect(result).toContain(${JSON.stringify(expected.value)});`;

      case 'matches':
        return `    const result = // TODO: Call function under test
      // functionUnderTest(${test.given.join(', ')});
      expect(result).toMatch(${expected.value});`;

      default:
        return `    // TODO: Implement test for ${test.name}\n    expect(true).toBe(false); // Force failure until implemented`;
    }
  }

  async validateTestSyntax(testFiles: TestFile[]): Promise<ValidationResult> {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    for (const testFile of testFiles) {
      try {
        // Syntax validation using language-specific parsers
        const syntaxErrors = await this.validateSyntax(testFile);
        errors.push(...syntaxErrors);

        // Framework-specific validation
        const frameworkErrors = await this.validateFramework(testFile);
        errors.push(...frameworkErrors);

        // Best practices validation
        const practiceWarnings = await this.validateBestPractices(testFile);
        warnings.push(...practiceWarnings);
      } catch (error) {
        errors.push({
          type: 'syntax',
          message: `Failed to validate ${testFile.path}: ${error.message}`,
          file: testFile.path,
        });
      }
    }

    return {
      valid: errors.length === 0,
      errors,
      warnings,
      framework: testFiles[0]?.framework || 'unknown',
    };
  }

  async executeTests(testFiles: TestFile[]): Promise<TestExecutionResult> {
    try {
      return await this.testRunner.runTests(testFiles);
    } catch (error) {
      this.logger.error('Test execution failed', { error });

      // Return failure result
      return {
        passed: 0,
        failed: testFiles.reduce((sum, file) => sum + this.countTests(file.content), 0),
        skipped: 0,
        total: testFiles.reduce((sum, file) => sum + this.countTests(file.content), 0),
        duration: 0,
        failures: [
          {
            test: 'test-execution',
            file: 'test-runner',
            error: error.message,
          },
        ],
        framework: testFiles[0]?.framework || 'unknown',
      };
    }
  }

  async confirmTestsFail(results: TestExecutionResult): Promise<boolean> {
    // In TDD red phase, all tests should fail
    // However, some tests might pass if they're testing setup or configuration

    const passRate = results.total > 0 ? results.passed / results.total : 0;
    const maxAllowedPassRate = 0.1; // Allow up to 10% of tests to pass

    const testsFailAsExpected = passRate <= maxAllowedPassRate;

    if (!testsFailAsExpected) {
      this.logger.warn('Too many tests passing in red phase', {
        passed: results.passed,
        total: results.total,
        passRate: (passRate * 100).toFixed(1) + '%',
        maxAllowed: (maxAllowedPassRate * 100).toFixed(1) + '%',
      });
    }

    return testsFailAsExpected;
  }

  private async writeTestFiles(testFiles: TestFile[]): Promise<void> {
    for (const testFile of testFiles) {
      await this.fileSystem.writeFile(testFile.path, testFile.content);

      this.logger.debug('Test file written', {
        path: testFile.path,
        size: testFile.content.length,
        framework: testFile.framework,
      });
    }
  }

  private generateFallbackTestSuite(fileChange: FileChange, plan: DevelopmentPlan): TestSuite {
    // Basic fallback test suite when AI generation fails
    return {
      id: this.generateSuiteId(fileChange.path),
      name: `Fallback tests for ${fileChange.path}`,
      description: `Basic test suite generated as fallback for ${fileChange.path}`,
      targetFile: fileChange.path,
      testType: 'unit',
      framework: 'jest',
      tests: [
        {
          id: 'fallback-1',
          name: 'should be implemented',
          description: 'Placeholder test that will fail until implementation exists',
          scenario: 'Basic functionality test',
          given: ['system is ready'],
          when: ['function is called'],
          then: ['function should work correctly'],
          expected: { type: 'truthy' },
          tags: ['fallback'],
          priority: 'medium',
        },
      ],
      setup: [],
      teardown: [],
      mocks: [],
      fixtures: [],
    };
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

  private detectTestFramework(filePath: string): string {
    // Check for existing test configuration files
    const projectFiles = this.fileSystem.listFiles('.');

    if (projectFiles.includes('vitest.config.ts') || projectFiles.includes('vitest.config.js')) {
      return 'vitest';
    }

    if (projectFiles.includes('jest.config.js') || projectFiles.includes('jest.config.ts')) {
      return 'jest';
    }

    if (projectFiles.includes('mocha.opts') || projectFiles.includes('test/mocha.opts')) {
      return 'mocha';
    }

    if (projectFiles.includes('pytest.ini') || projectFiles.includes('pyproject.toml')) {
      return 'pytest';
    }

    // Default based on language
    const language = this.detectLanguage(filePath);
    const defaults: Record<string, string> = {
      typescript: 'vitest',
      javascript: 'vitest',
      python: 'pytest',
      java: 'jest',
      cpp: 'gtest',
      go: 'testing',
    };

    return defaults[language] || 'jest';
  }

  private getTestDirectory(sourcePath: string): string {
    const language = this.detectLanguage(sourcePath);

    const testDirs: Record<string, string> = {
      typescript: 'src/__tests__',
      javascript: 'src/__tests__',
      python: 'tests',
      java: 'src/test/java',
      cpp: 'test',
      go: 'pkg_test',
    };

    return testDirs[language] || 'tests';
  }

  private getTestFilePath(sourcePath: string, framework: string): string {
    const testDir = this.getTestDirectory(sourcePath);
    const fileName = sourcePath.split('/').pop() || '';
    const baseName = fileName.replace(/\.[^.]+$/, '');
    const ext = this.getTestFileExtension(framework);

    return `${testDir}/${baseName}.test${ext}`;
  }

  private getTestFileExtension(framework: string): string {
    const extensions: Record<string, string> = {
      jest: '.ts',
      vitest: '.ts',
      mocha: '.ts',
      pytest: '.py',
      gtest: '.cpp',
    };

    return extensions[framework] || '.ts';
  }

  private generateSuiteId(filePath: string): string {
    return `suite_${filePath.replace(/[^a-zA-Z0-9]/g, '_')}_${Date.now()}`;
  }

  private generateTestId(): string {
    return `test_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private countTests(content: string): number {
    // Count test functions in various frameworks
    const patterns = [
      /it\s*\(/g, // Jest/Vitest/Mocha
      /test\s*\(/g, // Jest
      /def test_/g, // Pytest
      /TEST\s*\(/g, // Google Test
    ];

    let count = 0;
    for (const pattern of patterns) {
      const matches = content.match(pattern);
      if (matches) {
        count += matches.length;
      }
    }

    return count;
  }
}
```

### Integration Points

**1. AI Provider Integration**:

- Test case generation using structured prompts
- Fallback to rule-based generation
- Framework-specific test patterns

**2. File System Integration**:

- Writing test files to appropriate directories
- Reading existing test configurations
- Managing test file organization

**3. Test Runner Integration**:

- Executing generated tests
- Collecting test results and coverage
- Framework-specific execution

**4. Event Store Integration**:

- `TESTS.GENERATED.SUCCESS/FAILED`
- Complete audit trail for test generation

### Testing Strategy

**Unit Tests**:

```typescript
describe('TestFirstGenerator', () => {
  let generator: TestFirstGenerator;
  let mockAIProvider: jest.Mocked<IAIProvider>;
  let mockFileSystem: jest.Mocked<IFileSystem>;
  let mockTestRunner: jest.Mocked<ITestRunner>;

  beforeEach(() => {
    mockAIProvider = createMockAIProvider();
    mockFileSystem = createMockFileSystem();
    mockTestRunner = createMockTestRunner();
    generator = new TestFirstGenerator(
      mockAIProvider,
      mockFileSystem,
      mockTestRunner,
      createMockConfig(),
      mockLogger,
      mockEventStore
    );
  });

  describe('generateTests', () => {
    it('should generate failing tests for TDD red phase', async () => {
      const plan = createMockDevelopmentPlan();
      plan.files = [
        {
          path: 'src/utils/auth.ts',
          action: 'create',
          description: 'Authentication utility functions',
          estimatedComplexity: 'medium',
          testsRequired: true,
        },
      ];

      mockAIProvider.sendMessage.mockResolvedValue({
        content: JSON.stringify(createMockTestSuiteData()),
        usage: { tokens: 1000 },
      });

      mockFileSystem.writeFile.mockResolvedValue();
      mockTestRunner.runTests.mockResolvedValue({
        passed: 0,
        failed: 3,
        skipped: 0,
        total: 3,
        duration: 100,
        failures: [],
        framework: 'vitest',
      });

      const result = await generator.generateTests(plan);

      expect(result).toHaveLength(1);
      expect(result[0].tests).toHaveLength(3);
      expect(mockFileSystem.writeFile).toHaveBeenCalled();
      expect(mockTestRunner.runTests).toHaveBeenCalled();
    });

    it('should handle AI generation failure with fallback', async () => {
      const plan = createMockDevelopmentPlan();

      mockAIProvider.sendMessage.mockRejectedValue(new Error('AI unavailable'));

      const result = await generator.generateTests(plan);

      expect(result[0].name).toContain('Fallback');
      expect(result[0].tests).toHaveLength(1);
      expect(result[0].tests[0].name).toBe('should be implemented');
    });
  });

  describe('confirmTestsFail', () => {
    it('should confirm tests fail as expected', async () => {
      const results: TestExecutionResult = {
        passed: 0,
        failed: 5,
        skipped: 0,
        total: 5,
        duration: 200,
        failures: [],
        framework: 'vitest',
      };

      const confirmed = await generator.confirmTestsFail(results);

      expect(confirmed).toBe(true);
    });

    it('should reject when too many tests pass', async () => {
      const results: TestExecutionResult = {
        passed: 3,
        failed: 2,
        skipped: 0,
        total: 5,
        duration: 200,
        failures: [],
        framework: 'vitest',
      };

      const confirmed = await generator.confirmTestsFail(results);

      expect(confirmed).toBe(false);
    });
  });
});
```

### Configuration Examples

**Test-First Configuration**:

```yaml
test_first:
  ai_generation:
    model: 'claude-3-sonnet'
    max_tokens: 2000
    temperature: 0.3
    fallback_enabled: true

  frameworks:
    typescript: 'vitest'
    javascript: 'vitest'
    python: 'pytest'
    java: 'jest'
    cpp: 'gtest'

  test_organization:
    test_directories:
      typescript: 'src/__tests__'
      javascript: 'src/__tests__'
      python: 'tests'
      java: 'src/test/java'

    file_naming: '{filename}.test.{ext}'

  validation:
    syntax_check: true
    framework_validation: true
    best_practices: true

  execution:
    max_pass_rate_red_phase: 0.1 # Allow 10% of tests to pass
    timeout_seconds: 30
    coverage_collection: false # Skip coverage in red phase

  fallback:
    generate_basic_tests: true
    include_placeholders: true
    min_tests_per_file: 1
```

---

## Implementation Notes

**Key Considerations**:

1. **TDD Principles**: Tests must genuinely fail before implementation exists.

2. **Framework Detection**: Automatically detect and use project's existing testing framework.

3. **Test Organization**: Follow project conventions for test file structure and naming.

4. **Comprehensive Coverage**: Generate tests for happy paths, edge cases, and error conditions.

5. **Mock Strategy**: Generate appropriate mocks for external dependencies.

6. **Validation**: Ensure generated tests have valid syntax and structure.

**Performance Targets**:

- Test generation: < 30 seconds per file
- Syntax validation: < 5 seconds
- Test execution: < 60 seconds
- Total red phase: < 2 minutes per file

**Security Considerations**:

- Validate generated test code to prevent injection attacks
- Sanitize test data and fixtures
- Handle sensitive data appropriately in tests
- Use appropriate mocking for external services

**TDD Best Practices**:

- Write descriptive test names that explain behavior
- Use Given-When-Then structure for clarity
- Test one behavior per test case
- Include setup and teardown for proper isolation
- Generate realistic test data and fixtures
- Focus on behavior over implementation details

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
