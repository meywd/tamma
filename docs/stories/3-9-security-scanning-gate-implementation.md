# Story 3.9: Security Scanning Gate Implementation

**Epic**: Epic 3 - Quality Gates & Intelligence Layer  
**Status**: Ready for Development  
**Priority**: High

## Acceptance Criteria

- [ ] Security scanning gate performs comprehensive security analysis across multiple layers
- [ ] System detects vulnerabilities in code, dependencies, containers, and infrastructure
- [ ] Scans cover OWASP Top 10, CWE, and industry-standard security frameworks
- [ ] System provides risk-based prioritization with CVSS scoring
- [ ] Security findings include detailed remediation guidance and exploit information
- [ ] Gate integrates with security tools and vulnerability databases
- [ ] System maintains security posture tracking and compliance reporting

## Technical Context

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**
1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)


### Security Scanning Gate Overview

The Security Scanning Gate is responsible for comprehensive security analysis across the entire application stack, including source code, dependencies, container images, infrastructure as code, and runtime configurations. It provides risk-based vulnerability detection, prioritization, and remediation guidance.

### Core Responsibilities

1. **Multi-Layer Security Analysis**
   - Static Application Security Testing (SAST) for source code
   - Dynamic Application Security Testing (DAST) for running applications
   - Software Composition Analysis (SCA) for dependencies
   - Container image scanning for vulnerabilities
   - Infrastructure as Code (IaC) security scanning

2. **Vulnerability Detection and Classification**
   - OWASP Top 10 vulnerability detection
   - Common Weakness Enumeration (CWE) mapping
   - Common Vulnerabilities and Exposures (CVE) integration
   - Custom security rule detection
   - Zero-day vulnerability detection

3. **Risk Assessment and Prioritization**
   - CVSS (Common Vulnerability Scoring System) scoring
   - Business impact assessment
   - Exploitability analysis
   - Threat modeling integration
   - Risk-based prioritization

4. **Remediation and Compliance**
   - Automated remediation suggestions
   - Security patch recommendations
   - Compliance framework mapping (SOC2, ISO27001, PCI-DSS)
   - Security policy enforcement
   - Incident response integration

### Implementation Details

#### Security Scanning Configuration Schema

```typescript
interface SecurityScanningConfig {
  // Scanning engines
  scanners: ScannerConfig[];

  // Vulnerability databases
  vulnerabilityDatabases: VulnerabilityDatabaseConfig[];

  // Risk assessment
  riskAssessment: {
    cvss: CVSSConfig;
    businessImpact: BusinessImpactConfig;
    exploitability: ExploitabilityConfig;
    threatModeling: ThreatModelingConfig;
  };

  // Compliance frameworks
  compliance: ComplianceConfig[];

  // Remediation settings
  remediation: {
    autoRemediation: AutoRemediationConfig;
    patchManagement: PatchManagementConfig;
    securityPolicies: SecurityPolicyConfig[];
  };

  // Reporting and alerts
  reporting: {
    formats: ReportFormat[];
    alerts: AlertConfig[];
    dashboards: DashboardConfig[];
    retention: RetentionConfig;
  };

  // Integration settings
  integrations: SecurityIntegrationConfig[];
}

interface ScannerConfig {
  id: string;
  name: string;
  type: 'sast' | 'dast' | 'sca' | 'container' | 'iac' | 'runtime';
  enabled: boolean;
  priority: number;
  config: Record<string, any>;
  rules: SecurityRule[];
  exclusions: ExclusionRule[];
  schedule: ScanSchedule;
}

interface VulnerabilityDatabaseConfig {
  name: string;
  type: 'nvd' | 'github' | 'snyk' | 'custom';
  enabled: boolean;
  updateInterval: number;
  apiKey?: string;
  endpoint: string;
  filters: DatabaseFilter[];
}

interface SecurityRule {
  id: string;
  name: string;
  description: string;
  category: SecurityCategory;
  severity: VulnerabilitySeverity;
  cwe?: string;
  owasp?: string;
  pattern: string | RegExp;
  conditions: RuleCondition[];
  remediation: RemediationGuidance;
  references: SecurityReference[];
}
```

#### Security Scanning Engine

