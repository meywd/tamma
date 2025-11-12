# Task 4: AI-Powered Fix Suggestions

**Story**: 3.1 - Build Automation Gate Implementation  
**Phase**: Core MVP  
**Priority**: High  
**Estimated Time**: 3-4 days

## 🎯 Objective

Implement AI-powered system to analyze build failures and generate specific fix suggestions that can be automatically applied.

## ✅ Acceptance Criteria

- [ ] System sends build failure analysis to AI provider
- [ ] AI provides specific code changes needed
- [ ] AI suggests configuration file modifications
- [ ] AI recommends dependency updates
- [ ] AI provides commands to execute fixes
- [ ] Suggestions are structured and actionable
- [ ] Support for multiple AI providers (Claude, OpenAI, etc.)
- [ ] Fallback mechanisms for AI failures

## 🔧 Technical Implementation

### Core Interfaces

```typescript
interface IAIFixSuggestionGenerator {
  generateFixSuggestions(failureAnalysis: BuildFailureAnalysis): Promise<FixSuggestion>;
  validateSuggestion(suggestion: FixSuggestion): Promise<ValidationResult>;
  refineSuggestion(suggestion: FixSuggestion, feedback: string): Promise<FixSuggestion>;
}

interface FixSuggestion {
  id: string;
  buildId: string;
  confidence: number;
  category: FixCategory;
  changes: FixChange[];
  commands: FixCommand[];
  explanation: string;
  riskLevel: RiskLevel;
  estimatedTime: number;
  dependencies: string[];
  rollbackPlan: RollbackPlan;
  generatedAt: Date;
  aiProvider: string;
  model: string;
}

interface FixChange {
  type: ChangeType;
  file: string;
  content: string;
  operation: Operation;
  line?: number;
  column?: number;
  description: string;
  confidence: number;
}

enum ChangeType {
  FILE_MODIFICATION = 'file_modification',
  CONFIGURATION_CHANGE = 'configuration_change',
  DEPENDENCY_UPDATE = 'dependency_update',
  NEW_FILE = 'new_file',
  FILE_DELETE = 'file_delete',
}

enum Operation {
  INSERT = 'insert',
  REPLACE = 'replace',
  DELETE = 'delete',
  APPEND = 'append',
}

enum FixCategory {
  SYNTAX_FIX = 'syntax_fix',
  DEPENDENCY_FIX = 'dependency_fix',
  CONFIGURATION_FIX = 'configuration_fix',
  IMPORT_FIX = 'import_fix',
  TYPE_FIX = 'type_fix',
  BUILD_SCRIPT_FIX = 'build_script_fix',
  ENVIRONMENT_FIX = 'environment_fix',
}

enum RiskLevel {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  CRITICAL = 'critical',
}

interface FixCommand {
  command: string;
  description: string;
  workingDirectory?: string;
  timeout?: number;
  retries?: number;
  environment?: Record<string, string>;
}

interface RollbackPlan {
  changes: RollbackChange[];
  commands: string[];
  description: string;
}
```

### AI Fix Suggestion Generator

