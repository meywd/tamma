# Finding: [Finding Name]

**Date**: YYYY-MM-DD
**Author**: [Your Name / AI Agent Name]
**Type**: ⚠️ Pitfall | 🚨 Known Issue | 📚 Lesson Learned | 🔍 Best Practice | ⚡ Performance Note
**Category**: [Architecture | Performance | Security | DX | Integration | Testing]

## 📋 Summary

Brief description of the finding.

## 🔍 Context

What were you working on when you discovered this?

### Related Components
- Package: `@tamma/package-name`
- Files: `packages/package-name/src/file.ts`
- Epic/Story: Epic X, Story Y

## 💡 The Finding

### What Did You Discover?

Detailed explanation of what you found.

### Why Does This Matter?

Impact and importance of this finding.

## 📊 Details

### The Problem (if applicable)

```typescript
// Code that demonstrates the issue
async function problematicPattern() {
  // This doesn't work as expected because...
  const result = await operation();
  return result;
}
```

**Why This Happens**:
Technical explanation.

### The Solution / Best Practice

```typescript
// Better approach
async function improvedPattern(): Promise<Result> {
  try {
    const result = await retryWithBackoff(
      () => operation(),
      { maxAttempts: 3, baseDelay: 1000, maxDelay: 5000 }
    );

    logger.info('Operation succeeded', { result });
    await eventStore.append({
      type: 'OPERATION.SUCCESS',
      tags: { operationId: result.id },
      metadata: { workflowVersion: '1.0.0', eventSource: 'system' },
      data: { duration: result.duration }
    });

    return result;
  } catch (error) {
    logger.error('Operation failed after retries', { error });
    throw new TammaError('OPERATION_FAILED', 'Operation failed', {
      error: error.message,
      retryable: false
    });
  }
}
```

**Why This Works Better**:
Explanation of the improvement.

## 🎯 Specific Examples

### Example 1: [Scenario Name]

**Situation**:
When you're doing X...

**What Happens**:
Y occurs...

**Solution**:
Do Z instead...

### Example 2: [Scenario Name]

**Situation**:
When you're doing A...

**What Happens**:
B occurs...

**Solution**:
Do C instead...

## ⚠️ Gotchas & Edge Cases

### Gotcha 1: [Name]
**Description**: What to watch out for
**Example**:
```typescript
// This fails silently
```
**Fix**:
```typescript
// Do this instead
```

### Gotcha 2: [Name]
**Description**: Another edge case
**Example**:
```typescript
// Problematic code
```
**Fix**:
```typescript
// Correct code
```

## 📈 Performance Considerations (if applicable)

### Benchmark Results

```
Approach 1: 150ms (baseline)
Approach 2: 45ms (3.3x faster)
Approach 3: 200ms (1.3x slower)
```

### When to Use Each Approach

- **Approach 1**: When X
- **Approach 2**: When Y (recommended)
- **Approach 3**: Avoid unless Z

## 🔒 Security Implications (if applicable)

### Vulnerability
What security concern does this relate to?

### Mitigation
How to handle it securely?

```typescript
// Secure implementation
async function secureOperation(input: string): Promise<Result> {
  // Validate and sanitize input
  const sanitized = sanitizeInput(input);

  // Use parameterized queries
  const result = await db.query(
    'SELECT * FROM users WHERE id = ?',
    [sanitized]
  );

  // Never log sensitive data
  logger.info('Operation completed', {
    userId: result.id // OK to log
    // password: result.password // NEVER LOG THIS
  });

  return result;
}
```

## ✅ Action Items

**For Current Implementation**:
- [ ] Update existing code to follow best practice
- [ ] Add tests for edge cases
- [ ] Update documentation

**For Future Reference**:
- [ ] Add to onboarding docs
- [ ] Create linting rule (if applicable)
- [ ] Share in team knowledge base

## 🔗 Related

- Related finding: `.dev/findings/YYYY-MM-DD-related-finding.md`
- Related spike: `.dev/spikes/YYYY-MM-DD-spike.md`
- Related bug: `.dev/bugs/YYYY-MM-DD-bug.md`
- Documentation: `docs/architecture.md#section`
- GitHub Issue: #123

## 📚 References

- [Official Documentation](https://example.com)
- [Blog Post](https://example.com/article)
- [GitHub Discussion](https://github.com/project/discussions/123)

## 🎓 Learning Resources

For others who encounter this:
- [Tutorial](https://example.com)
- [Video](https://youtube.com/watch?v=xxx)
- [Book Chapter](https://example.com/book)

## 💬 Discussion

**Questions Raised**:
- Question 1?
- Question 2?

**Answers**:
- Answer 1
- Answer 2

## 📊 Impact Assessment

**Who Benefits**:
- All developers
- Specific use case
- Performance-critical code

**Severity**:
- 🔴 Critical - Must address immediately
- 🟠 High - Should address soon
- 🟡 Medium - Good to know
- 🟢 Low - Nice to have

## ✅ Validation

**Tested In**:
- [ ] Unit tests
- [ ] Integration tests
- [ ] Production environment

**Verified By**:
- [ ] Code review
- [ ] Manual testing
- [ ] Automated testing

---

**Status**: ✅ Validated | 🔍 Needs Review | 📝 Draft
**Last Updated**: YYYY-MM-DD