```typescript
class SecurityScanningEngine implements ISecurityScanningEngine {
  private readonly config: SecurityScanningConfig;
  private readonly scanners: Map<string, ISecurityScanner>;
  private readonly vulnerabilityDatabase: IVulnerabilityDatabase;
  private readonly riskAssessor: IRiskAssessor;
  private readonly complianceChecker: IComplianceChecker;
  private readonly remediationEngine: IRemediationEngine;
  private readonly reportGenerator: ISecurityReportGenerator;

  async performSecurityScan(context: SecurityScanContext): Promise<SecurityScanResult> {
    const scanId = this.generateScanId();
    const startTime = Date.now();

    try {
      // Emit scan start event
      await this.eventStore.append({
        type: 'SECURITY_SCAN.STARTED',
        tags: {
          scanId,
          repository: context.repository.url,
          commit: context.commit.hash,
          environment: context.environment,
        },
        data: {
          scanners: this.config.scanners.filter((s) => s.enabled).map((s) => s.name),
          scanType: context.scanType,
          scope: context.scope,
        },
      });

      // Prepare scan targets
      const scanTargets = await this.prepareScanTargets(context);

      // Execute security scanners
      const scannerResults = await this.executeScanners(scanTargets, context);

      // Enrich with vulnerability database
      const enrichedResults = await this.enrichWithVulnerabilityDatabase(scannerResults);

      // Assess risk and prioritize
      const riskAssessment = await this.assessRisk(enrichedResults, context);

      // Check compliance
      const complianceResults = await this.checkCompliance(enrichedResults, context);

      // Generate remediation plan
      const remediationPlan = await this.generateRemediationPlan(
        enrichedResults,
        riskAssessment,
        context
      );

      // Create security scan result
      const securityScanResult: SecurityScanResult = {
        id: scanId,
        context,
        scannerResults,
        enrichedResults,
        riskAssessment,
        complianceResults,
        remediationPlan,
        summary: this.generateScanSummary(enrichedResults, riskAssessment),
        metadata: {
          scannedAt: new Date().toISOString(),
          duration: Date.now() - startTime,
          scanners: scannerResults.map((r) => r.scannerName),
          version: this.config.version,
        },
      };

      // Store scan results
      await this.storeScanResults(securityScanResult);

      // Trigger alerts for critical findings
      await this.triggerSecurityAlerts(securityScanResult);

      // Emit scan completion event
      await this.eventStore.append({
        type: 'SECURITY_SCAN.COMPLETED',
        tags: {
          scanId,
          repository: context.repository.url,
          commit: context.commit.hash,
        },
        data: {
          vulnerabilitiesCount: enrichedResults.vulnerabilities.length,
          criticalVulnerabilities: enrichedResults.vulnerabilities.filter(
            (v) => v.severity === 'critical'
          ).length,
          riskScore: riskAssessment.overallRiskScore,
          complianceStatus: complianceResults.overallStatus,
          duration: securityScanResult.metadata.duration,
        },
      });

      return securityScanResult;
    } catch (error) {
      // Emit scan error event
      await this.eventStore.append({
        type: 'SECURITY_SCAN.ERROR',
        tags: {
          scanId,
          repository: context.repository.url,
          commit: context.commit.hash,
        },
        data: {
          error: error.message,
          duration: Date.now() - startTime,
        },
      });

      throw error;
    }
  }

  private async executeScanners(
    scanTargets: ScanTargets,
    context: SecurityScanContext
  ): Promise<ScannerResult[]> {
    const results: ScannerResult[] = [];

    // Get enabled scanners for context
    const enabledScanners = this.config.scanners.filter(
      (scanner) => scanner.enabled && this.isScannerApplicable(scanner, context)
    );

    // Execute scanners in parallel where possible
    const scannerPromises = enabledScanners.map(async (scannerConfig) => {
      const scanner = this.scanners.get(scannerConfig.id);

      if (!scanner) {
        throw new Error(`Scanner not found: ${scannerConfig.id}`);
      }

      try {
        // Emit scanner start event
        await this.eventStore.append({
          type: 'SECURITY_SCAN.SCANNER_STARTED',
          tags: {
            scanId: context.scanId,
            scanner: scannerConfig.name,
            type: scannerConfig.type,
          },
          data: {
            targets: this.getTargetCount(scannerConfig.type, scanTargets),
            config: scannerConfig.config,
          },
        });

        // Configure scanner
        await scanner.configure(scannerConfig);

        // Execute scan
        const result = await scanner.scan(scanTargets, {
          context,
          rules: scannerConfig.rules,
          exclusions: scannerConfig.exclusions,
        });

        // Emit scanner completion event
        await this.eventStore.append({
          type: 'SECURITY_SCAN.SCANNER_COMPLETED',
          tags: {
            scanId: context.scanId,
            scanner: scannerConfig.name,
            type: scannerConfig.type,
          },
          data: {
            vulnerabilitiesFound: result.vulnerabilities.length,
            errorsCount: result.errors.length,
            duration: result.duration,
          },
        });

        return result;
      } catch (error) {
        // Emit scanner error event
        await this.eventStore.append({
          type: 'SECURITY_SCAN.SCANNER_ERROR',
          tags: {
            scanId: context.scanId,
            scanner: scannerConfig.name,
            type: scannerConfig.type,
          },
          data: {
            error: error.message,
          },
        });

        return {
          scannerName: scannerConfig.name,
          scannerType: scannerConfig.type,
          vulnerabilities: [],
          errors: [{ message: error.message, timestamp: new Date().toISOString() }],
          duration: 0,
          success: false,
        };
      }
    });

    const scannerResults = await Promise.allSettled(scannerPromises);

    // Process results
    for (const result of scannerResults) {
      if (result.status === 'fulfilled') {
        results.push(result.value);
      } else {
        // Log error but continue with other scanners
        await this.logger.error('Scanner failed', {
          error: result.reason,
          scanId: context.scanId,
        });
      }
    }

    return results;
  }

  private async enrichWithVulnerabilityDatabase(
    scannerResults: ScannerResult[]
  ): Promise<EnrichedSecurityResult> {
    const enrichedResult: EnrichedSecurityResult = {
      vulnerabilities: [],
      dependencies: [],
      containers: [],
      infrastructure: [],
      secrets: [],
      summary: {
        totalVulnerabilities: 0,
        criticalCount: 0,
        highCount: 0,
        mediumCount: 0,
        lowCount: 0,
      },
    };

    // Process all vulnerabilities from all scanners
    const allVulnerabilities = scannerResults.flatMap((result) => result.vulnerabilities);

    for (const vulnerability of allVulnerabilities) {
      // Enrich with vulnerability database
      const enrichedVulnerability = await this.enrichVulnerability(vulnerability);
      enrichedResult.vulnerabilities.push(enrichedVulnerability);
    }

    // Process dependencies
    const dependencyScanners = scannerResults.filter((r) => r.scannerType === 'sca');
    for (const scanner of dependencyScanners) {
      const enrichedDependencies = await this.enrichDependencies(scanner.dependencies);
      enrichedResult.dependencies.push(...enrichedDependencies);
    }

    // Process container vulnerabilities
    const containerScanners = scannerResults.filter((r) => r.scannerType === 'container');
    for (const scanner of containerScanners) {
      const enrichedContainers = await this.enrichContainers(scanner.containers);
      enrichedResult.containers.push(...enrichedContainers);
    }

    // Process infrastructure vulnerabilities
    const iacScanners = scannerResults.filter((r) => r.scannerType === 'iac');
    for (const scanner of iacScanners) {
      const enrichedInfrastructure = await this.enrichInfrastructure(scanner.infrastructure);
      enrichedResult.infrastructure.push(...enrichedInfrastructure);
    }

    // Process secrets
    const secretScanners = scannerResults.filter((r) => r.scannerType === 'sast');
    for (const scanner of secretScanners) {
      enrichedResult.secrets.push(...scanner.secrets);
    }

    // Calculate summary
    enrichedResult.summary = this.calculateVulnerabilitySummary(enrichedResult);

    return enrichedResult;
  }

  private async enrichVulnerability(vulnerability: Vulnerability): Promise<EnrichedVulnerability> {
    // Search vulnerability databases
    const databaseMatches = await this.vulnerabilityDatabase.search({
      cve: vulnerability.cve,
      cwe: vulnerability.cwe,
      title: vulnerability.title,
      description: vulnerability.description,
    });

    // Select best match
    const bestMatch = this.selectBestVulnerabilityMatch(databaseMatches, vulnerability);

    // Calculate CVSS score if not present
    const cvssScore = bestMatch?.cvssScore || (await this.calculateCVSSScore(vulnerability));

    // Assess exploitability
    const exploitability = await this.assessExploitability(vulnerability, bestMatch);

    // Map to compliance frameworks
    const complianceMappings = await this.mapToComplianceFrameworks(vulnerability, bestMatch);

    // Generate enhanced remediation
    const enhancedRemediation = await this.generateEnhancedRemediation(vulnerability, bestMatch);

    const enrichedVulnerability: EnrichedVulnerability = {
      ...vulnerability,
      cvssScore,
      exploitability,
      complianceMappings,
      enhancedRemediation,
      databaseReferences: bestMatch?.references || [],
      firstSeen: bestMatch?.firstSeen || vulnerability.detectedAt,
      lastSeen: new Date().toISOString(),
      publishedDate: bestMatch?.publishedDate,
      modifiedDate: bestMatch?.modifiedDate,
      confidence: this.calculateVulnerabilityConfidence(vulnerability, bestMatch),
      metadata: {
        enrichedAt: new Date().toISOString(),
        databaseSources: databaseMatches.map((m) => m.source),
        matchScore: bestMatch?.matchScore || 0,
      },
    };

    return enrichedVulnerability;
  }

  private async assessRisk(
    enrichedResults: EnrichedSecurityResult,
    context: SecurityScanContext
  ): Promise<RiskAssessment> {
    // Calculate individual vulnerability risks
    const vulnerabilityRisks = await Promise.all(
      enrichedResults.vulnerabilities.map((v) => this.assessVulnerabilityRisk(v, context))
    );

    // Calculate dependency risks
    const dependencyRisks = await Promise.all(
      enrichedResults.dependencies.map((d) => this.assessDependencyRisk(d, context))
    );

    // Calculate container risks
    const containerRisks = await Promise.all(
      enrichedResults.containers.map((c) => this.assessContainerRisk(c, context))
    );

    // Calculate infrastructure risks
    const infrastructureRisks = await Promise.all(
      enrichedResults.infrastructure.map((i) => this.assessInfrastructureRisk(i, context))
    );

    // Calculate overall risk score
    const overallRiskScore = this.calculateOverallRiskScore({
      vulnerabilities: vulnerabilityRisks,
      dependencies: dependencyRisks,
      containers: containerRisks,
      infrastructure: infrastructureRisks,
    });

    // Identify risk hotspots
    const riskHotspots = this.identifyRiskHotspots({
      vulnerabilities: vulnerabilityRisks,
      dependencies: dependencyRisks,
      containers: containerRisks,
      infrastructure: infrastructureRisks,
    });

    // Generate risk trends
    const riskTrends = await this.generateRiskTrends(context);

    const riskAssessment: RiskAssessment = {
      overallRiskScore,
      riskLevel: this.categorizeRiskLevel(overallRiskScore),
      vulnerabilityRisks,
      dependencyRisks,
      containerRisks,
      infrastructureRisks,
      riskHotspots,
      riskTrends,
      recommendations: await this.generateRiskRecommendations(riskHotspots, context),
      metadata: {
        assessedAt: new Date().toISOString(),
        riskModel: 'cvss_v3.1_business_impact',
        confidence: this.calculateRiskAssessmentConfidence(vulnerabilityRisks),
      },
    };

    return riskAssessment;
  }

  private async generateRemediationPlan(
    enrichedResults: EnrichedSecurityResult,
    riskAssessment: RiskAssessment,
    context: SecurityScanContext
  ): Promise<SecurityRemediationPlan> {
    // Prioritize vulnerabilities for remediation
    const prioritizedVulnerabilities = this.prioritizeForRemediation(
      enrichedResults.vulnerabilities,
      riskAssessment
    );

    // Group vulnerabilities by remediation type
    const remediationGroups = this.groupByRemediationType(prioritizedVulnerabilities);

    // Generate remediation phases
    const phases: RemediationPhase[] = [];

    // Phase 1: Critical vulnerabilities
    const criticalPhase = await this.generateRemediationPhase(
      'critical',
      remediationGroups.critical,
      { priority: 1, timeframe: '24-48 hours', autoRemediate: true }
    );
    phases.push(criticalPhase);

    // Phase 2: High vulnerabilities
    const highPhase = await this.generateRemediationPhase('high', remediationGroups.high, {
      priority: 2,
      timeframe: '1-2 weeks',
      autoRemediate: false,
    });
    phases.push(highPhase);

    // Phase 3: Medium vulnerabilities
    const mediumPhase = await this.generateRemediationPhase('medium', remediationGroups.medium, {
      priority: 3,
      timeframe: '1 month',
      autoRemediate: false,
    });
    phases.push(mediumPhase);

    // Phase 4: Low vulnerabilities
    const lowPhase = await this.generateRemediationPhase('low', remediationGroups.low, {
      priority: 4,
      timeframe: '3 months',
      autoRemediate: false,
    });
    phases.push(lowPhase);

    // Calculate total effort
    const totalEffort = phases.reduce((sum, phase) => sum + phase.estimatedEffort, 0);

    // Generate dependency updates
    const dependencyUpdates = await this.generateDependencyUpdates(enrichedResults.dependencies);

    const remediationPlan: SecurityRemediationPlan = {
      id: this.generateRemediationPlanId(),
      scanId: context.scanId,
      phases,
      dependencyUpdates,
      totalEffort,
      estimatedDuration: this.calculateTotalDuration(phases),
      resources: await this.calculateRequiredResources(phases),
      risks: await this.identifyRemediationRisks(phases),
      successCriteria: this.defineSuccessCriteria(enrichedResults),
      metadata: {
        generatedAt: new Date().toISOString(),
        prioritizationModel: 'risk_based',
        effortModel: 'story_points',
      },
    };

    return remediationPlan;
  }
}
```

