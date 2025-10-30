# Story 5.10: Alpha Release Preparation

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Drafted  
**MVP Priority**: Critical (MVP Essential)  
**Estimated Complexity**: High  
**Target Implementation**: Sprint 5 (MVP Critical)

## Story

As a **release manager**,
I want a release checklist and deployment artifacts for alpha launch,
so that early adopters can test the system in real projects.

## Acceptance Criteria

### Release Checklist and Validation

- [ ] **Release checklist completed** with all acceptance criteria met, integration tests passing, documentation complete, and security review passed
- [ ] **Pre-release validation script** runs automated checks for all critical components
- [ ] **Integration test suite passes** with >80% code coverage and all scenarios working
- [ ] **Documentation review completed** with external beta tester feedback incorporated
- [ ] **Security review passed** with vulnerability scans and dependency checks

### Release Artifacts and Build Process

- [ ] **Docker multi-arch images built** for amd64 and arm64 architectures with optimized layers
- [ ] **Binary releases created** for Windows (.exe), macOS (.dmg/.pkg), and Linux (.deb/.rpm/.tar.gz)
- [ ] **Source tarball prepared** with all dependencies and build scripts included
- [ ] **Release artifacts signed** with GPG signatures for security verification
- [ ] **Artifact checksums generated** (SHA256) for integrity verification

### Release Notes and Documentation

- [ ] **Release notes drafted** with features included, known limitations, breaking changes, and upgrade path
- [ ] **CHANGELOG updated** with version-specific changes and migration guide
- [ ] **README updated** with alpha installation instructions and warning notices
- [ ] **Version information updated** in package.json, CLI help, and about commands

### GitHub Release and Distribution

- [ ] **GitHub release created** with version tag (v0.1.0-alpha), release notes, and artifact downloads
- [ ] **Release tagged as prerelease** with appropriate warnings and limitations
- [ ] **Release assets uploaded** including all binaries, Docker images, and source tarball
- [ ] **Release metadata configured** with target market, changelog links, and support information

### Announcement and Communication

- [ ] **Release announcement prepared** for project README, Discord/Slack channels, and mailing list
- [ ] **Alpha warning banners added** to documentation and website with clear expectations
- [ ] **Support channels prepared** with issue templates, FAQ, and troubleshooting guides
- [ ] **Community guidelines established** for alpha testing feedback and bug reporting

### Telemetry and Analytics

- [ ] **Telemetry consent mechanism implemented** with opt-in for usage data collection
- [ ] **Privacy policy documented** with data collection, usage, and retention policies
- [ ] **Analytics dashboard prepared** for monitoring alpha adoption and usage patterns
- [ ] **Feedback collection system** ready for alpha user input and improvement suggestions

## Tasks / Subtasks

- [ ] **Task 1: Create release checklist and validation** (AC: 1, 2, 3, 4, 5)
  - [ ] Develop comprehensive pre-release checklist script
  - [ ] Implement automated validation for all acceptance criteria
  - [ ] Create integration test validation runner
  - [ ] Set up documentation review process
  - [ ] Implement security review automation

- [ ] **Task 2: Build multi-arch Docker images** (AC: 6, 7)
  - [ ] Configure Docker buildx for multi-platform builds
  - [ ] Create optimized multi-stage Dockerfile
  - [ ] Build and test amd64 and arm64 images
  - [ ] Push images to Docker Hub with proper tags
  - [ ] Implement image signing and verification

- [ ] **Task 3: Create platform-specific binaries** (AC: 6, 7)
  - [ ] Set up cross-compilation environment
  - [ ] Configure esbuild for binary bundling
  - [ ] Create Windows installer with certificate signing
  - [ ] Build macOS packages (.dmg and .pkg)
  - [ ] Generate Linux packages (.deb, .rpm, .tar.gz)

- [ ] **Task 4: Prepare source tarball and checksums** (AC: 6, 7, 8)
  - [ ] Create source distribution with dependencies
  - [ ] Generate SHA256 checksums for all artifacts
  - [ ] Create GPG signatures for security verification
  - [ ] Verify artifact integrity and signatures

