# Bug: [Bug Name]

**Date Discovered**: YYYY-MM-DD
**Reporter**: [Your Name / AI Agent Name]
**Severity**: 🔴 Critical | 🟠 High | 🟡 Medium | 🟢 Low
**Status**: 🐛 Open | 🔍 Investigating | 🔧 In Progress | ✅ Resolved | 🚫 Won't Fix

## 📋 Summary

Brief description of the bug.

## 🔍 Details

### Affected Components
- Package: `@tamma/package-name`
- File: `packages/package-name/src/file.ts:123`
- Function: `functionName()`

### Environment
- Node.js Version: v22.x.x
- OS: Linux / macOS / Windows
- Package Version: 0.1.0

### Reproducibility
- [ ] Always reproducible
- [ ] Intermittent (X% of the time)
- [ ] Race condition
- [ ] Environment specific

## 🔬 Steps to Reproduce

```bash
# Step-by-step instructions
pnpm install
pnpm build
pnpm test:integration
# Bug occurs here
```

### Minimal Reproduction Code

```typescript
// Minimal code that reproduces the issue
import { functionName } from '@tamma/package';

async function reproduce() {
  // Code that triggers bug
  const result = await functionName();
  // Expected: X
  // Actual: Y
}
```

## 💥 Expected Behavior

What should happen?

## 🐛 Actual Behavior

What actually happens?

## 📊 Error Messages / Stack Trace

```
Error: Something went wrong
  at functionName (packages/package/src/file.ts:123:45)
  at async otherFunction (packages/package/src/other.ts:67:89)
```

## 📸 Screenshots / Logs

```json
{
  "level": "error",
  "time": 1698483296789,
  "service": "orchestrator",
  "msg": "Failed to process task",
  "error": {
    "message": "Connection timeout",
    "code": "ETIMEDOUT"
  }
}
```

## 🔍 Root Cause Analysis

### Investigation Findings
- Finding 1
- Finding 2

### Root Cause
What caused the bug?

### Code Location
```typescript
// Problematic code
async function buggyFunction() {
  // This line causes the issue
  const result = await someOperation(); // No error handling!
  return result;
}
```

## 🔧 Proposed Solution

### Approach 1: [Name]
**Description**: How to fix it

**Pros**:
- Pro 1
- Pro 2

**Cons**:
- Con 1
- Con 2

### Approach 2: [Name]
**Description**: Alternative fix

**Pros**:
- Pro 1

**Cons**:
- Con 1

### Recommended Solution
Which approach and why?

## ✅ Fix Implementation

```typescript
// Fixed code
async function fixedFunction(): Promise<Result> {
  try {
    const result = await someOperation();
    if (!result) {
      throw new TammaError('OPERATION_FAILED', 'Operation returned null', {
        operation: 'someOperation'
      });
    }
    return result;
  } catch (error) {
    logger.error('Operation failed', { error });
    throw error;
  }
}
```

## 🧪 Tests Added

```typescript
describe('buggyFunction', () => {
  it('should handle null result', async () => {
    // Test that verifies fix
    const result = await fixedFunction();
    expect(result).toBeDefined();
  });

  it('should throw TammaError on failure', async () => {
    // Test error handling
    await expect(fixedFunction()).rejects.toThrow(TammaError);
  });
});
```

## ⚠️ Impact Assessment

### Who is affected?
- All users
- Specific use case
- Specific environment

### Workaround (if any)
```typescript
// Temporary workaround
const result = await buggyFunction() || defaultValue;
```

## 🔗 Related

- Related bug: `.dev/bugs/YYYY-MM-DD-related-bug.md`
- Related spike: `.dev/spikes/YYYY-MM-DD-spike.md`
- GitHub Issue: #123
- Pull Request: #456
- Story: `docs/stories/1-1-story-name.md`

## 📚 References

- [Documentation](https://example.com)
- [Stack Overflow](https://stackoverflow.com)

## ✅ Resolution

**Resolution Date**: YYYY-MM-DD
**Resolution**: How the bug was fixed

**Verification**:
- [ ] Unit tests pass
- [ ] Integration tests pass
- [ ] Manual testing confirms fix
- [ ] Regression tests added
- [ ] Documentation updated

## 📝 Lessons Learned

What did we learn from this bug?
- Lesson 1
- Lesson 2

**Prevention**:
How to prevent similar bugs in the future?

---

**Time to Resolve**: X hours/days
**Fixed in Version**: 0.1.1