#### Security Scanners

```typescript
class SASTScanner implements ISecurityScanner {
  private readonly config: SASTScannerConfig;
  private readonly ruleEngine: ISecurityRuleEngine;
  private readonly astParser: IASTParser;
  private readonly vulnerabilityMatcher: IVulnerabilityMatcher;

  async scan(targets: ScanTargets, options: ScanOptions): Promise<ScannerResult> {
    const vulnerabilities: Vulnerability[] = [];
    const errors: ScannerError[] = [];
    const startTime = Date.now();

    try {
      // Scan source files
      for (const file of targets.sourceFiles) {
        const fileVulnerabilities = await this.scanFile(file, options);
        vulnerabilities.push(...fileVulnerabilities);
      }

      // Remove duplicates
      const uniqueVulnerabilities = this.deduplicateVulnerabilities(vulnerabilities);

      return {
        scannerName: 'sast-scanner',
        scannerType: 'sast',
        vulnerabilities: uniqueVulnerabilities,
        errors,
        duration: Date.now() - startTime,
        success: errors.length === 0,
      };
    } catch (error) {
      return {
        scannerName: 'sast-scanner',
        scannerType: 'sast',
        vulnerabilities: [],
        errors: [{ message: error.message, timestamp: new Date().toISOString() }],
        duration: Date.now() - startTime,
        success: false,
      };
    }
  }

  private async scanFile(file: SourceFile, options: ScanOptions): Promise<Vulnerability[]> {
    const vulnerabilities: Vulnerability[] = [];

    try {
      // Parse AST
      const ast = await this.astParser.parse(file.content, file.language);

      // Apply security rules
      for (const rule of options.rules) {
        const ruleVulnerabilities = await this.applySecurityRule(rule, ast, file);
        vulnerabilities.push(...ruleVulnerabilities);
      }

      // Check for known vulnerability patterns
      const patternVulnerabilities = await this.checkVulnerabilityPatterns(ast, file);
      vulnerabilities.push(...patternVulnerabilities);

      // Check for hardcoded secrets
      const secretVulnerabilities = await this.checkForSecrets(file);
      vulnerabilities.push(...secretVulnerabilities);

      return vulnerabilities;
    } catch (error) {
      // Log error but continue with other files
      await this.logger.warn('Failed to scan file', {
        file: file.path,
        error: error.message,
      });

      return [];
    }
  }

  private async applySecurityRule(
    rule: SecurityRule,
    ast: AST,
    file: SourceFile
  ): Promise<Vulnerability[]> {
    const vulnerabilities: Vulnerability[] = [];

    // Find pattern matches in AST
    const matches = ast.query(rule.pattern);

    for (const match of matches) {
      const vulnerability: Vulnerability = {
        id: this.generateVulnerabilityId(),
        ruleId: rule.id,
        title: rule.name,
        description: rule.description,
        category: rule.category,
        severity: rule.severity,
        file: file.path,
        line: match.loc?.start.line || 0,
        column: match.loc?.start.column || 0,
        code: match.source || '',
        cwe: rule.cwe,
        owasp: rule.owasp,
        confidence: this.calculateRuleConfidence(rule, match),
        impact: this.assessImpact(rule, match),
        remediation: rule.remediation,
        references: rule.references,
        detectedAt: new Date().toISOString(),
        scanner: 'sast-scanner',
      };

      vulnerabilities.push(vulnerability);
    }

    return vulnerabilities;
  }

  private async checkForSecrets(file: SourceFile): Promise<Vulnerability[]> {
    const vulnerabilities: Vulnerability[] = [];

    // Secret detection patterns
    const secretPatterns = [
      {
        name: 'AWS Access Key',
        pattern: /AKIA[0-9A-Z]{16}/g,
        severity: 'critical' as VulnerabilitySeverity,
        cwe: 'CWE-798',
      },
      {
        name: 'GitHub Token',
        pattern: /ghp_[a-zA-Z0-9]{36}/g,
        severity: 'critical' as VulnerabilitySeverity,
        cwe: 'CWE-798',
      },
      {
        name: 'JWT Token',
        pattern: /eyJ[a-zA-Z0-9_-]*\.eyJ[a-zA-Z0-9_-]*\.[a-zA-Z0-9_-]*/g,
        severity: 'high' as VulnerabilitySeverity,
        cwe: 'CWE-522',
      },
      {
        name: 'Private Key',
        pattern: /-----BEGIN (RSA |OPENSSH |DSA |EC |PGP )?PRIVATE KEY-----/g,
        severity: 'critical' as VulnerabilitySeverity,
        cwe: 'CWE-798',
      },
      {
        name: 'Password in URL',
        pattern: /[a-zA-Z][a-zA-Z0-9._-]+:[a-zA-Z0-9._-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}/g,
        severity: 'high' as VulnerabilitySeverity,
        cwe: 'CWE-522',
      },
    ];

    for (const secretPattern of secretPatterns) {
      const matches = file.content.match(secretPattern.pattern);

      if (matches) {
        for (const match of matches) {
          const lines = file.content.split('\n');
          const lineNumber = lines.findIndex((line) => line.includes(match)) + 1;

          const vulnerability: Vulnerability = {
            id: this.generateVulnerabilityId(),
            ruleId: 'secret-detection',
            title: `Secret detected: ${secretPattern.name}`,
            description: `Potential ${secretPattern.name} found in source code`,
            category: 'secrets',
            severity: secretPattern.severity,
            file: file.path,
            line: lineNumber,
            column: 0,
            code: match,
            cwe: secretPattern.cwe,
            confidence: 0.9,
            impact: 'critical',
            remediation: {
              type: 'remove_secret',
              description:
                'Remove hardcoded secret and use environment variables or secret management',
              example: 'const apiKey = process.env.API_KEY;',
              references: [
                'https://owasp.org/www-project-cheat-sheets/cheatsheets/Secret_Management_Cheat_Sheet.html',
              ],
            },
            references: [
              {
                title: 'OWASP Secret Management',
                url: 'https://owasp.org/www-project-cheat-sheets/cheatsheets/Secret_Management_Cheat_Sheet.html',
              },
            ],
            detectedAt: new Date().toISOString(),
            scanner: 'sast-scanner',
          };

          vulnerabilities.push(vulnerability);
        }
      }
    }

    return vulnerabilities;
  }
}

class ContainerScanner implements ISecurityScanner {
  private readonly config: ContainerScannerConfig;
  private readonly imageAnalyzer: IContainerImageAnalyzer;
  private readonly vulnerabilityMatcher: IVulnerabilityMatcher;

  async scan(targets: ScanTargets, options: ScanOptions): Promise<ScannerResult> {
    const containers: ContainerVulnerability[] = [];
    const errors: ScannerError[] = [];
    const startTime = Date.now();

    try {
      // Scan container images
      for (const image of targets.containerImages) {
        const imageVulnerabilities = await this.scanContainerImage(image, options);
        containers.push(imageVulnerabilities);
      }

      return {
        scannerName: 'container-scanner',
        scannerType: 'container',
        vulnerabilities: containers.flatMap((c) => c.vulnerabilities),
        containers,
        errors,
        duration: Date.now() - startTime,
        success: errors.length === 0,
      };
    } catch (error) {
      return {
        scannerName: 'container-scanner',
        scannerType: 'container',
        vulnerabilities: [],
        containers: [],
        errors: [{ message: error.message, timestamp: new Date().toISOString() }],
        duration: Date.now() - startTime,
        success: false,
      };
    }
  }

  private async scanContainerImage(
    image: ContainerImage,
    options: ScanOptions
  ): Promise<ContainerVulnerability> {
    try {
      // Analyze container image layers
      const layers = await this.imageAnalyzer.analyzeLayers(image);

      // Extract installed packages
      const packages = await this.imageAnalyzer.extractPackages(layers);

      // Check for vulnerabilities in packages
      const packageVulnerabilities = await this.checkPackageVulnerabilities(packages);

      // Check for image configuration issues
      const configIssues = await this.checkImageConfiguration(image);

      // Check for root user usage
      const rootUserIssues = await this.checkRootUserUsage(image);

      // Combine all vulnerabilities
      const allVulnerabilities = [...packageVulnerabilities, ...configIssues, ...rootUserIssues];

      const containerVulnerability: ContainerVulnerability = {
        imageId: image.id,
        imageName: image.name,
        imageTag: image.tag,
        digest: image.digest,
        size: image.size,
        layers: layers.length,
        vulnerabilities: allVulnerabilities,
        baseImage: await this.identifyBaseImage(image),
        osInfo: await this.extractOSInfo(image),
        metadata: {
          scannedAt: new Date().toISOString(),
          scanner: 'container-scanner',
        },
      };

      return containerVulnerability;
    } catch (error) {
      throw new Error(`Failed to scan container image ${image.name}: ${error.message}`);
    }
  }

  private async checkPackageVulnerabilities(packages: PackageInfo[]): Promise<Vulnerability[]> {
    const vulnerabilities: Vulnerability[] = [];

    for (const pkg of packages) {
      // Search for vulnerabilities in this package
      const packageVulnerabilities = await this.vulnerabilityMatcher.searchPackageVulnerabilities({
        name: pkg.name,
        version: pkg.version,
        ecosystem: pkg.ecosystem,
      });

      for (const vuln of packageVulnerabilities) {
        const vulnerability: Vulnerability = {
          id: this.generateVulnerabilityId(),
          ruleId: 'package-vulnerability',
          title: `Vulnerability in ${pkg.name}`,
          description: vuln.description,
          category: 'dependencies',
          severity: vuln.severity,
          file: `package:${pkg.name}`,
          line: 0,
          column: 0,
          code: `${pkg.name}@${pkg.version}`,
          cwe: vuln.cwe,
          cvssScore: vuln.cvssScore,
          confidence: vuln.confidence,
          impact: vuln.impact,
          remediation: {
            type: 'package_update',
            description: `Update ${pkg.name} to fixed version`,
            example: `${pkg.name}@${vuln.fixedVersion}`,
            references: vuln.references,
          },
          references: vuln.references,
          detectedAt: new Date().toISOString(),
          scanner: 'container-scanner',
        };

        vulnerabilities.push(vulnerability);
      }
    }

    return vulnerabilities;
  }
}
```