- [ ] **Task 5: Draft release notes and documentation** (AC: 9, 10, 11, 12)
  - [ ] Generate comprehensive release notes
  - [ ] Update CHANGELOG with version-specific changes
  - [ ] Update README with alpha instructions
  - [ ] Add alpha warnings to all documentation

- [ ] **Task 6: Create GitHub release** (AC: 13, 14, 15, 16)
  - [ ] Configure GitHub release workflow
  - [ ] Create version tag (v0.1.0-alpha)
  - [ ] Upload all release artifacts
  - [ ] Configure prerelease settings and warnings

- [ ] **Task 7: Prepare announcements and communications** (AC: 17, 18, 19, 20)
  - [ ] Create announcement templates for different channels
  - [ ] Prepare Discord/Slack announcement messages
  - [ ] Draft mailing list announcement
  - [ ] Set up community support guidelines

- [ ] **Task 8: Implement telemetry and analytics** (AC: 21, 22, 23, 24)
  - [ ] Implement telemetry consent mechanism
  - [ ] Create privacy policy and documentation
  - [ ] Set up analytics dashboard for alpha monitoring
  - [ ] Configure feedback collection for alpha users

## Dev Notes

### Current System State

From `docs/stories/5-8-integration-testing-suite.md`:

- Comprehensive integration test suite with >80% code coverage
- Test scenarios covering all critical workflows and failure modes
- CI/CD pipeline integration for automated testing

From `docs/stories/5-9a-installation-setup-documentation.md`:

- Complete installation documentation for all platforms and methods
- Troubleshooting guides and common error solutions
- Configuration wizard and setup automation

From `docs/stories/5-9b-usage-configuration-documentation.md`:

- Comprehensive usage and configuration documentation
- CLI command reference and configuration file examples
- Provider setup guides and deployment mode documentation

From `docs/stories/1.5-5-docker-packaging.md`:

- Docker multi-stage builds for optimized images
- Multi-architecture build support with buildx
- Container security and best practices

From `docs/stories/1.5-8-npm-package-publishing.md`:

- npm package configuration and publishing workflow
- Version management and semantic versioning
- Package distribution and registry management

From `docs/stories/1.5-9-binary-releases-installers.md`:

- Cross-compilation setup for multiple platforms
- Binary signing and security verification
- Platform-specific packaging and installers

### Release Architecture

```yaml
# Alpha Release Architecture
release:
  version: '0.1.0-alpha'
  tag: 'v0.1.0-alpha'
  prerelease: true

artifacts:
  docker:
    - image: 'tamma/tamma:v0.1.0-alpha'
      platforms: ['linux/amd64', 'linux/arm64']
      registry: 'docker.io'

  binaries:
    - name: 'tamma-windows-x64.exe'
      platform: 'windows'
      architecture: 'amd64'
      signature: 'tamma-windows-x64.exe.sig'

    - name: 'tamma-macos-x64.dmg'
      platform: 'macos'
      architecture: 'amd64'
      signature: 'tamma-macos-x64.dmg.sig'

    - name: 'tamma-linux-x64.tar.gz'
      platform: 'linux'
      architecture: 'amd64'
      signature: 'tamma-linux-x64.tar.gz.sig'

  source:
    - name: 'tamma-v0.1.0-alpha.tar.gz'
      format: 'source'
      includes: ['src/', 'package.json', 'README.md']

validation:
  checklist: 'scripts/pre-release-checklist.sh'
  tests: 'packages/integration-tests'
  documentation: 'docs/'
  security: 'scripts/security-scan.sh'

distribution:
  github:
    repository: 'meywd/tamma'
    releases: true
    discussions: true

  docker_hub:
    repository: 'tamma/tamma'
    automated_builds: true

  npm:
    package: '@tamma/cli'
    tag: 'latest-alpha'

communications:
  announcement_channels:
    - 'github_discussions'
    - 'discord'
    - 'slack'
    - 'mailing_list'

  warning_banner: |
    ⚠️  **Alpha Release Warning**

    This is an alpha release of Tamma. It is not production-ready and may contain:
    - Breaking changes
    - Security vulnerabilities
    - Data loss bugs
    - Performance issues

    **Do not use in production environments.**

    Please report issues and provide feedback to help improve Tamma.
```

