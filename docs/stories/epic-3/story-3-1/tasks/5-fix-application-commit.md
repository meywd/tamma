# Task 5: Fix Application and Commit System

**Story**: 3.1 - Build Automation Gate Implementation  
**Phase**: Core MVP  
**Priority**: High  
**Estimated Time**: 2-3 days

## 🎯 Objective

Implement system to automatically apply AI-suggested fixes, commit changes, and prepare for build retry.

## ✅ Acceptance Criteria

- [ ] System applies file changes suggested by AI
- [ ] System modifies configuration files as needed
- [ ] System updates dependencies and runs install commands
- [ ] System commits changes with descriptive messages
- [ ] System validates changes before committing
- [ ] System creates rollback points for each change
- [ ] Support for multiple change types (file, config, dependency)
- [ ] Error handling for failed applications

## 🔧 Technical Implementation

### Core Interfaces

```typescript
interface IFixApplicator {
  applyFix(suggestion: FixSuggestion): Promise<FixApplicationResult>;
  validateChanges(changes: FixChange[]): Promise<ValidationResult>;
  createRollbackPoint(changes: FixChange[]): Promise<RollbackPoint>;
  rollbackToPoint(rollbackPoint: RollbackPoint): Promise<void>;
}

interface FixApplicationResult {
  success: boolean;
  appliedChanges: AppliedChange[];
  failedChanges: FailedChange[];
  rollbackPoint: RollbackPoint;
  commitHash?: string;
  errors: string[];
  warnings: string[];
  duration: number;
}

interface AppliedChange {
  change: FixChange;
  appliedAt: Date;
  backupPath?: string;
  checksum: string;
  validated: boolean;
}

interface FailedChange {
  change: FixChange;
  error: string;
  attemptedAt: Date;
  canRetry: boolean;
}

interface RollbackPoint {
  id: string;
  createdAt: Date;
  changes: RollbackChange[];
  commitHash?: string;
  description: string;
}

interface RollbackChange {
  file: string;
  originalContent: string;
  originalChecksum: string;
  operation: Operation;
}
```

### Fix Applicator Implementation