#### Risk Assessor

```typescript
class CVSSRiskAssessor implements IRiskAssessor {
  private readonly config: CVSSConfig;
  private readonly businessImpactAnalyzer: IBusinessImpactAnalyzer;
  private readonly exploitabilityAnalyzer: IExploitabilityAnalyzer;

  async assessVulnerabilityRisk(
    vulnerability: EnrichedVulnerability,
    context: SecurityScanContext
  ): Promise<VulnerabilityRisk> {
    // Calculate CVSS base score
    const baseScore =
      vulnerability.cvssScore?.baseScore || (await this.calculateBaseScore(vulnerability));

    // Calculate CVSS temporal score
    const temporalScore =
      vulnerability.cvssScore?.temporalScore || (await this.calculateTemporalScore(vulnerability));

    // Calculate CVSS environmental score
    const environmentalScore = await this.calculateEnvironmentalScore(vulnerability, context);

    // Assess business impact
    const businessImpact = await this.businessImpactAnalyzer.assess(vulnerability, context);

    // Assess exploitability
    const exploitability = await this.exploitabilityAnalyzer.assess(vulnerability, context);

    // Calculate overall risk score
    const overallRiskScore = this.calculateOverallRiskScore({
      baseScore,
      temporalScore,
      environmentalScore,
      businessImpact,
      exploitability,
    });

    const vulnerabilityRisk: VulnerabilityRisk = {
      vulnerabilityId: vulnerability.id,
      baseScore,
      temporalScore,
      environmentalScore,
      businessImpact,
      exploitability,
      overallRiskScore,
      riskLevel: this.categorizeRiskLevel(overallRiskScore),
      riskFactors: this.identifyRiskFactors(vulnerability, context),
      mitigation: await this.suggestMitigation(vulnerability, context),
      metadata: {
        assessedAt: new Date().toISOString(),
        riskModel: 'cvss_v3.1_enhanced',
        confidence: this.calculateRiskConfidence(vulnerability),
      },
    };

    return vulnerabilityRisk;
  }

  private async calculateBaseScore(vulnerability: EnrichedVulnerability): Promise<number> {
    // If CVSS score is already available, use it
    if (vulnerability.cvssScore?.baseScore) {
      return vulnerability.cvssScore.baseScore;
    }

    // Calculate base score from vulnerability characteristics
    const attackVector = this.mapToAttackVector(vulnerability);
    const attackComplexity = this.mapToAttackComplexity(vulnerability);
    const privilegesRequired = this.mapToPrivilegesRequired(vulnerability);
    const userInteraction = this.mapToUserInteraction(vulnerability);
    const scope = this.mapToScope(vulnerability);
    const confidentiality = this.mapToConfidentiality(vulnerability);
    const integrity = this.mapToIntegrity(vulnerability);
    const availability = this.mapToAvailability(vulnerability);

    return this.calculateCVSSBaseScore({
      attackVector,
      attackComplexity,
      privilegesRequired,
      userInteraction,
      scope,
      confidentiality,
      integrity,
      availability,
    });
  }

  private async calculateEnvironmentalScore(
    vulnerability: EnrichedVulnerability,
    context: SecurityScanContext
  ): Promise<number> {
    // Get environmental metrics
    const environmentalMetrics = await this.getEnvironmentalMetrics(context);

    // Adjust CVSS metrics based on environment
    const modifiedAttackVector = this.modifyAttackVector(vulnerability, environmentalMetrics);
    const modifiedAttackComplexity = this.modifyAttackComplexity(
      vulnerability,
      environmentalMetrics
    );
    const modifiedPrivilegesRequired = this.modifyPrivilegesRequired(
      vulnerability,
      environmentalMetrics
    );
    const modifiedUserInteraction = this.modifyUserInteraction(vulnerability, environmentalMetrics);
    const modifiedScope = this.modifyScope(vulnerability, environmentalMetrics);
    const modifiedConfidentiality = this.modifyConfidentiality(vulnerability, environmentalMetrics);
    const modifiedIntegrity = this.modifyIntegrity(vulnerability, environmentalMetrics);
    const modifiedAvailability = this.modifyAvailability(vulnerability, environmentalMetrics);

    return this.calculateCVSSEnvironmentalScore({
      baseScore: vulnerability.cvssScore?.baseScore || 0,
      modifiedAttackVector,
      modifiedAttackComplexity,
      modifiedPrivilegesRequired,
      modifiedUserInteraction,
      modifiedScope,
      modifiedConfidentiality,
      modifiedIntegrity,
      modifiedAvailability,
    });
  }
}
```