### Technical Implementation

#### 1. Release Checklist Script

```bash
#!/bin/bash
# scripts/pre-release-checklist.sh - Comprehensive pre-release validation

set -e

RELEASE_VERSION="0.1.0-alpha"
RELEASE_TAG="v0.1.0-alpha"
LOG_FILE="release-checklist.log"

echo "🚀 Tamma Alpha Release Checklist" | tee "$LOG_FILE"
echo "=================================" | tee -a "$LOG_FILE"
echo "Version: $RELEASE_VERSION" | tee -a "$LOG_FILE"
echo "Tag: $RELEASE_TAG" | tee -a "$LOG_FILE"
echo "" | tee -a "$LOG_FILE"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

check_passed=0
check_failed=0

check_step() {
    local step_name="$1"
    local step_command="$2"

    echo -n "Checking $step_name... " | tee -a "$LOG_FILE"

    if eval "$step_command" >> "$LOG_FILE" 2>&1; then
        echo -e "${GREEN}✓ PASS${NC}" | tee -a "$LOG_FILE"
        ((check_passed++))
        return 0
    else
        echo -e "${RED}✗ FAIL${NC}" | tee -a "$LOG_FILE"
        ((check_failed++))
        return 1
    fi
}

# 1. Acceptance Criteria Validation
check_step "Acceptance Criteria Complete" "
    find docs/stories/ -name '*.md' -exec grep -l 'Status.*Ready for Dev' {} \; |
    wc -l | grep -q '^10$'
"

# 2. Integration Tests
check_step "Integration Tests Passing" "
    cd packages/integration-tests && npm test -- --coverage --reporter=json |
    jq -r '.numTotalTests' |
    grep -q '^[0-9]\+$' &&
    [ \$(npm test -- --coverage --reporter=json | jq -r '.numPassingTests') -eq \$(npm test -- --coverage --reporter=json | jq -r '.numTotalTests') ]
"

# 3. Documentation Review
check_step "Documentation Complete" "
    [ -f 'docs/installation.md' ] &&
    [ -f 'docs/usage.md' ] &&
    [ -f 'docs/api-reference.md' ] &&
    [ -f 'CHANGELOG.md' ]
"

# 4. Security Review
check_step "Security Review Passed" "
    npm audit --audit-level=moderate |
    grep -q 'found 0 vulnerabilities' &&
    npx snyk test --severity-threshold=high |
    grep -q 'No vulnerabilities found'
"

# 5. Build Artifacts
check_step "Docker Images Built" "
    docker buildx imagetools inspect tamma/tamma:$RELEASE_TAG |
    grep -q 'linux/amd64' &&
    docker buildx imagetools inspect tamma/tamma:$RELEASE_TAG |
    grep -q 'linux/arm64'
"

check_step "Binary Releases Created" "
    [ -f 'dist/tamma-windows-x64.exe' ] &&
    [ -f 'dist/tamma-macos-x64.dmg' ] &&
    [ -f 'dist/tamma-linux-x64.tar.gz' ]
"

# 6. Version Information
check_step "Version Information Updated" "
    grep -q '\"version\": \"0.1.0-alpha\"' package.json &&
    grep -q 'v0.1.0-alpha' README.md &&
    npm run version -- --silent | grep -q '0.1.0-alpha'
"

# 7. Release Notes
check_step "Release Notes Prepared" "
    [ -f 'RELEASE_NOTES.md' ] &&
    grep -q '## Features' RELEASE_NOTES.md &&
    grep -q '## Known Limitations' RELEASE_NOTES.md &&
    grep -q '## Breaking Changes' RELEASE_NOTES.md
"

# 8. Telemetry Implementation
check_step "Telemetry Consent Implemented" "
    grep -q 'telemetry.*consent' packages/observability/src/telemetry.ts &&
    grep -q '--telemetry' packages/cli/src/index.ts
"

# Summary
echo "" | tee -a "$LOG_FILE"
echo "📊 Checklist Summary:" | tee -a "$LOG_FILE"
echo -e "Passed: ${GREEN}$check_passed${NC}" | tee -a "$LOG_FILE"
echo -e "Failed: ${RED}$check_failed${NC}" | tee -a "$LOG_FILE"

if [ $check_failed -eq 0 ]; then
    echo -e "${GREEN}🎉 All checks passed! Ready for release.${NC}" | tee -a "$LOG_FILE"
    exit 0
else
    echo -e "${RED}❌ $check_failed checks failed. Fix issues before release.${NC}" | tee -a "$LOG_FILE"
    exit 1
fi
```

