# Story 4.5: Event Capture - Code Changes & Git Operations

Status: ready-for-dev

## Story

As a **code reviewer**,
I want all code changes and Git operations captured as events,
so that I can see the complete evolution of code during autonomous development.

## Acceptance Criteria

1. `CodeFileWrittenEvent` captured for each file write including: file path, file size, change type (create/update/delete)
2. `CommitCreatedEvent` captured for each commit including: commit SHA, message, branch name, file count
3. `BranchCreatedEvent` captured when branch created (Story 2.4)
4. `PRCreatedEvent` captured when PR created (Story 2.8) including: PR number, URL, base/head branches
5. `PRMergedEvent` captured when PR merged (Story 2.10) including: merge strategy, merge SHA
6. Events include file diffs stored in blob storage (linked from event)
7. Events capture who triggered action (user approval vs autonomous decision)

## Tasks / Subtasks

- [ ] Task 1: Implement code file operation event capture (AC: 1)
  - [ ] Subtask 1.1: Create CodeFileWrittenEvent schema and payload structure
  - [ ] Subtask 1.2: Integrate event capture into file system operations
  - [ ] Subtask 1.3: Add file change detection and classification
  - [ ] Subtask 1.4: Implement file diff generation and storage
  - [ ] Subtask 1.5: Add file operation event capture unit tests

- [ ] Task 2: Implement Git commit event capture (AC: 2)
  - [ ] Subtask 2.1: Create CommitCreatedEvent schema and payload structure
  - [ ] Subtask 2.2: Integrate event capture into Git operations
  - [ ] Subtask 2.3: Add commit metadata extraction (author, timestamp, message)
  - [ ] Subtask 2.4: Implement commit file list and statistics
  - [ ] Subtask 2.5: Add commit event capture unit tests

- [ ] Task 3: Implement Git branch event capture (AC: 3)
  - [ ] Subtask 3.1: Create BranchCreatedEvent schema and payload structure
  - [ ] Subtask 3.2: Integrate event capture into branch creation (Story 2.4)
  - [ ] Subtask 3.3: Add branch metadata and tracking
  - [ ] Subtask 3.4: Implement branch deletion events (optional)
  - [ ] Subtask 3.5: Add branch event capture unit tests

- [ ] Task 4: Implement pull request event capture (AC: 4, 5)
  - [ ] Subtask 4.1: Create PRCreatedEvent and PRMergedEvent schemas
  - [ ] Subtask 4.2: Integrate event capture into PR creation (Story 2.8)
  - [ ] Subtask 4.3: Integrate event capture into PR merge (Story 2.10)
  - [ ] Subtask 4.4: Add PR metadata extraction (number, URL, branches)
  - [ ] Subtask 4.5: Add PR event capture unit tests

- [ ] Task 5: Implement file diff blob storage (AC: 6)
  - [ ] Subtask 5.1: Design diff storage format and organization
  - [ ] Subtask 5.2: Implement diff generation for file changes
  - [ ] Subtask 5.3: Create blob storage integration for large diffs
  - [ ] Subtask 5.4: Add diff compression and optimization
  - [ ] Subtask 5.5: Create diff storage cleanup and retention

- [ ] Task 6: Implement actor attribution (AC: 7)
  - [ ] Subtask 6.1: Create actor detection service (user vs system)
  - [ ] Subtask 6.2: Implement approval tracking and attribution
  - [ ] Subtask 6.3: Add autonomous decision tracking
  - [ ] Subtask 6.4: Create actor context and metadata capture
  - [ ] Subtask 6.5: Add actor attribution validation tests

## Dev Notes

### Requirements Context Summary

**Epic 4 Integration:** This story captures all code-related events to provide a complete audit trail of autonomous development activities. These events enable code review, compliance auditing, and debugging of autonomous code generation.

**Code Review Requirements:** Events must capture sufficient detail to enable code review without requiring access to the actual repository. This includes file changes, diffs, commit messages, and PR metadata.

**Autonomous Attribution:** Events must distinguish between autonomous actions and user-approved actions to provide transparency about what the system did versus what humans approved.

### Implementation Guidance

**Event Schema Definitions:**

