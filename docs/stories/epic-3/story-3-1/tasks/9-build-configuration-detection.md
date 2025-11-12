# Task 9: Build Configuration Detection

## Objective

Implement automatic detection and parsing of build configuration files across different build systems and languages, with support for custom configurations and build matrix detection.

## Acceptance Criteria

- [ ] Detect GitHub Actions workflow files
- [ ] Detect GitLab CI/CD configuration files
- [ ] Detect Jenkins pipeline files
- [ ] Detect CircleCI configuration files
- [ ] Detect Azure DevOps pipeline files
- [ ] Parse Docker configuration files
- [ ] Detect Makefile and CMake configurations
- [ ] Parse package.json build scripts
- [ ] Detect Gradle and Maven configurations
- [ ] Parse Cargo.toml for Rust projects
- [ ] Detect Go module configurations
- [ ] Support custom build configuration patterns
- [ ] Extract build matrix and parallel execution info
- [ ] Detect environment variable configurations
- [ ] Parse dependency and caching configurations

## Technical Implementation

### Core Interfaces

```typescript
// Build configuration types
export enum BuildConfigType {
  GITHUB_ACTIONS = 'github-actions',
  GITLAB_CI = 'gitlab-ci',
  JENKINS = 'jenkins',
  CIRCLECI = 'circleci',
  AZURE_DEVOPS = 'azure-devops',
  DOCKER = 'docker',
  MAKEFILE = 'makefile',
  CMAKE = 'cmake',
  PACKAGE_JSON = 'package-json',
  GRADLE = 'gradle',
  MAVEN = 'maven',
  CARGO = 'cargo',
  GO_MOD = 'go-mod',
  CUSTOM = 'custom',
}

// Build configuration file
export interface BuildConfigFile {
  type: BuildConfigType;
  path: string;
  content: string;
  format: 'yaml' | 'json' | 'toml' | 'xml' | 'makefile' | 'dockerfile';
  parsed: any;
  isValid: boolean;
  errors: string[];
}

// Build configuration analysis
export interface BuildConfigAnalysis {
  files: BuildConfigFile[];
  primaryConfig: BuildConfigFile | null;
  buildTriggers: BuildTrigger[];
  buildSteps: BuildStep[];
  testSteps: BuildStep[];
  deploySteps: BuildStep[];
  environmentVariables: Record<string, string>;
  secrets: string[];
  dependencies: BuildDependency[];
  cache: BuildCache[];
  matrix: BuildMatrix | null;
  parallelism: ParallelismConfig | null;
  artifacts: BuildArtifact[];
  notifications: NotificationConfig[];
}

// Build trigger
export interface BuildTrigger {
  type: 'push' | 'pull_request' | 'schedule' | 'manual' | 'webhook';
  branches?: string[];
  paths?: string[];
  tags?: string[];
  schedule?: string;
  conditions?: string[];
}

// Build step
export interface BuildStep {
  name: string;
  command: string;
  condition?: string;
  timeout?: number;
  retryCount?: number;
  continueOnError?: boolean;
  workingDirectory?: string;
  environment?: Record<string, string>;
  dependsOn?: string[];
}

// Build matrix
export interface BuildMatrix {
  axis: Record<string, string[]>;
  exclude?: Record<string, string>[];
  include?: Record<string, string>[];
  maxParallel: number;
  failFast: boolean;
}

// Parallelism configuration
export interface ParallelismConfig {
  type: 'matrix' | 'parallel' | 'fan-out';
  maxParallel: number;
  strategy: string;
}

// Build dependency
export interface BuildDependency {
  name: string;
  version?: string;
  type: 'runtime' | 'development' | 'test' | 'build';
  source: string;
}

// Build cache
export interface BuildCache {
  path: string;
  key: string;
  restoreKeys?: string[];
  scope: 'job' | 'workflow' | 'branch';
}

// Build artifact
export interface BuildArtifact {
  name: string;
  path: string;
  retentionDays?: number;
  condition?: string;
}

// Notification configuration
export interface NotificationConfig {
  type: 'email' | 'slack' | 'discord' | 'teams' | 'webhook';
  condition: string;
  config: Record<string, any>;
}
```

### Build Configuration Detector