#### 2. GitHub Release Workflow

```yaml
# .github/workflows/release.yml - Automated release workflow
name: Release

on:
  push:
    tags:
      - 'v*'

env:
  REGISTRY: docker.io
  IMAGE_NAME: tamma/tamma

jobs:
  # Security and validation
  security-scan:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '22'
          cache: 'npm'

      - name: Install dependencies
        run: pnpm install --frozen-lockfile

      - name: Security audit
        run: |
          npm audit --audit-level=moderate
          npx snyk test --severity-threshold=high

      - name: Run pre-release checklist
        run: |
          chmod +x scripts/pre-release-checklist.sh
          ./scripts/pre-release-checklist.sh

  # Build Docker images
  build-docker:
    needs: security-scan
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3

      - name: Log in to Docker Hub
        uses: docker/login-action@v3
        with:
          username: ${{ secrets.DOCKER_USERNAME }}
          password: ${{ secrets.DOCKER_PASSWORD }}

      - name: Extract metadata
        id: meta
        uses: docker/metadata-action@v5
        with:
          images: ${{ env.IMAGE_NAME }}
          tags: |
            type=ref,event=tag
            type=raw,value=latest,enable={{is_default_branch}}

      - name: Build and push Docker image
        uses: docker/build-push-action@v5
        with:
          context: .
          platforms: linux/amd64,linux/arm64
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}
          cache-from: type=gha
          cache-to: type=gha,mode=max

  # Build binaries
  build-binaries:
    needs: security-scan
    strategy:
      matrix:
        include:
          - os: windows-latest
            platform: windows
            arch: x64
            ext: .exe
          - os: macos-latest
            platform: macos
            arch: x64
            ext: .dmg
          - os: ubuntu-latest
            platform: linux
            arch: x64
            ext: .tar.gz

    runs-on: ${{ matrix.os }}
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup Node.js
        uses: actions/setup-node@v4
        with:
          node-version: '22'
          cache: 'npm'

      - name: Install dependencies
        run: pnpm install --frozen-lockfile

      - name: Build binary
        run: |
          pnpm build
          mkdir -p dist

          case "${{ matrix.platform }}" in
            windows)
              pkg packages/cli/src/index.js --targets node18-win-x64 --output dist/tamma-windows-x64.exe
              ;;
            macos)
              pkg packages/cli/src/index.js --targets node18-macos-x64 --output dist/tamma-macos-x64
              create-dmg dist/tamma-macos-x64 tamma-macos-x64.dmg
              ;;
            linux)
              pkg packages/cli/src/index.js --targets node18-linux-x64 --output dist/tamma-linux-x64
              tar -czf dist/tamma-linux-x64.tar.gz -C dist tamma-linux-x64
              ;;
          esac

      - name: Upload binary artifacts
        uses: actions/upload-artifact@v4
        with:
          name: tamma-${{ matrix.platform }}-${{ matrix.arch }}
          path: dist/tamma-*${{ matrix.ext }}

  # Create GitHub release
  create-release:
    needs: [build-docker, build-binaries]
    runs-on: ubuntu-latest
    permissions:
      contents: write
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Download all artifacts
        uses: actions/download-artifact@v4
        with:
          path: artifacts

      - name: Generate release notes
        run: |
          chmod +x scripts/generate-release-notes.sh
          ./scripts/generate-release-notes.sh > RELEASE_NOTES.md

      - name: Create source tarball
        run: |
          tar -czf tamma-${{ github.ref_name }}.tar.gz \
            --exclude=node_modules \
            --exclude=.git \
            --exclude=dist \
            .

      - name: Generate checksums
        run: |
          cd artifacts
          find . -name 'tamma-*' -type f -exec sha256sum {} + > ../checksums.txt
          cd ..
          sha256sum tamma-${{ github.ref_name }}.tar.gz >> checksums.txt

      - name: Create Release
        uses: softprops/action-gh-release@v2
        with:
          tag_name: ${{ github.ref_name }}
          name: Tamma ${{ github.ref_name }}
          body_path: RELEASE_NOTES.md
          prerelease: true
          draft: false
          files: |
            artifacts/*
            tamma-${{ github.ref_name }}.tar.gz
            checksums.txt
          generate_release_notes: false
```