```typescript
interface CodeFileWrittenEvent extends BaseEvent {
  eventType: 'CODE.FILE.CREATED' | 'CODE.FILE.UPDATED' | 'CODE.FILE.DELETED';
  payload: {
    file: {
      path: string; // Relative path from repository root
      absolutePath: string; // Absolute file system path
      size: number; // File size in bytes
      hash: string; // SHA-256 hash of file content
      encoding: 'utf-8' | 'binary';
      language?: string; // Detected programming language
    };
    change: {
      type: 'create' | 'update' | 'delete';
      linesAdded: number; // Lines added (0 for delete)
      linesRemoved: number; // Lines removed (0 for create)
      linesChanged: number; // Total lines changed
    };
    content: {
      preview: string; // First 500 characters of content
      length: number; // Full content length
      blobKey?: string; // Key for full content in blob storage
    };
    diff?: {
      blobKey: string; // Key for diff in blob storage
      format: 'unified' | 'json';
      size: number; // Diff size in bytes
    };
    attribution: {
      actor: 'system' | 'user' | 'ai';
      actorId: string; // User ID or system component
      reason: string; // Why this change was made
      approved: boolean; // Whether change was user-approved
      approvedBy?: string; // User who approved (if applicable)
    };
    context: {
      correlationId: string; // Workflow correlation
      stepId: string; // Current workflow step
      issueId?: string; // Related issue
      prId?: string; // Related PR
      commitSha?: string; // Related commit
    };
  };
}

interface CommitCreatedEvent extends BaseEvent {
  eventType: 'CODE.COMMIT.CREATED';
  payload: {
    commit: {
      sha: string; // Full commit SHA
      shortSha: string; // Short SHA (first 8 chars)
      message: string; // Commit message
      author: {
        name: string;
        email: string;
        username?: string; // Git username
      };
      committer: {
        name: string;
        email: string;
        username?: string;
      };
      timestamp: string; // ISO 8601 timestamp
      parents: string[]; // Parent commit SHAs
    };
    branch: {
      name: string; // Branch name
      isMain: boolean; // Is main/master branch
      isProtected: boolean; // Is protected branch
    };
    files: Array<{
      path: string;
      changeType: 'added' | 'modified' | 'deleted' | 'renamed';
      additions: number;
      deletions: number;
      blobKey?: string; // Key for file diff in blob storage
    }>;
    statistics: {
      totalFiles: number;
      totalAdditions: number;
      totalDeletions: number;
      totalChanges: number;
    };
    attribution: {
      actor: 'system' | 'user' | 'ai';
      actorId: string;
      reason: string;
      approved: boolean;
      approvedBy?: string;
    };
    context: {
      correlationId: string;
      stepId: string;
      issueId?: string;
      prId?: string;
    };
  };
}

interface BranchCreatedEvent extends BaseEvent {
  eventType: 'GIT.BRANCH.CREATED';
  payload: {
    branch: {
      name: string;
      fullName: string; // refs/heads/feature-name
      isMain: boolean;
      isProtected: boolean;
    };
    source: {
      fromBranch: string; // Source branch
      fromCommit: string; // Source commit SHA
      fromTag?: string; // Source tag if applicable
    };
    purpose: {
      type: 'feature' | 'bugfix' | 'hotfix' | 'release' | 'experimental';
      description: string; // Why branch was created
      issueId?: string; // Related issue number
    };
    attribution: {
      actor: 'system' | 'user' | 'ai';
      actorId: string;
      reason: string;
    };
    context: {
      correlationId: string;
      stepId: string;
      issueId?: string;
    };
  };
}

interface PRCreatedEvent extends BaseEvent {
  eventType: 'GIT.PULL_REQUEST.CREATED';
  payload: {
    pullRequest: {
      number: number;
      title: string;
      description: string;
      url: string;
      state: 'open' | 'closed' | 'merged';
      isDraft: boolean;
      isMergeable: boolean;
    };
    branches: {
      base: {
        name: string;
        sha: string;
        repository: {
          name: string;
          owner: string;
          url: string;
        };
      };
      head: {
        name: string;
        sha: string;
        repository: {
          name: string;
          owner: string;
          url: string;
        };
      };
    };
    metadata: {
      createdAt: string;
      updatedAt: string;
      mergeable?: boolean;
      conflicts: boolean;
      additions: number;
      deletions: number;
      changedFiles: number;
      commits: number;
    };
    attribution: {
      actor: 'system' | 'user' | 'ai';
      actorId: string;
      reason: string;
      approved: boolean;
      approvedBy?: string;
    };
    context: {
      correlationId: string;
      stepId: string;
      issueId?: string;
    };
  };
}

interface PRMergedEvent extends BaseEvent {
  eventType: 'GIT.PULL_REQUEST.MERGED';
  payload: {
    pullRequest: {
      number: number;
      title: string;
      url: string;
    };
    merge: {
      sha: string; // Merge commit SHA
      strategy: 'merge' | 'squash' | 'rebase';
      message: string; // Merge commit message
      timestamp: string; // Merge timestamp
    };
    branches: {
      target: {
        name: string;
        sha: string;
      };
      source: {
        name: string;
        sha: string;
        deleted: boolean; // Whether source branch was deleted
      };
    };
    metadata: {
      mergedBy: {
        name: string;
        username?: string;
        type: 'user' | 'system' | 'ai';
      };
      mergeDuration: number; // Time from PR creation to merge (minutes)
      reviewCount: number; // Number of reviews
      approvalCount: number; // Number of approvals
    };
    attribution: {
      actor: 'system' | 'user' | 'ai';
      actorId: string;
      reason: string;
      approved: boolean;
      approvedBy?: string;
    };
    context: {
      correlationId: string;
      stepId: string;
      issueId?: string;
    };
  };
}
```