### Integration Points

#### Security Tool Integration

```typescript
interface SecurityToolIntegration {
  // Snyk
  snyk: {
    enabled: boolean;
    token: string;
    organization: string;
    severity: ('low' | 'medium' | 'high' | 'critical')[];
  };

  // Veracode
  veracode: {
    enabled: boolean;
    apiId: string;
    apiKey: string;
    profileId: string;
  };

  // Checkmarx
  checkmarx: {
    enabled: boolean;
    serverUrl: string;
    username: string;
    password: string;
    projectId: string;
  };

  // OWASP ZAP
  zap: {
    enabled: boolean;
    apiKey: string;
    proxyUrl: string;
    activeScan: boolean;
  };
}
```

#### Vulnerability Database Integration

```typescript
interface VulnerabilityDatabaseIntegration {
  // NVD (National Vulnerability Database)
  nvd: {
    enabled: boolean;
    apiKey: string;
    endpoint: string;
    updateInterval: number;
  };

  // GitHub Advisory Database
  githubAdvisory: {
    enabled: boolean;
    token: string;
    endpoint: string;
    updateInterval: number;
  };

  // OSV (Open Source Vulnerability Database)
  osv: {
    enabled: boolean;
    endpoint: string;
    updateInterval: number;
  };

  // VulnDB
  vulnDB: {
    enabled: boolean;
    apiKey: string;
    endpoint: string;
    updateInterval: number;
  };
}
```

