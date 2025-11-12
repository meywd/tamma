# Task 8: Language Detection and Support

## Objective

Implement automatic programming language detection and language-specific build configurations with support for multiple languages in a single repository.

## Acceptance Criteria

- [ ] Automatic language detection from repository files
- [ ] Support for JavaScript/TypeScript
- [ ] Support for Python
- [ ] Support for Java
- [ ] Support for Go
- [ ] Support for Rust
- [ ] Support for C/C++
- [ ] Support for .NET
- [ ] Support for PHP
- [ ] Support for Ruby
- [ ] Multi-language repository support
- [ ] Language-specific build configurations
- [ ] Framework detection (React, Vue, Django, Spring, etc.)
- [ ] Package manager detection
- [ ] Build tool detection

## Technical Implementation

### Core Interfaces

```typescript
// Programming language types
export enum ProgrammingLanguage {
  JAVASCRIPT = 'javascript',
  TYPESCRIPT = 'typescript',
  PYTHON = 'python',
  JAVA = 'java',
  GO = 'go',
  RUST = 'rust',
  C = 'c',
  CPP = 'cpp',
  CSHARP = 'csharp',
  PHP = 'php',
  RUBY = 'ruby',
  SWIFT = 'swift',
  KOTLIN = 'kotlin',
  SCALA = 'scala',
  DART = 'dart',
  ELIXIR = 'elixir',
  HASKELL = 'haskell',
  R = 'r',
  SHELL = 'shell',
}

// Language detection result
export interface LanguageDetectionResult {
  primaryLanguage: ProgrammingLanguage;
  secondaryLanguages: ProgrammingLanguage[];
  frameworks: string[];
  packageManagers: PackageManager[];
  buildTools: BuildTool[];
  confidence: number;
  fileCount: number;
  totalLines: number;
}

// Language configuration
export interface LanguageConfig {
  language: ProgrammingLanguage;
  fileExtensions: string[];
  fileNames: string[];
  buildCommands: string[];
  testCommands: string[];
  installCommands: string[];
  frameworks: FrameworkConfig[];
  packageManagers: PackageManagerConfig[];
  buildTools: BuildToolConfig[];
  environmentVariables?: Record<string, string>;
  dependencies?: string[];
  devDependencies?: string[];
}

// Framework configuration
export interface FrameworkConfig {
  name: string;
  detectionFiles: string[];
  detectionPatterns: string[];
  buildCommands: string[];
  testCommands: string[];
  devCommands: string[];
}

// Package manager configuration
export interface PackageManagerConfig {
  name: string;
  lockFiles: string[];
  configFiles: string[];
  installCommand: string;
  addCommand: string;
  removeCommand: string;
  updateCommand: string;
}

// Build tool configuration
export interface BuildToolConfig {
  name: string;
  configFiles: string[];
  buildCommand: string;
  testCommand: string;
  cleanCommand: string;
  packageCommand: string;
}
```

### Language Detection Engine