**File System Integration:**

```typescript
class EventCapturingFileSystem {
  constructor(
    private eventStore: IEventStore,
    private blobStorage: IBlobStorage,
    private correlationManager: CorrelationManager,
    private actorDetector: ActorDetector
  ) {}

  async writeFile(
    filePath: string,
    content: string | Buffer,
    options?: WriteOptions
  ): Promise<void> {
    const beforeState = await this.getFileState(filePath);
    const changeType = this.determineChangeType(beforeState, content);

    // Write the file
    await fs.writeFile(filePath, content, options);

    const afterState = await this.getFileState(filePath);
    const attribution = await this.actorDetector.getCurrentAttribution();

    // Generate diff if file was updated
    let diffBlobKey: string | undefined;
    if (changeType === 'update' && beforeState.exists) {
      const diff = await this.generateDiff(beforeState.content, content);
      diffBlobKey = await this.storeDiffInBlob(filePath, diff);
    }

    // Store full content in blob if large
    let contentBlobKey: string | undefined;
    const contentStr = content.toString();
    if (contentStr.length > 1000) {
      contentBlobKey = await this.storeContentInBlob(filePath, contentStr);
    }

    // Create and capture event
    const event: CodeFileWrittenEvent = {
      eventId: generateEventId(),
      timestamp: new Date().toISOString(),
      eventType: `CODE.FILE.${changeType.toUpperCase()}` as any,
      actorType: attribution.actor,
      actorId: attribution.actorId,
      correlationId: this.correlationManager.getCurrentCorrelationId(),
      schemaVersion: '1.0.0',
      payload: {
        file: {
          path: path.relative(process.cwd(), filePath),
          absolutePath: filePath,
          size: afterState.size,
          hash: afterState.hash,
          encoding: this.detectEncoding(content),
          language: this.detectLanguage(filePath),
        },
        change: {
          type: changeType,
          linesAdded: this.countLinesAdded(beforeState.content, contentStr),
          linesRemoved: this.countLinesRemoved(beforeState.content, contentStr),
          linesChanged: this.countLinesChanged(beforeState.content, contentStr),
        },
        content: {
          preview: contentStr.substring(0, 500),
          length: contentStr.length,
          blobKey: contentBlobKey,
        },
        diff: diffBlobKey
          ? {
              blobKey: diffBlobKey,
              format: 'unified',
              size: (await this.blobStorage.getMetadata(diffBlobKey))?.size || 0,
            }
          : undefined,
        attribution: {
          actor: attribution.actor,
          actorId: attribution.actorId,
          reason: attribution.reason,
          approved: attribution.approved,
          approvedBy: attribution.approvedBy,
        },
        context: {
          correlationId: this.correlationManager.getCurrentCorrelationId(),
          stepId: this.correlationManager.getCurrentStepId(),
          issueId: this.correlationManager.getCurrentIssueId(),
          prId: this.correlationManager.getCurrentPrId(),
          commitSha: this.correlationManager.getCurrentCommitSha(),
        },
      },
      metadata: {
        source: 'orchestrator',
        version: process.env.TAMMA_VERSION || '1.0.0',
        environment: process.env.NODE_ENV || 'development',
      },
    };

    await this.eventStore.append(event);
  }

  private async generateDiff(oldContent: string, newContent: string): Promise<string> {
    const diff = require('diff');
    const patches = diff.createPatch('file', oldContent, newContent);
    return patches;
  }

  private async storeDiffInBlob(filePath: string, diff: string): Promise<string> {
    const key = `diffs/${this.correlationManager.getCurrentCorrelationId()}/${path.basename(filePath)}.diff`;
    await this.blobStorage.store(key, diff, {
      contentType: 'text/plain',
      compression: 'gzip',
      retentionDays: 90,
    });
    return key;
  }
}
```