```typescript
export class BuildConfigDetector {
  private parsers: Map<BuildConfigType, ConfigParser> = new Map();
  private filePatterns: Map<BuildConfigType, string[]> = new Map();

  constructor() {
    this.initializeParsers();
    this.initializeFilePatterns();
  }

  private initializeParsers(): void {
    this.parsers.set(BuildConfigType.GITHUB_ACTIONS, new GitHubActionsParser());
    this.parsers.set(BuildConfigType.GITLAB_CI, new GitLabCIParser());
    this.parsers.set(BuildConfigType.JENKINS, new JenkinsParser());
    this.parsers.set(BuildConfigType.CIRCLECI, new CircleCIParser());
    this.parsers.set(BuildConfigType.AZURE_DEVOPS, new AzureDevOpsParser());
    this.parsers.set(BuildConfigType.DOCKER, new DockerParser());
    this.parsers.set(BuildConfigType.MAKEFILE, new MakefileParser());
    this.parsers.set(BuildConfigType.CMAKE, new CMakeParser());
    this.parsers.set(BuildConfigType.PACKAGE_JSON, new PackageJSONParser());
    this.parsers.set(BuildConfigType.GRADLE, new GradleParser());
    this.parsers.set(BuildConfigType.MAVEN, new MavenParser());
    this.parsers.set(BuildConfigType.CARGO, new CargoParser());
    this.parsers.set(BuildConfigType.GO_MOD, new GoModParser());
  }

  private initializeFilePatterns(): void {
    this.filePatterns.set(BuildConfigType.GITHUB_ACTIONS, [
      '.github/workflows/*.yml',
      '.github/workflows/*.yaml',
    ]);
    this.filePatterns.set(BuildConfigType.GITLAB_CI, ['.gitlab-ci.yml', '.gitlab-ci.yaml']);
    this.filePatterns.set(BuildConfigType.JENKINS, [
      'Jenkinsfile',
      'Jenkinsfile.*',
      '**/*.jenkinsfile',
    ]);
    this.filePatterns.set(BuildConfigType.CIRCLECI, [
      '.circleci/config.yml',
      '.circleci/config.yaml',
    ]);
    this.filePatterns.set(BuildConfigType.AZURE_DEVOPS, [
      '.azure/pipelines/*.yml',
      '.azure/pipelines/*.yaml',
      'azure-pipelines.yml',
      'azure-pipelines.yaml',
    ]);
    this.filePatterns.set(BuildConfigType.DOCKER, [
      'Dockerfile*',
      'docker-compose*.yml',
      'docker-compose*.yaml',
      'docker-compose*.json',
    ]);
    this.filePatterns.set(BuildConfigType.MAKEFILE, [
      'Makefile',
      'makefile',
      'GNUmakefile',
      '**/Makefile',
      '**/makefile',
    ]);
    this.filePatterns.set(BuildConfigType.CMAKE, [
      'CMakeLists.txt',
      '**/CMakeLists.txt',
      '*.cmake',
    ]);
    this.filePatterns.set(BuildConfigType.PACKAGE_JSON, ['package.json']);
    this.filePatterns.set(BuildConfigType.GRADLE, [
      'build.gradle',
      'build.gradle.kts',
      'settings.gradle',
      'settings.gradle.kts',
      '**/build.gradle',
      '**/build.gradle.kts',
    ]);
    this.filePatterns.set(BuildConfigType.MAVEN, ['pom.xml', '**/pom.xml']);
    this.filePatterns.set(BuildConfigType.CARGO, ['Cargo.toml', 'Cargo.lock']);
    this.filePatterns.set(BuildConfigType.GO_MOD, ['go.mod', 'go.sum']);
  }

  async detectBuildConfigs(repositoryPath: string): Promise<BuildConfigAnalysis> {
    const configFiles = await this.scanConfigFiles(repositoryPath);
    const parsedFiles = await this.parseConfigFiles(configFiles);
    const primaryConfig = this.determinePrimaryConfig(parsedFiles);

    return {
      files: parsedFiles,
      primaryConfig,
      buildTriggers: this.extractBuildTriggers(parsedFiles),
      buildSteps: this.extractBuildSteps(parsedFiles),
      testSteps: this.extractTestSteps(parsedFiles),
      deploySteps: this.extractDeploySteps(parsedFiles),
      environmentVariables: this.extractEnvironmentVariables(parsedFiles),
      secrets: this.extractSecrets(parsedFiles),
      dependencies: this.extractDependencies(parsedFiles),
      cache: this.extractCache(parsedFiles),
      matrix: this.extractMatrix(parsedFiles),
      parallelism: this.extractParallelism(parsedFiles),
      artifacts: this.extractArtifacts(parsedFiles),
      notifications: this.extractNotifications(parsedFiles),
    };
  }

  private async scanConfigFiles(repositoryPath: string): Promise<BuildConfigFile[]> {
    const files: BuildConfigFile[] = [];

    for (const [configType, patterns] of this.filePatterns) {
      for (const pattern of patterns) {
        const matchedFiles = await this.globFiles(repositoryPath, pattern);

        for (const filePath of matchedFiles) {
          try {
            const content = await fs.readFile(filePath, 'utf-8');
            const format = this.detectFileFormat(filePath);

            files.push({
              type: configType,
              path: filePath,
              content,
              format,
              parsed: null,
              isValid: false,
              errors: [],
            });
          } catch (error) {
            console.warn(`Failed to read config file ${filePath}: ${error.message}`);
          }
        }
      }
    }

    return files;
  }

  private async globFiles(repositoryPath: string, pattern: string): Promise<string[]> {
    const { glob } = await import('glob');
    const fullPattern = path.join(repositoryPath, pattern);
    return glob(fullPattern, { ignore: '**/node_modules/**' });
  }

  private detectFileFormat(
    filePath: string
  ): 'yaml' | 'json' | 'toml' | 'xml' | 'makefile' | 'dockerfile' {
    const ext = path.extname(filePath).toLowerCase();
    const basename = path.basename(filePath).toLowerCase();

    if (['.yml', '.yaml'].includes(ext)) return 'yaml';
    if (['.json'].includes(ext)) return 'json';
    if (['.toml'].includes(ext)) return 'toml';
    if (['.xml'].includes(ext)) return 'xml';
    if (basename.startsWith('makefile') || basename === 'makefile') return 'makefile';
    if (basename.startsWith('dockerfile')) return 'dockerfile';

    return 'yaml'; // Default
  }

  private async parseConfigFiles(files: BuildConfigFile[]): Promise<BuildConfigFile[]> {
    const parsedFiles = [...files];

    for (const file of parsedFiles) {
      const parser = this.parsers.get(file.type);
      if (parser) {
        try {
          file.parsed = await parser.parse(file.content, file.format);
          file.isValid = true;
        } catch (error) {
          file.isValid = false;
          file.errors = [error.message];
        }
      }
    }

    return parsedFiles;
  }

  private determinePrimaryConfig(files: BuildConfigFile[]): BuildConfigFile | null {
    // Priority order for primary config
    const priority = [
      BuildConfigType.GITHUB_ACTIONS,
      BuildConfigType.GITLAB_CI,
      BuildConfigType.JENKINS,
      BuildConfigType.CIRCLECI,
      BuildConfigType.AZURE_DEVOPS,
      BuildConfigType.DOCKER,
      BuildConfigType.MAKEFILE,
      BuildConfigType.CMAKE,
      BuildConfigType.PACKAGE_JSON,
      BuildConfigType.GRADLE,
      BuildConfigType.MAVEN,
      BuildConfigType.CARGO,
      BuildConfigType.GO_MOD,
    ];

    for (const configType of priority) {
      const file = files.find((f) => f.type === configType && f.isValid);
      if (file) {
        return file;
      }
    }

    return null;
  }

  private extractBuildTriggers(files: BuildConfigFile[]): BuildTrigger[] {
    const triggers: BuildTrigger[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractTriggers) {
        const fileTriggers = parser.extractTriggers(file.parsed);
        triggers.push(...fileTriggers);
      }
    }

    return triggers;
  }

  private extractBuildSteps(files: BuildConfigFile[]): BuildStep[] {
    const steps: BuildStep[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractBuildSteps) {
        const fileSteps = parser.extractBuildSteps(file.parsed);
        steps.push(...fileSteps);
      }
    }

    return steps;
  }

  private extractTestSteps(files: BuildConfigFile[]): BuildStep[] {
    const steps: BuildStep[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractTestSteps) {
        const fileSteps = parser.extractTestSteps(file.parsed);
        steps.push(...fileSteps);
      }
    }

    return steps;
  }

  private extractDeploySteps(files: BuildConfigFile[]): BuildStep[] {
    const steps: BuildStep[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractDeploySteps) {
        const fileSteps = parser.extractDeploySteps(file.parsed);
        steps.push(...fileSteps);
      }
    }

    return steps;
  }

  private extractEnvironmentVariables(files: BuildConfigFile[]): Record<string, string> {
    const envVars: Record<string, string> = {};

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractEnvironmentVariables) {
        const fileEnvVars = parser.extractEnvironmentVariables(file.parsed);
        Object.assign(envVars, fileEnvVars);
      }
    }

    return envVars;
  }

  private extractSecrets(files: BuildConfigFile[]): string[] {
    const secrets: string[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractSecrets) {
        const fileSecrets = parser.extractSecrets(file.parsed);
        secrets.push(...fileSecrets);
      }
    }

    return [...new Set(secrets)]; // Remove duplicates
  }

  private extractDependencies(files: BuildConfigFile[]): BuildDependency[] {
    const dependencies: BuildDependency[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractDependencies) {
        const fileDeps = parser.extractDependencies(file.parsed);
        dependencies.push(...fileDeps);
      }
    }

    return dependencies;
  }

  private extractCache(files: BuildConfigFile[]): BuildCache[] {
    const cache: BuildCache[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractCache) {
        const fileCache = parser.extractCache(file.parsed);
        cache.push(...fileCache);
      }
    }

    return cache;
  }

  private extractMatrix(files: BuildConfigFile[]): BuildMatrix | null {
    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractMatrix) {
        const matrix = parser.extractMatrix(file.parsed);
        if (matrix) {
          return matrix;
        }
      }
    }

    return null;
  }

  private extractParallelism(files: BuildConfigFile[]): ParallelismConfig | null {
    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractParallelism) {
        const parallelism = parser.extractParallelism(file.parsed);
        if (parallelism) {
          return parallelism;
        }
      }
    }

    return null;
  }

  private extractArtifacts(files: BuildConfigFile[]): BuildArtifact[] {
    const artifacts: BuildArtifact[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractArtifacts) {
        const fileArtifacts = parser.extractArtifacts(file.parsed);
        artifacts.push(...fileArtifacts);
      }
    }

    return artifacts;
  }

  private extractNotifications(files: BuildConfigFile[]): NotificationConfig[] {
    const notifications: NotificationConfig[] = [];

    for (const file of files) {
      if (!file.isValid || !file.parsed) continue;

      const parser = this.parsers.get(file.type);
      if (parser && parser.extractNotifications) {
        const fileNotifications = parser.extractNotifications(file.parsed);
        notifications.push(...fileNotifications);
      }
    }

    return notifications;
  }
}
```

