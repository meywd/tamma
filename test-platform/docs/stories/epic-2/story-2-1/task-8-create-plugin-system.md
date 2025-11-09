# Task 8: Create Plugin System

**Story**: 2.1 - AI Provider Abstraction Interface  
**Acceptance Criteria**: 8 - Provider plugin system for easy extensibility  
**Status**: Ready for Development

## Overview

This task involves implementing a comprehensive plugin system that allows dynamic loading and management of AI provider plugins. The system must support secure plugin discovery, validation, loading, and lifecycle management while maintaining isolation and security boundaries.

## Subtasks

### Subtask 8.1: Design Plugin Architecture

**Objective**: Define the architecture and interfaces for the plugin system.

**Implementation Details**:

1. **File Location**: `packages/providers/src/plugin/PluginSystem.ts`

2. **Core Plugin Interfaces**:

   ```typescript
   export interface IPlugin {
     readonly manifest: PluginManifest;
     readonly provider: IAIProvider;

     // Lifecycle methods
     initialize(context: PluginContext): Promise<void>;
     activate(): Promise<void>;
     deactivate(): Promise<void>;
     dispose(): Promise<void>;

     // State management
     isInitialized(): boolean;
     isActive(): boolean;
     getState(): PluginState;

     // Event handling
     onEvent(event: PluginEvent): Promise<void>;
     emit(event: PluginEvent): void;
   }

   export interface PluginManifest {
     // Basic information
     name: string;
     version: string;
     description: string;
     author: string;
     license: string;
     homepage?: string;
     repository?: string;

     // Plugin metadata
     id: string;
     category: PluginCategory;
     type: PluginType;
     entryPoint: string;

     // Compatibility
     tammaVersion: string;
     nodeVersion: string;
     dependencies: PluginDependency[];
     peerDependencies: PluginDependency[];

     // Capabilities
     capabilities: PluginCapabilities;
     permissions: PluginPermission[];

     // Security
     signature?: string;
     checksum?: string;
     verified?: boolean;

     // Runtime requirements
     engines: PluginEngines;
     environment: PluginEnvironment;

     // Configuration
     configSchema?: JSONSchema;
     defaultConfig?: Record<string, any>;

     // Resources
     resources: PluginResources;

     // Lifecycle
     lifecycle: PluginLifecycle;

     // Metadata
     keywords: string[];
     tags: string[];
     createdAt: string;
     updatedAt: string;
   }

   export interface PluginContext {
     pluginId: string;
     pluginPath: string;
     configPath: string;
     dataPath: string;
     logPath: string;
     tempPath: string;

     // System interfaces
     logger: Logger;
     eventBus: IPluginEventBus;
     storage: IPluginStorage;
     registry: IPluginRegistry;

     // Tamma interfaces
     providerRegistry: IProviderRegistry;
     configManager: IConfigManager;

     // Security context
     permissions: PluginPermission[];
     sandbox: ISandbox;

     // Utilities
     utils: PluginUtils;
   }

   export interface PluginCapabilities {
     // Provider capabilities
     providerType: string;
     supportedFeatures: string[];
     maxConcurrency?: number;
     streamingSupport: boolean;
     functionCallingSupport: boolean;
     multimodalSupport: boolean;

     // Integration capabilities
     configuration: boolean;
     monitoring: boolean;
     logging: boolean;
     caching: boolean;

     // Extension capabilities
     customModels: boolean;
     customEndpoints: boolean;
     webhooks: boolean;
     batchProcessing: boolean;

     // Performance capabilities
     maxRequestsPerSecond?: number;
     maxTokensPerRequest?: number;
     maxConcurrentRequests?: number;

     // Security capabilities
     encryption: boolean;
     authentication: string[];
     dataPrivacy: boolean;
     auditLogging: boolean;
   }

   export interface PluginPermission {
     type: PermissionType;
     scope: string;
     description: string;
     required: boolean;
     dangerous: boolean;
   }

   export type PermissionType =
     | 'network'
     | 'filesystem'
     | 'process'
     | 'environment'
     | 'system'
     | 'provider'
     | 'config'
     | 'storage'
     | 'logging'
     | 'events';

   export interface PluginDependency {
     name: string;
     version: string;
     optional: boolean;
     reason?: string;
   }

   export interface PluginEngines {
     tamma: string;
     node: string;
     npm?: string;
     yarn?: string;
     pnpm?: string;
   }

   export interface PluginEnvironment {
     variables: string[];
     required: string[];
     optional: string[];
   }

   export interface PluginResources {
     memory?: string;
     cpu?: string;
     storage?: string;
     network?: string;
     maxFileSize?: string;
   }

   export interface PluginLifecycle {
     install?: string;
     uninstall?: string;
     activate?: string;
     deactivate?: string;
     update?: string;
     configure?: string;
   }

   export type PluginCategory =
     | 'provider'
     | 'extension'
     | 'integration'
     | 'utility'
     | 'monitoring'
     | 'security';

   export type PluginType = 'javascript' | 'typescript' | 'native' | 'webassembly';

   export type PluginState =
     | 'uninstalled'
     | 'installing'
     | 'installed'
     | 'initializing'
     | 'active'
     | 'deactivating'
     | 'error'
     | 'updating';
   ```

