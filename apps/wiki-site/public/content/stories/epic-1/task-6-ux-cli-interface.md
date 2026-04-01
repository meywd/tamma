---
title: "Task 6: CLI Interface UX Enhancement"
---

## Objective

Enhance the command-line interface with user-friendly interactive prompts, progress indicators, help system, and visual feedback to improve developer experience.

## Acceptance Criteria

- [ ] Interactive setup wizard for first-time configuration
- [ ] Colored output with semantic meaning (success/error/warning/info)
- [ ] Progress bars for long-running operations
- [ ] Interactive provider selection and configuration
- [ ] Contextual help system with examples
- [ ] Command auto-completion support
- [ ] Error messages with actionable suggestions
- [ ] Verbose/quiet output modes
- [ ] ASCII art branding and version display
- [ ] Configuration validation with inline feedback

## Technical Implementation

### CLI Enhancement Framework

```typescript
// CLI UX interfaces
export interface CLIContext {
  mode: 'interactive' | 'silent' | 'verbose';
  colors: boolean;
  progress: boolean;
  config: TammaConfig;
}

export interface ProgressIndicator {
  start(message: string): void;
  update(percent: number, message?: string): void;
  complete(message?: string): void;
  error(message: string): void;
}

export interface InteractivePrompts {
  select(message: string, choices: Choice[]): Promise<string>;
  input(message: string, options?: InputOptions): Promise<string>;
  confirm(message: string, default?: boolean): Promise<boolean>;
  multiSelect(message: string, choices: Choice[]): Promise<string[]>;
  password(message: string): Promise<string>;
}

export interface Choice {
  name: string;
  value: string;
  description?: string;
  disabled?: boolean;
}

export interface InputOptions {
  default?: string;
  validate?: (value: string) => string | true;
  transform?: (value: string) => string;
}
```

### Enhanced CLI Application

```typescript
export class EnhancedCLI {
  private ctx: CLIContext;
  private progress: ProgressIndicator;
  private prompts: InteractivePrompts;
  private output: OutputFormatter;

  constructor() {
    this.ctx = this.initializeContext();
    this.progress = new ProgressBarIndicator(this.ctx);
    this.prompts = new InquirerPrompts(this.ctx);
    this.output = new ColoredOutput(this.ctx);
  }

  async run(argv: string[]): Promise<void> {
    try {
      // Display branding
      this.displayBranding();

      // Handle help/version
      if (argv.includes('--help') || argv.includes('-h')) {
        this.displayHelp();
        return;
      }

      if (argv.includes('--version') || argv.includes('-v')) {
        this.displayVersion();
        return;
      }

      // Check for first-time setup
      if (!(await this.hasConfiguration())) {
        await this.runSetupWizard();
        return;
      }

      // Parse and execute commands
      const command = await this.parseCommand(argv);
      await this.executeCommand(command);
    } catch (error) {
      this.handleError(error);
    }
  }

  private displayBranding(): void {
    if (!this.ctx.colors) return;

    console.log(`