### GitHub Actions Parser Implementation

```typescript
export class GitHubActionsParser implements ConfigParser {
  async parse(content: string, format: string): Promise<any> {
    if (format !== 'yaml') {
      throw new Error('GitHub Actions only supports YAML format');
    }

    const yaml = await import('js-yaml');
    return yaml.load(content);
  }

  extractTriggers(parsed: any): BuildTrigger[] {
    const triggers: BuildTrigger[] = [];
    const on = parsed.on;

    if (!on) return triggers;

    // Push triggers
    if (on.push) {
      triggers.push({
        type: 'push',
        branches: Array.isArray(on.push.branches)
          ? on.push.branches
          : [on.push.branches].filter(Boolean),
        paths: Array.isArray(on.push.paths) ? on.push.paths : [on.push.paths].filter(Boolean),
        tags: Array.isArray(on.push.tags) ? on.push.tags : [on.push.tags].filter(Boolean),
      });
    }

    // Pull request triggers
    if (on.pull_request) {
      triggers.push({
        type: 'pull_request',
        branches: Array.isArray(on.pull_request.branches)
          ? on.pull_request.branches
          : [on.pull_request.branches].filter(Boolean),
        paths: Array.isArray(on.pull_request.paths)
          ? on.pull_request.paths
          : [on.pull_request.paths].filter(Boolean),
      });
    }

    // Schedule triggers
    if (on.schedule) {
      for (const schedule of on.schedule) {
        triggers.push({
          type: 'schedule',
          schedule: schedule.cron,
        });
      }
    }

    // Manual triggers
    if (on.workflow_dispatch) {
      triggers.push({
        type: 'manual',
      });
    }

    return triggers;
  }

  extractBuildSteps(parsed: any): BuildStep[] {
    const steps: BuildStep[] = [];

    if (!parsed.jobs) return steps;

    for (const [jobName, job] of Object.entries(parsed.jobs) as [string, any]) {
      if (job.steps) {
        for (const step of job.steps) {
          if (step.run && !step.name?.toLowerCase().includes('test')) {
            steps.push({
              name: step.name || `Step ${steps.length + 1}`,
              command: step.run,
              condition: step['if'],
              timeout: step.timeoutMinutes ? step.timeoutMinutes * 60 * 1000 : undefined,
              continueOnError: step['continue-on-error'],
              workingDirectory: step['working-directory'],
              environment: step.env,
            });
          }
        }
      }
    }

    return steps;
  }

  extractTestSteps(parsed: any): BuildStep[] {
    const steps: BuildStep[] = [];

    if (!parsed.jobs) return steps;

    for (const [jobName, job] of Object.entries(parsed.jobs) as [string, any]) {
      if (job.steps) {
        for (const step of job.steps) {
          if (step.run && step.name?.toLowerCase().includes('test')) {
            steps.push({
              name: step.name || `Test Step ${steps.length + 1}`,
              command: step.run,
              condition: step['if'],
              timeout: step.timeoutMinutes ? step.timeoutMinutes * 60 * 1000 : undefined,
              continueOnError: step['continue-on-error'],
              workingDirectory: step['working-directory'],
              environment: step.env,
            });
          }
        }
      }
    }

    return steps;
  }

  extractDeploySteps(parsed: any): BuildStep[] {
    const steps: BuildStep[] = [];

    if (!parsed.jobs) return steps;

    for (const [jobName, job] of Object.entries(parsed.jobs) as [string, any]) {
      if (job.steps) {
        for (const step of job.steps) {
          if (step.run && step.name?.toLowerCase().includes('deploy')) {
            steps.push({
              name: step.name || `Deploy Step ${steps.length + 1}`,
              command: step.run,
              condition: step['if'],
              timeout: step.timeoutMinutes ? step.timeoutMinutes * 60 * 1000 : undefined,
              continueOnError: step['continue-on-error'],
              workingDirectory: step['working-directory'],
              environment: step.env,
            });
          }
        }
      }
    }

    return steps;
  }

  extractEnvironmentVariables(parsed: any): Record<string, string> {
    const envVars: Record<string, string> = {};

    if (parsed.env) {
      Object.assign(envVars, parsed.env);
    }

    if (parsed.jobs) {
      for (const job of Object.values(parsed.jobs) as any[]) {
        if (job.env) {
          Object.assign(envVars, job.env);
        }
      }
    }

    return envVars;
  }

  extractSecrets(parsed: any): string[] {
    const secrets: string[] = [];
    const content = JSON.stringify(parsed);

    // Find all references to secrets (format: ${{ secrets.SECRET_NAME }})
    const secretRegex = /\$\{\{\s*secrets\.(\w+)\s*\}\}/g;
    let match;

    while ((match = secretRegex.exec(content)) !== null) {
      secrets.push(match[1]);
    }

    return [...new Set(secrets)];
  }

  extractDependencies(parsed: any): BuildDependency[] {
    // GitHub Actions doesn't typically define dependencies directly
    // This would need to be extracted from package.json, requirements.txt, etc.
    return [];
  }

  extractCache(parsed: any): BuildCache[] {
    const cache: BuildCache[] = [];

    if (parsed.jobs) {
      for (const job of Object.values(parsed.jobs) as any[]) {
        if (job.steps) {
          for (const step of job.steps) {
            if (step.uses === 'actions/cache@v3' || step.uses === 'actions/cache@v2') {
              cache.push({
                path: step.with?.path || '',
                key: step.with?.key || '',
                restoreKeys: step.with?.restoreKeys?.split('\n') || [],
                scope: 'job',
              });
            }
          }
        }
      }
    }

    return cache;
  }

  extractMatrix(parsed: any): BuildMatrix | null {
    if (!parsed.jobs) return null;

    for (const job of Object.values(parsed.jobs) as any[]) {
      if (job.strategy?.matrix) {
        return {
          axis: job.strategy.matrix,
          exclude: job.strategy.matrix?.exclude,
          include: job.strategy.matrix?.include,
          maxParallel: job.strategy.maxParallel || 1,
          failFast: job.strategy.failFast !== false,
        };
      }
    }

    return null;
  }

  extractParallelism(parsed: any): ParallelismConfig | null {
    if (!parsed.jobs) return null;

    const parallelJobs = Object.keys(parsed.jobs).length;

    if (parallelJobs > 1) {
      return {
        type: 'parallel',
        maxParallel: parallelJobs,
        strategy: 'job-level',
      };
    }

    return null;
  }

  extractArtifacts(parsed: any): BuildArtifact[] {
    const artifacts: BuildArtifact[] = [];

    if (parsed.jobs) {
      for (const job of Object.values(parsed.jobs) as any[]) {
        if (job.steps) {
          for (const step of job.steps) {
            if (step.uses?.includes('upload-artifact')) {
              artifacts.push({
                name: step.with?.name || 'artifact',
                path: step.with?.path || '',
                retentionDays: step.with?.retentionDays
                  ? parseInt(step.with.retentionDays)
                  : undefined,
                condition: step['if'],
              });
            }
          }
        }
      }
    }

    return artifacts;
  }

  extractNotifications(parsed: any): NotificationConfig[] {
    const notifications: NotificationConfig[] = [];

    // This would need to be implemented based on common notification patterns
    // e.g., Slack actions, email actions, etc.

    return notifications;
  }
}
```