```typescript
class AIFixSuggestionGenerator implements IAIFixSuggestionGenerator {
  constructor(
    private aiProvider: IAIProvider,
    private promptTemplates: Map<FixCategory, PromptTemplate>,
    private validator: IFixValidator,
    private logger: Logger
  ) {}

  async generateFixSuggestions(failureAnalysis: BuildFailureAnalysis): Promise<FixSuggestion> {
    const category = this.mapErrorCategoryToFixCategory(failureAnalysis.errorCategory);
    const template = this.promptTemplates.get(category);

    if (!template) {
      throw new Error(`No template available for category: ${category}`);
    }

    // Generate prompt from template
    const prompt = this.buildPrompt(template, failureAnalysis);

    try {
      // Send to AI provider
      const response = await this.aiProvider.sendMessage({
        messages: [{ role: 'user', content: prompt }],
        temperature: 0.2, // Low temperature for consistent fixes
        maxTokens: 2000,
        model: 'claude-3-sonnet-20241022', // Default model
      });

      // Parse AI response
      const suggestion = await this.parseAIResponse(response.content, failureAnalysis);

      // Validate suggestion
      const validation = await this.validator.validate(suggestion);
      if (!validation.isValid) {
        this.logger.warn('AI suggestion failed validation', {
          suggestionId: suggestion.id,
          errors: validation.errors,
        });

        // Try to refine the suggestion
        return await this.refineSuggestion(suggestion, validation.errors.join('\n'));
      }

      return suggestion;
    } catch (error) {
      this.logger.error('Failed to generate AI fix suggestion', {
        buildId: failureAnalysis.buildId,
        error: error.message,
      });

      throw new Error(`AI fix generation failed: ${error.message}`);
    }
  }

  private buildPrompt(template: PromptTemplate, analysis: BuildFailureAnalysis): string {
    const context = {
      errorMessage: analysis.primaryError.message,
      errorFile: analysis.primaryError.file,
      errorLine: analysis.primaryError.line,
      errorColumn: analysis.primaryError.column,
      errorCode: analysis.primaryError.code,
      errorCategory: analysis.errorCategory,
      language: analysis.context.language,
      buildSystem: analysis.context.buildSystem,
      framework: analysis.context.framework,
      dependencies: analysis.context.dependencies.map((d) => `${d.name}@${d.version}`),
      recentChanges: analysis.context.recentChanges.map((c) => `${c.file}: ${c.type}`),
      buildConfiguration: JSON.stringify(analysis.context.buildConfiguration, null, 2),
      similarFailures: analysis.similarFailures.slice(0, 3).map((f) => f.resolution),
    };

    return template.render(context);
  }

  private async parseAIResponse(
    response: string,
    analysis: BuildFailureAnalysis
  ): Promise<FixSuggestion> {
    try {
      // Try to parse as JSON first
      if (response.trim().startsWith('{')) {
        const parsed = JSON.parse(response);
        return this.mapParsedResponse(parsed, analysis);
      }

      // Parse as structured text
      return this.parseStructuredResponse(response, analysis);
    } catch (error) {
      this.logger.error('Failed to parse AI response', {
        response: response.substring(0, 500),
        error: error.message,
      });

      throw new Error('Invalid AI response format');
    }
  }

  private mapParsedResponse(parsed: any, analysis: BuildFailureAnalysis): FixSuggestion {
    return {
      id: this.generateSuggestionId(),
      buildId: analysis.buildId,
      confidence: parsed.confidence || 0.7,
      category: this.mapStringToFixCategory(parsed.category),
      changes: parsed.changes?.map(this.mapChange) || [],
      commands: parsed.commands?.map(this.mapCommand) || [],
      explanation: parsed.explanation || '',
      riskLevel: this.mapStringToRiskLevel(parsed.riskLevel || 'medium'),
      estimatedTime: parsed.estimatedTime || 300, // 5 minutes default
      dependencies: parsed.dependencies || [],
      rollbackPlan: parsed.rollbackPlan
        ? this.mapRollbackPlan(parsed.rollbackPlan)
        : this.generateDefaultRollbackPlan(),
      generatedAt: new Date(),
      aiProvider: this.aiProvider.name,
      model: 'claude-3-sonnet-20241022',
    };
  }
}
```

### Prompt Templates

```typescript
class PromptTemplate {
  constructor(
    private category: FixCategory,
    private template: string,
    private examples: PromptExample[]
  ) {}

  render(context: PromptContext): string {
    let rendered = this.template;

    // Replace placeholders
    Object.entries(context).forEach(([key, value]) => {
      const placeholder = `{{${key}}}`;
      rendered = rendered.replace(new RegExp(placeholder, 'g'), String(value));
    });

    // Add examples if available
    if (this.examples.length > 0) {
      rendered += '\n\nExamples of similar fixes:\n';
      this.examples.forEach((example, index) => {
        rendered += `\nExample ${index + 1}:\n${example.example}\n`;
      });
    }

    return rendered;
  }
}

// Dependency fix template
const DEPENDENCY_FIX_TEMPLATE = `
You are an expert build engineer helping to fix a dependency-related build failure.

## Build Failure Information:
- Error Message: {{errorMessage}}
- Error File: {{errorFile}}
- Error Line: {{errorLine}}
- Language: {{language}}
- Build System: {{buildSystem}}
- Framework: {{framework}}

## Current Dependencies:
{{dependencies}}

## Recent Changes:
{{recentChanges}}

## Build Configuration:
{{buildConfiguration}}

## Task:
Analyze this dependency error and provide specific fix suggestions. Your response must be valid JSON with this structure:

{
  "confidence": 0.8,
  "category": "dependency_fix",
  "explanation": "Clear explanation of the root cause",
  "riskLevel": "low|medium|high|critical",
  "estimatedTime": 300,
  "changes": [
    {
      "type": "dependency_update",
      "file": "package.json",
      "operation": "replace",
      "content": "updated dependency line",
      "description": "Update lodash from 4.0.0 to 4.17.21",
      "confidence": 0.9
    }
  ],
  "commands": [
    {
      "command": "npm install",
      "description": "Install updated dependencies",
      "workingDirectory": ".",
      "timeout": 60000
    }
  ],
  "rollbackPlan": {
    "changes": ["Restore original package.json"],
    "commands": ["git checkout -- package.json"],
    "description": "Restore original dependency versions"
  }
}

Focus on:
1. Identifying the exact dependency causing the issue
2. Suggesting compatible version updates
3. Providing commands to fix the issue
4. Including a rollback plan
5. Assessing the risk level of the changes

Be specific and actionable. Avoid generic suggestions.
`;

// Syntax fix template
const SYNTAX_FIX_TEMPLATE = `
You are an expert programmer helping to fix a syntax error in a build failure.

## Build Failure Information:
- Error Message: {{errorMessage}}
- Error File: {{errorFile}}
- Error Line: {{errorLine}}
- Error Column: {{errorColumn}}
- Error Code: {{errorCode}}
- Language: {{language}}
- Build System: {{buildSystem}}

## Task:
Analyze this syntax error and provide the exact code fix needed. Your response must be valid JSON with this structure:

{
  "confidence": 0.9,
  "category": "syntax_fix",
  "explanation": "Clear explanation of the syntax error",
  "riskLevel": "low",
  "estimatedTime": 60,
  "changes": [
    {
      "type": "file_modification",
      "file": "{{errorFile}}",
      "operation": "replace",
      "line": {{errorLine}},
      "content": "corrected line of code",
      "description": "Fix missing semicolon",
      "confidence": 0.95
    }
  ],
  "commands": [],
  "rollbackPlan": {
    "changes": ["Restore original line"],
    "commands": ["git checkout -- {{errorFile}}"],
    "description": "Restore original code"
  }
}

Focus on:
1. Providing the exact code correction
2. Explaining why the error occurred
3. Including line numbers for precise fixes
4. Ensuring the fix is minimal and targeted
5. Providing a clear rollback plan

Be precise with the code changes. Include the exact corrected line.
`;
```

### Fix Validator

```typescript
class FixValidator implements IFixValidator {
  async validate(suggestion: FixSuggestion): Promise<ValidationResult> {
    const errors: string[] = [];
    const warnings: string[] = [];

    // Validate structure
    if (!suggestion.id) errors.push('Missing suggestion ID');
    if (!suggestion.buildId) errors.push('Missing build ID');
    if (!suggestion.changes?.length && !suggestion.commands?.length) {
      errors.push('No changes or commands specified');
    }

    // Validate changes
    for (const change of suggestion.changes) {
      const changeValidation = await this.validateChange(change);
      errors.push(...changeValidation.errors);
      warnings.push(...changeValidation.warnings);
    }

    // Validate commands
    for (const command of suggestion.commands) {
      const commandValidation = await this.validateCommand(command);
      errors.push(...commandValidation.errors);
      warnings.push(...commandValidation.warnings);
    }

    // Validate risk level
    if (suggestion.riskLevel === RiskLevel.CRITICAL && suggestion.confidence < 0.9) {
      warnings.push('Critical risk level with low confidence');
    }

    // Validate rollback plan
    if (!suggestion.rollbackPlan) {
      warnings.push('No rollback plan provided');
    }

    return {
      isValid: errors.length === 0,
      errors,
      warnings,
      score: this.calculateValidationScore(errors, warnings),
    };
  }

  private async validateChange(change: FixChange): Promise<ValidationResult> {
    const errors: string[] = [];
    const warnings: string[] = [];

    // Validate file path
    if (!change.file) {
      errors.push('Change missing file path');
    } else if (this.isDangerousPath(change.file)) {
      errors.push(`Dangerous file path: ${change.file}`);
    }

    // Validate operation
    if (!Object.values(Operation).includes(change.operation)) {
      errors.push(`Invalid operation: ${change.operation}`);
    }

    // Validate content for file modifications
    if (change.type === ChangeType.FILE_MODIFICATION && !change.content) {
      errors.push('File modification missing content');
    }

    // Check for dangerous operations
    if (this.isDangerousChange(change)) {
      warnings.push('Potentially dangerous change detected');
    }

    return { isValid: errors.length === 0, errors, warnings, score: 1 };
  }

  private async validateCommand(command: FixCommand): Promise<ValidationResult> {
    const errors: string[] = [];
    const warnings: string[] = [];

    // Validate command string
    if (!command.command) {
      errors.push('Command missing command string');
    } else if (this.isDangerousCommand(command.command)) {
      errors.push(`Dangerous command detected: ${command.command}`);
    }

    // Validate timeout
    if (command.timeout && command.timeout > 300000) {
      // 5 minutes
      warnings.push('Command timeout exceeds 5 minutes');
    }

    return { isValid: errors.length === 0, errors, warnings, score: 1 };
  }

  private isDangerousPath(filePath: string): boolean {
    const dangerousPaths = [
      '/etc',
      '/usr/bin',
      '/bin',
      '/sbin',
      'system32',
      '../',
      '~/.ssh',
      '~/.aws',
    ];

    return dangerousPaths.some((dangerous) => filePath.includes(dangerous));
  }

  private isDangerousCommand(command: string): boolean {
    const dangerousCommands = [
      'rm -rf',
      'sudo',
      'chmod 777',
      'curl | sh',
      'wget | sh',
      'eval',
      'exec',
      '> /dev/',
      'format',
      'fdisk',
    ];

    return dangerousCommands.some((dangerous) => command.includes(dangerous));
  }
}
```