```typescript
class FixApplicator implements IFixApplicator {
  constructor(
    private gitPlatform: IGitPlatform,
    private fileSystem: IFileSystem,
    private commandExecutor: ICommandExecutor,
    private validator: IChangeValidator,
    private logger: Logger
  ) {}

  async applyFix(suggestion: FixSuggestion): Promise<FixApplicationResult> {
    const startTime = Date.now();
    const result: FixApplicationResult = {
      success: false,
      appliedChanges: [],
      failedChanges: [],
      rollbackPoint: null,
      errors: [],
      warnings: [],
      duration: 0,
    };

    try {
      // Create rollback point before making changes
      result.rollbackPoint = await this.createRollbackPoint(suggestion.changes);

      // Validate changes before applying
      const validation = await this.validateChanges(suggestion.changes);
      if (!validation.isValid) {
        result.errors.push(...validation.errors);
        return result;
      }

      // Apply file changes
      for (const change of suggestion.changes) {
        try {
          const appliedChange = await this.applyChange(change);
          result.appliedChanges.push(appliedChange);
        } catch (error) {
          result.failedChanges.push({
            change,
            error: error.message,
            attemptedAt: new Date(),
            canRetry: this.canRetryChange(change, error),
          });
        }
      }

      // Execute commands
      for (const command of suggestion.commands) {
        try {
          await this.executeCommand(command);
        } catch (error) {
          result.errors.push(`Command failed: ${command.command} - ${error.message}`);
        }
      }

      // Validate applied changes
      await this.validateAppliedChanges(result.appliedChanges);

      // Commit changes if successful
      if (result.failedChanges.length === 0 && result.errors.length === 0) {
        result.commitHash = await this.commitChanges(suggestion);
        result.success = true;
      } else {
        // Rollback on failure
        await this.rollbackToPoint(result.rollbackPoint);
      }
    } catch (error) {
      result.errors.push(`Fix application failed: ${error.message}`);

      // Attempt rollback
      if (result.rollbackPoint) {
        try {
          await this.rollbackToPoint(result.rollbackPoint);
        } catch (rollbackError) {
          result.errors.push(`Rollback failed: ${rollbackError.message}`);
        }
      }
    }

    result.duration = Date.now() - startTime;
    return result;
  }

  private async applyChange(change: FixChange): Promise<AppliedChange> {
    const filePath = this.resolveFilePath(change.file);

    // Create backup
    const backupPath = await this.createBackup(filePath);
    const originalChecksum = await this.calculateChecksum(filePath);

    // Apply change based on type
    switch (change.type) {
      case ChangeType.FILE_MODIFICATION:
        await this.applyFileModification(change, filePath);
        break;

      case ChangeType.CONFIGURATION_CHANGE:
        await this.applyConfigurationChange(change, filePath);
        break;

      case ChangeType.DEPENDENCY_UPDATE:
        await this.applyDependencyUpdate(change, filePath);
        break;

      case ChangeType.NEW_FILE:
        await this.createNewFile(change, filePath);
        break;

      case ChangeType.FILE_DELETE:
        await this.deleteFile(change, filePath);
        break;

      default:
        throw new Error(`Unsupported change type: ${change.type}`);
    }

    // Validate the applied change
    const isValid = await this.validator.validateFile(filePath);
    if (!isValid) {
      // Restore from backup
      await this.restoreFromBackup(filePath, backupPath);
      throw new Error(`Applied change failed validation: ${filePath}`);
    }

    return {
      change,
      appliedAt: new Date(),
      backupPath,
      checksum: await this.calculateChecksum(filePath),
      validated: true,
    };
  }

  private async applyFileModification(change: FixChange, filePath: string): Promise<void> {
    const content = await fs.readFile(filePath, 'utf-8');
    const lines = content.split('\n');

    switch (change.operation) {
      case Operation.REPLACE:
        if (change.line !== undefined) {
          lines[change.line - 1] = change.content;
        } else {
          // Replace entire file content
          await fs.writeFile(filePath, change.content, 'utf-8');
          return;
        }
        break;

      case Operation.INSERT:
        if (change.line !== undefined) {
          lines.splice(change.line - 1, 0, change.content);
        } else {
          // Append to end
          lines.push(change.content);
        }
        break;

      case Operation.DELETE:
        if (change.line !== undefined) {
          lines.splice(change.line - 1, 1);
        } else {
          // Delete entire file
          await fs.unlink(filePath);
          return;
        }
        break;

      case Operation.APPEND:
        lines.push(change.content);
        break;

      default:
        throw new Error(`Unsupported operation: ${change.operation}`);
    }

    await fs.writeFile(filePath, lines.join('\n'), 'utf-8');
  }

  private async applyConfigurationChange(change: FixChange, filePath: string): Promise<void> {
    // Handle different configuration formats
    const ext = path.extname(filePath).toLowerCase();

    switch (ext) {
      case '.json':
        await this.applyJsonConfigChange(change, filePath);
        break;

      case '.yml':
      case '.yaml':
        await this.applyYamlConfigChange(change, filePath);
        break;

      case '.xml':
        await this.applyXmlConfigChange(change, filePath);
        break;

      case '.properties':
        await this.applyPropertiesConfigChange(change, filePath);
        break;

      default:
        // Fallback to file modification
        await this.applyFileModification(change, filePath);
    }
  }

  private async applyJsonConfigChange(change: FixChange, filePath: string): Promise<void> {
    const content = await fs.readFile(filePath, 'utf-8');
    const config = JSON.parse(content);

    // Parse the change content as JSON path operation
    const operation = JSON.parse(change.content);

    switch (operation.type) {
      case 'update':
        this.setNestedValue(config, operation.path, operation.value);
        break;

      case 'add':
        this.addNestedValue(config, operation.path, operation.value);
        break;

      case 'delete':
        this.deleteNestedValue(config, operation.path);
        break;
    }

    await fs.writeFile(filePath, JSON.stringify(config, null, 2), 'utf-8');
  }

  private async applyDependencyUpdate(change: FixChange, filePath: string): Promise<void> {
    const ext = path.extname(filePath).toLowerCase();

    switch (ext) {
      case '.json':
        await this.applyNpmDependencyUpdate(change, filePath);
        break;

      case '.yml':
      case '.yaml':
        await this.applyYamlDependencyUpdate(change, filePath);
        break;

      default:
        throw new Error(`Unsupported dependency file format: ${ext}`);
    }
  }

  private async applyNpmDependencyUpdate(change: FixChange, filePath: string): Promise<void> {
    const content = await fs.readFile(filePath, 'utf-8');
    const packageJson = JSON.parse(content);

    const operation = JSON.parse(change.content);

    if (operation.type === 'update') {
      // Update dependency version
      if (operation.devDependency) {
        packageJson.devDependencies = packageJson.devDependencies || {};
        packageJson.devDependencies[operation.name] = operation.version;
      } else {
        packageJson.dependencies = packageJson.dependencies || {};
        packageJson.dependencies[operation.name] = operation.version;
      }
    }

    await fs.writeFile(filePath, JSON.stringify(packageJson, null, 2), 'utf-8');
  }

  private async executeCommand(command: FixCommand): Promise<void> {
    const options: CommandOptions = {
      cwd: command.workingDirectory || process.cwd(),
      timeout: command.timeout || 60000,
      env: {
        ...process.env,
        ...command.environment,
      },
    };

    try {
      const result = await this.commandExecutor.execute(command.command, options);

      if (result.exitCode !== 0) {
        throw new Error(`Command exited with code ${result.exitCode}: ${result.stderr}`);
      }

      this.logger.info('Command executed successfully', {
        command: command.command,
        duration: result.duration,
        stdout: result.stdout,
      });
    } catch (error) {
      this.logger.error('Command execution failed', {
        command: command.command,
        error: error.message,
      });
      throw error;
    }
  }

  private async commitChanges(suggestion: FixSuggestion): Promise<string> {
    const commitMessage = this.generateCommitMessage(suggestion);

    try {
      // Stage all changes
      await this.gitPlatform.add(['.']);

      // Commit with descriptive message
      const commitResult = await this.gitPlatform.commit(commitMessage);

      this.logger.info('Changes committed successfully', {
        commitHash: commitResult.hash,
        message: commitMessage,
        changesCount: suggestion.changes.length,
      });

      return commitResult.hash;
    } catch (error) {
      this.logger.error('Failed to commit changes', {
        error: error.message,
        suggestionId: suggestion.id,
      });
      throw new Error(`Commit failed: ${error.message}`);
    }
  }

  private generateCommitMessage(suggestion: FixSuggestion): string {
    const timestamp = new Date().toISOString().split('T')[0];
    const category = suggestion.category.replace('_', ' ');
    const riskLevel = suggestion.riskLevel.toUpperCase();

    let message = `fix: ${category} - ${suggestion.explanation.substring(0, 100)}`;

    if (suggestion.riskLevel !== RiskLevel.LOW) {
      message += ` [${riskLevel} RISK]`;
    }

    message += `\n\nAuto-generated fix for build failure: ${suggestion.buildId}`;
    message += `\nAI Provider: ${suggestion.aiProvider} (${suggestion.model})`;
    message += `\nConfidence: ${(suggestion.confidence * 100).toFixed(1)}%`;
    message += `\nChanges: ${suggestion.changes.length} files, ${suggestion.commands.length} commands`;

    if (suggestion.estimatedTime) {
      message += `\nEstimated fix time: ${Math.round(suggestion.estimatedTime / 60)} minutes`;
    }

    return message;
  }

  async createRollbackPoint(changes: FixChange[]): Promise<RollbackPoint> {
    const rollbackChanges: RollbackChange[] = [];

    for (const change of changes) {
      const filePath = this.resolveFilePath(change.file);

      try {
        const originalContent = await fs.readFile(filePath, 'utf-8');
        const originalChecksum = await this.calculateChecksum(filePath);

        rollbackChanges.push({
          file: filePath,
          originalContent,
          originalChecksum,
          operation: change.operation,
        });
      } catch (error) {
        // File might not exist yet (for new files)
        if (change.type === ChangeType.NEW_FILE) {
          rollbackChanges.push({
            file: filePath,
            originalContent: '',
            originalChecksum: '',
            operation: change.operation,
          });
        }
      }
    }

    return {
      id: this.generateRollbackId(),
      createdAt: new Date(),
      changes: rollbackChanges,
      description: `Rollback point for ${changes.length} changes`,
    };
  }

  async rollbackToPoint(rollbackPoint: RollbackPoint): Promise<void> {
    this.logger.info('Starting rollback', {
      rollbackId: rollbackPoint.id,
      changesCount: rollbackPoint.changes.length,
    });

    for (const rollbackChange of rollbackPoint.changes) {
      try {
        await this.restoreFile(rollbackChange);
      } catch (error) {
        this.logger.error('Failed to restore file during rollback', {
          file: rollbackChange.file,
          error: error.message,
        });
      }
    }

    this.logger.info('Rollback completed', {
      rollbackId: rollbackPoint.id,
    });
  }

  private async restoreFile(rollbackChange: RollbackChange): Promise<void> {
    const { file, originalContent, originalChecksum } = rollbackChange;

    if (originalContent === '') {
      // File was created, delete it
      try {
        await fs.unlink(file);
      } catch (error) {
        // File might not exist
        if (error.code !== 'ENOENT') {
          throw error;
        }
      }
    } else {
      // Restore original content
      await fs.writeFile(file, originalContent, 'utf-8');

      // Verify checksum
      const currentChecksum = await this.calculateChecksum(file);
      if (currentChecksum !== originalChecksum) {
        throw new Error(`Checksum mismatch after rollback: ${file}`);
      }
    }
  }
}
```