### Config Parser Interface

```typescript
export interface ConfigParser {
  parse(content: string, format: string): Promise<any>;
  extractTriggers?(parsed: any): BuildTrigger[];
  extractBuildSteps?(parsed: any): BuildStep[];
  extractTestSteps?(parsed: any): BuildStep[];
  extractDeploySteps?(parsed: any): BuildStep[];
  extractEnvironmentVariables?(parsed: any): Record<string, string>;
  extractSecrets?(parsed: any): string[];
  extractDependencies?(parsed: any): BuildDependency[];
  extractCache?(parsed: any): BuildCache[];
  extractMatrix?(parsed: any): BuildMatrix | null;
  extractParallelism?(parsed: any): ParallelismConfig | null;
  extractArtifacts?(parsed: any): BuildArtifact[];
  extractNotifications?(parsed: any): NotificationConfig[];
}
```

## Testing Strategy

### Unit Tests

```typescript
describe('BuildConfigDetector', () => {
  let detector: BuildConfigDetector;

  beforeEach(() => {
    detector = new BuildConfigDetector();
  });

  describe('detectBuildConfigs', () => {
    it('should detect GitHub Actions workflow', async () => {
      const mockFiles = [
        {
          type: BuildConfigType.GITHUB_ACTIONS,
          path: '.github/workflows/ci.yml',
          content: `
