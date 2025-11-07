#!/usr/bin/env tsx

import fs from 'fs/promises';
import path from 'path';

interface StoryConfig {
  storyId: string;
  epic: string;
  title?: string;
  description?: string;
  acceptanceCriteria?: string;
}

interface StoryTemplate {
  title: string;
  description: string;
  acceptanceCriteria: string[];
  tasks: Task[];
}

interface Task {
  title: string;
  subtasks: string[];
}

// Story templates for different story types
const storyTemplates: Record<string, Partial<StoryTemplate>> = {
  '3-3-contamination-prevention-system': {
    title: 'Contamination Prevention System',
    description:
      'As a **benchmark maintainer**, I want to **prevent AI models from training on our benchmark tasks through comprehensive contamination prevention measures**, so that **benchmark results reflect genuine AI capability rather than memorization of training data**.',
    acceptanceCriteria: [
      'Private Test Suite Separation: Implement strict separation between public task descriptions and private test cases with encrypted storage and access controls',
      'Task Refreshment System: Automated periodic generation of task variations with semantic preservation to prevent pattern memorization',
      'Task Obfuscation Techniques: Advanced obfuscation methods including variable renaming, structure randomization, and semantic preservation',
      'Training Data Monitoring: Continuous monitoring of public repositories, training datasets, and model outputs for task content leakage',
      'Canary Task Deployment: Special canary tasks embedded in benchmarks to detect contamination and model memorization patterns',
      'Version Isolation Enforcement: Strict isolation between task versions with cross-contamination prevention and access logging',
      'Comprehensive Access Logging: Complete audit trail of all task access, exposure, and distribution with tamper-proof logging',
      'Automated Contamination Detection: AI-powered detection system with real-time alerts and contamination scoring',
    ],
    tasks: [
      {
        title: 'Private Test Suite Architecture',
        subtasks: [
          'Design encrypted storage system for private test cases with AES-256 encryption',
          'Implement role-based access control for test suite access with audit logging',
          'Create public/private task separation with secure API endpoints',
          'Build test suite versioning with secure distribution mechanisms',
        ],
      },
      {
        title: 'Task Refreshment Engine',
        subtasks: [
          'Develop semantic variation generation algorithms for task refreshment',
          'Implement automated scheduling system for periodic task updates',
          'Create difficulty preservation validation for refreshed tasks',
          'Build task variation tracking and lineage management system',
        ],
      },
      {
        title: 'Advanced Obfuscation System',
        subtasks: [
          'Implement variable and function name randomization with semantic preservation',
          'Create code structure randomization algorithms maintaining functionality',
          'Develop comment and documentation obfuscation techniques',
          'Build obfuscation validation system to ensure task integrity',
        ],
      },
      {
        title: 'Training Data Monitoring',
        subtasks: [
          'Implement GitHub repository scanning for task content leakage',
          'Create training dataset analysis integration with major data providers',
          'Build model output monitoring for task memorization detection',
          'Develop real-time contamination alerting system',
        ],
      },
      {
        title: 'Canary Task System',
        subtasks: [
          'Design canary task generation with unique identifiers',
          'Implement canary task embedding in benchmark workflows',
          'Create canary result analysis for contamination detection',
          'Build canary task rotation and replacement system',
        ],
      },
      {
        title: 'Version Isolation Framework',
        subtasks: [
          'Implement strict version separation with access controls',
          'Create cross-version contamination prevention mechanisms',
          'Build version access logging and monitoring',
          'Develop version retirement and archival procedures',
        ],
      },
      {
        title: 'Comprehensive Access Logging',
        subtasks: [
          'Implement tamper-proof logging system for all task access',
          'Create detailed access pattern analysis and anomaly detection',
          'Build access audit trail with immutable storage',
          'Develop access reporting and compliance documentation',
        ],
      },
      {
        title: 'Automated Contamination Detection',
        subtasks: [
          'Integrate AI models for contamination pattern recognition',
          'Create contamination scoring algorithms with confidence metrics',
          'Implement real-time alerting with escalation procedures',
          'Build contamination response and mitigation workflows',
        ],
      },
    ],
  },
};