### Error Handling and Recovery

#### Security Scan Error Handling

```typescript
class SecurityScanErrorHandler {
  async handleScanError(
    scanId: string,
    error: Error,
    context: SecurityScanContext
  ): Promise<ErrorHandlingResult> {
    // Log error
    await this.logger.error('Security scan error', {
      scanId,
      error: error.message,
      stack: error.stack,
      repository: context.repository.url,
    });

    // Classify error type
    const errorType = this.classifyError(error);

    // Determine recovery strategy
    const recoveryStrategy = this.getRecoveryStrategy(errorType);

    switch (recoveryStrategy) {
      case 'retry_with_subset':
        return await this.retryWithSubset(scanId, error, context);

      case 'use_cached_results':
        return await this.useCachedResults(scanId, error, context);

      case 'continue_with_partial_results':
        return await this.continueWithPartialResults(scanId, error, context);

      case 'escalate_to_security_team':
        return await this.escalateToSecurityTeam(scanId, error, context);

      default:
        return await this.handleUnknownError(scanId, error, context);
    }
  }

  private async retryWithSubset(
    scanId: string,
    error: Error,
    context: SecurityScanContext
  ): Promise<ErrorHandlingResult> {
    // Identify problematic scanners
    const problematicScanners = await this.identifyProblematicScanners(error);

    // Create new context without problematic scanners
    const retryContext = {
      ...context,
      scanners: context.scanners.filter((s) => !problematicScanners.includes(s.id)),
    };

    // Retry scan with subset
    const result = await this.retryScan(scanId, retryContext);

    return {
      strategy: 'retry_with_subset',
      success: result.success,
      excludedScanners: problematicScanners,
      message: `Retried scan excluding ${problematicScanners.length} problematic scanners`,
      originalError: error.message,
    };
  }
}
```

### Testing Strategy

#### Unit Tests

- Security rule application
- CVSS score calculation
- Vulnerability enrichment
- Risk assessment algorithms
- Remediation plan generation

#### Integration Tests

- End-to-end security scanning workflows
- Multi-scanner coordination
- Vulnerability database integration
- Risk assessment accuracy
- Report generation

#### Performance Tests

- Large codebase scanning performance
- Concurrent scanner execution
- Vulnerability database query performance
- Memory usage optimization

### Monitoring and Observability

#### Security Scanning Metrics

```typescript
interface SecurityScanningMetrics {
  // Scan volume
  scansInitiated: Counter;
  scansCompleted: Counter;
  scansFailed: Counter;

  // Vulnerability metrics
  vulnerabilitiesFound: Counter;
  criticalVulnerabilities: Counter;
  highVulnerabilities: Counter;
  mediumVulnerabilities: Counter;
  lowVulnerabilities: Counter;

  // Performance metrics
  scanDuration: Histogram;
  scannerPerformance: Histogram;
  vulnerabilityDatabaseQueryTime: Histogram;

  // Risk metrics
  averageRiskScore: Gauge;
  riskTrend: Gauge;
  remediationTime: Histogram;

  // Compliance metrics
  complianceScore: Gauge;
  frameworkCompliance: Gauge;
  policyViolations: Counter;
}
```

#### Security Scanning Events