### Multi-Provider Support

```typescript
class MultiProviderFixGenerator implements IAIFixSuggestionGenerator {
  private providers: Map<string, IAIProvider> = new Map();
  private fallbackOrder: string[] = ['claude', 'openai', 'gemini'];

  constructor(providers: IAIProvider[]) {
    providers.forEach((provider) => {
      this.providers.set(provider.name, provider);
    });
  }

  async generateFixSuggestions(failureAnalysis: BuildFailureAnalysis): Promise<FixSuggestion> {
    let lastError: Error | null = null;

    // Try providers in fallback order
    for (const providerName of this.fallbackOrder) {
      const provider = this.providers.get(providerName);
      if (!provider) continue;

      try {
        const generator = new AIFixSuggestionGenerator(
          provider,
          this.promptTemplates,
          this.validator,
          this.logger
        );

        const suggestion = await generator.generateFixSuggestions(failureAnalysis);

        // Add provider info
        suggestion.aiProvider = providerName;
        suggestion.model = provider.defaultModel;

        return suggestion;
      } catch (error) {
        lastError = error as Error;
        this.logger.warn(`Provider ${providerName} failed`, {
          error: error.message,
          buildId: failureAnalysis.buildId,
        });
      }
    }

    // All providers failed
    throw new Error(`All AI providers failed. Last error: ${lastError?.message}`);
  }
}
```

## 🧪 Testing Strategy

### Unit Tests

```typescript
describe('AIFixSuggestionGenerator', () => {
  let generator: AIFixSuggestionGenerator;
  let mockAIProvider: jest.Mocked<IAIProvider>;
  let mockValidator: jest.Mocked<IFixValidator>;

  beforeEach(() => {
    mockAIProvider = createMockAIProvider();
    mockValidator = createMockFixValidator();
    generator = new AIFixSuggestionGenerator(
      mockAIProvider,
      new Map([['dependency_fix', new PromptTemplate(...)]],
      mockValidator,
      mockLogger
    );
  });

  it('should generate dependency fix suggestions', async () => {
    const failureAnalysis = createMockDependencyFailureAnalysis();

    mockAIProvider.sendMessage.mockResolvedValue({
      content: JSON.stringify({
        confidence: 0.8,
        category: 'dependency_fix',
        explanation: 'Missing lodash dependency',
        changes: [{
          type: 'dependency_update',
          file: 'package.json',
          operation: 'replace',
          content: '"lodash": "^4.17.21"',
          description: 'Add lodash dependency',
          confidence: 0.9,
        }],
        commands: [{
          command: 'npm install',
          description: 'Install dependencies',
        }],
      }),
    });

    mockValidator.validate.mockResolvedValue({
      isValid: true,
      errors: [],
      warnings: [],
      score: 1,
    });

    const suggestion = await generator.generateFixSuggestions(failureAnalysis);

    expect(suggestion.category).toBe(FixCategory.DEPENDENCY_FIX);
    expect(suggestion.changes).toHaveLength(1);
    expect(suggestion.commands).toHaveLength(1);
    expect(suggestion.confidence).toBe(0.8);
  });

  it('should handle AI provider failures gracefully', async () => {
    const failureAnalysis = createMockFailureAnalysis();

    mockAIProvider.sendMessage.mockRejectedValue(new Error('AI service unavailable'));

    await expect(generator.generateFixSuggestions(failureAnalysis))
      .rejects.toThrow('AI fix generation failed');
  });
});
```

