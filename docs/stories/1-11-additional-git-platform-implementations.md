# Story 1.11: Additional Git Platform Implementations

Status: ready-for-dev

## Story

As a **Tamma operator**,
I want support for multiple Git platforms (Gitea, Forgejo, Bitbucket, Azure DevOps, and plain Git),
so that I can use Tamma with my preferred Git hosting service regardless of vendor.

## Acceptance Criteria

1. Gitea provider implements IGitPlatform interface with Gitea API integration
2. Forgejo provider implements IGitPlatform interface with Forgejo API integration
3. Bitbucket provider implements IGitPlatform interface with Bitbucket Cloud and Server API support
4. Azure DevOps provider implements IGitPlatform interface with Azure DevOps Services and Server API support
5. Plain Git provider implements IGitPlatform interface with local Git operations (no platform features)
6. Each provider includes comprehensive error handling, retry logic, and pagination support
7. Provider selection configurable via config file or environment variables
8. Integration tests validate each provider with real API calls (or local Git for plain Git provider)
9. Documentation includes platform comparison matrix and setup instructions for each platform

## Prerequisites

- Story 1-4: Git Platform Interface Definition (interface must exist)
- Story 1-5: GitHub Platform Implementation (reference implementation)
- Story 1-6: GitLab Platform Implementation (reference implementation)

## Tasks / Subtasks

### Task 1: Gitea Provider Implementation (AC: 1, 6, 8)

- [ ] Subtask 1.1: Research Gitea API capabilities and authentication
- [ ] Subtask 1.2: Create GiteaPlatform class implementing IGitPlatform
- [ ] Subtask 1.3: Integrate Gitea API client (REST API v1)
- [ ] Subtask 1.4: Implement repository operations (getRepository, listIssues, getIssue)
- [ ] Subtask 1.5: Implement branch operations (createBranch, getBranch)
- [ ] Subtask 1.6: Implement pull request operations (create, get, update, merge)
- [ ] Subtask 1.7: Add authentication (API token, OAuth2)
- [ ] Subtask 1.8: Add error handling (rate limits, API-specific errors)
- [ ] Subtask 1.9: Implement pagination and retry logic
- [ ] Subtask 1.10: Write unit and integration tests

### Task 2: Forgejo Provider Implementation (AC: 2, 6, 8)

- [ ] Subtask 2.1: Research Forgejo API capabilities (Gitea-compatible API)
- [ ] Subtask 2.2: Create ForgejoPlatform class implementing IGitPlatform
- [ ] Subtask 2.3: Integrate Forgejo API client (reuse Gitea client if API-compatible)
- [ ] Subtask 2.4: Implement repository operations (getRepository, listIssues, getIssue)
- [ ] Subtask 2.5: Implement branch operations (createBranch, getBranch)
- [ ] Subtask 2.6: Implement pull request operations (create, get, update, merge)
- [ ] Subtask 2.7: Add authentication (API token, OAuth2)
- [ ] Subtask 2.8: Add error handling (rate limits, API-specific errors)
- [ ] Subtask 2.9: Implement pagination and retry logic
- [ ] Subtask 2.10: Write unit and integration tests

### Task 3: Bitbucket Provider Implementation (AC: 3, 6, 8)

- [ ] Subtask 3.1: Research Bitbucket Cloud API v2 and Bitbucket Server API
- [ ] Subtask 3.2: Create BitbucketPlatform class implementing IGitPlatform
- [ ] Subtask 3.3: Integrate Bitbucket API clients (Cloud REST API v2, Server REST API)
- [ ] Subtask 3.4: Implement repository operations (getRepository, listIssues, getIssue)
- [ ] Subtask 3.5: Implement branch operations (createBranch, getBranch)
- [ ] Subtask 3.6: Implement pull request operations (create, get, update, merge)
- [ ] Subtask 3.7: Add authentication (App passwords, OAuth2, PAT for Server)
- [ ] Subtask 3.8: Add error handling (rate limits, API version differences)
- [ ] Subtask 3.9: Implement pagination (Cloud uses cursors, Server uses pages)
- [ ] Subtask 3.10: Implement retry logic with exponential backoff
- [ ] Subtask 3.11: Write unit and integration tests for Cloud and Server