### Change Validator

```typescript
class ChangeValidator implements IChangeValidator {
  async validateChanges(changes: FixChange[]): Promise<ValidationResult> {
    const errors: string[] = [];
    const warnings: string[] = [];

    for (const change of changes) {
      const validation = await this.validateSingleChange(change);
      errors.push(...validation.errors);
      warnings.push(...validation.warnings);
    }

    return {
      isValid: errors.length === 0,
      errors,
      warnings,
      score: this.calculateValidationScore(errors, warnings),
    };
  }

  async validateFile(filePath: string): Promise<boolean> {
    try {
      // Check if file is syntactically valid
      const ext = path.extname(filePath).toLowerCase();

      switch (ext) {
        case '.ts':
        case '.tsx':
          return await this.validateTypeScript(filePath);

        case '.js':
        case '.jsx':
          return await this.validateJavaScript(filePath);

        case '.json':
          return await this.validateJson(filePath);

        case '.yml':
        case '.yaml':
          return await this.validateYaml(filePath);

        default:
          return true; // Assume valid for unknown types
      }
    } catch (error) {
      return false;
    }
  }

  private async validateTypeScript(filePath: string): Promise<boolean> {
    try {
      // Use TypeScript compiler API
      const program = ts.createProgram([filePath], {});
      const diagnostics = ts.getPreEmitDiagnostics(program);

      return diagnostics.length === 0;
    } catch (error) {
      return false;
    }
  }

  private async validateJavaScript(filePath: string): Promise<boolean> {
    try {
      // Use acorn or similar parser
      const content = await fs.readFile(filePath, 'utf-8');
      acorn.parse(content, {
        ecmaVersion: 'latest',
        sourceType: 'module',
      });
      return true;
    } catch (error) {
      return false;
    }
  }

  private async validateJson(filePath: string): Promise<boolean> {
    try {
      const content = await fs.readFile(filePath, 'utf-8');
      JSON.parse(content);
      return true;
    } catch (error) {
      return false;
    }
  }
}
```