name: CI
on: [push, pull_request]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - name: Install dependencies
        run: npm install
      - name: Build
        run: npm run build
      - name: Test
        run: npm test
          `,
          format: 'yaml',
          parsed: null,
          isValid: false,
          errors: [],
        },
      ];

      jest.spyOn(detector as any, 'scanConfigFiles').mockResolvedValue(mockFiles);
      jest.spyOn(detector as any, 'parseConfigFiles').mockResolvedValue(mockFiles);

      const result = await detector.detectBuildConfigs('/path/to/repo');

      expect(result.files).toHaveLength(1);
      expect(result.buildTriggers).toHaveLength(2); // push and pull_request
      expect(result.buildSteps).toHaveLength(2); // install and build
      expect(result.testSteps).toHaveLength(1); // test
    });

    it('should detect multiple build configurations', async () => {
      const mockFiles = [
        {
          type: BuildConfigType.GITHUB_ACTIONS,
          path: '.github/workflows/ci.yml',
          content: 'name: CI',
          format: 'yaml',
          parsed: { name: 'CI' },
          isValid: true,
          errors: [],
        },
        {
          type: BuildConfigType.DOCKER,
          path: 'Dockerfile',
          content: 'FROM node:16',
          format: 'dockerfile',
          parsed: { from: 'node:16' },
          isValid: true,
          errors: [],
        },
      ];

      jest.spyOn(detector as any, 'scanConfigFiles').mockResolvedValue(mockFiles);
      jest.spyOn(detector as any, 'parseConfigFiles').mockResolvedValue(mockFiles);

      const result = await detector.detectBuildConfigs('/path/to/repo');

      expect(result.files).toHaveLength(2);
      expect(result.primaryConfig?.type).toBe(BuildConfigType.GITHUB_ACTIONS);
    });
  });
});

describe('GitHubActionsParser', () => {
  let parser: GitHubActionsParser;

  beforeEach(() => {
    parser = new GitHubActionsParser();
  });

  describe('extractTriggers', () => {
    it('should extract push and pull request triggers', () => {
      const parsed = {
        on: {
          push: {
            branches: ['main', 'develop'],
          },
          pull_request: {
            branches: ['main'],
          },
        },
      };

      const triggers = parser.extractTriggers(parsed);

      expect(triggers).toHaveLength(2);
      expect(triggers[0].type).toBe('push');
      expect(triggers[0].branches).toEqual(['main', 'develop']);
      expect(triggers[1].type).toBe('pull_request');
      expect(triggers[1].branches).toEqual(['main']);
    });

    it('should extract schedule triggers', () => {
      const parsed = {
        on: {
          schedule: [{ cron: '0 0 * * *' }, { cron: '0 12 * * 1' }],
        },
      };

      const triggers = parser.extractTriggers(parsed);

      expect(triggers).toHaveLength(2);
      expect(triggers[0].type).toBe('schedule');
      expect(triggers[0].schedule).toBe('0 0 * * *');
      expect(triggers[1].schedule).toBe('0 12 * * 1');
    });
  });

  describe('extractMatrix', () => {
    it('should extract build matrix', () => {
      const parsed = {
        jobs: {
          test: {
            strategy: {
              matrix: {
                node: [14, 16, 18],
                os: ['ubuntu-latest', 'windows-latest'],
              },
              maxParallel: 4,
              failFast: false,
            },
          },
        },
      };

      const matrix = parser.extractMatrix(parsed);

      expect(matrix).not.toBeNull();
      expect(matrix!.axis).toEqual({
        node: [14, 16, 18],
        os: ['ubuntu-latest', 'windows-latest'],
      });
      expect(matrix!.maxParallel).toBe(4);
      expect(matrix!.failFast).toBe(false);
    });
  });
});
```