${chalk.cyan('╔══════════════════════════════════════════════════════════════╗')}
${chalk.cyan('║')}                                                              ${chalk.cyan('║')}
${chalk.cyan('║')}  ${chalk.bold.green('Tamma')} - ${chalk.blue('AI-Powered Development Orchestration')}        ${chalk.cyan('║')}
${chalk.cyan('║')}                                                              ${chalk.cyan('║')}
${chalk.cyan('║')}  ${chalk.gray('From GitHub issue to merged PR—completely autonomous')}      ${chalk.cyan('║')}
${chalk.cyan('║')}                                                              ${chalk.cyan('║')}
${chalk.cyan('╚══════════════════════════════════════════════════════════════╝')}
${chalk.dim(`Version: ${packageInfo.version} | Node: ${process.version}`)}
    `);
  }

  private async runSetupWizard(): Promise<void> {
    this.output.info("🚀 Welcome to Tamma! Let's get you set up.");

    const config = await this.gatherConfiguration();

    this.progress.start('Saving configuration...');
    await this.saveConfiguration(config);
    this.progress.complete('Configuration saved successfully!');

    this.output.success('✅ Setup complete! You can now run Tamma commands.');
    this.output.info('💡 Run "tamma --help" to see available commands.');
  }

  private async gatherConfiguration(): Promise<Partial<TammaConfig>> {
    const config: Partial<TammaConfig> = {};

    // Mode selection
    config.mode = await this.prompts.select('Select deployment mode:', [
      {
        name: 'Standalone',
        value: 'standalone',
        description: 'Run everything on your local machine',
      },
      {
        name: 'Orchestrator',
        value: 'orchestrator',
        description: 'Coordinate distributed workers',
      },
      {
        name: 'Worker',
        value: 'worker',
        description: 'Execute tasks from an orchestrator',
      },
    ]);

    // AI Provider setup
    const setupAI = await this.prompts.confirm('Do you want to configure an AI provider now?');
    if (setupAI) {
      config.providers = await this.setupAIProviders();
    }

    // Git Platform setup
    const setupGit = await this.prompts.confirm('Do you want to configure a Git platform now?');
    if (setupGit) {
      config.platforms = await this.setupGitPlatforms();
    }

    // Project directory
    config.projectDir = await this.prompts.input('Project directory:', { default: process.cwd() });

    return config;
  }

  private async setupAIProviders(): Promise<ProviderConfig[]> {
    const providers: ProviderConfig[] = [];
    let adding = true;

    while (adding) {
      const providerType = await this.prompts.select('Select AI provider type:', [
        { name: 'Anthropic Claude', value: 'anthropic' },
        { name: 'OpenAI', value: 'openai' },
        { name: 'GitHub Copilot', value: 'github-copilot' },
        { name: 'Google Gemini', value: 'gemini' },
        { name: 'Local LLM', value: 'local' },
      ]);

      const provider = await this.configureAIProvider(providerType);
      providers.push(provider);

      adding = await this.prompts.confirm('Add another AI provider?');
    }

    return providers;
  }

  private async configureAIProvider(type: string): Promise<ProviderConfig> {
    this.output.info(`\n🤖 Configuring ${type} provider...`);

    const name = await this.prompts.input('Provider name:', {
      default: type,
      validate: (value) => (value.trim() ? true : 'Name is required'),
    });

    const apiKey = await this.prompts.password('API key:', {
      validate: (value) => (value.trim() ? true : 'API key is required'),
    });

    const endpoint = await this.prompts.input('API endpoint (optional):');

    // Test connection
    this.progress.start('Testing connection...');
    const testResult = await this.testProviderConnection({ type, name, apiKey, endpoint });

    if (testResult.success) {
      this.progress.complete('✅ Connection successful!');
    } else {
      this.progress.error('❌ Connection failed');
      const retry = await this.prompts.confirm(
        'Would you like to retry with different credentials?'
      );
      if (retry) {
        return this.configureAIProvider(type);
      }
    }

    return { type, name, apiKey, endpoint, enabled: true };
  }
}
```

### Progress Indicators

```typescript
export class ProgressBarIndicator implements ProgressIndicator {
  private bar: cliProgress.SingleBar;
  private isActive = false;

  constructor(private ctx: CLIContext) {
    this.bar = new cliProgress.SingleBar({
      format: `${ctx.colors ? '{bar}' : 'Progress'} | {percentage}% | {value}/{total} | {status}`,
      barCompleteChar: '█',
      barIncompleteChar: '░',
      hideCursor: true,
    });
  }

  start(message: string, total = 100): void {
    if (!this.ctx.progress) return;

    this.isActive = true;
    if (message && this.ctx.colors) {
      console.log(chalk.blue(message));
    }

    this.bar.start(total, 0, { status: 'Initializing...' });
  }

  update(current: number, message?: string): void {
    if (!this.isActive) return;

    this.bar.update(current, {
      status: message || 'Processing...',
    });
  }

  complete(message?: string): void {
    if (!this.isActive) return;

    this.bar.update(this.bar.getTotal(), { status: 'Complete' });
    this.bar.stop();
    this.isActive = false;

    if (message && this.ctx.colors) {
      console.log(chalk.green(`✅ ${message}`));
    }
  }

  error(message: string): void {
    if (!this.isActive) return;

    this.bar.stop();
    this.isActive = false;

    if (this.ctx.colors) {
      console.log(chalk.red(`❌ ${message}`));
    }
  }
}

export class SpinnerIndicator implements ProgressIndicator {
  private spinner: ora.Ora;
  private isActive = false;

  constructor(private ctx: CLIContext) {
    this.spinner = ora({
      color: this.ctx.colors ? 'blue' : 'white',
      spinner: 'dots',
    });
  }

  start(message: string): void {
    if (!this.ctx.progress) return;

    this.isActive = true;
    this.spinner.start(message);
  }

  update(percent: number, message?: string): void {
    if (!this.isActive) return;

    const displayMessage = message ? `${message} (${percent}%)` : `Progress: ${percent}%`;

    this.spinner.text = displayMessage;
  }

  complete(message?: string): void {
    if (!this.isActive) return;

    this.spinner.succeed(message || 'Complete');
    this.isActive = false;
  }

  error(message: string): void {
    if (!this.isActive) return;

    this.spinner.fail(message);
    this.isActive = false;
  }
}
```