## 🧪 Testing Strategy

### Unit Tests

```typescript
describe('FixApplicator', () => {
  let applicator: FixApplicator;
  let mockGitPlatform: jest.Mocked<IGitPlatform>;
  let mockFileSystem: jest.Mocked<IFileSystem>;
  let mockCommandExecutor: jest.Mocked<ICommandExecutor>;

  beforeEach(() => {
    mockGitPlatform = createMockGitPlatform();
    mockFileSystem = createMockFileSystem();
    mockCommandExecutor = createMockCommandExecutor();

    applicator = new FixApplicator(
      mockGitPlatform,
      mockFileSystem,
      mockCommandExecutor,
      new ChangeValidator(),
      mockLogger
    );
  });

  it('should apply file modification changes', async () => {
    const suggestion: FixSuggestion = {
      id: 'test-suggestion',
      buildId: 'build-123',
      confidence: 0.8,
      category: FixCategory.SYNTAX_FIX,
      changes: [
        {
          type: ChangeType.FILE_MODIFICATION,
          file: 'src/test.ts',
          operation: Operation.REPLACE,
          line: 10,
          content: 'const x = 1;',
          description: 'Fix syntax error',
          confidence: 0.9,
        },
      ],
      commands: [],
      explanation: 'Fix syntax error',
      riskLevel: RiskLevel.LOW,
      estimatedTime: 60,
      dependencies: [],
      rollbackPlan: null,
      generatedAt: new Date(),
      aiProvider: 'claude',
      model: 'claude-3-sonnet',
    };

    mockFileSystem.readFile.mockResolvedValue('original content\n');
    mockFileSystem.writeFile.mockResolvedValue();
    mockGitPlatform.add.mockResolvedValue();
    mockGitPlatform.commit.mockResolvedValue({ hash: 'abc123' });

    const result = await applicator.applyFix(suggestion);

    expect(result.success).toBe(true);
    expect(result.appliedChanges).toHaveLength(1);
    expect(result.commitHash).toBe('abc123');
    expect(mockFileSystem.writeFile).toHaveBeenCalledWith(
      expect.any(String),
      expect.stringContaining('const x = 1;'),
      'utf-8'
    );
  });

  it('should rollback on validation failure', async () => {
    const suggestion = createMockFixSuggestion();

    mockFileSystem.readFile.mockResolvedValue('original content');
    mockFileSystem.writeFile.mockResolvedValue();

    const mockValidator = {
      validateFile: jest.fn().mockResolvedValue(false),
      validateChanges: jest.fn().mockResolvedValue({ isValid: true, errors: [], warnings: [] }),
    };

    const applicatorWithFailingValidator = new FixApplicator(
      mockGitPlatform,
      mockFileSystem,
      mockCommandExecutor,
      mockValidator,
      mockLogger
    );

    const result = await applicatorWithFailingValidator.applyFix(suggestion);

    expect(result.success).toBe(false);
    expect(result.rollbackPoint).toBeDefined();
  });
});
```

