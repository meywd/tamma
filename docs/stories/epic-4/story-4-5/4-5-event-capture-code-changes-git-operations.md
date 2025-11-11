# Story 4.5: Event Capture - Code Changes & Git Operations

## Overview

Implement comprehensive event capture for all code changes and Git operations to provide complete visibility into the evolution of code during autonomous development, enabling detailed code review, audit trails, and change analysis.

## Acceptance Criteria

### Code Change and Git Operation Event Capture

- [ ] `CodeFileWrittenEvent` captured for each file write including: file path, file size, change type (create/update/delete)
- [ ] `CommitCreatedEvent` captured for each commit including: commit SHA, message, branch name, file count
- [ ] `BranchCreatedEvent` captured when branch created (Story 2.4)
- [ ] `PRCreatedEvent` captured when PR created (Story 2.8) including: PR number, URL, base/head branches
- [ ] `PRMergedEvent` captured when PR merged (Story 2.10) including: merge strategy, merge SHA
- [ ] Events include file diffs stored in blob storage (linked from event)
- [ ] Events capture who triggered the action (user approval vs autonomous decision)

## Technical Context

### Event Capture Integration Points

This story integrates with the Git platform abstraction from Epic 1 and the autonomous workflow from Epic 2:

**Code File Written Event:**

```typescript
interface CodeFileWrittenEvent {
  eventId: string; // UUID v7
  timestamp: string; // ISO 8601 millisecond precision
  eventType: 'CodeFileWritten';
  actorType: 'system' | 'user' | 'ai';
  actorId: string; // System ID, user ID, or AI provider ID
  payload: {
    file: {
      path: string; // Relative path from repository root
      absolutePath: string; // Absolute file system path
      name: string; // File name with extension
      extension: string; // File extension
      directory: string; // Directory containing the file
      size: number; // File size in bytes
      encoding: string; // File encoding (utf-8, binary, etc.)
      language: string; // Programming language detected
      mimeType: string; // MIME type
    };
    change: {
      type: 'create' | 'update' | 'delete' | 'move' | 'copy';
      previousPath?: string; // For move/copy operations
      linesAdded: number; // Number of lines added
      linesRemoved: number; // Number of lines removed
      linesModified: number; // Number of lines modified
      binaryChange: boolean; // Whether this is a binary file change
    };
    content: {
      hash: string; // SHA-256 hash of new content
      previousHash?: string; // SHA-256 hash of previous content
      diffStorageId?: string; // ID of stored diff in blob storage
      contentStorageId?: string; // ID of full content in blob storage
      truncated: boolean; // Whether content is truncated in event
      preview?: string; // First 200 characters of content
    };
    context: {
      workflowId: string;
      correlationId: string;
      issueId?: string;
      prId?: string;
      commitId?: string;
      branchName?: string;
      step: string; // Workflow step that caused this change
      operation: 'generation' | 'modification' | 'refactoring' | 'testing' | 'documentation';
    };
    attribution: {
      source: 'ai_generated' | 'user_written' | 'system_generated' | 'template' | 'external';
      aiProvider?: string; // If AI generated
      aiModel?: string; // If AI generated
      userId?: string; // If user written
      approvalRequired: boolean;
      approvedBy?: string; // User ID if approved
    };
    metadata: {
      creationTime: string; // File creation timestamp
      modificationTime: string; // File modification timestamp
      permissions: string; // File permissions
      executable: boolean; // Whether file is executable
      readOnly: boolean; // Whether file is read-only
      hidden: boolean; // Whether file is hidden
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string; // Links entire development cycle
    workflowId: string;
    source: 'orchestrator' | 'worker' | 'cli' | 'api';
    mode: 'dev' | 'business';
    repositoryId: string; // Repository identifier
  };
}
```

**Commit Created Event:**