3. **Plugin System Architecture**:

   ```typescript
   export class PluginSystem extends EventEmitter {
     private plugins: Map<string, IPlugin> = new Map();
     private loader: PluginLoader;
     private validator: PluginValidator;
     private sandbox: PluginSandbox;
     private registry: PluginRegistry;
     private eventBus: PluginEventBus;
     private logger: Logger;
     private config: PluginSystemConfig;

     constructor(config: PluginSystemConfig, logger: Logger) {
       super();
       this.config = config;
       this.logger = logger;
       this.loader = new PluginLoader(config.loader, logger);
       this.validator = new PluginValidator(config.validation, logger);
       this.sandbox = new PluginSandbox(config.sandbox, logger);
       this.registry = new PluginRegistry(config.registry, logger);
       this.eventBus = new PluginEventBus(config.events, logger);

       this.setupEventHandlers();
     }

     async initialize(): Promise<void> {
       this.logger.info('Initializing plugin system');

       // Initialize components
       await this.loader.initialize();
       await this.validator.initialize();
       await this.sandbox.initialize();
       await this.registry.initialize();
       await this.eventBus.initialize();

       // Discover and load plugins
       await this.discoverPlugins();
       await this.loadInstalledPlugins();

       this.emit('initialized');
     }

     async installPlugin(source: PluginSource): Promise<InstallResult> {
       this.logger.info('Installing plugin', { source });

       try {
         // Download and extract plugin
         const pluginPackage = await this.loader.downloadPlugin(source);

         // Validate plugin
         const validationResult = await this.validator.validatePlugin(pluginPackage);
         if (!validationResult.valid) {
           throw new PluginError(
             'VALIDATION_FAILED',
             `Plugin validation failed: ${validationResult.errors.join(', ')}`
           );
         }

         // Check dependencies
         await this.checkDependencies(pluginPackage.manifest);

         // Install plugin
         const installPath = await this.loader.installPlugin(pluginPackage);

         // Load plugin
         const plugin = await this.loadPlugin(installPath);

         // Register plugin
         await this.registry.registerPlugin(plugin);

         this.emit('pluginInstalled', { plugin, source });

         return {
           success: true,
           pluginId: plugin.manifest.id,
           version: plugin.manifest.version,
           installPath,
         };
       } catch (error) {
         this.logger.error('Plugin installation failed', { source, error });
         this.emit('pluginInstallFailed', { source, error });

         return {
           success: false,
           error: error.message,
         };
       }
     }

     async uninstallPlugin(pluginId: string): Promise<UninstallResult> {
       this.logger.info('Uninstalling plugin', { pluginId });

       try {
         const plugin = this.plugins.get(pluginId);
         if (!plugin) {
           throw new PluginError('PLUGIN_NOT_FOUND', `Plugin ${pluginId} not found`);
         }

         // Deactivate plugin if active
         if (plugin.isActive()) {
           await this.deactivatePlugin(pluginId);
         }

         // Run uninstall lifecycle
         await plugin.dispose();

         // Remove from registry
         await this.registry.unregisterPlugin(pluginId);

         // Remove from filesystem
         await this.loader.uninstallPlugin(pluginId);

         // Remove from memory
         this.plugins.delete(pluginId);

         this.emit('pluginUninstalled', { pluginId });

         return {
           success: true,
           pluginId,
         };
       } catch (error) {
         this.logger.error('Plugin uninstallation failed', { pluginId, error });
         this.emit('pluginUninstallFailed', { pluginId, error });

         return {
           success: false,
           error: error.message,
         };
       }
     }

     async activatePlugin(pluginId: string): Promise<ActivateResult> {
       this.logger.info('Activating plugin', { pluginId });

       try {
         const plugin = this.plugins.get(pluginId);
         if (!plugin) {
           throw new PluginError('PLUGIN_NOT_FOUND', `Plugin ${pluginId} not found`);
         }

         if (plugin.isActive()) {
           return {
             success: true,
             pluginId,
             alreadyActive: true,
           };
         }

         // Create plugin context
         const context = await this.createPluginContext(plugin);

         // Initialize plugin
         if (!plugin.isInitialized()) {
           await plugin.initialize(context);
         }

         // Activate plugin
         await plugin.activate();

         // Register provider if applicable
         if (plugin.manifest.category === 'provider') {
           await this.registerProvider(plugin);
         }

         this.emit('pluginActivated', { pluginId });

         return {
           success: true,
           pluginId,
         };
       } catch (error) {
         this.logger.error('Plugin activation failed', { pluginId, error });
         this.emit('pluginActivateFailed', { pluginId, error });

         return {
           success: false,
           error: error.message,
         };
       }
     }

     async deactivatePlugin(pluginId: string): Promise<DeactivateResult> {
       this.logger.info('Deactivating plugin', { pluginId });

       try {
         const plugin = this.plugins.get(pluginId);
         if (!plugin) {
           throw new PluginError('PLUGIN_NOT_FOUND', `Plugin ${pluginId} not found`);
         }

         if (!plugin.isActive()) {
           return {
             success: true,
             pluginId,
             alreadyInactive: true,
           };
         }

         // Unregister provider if applicable
         if (plugin.manifest.category === 'provider') {
           await this.unregisterProvider(plugin);
         }

         // Deactivate plugin
         await plugin.deactivate();

         this.emit('pluginDeactivated', { pluginId });

         return {
           success: true,
           pluginId,
         };
       } catch (error) {
         this.logger.error('Plugin deactivation failed', { pluginId, error });
         this.emit('pluginDeactivateFailed', { pluginId, error });

         return {
           success: false,
           error: error.message,
         };
       }
     }

     async updatePlugin(pluginId: string, version?: string): Promise<UpdateResult> {
       this.logger.info('Updating plugin', { pluginId, version });

       try {
         const plugin = this.plugins.get(pluginId);
         if (!plugin) {
           throw new PluginError('PLUGIN_NOT_FOUND', `Plugin ${pluginId} not found`);
         }

         // Check for updates
         const updateInfo = await this.checkForUpdates(plugin);
         if (!updateInfo.available) {
           return {
             success: true,
             pluginId,
             alreadyUpToDate: true,
           };
         }

         // Download update
         const source = updateInfo.source;

         // Deactivate current version
         await this.deactivatePlugin(pluginId);

         // Install new version
         const installResult = await this.installPlugin(source);

         if (!installResult.success) {
           // Reactivate old version on failure
           await this.activatePlugin(pluginId);
           throw new PluginError('UPDATE_FAILED', 'Failed to install updated version');
         }

         this.emit('pluginUpdated', {
           pluginId,
           oldVersion: plugin.manifest.version,
           newVersion: installResult.version,
         });

         return {
           success: true,
           pluginId,
           oldVersion: plugin.manifest.version,
           newVersion: installResult.version,
         };
       } catch (error) {
         this.logger.error('Plugin update failed', { pluginId, error });
         this.emit('pluginUpdateFailed', { pluginId, error });

         return {
           success: false,
           error: error.message,
         };
       }
     }

     getPlugin(pluginId: string): IPlugin | undefined {
       return this.plugins.get(pluginId);
     }

     getPlugins(): IPlugin[] {
       return Array.from(this.plugins.values());
     }

     getActivePlugins(): IPlugin[] {
       return this.getPlugins().filter((plugin) => plugin.isActive());
     }

     getPluginsByCategory(category: PluginCategory): IPlugin[] {
       return this.getPlugins().filter((plugin) => plugin.manifest.category === category);
     }

     async searchPlugins(query: PluginSearchQuery): Promise<PluginSearchResult> {
       return await this.registry.searchPlugins(query);
     }

     private async discoverPlugins(): Promise<void> {
       const pluginPaths = await this.loader.discoverPlugins();

       for (const pluginPath of pluginPaths) {
         try {
           const manifest = await this.loader.loadManifest(pluginPath);
           if (manifest && !this.plugins.has(manifest.id)) {
             this.logger.debug('Discovered plugin', { pluginId: manifest.id, path: pluginPath });
           }
         } catch (error) {
           this.logger.warn('Failed to discover plugin', { path: pluginPath, error });
         }
       }
     }

     private async loadInstalledPlugins(): Promise<void> {
       const installedPlugins = await this.loader.getInstalledPlugins();

       for (const pluginPath of installedPlugins) {
         try {
           const plugin = await this.loadPlugin(pluginPath);
           this.plugins.set(plugin.manifest.id, plugin);
         } catch (error) {
           this.logger.error('Failed to load installed plugin', { path: pluginPath, error });
         }
       }
     }

     private async loadPlugin(pluginPath: string): Promise<IPlugin> {
       // Load plugin in sandbox
       const sandboxedPlugin = await this.sandbox.loadPlugin(pluginPath);

       // Validate plugin interface
       if (!this.isValidPlugin(sandboxedPlugin)) {
         throw new PluginError('INVALID_PLUGIN', 'Plugin does not implement required interface');
       }

       return sandboxedPlugin;
     }

     private isValidPlugin(plugin: any): plugin is IPlugin {
       return (
         plugin &&
         typeof plugin.manifest === 'object' &&
         typeof plugin.provider === 'object' &&
         typeof plugin.initialize === 'function' &&
         typeof plugin.activate === 'function' &&
         typeof plugin.deactivate === 'function' &&
         typeof plugin.dispose === 'function'
       );
     }

     private async createPluginContext(plugin: IPlugin): Promise<PluginContext> {
       const pluginId = plugin.manifest.id;
       const pluginPath = await this.loader.getPluginPath(pluginId);

       return {
         pluginId,
         pluginPath,
         configPath: path.join(pluginPath, 'config'),
         dataPath: path.join(pluginPath, 'data'),
         logPath: path.join(pluginPath, 'logs'),
         tempPath: path.join(pluginPath, 'temp'),

         logger: this.logger.child({ plugin: pluginId }),
         eventBus: this.eventBus,
         storage: new PluginStorage(pluginId),
         registry: this.registry,

         providerRegistry: this.getProviderRegistry(),
         configManager: this.getConfigManager(),

         permissions: plugin.manifest.permissions,
         sandbox: this.sandbox.createSandboxContext(pluginId),

         utils: new PluginUtils(),
       };
     }

     private async registerProvider(plugin: IPlugin): Promise<void> {
       const providerRegistry = this.getProviderRegistry();
       await providerRegistry.register(plugin.manifest.id, plugin.provider);
     }

     private async unregisterProvider(plugin: IPlugin): Promise<void> {
       const providerRegistry = this.getProviderRegistry();
       await providerRegistry.unregister(plugin.manifest.id);
     }

     private async checkDependencies(manifest: PluginManifest): Promise<void> {
       for (const dependency of manifest.dependencies) {
         if (!dependency.optional) {
           const isInstalled = this.plugins.has(dependency.name);
           if (!isInstalled) {
             throw new PluginError(
               'MISSING_DEPENDENCY',
               `Required dependency ${dependency.name}@${dependency.version} is not installed`
             );
           }
         }
       }
     }

     private async checkForUpdates(plugin: IPlugin): Promise<UpdateInfo> {
       return await this.registry.checkForUpdates(plugin.manifest);
     }

     private setupEventHandlers(): void {
       this.eventBus.on('plugin:error', (event) => {
         this.logger.error('Plugin error', event);
       });

       this.eventBus.on('plugin:warning', (event) => {
         this.logger.warn('Plugin warning', event);
       });
     }

     private getProviderRegistry(): IProviderRegistry {
       // This would be injected or provided by the main application
       throw new Error('Provider registry not available in plugin system context');
     }

     private getConfigManager(): IConfigManager {
       // This would be injected or provided by the main application
       throw new Error('Config manager not available in plugin system context');
     }
   }

   export interface PluginSystemConfig {
     loader: PluginLoaderConfig;
     validation: PluginValidationConfig;
     sandbox: PluginSandboxConfig;
     registry: PluginRegistryConfig;
     events: PluginEventConfig;
   }

   export interface PluginSource {
     type: 'npm' | 'github' | 'url' | 'local' | 'registry';
     location: string;
     version?: string;
     checksum?: string;
   }

   export interface InstallResult {
     success: boolean;
     pluginId?: string;
     version?: string;
     installPath?: string;
     error?: string;
   }

   export interface UninstallResult {
     success: boolean;
     pluginId?: string;
     error?: string;
   }

   export interface ActivateResult {
     success: boolean;
     pluginId?: string;
     alreadyActive?: boolean;
     error?: string;
   }

   export interface DeactivateResult {
     success: boolean;
     pluginId?: string;
     alreadyInactive?: boolean;
     error?: string;
   }

   export interface UpdateResult {
     success: boolean;
     pluginId?: string;
     oldVersion?: string;
     newVersion?: string;
     alreadyUpToDate?: boolean;
     error?: string;
   }

   export interface UpdateInfo {
     available: boolean;
     source?: PluginSource;
     version?: string;
     changelog?: string;
   }

   export interface PluginSearchQuery {
     query?: string;
     category?: PluginCategory;
     type?: PluginType;
     tags?: string[];
     author?: string;
     verified?: boolean;
     limit?: number;
     offset?: number;
   }

   export interface PluginSearchResult {
     plugins: PluginManifest[];
     total: number;
     hasMore: boolean;
   }
   ```