```typescript
export class LanguageDetectionEngine {
  private languageConfigs: Map<ProgrammingLanguage, LanguageConfig> = new Map();
  private fileAnalyzers: Map<ProgrammingLanguage, FileAnalyzer> = new Map();

  constructor() {
    this.initializeLanguageConfigs();
    this.initializeFileAnalyzers();
  }

  private initializeLanguageConfigs(): void {
    // JavaScript/TypeScript configuration
    this.languageConfigs.set(ProgrammingLanguage.JAVASCRIPT, {
      language: ProgrammingLanguage.JAVASCRIPT,
      fileExtensions: ['.js', '.jsx', '.mjs'],
      fileNames: ['package.json', 'yarn.lock', 'package-lock.json'],
      buildCommands: ['npm run build', 'yarn build', 'pnpm build'],
      testCommands: ['npm test', 'yarn test', 'pnpm test'],
      installCommands: ['npm install', 'yarn install', 'pnpm install'],
      frameworks: [
        {
          name: 'React',
          detectionFiles: ['package.json'],
          detectionPatterns: ['"react":', '"react-dom":'],
          buildCommands: ['npm run build', 'yarn build'],
          testCommands: ['npm test', 'yarn test'],
          devCommands: ['npm start', 'yarn start'],
        },
        {
          name: 'Vue',
          detectionFiles: ['package.json', 'vue.config.js'],
          detectionPatterns: ['"vue":', '"@vue/cli"'],
          buildCommands: ['npm run build', 'yarn build'],
          testCommands: ['npm run test:unit', 'yarn test:unit'],
          devCommands: ['npm run serve', 'yarn serve'],
        },
        {
          name: 'Angular',
          detectionFiles: ['angular.json', 'package.json'],
          detectionPatterns: ['"@angular/core":'],
          buildCommands: ['ng build', 'npm run build'],
          testCommands: ['ng test', 'npm run test'],
          devCommands: ['ng serve', 'npm run start'],
        },
      ],
      packageManagers: [
        {
          name: 'npm',
          lockFiles: ['package-lock.json'],
          configFiles: ['package.json'],
          installCommand: 'npm install',
          addCommand: 'npm install',
          removeCommand: 'npm uninstall',
          updateCommand: 'npm update',
        },
        {
          name: 'yarn',
          lockFiles: ['yarn.lock'],
          configFiles: ['package.json'],
          installCommand: 'yarn install',
          addCommand: 'yarn add',
          removeCommand: 'yarn remove',
          updateCommand: 'yarn upgrade',
        },
        {
          name: 'pnpm',
          lockFiles: ['pnpm-lock.yaml'],
          configFiles: ['package.json', 'pnpm-workspace.yaml'],
          installCommand: 'pnpm install',
          addCommand: 'pnpm add',
          removeCommand: 'pnpm remove',
          updateCommand: 'pnpm update',
        },
      ],
      buildTools: [
        {
          name: 'Webpack',
          configFiles: ['webpack.config.js', 'webpack.config.ts'],
          buildCommand: 'webpack --mode production',
          testCommand: 'webpack --mode development',
          cleanCommand: 'rm -rf dist',
          packageCommand: 'webpack --mode production',
        },
        {
          name: 'Vite',
          configFiles: ['vite.config.js', 'vite.config.ts'],
          buildCommand: 'vite build',
          testCommand: 'vite test',
          cleanCommand: 'rm -rf dist',
          packageCommand: 'vite build',
        },
      ],
    });

    // Python configuration
    this.languageConfigs.set(ProgrammingLanguage.PYTHON, {
      language: ProgrammingLanguage.PYTHON,
      fileExtensions: ['.py', '.pyx', '.pyi'],
      fileNames: ['requirements.txt', 'setup.py', 'pyproject.toml', 'Pipfile'],
      buildCommands: ['python setup.py build', 'python -m build'],
      testCommands: ['pytest', 'python -m unittest', 'python -m nose'],
      installCommands: ['pip install -r requirements.txt', 'pipenv install', 'poetry install'],
      frameworks: [
        {
          name: 'Django',
          detectionFiles: ['manage.py', 'settings.py'],
          detectionPatterns: ['DJANGO_SETTINGS_MODULE', 'django.contrib'],
          buildCommands: ['python manage.py collectstatic --noinput'],
          testCommands: ['python manage.py test', 'pytest'],
          devCommands: ['python manage.py runserver'],
        },
        {
          name: 'Flask',
          detectionFiles: ['app.py', 'wsgi.py'],
          detectionPatterns: ['from flask import', 'Flask(__name__)'],
          buildCommands: [],
          testCommands: ['pytest', 'python -m unittest'],
          devCommands: ['flask run'],
        },
        {
          name: 'FastAPI',
          detectionFiles: ['main.py'],
          detectionPatterns: ['from fastapi import', 'FastAPI()'],
          buildCommands: [],
          testCommands: ['pytest', 'python -m unittest'],
          devCommands: ['uvicorn main:app --reload'],
        },
      ],
      packageManagers: [
        {
          name: 'pip',
          lockFiles: ['requirements.txt'],
          configFiles: ['setup.py', 'requirements.txt'],
          installCommand: 'pip install -r requirements.txt',
          addCommand: 'pip install',
          removeCommand: 'pip uninstall',
          updateCommand: 'pip install --upgrade',
        },
        {
          name: 'pipenv',
          lockFiles: ['Pipfile.lock'],
          configFiles: ['Pipfile'],
          installCommand: 'pipenv install',
          addCommand: 'pipenv install',
          removeCommand: 'pipenv uninstall',
          updateCommand: 'pipenv update',
        },
        {
          name: 'poetry',
          lockFiles: ['poetry.lock'],
          configFiles: ['pyproject.toml'],
          installCommand: 'poetry install',
          addCommand: 'poetry add',
          removeCommand: 'poetry remove',
          updateCommand: 'poetry update',
        },
      ],
      buildTools: [
        {
          name: 'setuptools',
          configFiles: ['setup.py'],
          buildCommand: 'python setup.py build',
          testCommand: 'python setup.py test',
          cleanCommand: 'python setup.py clean --all',
          packageCommand: 'python setup.py sdist bdist_wheel',
        },
      ],
    });

    // Add configurations for other languages...
  }

  async detectLanguages(repositoryPath: string): Promise<LanguageDetectionResult> {
    const files = await this.scanRepository(repositoryPath);
    const languageStats = await this.analyzeFiles(files);
    const frameworks = await this.detectFrameworks(repositoryPath, languageStats);
    const packageManagers = await this.detectPackageManagers(repositoryPath);
    const buildTools = await this.detectBuildTools(repositoryPath);

    const sortedLanguages = Array.from(languageStats.entries())
      .sort(([, a], [, b]) => b.fileCount - a.fileCount)
      .map(([lang, stats]) => ({ language: lang, ...stats }));

    const primaryLanguage = sortedLanguages[0]?.language || ProgrammingLanguage.JAVASCRIPT;
    const secondaryLanguages = sortedLanguages.slice(1).map((item) => item.language);

    return {
      primaryLanguage,
      secondaryLanguages,
      frameworks,
      packageManagers,
      buildTools,
      confidence: this.calculateConfidence(sortedLanguages),
      fileCount: files.length,
      totalLines: sortedLanguages.reduce((sum, item) => sum + item.lineCount, 0),
    };
  }

  private async scanRepository(repositoryPath: string): Promise<string[]> {
    const files: string[] = [];

    async function scanDirectory(dir: string): Promise<void> {
      const entries = await fs.readdir(dir, { withFileTypes: true });

      for (const entry of entries) {
        const fullPath = path.join(dir, entry.name);

        if (entry.isDirectory()) {
          // Skip common ignore directories
          if (
            !['node_modules', '.git', 'target', 'build', 'dist', '__pycache__'].includes(entry.name)
          ) {
            await scanDirectory(fullPath);
          }
        } else {
          files.push(fullPath);
        }
      }
    }

    await scanDirectory(repositoryPath);
    return files;
  }

  private async analyzeFiles(
    files: string[]
  ): Promise<Map<ProgrammingLanguage, { fileCount: number; lineCount: number }>> {
    const stats = new Map<ProgrammingLanguage, { fileCount: number; lineCount: number }>();

    for (const file of files) {
      const language = this.detectFileLanguage(file);
      if (language) {
        const current = stats.get(language) || { fileCount: 0, lineCount: 0 };
        const lineCount = await this.countFileLines(file);

        stats.set(language, {
          fileCount: current.fileCount + 1,
          lineCount: current.lineCount + lineCount,
        });
      }
    }

    return stats;
  }

  private detectFileLanguage(filePath: string): ProgrammingLanguage | null {
    const ext = path.extname(filePath).toLowerCase();
    const fileName = path.basename(filePath).toLowerCase();

    for (const [language, config] of this.languageConfigs) {
      if (config.fileExtensions.includes(ext) || config.fileNames.includes(fileName)) {
        return language;
      }
    }

    return null;
  }

  private async countFileLines(filePath: string): Promise<number> {
    try {
      const content = await fs.readFile(filePath, 'utf-8');
      return content.split('\n').length;
    } catch {
      return 0;
    }
  }

  private async detectFrameworks(
    repositoryPath: string,
    languageStats: Map<ProgrammingLanguage, any>
  ): Promise<string[]> {
    const frameworks: string[] = [];

    for (const [language, config] of this.languageConfigs) {
      if (!languageStats.has(language)) continue;

      for (const framework of config.frameworks) {
        const detected = await this.checkFrameworkDetection(repositoryPath, framework);
        if (detected) {
          frameworks.push(framework.name);
        }
      }
    }

    return frameworks;
  }

  private async checkFrameworkDetection(
    repositoryPath: string,
    framework: FrameworkConfig
  ): Promise<boolean> {
    // Check for detection files
    for (const file of framework.detectionFiles) {
      const filePath = path.join(repositoryPath, file);
      try {
        const content = await fs.readFile(filePath, 'utf-8');

        // Check for detection patterns
        for (const pattern of framework.detectionPatterns) {
          if (content.includes(pattern)) {
            return true;
          }
        }
      } catch {
        // File doesn't exist, continue checking
      }
    }

    return false;
  }

  private async detectPackageManagers(repositoryPath: string): Promise<PackageManager[]> {
    const managers: PackageManager[] = [];

    for (const config of this.languageConfigs.values()) {
      for (const manager of config.packageManagers) {
        const detected = await this.checkPackageManagerDetection(repositoryPath, manager);
        if (detected) {
          managers.push(manager.name as PackageManager);
        }
      }
    }

    return managers;
  }

  private async checkPackageManagerDetection(
    repositoryPath: string,
    manager: PackageManagerConfig
  ): Promise<boolean> {
    for (const file of [...manager.lockFiles, ...manager.configFiles]) {
      const filePath = path.join(repositoryPath, file);
      try {
        await fs.access(filePath);
        return true;
      } catch {
        // File doesn't exist
      }
    }
    return false;
  }

  private async detectBuildTools(repositoryPath: string): Promise<BuildTool[]> {
    const tools: BuildTool[] = [];

    for (const config of this.languageConfigs.values()) {
      for (const tool of config.buildTools) {
        const detected = await this.checkBuildToolDetection(repositoryPath, tool);
        if (detected) {
          tools.push(tool.name as BuildTool);
        }
      }
    }

    return tools;
  }

  private async checkBuildToolDetection(
    repositoryPath: string,
    tool: BuildToolConfig
  ): Promise<boolean> {
    for (const file of tool.configFiles) {
      const filePath = path.join(repositoryPath, file);
      try {
        await fs.access(filePath);
        return true;
      } catch {
        // File doesn't exist
      }
    }
    return false;
  }

  private calculateConfidence(sortedLanguages: any[]): number {
    if (sortedLanguages.length === 0) return 0;

    const primary = sortedLanguages[0];
    const total = sortedLanguages.reduce((sum, item) => sum + item.fileCount, 0);

    return primary.fileCount / total;
  }
}
```