```typescript
interface CommitCreatedEvent {
  eventId: string;
  timestamp: string;
  eventType: 'CommitCreated';
  actorType: 'system' | 'user';
  actorId: string;
  payload: {
    commit: {
      sha: string; // Full commit SHA
      shortSha: string; // Short commit SHA (7 characters)
      message: string; // Commit message
      summary: string; // First line of commit message
      description?: string; // Rest of commit message
      author: {
        name: string;
        email: string;
        username?: string;
        id?: string;
      };
      committer: {
        name: string;
        email: string;
        username?: string;
        id?: string;
      };
      tree: {
        sha: string; // Tree SHA
        fileCount: number; // Number of files in commit
        additions: number; // Total lines added
        deletions: number; // Total lines deleted
        changes: number; // Total files changed
      };
      parents: Array<{
        sha: string;
        shortSha: string;
      }>;
    };
    branch: {
      name: string; // Branch name
      fullName: string; // Full branch reference (refs/heads/feature)
      isDefault: boolean; // Whether this is the default branch
      isProtected: boolean; // Whether branch is protected
    };
    files: Array<{
      path: string;
      changeType: 'added' | 'modified' | 'deleted' | 'renamed' | 'copied';
      additions: number;
      deletions: number;
      patch?: string; // Truncated patch
      diffStorageId?: string; // Full diff in blob storage
    }>;
    context: {
      workflowId: string;
      correlationId: string;
      issueId?: string;
      prId?: string;
      step: string; // Workflow step that triggered commit
      automated: boolean; // Whether commit was automated
      messageGenerated: boolean; // Whether commit message was AI-generated
    };
    attribution: {
      source: 'ai_generated' | 'user_written' | 'system_generated';
      aiProvider?: string;
      userId?: string;
      approvalRequired: boolean;
      approvedBy?: string;
    };
    signatures: Array<{
      type: 'gpg' | 'ssh' | 'x509';
      keyId: string;
      signer: string;
      signature: string;
      verified: boolean;
    }>;
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string;
    workflowId: string;
    source: 'orchestrator' | 'worker' | 'cli';
    mode: 'dev' | 'business';
    repositoryId: string;
  };
}
```

**Branch Created Event:**

```typescript
interface BranchCreatedEvent {
  eventId: string;
  timestamp: string;
  eventType: 'BranchCreated';
  actorType: 'system' | 'user';
  actorId: string;
  payload: {
    branch: {
      name: string; // Branch name
      fullName: string; // Full branch reference
      isDefault: boolean;
      isProtected: boolean;
      isRemote: boolean; // Whether this is a remote branch
    };
    source: {
      fromBranch?: string; // Source branch if created from another branch
      fromCommit?: string; // Source commit if created from commit
      fromTag?: string; // Source tag if created from tag
    };
    target: {
      commit: {
        sha: string;
        shortSha: string;
        message: string;
        author: string;
      };
      aheadBy?: number; // Commits ahead of source branch
      behindBy?: number; // Commits behind source branch
    };
    context: {
      workflowId: string;
      correlationId: string;
      issueId?: string;
      step: string; // Workflow step that created branch
      purpose: 'feature' | 'bugfix' | 'hotfix' | 'experimental' | 'release';
    };
    attribution: {
      source: 'ai_generated' | 'user_created' | 'system_generated';
      aiProvider?: string;
      userId?: string;
      automated: boolean;
    };
    configuration: {
      upstreamBranch?: string; // Default upstream branch
      mergeStrategy: 'merge' | 'rebase' | 'squash';
      protectionRules: Array<{
        type: 'require_reviews' | 'require_status_checks' | 'restrict_pushes';
        enabled: boolean;
        configuration?: Record<string, unknown>;
      }>;
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string;
    workflowId: string;
    source: 'orchestrator' | 'worker' | 'cli';
    mode: 'dev' | 'business';
    repositoryId: string;
  };
}
```

**PR Created Event:**