```typescript
// Scan lifecycle events
SECURITY_SCAN.STARTED;
SECURITY_SCAN.SCANNER_STARTED;
SECURITY_SCAN.SCANNER_COMPLETED;
SECURITY_SCAN.SCANNER_ERROR;
SECURITY_SCAN.VULNERABILITY_FOUND;
SECURITY_SCAN.COMPLETED;
SECURITY_SCAN.ERROR;

// Risk assessment events
SECURITY_SCAN.RISK_ASSESSMENT_STARTED;
SECURITY_SCAN.RISK_CALCULATED;
SECURITY_SCAN.RISK_HOTSPOT_IDENTIFIED;
SECURITY_SCAN.RISK_TREND_GENERATED;

// Remediation events
SECURITY_SCAN.REMEDIATION_PLAN_GENERATED;
SECURITY_SCAN.AUTO_REMEDIATION_STARTED;
SECURITY_SCAN.AUTO_REMEDIATION_COMPLETED;
SECURITY_SCAN.PATCH_APPLIED;

// Compliance events
SECURITY_SCAN.COMPLIANCE_CHECK_STARTED;
SECURITY_SCAN.COMPLIANCE_VIOLATION_FOUND;
SECURITY_SCAN.COMPLIANCE_REPORT_GENERATED;
SECURITY_SCAN.POLICY_VIOLATION_DETECTED;
```

### Configuration Examples

#### Security Scanning Configuration