#### 3. Release Notes Generator

````bash
#!/bin/bash
# scripts/generate-release-notes.sh - Generate comprehensive release notes

VERSION="0.1.0-alpha"
CHANGELOG="CHANGELOG.md"
RELEASE_NOTES="RELEASE_NOTES.md"

echo "📝 Generating Release Notes for $VERSION" >&2

# Extract version-specific changes from CHANGELOG
extract_changes() {
    local section="$1"
    local pattern="^## \[$VERSION\] - .*"

    echo "### $section" >> "$RELEASE_NOTES"
    echo "" >> "$RELEASE_NOTES"

    if grep -q "$pattern" "$CHANGELOG"; then
        sed -n "/$pattern/,/^## /p" "$CHANGELOG" | \
        sed '1d;$d' | \
        sed '/^$/d' >> "$RELEASE_NOTES"
    else
        echo "No $section for this release." >> "$RELEASE_NOTES"
    fi

    echo "" >> "$RELEASE_NOTES"
}

# Generate release notes
cat > "$RELEASE_NOTES" << EOF
# Tamma $VERSION - Alpha Release

> ⚠️ **Alpha Release Warning**
>
> This is an alpha release of Tamma. It is **not production-ready** and may contain:
> - Breaking changes
> - Security vulnerabilities
> - Data loss bugs
> - Performance issues
>
> **Do not use in production environments.**
>
> Please report issues and provide feedback to help improve Tamma.
>
> ## 🚀 What's New
>
EOF

# Extract changes from CHANGELOG
extract_changes "Features" "🆕 Features"
extract_changes "Improvements" "✨ Improvements"
extract_changes "Bug Fixes" "🐛 Bug Fixes"
extract_changes "Breaking Changes" "💥 Breaking Changes"

# Add known limitations
cat >> "$RELEASE_NOTES" << 'EOF'

## ⚠️ Known Limitations

- **Limited AI Provider Support**: Currently supports Anthropic Claude and OpenAI. Other providers coming soon.
- **GitHub Platform Only**: GitLab support is in development. Only GitHub repositories are supported.
- **Single-User Mode**: Multi-tenant support is planned for future releases.
- **Resource Requirements**: Requires Node.js 22+ and PostgreSQL 17+. Older versions not supported.
- **Linux/macOS/Windows**: Full support on these platforms. ARM64 support is experimental.

## 🔄 Upgrade Path

### From Source
If running from source:
```bash
git fetch origin
git checkout v0.1.0-alpha
pnpm install
pnpm build
````

### From Previous Alpha

If running previous alpha version:

```bash
npm install -g @tamma/cli@0.1.0-alpha
tamma config validate
```

## 📋 System Requirements

- **Node.js**: 22.0.0 or higher
- **PostgreSQL**: 17.0 or higher
- **Memory**: Minimum 2GB RAM, 4GB recommended
- **Storage**: Minimum 1GB free space
- **Network**: Internet connection required for AI provider access

## 🔧 Installation

### Quick Install

```bash
# npm (recommended)
npm install -g @tamma/cli@0.1.0-alpha