### Language-Specific Build Configuration

```typescript
export class LanguageBuildConfigurator {
  private detectionEngine: LanguageDetectionEngine;

  constructor() {
    this.detectionEngine = new LanguageDetectionEngine();
  }

  async generateBuildConfig(repositoryPath: string): Promise<BuildConfiguration> {
    const detection = await this.detectionEngine.detectLanguages(repositoryPath);

    const config: BuildConfiguration = {
      primaryLanguage: detection.primaryLanguage,
      languages: [detection.primaryLanguage, ...detection.secondaryLanguages],
      frameworks: detection.frameworks,
      buildSteps: [],
      testSteps: [],
      environment: {},
      dependencies: [],
      cache: {},
    };

    // Add language-specific build steps
    for (const language of config.languages) {
      const langConfig = this.getLanguageConfig(language);
      if (langConfig) {
        config.buildSteps.push(...this.createBuildSteps(langConfig, detection));
        config.testSteps.push(...this.createTestSteps(langConfig, detection));
        config.environment = { ...config.environment, ...langConfig.environmentVariables };
      }
    }

    // Add framework-specific steps
    for (const framework of detection.frameworks) {
      const frameworkSteps = this.createFrameworkSteps(framework, detection);
      config.buildSteps.push(...frameworkSteps.build);
      config.testSteps.push(...frameworkSteps.test);
    }

    // Add caching configuration
    config.cache = this.createCacheConfig(detection);

    return config;
  }

  private createBuildSteps(
    config: LanguageConfig,
    detection: LanguageDetectionResult
  ): BuildStep[] {
    const steps: BuildStep[] = [];

    // Install dependencies
    if (detection.packageManagers.length > 0) {
      const manager = detection.packageManagers[0];
      const installCommand = this.getInstallCommand(manager, config);
      if (installCommand) {
        steps.push({
          name: `Install ${config.language} dependencies`,
          command: installCommand,
          timeout: 300000, // 5 minutes
          retryCount: 2,
        });
      }
    }

    // Build commands
    for (const buildCommand of config.buildCommands) {
      steps.push({
        name: `Build ${config.language}`,
        command: buildCommand,
        timeout: 600000, // 10 minutes
        retryCount: 1,
      });
    }

    return steps;
  }

  private createTestSteps(config: LanguageConfig, detection: LanguageDetectionResult): BuildStep[] {
    const steps: BuildStep[] = [];

    for (const testCommand of config.testCommands) {
      steps.push({
        name: `Test ${config.language}`,
        command: testCommand,
        timeout: 300000, // 5 minutes
        retryCount: 1,
        continueOnError: true,
      });
    }

    return steps;
  }

  private createFrameworkSteps(
    framework: string,
    detection: LanguageDetectionResult
  ): { build: BuildStep[]; test: BuildStep[] } {
    const result = { build: [] as BuildStep[], test: [] as BuildStep[] };

    for (const config of this.detectionEngine['languageConfigs'].values()) {
      const frameworkConfig = config.frameworks.find((f) => f.name === framework);
      if (frameworkConfig) {
        for (const buildCommand of frameworkConfig.buildCommands) {
          result.build.push({
            name: `Build ${framework}`,
            command: buildCommand,
            timeout: 600000,
            retryCount: 1,
          });
        }

        for (const testCommand of frameworkConfig.testCommands) {
          result.test.push({
            name: `Test ${framework}`,
            command: testCommand,
            timeout: 300000,
            retryCount: 1,
            continueOnError: true,
          });
        }
      }
    }

    return result;
  }

  private createCacheConfig(detection: LanguageDetectionResult): CacheConfig {
    const cache: CacheConfig = {
      paths: [],
      keys: [],
    };

    // Add language-specific cache paths
    for (const language of detection.languages) {
      const config = this.getLanguageConfig(language);
      if (config) {
        switch (language) {
          case ProgrammingLanguage.JAVASCRIPT:
          case ProgrammingLanguage.TYPESCRIPT:
            cache.paths.push('node_modules', '.npm', '.yarn', '.pnpm-store');
            break;
          case ProgrammingLanguage.PYTHON:
            cache.paths.push('.venv', 'venv', '__pycache__', '.pytest_cache');
            break;
          case ProgrammingLanguage.JAVA:
            cache.paths.push('target', '.gradle', '.m2');
            break;
          case ProgrammingLanguage.GO:
            cache.paths.push('vendor', '.cache/go-build');
            break;
          case ProgrammingLanguage.RUST:
            cache.paths.push('target', '.cargo');
            break;
        }
      }
    }

    // Add framework-specific cache
    for (const framework of detection.frameworks) {
      switch (framework) {
        case 'React':
        case 'Vue':
        case 'Angular':
          cache.paths.push('dist', 'build', '.next', '.nuxt');
          break;
        case 'Django':
          cache.paths.push('staticfiles');
          break;
      }
    }

    return cache;
  }

  private getInstallCommand(manager: PackageManager, config: LanguageConfig): string | null {
    const managerConfig = config.packageManagers.find((m) => m.name === manager);
    return managerConfig?.installCommand || null;
  }

  private getLanguageConfig(language: ProgrammingLanguage): LanguageConfig | null {
    return this.detectionEngine['languageConfigs'].get(language) || null;
  }
}
```