### Task 4: Azure DevOps Provider Implementation (AC: 4, 6, 8)

- [ ] Subtask 4.1: Research Azure DevOps Services and Server API (REST API 7.1+)
- [ ] Subtask 4.2: Create AzureDevOpsPlatform class implementing IGitPlatform
- [ ] Subtask 4.3: Integrate Azure DevOps SDK or REST API client
- [ ] Subtask 4.4: Implement repository operations (getRepository, listWorkItems, getWorkItem)
- [ ] Subtask 4.5: Implement branch operations (createBranch, getBranch)
- [ ] Subtask 4.6: Implement pull request operations (create, get, update, complete)
- [ ] Subtask 4.7: Add authentication (PAT, OAuth2, Azure AD integration)
- [ ] Subtask 4.8: Add error handling (rate limits, API-specific errors, work item types)
- [ ] Subtask 4.9: Implement pagination and continuation tokens
- [ ] Subtask 4.10: Implement retry logic with exponential backoff
- [ ] Subtask 4.11: Write unit and integration tests for Services and Server

### Task 5: Plain Git Provider Implementation (AC: 5, 6, 8)

- [ ] Subtask 5.1: Research plain Git operations without platform features
- [ ] Subtask 5.2: Create PlainGitPlatform class implementing IGitPlatform
- [ ] Subtask 5.3: Integrate local Git client (simple-git or isomorphic-git)
- [ ] Subtask 5.4: Implement repository operations (local repository access)
- [ ] Subtask 5.5: Implement branch operations (createBranch, getBranch using Git commands)
- [ ] Subtask 5.6: Implement commit and push operations (no PR support)
- [ ] Subtask 5.7: Add error handling (Git command errors, merge conflicts)
- [ ] Subtask 5.8: Document limitations (no issues, no PRs, no CI/CD integration)
- [ ] Subtask 5.9: Write unit and integration tests with local Git repos

### Task 6: Platform Selection and Configuration (AC: 7)

- [ ] Subtask 6.1: Extend platform configuration schema for all new platforms
- [ ] Subtask 6.2: Add platform selection logic (read from config, environment)
- [ ] Subtask 6.3: Add platform factory pattern for dynamic provider instantiation
- [ ] Subtask 6.4: Add validation for platform-specific configuration
- [ ] Subtask 6.5: Add platform capability discovery (which platforms support which features)
- [ ] Subtask 6.6: Write tests for platform selection and configuration loading

### Task 7: Documentation and Platform Comparison (AC: 9)

- [ ] Subtask 7.1: Document Gitea setup (API token generation, permissions)
- [ ] Subtask 7.2: Document Forgejo setup (API token generation, permissions)
- [ ] Subtask 7.3: Document Bitbucket Cloud setup (App passwords, OAuth2)
- [ ] Subtask 7.4: Document Bitbucket Server setup (PAT, permissions)
- [ ] Subtask 7.5: Document Azure DevOps Services setup (PAT, OAuth2, Azure AD)
- [ ] Subtask 7.6: Document Azure DevOps Server setup (PAT, permissions)
- [ ] Subtask 7.7: Document Plain Git setup (local repository requirements, limitations)
- [ ] Subtask 7.8: Create platform comparison matrix (features, API maturity, rate limits)
- [ ] Subtask 7.9: Document platform selection strategy (when to use which platform)
- [ ] Subtask 7.10: Create troubleshooting guide for platform-specific issues
- [ ] Subtask 7.11: Update architecture documentation with platform extensibility patterns
- [ ] Subtask 7.12: Review documentation with stakeholders

## Dev Notes

### Requirements Context Summary