```typescript
interface PRCreatedEvent {
  eventId: string;
  timestamp: string;
  eventType: 'PRCreated';
  actorType: 'system' | 'user';
  actorId: string;
  payload: {
    pullRequest: {
      number: number; // PR number
      id: string; // PR ID
      title: string;
      description: string;
      url: string; // PR URL
      htmlUrl: string; // HTML URL
      state: 'open' | 'closed' | 'merged';
      isDraft: boolean; // Whether this is a draft PR
      mergeable: boolean; // Whether PR can be merged
      canMerge: boolean; // Whether current user can merge
    };
    branches: {
      base: {
        name: string;
        fullName: string;
        commit: {
          sha: string;
          message: string;
        };
        repository: {
          name: string;
          owner: string;
          url: string;
        };
      };
      head: {
        name: string;
        fullName: string;
        commit: {
          sha: string;
          message: string;
        };
        repository: {
          name: string;
          owner: string;
          url: string;
        };
      };
    };
    changes: {
      commits: number; // Number of commits
      additions: number; // Total lines added
      deletions: number; // Total lines deleted
      changedFiles: number; // Number of files changed
      diffStorageId?: string; // Full diff in blob storage
    };
    context: {
      workflowId: string;
      correlationId: string;
      issueId?: string;
      step: string; // Workflow step that created PR
      automated: boolean; // Whether PR was created automatically
      titleGenerated: boolean; // Whether PR title was AI-generated
      descriptionGenerated: boolean; // Whether PR description was AI-generated
    };
    attribution: {
      source: 'ai_generated' | 'user_created' | 'system_generated';
      aiProvider?: string;
      userId?: string;
      approvalRequired: boolean;
      reviewers: Array<{
        id: string;
        name: string;
        type: 'required' | 'optional';
      }>;
    };
    metadata: {
      createdAt: string;
      updatedAt: string;
      closedAt?: string;
      mergedAt?: string;
      mergeableState: 'clean' | 'dirty' | 'unknown' | 'draft';
      draft?: boolean;
      locked?: boolean;
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string;
    workflowId: string;
    source: 'orchestrator' | 'worker' | 'cli';
    mode: 'dev' | 'business';
    repositoryId: string;
  };
}
```

**PR Merged Event:**

```typescript
interface PRMergedEvent {
  eventId: string;
  timestamp: string;
  eventType: 'PRMerged';
  actorType: 'system' | 'user';
  actorId: string;
  payload: {
    pullRequest: {
      number: number;
      id: string;
      title: string;
      url: string;
      mergeCommitSha: string; // SHA of merge commit
      mergeStrategy: 'merge' | 'squash' | 'rebase';
    };
    merge: {
      commitSha: string; // SHA of merge commit
      message: string; // Merge commit message
      author: {
        name: string;
        email: string;
        id?: string;
      };
      timestamp: string; // Merge timestamp
    };
    branches: {
      base: {
        name: string;
        commitBefore: string; // Base branch commit before merge
        commitAfter: string; // Base branch commit after merge
      };
      head: {
        name: string;
        commitSha: string; // Head branch commit that was merged
        deleted: boolean; // Whether head branch was deleted after merge
      };
    };
    integration: {
      deployed: boolean; // Whether merge triggered deployment
      deploymentId?: string; // Deployment ID if triggered
      environment?: string; // Target environment
      rollbackEnabled: boolean; // Whether rollback is enabled
    };
    context: {
      workflowId: string;
      correlationId: string;
      issueId?: string;
      step: string; // Workflow step that triggered merge
      automated: boolean; // Whether merge was automated
      approvalRequired: boolean;
      approvedBy: string[]; // List of approvers
    };
    attribution: {
      source: 'ai_triggered' | 'user_triggered' | 'system_triggered';
      aiProvider?: string;
      userId?: string;
      mergeAuthorizedBy: string; // Who authorized the merge
    };
    quality: {
      checksPassed: number; // Number of quality checks passed
      checksFailed: number; // Number of quality checks failed
      testsPassed: number; // Number of tests passed
      testsFailed: number; // Number of tests failed
      coverage?: number; // Code coverage percentage
    };
  };
  metadata: {
    schemaVersion: '1.0.0';
    correlationId: string;
    workflowId: string;
    source: 'orchestrator' | 'worker' | 'cli';
    mode: 'dev' | 'business';
    repositoryId: string;
  };
}
```

### Event Capture Integration

