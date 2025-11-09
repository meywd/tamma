# Story 2.7: Code Refactoring Pass

**Epic**: Epic 2 - Autonomous Development Loop - Core  
**Status**: Ready for Development  
**Priority**: Medium  
**Prerequisites**: Story 2.6 (implementation code generation must complete first)

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
I want the system to perform optional code refactoring to improve quality and maintainability,
So that the codebase remains clean and follows best practices while maintaining functionality.

---

## Acceptance Criteria

1. System analyzes generated code for refactoring opportunities
2. Refactoring focuses on code quality, performance, and maintainability improvements
3. All tests must continue to pass after refactoring
4. Refactoring is optional and can be configured or skipped
5. System validates refactoring doesn't break functionality
6. Refactoring changes and test results logged to event trail
7. Integration test validates refactoring workflow
8. Rollback capability if refactoring introduces issues

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

**CodeRefactorer Service**:

```typescript
interface ICodeRefactorer {
  analyzeRefactoringOpportunities(files: GeneratedFile[]): Promise<RefactoringOpportunity[]>;
  applyRefactoring(opportunities: RefactoringOpportunity[]): Promise<RefactoringResult>;
  validateRefactoring(files: GeneratedFile[]): Promise<ValidationResult>;
  runTests(testSuites: TestSuite[]): Promise<TestExecutionResult>;
  rollbackChanges(changes: RefactoringChange[]): Promise<void>;
}

interface RefactoringOpportunity {
  id: string;
  type: RefactoringType;
  file: string;
  description: string;
  impact: 'low' | 'medium' | 'high';
  effort: 'low' | 'medium' | 'high';
  priority: number;
  automated: boolean;
  risk: 'low' | 'medium' | 'high';
  before: CodeSnippet;
  after: CodeSnippet;
  reasoning: string;
  benefits: string[];
  tradeoffs: string[];
}

type RefactoringType =
  | 'extract_method'
  | 'extract_variable'
  | 'inline_variable'
  | 'rename_variable'
  | 'rename_method'
  | 'move_method'
  | 'extract_class'
  | 'inline_class'
  | 'move_class'
  | 'extract_interface'
  | 'remove_dead_code'
  | 'simplify_conditional'
  | 'replace_conditional_with_polymorphism'
  | 'replace_magic_number'
  | 'introduce_parameter_object'
  | 'replace_array_with_object'
  | 'decompose_conditional'
  | 'consolidate_conditional'
  | 'replace_nested_conditional_with_guard_clauses'
  | 'replace_parameter_with_method'
  | 'preserve_whole_object'
  | 'replace_type_code_with_subclasses'
  | 'replace_subclass_with_fields'
  | 'extract_superclass'
  | 'extract_delegate'
  | 'introduce_foreign_method'
  | 'introduce_local_extension'
  | 'replace_inheritance_with_delegation'
  | 'replace_delegation_with_inheritance'
  | 'optimize_imports'
  | 'remove_unused_imports'
  | 'organize_imports'
  | 'format_code'
  | 'add_type_annotations'
  | 'remove_redundant_code'
  | 'simplify_expression'
  | 'optimize_loops'
  | 'reduce_complexity'
  | 'improve_naming'
  | 'add_documentation'
  | 'extract_constants';

interface RefactoringResult {
  applied: RefactoringOpportunity[];
  skipped: RefactoringOpportunity[];
  failed: RefactoringOpportunity[];
  changes: RefactoringChange[];
  metrics: RefactoringMetrics;
  duration: number;
  rollbackAvailable: boolean;
}

interface RefactoringChange {
  id: string;
  file: string;
  type: 'add' | 'remove' | 'modify' | 'move';
  path: string;
  oldContent?: string;
  newContent?: string;
  oldPath?: string;
  newPath?: string;
  timestamp: Date;
}

interface RefactoringMetrics {
  opportunitiesAnalyzed: number;
  opportunitiesApplied: number;
  linesChanged: number;
  complexityReduction: number;
  maintainabilityImprovement: number;
  testCoverageChange: number;
  duplicateCodeReduction: number;
  performanceImprovement: number;
}

interface CodeSnippet {
  content: string;
  lineStart: number;
  lineEnd: number;
  language: string;
}

interface RefactoringConfig {
  enabled: boolean;
  maxRiskLevel: 'low' | 'medium' | 'high';
  maxEffortLevel: 'low' | 'medium' | 'high';
  minPriority: number;
  automatedOnly: boolean;
  preserveTests: boolean;
  backupChanges: boolean;
  types: RefactoringTypeConfig[];
}

interface RefactoringTypeConfig {
  type: RefactoringType;
  enabled: boolean;
  maxRisk: 'low' | 'medium' | 'high';
  maxEffort: 'low' | 'medium' | 'high';
  priority: number;
  automated: boolean;
}
```