## Testing Strategy

### Unit Tests

```typescript
describe('LanguageDetectionEngine', () => {
  let engine: LanguageDetectionEngine;

  beforeEach(() => {
    engine = new LanguageDetectionEngine();
  });

  describe('detectLanguages', () => {
    it('should detect JavaScript project', async () => {
      const mockFiles = ['package.json', 'src/index.js', 'src/App.jsx', 'webpack.config.js'];

      jest.spyOn(engine as any, 'scanRepository').mockResolvedValue(mockFiles);
      jest
        .spyOn(engine as any, 'analyzeFiles')
        .mockResolvedValue(
          new Map([[ProgrammingLanguage.JAVASCRIPT, { fileCount: 3, lineCount: 100 }]])
        );

      const result = await engine.detectLanguages('/path/to/repo');

      expect(result.primaryLanguage).toBe(ProgrammingLanguage.JAVASCRIPT);
      expect(result.frameworks).toContain('React');
    });

    it('should detect Python project with Django', async () => {
      const mockFiles = ['requirements.txt', 'manage.py', 'myapp/settings.py', 'myapp/views.py'];

      jest.spyOn(engine as any, 'scanRepository').mockResolvedValue(mockFiles);
      jest
        .spyOn(engine as any, 'analyzeFiles')
        .mockResolvedValue(
          new Map([[ProgrammingLanguage.PYTHON, { fileCount: 4, lineCount: 200 }]])
        );

      const result = await engine.detectLanguages('/path/to/repo');

      expect(result.primaryLanguage).toBe(ProgrammingLanguage.PYTHON);
      expect(result.frameworks).toContain('Django');
    });

    it('should detect multi-language repository', async () => {
      const mockFiles = ['package.json', 'src/index.js', 'requirements.txt', 'api/main.py'];

      jest.spyOn(engine as any, 'scanRepository').mockResolvedValue(mockFiles);
      jest.spyOn(engine as any, 'analyzeFiles').mockResolvedValue(
        new Map([
          [ProgrammingLanguage.JAVASCRIPT, { fileCount: 2, lineCount: 100 }],
          [ProgrammingLanguage.PYTHON, { fileCount: 2, lineCount: 80 }],
        ])
      );

      const result = await engine.detectLanguages('/path/to/repo');

      expect(result.primaryLanguage).toBe(ProgrammingLanguage.JAVASCRIPT);
      expect(result.secondaryLanguages).toContain(ProgrammingLanguage.PYTHON);
    });
  });
});

describe('LanguageBuildConfigurator', () => {
  let configurator: LanguageBuildConfigurator;

  beforeEach(() => {
    configurator = new LanguageBuildConfigurator();
  });

  describe('generateBuildConfig', () => {
    it('should generate build config for JavaScript project', async () => {
      const mockDetection: LanguageDetectionResult = {
        primaryLanguage: ProgrammingLanguage.JAVASCRIPT,
        secondaryLanguages: [],
        frameworks: ['React'],
        packageManagers: ['npm'],
        buildTools: ['Webpack'],
        confidence: 0.9,
        fileCount: 10,
        totalLines: 1000,
      };

      jest
        .spyOn(configurator['detectionEngine'], 'detectLanguages')
        .mockResolvedValue(mockDetection);

      const config = await configurator.generateBuildConfig('/path/to/repo');

      expect(config.primaryLanguage).toBe(ProgrammingLanguage.JAVASCRIPT);
      expect(config.buildSteps).toHaveLength(2); // Install + Build
      expect(config.testSteps).toHaveLength(1); // Test
      expect(config.buildSteps[0].command).toContain('npm install');
    });
  });
});
```