function parseArgs(): StoryConfig {
  const args = process.argv.slice(2);
  const config: StoryConfig = {
    storyId: '',
    epic: '3',
  };

  for (let i = 0; i < args.length; i += 2) {
    const flag = args[i];
    const value = args[i + 1];

    switch (flag) {
      case '--story-id':
        config.storyId = value;
        break;
      case '--epic':
        config.epic = value;
        break;
      case '--title':
        config.title = value;
        break;
      case '--description':
        config.description = value;
        break;
      case '--acceptance-criteria':
        config.acceptanceCriteria = value;
        break;
    }
  }

  if (!config.storyId) {
    console.error('❌ Error: --story-id is required');
    process.exit(1);
  }

  return config;
}

function generateTitleFromId(storyId: string): string {
  return storyId
    .split('-')
    .slice(1)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(' ');
}

function generateStoryContent(config: StoryConfig): string {
  const template = storyTemplates[config.storyId] || {};

  const title = config.title || template.title || generateTitleFromId(config.storyId);
  const description =
    config.description ||
    template.description ||
    `As a **user**, I want to **implement the required functionality**, so that **I can achieve the desired outcome efficiently and effectively**.`;

  let acceptanceCriteria: string[];
  if (config.acceptanceCriteria) {
    try {
      acceptanceCriteria = JSON.parse(config.acceptanceCriteria);
    } catch {
      acceptanceCriteria = [config.acceptanceCriteria];
    }
  } else {
    acceptanceCriteria = template.acceptanceCriteria || [
      'Core functionality implemented according to specifications',
      'Comprehensive test coverage with unit and integration tests',
      'Error handling and edge case management',
      'Performance requirements met and documented',
      'Security best practices implemented',
      'Documentation complete and up to date',
    ];
  }

  const tasks = template.tasks || [
    {
      title: 'Core Implementation',
      subtasks: [
        'Implement primary functionality',
        'Add error handling and validation',
        'Create necessary data models',
        'Implement business logic',
      ],
    },
    {
      title: 'Integration',
      subtasks: [
        'Connect with existing systems',
        'Implement API endpoints',
        'Add database integration',
        'Configure external services',
      ],
    },
    {
      title: 'Testing',
      subtasks: [
        'Write unit tests',
        'Create integration tests',
        'Add performance tests',
        'Implement security tests',
      ],
    },
    {
      title: 'Documentation',
      subtasks: [
        'Update API documentation',
        'Create user guides',
        'Document technical decisions',
        'Add deployment instructions',
      ],
    },
  ];

  return `# Story ${config.storyId.replace('-', '.')}: ${title}

Status: ready-for-dev

## ⚠️ MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

📖 **[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)**

This mandatory guide includes:

- 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling)
- Knowledge Base Usage (.dev/ directory: spikes, bugs, findings, decisions)
- TRACE/DEBUG Logging Requirements for all functions
- Test-Driven Development (TDD) mandatory workflow
- 100% Test Coverage requirement
- Build Success enforcement
- Automatic retry and developer alert procedures

**Failure to follow this process will result in rework.**

## Story

${description}

## Acceptance Criteria

${acceptanceCriteria.map((criteria, index) => `${index + 1}. **${criteria}**`).join('\n')}

## Tasks / Subtasks

${tasks
  .map(
    (task, taskIndex) => `- [ ] Task ${taskIndex + 1}: ${task.title}
${task.subtasks.map((subtask, subtaskIndex) => `  - [ ] Subtask ${taskIndex + 1}.${subtaskIndex + 1}: ${subtask}`).join('\n')}`
  )
  .join('\n')}

## Dev Notes

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**

1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched \`.dev/\` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in \`docs/\` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)

### Requirements Context Summary

**Epic Context:** This story is part of Epic ${config.epic} and implements critical functionality for the test platform. The story delivers specific value while building on previous work and enabling future capabilities.

**Technical Context:** The implementation must integrate with existing systems and follow established patterns while delivering the specified functionality.

**Integration Points:**

- Previous stories in Epic ${config.epic} for foundational functionality
- Database schema from Story 1.1 for data persistence
- Authentication system from Story 1.2 for security
- API infrastructure from Story 1.4 for service exposure

### Implementation Guidance

**Key Design Decisions:**

- Follow established architectural patterns from previous stories
- Implement comprehensive error handling and logging
- Ensure scalability and performance requirements are met
- Maintain security best practices throughout implementation

**Technical Specifications:**

**Core Interface:**

\`\`\`typescript
interface StoryInterface {
  // Define core interface based on story requirements
  id: string;
  status: string;
  createdAt: string;
  updatedAt: string;
}
\`\`\`

**Implementation Pipeline:**

1. **Setup**: Initialize project structure and dependencies
2. **Core Logic**: Implement primary functionality
3. **Integration**: Connect with existing systems
4. **Testing**: Comprehensive test coverage
5. **Documentation**: Update technical documentation
6. **Deployment**: Prepare for production deployment

**Configuration Requirements:**

- Environment-specific configuration management
- Feature flags for gradual rollout
- Monitoring and alerting configuration
- Security and access control settings

**Performance Considerations:**

- Efficient data processing and storage
- Optimized query performance
- Scalable architecture for growth
- Resource management and cleanup

**Security Requirements:**

- Input validation and sanitization
- Authentication and authorization
- Data encryption at rest and in transit
- Audit logging and compliance

### Testing Strategy

**Unit Test Requirements:**

- Core functionality testing with edge cases
- Error handling and validation testing
- Performance and load testing
- Security testing and vulnerability assessment

**Integration Test Requirements:**

- End-to-end workflow testing
- API integration testing
- Database integration testing
- Third-party service integration testing

**Performance Test Requirements:**

- Load testing with expected traffic
- Stress testing beyond normal limits
- Scalability testing for growth scenarios
- Resource utilization optimization

**Edge Cases to Consider:**

- Network failures and timeouts
- Data corruption and recovery
- Concurrent access and race conditions
- Resource exhaustion and degradation

### Dependencies

**Internal Dependencies:**

- Previous stories in Epic ${config.epic}
- Database schema and migration system
- Authentication and authorization framework
- API infrastructure and documentation

**External Dependencies:**

- Third-party APIs and services
- Database systems and storage
- Monitoring and logging services
- Security and compliance tools

### Risks and Mitigations

| Risk | Severity | Mitigation |
| ---- | -------- | ---------- |
| Technical complexity | Medium | Incremental development, thorough testing |
| Integration challenges | Medium | Early integration testing, clear interfaces |
| Performance bottlenecks | Low | Performance monitoring, optimization |
| Security vulnerabilities | High | Security reviews, penetration testing |

### Success Metrics

- [ ] Metric 1: Functional completeness - 100% of acceptance criteria met
- [ ] Metric 2: Test coverage - 90%+ code coverage achieved
- [ ] Metric 3: Performance - Meets specified performance requirements
- [ ] Metric 4: Security - Passes security assessment
- [ ] Metric 5: Documentation - Complete technical documentation

## Related

- Related story: \`docs/stories/\` - Previous/next story in epic
- Related epic: \`docs/epics.md#Epic-${config.epic}\` - Epic context
- Related architecture: \`docs/ARCHITECTURE.md\` - Technical specifications

## References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../README.md) - Search spikes, bugs, findings, decisions
- **Epic Specification:** [test-platform/docs/epics.md](epics.md)
- **Architecture Document:** [test-platform/docs/ARCHITECTURE.md](ARCHITECTURE.md)
- **Product Requirements:** [test-platform/docs/PRD.md](PRD.md)
`;
}