### Implementation Strategy

**1. Refactoring Analysis Engine**:

```typescript
class CodeRefactorer implements ICodeRefactorer {
  constructor(
    private aiProvider: IAIProvider,
    private codeAnalyzer: ICodeAnalyzer,
    private testRunner: ITestRunner,
    private fileSystem: IFileSystem,
    private config: RefactoringConfig,
    private logger: Logger,
    private eventStore: IEventStore
  ) {}

  async analyzeRefactoringOpportunities(files: GeneratedFile[]): Promise<RefactoringOpportunity[]> {
    const startTime = Date.now();
    const opportunities: RefactoringOpportunity[] = [];

    try {
      for (const file of files) {
        const fileOpportunities = await this.analyzeFile(file);
        opportunities.push(...fileOpportunities);
      }

      // Filter and prioritize opportunities
      const filteredOpportunities = this.filterOpportunities(opportunities);
      const prioritizedOpportunities = this.prioritizeOpportunities(filteredOpportunities);

      await this.eventStore.append({
        type: 'REFACTORING.ANALYSIS.SUCCESS',
        tags: {
          filesAnalyzed: files.length.toString(),
          opportunitiesFound: opportunities.length.toString(),
          opportunitiesFiltered: filteredOpportunities.length.toString(),
        },
        data: {
          analysisTime: Date.now() - startTime,
          opportunitiesByType: this.groupOpportunitiesByType(prioritizedOpportunities),
        },
      });

      this.logger.info('Refactoring analysis completed', {
        filesAnalyzed: files.length,
        opportunitiesFound: opportunities.length,
        opportunitiesFiltered: filteredOpportunities.length,
      });

      return prioritizedOpportunities;
    } catch (error) {
      await this.eventStore.append({
        type: 'REFACTORING.ANALYSIS.FAILED',
        tags: {
          filesAnalyzed: files.length.toString(),
        },
        data: {
          error: error.message,
          analysisTime: Date.now() - startTime,
        },
      });

      throw error;
    }
  }

  private async analyzeFile(file: GeneratedFile): Promise<RefactoringOpportunity[]> {
    const opportunities: RefactoringOpportunity[] = [];

    try {
      // Analyze code structure and metrics
      const analysis = await this.codeAnalyzer.analyze(file);

      // Run different refactoring analyzers
      for (const typeConfig of this.config.types) {
        if (!typeConfig.enabled) continue;

        const typeOpportunities = await this.analyzeRefactoringType(file, analysis, typeConfig);
        opportunities.push(...typeOpportunities);
      }
    } catch (error) {
      this.logger.warn('Failed to analyze file for refactoring', {
        file: file.path,
        error: error.message,
      });
    }

    return opportunities;
  }

  private async analyzeRefactoringType(
    file: GeneratedFile,
    analysis: CodeAnalysis,
    typeConfig: RefactoringTypeConfig
  ): Promise<RefactoringOpportunity[]> {
    switch (typeConfig.type) {
      case 'extract_method':
        return this.analyzeExtractMethod(file, analysis, typeConfig);
      case 'extract_variable':
        return this.analyzeExtractVariable(file, analysis, typeConfig);
      case 'simplify_conditional':
        return this.analyzeSimplifyConditional(file, analysis, typeConfig);
      case 'remove_dead_code':
        return this.analyzeRemoveDeadCode(file, analysis, typeConfig);
      case 'optimize_imports':
        return this.analyzeOptimizeImports(file, analysis, typeConfig);
      case 'improve_naming':
        return this.analyzeImproveNaming(file, analysis, typeConfig);
      case 'add_documentation':
        return this.analyzeAddDocumentation(file, analysis, typeConfig);
      case 'reduce_complexity':
        return this.analyzeReduceComplexity(file, analysis, typeConfig);
      default:
        return this.analyzeWithAI(file, analysis, typeConfig);
    }
  }

  private async analyzeWithAI(
    file: GeneratedFile,
    analysis: CodeAnalysis,
    typeConfig: RefactoringTypeConfig
  ): Promise<RefactoringOpportunity[]> {
    const prompt = this.buildAIRefactoringPrompt(file, analysis, typeConfig);

    try {
      const response = await this.aiProvider.sendMessage({
        messages: [{ role: 'user', content: prompt }],
        maxTokens: 2000,
        temperature: 0.2,
        responseFormat: { type: 'json_object' },
      });

      const opportunitiesData = JSON.parse(response.content);

      return this.validateAndNormalizeOpportunities(opportunitiesData, file, typeConfig);
    } catch (error) {
      this.logger.warn('AI refactoring analysis failed', {
        file: file.path,
        type: typeConfig.type,
        error: error.message,
      });

      return [];
    }
  }

  private buildAIRefactoringPrompt(
    file: GeneratedFile,
    analysis: CodeAnalysis,
    typeConfig: RefactoringTypeConfig
  ): string {
    return `