### Integration Tests

```typescript
describe('MultiProviderFixGenerator Integration', () => {
  it('should fallback to secondary provider on failure', async () => {
    const primaryProvider = createMockAIProvider('claude');
    const secondaryProvider = createMockAIProvider('openai');

    primaryProvider.sendMessage.mockRejectedValue(new Error('Rate limited'));
    secondaryProvider.sendMessage.mockResolvedValue(createValidAIResponse());

    const generator = new MultiProviderFixGenerator([primaryProvider, secondaryProvider]);
    const suggestion = await generator.generateFixSuggestions(createMockFailureAnalysis());

    expect(suggestion.aiProvider).toBe('openai');
    expect(primaryProvider.sendMessage).toHaveBeenCalled();
    expect(secondaryProvider.sendMessage).toHaveBeenCalled();
  });
});
```

## 📊 Monitoring & Metrics

### Key Metrics

- AI fix suggestion success rate
- Provider fallback frequency
- Fix validation pass rate
- Average suggestion generation time
- Risk level distribution

### Events to Emit

```typescript
// Fix generation events
AI.FIX_GENERATION_STARTED;
AI.FIX_GENERATION_COMPLETED;
AI.FIX_GENERATION_FAILED;
AI.PROVIDER_FALLBACK_TRIGGERED;

// Validation events
AI.FIX_VALIDATION_PASSED;
AI.FIX_VALIDATION_FAILED;
AI.FIX_REFINEMENT_REQUIRED;
```

## 🔧 Configuration

### Environment Variables

```bash
# AI provider configuration
PRIMARY_AI_PROVIDER=claude
FALLBACK_PROVIDERS=openai,gemini
AI_TEMPERATURE=0.2
AI_MAX_TOKENS=2000
AI_TIMEOUT=30000

# Validation configuration
ENABLE_DANGEROUS_OPERATION_DETECTION=true
MAX_COMMAND_TIMEOUT=300000
RISK_THRESHOLD=0.8
```

### Configuration File

```yaml
ai_fix_generation:
  primary_provider: claude
  fallback_providers: [openai, gemini]
  temperature: 0.2
  max_tokens: 2000
  timeout: 30000

validation:
  enable_dangerous_detection: true
  max_command_timeout: 300000
  risk_threshold: 0.8
  require_rollback_plan: true

providers:
  claude:
    model: claude-3-sonnet-20241022
    max_retries: 3

  openai:
    model: gpt-4
    max_retries: 3

  gemini:
    model: gemini-pro
    max_retries: 3

prompt_templates:
  dependency_fix: './templates/dependency_fix.txt'
  syntax_fix: './templates/syntax_fix.txt'
  configuration_fix: './templates/configuration_fix.txt'
```

## 🚨 Error Handling

### Common Error Scenarios

1. **AI provider rate limiting**
   - Implement exponential backoff
   - Fallback to secondary providers

2. **Invalid AI responses**
   - Attempt response parsing with multiple strategies
   - Request refined response from AI

3. **Validation failures**
   - Automatic refinement attempts
   - Manual escalation for critical issues

### Recovery Strategies

- Multi-provider fallback chain
- Response format normalization
- Automatic suggestion refinement
- Graceful degradation for partial failures

## 📝 Implementation Checklist

- [ ] Define core interfaces and enums
- [ ] Implement AIFixSuggestionGenerator class
- [ ] Create prompt templates for each fix category
- [ ] Implement FixValidator with security checks
- [ ] Create MultiProviderFixGenerator with fallback
- [ ] Add comprehensive error handling
- [ ] Write unit tests for all components
- [ ] Write integration tests with real AI providers
- [ ] Add monitoring and metrics
- [ ] Create configuration management
- [ ] Document security considerations
- [ ] Add rate limiting and cost controls

---

**Dependencies**: Task 3 (Build Failure Analysis)  
**Blocked By**: Epic 1 (AI Provider Interface)  
**Blocks**: Task 5 (Fix Application and Commit)