# Docker
docker pull tamma/tamma:v0.1.0-alpha
docker run -p 3000:3000 tamma/tamma:v0.1.0-alpha
```

See [Installation Guide](docs/installation.md) for detailed instructions.

## 📚 Documentation

- [Installation Guide](docs/installation.md)
- [Usage Guide](docs/usage.md)
- [API Reference](docs/api-reference.md)
- [Troubleshooting](docs/troubleshooting.md)

## 🐛 Bug Reports & Feedback

- **GitHub Issues**: [Create new issue](https://github.com/meywd/tamma/issues/new?labels=bug,alpha)
- **Discord**: [Join our Discord](https://discord.gg/tamma)
- **Discussions**: [GitHub Discussions](https://github.com/meywd/tamma/discussions)

## 🙏 Acknowledgments

Thanks to all the alpha testers and contributors who helped make this release possible!

---

**Download**: [GitHub Release](https://github.com/meywd/tamma/releases/tag/v0.1.0-alpha)

**Full Changelog**: [CHANGELOG.md](CHANGELOG.md)
EOF

echo "✅ Release notes generated: $RELEASE_NOTES" >&2

````

#### 4. Telemetry Implementation

```typescript
// packages/observability/src/telemetry.ts
import { Logger } from 'pino';
import { readFileSync, writeFileSync, existsSync } from 'fs';
import { join } from 'path';
import { homedir } from 'os';

export interface TelemetryConfig {
  enabled: boolean;
  consent_given: boolean;
  user_id?: string;
  installation_id: string;
  endpoint?: string;
}

export interface TelemetryEvent {
  event_type: string;
  timestamp: string;
  user_id?: string;
  installation_id: string;
  data: Record<string, any>;
}

export class TelemetryService {
  private config: TelemetryConfig;
  private logger: Logger;
  private configPath: string;

  constructor(logger: Logger) {
    this.logger = logger;
    this.configPath = join(homedir(), '.tamma', 'telemetry.json');
    this.config = this.loadConfig();
  }

  private loadConfig(): TelemetryConfig {
    try {
      if (existsSync(this.configPath)) {
        const config = JSON.parse(readFileSync(this.configPath, 'utf8'));
        return {
          enabled: config.enabled || false,
          consent_given: config.consent_given || false,
          user_id: config.user_id,
          installation_id: config.installation_id || this.generateInstallationId(),
          endpoint: config.endpoint
        };
      }
    } catch (error) {
      this.logger.warn('Failed to load telemetry config', { error });
    }

    return {
      enabled: false,
      consent_given: false,
      installation_id: this.generateInstallationId()
    };
  }

  private saveConfig(): void {
    try {
      const configDir = join(homedir(), '.tamma');
      if (!existsSync(configDir)) {
        mkdirSync(configDir, { recursive: true });
      }

      writeFileSync(this.configPath, JSON.stringify(this.config, null, 2));
    } catch (error) {
      this.logger.error('Failed to save telemetry config', { error });
    }
  }