```typescript
class CodeEventCapture {
  constructor(
    private eventStore: IEventStore,
    private diffStorage: DiffStorage,
    private gitPlatform: IGitPlatform,
    private fileSystem: FileSystem
  ) {}

  async captureFileWrite(
    filePath: string,
    changeType: 'create' | 'update' | 'delete',
    context: CodeChangeContext,
    correlationId: string
  ): Promise<string> {
    // Get file information
    const fileInfo = await this.fileSystem.getFileInfo(filePath);
    const previousInfo =
      changeType !== 'create' ? await this.fileSystem.getPreviousFileInfo(filePath) : null;

    // Calculate diff
    const diff =
      changeType !== 'create' && previousInfo
        ? await this.fileSystem.calculateDiff(previousInfo.content, fileInfo.content)
        : null;

    // Store diff in blob storage
    const diffStorageId = diff
      ? await this.diffStorage.storeDiff(diff, {
          filePath,
          changeType,
          timestamp: new Date().toISOString(),
          correlationId,
        })
      : undefined;

    // Store full content if needed
    const contentStorageId =
      fileInfo.size < 10000 // Only store small files
        ? await this.diffStorage.storeContent(fileInfo.content, {
            filePath,
            timestamp: new Date().toISOString(),
            correlationId,
          })
        : undefined;

    // Create event
    const event: CodeFileWrittenEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'CodeFileWritten',
      actorType: this.getActorType(context),
      actorId: this.getActorId(context),
      payload: {
        file: {
          path: filePath,
          absolutePath: fileInfo.absolutePath,
          name: fileInfo.name,
          extension: fileInfo.extension,
          directory: fileInfo.directory,
          size: fileInfo.size,
          encoding: fileInfo.encoding,
          language: this.detectLanguage(filePath),
          mimeType: fileInfo.mimeType,
        },
        change: {
          type: changeType,
          previousPath: context.previousPath,
          linesAdded: diff?.linesAdded || 0,
          linesRemoved: diff?.linesRemoved || 0,
          linesModified: diff?.linesModified || 0,
          binaryChange: fileInfo.isBinary,
        },
        content: {
          hash: fileInfo.hash,
          previousHash: previousInfo?.hash,
          diffStorageId,
          contentStorageId,
          truncated: !contentStorageId,
          preview: fileInfo.size < 200 ? fileInfo.content : fileInfo.content.substring(0, 200),
        },
        context: {
          workflowId: context.workflowId,
          correlationId,
          issueId: context.issueId,
          prId: context.prId,
          commitId: context.commitId,
          branchName: context.branchName,
          step: context.step,
          operation: context.operation,
        },
        attribution: {
          source: context.source,
          aiProvider: context.aiProvider,
          userId: context.userId,
          approvalRequired: context.approvalRequired,
          approvedBy: context.approvedBy,
        },
        metadata: {
          creationTime: fileInfo.creationTime,
          modificationTime: fileInfo.modificationTime,
          permissions: fileInfo.permissions,
          executable: fileInfo.executable,
          readOnly: fileInfo.readOnly,
          hidden: fileInfo.hidden,
        },
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: context.workflowId,
        source: this.getSource(),
        mode: this.getMode(),
        repositoryId: context.repositoryId,
      },
    };

    // Persist event
    await this.eventStore.append(event);

    return event.eventId;
  }

  async captureCommitCreated(
    commit: GitCommit,
    context: CommitContext,
    correlationId: string
  ): Promise<string> {
    // Get file changes
    const files = await this.gitPlatform.getCommitFiles(commit.sha);

    // Store full diff for large commits
    const diffStorageId =
      files.length > 20
        ? await this.diffStorage.storeCommitDiff(commit.sha, files, {
            timestamp: new Date().toISOString(),
            correlationId,
          })
        : undefined;

    // Create event
    const event: CommitCreatedEvent = {
      eventId: generateUUIDv7(),
      timestamp: new Date().toISOString(),
      eventType: 'CommitCreated',
      actorType: this.getActorType(context),
      actorId: this.getActorId(context),
      payload: {
        commit: {
          sha: commit.sha,
          shortSha: commit.shortSha,
          message: commit.message,
          summary: commit.summary,
          description: commit.description,
          author: commit.author,
          committer: commit.committer,
          tree: {
            sha: commit.treeSha,
            fileCount: files.length,
            additions: files.reduce((sum, f) => sum + f.additions, 0),
            deletions: files.reduce((sum, f) => sum + f.deletions, 0),
            changes: files.length,
          },
          parents: commit.parents,
        },
        branch: {
          name: context.branchName,
          fullName: `refs/heads/${context.branchName}`,
          isDefault: context.isDefaultBranch,
          isProtected: context.isProtectedBranch,
        },
        files: files.map((file) => ({
          path: file.path,
          changeType: file.changeType,
          additions: file.additions,
          deletions: file.deletions,
          patch: file.patch?.substring(0, 500), // Truncate patch
          diffStorageId: diffStorageId && file.changeType !== 'deleted' ? diffStorageId : undefined,
        })),
        context: {
          workflowId: context.workflowId,
          correlationId,
          issueId: context.issueId,
          prId: context.prId,
          step: context.step,
          automated: context.automated,
          messageGenerated: context.messageGenerated,
        },
        attribution: {
          source: context.source,
          aiProvider: context.aiProvider,
          userId: context.userId,
          approvalRequired: context.approvalRequired,
          approvedBy: context.approvedBy,
        },
        signatures: commit.signatures || [],
      },
      metadata: {
        schemaVersion: '1.0.0',
        correlationId,
        workflowId: context.workflowId,
        source: this.getSource(),
        mode: this.getMode(),
        repositoryId: context.repositoryId,
      },
    };

    // Persist event
    await this.eventStore.append(event);

    return event.eventId;
  }

  private detectLanguage(filePath: string): string {
    const extension = filePath.split('.').pop()?.toLowerCase();
    const languageMap: Record<string, string> = {
      ts: 'typescript',
      js: 'javascript',
      py: 'python',
      java: 'java',
      cpp: 'cpp',
      c: 'c',
      go: 'go',
      rs: 'rust',
      php: 'php',
      rb: 'ruby',
      cs: 'csharp',
      swift: 'swift',
      kt: 'kotlin',
      scala: 'scala',
      sh: 'shell',
      yaml: 'yaml',
      yml: 'yaml',
      json: 'json',
      xml: 'xml',
      md: 'markdown',
      sql: 'sql',
      html: 'html',
      css: 'css',
      scss: 'scss',
      less: 'less',
    };

    return languageMap[extension || ''] || 'unknown';
  }
}
```