### Integration Tests

```typescript
describe('Language Detection Integration', () => {
  describe('Real Repository Analysis', () => {
    it('should detect React project correctly', async () => {
      const engine = new LanguageDetectionEngine();
      const result = await engine.detectLanguages('/path/to/react-project');

      expect(result.primaryLanguage).toBe(ProgrammingLanguage.JAVASCRIPT);
      expect(result.frameworks).toContain('React');
      expect(result.packageManagers).toContain('npm');
    });

    it('should detect Django project correctly', async () => {
      const engine = new LanguageDetectionEngine();
      const result = await engine.detectLanguages('/path/to/django-project');

      expect(result.primaryLanguage).toBe(ProgrammingLanguage.PYTHON);
      expect(result.frameworks).toContain('Django');
      expect(result.packageManagers).toContain('pip');
    });
  });
});
```

## Monitoring and Metrics

### Language Detection Metrics

```typescript
export interface LanguageDetectionMetrics {
  totalRepositories: number;
  languageDistribution: Record<ProgrammingLanguage, number>;
  frameworkDistribution: Record<string, number>;
  packageManagerDistribution: Record<string, number>;
  averageDetectionTime: number;
  detectionAccuracy: number;
  multiLanguageRepositories: number;
}

export class LanguageDetectionMonitor {
  private metricsCollector: MetricsCollector;

  async recordDetection(
    repositoryId: string,
    result: LanguageDetectionResult,
    detectionTime: number
  ): Promise<void> {
    await this.metricsCollector.increment('language_detections_total');
    await this.metricsCollector.record('language_detection_duration', detectionTime);

    await this.metricsCollector.increment('language_detections_by_language', {
      language: result.primaryLanguage,
    });

    for (const framework of result.frameworks) {
      await this.metricsCollector.increment('framework_detections_total', {
        framework,
      });
    }

    if (result.secondaryLanguages.length > 0) {
      await this.metricsCollector.increment('multi_language_detections_total');
    }
  }

  async getMetrics(): Promise<LanguageDetectionMetrics> {
    // Implementation for collecting metrics
    return {} as LanguageDetectionMetrics;
  }
}
```

