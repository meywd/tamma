/**
 * Automated Scoring for Code Generation Scenario
 *
 * Scores: 0-10 based on syntax validity, type safety, error handling, functionality, and quality
 */

import * as ts from 'typescript';

export interface CodeGenerationScore {
  total: number; // 0-10
  breakdown: {
    syntaxValidity: number; // 0-3
    typeSafety: number; // 0-2
    errorHandling: number; // 0-2
    functionality: number; // 0-2
    codeQuality: number; // 0-1
  };
  details: string[];
}

export function scoreCodeGeneration(response: string): CodeGenerationScore {
  const details: string[] = [];
  let syntaxScore = 0;
  let typeSafetyScore = 0;
  let errorHandlingScore = 0;
  let functionalityScore = 0;
  let qualityScore = 0;

  // Extract code block from response
  const codeBlockMatch = response.match(/```(?:typescript|ts|javascript|js)?\n([\s\S]*?)\n```/);
  const code = codeBlockMatch ? codeBlockMatch[1] : response;

  // === Syntax Validity Scoring (3 pts) ===
  const syntaxResult = checkTypeScriptSyntax(code);
  if (syntaxResult.valid) {
    syntaxScore = 3;
    details.push('✓ Valid TypeScript syntax');
  } else {
    details.push(`✗ Syntax errors: ${syntaxResult.errors.slice(0, 2).join('; ')}`);
  }

  // === Type Safety Scoring (2 pts) ===
  const hasFunctionSignature = /function\s+\w+\s*\([^)]*:\s*\w+/i.test(code) ||
                                /const\s+\w+\s*=\s*\([^)]*:\s*\w+/i.test(code);
  const hasReturnType = /:\s*\w+[\s<>]*(?:{|=|;)/i.test(code);
  const hasNoAny = !/:\s*any\b/i.test(code);

  if (hasFunctionSignature && hasReturnType) {
    typeSafetyScore = 2;
    details.push('✓ Has typed function signature and return type');
  } else if (hasFunctionSignature || hasReturnType) {
    typeSafetyScore = 1;
    details.push('~ Partial type annotations');
  }

  if (!hasNoAny) {
    typeSafetyScore = Math.max(0, typeSafetyScore - 0.5);
    details.push('✗ Uses "any" type');
  }

  // === Error Handling Scoring (2 pts) ===
  const hasTryCatch = /try\s*{[\s\S]*}\s*catch/i.test(code);
  const hasThrows = /throw\s+(?:new\s+)?Error/i.test(code);
  const hasValidation = /if\s*\(!.*\)\s*{?\s*throw/i.test(code) ||
                         /if\s*\(.*\)\s*{?\s*throw/i.test(code);

  if ((hasTryCatch || hasThrows) && hasValidation) {
    errorHandlingScore = 2;
    details.push('✓ Has error handling and input validation');
  } else if (hasTryCatch || hasThrows || hasValidation) {
    errorHandlingScore = 1;
    details.push('~ Basic error handling present');
  } else {
    details.push('✗ Missing error handling');
  }

  // === Functionality Scoring (2 pts) ===
  // Check for JWT validation specifics
  const validatesFormat = /split\s*\(\s*['"`]\.\s*['"`]\s*\)/.test(code) ||
                          /\.length\s*[!=]==?\s*3/.test(code);
  const decodesPayload = /atob|Buffer\.from|base64/.test(code) ||
                         /JSON\.parse/.test(code);
  const hasSecretParam = /secret\s*:/i.test(code) ||
                         /\(.*secret.*\)/i.test(code);

  let funcCount = 0;
  if (validatesFormat) {
    funcCount++;
    details.push('✓ Validates token format');
  }
  if (decodesPayload) {
    funcCount++;
    details.push('✓ Decodes JWT payload');
  }

  functionalityScore = funcCount >= 2 ? 2 : funcCount;

  // === Code Quality Scoring (1 pt) ===
  const hasProperNaming = /function\s+[a-z][a-zA-Z0-9]*|const\s+[a-z][a-zA-Z0-9]*/.test(code);
  const hasComments = /\/\/|\/\*/.test(code);
  const notTooLong = code.split('\n').length < 50;

  if (hasProperNaming && notTooLong) {
    qualityScore = 1;
    details.push('✓ Good code quality');
  } else if (hasProperNaming || notTooLong) {
    qualityScore = 0.5;
    details.push('~ Acceptable code quality');
  }

  const total = syntaxScore + typeSafetyScore + errorHandlingScore + functionalityScore + qualityScore;

  return {
    total: Math.min(10, Math.round(total * 10) / 10),
    breakdown: {
      syntaxValidity: syntaxScore,
      typeSafety: typeSafetyScore,
      errorHandling: errorHandlingScore,
      functionality: functionalityScore,
      codeQuality: qualityScore
    },
    details
  };
}

function checkTypeScriptSyntax(code: string): { valid: boolean; errors: string[] } {
  const errors: string[] = [];

  try {
    const sourceFile = ts.createSourceFile(
      'temp.ts',
      code,
      ts.ScriptTarget.Latest,
      true
    );

    // Check for syntax errors
    const diagnostics = (sourceFile as any).parseDiagnostics || [];

    if (diagnostics.length > 0) {
      for (const diagnostic of diagnostics) {
        if (diagnostic.messageText) {
          const message = typeof diagnostic.messageText === 'string'
            ? diagnostic.messageText
            : diagnostic.messageText.messageText;
          errors.push(message);
        }
      }
      return { valid: false, errors };
    }

    return { valid: true, errors: [] };
  } catch (error) {
    return {
      valid: false,
      errors: [error instanceof Error ? error.message : 'Unknown syntax error']
    };
  }
}