### Integration Tests

```typescript
describe('Build Configuration Integration', () => {
  describe('Real Repository Analysis', () => {
    it('should analyze GitHub Actions workflow correctly', async () => {
      const detector = new BuildConfigDetector();
      const result = await detector.detectBuildConfigs('/path/to/real-repo');

      expect(result.files.length).toBeGreaterThan(0);
      expect(result.primaryConfig).not.toBeNull();
      expect(result.buildTriggers.length).toBeGreaterThan(0);
    });

    it('should handle complex multi-language project', async () => {
      const detector = new BuildConfigDetector();
      const result = await detector.detectBuildConfigs('/path/to/multi-lang-repo');

      expect(result.files.length).toBeGreaterThan(1);
      expect(result.dependencies.length).toBeGreaterThan(0);
      expect(result.cache.length).toBeGreaterThan(0);
    });
  });
});
```

## Monitoring and Metrics

### Configuration Detection Metrics

```typescript
export interface ConfigDetectionMetrics {
  totalRepositories: number;
  configTypeDistribution: Record<BuildConfigType, number>;
  averageParsingTime: number;
  parsingErrors: number;
  matrixConfigurations: number;
  parallelConfigurations: number;
  cacheConfigurations: number;
  averageConfigComplexity: number;
}

export class ConfigDetectionMonitor {
  private metricsCollector: MetricsCollector;

  async recordDetection(
    repositoryId: string,
    result: BuildConfigAnalysis,
    detectionTime: number
  ): Promise<void> {
    await this.metricsCollector.increment('config_detections_total');
    await this.metricsCollector.record('config_detection_duration', detectionTime);

    for (const file of result.files) {
      await this.metricsCollector.increment('config_files_detected', {
        type: file.type,
        valid: file.isValid.toString(),
      });
    }

    if (result.matrix) {
      await this.metricsCollector.increment('matrix_configurations_detected');
    }

    if (result.parallelism) {
      await this.metricsCollector.increment('parallel_configurations_detected');
    }

    await this.metricsCollector.record('config_complexity', this.calculateComplexity(result));
  }

  private calculateComplexity(result: BuildConfigAnalysis): number {
    let complexity = 0;

    complexity += result.buildTriggers.length * 1;
    complexity += result.buildSteps.length * 2;
    complexity += result.testSteps.length * 2;
    complexity += result.deploySteps.length * 3;
    complexity += result.environmentVariables.length * 1;
    complexity += result.secrets.length * 1;
    complexity += result.dependencies.length * 1;
    complexity += result.cache.length * 2;
    complexity += result.artifacts.length * 2;

    if (result.matrix) {
      complexity += 5;
      const matrixSize = Object.values(result.matrix.axis).reduce(
        (sum, values) => sum + values.length,
        0
      );
      complexity += matrixSize;
    }

    if (result.parallelism) {
      complexity += 3;
    }

    return complexity;
  }
}
```

