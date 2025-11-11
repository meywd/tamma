/**
 * Automated Scoring for Test Generation Scenario
 *
 * Scores: 0-10 based on syntax validity, coverage, assertions, and structure
 */

export interface TestGenerationScore {
  total: number; // 0-10
  breakdown: {
    syntaxValidity: number; // 0-3
    coverage: number; // 0-3
    assertions: number; // 0-2
    structure: number; // 0-2
  };
  details: string[];
}

export function scoreTestGeneration(response: string): TestGenerationScore {
  const details: string[] = [];
  let syntaxScore = 0;
  let coverageScore = 0;
  let assertionScore = 0;
  let structureScore = 0;

  // Extract code block from response
  const codeBlockMatch = response.match(/```(?:typescript|ts|javascript|js)?\n([\s\S]*?)\n```/);
  const code = codeBlockMatch ? codeBlockMatch[1] : response;

  // === Syntax Validity Scoring (3 pts) ===
  const hasVitestImport = /import\s+{[^}]*(?:describe|it|expect|test)[^}]*}\s+from\s+['"]vitest['"]/.test(code) ||
                          /from\s+['"]vitest['"]/.test(code);
  const hasDescribe = /describe\s*\(\s*['"`]/.test(code);
  const hasIt = /it\s*\(\s*['"`]|test\s*\(\s*['"`]/.test(code);
  const hasExpect = /expect\s*\(/.test(code);

  if (hasVitestImport && hasDescribe && hasIt && hasExpect) {
    syntaxScore = 3;
    details.push('✓ Valid Vitest syntax with all required elements');
  } else {
    const missing = [];
    if (!hasVitestImport) missing.push('import');
    if (!hasDescribe) missing.push('describe');
    if (!hasIt) missing.push('it/test');
    if (!hasExpect) missing.push('expect');

    if (missing.length <= 1) {
      syntaxScore = 2;
      details.push(`~ Mostly valid syntax (missing: ${missing.join(', ')})`);
    } else if (missing.length <= 2) {
      syntaxScore = 1;
      details.push(`✗ Incomplete syntax (missing: ${missing.join(', ')})`);
    } else {
      details.push(`✗ Invalid test syntax (missing: ${missing.join(', ')})`);
    }
  }

  // === Coverage Scoring (3 pts) ===
  // Required test cases from the prompt:
  // 1. Happy path (valid token)
  // 2. Missing token/secret
  // 3. Invalid format
  // 4. Malformed payload

  const testCaseMatches = code.match(/it\s*\(\s*['"`]([^'"`]+)['"`]|test\s*\(\s*['"`]([^'"`]+)['"`]/g) || [];
  const testCaseCount = testCaseMatches.length;

  const coversValidToken = /valid.*token|should.*work|happy.*path|successful/i.test(code);
  const coversMissing = /missing.*(?:token|secret)|(?:token|secret).*missing|not.*provided|required/i.test(code);
  const coversInvalidFormat = /invalid.*format|wrong.*format|malformed|bad.*format/i.test(code);
  const coversMalformed = /malformed.*payload|decode.*fail|parse.*error|invalid.*json/i.test(code);

  const requiredCases = [
    { check: coversValidToken, label: 'valid token' },
    { check: coversMissing, label: 'missing params' },
    { check: coversInvalidFormat, label: 'invalid format' },
    { check: coversMalformed, label: 'malformed payload' }
  ];

  const coveredCount = requiredCases.filter(c => c.check).length;

  if (coveredCount === 4) coverageScore = 3;
  else if (coveredCount === 3) coverageScore = 2;
  else if (coveredCount === 2) coverageScore = 1;
  else coverageScore = 0.5;

  details.push(`Test coverage: ${coveredCount}/4 required cases (${testCaseCount} total tests)`);
  details.push(`  Covered: ${requiredCases.filter(c => c.check).map(c => c.label).join(', ')}`);

  // === Assertions Scoring (2 pts) ===
  const expectMatches = code.match(/expect\s*\([^)]+\)/g) || [];
  const expectCount = expectMatches.length;

  const usesToThrow = /toThrow|toThrowError/.test(code);
  const usesToBe = /toBe|toEqual|toStrictEqual/.test(code);
  const usesToHaveProperty = /toHaveProperty|toContain/.test(code);
  const usesTruthy = /toBeTruthy|toBeFalsy|toBeUndefined|toBeNull/.test(code);

  const assertionVariety = [usesToThrow, usesToBe, usesToHaveProperty, usesTruthy].filter(Boolean).length;

  if (expectCount >= 4 && assertionVariety >= 2) {
    assertionScore = 2;
    details.push(`✓ Good assertions (${expectCount} expect calls, ${assertionVariety} matcher types)`);
  } else if (expectCount >= 2) {
    assertionScore = 1;
    details.push(`~ Basic assertions (${expectCount} expect calls)`);
  } else {
    details.push(`✗ Insufficient assertions (${expectCount} expect calls)`);
  }

  // === Structure Scoring (2 pts) ===
  const hasDescribeBlocks = (code.match(/describe\s*\(/g) || []).length;
  const hasBeforeEach = /beforeEach|beforeAll|afterEach|afterAll/.test(code);
  const wellOrganized = hasDescribeBlocks >= 1 && testCaseCount >= 3;
  const hasSetup = /const.*=.*new|let.*=|const.*{/.test(code);

  if (wellOrganized && (hasBeforeEach || hasSetup)) {
    structureScore = 2;
    details.push('✓ Well-structured tests with setup');
  } else if (wellOrganized) {
    structureScore = 1.5;
    details.push('✓ Well-organized tests');
  } else if (hasDescribeBlocks >= 1) {
    structureScore = 1;
    details.push('~ Basic structure');
  } else {
    details.push('✗ Poor structure');
  }

  const total = syntaxScore + coverageScore + assertionScore + structureScore;

  return {
    total: Math.min(10, Math.round(total * 10) / 10),
    breakdown: {
      syntaxValidity: syntaxScore,
      coverage: coverageScore,
      assertions: assertionScore,
      structure: structureScore
    },
    details
  };
}