async function main() {
  try {
    const config = parseArgs();

    console.log(`🎯 Creating story: ${config.storyId}`);

    const content = generateStoryContent(config);
    const filePath = path.join(
      process.cwd(),
      'test-platform',
      'docs',
      'stories',
      `${config.storyId}.md`
    );

    // Ensure directory exists
    await fs.mkdir(path.dirname(filePath), { recursive: true });

    // Write story file
    await fs.writeFile(filePath, content, 'utf8');

    console.log(`✅ Story created successfully: ${filePath}`);

    // Display summary
    const lines = content.split('\n');
    const titleLine = lines.find((line) => line.startsWith('# Story'));
    const descriptionStart = lines.findIndex((line) => line.includes('As a'));
    const descriptionEnd = lines.findIndex((line) => line.includes('## Acceptance Criteria'));

    if (titleLine) {
      console.log(`\n📋 ${titleLine}`);
    }

    if (descriptionStart >= 0 && descriptionEnd > descriptionStart) {
      const description = lines.slice(descriptionStart, descriptionEnd).join('\n');
      console.log(`\n📝 ${description}`);
    }

    const acceptanceCount = content.match(/^\d+\./gm)?.length || 0;
    const taskCount = content.match(/^- \[ \]/gm)?.length || 0;

    console.log(`\n📊 Summary:`);
    console.log(`   Acceptance Criteria: ${acceptanceCount}`);
    console.log(`   Tasks: ${taskCount}`);
  } catch (error) {
    console.error('❌ Error creating story:', error);
    process.exit(1);
  }
}

if (import.meta.url === `file://${process.argv[1]}`) {
  main();
}