## Configuration Management

### Detection Configuration

```typescript
export interface BuildConfigDetectionConfig {
  enabledConfigTypes: BuildConfigType[];
  maxFileSize: number;
  ignoreDirectories: string[];
  ignoreFiles: string[];
  customPatterns: Record<BuildConfigType, string[]>;
  parsingTimeout: number;
  maxConfigFiles: number;
  validateSyntax: boolean;
  extractDependencies: boolean;
  extractSecrets: boolean;
  cacheParsing: boolean;
}

export const defaultConfigDetectionConfig: BuildConfigDetectionConfig = {
  enabledConfigTypes: [
    BuildConfigType.GITHUB_ACTIONS,
    BuildConfigType.GITLAB_CI,
    BuildConfigType.JENKINS,
    BuildConfigType.CIRCLECI,
    BuildConfigType.AZURE_DEVOPS,
    BuildConfigType.DOCKER,
    BuildConfigType.MAKEFILE,
    BuildConfigType.CMAKE,
    BuildConfigType.PACKAGE_JSON,
    BuildConfigType.GRADLE,
    BuildConfigType.MAVEN,
    BuildConfigType.CARGO,
    BuildConfigType.GO_MOD,
  ],
  maxFileSize: 1024 * 1024, // 1MB
  ignoreDirectories: [
    'node_modules',
    '.git',
    'target',
    'build',
    'dist',
    '__pycache__',
    '.pytest_cache',
    '.next',
    '.nuxt',
    'vendor',
    '.gradle',
    '.m2',
  ],
  ignoreFiles: ['*.min.js', '*.min.css', '*.map', '*.lock', '*.log'],
  customPatterns: {},
  parsingTimeout: 10000, // 10 seconds
  maxConfigFiles: 50,
  validateSyntax: true,
  extractDependencies: true,
  extractSecrets: true,
  cacheParsing: true,
};
```