### Interactive Prompts

```typescript
export class InquirerPrompts implements InteractivePrompts {
  constructor(private ctx: CLIContext) {}

  async select(message: string, choices: Choice[]): Promise<string> {
    if (this.ctx.mode === 'silent') {
      return choices[0]?.value || '';
    }

    const { answer } = await inquirer.prompt([
      {
        type: 'list',
        name: 'answer',
        message: this.formatMessage(message),
        choices: choices.map((choice) => ({
          name: `${choice.name}${choice.description ? chalk.dim(` - ${choice.description}`) : ''}`,
          value: choice.value,
          disabled: choice.disabled,
        })),
      },
    ]);

    return answer;
  }

  async input(message: string, options: InputOptions = {}): Promise<string> {
    if (this.ctx.mode === 'silent' && options.default) {
      return options.default;
    }

    const { answer } = await inquirer.prompt([
      {
        type: 'input',
        name: 'answer',
        message: this.formatMessage(message),
        default: options.default,
        validate: options.validate
          ? (input) => {
              const result = options.validate!(input);
              return result === true ? true : result;
            }
          : undefined,
        filter: options.transform,
      },
    ]);

    return answer;
  }

  async confirm(message: string, defaultValue = false): Promise<boolean> {
    if (this.ctx.mode === 'silent') {
      return defaultValue;
    }

    const { answer } = await inquirer.prompt([
      {
        type: 'confirm',
        name: 'answer',
        message: this.formatMessage(message),
        default: defaultValue,
      },
    ]);

    return answer;
  }

  async multiSelect(message: string, choices: Choice[]): Promise<string[]> {
    if (this.ctx.mode === 'silent') {
      return choices.filter((c) => !c.disabled).map((c) => c.value);
    }

    const { answer } = await inquirer.prompt([
      {
        type: 'checkbox',
        name: 'answer',
        message: this.formatMessage(message),
        choices: choices.map((choice) => ({
          name: `${choice.name}${choice.description ? chalk.dim(` - ${choice.description}`) : ''}`,
          value: choice.value,
          disabled: choice.disabled,
        })),
      },
    ]);

    return answer;
  }

  async password(message: string): Promise<string> {
    if (this.ctx.mode === 'silent') {
      throw new Error('Password input not available in silent mode');
    }

    const { answer } = await inquirer.prompt([
      {
        type: 'password',
        name: 'answer',
        message: this.formatMessage(message),
        mask: '*',
      },
    ]);

    return answer;
  }

  private formatMessage(message: string): string {
    return this.ctx.colors ? message : chalk.stripColor(message);
  }
}
```

### Colored Output System

```typescript
export class ColoredOutput {
  constructor(private ctx: CLIContext) {}

  success(message: string): void {
    this.log(message, 'green', '✅');
  }

  error(message: string): void {
    this.log(message, 'red', '❌');
  }

  warning(message: string): void {
    this.log(message, 'yellow', '⚠️');
  }

  info(message: string): void {
    this.log(message, 'blue', 'ℹ️');
  }

  debug(message: string): void {
    if (this.ctx.mode === 'verbose') {
      this.log(message, 'gray', '🐛');
    }
  }

  plain(message: string): void {
    console.log(message);
  }

  private log(message: string, color: string, icon: string): void {
    if (!this.ctx.colors) {
      console.log(`${icon} ${message}`);
      return;
    }

    const coloredMessage = chalk[color](message);
    console.log(`${icon} ${coloredMessage}`);
  }

  table(data: Record<string, any>[], options: TableOptions = {}): void {
    const table = new cliTable3.Table({
      chars: this.ctx.colors
        ? {
            top: '─',
            'top-mid': '┬',
            'top-left': '┌',
            'top-right': '┐',
            bottom: '─',
            'bottom-mid': '┴',
            'bottom-left': '└',
            'bottom-right': '┘',
            left: '│',
            'left-mid': '├',
            mid: '─',
            'mid-mid': '┼',
            right: '│',
            'right-mid': '┤',
            middle: '│',
          }
        : {},
      style: {
        head: this.ctx.colors ? ['blue'] : [],
        border: this.ctx.colors ? ['gray'] : [],
      },
    });

    if (data.length > 0) {
      table.addRows(data);
      console.log(table.toString());
    } else {
      this.info('No data to display');
    }
  }
}
```

### Enhanced Help System

```typescript
export class HelpSystem {
  constructor(private output: ColoredOutput) {}

  displayMainHelp(): void {
    const help = `
${chalk.bold.blue('Usage:')}
  tamma [command] [options]