**Git Operations Integration:**

```typescript
class EventCapturingGitOperations {
  constructor(
    private eventStore: IEventStore,
    private gitPlatform: IGitPlatform,
    private correlationManager: CorrelationManager,
    private actorDetector: ActorDetector
  ) {}

  async createBranch(
    branchName: string,
    sourceBranch: string,
    purpose?: BranchPurpose
  ): Promise<void> {
    const attribution = await this.actorDetector.getCurrentAttribution();

    // Create the branch
    await this.gitPlatform.createBranch(branchName, sourceBranch);

    // Capture event
    const event: BranchCreatedEvent = {
      eventId: generateEventId(),
      timestamp: new Date().toISOString(),
      eventType: 'GIT.BRANCH.CREATED',
      actorType: attribution.actor,
      actorId: attribution.actorId,
      correlationId: this.correlationManager.getCurrentCorrelationId(),
      schemaVersion: '1.0.0',
      payload: {
        branch: {
          name: branchName,
          fullName: `refs/heads/${branchName}`,
          isMain: branchName === 'main' || branchName === 'master',
          isProtected: await this.gitPlatform.isBranchProtected(branchName),
        },
        source: {
          fromBranch: sourceBranch,
          fromCommit: await this.gitPlatform.getBranchCommit(sourceBranch),
          fromTag: undefined,
        },
        purpose: {
          type: purpose?.type || 'feature',
          description: purpose?.description || `Created branch for development`,
          issueId: purpose?.issueId,
        },
        attribution: {
          actor: attribution.actor,
          actorId: attribution.actorId,
          reason: attribution.reason,
        },
        context: {
          correlationId: this.correlationManager.getCurrentCorrelationId(),
          stepId: this.correlationManager.getCurrentStepId(),
          issueId: this.correlationManager.getCurrentIssueId(),
        },
      },
      metadata: {
        source: 'orchestrator',
        version: process.env.TAMMA_VERSION || '1.0.0',
        environment: process.env.NODE_ENV || 'development',
      },
    };

    await this.eventStore.append(event);
  }

  async createCommit(files: string[], message: string): Promise<string> {
    const attribution = await this.actorDetector.getCurrentAttribution();

    // Stage and commit files
    await this.gitPlatform.stageFiles(files);
    const commitSha = await this.gitPlatform.commit(message);

    // Get commit details
    const commitDetails = await this.gitPlatform.getCommit(commitSha);
    const changedFiles = await this.gitPlatform.getCommitFiles(commitSha);

    // Store file diffs in blob storage
    const filesWithDiffs = await Promise.all(
      changedFiles.map(async (file) => {
        const diff = await this.gitPlatform.getFileDiff(commitSha, file.path);
        const diffBlobKey = await this.storeDiffInBlob(file.path, diff);
        return {
          ...file,
          blobKey: diffBlobKey,
        };
      })
    );

    // Capture event
    const event: CommitCreatedEvent = {
      eventId: generateEventId(),
      timestamp: new Date().toISOString(),
      eventType: 'CODE.COMMIT.CREATED',
      actorType: attribution.actor,
      actorId: attribution.actorId,
      correlationId: this.correlationManager.getCurrentCorrelationId(),
      schemaVersion: '1.0.0',
      payload: {
        commit: {
          sha: commitSha,
          shortSha: commitSha.substring(0, 8),
          message,
          author: commitDetails.author,
          committer: commitDetails.committer,
          timestamp: commitDetails.timestamp,
          parents: commitDetails.parents,
        },
        branch: {
          name: await this.gitPlatform.getCurrentBranch(),
          isMain: false,
          isProtected: false,
        },
        files: filesWithDiffs,
        statistics: {
          totalFiles: filesWithDiffs.length,
          totalAdditions: filesWithDiffs.reduce((sum, f) => sum + f.additions, 0),
          totalDeletions: filesWithDiffs.reduce((sum, f) => sum + f.deletions, 0),
          totalChanges: filesWithDiffs.reduce((sum, f) => sum + f.additions + f.deletions, 0),
        },
        attribution: {
          actor: attribution.actor,
          actorId: attribution.actorId,
          reason: attribution.reason,
          approved: attribution.approved,
          approvedBy: attribution.approvedBy,
        },
        context: {
          correlationId: this.correlationManager.getCurrentCorrelationId(),
          stepId: this.correlationManager.getCurrentStepId(),
          issueId: this.correlationManager.getCurrentIssueId(),
          prId: this.correlationManager.getCurrentPrId(),
        },
      },
      metadata: {
        source: 'orchestrator',
        version: process.env.TAMMA_VERSION || '1.0.0',
        environment: process.env.NODE_ENV || 'development',
      },
    };

    await this.eventStore.append(event);
    return commitSha;
  }
}
```