Analyze the following code for ${typeConfig.type} refactoring opportunities.

File: ${file.path}
Language: ${file.language}

Code Content:
\`\`\`${file.language}
${file.content}
\`\`\`

Code Analysis:
- Lines of Code: ${analysis.linesOfCode}
- Cyclomatic Complexity: ${analysis.cyclomaticComplexity}
- Maintainability Index: ${analysis.maintainabilityIndex}
- Test Coverage: ${analysis.testCoverage}%
- Duplicate Lines: ${analysis.duplicateLines}
- Functions: ${analysis.functions.length}
- Classes: ${analysis.classes.length}

Refactoring Type: ${typeConfig.type}
Max Risk Level: ${typeConfig.maxRisk}
Max Effort Level: ${typeConfig.maxEffort}
Automated: ${typeConfig.automated}

Requirements:
1. Identify specific refactoring opportunities of type: ${typeConfig.type}
2. For each opportunity, provide before/after code snippets
3. Assess impact, effort, risk, and priority
4. Explain reasoning and benefits
5. Consider trade-offs and potential issues
6. Ensure refactoring maintains functionality
7. Follow language-specific best practices

Generate a JSON response with this structure:
{
  "opportunities": [
    {
      "type": "${typeConfig.type}",
      "description": "Clear description of the refactoring",
      "impact": "low|medium|high",
      "effort": "low|medium|high",
      "priority": 1-10,
      "automated": true,
      "risk": "low|medium|high",
      "before": {
        "content": "original code snippet",
        "lineStart": 10,
        "lineEnd": 20,
        "language": "${file.language}"
      },
      "after": {
        "content": "refactored code snippet",
        "lineStart": 10,
        "lineEnd": 18,
        "language": "${file.language}"
      },
      "reasoning": "Why this refactoring is beneficial",
      "benefits": ["benefit1", "benefit2"],
      "tradeoffs": ["tradeoff1", "tradeoff2"]
    }
  ]
}

Focus on:
1. High-impact, low-risk refactoring opportunities
2. Code quality and maintainability improvements
3. Performance optimizations where applicable
4. Readability and understandability enhancements
5. Adherence to SOLID principles and design patterns
6. Reduction of code duplication and complexity
7. Improvement of testability and modularity
    `.trim();
  }

  private analyzeExtractMethod(
    file: GeneratedFile,
    analysis: CodeAnalysis,
    typeConfig: RefactoringTypeConfig
  ): RefactoringOpportunity[] {
    const opportunities: RefactoringOpportunity[] = [];

    // Find long methods that can be extracted
    for (const func of analysis.functions) {
      if (func.linesOfCode > 20 && func.cyclomaticComplexity > 5) {
        const lines = file.content.split('\n');
        const methodLines = lines.slice(func.lineStart - 1, func.lineEnd);
        const methodContent = methodLines.join('\n');

        // Look for extractable code blocks
        const extractableBlocks = this.findExtractableBlocks(methodContent);

        for (const block of extractableBlocks) {
          opportunities.push({
            id: this.generateOpportunityId(),
            type: 'extract_method',
            file: file.path,
            description: `Extract method from ${func.name} - ${block.description}`,
            impact: this.assessImpact(block.complexity, block.length),
            effort: this.assessEffort(block.length, block.dependencies),
            priority: this.calculatePriority(block.complexity, block.length, func.importance),
            automated: typeConfig.automated,
            risk: this.assessRisk(func.testCoverage, block.complexity),
            before: {
              content: block.content,
              lineStart: func.lineStart + block.startLine,
              lineEnd: func.lineStart + block.endLine,
              language: file.language,
            },
            after: {
              content: this.generateExtractedMethod(block, func.name),
              lineStart: func.lineStart + block.startLine,
              lineEnd: func.lineStart + block.endLine - 2,
              language: file.language,
            },
            reasoning: `Extracting this block reduces method complexity from ${func.cyclomaticComplexity} to ${func.cyclomaticComplexity - block.complexity + 1}`,
            benefits: [
              'Reduced method complexity',
              'Improved readability',
              'Better testability',
              'Code reusability',
            ],
            tradeoffs: ['Additional method call overhead', 'Increased number of methods'],
          });
        }
      }
    }

    return opportunities;
  }

  private analyzeSimplifyConditional(
    file: GeneratedFile,
    analysis: CodeAnalysis,
    typeConfig: RefactoringTypeConfig
  ): RefactoringOpportunity[] {
    const opportunities: RefactoringOpportunity[] = [];

    // Find complex conditional statements
    const complexConditionals = this.findComplexConditionals(file.content);

    for (const conditional of complexConditionals) {
      const simplification = this.simplifyConditional(conditional);

      if (simplification) {
        opportunities.push({
          id: this.generateOpportunityId(),
          type: 'simplify_conditional',
          file: file.path,
          description: `Simplify complex conditional at line ${conditional.line}`,
          impact: 'medium',
          effort: 'low',
          priority: 7,
          automated: typeConfig.automated,
          risk: 'low',
          before: {
            content: conditional.content,
            lineStart: conditional.line,
            lineEnd: conditional.line,
            language: file.language,
          },
          after: {
            content: simplification,
            lineStart: conditional.line,
            lineEnd: conditional.line,
            language: file.language,
          },
          reasoning: 'Complex conditional logic is hard to understand and maintain',
          benefits: [
            'Improved readability',
            'Reduced cognitive complexity',
            'Easier testing',
            'Better maintainability',
          ],
          tradeoffs: ['May require additional helper methods', 'Slightly more lines of code'],
        });
      }
    }

    return opportunities;
  }

  private analyzeRemoveDeadCode(
    file: GeneratedFile,
    analysis: CodeAnalysis,
    typeConfig: RefactoringTypeConfig
  ): RefactoringOpportunity[] {
    const opportunities: RefactoringOpportunity[] = [];

    // Find unused functions, variables, and imports
    const unusedCode = this.findUnusedCode(file, analysis);

    for (const unused of unusedCode) {
      opportunities.push({
        id: this.generateOpportunityId(),
        type: 'remove_dead_code',
        file: file.path,
        description: `Remove unused ${unused.type}: ${unused.name}`,
        impact: 'low',
        effort: 'low',
        priority: 5,
        automated: typeConfig.automated,
        risk: 'low',
        before: {
          content: unused.content,
          lineStart: unused.lineStart,
          lineEnd: unused.lineEnd,
          language: file.language,
        },
        after: {
          content: '',
          lineStart: unused.lineStart,
          lineEnd: unused.lineEnd,
          language: file.language,
        },
        reasoning: `${unused.type} is never used in the codebase`,
        benefits: [
          'Reduced code size',
          'Improved clarity',
          'Eliminated confusion',
          'Faster compilation',
        ],
        tradeoffs: ['Code might be used in future', 'May break dynamic references'],
      });
    }

    return opportunities;
  }

  async applyRefactoring(opportunities: RefactoringOpportunity[]): Promise<RefactoringResult> {
    const startTime = Date.now();
    const applied: RefactoringOpportunity[] = [];
    const skipped: RefactoringOpportunity[] = [];
    const failed: RefactoringOpportunity[] = [];
    const changes: RefactoringChange[] = [];

    try {
      // Create backup if configured
      if (this.config.backupChanges) {
        await this.createBackup(opportunities);
      }

      // Apply refactoring opportunities in priority order
      const sortedOpportunities = opportunities.sort((a, b) => b.priority - a.priority);

      for (const opportunity of sortedOpportunities) {
        try {
          // Check if opportunity should be applied
          if (!this.shouldApplyOpportunity(opportunity)) {
            skipped.push(opportunity);
            continue;
          }

          // Apply the refactoring
          const change = await this.applySingleRefactoring(opportunity);
          changes.push(change);
          applied.push(opportunity);

          this.logger.debug('Refactoring applied', {
            type: opportunity.type,
            file: opportunity.file,
            description: opportunity.description,
          });
        } catch (error) {
          this.logger.warn('Failed to apply refactoring', {
            type: opportunity.type,
            file: opportunity.file,
            error: error.message,
          });

          failed.push(opportunity);

          // If rollback is available, revert applied changes
          if (this.config.backupChanges && changes.length > 0) {
            await this.rollbackChanges(changes);
            return {
              applied: [],
              skipped: [],
              failed: [opportunity],
              changes: [],
              metrics: this.calculateMetrics([], [], []),
              duration: Date.now() - startTime,
              rollbackAvailable: true,
            };
          }
        }
      }

      // Validate refactoring results
      if (applied.length > 0) {
        const validation = await this.validateRefactoringResults(applied);
        if (!validation.valid) {
          this.logger.error('Refactoring validation failed', {
            errors: validation.errors,
          });

          // Rollback if validation fails
          if (this.config.backupChanges) {
            await this.rollbackChanges(changes);
            applied.length = 0; // Clear applied
            failed.push(...applied);
          }
        }
      }

      // Calculate metrics
      const metrics = this.calculateMetrics(applied, skipped, failed);

      await this.eventStore.append({
        type: 'REFACTORING.APPLIED.SUCCESS',
        tags: {
          opportunitiesApplied: applied.length.toString(),
          opportunitiesSkipped: skipped.length.toString(),
          opportunitiesFailed: failed.length.toString(),
        },
        data: {
          metrics,
          duration: Date.now() - startTime,
          rollbackAvailable: this.config.backupChanges,
        },
      });

      this.logger.info('Refactoring completed', {
        applied: applied.length,
        skipped: skipped.length,
        failed: failed.length,
        duration: Date.now() - startTime,
      });

      return {
        applied,
        skipped,
        failed,
        changes,
        metrics,
        duration: Date.now() - startTime,
        rollbackAvailable: this.config.backupChanges,
      };
    } catch (error) {
      await this.eventStore.append({
        type: 'REFACTORING.APPLIED.FAILED',
        tags: {},
        data: {
          error: error.message,
          duration: Date.now() - startTime,
        },
      });

      throw error;
    }
  }

  private async applySingleRefactoring(
    opportunity: RefactoringOpportunity
  ): Promise<RefactoringChange> {
    const fileContent = await this.fileSystem.readFile(opportunity.file);
    const lines = fileContent.split('\n');

    // Apply the refactoring based on type
    let newContent: string;
    switch (opportunity.type) {
      case 'extract_method':
        newContent = this.applyExtractMethod(lines, opportunity);
        break;
      case 'simplify_conditional':
        newContent = this.applySimplifyConditional(lines, opportunity);
        break;
      case 'remove_dead_code':
        newContent = this.applyRemoveDeadCode(lines, opportunity);
        break;
      case 'optimize_imports':
        newContent = this.applyOptimizeImports(lines, opportunity);
        break;
      default:
        throw new Error(`Unsupported refactoring type: ${opportunity.type}`);
    }

    // Write the refactored content
    await this.fileSystem.writeFile(opportunity.file, newContent);

    return {
      id: this.generateChangeId(),
      file: opportunity.file,
      type: 'modify',
      path: opportunity.file,
      oldContent: fileContent,
      newContent,
      timestamp: new Date(),
    };
  }

  private applyExtractMethod(lines: string[], opportunity: RefactoringOpportunity): string {
    const { before, after } = opportunity;

    // Replace the original code with method call
    const methodCall = this.generateMethodCall(after.content);

    // Insert the extracted method
    const insertPosition = this.findMethodInsertPosition(lines, opportunity);

    const newLines = [...lines];
    newLines.splice(before.lineStart - 1, before.lineEnd - before.lineStart + 1, methodCall);
    newLines.splice(insertPosition, 0, '', after.content, '');

    return newLines.join('\n');
  }

  private applyRemoveDeadCode(lines: string[], opportunity: RefactoringOpportunity): string {
    const { before } = opportunity;

    // Remove the dead code lines
    const newLines = [...lines];
    newLines.splice(before.lineStart - 1, before.lineEnd - before.lineStart + 1);

    return newLines.join('\n');
  }

  private async validateRefactoringResults(
    applied: RefactoringOpportunity[]
  ): Promise<ValidationResult> {
    // Run tests to ensure functionality is preserved
    const testResults = await this.runTests([]);

    if (testResults.failed > 0) {
      return {
        valid: false,
        errors: testResults.failures.map((failure) => ({
          type: 'test_failure',
          message: failure.error,
          file: failure.file,
          line: undefined,
        })),
        warnings: [],
        suggestions: [],
      };
    }

    // Additional validation can be added here
    return {
      valid: true,
      errors: [],
      warnings: [],
      suggestions: [],
    };
  }

  async runTests(testSuites: TestSuite[]): Promise<TestExecutionResult> {
    try {
      return await this.testRunner.runAllTests();
    } catch (error) {
      this.logger.error('Test execution failed during refactoring', { error });

      return {
        passed: 0,
        failed: 1,
        skipped: 0,
        total: 1,
        duration: 0,
        failures: [
          {
            test: 'refactoring-validation',
            file: 'test-runner',
            error: error.message,
          },
        ],
        framework: 'unknown',
      };
    }
  }

  async rollbackChanges(changes: RefactoringChange[]): Promise<void> {
    this.logger.info('Rolling back refactoring changes', {
      changesCount: changes.length,
    });

    for (const change of changes) {
      try {
        if (change.oldContent) {
          await this.fileSystem.writeFile(change.file, change.oldContent);
        }
      } catch (error) {
        this.logger.error('Failed to rollback change', {
          file: change.file,
          error: error.message,
        });
      }
    }

    await this.eventStore.append({
      type: 'REFACTORING.ROLLED_BACK',
      tags: {
        changesCount: changes.length.toString(),
      },
      data: {
        rolledBackAt: new Date().toISOString(),
      },
    });
  }

  private filterOpportunities(opportunities: RefactoringOpportunity[]): RefactoringOpportunity[] {
    return opportunities.filter((opportunity) => {
      // Filter by risk level
      if (this.compareRiskLevel(opportunity.risk, this.config.maxRiskLevel) > 0) {
        return false;
      }

      // Filter by effort level
      if (this.compareEffortLevel(opportunity.effort, this.config.maxEffortLevel) > 0) {
        return false;
      }

      // Filter by priority
      if (opportunity.priority < this.config.minPriority) {
        return false;
      }

      // Filter by automation requirement
      if (this.config.automatedOnly && !opportunity.automated) {
        return false;
      }

      return true;
    });
  }

  private prioritizeOpportunities(
    opportunities: RefactoringOpportunity[]
  ): RefactoringOpportunity[] {
    return opportunities.sort((a, b) => {
      // Sort by priority (descending), then by effort (ascending), then by risk (ascending)
      if (a.priority !== b.priority) {
        return b.priority - a.priority;
      }

      if (a.effort !== b.effort) {
        return this.compareEffortLevel(a.effort, b.effort);
      }

      return this.compareRiskLevel(a.risk, b.risk);
    });
  }

  private shouldApplyOpportunity(opportunity: RefactoringOpportunity): boolean {
    // Additional checks can be added here
    return opportunity.automated || !this.config.automatedOnly;
  }

  private generateOpportunityId(): string {
    return `refactor_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private generateChangeId(): string {
    return `change_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private compareRiskLevel(risk1: string, risk2: string): number {
    const levels = { low: 1, medium: 2, high: 3 };
    return levels[risk1 as keyof typeof levels] - levels[risk2 as keyof typeof levels];
  }

  private compareEffortLevel(effort1: string, effort2: string): number {
    const levels = { low: 1, medium: 2, high: 3 };
    return levels[effort1 as keyof typeof levels] - levels[effort2 as keyof typeof levels];
  }

  private calculateMetrics(
    applied: RefactoringOpportunity[],
    skipped: RefactoringOpportunity[],
    failed: RefactoringOpportunity[]
  ): RefactoringMetrics {
    return {
      opportunitiesAnalyzed: applied.length + skipped.length + failed.length,
      opportunitiesApplied: applied.length,
      linesChanged: applied.reduce((sum, opp) => {
        const beforeLines = opp.before.lineEnd - opp.before.lineStart + 1;
        const afterLines = opp.after.lineEnd - opp.after.lineStart + 1;
        return sum + Math.abs(afterLines - beforeLines);
      }, 0),
      complexityReduction: applied.reduce(
        (sum, opp) => sum + (opp.impact === 'high' ? 3 : opp.impact === 'medium' ? 2 : 1),
        0
      ),
      maintainabilityImprovement: applied.length * 2, // Simplified calculation
      testCoverageChange: 0, // Should be calculated from test results
      duplicateCodeReduction: applied.filter((opp) => opp.type === 'extract_method').length,
      performanceImprovement: applied.filter((opp) => opp.type.includes('optimize')).length,
    };
  }
}
```

### Integration Points

**1. AI Provider Integration**:

- Intelligent refactoring opportunity detection
- Code transformation suggestions
- Impact and risk assessment

**2. Code Analysis Integration**:

- Static analysis for code quality metrics
- Complexity and maintainability analysis
- Dead code detection

**3. Test Runner Integration**:

- Validation that refactoring preserves functionality
- Regression testing
- Coverage analysis

**4. File System Integration**:

- Applying code changes
- Creating backups for rollback
- Managing file operations

### Testing Strategy

**Unit Tests**:

```typescript
describe('CodeRefactorer', () => {
  let refactorer: CodeRefactorer;
  let mockAIProvider: jest.Mocked<IAIProvider>;
  let mockCodeAnalyzer: jest.Mocked<ICodeAnalyzer>;
  let mockTestRunner: jest.Mocked<ITestRunner>;
  let mockFileSystem: jest.Mocked<IFileSystem>;

  beforeEach(() => {
    mockAIProvider = createMockAIProvider();
    mockCodeAnalyzer = createMockCodeAnalyzer();
    mockTestRunner = createMockTestRunner();
    mockFileSystem = createMockFileSystem();
    refactorer = new CodeRefactorer(
      mockAIProvider,
      mockCodeAnalyzer,
      mockTestRunner,
      mockFileSystem,
      createMockRefactoringConfig(),
      mockLogger,
      mockEventStore
    );
  });

  describe('analyzeRefactoringOpportunities', () => {
    it('should identify extract method opportunities', async () => {
      const file = createMockGeneratedFile({
        content: `
function longMethod() {
  // Setup
  const data = fetchData();
  const config = loadConfig();
  
  // Complex logic that could be extracted
  for (let i = 0; i < data.length; i++) {
    if (data[i].active) {
      processItem(data[i], config);
    }
  }
  
  // Cleanup
  saveResults();
}
        `.trim(),
        language: 'typescript',
      });

      mockCodeAnalyzer.analyze.mockResolvedValue(
        createMockCodeAnalysis({
          functions: [
            {
              name: 'longMethod',
              linesOfCode: 15,
              cyclomaticComplexity: 6,
              testCoverage: 80,
              importance: 'high',
            },
          ],
        })
      );

      const opportunities = await refactorer.analyzeRefactoringOpportunities([file]);

      expect(opportunities).toContainEqual(
        expect.objectContaining({
          type: 'extract_method',
          automated: true,
          impact: expect.any(String),
        })
      );
    });

    it('should identify dead code removal opportunities', async () => {
      const file = createMockGeneratedFile({
        content: `
const usedFunction = () => console.log('used');
const unusedFunction = () => console.log('unused');

usedFunction();
        `.trim(),
        language: 'typescript',
      });

      mockCodeAnalyzer.analyze.mockResolvedValue(createMockCodeAnalysis());

      const opportunities = await refactorer.analyzeRefactoringOpportunities([file]);

      expect(opportunities).toContainEqual(
        expect.objectContaining({
          type: 'remove_dead_code',
          description: expect.stringContaining('unusedFunction'),
        })
      );
    });
  });

  describe('applyRefactoring', () => {
    it('should apply safe refactoring opportunities', async () => {
      const opportunities = [
        createMockRefactoringOpportunity({
          type: 'remove_dead_code',
          automated: true,
          risk: 'low',
        }),
      ];

      mockFileSystem.readFile.mockReturnValue('const unused = true;');
      mockFileSystem.writeFile.mockResolvedValue();
      mockTestRunner.runAllTests.mockResolvedValue({
        passed: 5,
        failed: 0,
        skipped: 0,
        total: 5,
        duration: 100,
        failures: [],
        framework: 'vitest',
      });

      const result = await refactorer.applyRefactoring(opportunities);

      expect(result.applied).toHaveLength(1);
      expect(result.failed).toHaveLength(0);
      expect(mockFileSystem.writeFile).toHaveBeenCalled();
      expect(mockTestRunner.runAllTests).toHaveBeenCalled();
    });

    it('should rollback on test failures', async () => {
      const opportunities = [
        createMockRefactoringOpportunity({
          type: 'extract_method',
          automated: true,
          risk: 'medium',
        }),
      ];

      const config = createMockRefactoringConfig({ backupChanges: true });
      refactorer = new CodeRefactorer(
        mockAIProvider,
        mockCodeAnalyzer,
        mockTestRunner,
        mockFileSystem,
        config,
        mockLogger,
        mockEventStore
      );

      mockFileSystem.readFile.mockReturnValue('function test() { return true; }');
      mockFileSystem.writeFile.mockResolvedValue();
      mockTestRunner.runAllTests.mockResolvedValue({
        passed: 2,
        failed: 3,
        skipped: 0,
        total: 5,
        duration: 100,
        failures: [{ test: 'test-1', file: 'test.ts', error: 'Test failed' }],
        framework: 'vitest',
      });

      const result = await refactorer.applyRefactoring(opportunities);

      expect(result.applied).toHaveLength(0);
      expect(result.failed).toHaveLength(1);
      expect(mockFileSystem.writeFile).toHaveBeenCalledTimes(2); // Apply + rollback
    });
  });
});
```

### Configuration Examples

**Refactoring Configuration**:

```yaml
refactoring:
  enabled: true
  max_risk_level: 'medium' # low, medium, high
  max_effort_level: 'medium' # low, medium, high
  min_priority: 5
  automated_only: true
  preserve_tests: true
  backup_changes: true

  types:
    - type: 'extract_method'
      enabled: true
      max_risk: 'medium'
      max_effort: 'medium'
      priority: 8
      automated: true

    - type: 'simplify_conditional'
      enabled: true
      max_risk: 'low'
      max_effort: 'low'
      priority: 7
      automated: true

    - type: 'remove_dead_code'
      enabled: true
      max_risk: 'low'
      max_effort: 'low'
      priority: 6
      automated: true

    - type: 'optimize_imports'
      enabled: true
      max_risk: 'low'
      max_effort: 'low'
      priority: 5
      automated: true

    - type: 'improve_naming'
      enabled: false # Requires human judgment
      max_risk: 'medium'
      max_effort: 'medium'
      priority: 4
      automated: false

  analysis:
    min_method_length: 20
    max_complexity: 5
    min_test_coverage: 80
    duplicate_threshold: 5

  validation:
    run_tests: true
    check_syntax: true
    verify_imports: true
    performance_check: false
```

---

## Implementation Notes

**Key Considerations**:

1. **Safety First**: Refactoring must preserve functionality - tests must continue to pass.

2. **Risk Assessment**: Each refactoring opportunity should be evaluated for risk and impact.

3. **Incremental Approach**: Apply refactoring opportunities incrementally with validation at each step.

4. **Rollback Capability**: Always maintain ability to rollback changes if issues arise.

5. **Automation vs Manual**: Balance automated refactoring with opportunities requiring human judgment.

6. **Performance Impact**: Consider performance implications of refactoring operations.

**Performance Targets**:

- Analysis: < 30 seconds per file
- Single refactoring: < 5 seconds
- Full refactoring pass: < 2 minutes
- Validation: < 30 seconds

**Security Considerations**:

- Validate refactoring doesn't introduce security vulnerabilities
- Preserve access controls and permissions
- Handle sensitive data appropriately during transformations
- Ensure refactoring doesn't expose internal implementation details

**Refactoring Best Practices**:

- Follow SOLID principles
- Maintain backward compatibility where possible
- Preserve existing APIs and contracts
- Ensure tests cover refactored code
- Document complex refactoring decisions
- Consider team coding standards and conventions

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