### Integration Tests

```typescript
describe('FixApplicator Integration', () => {
  it('should apply real fix suggestions', async () => {
    const applicator = new FixApplicator(
      new RealGitPlatform(),
      new RealFileSystem(),
      new RealCommandExecutor(),
      new ChangeValidator(),
      new Logger()
    );

    const suggestion = createRealFixSuggestion();
    const result = await applicator.applyFix(suggestion);

    expect(result.success).toBe(true);
    expect(result.commitHash).toBeDefined();

    // Verify changes were actually applied
    const changedFile = await fs.readFile(suggestion.changes[0].file, 'utf-8');
    expect(changedFile).toContain(suggestion.changes[0].content);
  });
});
```

## 📊 Monitoring & Metrics

### Key Metrics

- Fix application success rate
- Rollback frequency
- Validation failure rate
- Average fix application time
- Change type distribution

### Events to Emit

```typescript
// Fix application events
FIX.APPLICATION_STARTED;
FIX.CHANGE_APPLIED;
FIX.COMMAND_EXECUTED;
FIX.APPLICATION_COMPLETED;
FIX.APPLICATION_FAILED;
FIX.ROLLBACK_TRIGGERED;
FIX.ROLLBACK_COMPLETED;
FIX.CHANGES_COMMITTED;
```

## 🔧 Configuration

### Environment Variables

```bash
# Fix application configuration
ENABLE_CHANGE_VALIDATION=true
CREATE_BACKUPS=true
VALIDATE_SYNTAX=true
AUTO_COMMIT_CHANGES=true

# Rollback configuration
ROLLBACK_TIMEOUT=30000
MAX_ROLLBACK_ATTEMPTS=3
PRESERVE_BACKUPS_FOR_HOURS=24

# Command execution
COMMAND_TIMEOUT=60000
ALLOW_DANGEROUS_COMMANDS=false
```

### Configuration File

```yaml
fix_application:
  enable_validation: true
  create_backups: true
  validate_syntax: true
  auto_commit: true

rollback:
  timeout: 30000
  max_attempts: 3
  preserve_backups_for_hours: 24

command_execution:
  timeout: 60000
  allow_dangerous_commands: false
  working_directory: '.'

validation:
  typescript:
    enabled: true
    strict_mode: true

  javascript:
    enabled: true
    ecma_version: 'latest'

  json:
    enabled: true
    allow_comments: false

  yaml:
    enabled: true
    strict_mode: true
```

## 🚨 Error Handling

### Common Error Scenarios

1. **File permission errors**
   - Check file permissions before modification
   - Attempt permission fixes if possible

2. **Syntax validation failures**
   - Automatic rollback
   - Detailed error reporting

3. **Command execution failures**
   - Retry with different parameters
   - Fallback commands if available

4. **Git operation failures**
   - Check git repository status
   - Handle merge conflicts

### Recovery Strategies

- Automatic rollback on validation failures
- Multiple retry attempts for transient errors
- Detailed error logging for debugging
- Manual escalation for persistent failures

## 📝 Implementation Checklist

- [ ] Define core interfaces and types
- [ ] Implement FixApplicator class
- [ ] Create ChangeValidator with syntax checking
- [ ] Implement rollback functionality
- [ ] Add support for multiple change types
- [ ] Implement command execution system
- [ ] Add commit message generation
- [ ] Write comprehensive unit tests
- [ ] Write integration tests with real files
- [ ] Add monitoring and metrics
- [ ] Create configuration management
- [ ] Document security considerations
- [ ] Add backup and recovery procedures

---

**Dependencies**: Task 4 (AI-Powered Fix Suggestions)  
**Blocked By**: Epic 1 (Git Platform Integration)  
**Blocks**: Task 6 (Retry Logic with Exponential Backoff)