## Implementation Tasks

### 1. Event Schema Implementation

- [ ] Create code change and Git operation event schemas
- [ ] Implement TypeScript interfaces for all event payloads
- [ ] Add JSON schema validation for all event types
- [ ] Create event builders with proper validation

### 2. Diff Storage System

- [ ] Implement `DiffStorage` for file diffs and commit diffs
- [ ] Add efficient diff calculation and storage
- [ ] Create diff retrieval and comparison capabilities
- [ ] Implement storage cleanup and retention policies

### 3. Event Capture Service

- [ ] Implement `CodeEventCapture` class
- [ ] Add integration with file system operations
- [ ] Add integration with Git platform operations
- [ ] Implement language detection and file analysis

### 4. Git Platform Integration

- [ ] Integrate event capture into Git platform abstraction
- [ ] Ensure events are captured for all Git operations
- [ ] Add support for different Git providers
- [ ] Implement proper attribution tracking

### 5. Testing

- [ ] Unit tests for event capture service
- [ ] Integration tests with file system operations
- [ ] Integration tests with Git platform operations
- [ ] Diff storage and retrieval tests

## Dependencies

### Internal Dependencies

- `@tamma/events` - Event schemas and validation
- `@tamma/platforms` - Git platform abstraction
- `@tamma/workflow` - Autonomous workflow integration
- Story 4.2 - Event Store Backend (for persistence)

### External Dependencies

- Git operations (libgit2, git CLI, or Git APIs)
- File system operations
- Diff calculation libraries

## Success Metrics

- 100% of file writes captured as events
- 100% of Git operations captured as events
- Complete diff storage for all changes
- Event capture adds <100ms overhead to file operations
- Accurate attribution of all changes (AI vs user vs system)

## Risks and Mitigations

### Performance Risks

- **Risk**: Event capture may slow down file operations
- **Mitigation**: Async diff storage, optimized event serialization

### Storage Risks

- **Risk**: Diff storage may become expensive
- **Mitigation**: Compression, retention policies, selective storage

### Integration Risks

- **Risk**: Complex Git operations may be difficult to integrate
- **Mitigation**: Clear integration points, comprehensive testing

### Attribution Risks

- **Risk**: Change attribution may be inaccurate
- **Mitigation**: Clear attribution logic, audit trails, validation

## Notes

This story is critical for complete code change visibility. Every file write and Git operation must be captured with full context to provide complete auditability of the development process. The diff storage enables detailed code review and change analysis while keeping event sizes manageable.

The attribution tracking is essential for understanding which changes were made by AI versus humans, supporting both compliance requirements and development workflow optimization.