## Configuration Management

### Language Detection Configuration

```typescript
export interface LanguageDetectionConfig {
  enabledLanguages: ProgrammingLanguage[];
  confidenceThreshold: number;
  maxFileSize: number;
  ignoreDirectories: string[];
  ignoreFiles: string[];
  customLanguageConfigs: Record<string, LanguageConfig>;
  frameworkDetection: {
    enabled: boolean;
    confidenceThreshold: number;
  };
  packageManagerDetection: {
    enabled: boolean;
    priority: string[];
  };
}

export const defaultLanguageDetectionConfig: LanguageDetectionConfig = {
  enabledLanguages: [
    ProgrammingLanguage.JAVASCRIPT,
    ProgrammingLanguage.TYPESCRIPT,
    ProgrammingLanguage.PYTHON,
    ProgrammingLanguage.JAVA,
    ProgrammingLanguage.GO,
    ProgrammingLanguage.RUST,
    ProgrammingLanguage.CPP,
    ProgrammingLanguage.CSHARP,
    ProgrammingLanguage.PHP,
    ProgrammingLanguage.RUBY,
  ],
  confidenceThreshold: 0.1,
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
  ],
  ignoreFiles: ['*.min.js', '*.min.css', '*.map', '*.lock', '*.log'],
  customLanguageConfigs: {},
  frameworkDetection: {
    enabled: true,
    confidenceThreshold: 0.5,
  },
  packageManagerDetection: {
    enabled: true,
    priority: ['npm', 'yarn', 'pnpm', 'pip', 'poetry', 'pipenv'],
  },
};
```