  private generateInstallationId(): string {
    return `install_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  async requestConsent(): Promise<boolean> {
    if (this.config.consent_given) {
      return this.config.enabled;
    }

    console.log(`
🔍 Telemetry & Usage Analytics

Tamma can collect anonymous usage data to help improve the product. This data includes:
- Feature usage patterns
- Performance metrics
- Error reports (no code or credentials)
- System information (OS, Node.js version)

This data is:
- Completely anonymous
- Never includes your code or API keys
- Used only for product improvement
- Optional (you can opt out at any time)

Do you want to enable telemetry? (y/N)
    `);

    // In non-interactive mode, default to disabled
    if (process.stdin.isTTY) {
      const answer = await this.promptUser('Enable telemetry? (y/N): ');
      const consent = answer.toLowerCase().startsWith('y');

      this.config.consent_given = true;
      this.config.enabled = consent;
      this.saveConfig();

      console.log(consent ?
        '✅ Telemetry enabled. Thank you for helping improve Tamma!' :
        '❌ Telemetry disabled. You can enable it later with: tamma telemetry enable'
      );

      return consent;
    }

    this.config.consent_given = true;
    this.config.enabled = false;
    this.saveConfig();
    return false;
  }

  private async promptUser(question: string): Promise<string> {
    return new Promise((resolve) => {
      process.stdout.write(question);
      process.stdin.once('data', (data) => {
        resolve(data.toString().trim());
      });
    });
  }

  async trackEvent(eventType: string, data: Record<string, any>): Promise<void> {
    if (!this.config.enabled || !this.config.consent_given) {
      return;
    }

    const event: TelemetryEvent = {
      event_type: eventType,
      timestamp: new Date().toISOString(),
      user_id: this.config.user_id,
      installation_id: this.config.installation_id,
      data
    };

    try {
      // Send to telemetry endpoint (if configured)
      if (this.config.endpoint) {
        await this.sendEvent(event);
      }

      this.logger.debug('Telemetry event tracked', { eventType, data });
    } catch (error) {
      this.logger.warn('Failed to send telemetry event', { error, event });
    }
  }

  private async sendEvent(event: TelemetryEvent): Promise<void> {
    // Implementation would send to analytics service
    // For now, just log locally
    const logPath = join(homedir(), '.tamma', 'telemetry.log');
    const logEntry = JSON.stringify(event) + '\n';

    if (existsSync(logPath)) {
      appendFileSync(logPath, logEntry);
    } else {
      writeFileSync(logPath, logEntry);
    }
  }

  enable(): void {
    this.config.enabled = true;
    this.saveConfig();
    console.log('✅ Telemetry enabled');
  }

  disable(): void {
    this.config.enabled = false;
    this.saveConfig();
    console.log('❌ Telemetry disabled');
  }

  status(): void {
    console.log(`
Telemetry Status:
- Enabled: ${this.config.enabled ? 'Yes' : 'No'}
- Consent Given: ${this.config.consent_given ? 'Yes' : 'No'}
- Installation ID: ${this.config.installation_id}
- User ID: ${this.config.user_id || 'Not set'}
    `);
  }
}

// CLI telemetry commands
export async function handleTelemetryCommand(args: string[], telemetryService: TelemetryService): Promise<void> {
  const command = args[0];

  switch (command) {
    case 'enable':
      telemetryService.enable();
      break;

    case 'disable':
      telemetryService.disable();
      break;

    case 'status':
      telemetryService.status();
      break;

    case 'consent':
      await telemetryService.requestConsent();
      break;

    default:
      console.log(`
Usage: tamma telemetry <command>

Commands:
  enable     Enable telemetry
  disable    Disable telemetry
  status     Show telemetry status
  consent     Request consent for telemetry
      `);
  }
}
````

### Project Structure Notes

- Release scripts in `scripts/` directory with comprehensive validation
- GitHub Actions workflows in `.github/workflows/` for automated releases
- Release artifacts in `dist/` directory with proper naming conventions
- Telemetry service in `packages/observability/src/telemetry.ts`
- Documentation updates across all relevant files

### References

- [Source: docs/tech-spec-epic-5.md#Story-5.10-Alpha-Release-Preparation]
- [Source: docs/epics.md#Story-5.10-Alpha-Release-Preparation]
- [Source: docs/stories/5-8-integration-testing-suite.md]
- [Source: docs/stories/5-9a-installation-setup-documentation.md]
- [Source: docs/stories/5-9b-usage-configuration-documentation.md]
- [Source: docs/stories/1.5-5-docker-packaging.md]
- [Source: docs/stories/1.5-8-npm-package-publishing.md]
- [Source: docs/stories/1.5-9-binary-releases-installers.md]

## Dev Agent Record

### Context Reference

<!-- Path(s) to story context XML will be added here by context workflow -->

### Agent Model Used

claude-3-5-sonnet-20241022

### Debug Log References

### Completion Notes List

### File List