```yaml
securityScanning:
  scanners:
    - id: 'sast-scanner'
      name: 'Static Application Security Testing'
      type: 'sast'
      enabled: true
      priority: 1
      config:
        languages: ['typescript', 'javascript', 'python', 'go']
        rulesets: ['owasp-top-10', 'security-extended']
        sensitivity: 'high'
      rules:
        - id: 'sql-injection'
          name: 'SQL Injection'
          description: 'Potential SQL injection vulnerability'
          category: 'injection'
          severity: 'critical'
          cwe: 'CWE-89'
          owasp: 'A03:2021'
          pattern: "query.*\\+.*|execute.*\\+.*"
          remediation:
            type: 'parameterized_queries'
            description: 'Use parameterized queries or prepared statements'
            example: "db.query('SELECT * FROM users WHERE id = ?', [userId])"
          references:
            - title: 'OWASP SQL Injection'
              url: 'https://owasp.org/www-community/attacks/SQL_Injection'

        - id: 'xss-vulnerability'
          name: 'Cross-Site Scripting'
          description: 'Potential XSS vulnerability'
          category: 'xss'
          severity: 'high'
          cwe: 'CWE-79'
          owasp: 'A03:2021'
          pattern: "innerHTML.*=|document\\.write"
          remediation:
            type: 'output_encoding'
            description: 'Encode user input before outputting to HTML'
            example: 'element.textContent = userInput;'
          references:
            - title: 'OWASP XSS Prevention'
              url: 'https://owasp.org/www-project-cheat-sheets/cheatsheets/Cross_Site_Scripting_Prevention_Cheat_Sheet.html'

      exclusions:
        - pattern: '**/test/**/*'
          reason: 'Test files excluded from security scanning'
        - pattern: '**/node_modules/**/*'
          reason: 'Third-party dependencies handled by SCA scanner'
      schedule:
        type: 'on_commit'
        branches: ['main', 'develop', 'feature/*']

    - id: 'sca-scanner'
      name: 'Software Composition Analysis'
      type: 'sca'
      enabled: true
      priority: 2
      config:
        manifestFiles:
          [
            'package.json',
            'package-lock.json',
            'yarn.lock',
            'requirements.txt',
            'go.mod',
            'Cargo.lock',
          ]
        licenseCheck: true
        vulnerabilityDatabase: 'github-advisory'
      rules:
        - id: 'vulnerable-dependency'
          name: 'Vulnerable Dependency'
          description: 'Dependency with known vulnerabilities'
          category: 'dependencies'
          severity: 'high'
          remediation:
            type: 'package_update'
            description: 'Update dependency to fixed version'
      schedule:
        type: 'on_commit'
        branches: ['main', 'develop']

    - id: 'container-scanner'
      name: 'Container Image Security'
      type: 'container'
      enabled: true
      priority: 3
      config:
        imageTypes: ['docker', 'oci']
        baseImageCheck: true
        packageScan: true
        configCheck: true
      rules:
        - id: 'root-user-container'
          name: 'Container Running as Root'
          description: 'Container configured to run as root user'
          category: 'container_security'
          severity: 'medium'
          remediation:
            type: 'user_configuration'
            description: 'Configure container to run as non-root user'
            example: 'USER appuser'
      schedule:
        type: 'on_image_build'

    - id: 'iac-scanner'
      name: 'Infrastructure as Code Security'
      type: 'iac'
      enabled: true
      priority: 4
      config:
        frameworks: ['terraform', 'cloudformation', 'kubernetes']
        checkId: 'checkov'
      rules:
        - id: 'open-security-group'
          name: 'Open Security Group'
          description: 'Security group with open access to the internet'
          category: 'cloud_security'
          severity: 'high'
          remediation:
            type: 'access_restriction'
            description: 'Restrict security group access to specific IPs/ranges'
      schedule:
        type: 'on_push'
        paths: ['**/*.tf', '**/*.yaml', '**/*.yml']

  vulnerabilityDatabases:
    - name: 'NVD'
      type: 'nvd'
      enabled: true
      updateInterval: 86400000 # 24 hours
      apiKey: '${NVD_API_KEY}'
      endpoint: 'https://services.nvd.nist.gov/rest/json/cves/2.0'
      filters:
        - severity: ['medium', 'high', 'critical']
        - cvssScore: { min: 6.0 }

    - name: 'GitHub Advisory'
      type: 'github'
      enabled: true
      updateInterval: 3600000 # 1 hour
      token: '${GITHUB_TOKEN}'
      endpoint: 'https://api.github.com/advisories'
      filters:
        - severity: ['moderate', 'high', 'critical']
        - publishedSince: '2020-01-01'

    - name: 'Snyk Vulnerability Database'
      type: 'snyk'
      enabled: true
      updateInterval: 3600000 # 1 hour
      token: '${SNYK_TOKEN}'
      endpoint: 'https://snyk.io/api/v1/vulns'
      filters:
        - severity: ['medium', 'high', 'critical']

  riskAssessment:
    cvss:
      version: '3.1'
      environmentalAdjustments: true
      temporalAdjustments: true

    businessImpact:
      enabled: true
      factors:
        - name: 'data_sensitivity'
          weight: 0.4
          levels: ['public', 'internal', 'confidential', 'restricted']
        - name: 'user_impact'
          weight: 0.3
          levels: ['low', 'medium', 'high', 'critical']
        - name: 'revenue_impact'
          weight: 0.3
          levels: ['low', 'medium', 'high', 'critical']

    exploitability:
      enabled: true
      factors:
        - name: 'weaponization'
          source: 'cisa_kev'
        - name: 'public_exploit'
          source: 'exploit_db'
        - name: 'active_attacks'
          source: 'threat_intelligence'

    threatModeling:
      enabled: true
      framework: 'stride'
      assets: ['data', 'systems', 'networks']
      attackSurfaces: ['web', 'api', 'mobile', 'infrastructure']

  compliance:
    - name: 'OWASP Top 10 2021'
      framework: 'owasp'
      version: '2021'
      enabled: true
      requirements:
        - id: 'A01'
          name: 'Broken Access Control'
          category: 'access_control'
          mandatory: true
        - id: 'A02'
          name: 'Cryptographic Failures'
          category: 'cryptography'
          mandatory: true
        - id: 'A03'
          name: 'Injection'
          category: 'injection'
          mandatory: true

    - name: 'SOC 2 Type II'
      framework: 'soc2'
      version: '2017'
      enabled: true
      requirements:
        - id: 'CC6.1'
          name: 'Logical and Physical Access Controls'
          category: 'access_control'
          mandatory: true
        - id: 'CC6.7'
          name: 'Transmission Encryption'
          category: 'encryption'
          mandatory: true
        - id: 'CC7.1'
          name: 'System Operation Detection'
          category: 'monitoring'
          mandatory: true

    - name: 'PCI DSS 4.0'
      framework: 'pci_dss'
      version: '4.0'
      enabled: true
      requirements:
        - id: '4.1'
          name: 'Encrypt Cardholder Data'
          category: 'encryption'
          mandatory: true
        - id: '6.2'
          name: 'Secure Development Practices'
          category: 'secure_coding'
          mandatory: true

  remediation:
    autoRemediation:
      enabled: true
      rules:
        - pattern: 'hardcoded_secret'
          action: 'remove_and_env_var'
          confidence: 0.9
        - pattern: 'missing_security_header'
          action: 'add_header'
          confidence: 0.8
      maxAutoFixes: 5
      requireApproval: true

    patchManagement:
      enabled: true
      autoPatch: false
      patchWindow: '30_days'
      excludePatchPatterns:
        - 'major_version_bump'
        - 'breaking_change'

    securityPolicies:
      - name: 'Critical Vulnerability Policy'
        description: 'Critical vulnerabilities must be fixed within 24 hours'
        conditions:
          - severity: 'critical'
        actions:
          - type: 'immediate_notification'
            recipients: ['security-team@example.com']
          - type: 'block_deployment'
          - type: 'create_incident'
        sla: '24_hours'

      - name: 'High Risk Vulnerability Policy'
        description: 'High vulnerabilities must be fixed within 7 days'
        conditions:
          - severity: 'high'
        actions:
          - type: 'create_ticket'
            assignee: 'security-team'
            priority: 'high'
          - type: 'weekly_reminder'
        sla: '7_days'

  reporting:
    formats:
      - name: 'html'
        enabled: true
        template: 'security_dashboard'
        includeCharts: true
      - name: 'json'
        enabled: true
        template: 'api_response'
        includeRawData: true
      - name: 'sarif'
        enabled: true
        template: 'sarif_v2.1.0'
        includeToolInfo: true
      - name: 'pdf'
        enabled: true
        template: 'executive_summary'
        includeTrends: true

    alerts:
      - name: 'Critical Vulnerability Alert'
        conditions:
          - severity: 'critical'
          - count: { min: 1 }
        channels:
          - type: 'email'
            recipients: ['security-team@example.com', 'dev-lead@example.com']
            template: 'critical_vulnerability'
          - type: 'slack'
            webhook: '${SLACK_SECURITY_WEBHOOK}'
            channel: '#security-alerts'
            template: 'critical_slack'
        throttle: '1_hour'

      - name: 'Compliance Violation Alert'
        conditions:
          - complianceStatus: 'non_compliant'
          - framework: ['soc2', 'pci_dss']
        channels:
          - type: 'email'
            recipients: ['compliance-team@example.com']
            template: 'compliance_violation'
        throttle: '24_hours'

    dashboards:
      - name: 'Security Overview'
        type: 'executive'
        widgets:
          - type: 'vulnerability_trend'
            timeframe: '90_days'
          - type: 'risk_score'
            realtime: true
          - type: 'compliance_status'
            frameworks: ['owasp', 'soc2', 'pci_dss']

      - name: 'Technical Security Dashboard'
        type: 'technical'
        widgets:
          - type: 'vulnerability_breakdown'
            groupBy: 'severity'
          - type: 'scanner_performance'
            scanners: ['sast', 'sca', 'container']
          - type: 'remediation_progress'
            timeframe: '30_days'

    retention:
      scanResults: 7776000000 # 90 days
      vulnerabilityData: 31536000000 # 1 year
      complianceReports: 31536000000 # 1 year
      auditLogs: 63072000000 # 2 years

  integrations:
    - name: 'SIEM Integration'
      type: 'siem'
      enabled: true
      config:
        endpoint: '${SIEM_ENDPOINT}'
        apiKey: '${SIEM_API_KEY}'
        eventType: 'security_vulnerability'
        severityMapping:
          critical: 'high'
          high: 'medium'
          medium: 'low'
          low: 'info'

    - name: 'Ticketing System'
      type: 'jira'
      enabled: true
      config:
        url: '${JIRA_URL}'
        username: '${JIRA_USERNAME}'
        apiToken: '${JIRA_API_TOKEN}'
        project: 'SEC'
        issueType: 'Vulnerability'
        priorityMapping:
          critical: 'Highest'
          high: 'High'
          medium: 'Medium'
          low: 'Low'

    - name: 'Container Registry'
      type: 'harbor'
      enabled: true
      config:
        url: '${HARBOR_URL}'
        username: '${HARBOR_USERNAME}'
        password: '${HARBOR_PASSWORD}'
        webhookEnabled: true
        scanOnPush: true
```

This implementation provides a comprehensive security scanning gate that can analyze code across multiple layers, detect vulnerabilities with industry-standard frameworks, assess risk using CVSS scoring, and provide actionable remediation guidance while maintaining compliance with various security frameworks.

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