## Error Handling

### Language Detection Errors

```typescript
export class LanguageDetectionError extends Error {
  constructor(
    message: string,
    public readonly repositoryPath?: string,
    public readonly originalError?: Error
  ) {
    super(message);
    this.name = 'LanguageDetectionError';
  }
}

export class UnsupportedLanguageError extends LanguageDetectionError {
  constructor(language: ProgrammingLanguage) {
    super(`Language ${language} is not supported`);
    this.name = 'UnsupportedLanguageError';
  }
}

export class LanguageConfigurationError extends LanguageDetectionError {
  constructor(message: string, language?: ProgrammingLanguage) {
    super(`Language configuration error: ${message}`);
    this.name = 'LanguageConfigurationError';
  }
}
```

## Implementation Checklist

- [ ] Implement LanguageDetectionEngine
- [ ] Create language configurations for all supported languages
- [ ] Add framework detection for major frameworks
- [ ] Implement package manager detection
- [ ] Add build tool detection
- [ ] Create LanguageBuildConfigurator
- [ ] Implement multi-language repository support
- [ ] Add confidence scoring
- [ ] Create comprehensive unit tests
- [ ] Add integration tests with real repositories
- [ ] Implement monitoring and metrics
- [ ] Add configuration management
- [ ] Create error handling
- [ ] Add performance optimizations
- [ ] Create documentation for supported languages
- [ ] Add custom language configuration support
- [ ] Implement language-specific caching strategies
- [ ] Add support for monorepo detection
- [ ] Create language detection benchmarks