## Error Handling

### Configuration Detection Errors

```typescript
export class BuildConfigDetectionError extends Error {
  constructor(
    message: string,
    public readonly repositoryPath?: string,
    public readonly configType?: BuildConfigType,
    public readonly originalError?: Error
  ) {
    super(message);
    this.name = 'BuildConfigDetectionError';
  }
}

export class ConfigParsingError extends BuildConfigDetectionError {
  constructor(
    message: string,
    public readonly filePath: string,
    public readonly parseError: Error
  ) {
    super(`Failed to parse config file ${filePath}: ${message}`);
    this.name = 'ConfigParsingError';
  }
}

export class UnsupportedConfigFormatError extends BuildConfigDetectionError {
  constructor(format: string, configType: BuildConfigType) {
    super(`Unsupported format ${format} for config type ${configType}`);
    this.name = 'UnsupportedConfigFormatError';
  }
}
```

## Implementation Checklist

- [ ] Implement BuildConfigDetector
- [ ] Create GitHub Actions parser
- [ ] Create GitLab CI parser
- [ ] Create Jenkins parser
- [ ] Create CircleCI parser
- [ ] Create Azure DevOps parser
- [ ] Create Docker parser
- [ ] Create Makefile parser
- [ ] Create CMake parser
- [ ] Create Package.json parser
- [ ] Create Gradle parser
- [ ] Create Maven parser
- [ ] Create Cargo parser
- [ ] Create Go mod parser
- [ ] Implement matrix extraction
- [ ] Implement parallelism detection
- [ ] Add comprehensive unit tests
- [ ] Add integration tests
- [ ] Implement monitoring and metrics
- [ ] Add configuration management
- [ ] Create error handling
- [ ] Add performance optimizations
- [ ] Create documentation for supported formats
- [ ] Add custom pattern support
- [ ] Implement caching for parsed configs
- [ ] Add validation for configuration syntax
- [ ] Create configuration complexity scoring
- [ ] Add support for nested configurations