### Technical Specifications

**Performance Requirements:**

- File operation event capture: <100ms per file
- Git operation event capture: <500ms per operation
- Diff generation: <1s for typical files (<1000 lines)
- Blob storage latency: <200ms for typical diffs

**Storage Requirements:**

- Event storage: <1KB per file operation event
- Diff storage: Compressed to <50% of original size
- Retention policy: 90 days for diffs, infinite for events
- Cleanup automation: Automated retention enforcement

**Security Requirements:**

- File content masking: Sensitive patterns detected and masked
- Access control: Repository-based permissions
- Content validation: File size and type limits
- Audit trail: All file operations logged

**Integration Requirements:**

- Git platform integration: GitHub, GitLab, and others
- File system integration: Transparent file operation capture
- Actor detection: User vs system attribution
- Correlation tracking: Workflow-level event linking

### Dependencies

**Internal Dependencies:**

- Story 4.1: Event schema design (provides event structures)
- Story 4.2: Event store backend selection (provides storage)
- Story 2.4: Git branch creation (integration point)
- Story 2.8: Pull request creation (integration point)
- Story 2.10: PR merge with completion checkpoint (integration point)

**External Dependencies:**

- Git client library
- File system monitoring library
- Diff generation library
- Blob storage service

### Risks and Mitigations

| Risk                        | Severity | Mitigation                         |
| --------------------------- | -------- | ---------------------------------- |
| Large file diff performance | Medium   | Diff size limits, async processing |
| File system overhead        | Medium   | Efficient event batching           |
| Git operation conflicts     | Low      | Proper error handling and retry    |
| Actor attribution errors    | Low      | Robust detection and fallback      |

### Success Metrics

- [ ] Event capture success rate: >99.9%
- [ ] File operation overhead: <100ms per file
- [ ] Git operation overhead: <500ms per operation
- [ ] Diff storage efficiency: >50% compression
- [ ] Actor attribution accuracy: >99%

## Related

- Related story: `docs/stories/4-1-event-schema-design.md`
- Related story: `docs/stories/4-2-event-store-backend-selection.md`
- Related story: `docs/stories/2-4-git-branch-creation.md`
- Related story: `docs/stories/2-8-pull-request-creation.md`
- Related story: `docs/stories/2-10-pr-merge-with-completion-checkpoint.md`
- Technical specification: `docs/tech-spec-epic-4.md`

## References

- [Git Diff Format](https://git-scm.com/docs/diff-format)
- [File System Monitoring](https://nodejs.org/api/fs.html)
- [Event Sourcing for Code Changes](https://martinfowler.com/eaaDev/EventSourcing.html)
- [Blob Storage Patterns](https://docs.aws.amazon.com/AmazonS3/latest/dev/UsingObjects.html)