### Subtask 8.2: Implement Plugin Loading Mechanism

**Objective**: Create secure and efficient plugin loading with sandboxing.

**Implementation Details**:

1. **File Location**: `packages/providers/src/plugin/PluginLoader.ts`

2. **Plugin Loader Implementation**:

   ```typescript
   export class PluginLoader {
     private config: PluginLoaderConfig;
     private logger: Logger;
     private pluginPaths: string[];
     private cache: Map<string, CachedPlugin> = new Map();

     constructor(config: PluginLoaderConfig, logger: Logger) {
       this.config = config;
       this.logger = logger;
       this.pluginPaths = config.pluginPaths || [this.getDefaultPluginPath()];
     }

     async initialize(): Promise<void> {
       this.logger.info('Initializing plugin loader');

       // Ensure plugin directories exist
       for (const pluginPath of this.pluginPaths) {
         await fs.ensureDir(pluginPath);
       }

       // Clean up cache
       await this.cleanupCache();
     }

     async downloadPlugin(source: PluginSource): Promise<PluginPackage> {
       this.logger.info('Downloading plugin', { source });

       switch (source.type) {
         case 'npm':
           return await this.downloadFromNpm(source);
         case 'github':
           return await this.downloadFromGitHub(source);
         case 'url':
           return await this.downloadFromUrl(source);
         case 'local':
           return await this.loadFromLocal(source);
         case 'registry':
           return await this.downloadFromRegistry(source);
         default:
           throw new PluginError('INVALID_SOURCE', `Unsupported source type: ${source.type}`);
       }
     }

     async installPlugin(pluginPackage: PluginPackage): Promise<string> {
       this.logger.info('Installing plugin package', {
         name: pluginPackage.manifest.name,
         version: pluginPackage.manifest.version,
       });

       const installPath = this.getInstallPath(pluginPackage.manifest);

       // Check if already installed
       if (await fs.pathExists(installPath)) {
         throw new PluginError('ALREADY_INSTALLED', `Plugin already installed at ${installPath}`);
       }

       try {
         // Create install directory
         await fs.ensureDir(installPath);

         // Extract plugin package
         await this.extractPlugin(pluginPackage, installPath);

         // Install dependencies
         await this.installDependencies(installPath, pluginPackage.manifest);

         // Run install script if present
         await this.runInstallScript(installPath, pluginPackage.manifest);

         // Verify installation
         await this.verifyInstallation(installPath, pluginPackage.manifest);

         // Cache plugin info
         this.cachePlugin(pluginPackage.manifest, installPath);

         return installPath;
       } catch (error) {
         // Cleanup on failure
         await fs.remove(installPath);
         throw error;
       }
     }

     async uninstallPlugin(pluginId: string): Promise<void> {
       this.logger.info('Uninstalling plugin', { pluginId });

       const installPath = await this.getPluginPath(pluginId);
       const manifest = await this.loadManifest(installPath);

       if (!manifest) {
         throw new PluginError('NOT_INSTALLED', `Plugin ${pluginId} is not installed`);
       }

       try {
         // Run uninstall script if present
         await this.runUninstallScript(installPath, manifest);

         // Remove plugin directory
         await fs.remove(installPath);

         // Remove from cache
         this.cache.delete(pluginId);
       } catch (error) {
         this.logger.error('Failed to uninstall plugin', { pluginId, error });
         throw error;
       }
     }

     async discoverPlugins(): Promise<string[]> {
       const pluginPaths: string[] = [];

       for (const basePath of this.pluginPaths) {
         try {
           const entries = await fs.readdir(basePath, { withFileTypes: true });

           for (const entry of entries) {
             if (entry.isDirectory()) {
               const pluginPath = path.join(basePath, entry.name);
               const manifestPath = path.join(pluginPath, 'plugin.json');

               if (await fs.pathExists(manifestPath)) {
                 pluginPaths.push(pluginPath);
               }
             }
           }
         } catch (error) {
           this.logger.warn('Failed to scan plugin directory', { path: basePath, error });
         }
       }

       return pluginPaths;
     }

     async getInstalledPlugins(): Promise<string[]> {
       return await this.discoverPlugins();
     }

     async loadManifest(pluginPath: string): Promise<PluginManifest | null> {
       try {
         const manifestPath = path.join(pluginPath, 'plugin.json');

         if (!(await fs.pathExists(manifestPath))) {
           return null;
         }

         const manifestContent = await fs.readFile(manifestPath, 'utf8');
         const manifest = JSON.parse(manifestContent);

         // Validate manifest structure
         this.validateManifest(manifest);

         return manifest;
       } catch (error) {
         this.logger.error('Failed to load plugin manifest', { pluginPath, error });
         return null;
       }
     }

     async getPluginPath(pluginId: string): Promise<string> {
       // Check cache first
       const cached = this.cache.get(pluginId);
       if (cached && (await fs.pathExists(cached.path))) {
         return cached.path;
       }

       // Search in plugin paths
       for (const basePath of this.pluginPaths) {
         const pluginPath = path.join(basePath, pluginId);
         if (await fs.pathExists(pluginPath)) {
           return pluginPath;
         }
       }

       throw new PluginError('NOT_FOUND', `Plugin ${pluginId} not found`);
     }

     private async downloadFromNpm(source: PluginSource): Promise<PluginPackage> {
       const packageName = source.location;
       const version = source.version || 'latest';

       this.logger.info('Downloading from npm', { packageName, version });

       // Use npm pack to download the package
       const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), 'tamma-plugin-'));
       const tarballPath = path.join(tempDir, 'package.tgz');

       try {
         await execAsync(`npm pack ${packageName}@${version} --pack-destination="${tempDir}"`);

         // Extract tarball
         const extractDir = path.join(tempDir, 'extracted');
         await fs.ensureDir(extractDir);
         await tar.extract({
           file: tarballPath,
           cwd: extractDir,
         });

         // Find package directory (might be in a subdirectory)
         const entries = await fs.readdir(extractDir, { withFileTypes: true });
         const packageDir = entries.find((entry) => entry.isDirectory())?.name || 'package';
         const packagePath = path.join(extractDir, packageDir);

         // Load manifest
         const manifest = await this.loadManifest(packagePath);
         if (!manifest) {
           throw new PluginError('INVALID_PACKAGE', 'No valid plugin manifest found');
         }

         // Create plugin package
         const pluginPackage: PluginPackage = {
           manifest,
           path: packagePath,
           source,
         };

         return pluginPackage;
       } finally {
         await fs.remove(tempDir);
       }
     }

     private async downloadFromGitHub(source: PluginSource): Promise<PluginPackage> {
       const [owner, repo] = source.location.split('/');
       const version = source.version || 'main';

       this.logger.info('Downloading from GitHub', { owner, repo, version });

       const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), 'tamma-plugin-'));
       const repoUrl = `https://github.com/${owner}/${repo}.git`;

       try {
         // Clone repository
         await execAsync(`git clone --depth 1 --branch ${version} ${repoUrl} "${tempDir}"`);

         // Load manifest
         const manifest = await this.loadManifest(tempDir);
         if (!manifest) {
           throw new PluginError('INVALID_PACKAGE', 'No valid plugin manifest found');
         }

         const pluginPackage: PluginPackage = {
           manifest,
           path: tempDir,
           source,
         };

         return pluginPackage;
       } finally {
         await fs.remove(tempDir);
       }
     }

     private async downloadFromUrl(source: PluginSource): Promise<PluginPackage> {
       this.logger.info('Downloading from URL', { url: source.location });

       const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), 'tamma-plugin-'));
       const filePath = path.join(tempDir, 'plugin');

       try {
         // Download file
         const response = await fetch(source.location);
         if (!response.ok) {
           throw new PluginError('DOWNLOAD_FAILED', `Failed to download: ${response.statusText}`);
         }

         const buffer = await response.arrayBuffer();
         await fs.writeFile(filePath, Buffer.from(buffer));

         // Determine file type and extract
         let extractDir = tempDir;

         if (source.location.endsWith('.tar.gz') || source.location.endsWith('.tgz')) {
           extractDir = path.join(tempDir, 'extracted');
           await fs.ensureDir(extractDir);
           await tar.extract({
             file: filePath,
             cwd: extractDir,
           });
         } else if (source.location.endsWith('.zip')) {
           extractDir = path.join(tempDir, 'extracted');
           await fs.ensureDir(extractDir);
           await extract(filePath, { dir: extractDir });
         }

         // Load manifest
         const manifest = await this.loadManifest(extractDir);
         if (!manifest) {
           throw new PluginError('INVALID_PACKAGE', 'No valid plugin manifest found');
         }

         const pluginPackage: PluginPackage = {
           manifest,
           path: extractDir,
           source,
         };

         return pluginPackage;
       } finally {
         await fs.remove(tempDir);
       }
     }

     private async loadFromLocal(source: PluginSource): Promise<PluginPackage> {
       const localPath = path.resolve(source.location);

       if (!(await fs.pathExists(localPath))) {
         throw new PluginError('NOT_FOUND', `Local path does not exist: ${localPath}`);
       }

       const manifest = await this.loadManifest(localPath);
       if (!manifest) {
         throw new PluginError('INVALID_PACKAGE', 'No valid plugin manifest found');
       }

       const pluginPackage: PluginPackage = {
         manifest,
         path: localPath,
         source,
       };

       return pluginPackage;
     }

     private async downloadFromRegistry(source: PluginSource): Promise<PluginPackage> {
       // Implementation for downloading from Tamma plugin registry
       throw new PluginError('NOT_IMPLEMENTED', 'Registry download not yet implemented');
     }

     private async extractPlugin(pluginPackage: PluginPackage, installPath: string): Promise<void> {
       await fs.copy(pluginPackage.path, installPath);
     }

     private async installDependencies(
       installPath: string,
       manifest: PluginManifest
     ): Promise<void> {
       const packageJsonPath = path.join(installPath, 'package.json');

       if (await fs.pathExists(packageJsonPath)) {
         this.logger.info('Installing plugin dependencies', { pluginId: manifest.id });

         try {
           await execAsync('npm install --production', { cwd: installPath });
         } catch (error) {
           throw new PluginError(
             'DEPENDENCY_INSTALL_FAILED',
             `Failed to install dependencies: ${error.message}`
           );
         }
       }
     }

     private async runInstallScript(installPath: string, manifest: PluginManifest): Promise<void> {
       if (manifest.lifecycle?.install) {
         this.logger.info('Running install script', { pluginId: manifest.id });

         try {
           await execAsync(manifest.lifecycle.install, { cwd: installPath });
         } catch (error) {
           throw new PluginError(
             'INSTALL_SCRIPT_FAILED',
             `Install script failed: ${error.message}`
           );
         }
       }
     }

     private async runUninstallScript(
       installPath: string,
       manifest: PluginManifest
     ): Promise<void> {
       if (manifest.lifecycle?.uninstall) {
         this.logger.info('Running uninstall script', { pluginId: manifest.id });

         try {
           await execAsync(manifest.lifecycle.uninstall, { cwd: installPath });
         } catch (error) {
           this.logger.warn('Uninstall script failed', { pluginId: manifest.id, error });
         }
       }
     }

     private async verifyInstallation(
       installPath: string,
       manifest: PluginManifest
     ): Promise<void> {
       // Verify manifest exists
       const manifestPath = path.join(installPath, 'plugin.json');
       if (!(await fs.pathExists(manifestPath))) {
         throw new PluginError(
           'INSTALLATION_FAILED',
           'Plugin manifest not found after installation'
         );
       }

       // Verify entry point exists
       const entryPoint = path.join(installPath, manifest.entryPoint);
       if (!(await fs.pathExists(entryPoint))) {
         throw new PluginError(
           'INSTALLATION_FAILED',
           `Plugin entry point not found: ${manifest.entryPoint}`
         );
       }

       // Verify checksum if provided
       if (manifest.checksum) {
         const actualChecksum = await this.calculateChecksum(installPath);
         if (actualChecksum !== manifest.checksum) {
           throw new PluginError('CHECKSUM_MISMATCH', 'Plugin checksum verification failed');
         }
       }
     }

     private validateManifest(manifest: any): void {
       const requiredFields = ['name', 'version', 'id', 'entryPoint', 'category', 'type'];

       for (const field of requiredFields) {
         if (!manifest[field]) {
           throw new PluginError('INVALID_MANIFEST', `Missing required field: ${field}`);
         }
       }

       // Validate version format
       if (!semver.valid(manifest.version)) {
         throw new PluginError('INVALID_VERSION', 'Invalid version format');
       }

       // Validate plugin ID format
       if (!/^[a-z0-9-]+$/.test(manifest.id)) {
         throw new PluginError(
           'INVALID_ID',
           'Plugin ID must contain only lowercase letters, numbers, and hyphens'
         );
       }
     }

     private async calculateChecksum(pluginPath: string): Promise<string> {
       const hash = createHash('sha256');
       const files = await this.getAllFiles(pluginPath);

       for (const file of files.sort()) {
         const content = await fs.readFile(path.join(pluginPath, file));
         hash.update(file);
         hash.update(content);
       }

       return hash.digest('hex');
     }

     private async getAllFiles(dir: string): Promise<string[]> {
       const files: string[] = [];
       const entries = await fs.readdir(dir, { withFileTypes: true });

       for (const entry of entries) {
         const fullPath = path.join(dir, entry.name);
         const relativePath = path.relative(dir, fullPath);

         if (entry.isDirectory()) {
           files.push(...(await this.getAllFiles(fullPath)));
         } else {
           files.push(relativePath);
         }
       }

       return files;
     }

     private getInstallPath(manifest: PluginManifest): string {
       return path.join(this.getDefaultPluginPath(), manifest.id);
     }

     private getDefaultPluginPath(): string {
       return path.join(os.homedir(), '.tamma', 'plugins');
     }

     private async cleanupCache(): Promise<void> {
       // Remove stale cache entries
       for (const [pluginId, cached] of this.cache.entries()) {
         if (!(await fs.pathExists(cached.path))) {
           this.cache.delete(pluginId);
         }
       }
     }

     private cachePlugin(manifest: PluginManifest, installPath: string): void {
       this.cache.set(manifest.id, {
         manifest,
         path: installPath,
         cachedAt: Date.now(),
       });
     }
   }

   export interface PluginPackage {
     manifest: PluginManifest;
     path: string;
     source: PluginSource;
   }

   export interface CachedPlugin {
     manifest: PluginManifest;
     path: string;
     cachedAt: number;
   }

   export interface PluginLoaderConfig {
     pluginPaths?: string[];
     cacheEnabled?: boolean;
     cacheTimeout?: number;
     maxConcurrentDownloads?: number;
     verifyChecksums?: boolean;
     allowUnsigned?: boolean;
   }
   ```

### Subtask 8.3: Add Plugin Validation and Security

**Objective**: Implement comprehensive validation and security measures for plugins.

**Implementation Details**:

1. **File Location**: `packages/providers/src/plugin/PluginValidator.ts`

2. **Plugin Validator Implementation**:

   ```typescript
   export class PluginValidator {
     private config: PluginValidationConfig;
     private logger: Logger;
     private schemaValidator: SchemaValidator;
     private securityScanner: SecurityScanner;
     private signatureVerifier: SignatureVerifier;

     constructor(config: PluginValidationConfig, logger: Logger) {
       this.config = config;
       this.logger = logger;
       this.schemaValidator = new SchemaValidator();
       this.securityScanner = new SecurityScanner(config.security);
       this.signatureVerifier = new SignatureVerifier(config.signatures);
     }

     async initialize(): Promise<void> {
       this.logger.info('Initializing plugin validator');

       await this.schemaValidator.initialize();
       await this.securityScanner.initialize();
       await this.signatureVerifier.initialize();
     }

     async validatePlugin(pluginPackage: PluginPackage): Promise<PluginValidationResult> {
       this.logger.info('Validating plugin', {
         name: pluginPackage.manifest.name,
         version: pluginPackage.manifest.version,
       });

       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       try {
         // Validate manifest
         const manifestResult = await this.validateManifest(pluginPackage.manifest);
         errors.push(...manifestResult.errors);
         warnings.push(...manifestResult.warnings);

         // Validate compatibility
         const compatibilityResult = await this.validateCompatibility(pluginPackage.manifest);
         errors.push(...compatibilityResult.errors);
         warnings.push(...compatibilityResult.warnings);

         // Validate dependencies
         const dependencyResult = await this.validateDependencies(pluginPackage.manifest);
         errors.push(...dependencyResult.errors);
         warnings.push(...dependencyResult.warnings);

         // Validate security
         const securityResult = await this.validateSecurity(pluginPackage);
         errors.push(...securityResult.errors);
         warnings.push(...securityResult.warnings);

         // Validate signature
         const signatureResult = await this.validateSignature(pluginPackage);
         errors.push(...signatureResult.errors);
         warnings.push(...signatureResult.warnings);

         // Validate code quality
         const codeQualityResult = await this.validateCodeQuality(pluginPackage);
         errors.push(...codeQualityResult.errors);
         warnings.push(...codeQualityResult.warnings);

         const isValid = errors.length === 0;

         this.logger.info('Plugin validation completed', {
           isValid,
           errors: errors.length,
           warnings: warnings.length,
         });

         return {
           valid: isValid,
           errors,
           warnings,
           summary: this.generateValidationSummary(errors, warnings),
         };
       } catch (error) {
         this.logger.error('Plugin validation failed', { error });

         return {
           valid: false,
           errors: [
             {
               code: 'VALIDATION_ERROR',
               message: `Validation failed: ${error.message}`,
               severity: 'error',
             },
           ],
           warnings: [],
         };
       }
     }

     private async validateManifest(manifest: PluginManifest): Promise<ValidationResult> {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       // Schema validation
       const schemaResult = await this.schemaValidator.validate('plugin-manifest', manifest);
       if (!schemaResult.valid) {
         errors.push(
           ...schemaResult.errors.map((err) => ({
             code: 'MANIFEST_SCHEMA_ERROR',
             message: err.message,
             field: err.field,
             severity: 'error' as const,
           }))
         );
       }

       // Required fields validation
       const requiredFields = ['name', 'version', 'id', 'entryPoint', 'category', 'type'];
       for (const field of requiredFields) {
         if (!manifest[field as keyof PluginManifest]) {
           errors.push({
             code: 'MISSING_REQUIRED_FIELD',
             message: `Missing required field: ${field}`,
             field,
             severity: 'error',
           });
         }
       }

       // Version validation
       if (manifest.version && !semver.valid(manifest.version)) {
         errors.push({
           code: 'INVALID_VERSION',
           message: `Invalid version format: ${manifest.version}`,
           field: 'version',
           severity: 'error',
         });
       }

       // Plugin ID validation
       if (manifest.id && !/^[a-z0-9-]+$/.test(manifest.id)) {
         errors.push({
           code: 'INVALID_PLUGIN_ID',
           message: 'Plugin ID must contain only lowercase letters, numbers, and hyphens',
           field: 'id',
           severity: 'error',
         });
       }

       // Entry point validation
       if (
         manifest.entryPoint &&
         !manifest.entryPoint.endsWith('.js') &&
         !manifest.entryPoint.endsWith('.ts')
       ) {
         warnings.push({
           code: 'NON_JS_ENTRY_POINT',
           message: 'Entry point should be a JavaScript or TypeScript file',
           field: 'entryPoint',
           severity: 'warning',
         });
       }

       // Homepage validation
       if (manifest.homepage && !this.isValidUrl(manifest.homepage)) {
         errors.push({
           code: 'INVALID_HOMEPAGE',
           message: 'Invalid homepage URL',
           field: 'homepage',
           severity: 'error',
         });
       }

       // Repository validation
       if (manifest.repository && !this.isValidUrl(manifest.repository)) {
         errors.push({
           code: 'INVALID_REPOSITORY',
           message: 'Invalid repository URL',
           field: 'repository',
           severity: 'error',
         });
       }

       return { errors, warnings };
     }

     private async validateCompatibility(manifest: PluginManifest): Promise<ValidationResult> {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       // Tamma version compatibility
       if (manifest.tammaVersion) {
         const currentVersion = await this.getCurrentTammaVersion();
         if (!semver.satisfies(currentVersion, manifest.tammaVersion)) {
           errors.push({
             code: 'INCOMPATIBLE_TAMMA_VERSION',
             message: `Plugin requires Tamma ${manifest.tammaVersion}, but current version is ${currentVersion}`,
             field: 'tammaVersion',
             severity: 'error',
           });
         }
       }

       // Node.js version compatibility
       if (manifest.engines?.node) {
         const currentNodeVersion = process.version;
         if (!semver.satisfies(currentNodeVersion, manifest.engines.node)) {
           errors.push({
             code: 'INCOMPATIBLE_NODE_VERSION',
             message: `Plugin requires Node.js ${manifest.engines.node}, but current version is ${currentNodeVersion}`,
             field: 'engines.node',
             severity: 'error',
           });
         }
       }

       // Platform compatibility
       const currentPlatform = os.platform();
       if (
         manifest.environment?.required &&
         !manifest.environment.required.includes(currentPlatform)
       ) {
         warnings.push({
           code: 'PLATFORM_COMPATIBILITY_WARNING',
           message: `Plugin may not be compatible with current platform: ${currentPlatform}`,
           field: 'environment.required',
           severity: 'warning',
         });
       }

       return { errors, warnings };
     }

     private async validateDependencies(manifest: PluginManifest): Promise<ValidationResult> {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       for (const dependency of manifest.dependencies) {
         // Check if dependency is available
         const isAvailable = await this.isDependencyAvailable(dependency);

         if (!isAvailable && !dependency.optional) {
           errors.push({
             code: 'MISSING_DEPENDENCY',
             message: `Required dependency ${dependency.name}@${dependency.version} is not available`,
             field: 'dependencies',
             severity: 'error',
           });
         } else if (!isAvailable && dependency.optional) {
           warnings.push({
             code: 'OPTIONAL_DEPENDENCY_MISSING',
             message: `Optional dependency ${dependency.name}@${dependency.version} is not available`,
             field: 'dependencies',
             severity: 'warning',
           });
         }

         // Check for known vulnerable dependencies
         const vulnerabilityCheck = await this.checkDependencyVulnerability(dependency);
         if (vulnerabilityCheck.hasVulnerabilities) {
           errors.push({
             code: 'VULNERABLE_DEPENDENCY',
             message: `Dependency ${dependency.name} has known vulnerabilities: ${vulnerabilityCheck.vulnerabilities.join(', ')}`,
             field: 'dependencies',
             severity: 'error',
           });
         }
       }

       return { errors, warnings };
     }

     private async validateSecurity(pluginPackage: PluginPackage): Promise<ValidationResult> {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       // Security scan
       const scanResult = await this.securityScanner.scan(pluginPackage.path);

       if (scanResult.hasVulnerabilities) {
         errors.push(
           ...scanResult.vulnerabilities.map((vuln) => ({
             code: 'SECURITY_VULNERABILITY',
             message: `Security vulnerability: ${vuln.description}`,
             field: vuln.file,
             severity: 'error' as const,
             details: vuln,
           }))
         );
       }

       if (scanResult.suspiciousPatterns.length > 0) {
         warnings.push(
           ...scanResult.suspiciousPatterns.map((pattern) => ({
             code: 'SUSPICIOUS_PATTERN',
             message: `Suspicious code pattern detected: ${pattern.description}`,
             field: pattern.file,
             severity: 'warning' as const,
             details: pattern,
           }))
         );
       }

       // Permission validation
       const permissionResult = this.validatePermissions(pluginPackage.manifest.permissions);
       errors.push(...permissionResult.errors);
       warnings.push(...permissionResult.warnings);

       // Resource limits validation
       const resourceResult = this.validateResourceLimits(pluginPackage.manifest.resources);
       errors.push(...resourceResult.errors);
       warnings.push(...resourceResult.warnings);

       return { errors, warnings };
     }

     private async validateSignature(pluginPackage: PluginPackage): Promise<ValidationResult> {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       if (!pluginPackage.manifest.signature) {
         if (this.config.signatures.requireSigned) {
           errors.push({
             code: 'MISSING_SIGNATURE',
             message: 'Plugin signature is required but not present',
             field: 'signature',
             severity: 'error',
           });
         } else {
           warnings.push({
             code: 'UNSIGNED_PLUGIN',
             message: 'Plugin is not signed',
             field: 'signature',
             severity: 'warning',
           });
         }

         return { errors, warnings };
       }

       // Verify signature
       const signatureResult = await this.signatureVerifier.verify(pluginPackage);

       if (!signatureResult.valid) {
         errors.push({
           code: 'INVALID_SIGNATURE',
           message: `Plugin signature verification failed: ${signatureResult.reason}`,
           field: 'signature',
           severity: 'error',
         });
       } else if (!signatureResult.trusted) {
         warnings.push({
           code: 'UNTRUSTED_SIGNATURE',
           message: 'Plugin signature is valid but from untrusted source',
           field: 'signature',
           severity: 'warning',
         });
       }

       return { errors, warnings };
     }

     private async validateCodeQuality(pluginPackage: PluginPackage): Promise<ValidationResult> {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       // Code quality checks
       const qualityResult = await this.analyzeCodeQuality(pluginPackage.path);

       if (qualityResult.complexityScore > this.config.quality.maxComplexity) {
         warnings.push({
           code: 'HIGH_COMPLEXITY',
           message: `Code complexity score (${qualityResult.complexityScore}) exceeds recommended threshold`,
           field: 'code',
           severity: 'warning',
         });
       }

       if (qualityResult.duplicateLines > this.config.quality.maxDuplicateLines) {
         warnings.push({
           code: 'CODE_DUPLICATION',
           message: `High code duplication detected: ${qualityResult.duplicateLines} duplicate lines`,
           field: 'code',
           severity: 'warning',
         });
       }

       if (qualityResult.testCoverage < this.config.quality.minTestCoverage) {
         warnings.push({
           code: 'LOW_TEST_COVERAGE',
           message: `Low test coverage: ${qualityResult.testCoverage}% (minimum: ${this.config.quality.minTestCoverage}%)`,
           field: 'tests',
           severity: 'warning',
         });
       }

       // Linting results
       const lintResult = await this.lintCode(pluginPackage.path);
       errors.push(
         ...lintResult.errors.map((err) => ({
           code: 'LINT_ERROR',
           message: err.message,
           field: err.file,
           severity: 'error' as const,
         }))
       );

       warnings.push(
         ...lintResult.warnings.map((warn) => ({
           code: 'LINT_WARNING',
           message: warn.message,
           field: warn.file,
           severity: 'warning' as const,
         }))
       );

       return { errors, warnings };
     }

     private validatePermissions(permissions: PluginPermission[]): ValidationResult {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       for (const permission of permissions) {
         // Check for dangerous permissions
         if (permission.dangerous && !this.config.security.allowDangerousPermissions) {
           errors.push({
             code: 'DANGEROUS_PERMISSION',
             message: `Dangerous permission not allowed: ${permission.type}:${permission.scope}`,
             field: 'permissions',
             severity: 'error',
           });
         }

         // Check permission scope validity
         if (!this.isValidPermissionScope(permission.type, permission.scope)) {
           errors.push({
             code: 'INVALID_PERMISSION_SCOPE',
             message: `Invalid permission scope: ${permission.type}:${permission.scope}`,
             field: 'permissions',
             severity: 'error',
           });
         }

         // Warn about broad permissions
         if (this.isBroadPermission(permission)) {
           warnings.push({
             code: 'BROAD_PERMISSION',
             message: `Broad permission requested: ${permission.type}:${permission.scope}`,
             field: 'permissions',
             severity: 'warning',
           });
         }
       }

       return { errors, warnings };
     }

     private validateResourceLimits(resources?: PluginResources): ValidationResult {
       const errors: ValidationError[] = [];
       const warnings: ValidationWarning[] = [];

       if (!resources) {
         return { errors, warnings };
       }

       // Memory limits
       if (resources.memory) {
         const memoryMB = this.parseMemorySize(resources.memory);
         if (memoryMB > this.config.security.maxMemoryMB) {
           errors.push({
             code: 'EXCESSIVE_MEMORY_REQUEST',
             message: `Requested memory (${resources.memory}) exceeds maximum (${this.config.security.maxMemoryMB}MB)`,
             field: 'resources.memory',
             severity: 'error',
           });
         }
       }

       // CPU limits
       if (resources.cpu && parseFloat(resources.cpu) > this.config.security.maxCpuCores) {
         errors.push({
           code: 'EXCESSIVE_CPU_REQUEST',
           message: `Requested CPU (${resources.cpu}) exceeds maximum (${this.config.security.maxCpuCores} cores)`,
           field: 'resources.cpu',
           severity: 'error',
         });
       }

       return { errors, warnings };
     }

     private generateValidationSummary(
       errors: ValidationError[],
       warnings: ValidationWarning[]
     ): string {
       const errorCount = errors.length;
       const warningCount = warnings.length;

       if (errorCount === 0 && warningCount === 0) {
         return 'Plugin validation passed with no issues';
       }

       let summary = `Plugin validation completed`;

       if (errorCount > 0) {
         summary += ` with ${errorCount} error${errorCount > 1 ? 's' : ''}`;
       }

       if (warningCount > 0) {
         summary += ` and ${warningCount} warning${warningCount > 1 ? 's' : ''}`;
       }

       return summary;
     }

     // Helper methods
     private isValidUrl(url: string): boolean {
       try {
         new URL(url);
         return true;
       } catch {
         return false;
       }
     }

     private async getCurrentTammaVersion(): Promise<string> {
       // Get current Tamma version from package.json
       const packageJsonPath = path.join(__dirname, '../../../package.json');
       const packageJson = await fs.readJson(packageJsonPath);
       return packageJson.version;
     }

     private async isDependencyAvailable(dependency: PluginDependency): Promise<boolean> {
       // Check if dependency is installed or available
       try {
         // For npm packages, check if they can be resolved
         require.resolve(dependency.name);
         return true;
       } catch {
         return false;
       }
     }

     private async checkDependencyVulnerability(
       dependency: PluginDependency
     ): Promise<VulnerabilityCheck> {
       // Check against known vulnerability database
       // This is a simplified implementation
       return {
         hasVulnerabilities: false,
         vulnerabilities: [],
       };
     }

     private isValidPermissionScope(type: PermissionType, scope: string): boolean {
       const validScopes: Record<PermissionType, string[]> = {
         network: ['*', 'https://*', 'http://*', 'specific-domain'],
         filesystem: ['read', 'write', 'readwrite', 'specific-path'],
         process: ['spawn', 'exec', 'kill'],
         environment: ['read', 'write', 'readwrite'],
         system: ['info', 'control'],
         provider: ['register', 'unregister', 'configure'],
         config: ['read', 'write', 'readwrite'],
         storage: ['read', 'write', 'readwrite'],
         logging: ['write', 'readwrite'],
         events: ['emit', 'listen', 'readwrite'],
       };

       return validScopes[type]?.includes(scope) || scope === '*';
     }

     private isBroadPermission(permission: PluginPermission): boolean {
       return (
         permission.scope === '*' ||
         (permission.type === 'filesystem' && permission.scope === 'readwrite') ||
         (permission.type === 'network' && permission.scope === '*')
       );
     }

     private parseMemorySize(size: string): number {
       const match = size.match(/^(\d+(?:\.\d+)?)\s*(GB|MB|KB)?$/i);
       if (!match) return 0;

       const value = parseFloat(match[1]);
       const unit = (match[2] || 'MB').toUpperCase();

       switch (unit) {
         case 'GB':
           return value * 1024;
         case 'MB':
           return value;
         case 'KB':
           return value / 1024;
         default:
           return value;
       }
     }

     private async analyzeCodeQuality(pluginPath: string): Promise<CodeQualityResult> {
       // Simplified code quality analysis
       return {
         complexityScore: 5,
         duplicateLines: 10,
         testCoverage: 80,
       };
     }

     private async lintCode(pluginPath: string): Promise<LintResult> {
       // Simplified linting
       return {
         errors: [],
         warnings: [],
       };
     }
   }

   export interface PluginValidationResult {
     valid: boolean;
     errors: ValidationError[];
     warnings: ValidationWarning[];
     summary: string;
   }

   export interface ValidationError {
     code: string;
     message: string;
     field?: string;
     severity: 'error';
     details?: any;
   }

   export interface ValidationWarning {
     code: string;
     message: string;
     field?: string;
     severity: 'warning';
     details?: any;
   }

   export interface ValidationResult {
     errors: ValidationError[];
     warnings: ValidationWarning[];
   }

   export interface VulnerabilityCheck {
     hasVulnerabilities: boolean;
     vulnerabilities: string[];
   }

   export interface CodeQualityResult {
     complexityScore: number;
     duplicateLines: number;
     testCoverage: number;
   }

   export interface LintResult {
     errors: Array<{ file: string; message: string; line: number }>;
     warnings: Array<{ file: string; message: string; line: number }>;
   }

   export interface PluginValidationConfig {
     security: PluginSecurityConfig;
     signatures: PluginSignatureConfig;
     quality: PluginQualityConfig;
   }

   export interface PluginSecurityConfig {
     allowDangerousPermissions: boolean;
     maxMemoryMB: number;
     maxCpuCores: number;
     blockedPatterns: string[];
     requireSandbox: boolean;
   }

   export interface PluginSignatureConfig {
     requireSigned: boolean;
     trustedKeys: string[];
     allowUnsigned: boolean;
   }

   export interface PluginQualityConfig {
     maxComplexity: number;
     maxDuplicateLines: number;
     minTestCoverage: number;
     requireLinting: boolean;
   }
   ```

## Technical Requirements

### Plugin System Requirements

- Dynamic loading and unloading of plugins
- Secure sandboxing for plugin execution
- Comprehensive validation and security scanning
- Event-driven communication between plugins and system
- Dependency management and resolution
- Version compatibility checking

### Security Requirements

- Plugin signature verification
- Permission-based access control
- Sandboxed execution environment
- Resource usage monitoring and limits
- Security vulnerability scanning
- Code quality validation

### Performance Requirements

- Fast plugin loading and initialization
- Minimal overhead for plugin communication
- Efficient resource management
- Scalable plugin registry
- Concurrent plugin support

## Testing Strategy

### Unit Tests

```typescript
describe('PluginSystem', () => {
  describe('installation', () => {
    it('should install valid plugins');
    it('should reject invalid plugins');
    it('should handle dependency resolution');
    it('should verify plugin signatures');
  });

  describe('lifecycle', () => {
    it('should activate plugins correctly');
    it('should deactivate plugins cleanly');
    it('should handle plugin errors');
    it('should manage plugin state');
  });

  describe('security', () => {
    it('should enforce permission boundaries');
    it('should sandbox plugin execution');
    it('should scan for vulnerabilities');
    it('should validate signatures');
  });
});
```

### Integration Tests

- Test plugin installation from various sources
- Test plugin communication and events
- Test security sandboxing
- Test plugin updates and versioning

### Security Tests

- Test permission enforcement
- Test sandbox isolation
- Test signature verification
- Test resource limits

## Dependencies

### Internal Dependencies

- `@tamma/shared` - Shared utilities and types
- Task 1 output - IAIProvider interface
- Task 2 output - Provider registry

### External Dependencies

- TypeScript 5.7+ - Type safety
- Node.js VM/Worker Threads - Sandboxing
- `semver` - Version management
- `tar` - Archive extraction
- `node-forge` - Cryptographic operations

## Deliverables

1. **Plugin System**: `packages/providers/src/plugin/PluginSystem.ts`
2. **Plugin Loader**: `packages/providers/src/plugin/PluginLoader.ts`
3. **Plugin Validator**: `packages/providers/src/plugin/PluginValidator.ts`
4. **Plugin Sandbox**: `packages/providers/src/plugin/PluginSandbox.ts`
5. **Plugin Registry**: `packages/providers/src/plugin/PluginRegistry.ts`
6. **Event Bus**: `packages/providers/src/plugin/PluginEventBus.ts`
7. **Unit Tests**: `packages/providers/src/plugin/__tests__/`
8. **Integration Tests**: `packages/providers/src/plugin/__integration__/`

## Acceptance Criteria Verification

- [ ] Plugin system supports dynamic loading and unloading
- [ ] Plugin validation is comprehensive and secure
- [ ] Sandboxing provides effective isolation
- [ ] Permission system enforces security boundaries
- [ ] Event system enables plugin communication
- [ ] Dependency management works correctly
- [ ] Performance requirements are met
- [ ] Security measures are effective

## Implementation Notes

### Plugin Architecture

Use a layered approach:

1. **Core System**: Plugin management and lifecycle
2. **Security Layer**: Validation, sandboxing, permissions
3. **Communication Layer**: Events, messaging, APIs
4. **Resource Layer**: Storage, configuration, utilities

### Security Strategy

Implement defense-in-depth:

1. **Static Analysis**: Pre-installation validation
2. **Runtime Sandboxing**: Isolated execution
3. **Permission System**: Access control
4. **Monitoring**: Runtime security checks

### Performance Optimization

- Lazy loading of plugin components
- Efficient event routing
- Resource pooling and caching
- Minimal overhead communication

## Next Steps

After completing this task:

1. All tasks for Story 2.1 are complete
2. Integration testing of the complete provider system
3. Documentation and examples for plugin development
4. Performance testing and optimization

## Risk Mitigation

### Security Risks

- **Malicious Plugins**: Comprehensive validation and sandboxing
- **Privilege Escalation**: Strict permission enforcement
- **Resource Exhaustion**: Resource limits and monitoring

### Technical Risks

- **Plugin Conflicts**: Dependency isolation and versioning
- **Performance Impact**: Efficient architecture and monitoring
- **Compatibility Issues**: Version management and testing

### Mitigation Strategies

- Multi-layer security validation
- Comprehensive testing framework
- Performance monitoring and alerting
- Regular security audits and updates