${chalk.bold.blue('Commands:')}
  ${chalk.cyan('init')}        Initialize Tamma configuration
  ${chalk.cyan('run')}         Run autonomous development
  ${chalk.cyan('status')}      Show current status
  ${chalk.cyan('config')}      Manage configuration
  ${chalk.cyan('providers')}   List and test providers
  ${chalk.cyan('logs')}        View development logs
  ${chalk.cyan('version')}     Show version information

${chalk.bold.blue('Options:')}
  ${chalk.yellow('--help, -h')}     Show this help message
  ${chalk.yellow('--version, -v')}  Show version information
  ${chalk.yellow('--verbose')}       Enable verbose output
  ${chalk.yellow('--quiet')}         Suppress non-error output
  ${chalk.yellow('--no-color')}      Disable colored output
  ${chalk.yellow('--mode')}         Set operation mode (standalone|orchestrator|worker)

${chalk.bold.blue('Examples:')}
  ${chalk.gray('# Initialize configuration')}
  tamma init

  ${chalk.gray('# Run in standalone mode')}
  tamma run --mode standalone

  ${chalk.gray('# Check status with verbose output')}
  tamma status --verbose

  ${chalk.gray('# Configure providers interactively')}
  tamma config providers

${chalk.bold.blue('Getting Help:')}
  ${chalk.gray('# Get help for a specific command')}
  tamma <command> --help

  ${chalk.gray('# View detailed documentation')}
  ${chalk.underline('https://docs.tamma.dev')}
    `;

    console.log(help);
  }

  displayCommandHelp(command: string): void {
    const commandHelp = this.getCommandHelp(command);
    if (!commandHelp) {
      this.output.error(`Unknown command: ${command}`);
      this.displayMainHelp();
      return;
    }

    const help = `
${chalk.bold.blue(`Command: ${command}`)}
${commandHelp.description}

${chalk.bold.blue('Usage:')}
  tamma ${command} ${commandHelp.usage}

${
  commandHelp.options.length > 0
    ? `
${chalk.bold.blue('Options:')}
${commandHelp.options
  .map((opt) => `  ${chalk.yellow(opt.flag)}${opt.description ? ` - ${opt.description}` : ''}`)
  .join('\n')}
`
    : ''
}

${
  commandHelp.examples.length > 0
    ? `
${chalk.bold.blue('Examples:')}
${commandHelp.examples.map((ex) => `  ${chalk.gray(ex)}`).join('\n')}
`
    : ''
}
    `;

    console.log(help);
  }

  private getCommandHelp(command: string): CommandHelp | null {
    const commands: Record<string, CommandHelp> = {
      init: {
        description: 'Initialize Tamma configuration for the current project',
        usage: '[options]',
        options: [
          { flag: '--mode <mode>', description: 'Set deployment mode' },
          { flag: '--interactive', description: 'Run interactive setup (default)' },
          { flag: '--non-interactive', description: 'Run non-interactive setup' },
        ],
        examples: ['tamma init', 'tamma init --mode orchestrator', 'tamma init --non-interactive'],
      },
      run: {
        description: 'Run autonomous development process',
        usage: '[options] [issue-url]',
        options: [
          { flag: '--mode <mode>', description: 'Override configured mode' },
          { flag: '--dry-run', description: 'Simulate without making changes' },
          { flag: '--issue <url>', description: 'Specific issue to process' },
        ],
        examples: [
          'tamma run',
          'tamma run --issue https://github.com/user/repo/issues/123',
          'tamma run --dry-run',
        ],
      },
      status: {
        description: 'Display current system status and configuration',
        usage: '[options]',
        options: [
          { flag: '--verbose', description: 'Show detailed status' },
          { flag: '--json', description: 'Output in JSON format' },
        ],
        examples: ['tamma status', 'tamma status --verbose', 'tamma status --json'],
      },
    };

    return commands[command] || null;
  }
}
```

### Error Handling with Suggestions