**Epic 1 Foundation:** This story extends Git platform support beyond GitHub and GitLab to include self-hosted options (Gitea, Forgejo), enterprise platforms (Bitbucket, Azure DevOps), and plain Git for minimal setups.

**Strategic Importance:** Git platform diversity enables Tamma to work with:
- **Self-hosted platforms**: Gitea, Forgejo (privacy-focused, air-gapped environments)
- **Enterprise platforms**: Bitbucket (Atlassian shops), Azure DevOps (Microsoft shops)
- **Minimal setups**: Plain Git (no platform features, just version control)

**Platform Priority:** Implementation order should be driven by user demand:
1. **Gitea/Forgejo**: HIGH priority (self-hosted, privacy-focused users)
2. **Bitbucket**: MEDIUM-HIGH priority (Atlassian enterprise users)
3. **Azure DevOps**: MEDIUM-HIGH priority (Microsoft enterprise users)
4. **Plain Git**: LOW priority (limited functionality, niche use case)

### Project Structure Notes

**Package Location:** `packages/platforms/src/` with separate directories per platform:
- `gitea/gitea-platform.ts` - Gitea implementation
- `forgejo/forgejo-platform.ts` - Forgejo implementation (may reuse Gitea client if API-compatible)
- `bitbucket/bitbucket-platform.ts` - Bitbucket Cloud and Server implementation
- `azure-devops/azure-devops-platform.ts` - Azure DevOps Services and Server implementation
- `plain-git/plain-git-platform.ts` - Plain Git implementation (local operations only)

**Dependencies:**
- `axios@^1.7.9` - HTTP client for Gitea, Forgejo, Bitbucket REST APIs
- `azure-devops-node-api@^13.0.0` - Azure DevOps SDK (official)
- `simple-git@^3.27.0` - Local Git operations for Plain Git provider
- Platform-specific SDK packages as needed

**TypeScript Configuration:** Strict mode TypeScript 5.7+ with proper interface implementation and error type definitions.

**Naming Conventions:** Follow established patterns: `GiteaPlatform`, `ForgejoPlatform`, `BitbucketPlatform`, `AzureDevOpsPlatform`, `PlainGitPlatform`.

### Platform-Specific Implementation Notes

**Gitea:**
- API: REST API v1 (similar to GitHub API v3)
- Auth: API tokens, OAuth2
- Rate Limits: Configurable per instance (default: 5000/hour)
- Notable Features: Issues, PRs, webhooks, CI/CD (Gitea Actions)

**Forgejo:**
- API: Gitea-compatible API (forked from Gitea)
- Auth: API tokens, OAuth2
- Rate Limits: Same as Gitea (configurable)
- Notable Features: Same as Gitea (may have Forgejo-specific extensions)
- Implementation: May reuse GiteaPlatform with minimal changes

**Bitbucket:**
- API: REST API v2 (Cloud), REST API 1.0-8.0 (Server/Data Center)
- Auth: App passwords (Cloud), PAT (Server), OAuth2 (both)
- Rate Limits: 1000 requests/hour (Cloud), configurable (Server)
- Notable Features: Issues, PRs, Pipelines (Cloud), Bamboo integration (Server)
- Implementation Challenge: API differences between Cloud and Server versions

**Azure DevOps:**
- API: REST API 7.1+ (Services and Server)
- Auth: PAT, OAuth2, Azure AD integration
- Rate Limits: 200 requests/user/5 minutes (Services), configurable (Server)
- Notable Features: Work Items (not issues), PRs, Pipelines, Boards
- Implementation Challenge: Work Items have complex types (User Story, Bug, Task, etc.)

**Plain Git:**
- API: Local Git commands via simple-git
- Auth: SSH keys, HTTPS credentials (local only)
- Rate Limits: None (local operations)
- Notable Features: Branches, commits, pushes (NO issues, NO PRs, NO CI/CD)
- Implementation Challenge: No platform features, limited to version control only

### Configuration Schema

Example multi-platform configuration:

```json
{
  "gitPlatforms": [
    {
      "name": "gitea",
      "enabled": true,
      "baseUrl": "https://gitea.example.com",
      "apiToken": "${GITEA_API_TOKEN}",
      "options": {
        "defaultBranch": "main",
        "autoMerge": true
      }
    },
    {
      "name": "forgejo",
      "enabled": false,
      "baseUrl": "https://forgejo.example.com",
      "apiToken": "${FORGEJO_API_TOKEN}",
      "options": {
        "defaultBranch": "main",
        "autoMerge": true
      }
    },
    {
      "name": "bitbucket",
      "enabled": false,
      "type": "cloud",
      "workspace": "my-workspace",
      "apiToken": "${BITBUCKET_APP_PASSWORD}",
      "options": {
        "defaultReviewers": ["user1", "user2"]
      }
    },
    {
      "name": "azure-devops",
      "enabled": false,
      "organization": "my-org",
      "project": "my-project",
      "apiToken": "${AZURE_DEVOPS_PAT}",
      "options": {
        "workItemType": "User Story",
        "defaultAreaPath": "my-project\\Team"
      }
    },
    {
      "name": "plain-git",
      "enabled": false,
      "repositoryPath": "/path/to/local/repo",
      "options": {
        "autoCommit": true,
        "autoPush": false
      }
    }
  ],
  "defaultPlatform": "github"
}
```

### API Quirks and Gotchas

**Gitea:**
- API closely mirrors GitHub API v3 (easier migration)
- Self-hosted instances may have different rate limits
- CI/CD support via Gitea Actions (similar to GitHub Actions)

**Forgejo:**
- Forked from Gitea in 2022, API mostly compatible
- Check for Forgejo-specific API extensions before falling back to Gitea client
- Self-hosted, community-driven (may have faster feature development)

**Bitbucket:**
- Cloud and Server APIs are VERY different (need separate implementations or abstraction)
- Cloud uses workspace/repository structure (not username/repository)
- Pagination: Cloud uses cursors, Server uses page numbers
- Pull Requests: Different approval models between Cloud and Server

**Azure DevOps:**
- Work Items != Issues (different data model: types, states, area paths, iterations)
- PR completion (not merge) - different terminology
- Organization/Project structure required in API calls
- Rate limits are per-user, not per-token (shared across all tools)

**Plain Git:**
- No issues, no PRs, no CI/CD (limited to basic version control)
- Requires local repository access (file system or SSH)
- No collaboration features (single-user workflow)
- Best for testing, prototyping, or air-gapped environments with manual workflows

### References

- [Gitea API Documentation](https://docs.gitea.com/api/1.22/)
- [Forgejo API Documentation](https://forgejo.org/docs/latest/user/api-usage/)
- [Bitbucket Cloud REST API](https://developer.atlassian.com/cloud/bitbucket/rest/)
- [Bitbucket Server REST API](https://developer.atlassian.com/server/bitbucket/rest/)
- [Azure DevOps REST API](https://learn.microsoft.com/en-us/rest/api/azure/devops/)
- [simple-git Documentation](https://github.com/steveukx/git-js)
- [Source: docs/tech-spec-epic-1.md#Git-Platform-Integration](F:\Code\Repos\Tamma\docs\tech-spec-epic-1.md#Git-Platform-Integration)
- [Source: docs/PRD.md#Git-Platform-Integration](F:\Code\Repos\Tamma\docs\PRD.md#Git-Platform-Integration)
- [Source: docs/architecture.md#Technology-Stack](F:\Code\Repos\Tamma\docs\architecture.md#Technology-Stack)

## Change Log

| Date | Version | Changes | Author |
|------|---------|----------|--------|
| 2025-10-28 | 1.0.0 | Initial story creation for additional Git platforms | Bob (Scrum Master) |

## Dev Agent Record

### Context Reference

- docs/stories/1-11-additional-git-platform-implementations.context.xml

### Agent Model Used

Claude-3.5-Sonnet

### Debug Log References

### Completion Notes List

### File List