```typescript
export class ErrorHandler {
  constructor(private output: ColoredOutput) {}

  handle(error: Error): void {
    if (error instanceof ConfigurationError) {
      this.handleConfigurationError(error);
    } else if (error instanceof ProviderError) {
      this.handleProviderError(error);
    } else if (error instanceof NetworkError) {
      this.handleNetworkError(error);
    } else {
      this.handleGenericError(error);
    }
  }

  private handleConfigurationError(error: ConfigurationError): void {
    this.output.error(`Configuration Error: ${error.message}`);

    const suggestions = this.getConfigurationSuggestions(error);
    if (suggestions.length > 0) {
      this.output.info('\n💡 Suggestions:');
      suggestions.forEach((suggestion, index) => {
        console.log(`  ${index + 1}. ${suggestion}`);
      });
    }

    this.output.info('\n📖 Run "tamma config --help" for configuration assistance.');
  }

  private handleProviderError(error: ProviderError): void {
    this.output.error(`Provider Error: ${error.message}`);

    if (error.code === 'AUTHENTICATION_FAILED') {
      this.output.info('\n💡 Suggestions:');
      console.log('  1. Check your API key is correct');
      console.log('  2. Verify the API key has required permissions');
      console.log('  3. Ensure your account is in good standing');
      console.log('  4. Try "tamma providers test <provider>" to diagnose');
    } else if (error.code === 'RATE_LIMITED') {
      this.output.info('\n💡 Suggestions:');
      console.log('  1. Wait and try again later');
      console.log('  2. Check your usage limits');
      console.log('  3. Consider upgrading your plan');
    }
  }

  private handleNetworkError(error: NetworkError): void {
    this.output.error(`Network Error: ${error.message}`);

    this.output.info('\n💡 Suggestions:');
    console.log('  1. Check your internet connection');
    console.log('  2. Verify firewall settings');
    console.log('  3. Try again in a few moments');
    console.log('  4. Check service status at status.tamma.dev');
  }

  private getConfigurationSuggestions(error: ConfigurationError): string[] {
    const suggestions: string[] = [];

    if (error.field === 'apiKey') {
      suggestions.push('Run "tamma config providers" to update API key');
      suggestions.push('Check environment variables for TAMMA_API_KEY');
    } else if (error.field === 'mode') {
      suggestions.push('Valid modes: standalone, orchestrator, worker');
      suggestions.push('Use "tamma init" to reconfigure');
    } else if (error.field === 'repository') {
      suggestions.push("Ensure you're in a Git repository");
      suggestions.push('Run "git init" if this is a new project');
    }

    return suggestions;
  }
}
```

## Testing Strategy

### CLI Interaction Tests

```typescript
describe('CLI Setup Wizard', () => {
  let cli: EnhancedCLI;
  let mockInquirer: jest.Mocked<typeof inquirer>;

  beforeEach(() => {
    mockInquirer = inquirer as jest.Mocked<typeof inquirer>;
    cli = new EnhancedCLI();
  });

  it('should guide through first-time setup', async () => {
    mockInquirer.prompt
      .mockResolvedValueOnce({ answer: 'standalone' })
      .mockResolvedValueOnce({ answer: 'claude' })
      .mockResolvedValueOnce({ answer: 'test-key' })
      .mockResolvedValueOnce({ answer: false });

    await cli.run(['init']);

    expect(mockInquirer.prompt).toHaveBeenCalledTimes(4);
  });

  it('should validate API key format', async () => {
    mockInquirer.prompt.mockResolvedValueOnce({ answer: 'invalid-key' });

    await cli.run(['init']);

    expect(mockInquirer.prompt).toHaveBeenCalledWith(
      expect.objectContaining({
        validate: expect.any(Function),
      })
    );
  });
});
```

### Output Formatting Tests

```typescript
describe('ColoredOutput', () => {
  let output: ColoredOutput;
  let mockConsole: jest.Mocked<typeof console>;

  beforeEach(() => {
    mockConsole = console as jest.Mocked<typeof console>;
    output = new ColoredOutput({ colors: true, mode: 'interactive' });
  });

  it('should display success message with icon', () => {
    output.success('Test completed');
    expect(mockConsole.log).toHaveBeenCalledWith('✅ Test completed');
  });

  it('should display error message with icon', () => {
    output.error('Something went wrong');
    expect(mockConsole.log).toHaveBeenCalledWith('❌ Something went wrong');
  });

  it('should handle colorless mode', () => {
    const colorlessOutput = new ColoredOutput({ colors: false, mode: 'interactive' });
    colorlessOutput.success('Test');
    expect(mockConsole.log).toHaveBeenCalledWith('✅ Test');
  });
});
```

## Implementation Checklist

- [ ] Create enhanced CLI application class
- [ ] Implement interactive setup wizard
- [ ] Add progress indicators (progress bar and spinner)
- [ ] Build colored output system
- [ ] Create comprehensive help system
- [ ] Implement error handling with suggestions
- [ ] Add command auto-completion
- [ ] Create ASCII art branding
- [ ] Build configuration validation
- [ ] Add verbose/quiet output modes
- [ ] Implement provider testing interface
- [ ] Create interactive prompts system
- [ ] Add table formatting utilities
- [ ] Build comprehensive test suite
- [ ] Add accessibility features
- [ ] Create documentation and examples
