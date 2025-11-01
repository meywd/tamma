/**
 * Automated Scoring for Issue Analysis Scenario
 *
 * Scores: 0-10 based on structure, completeness, ambiguity detection, and scope clarity
 */

export interface IssueAnalysisScore {
  total: number; // 0-10
  breakdown: {
    structure: number; // 0-3
    completeness: number; // 0-3
    ambiguityDetection: number; // 0-2
    scopeClarity: number; // 0-2
  };
  details: string[];
}

export function scoreIssueAnalysis(response: string): IssueAnalysisScore {
  const details: string[] = [];
  let structureScore = 0;
  let completenessScore = 0;
  let ambiguityScore = 0;
  let scopeScore = 0;

  const lowerResponse = response.toLowerCase();

  // === Structure Scoring (3 pts) ===
  // Check for presence of key sections
  const hasRequirementsSection =
    /requirements?:?/i.test(response) ||
    /what.*needed/i.test(response) ||
    /functional.*requirements?/i.test(response);

  const hasAmbiguitiesSection =
    /ambiguit(y|ies):?/i.test(response) ||
    /unclear:?/i.test(response) ||
    /need.*clarif(y|ication)/i.test(response);

  const hasScopeSection =
    /scope:?/i.test(response) ||
    /in.*scope/i.test(response) ||
    /out.*of.*scope/i.test(response);

  if (hasRequirementsSection) {
    structureScore += 1;
    details.push('✓ Has requirements section');
  }
  if (hasAmbiguitiesSection) {
    structureScore += 1;
    details.push('✓ Has ambiguities section');
  }
  if (hasScopeSection) {
    structureScore += 1;
    details.push('✓ Has scope section');
  }

  // === Completeness Scoring (3 pts) ===
  // Check for coverage of key JWT auth requirements
  const mentionsJWT = /jwt/i.test(response);
  const mentionsAuth = /auth(entication|orization)?/i.test(response);
  const mentionsRegister = /register|registration|sign.?up/i.test(response);
  const mentionsLogin = /login|sign.?in/i.test(response);
  const mentionsExpiry = /expir(y|e|ation)|24.*hour|ttl|time.*to.*live/i.test(response);
  const mentionsToken = /token/i.test(response);

  const coverageItems = [
    { check: mentionsJWT || mentionsToken, label: 'JWT/token' },
    { check: mentionsAuth, label: 'authentication' },
    { check: mentionsRegister, label: 'registration' },
    { check: mentionsLogin, label: 'login' },
    { check: mentionsExpiry, label: 'token expiration' }
  ];

  const coveredCount = coverageItems.filter(item => item.check).length;

  if (coveredCount >= 5) completenessScore = 3;
  else if (coveredCount >= 4) completenessScore = 2;
  else if (coveredCount >= 3) completenessScore = 1;

  details.push(`Coverage: ${coveredCount}/5 key requirements (${coverageItems.filter(i => i.check).map(i => i.label).join(', ')})`);

  // === Ambiguity Detection Scoring (2 pts) ===
  // Check if response identifies common ambiguities in the issue
  const identifiesPasswordRules = /password.*rule|password.*requirement|password.*policy|password.*complexity/i.test(response);
  const identifiesUserSchema = /user.*model|user.*schema|user.*field|user.*data|user.*information/i.test(response);
  const identifiesRefreshToken = /refresh.*token/i.test(response);
  const identifiesStorageMethod = /store|storage|database|persist/i.test(response);
  const identifiesSecretManagement = /secret|key.*management|environment.*variable/i.test(response);

  const ambiguitiesDetected = [
    identifiesPasswordRules,
    identifiesUserSchema,
    identifiesRefreshToken,
    identifiesStorageMethod,
    identifiesSecretManagement
  ].filter(Boolean).length;

  if (ambiguitiesDetected >= 3) ambiguityScore = 2;
  else if (ambiguitiesDetected >= 2) ambiguityScore = 1.5;
  else if (ambiguitiesDetected >= 1) ambiguityScore = 1;

  details.push(`Ambiguities detected: ${ambiguitiesDetected}/5`);

  // === Scope Clarity Scoring (2 pts) ===
  // Check if response explicitly states what's in/out of scope
  const hasInScope = /in.*scope|included|will.*implement|part.*of/i.test(response);
  const hasOutScope = /out.*of.*scope|excluded|not.*included|won't.*implement|future/i.test(response);
  const definesBoundaries = /limitation|constraint|assumption|prerequisite/i.test(response);

  if (hasInScope && hasOutScope) {
    scopeScore = 2;
    details.push('✓ Clearly defines in-scope and out-of-scope items');
  } else if (hasInScope || hasOutScope) {
    scopeScore = 1;
    details.push('✓ Mentions scope boundaries');
  } else if (definesBoundaries) {
    scopeScore = 0.5;
    details.push('~ Mentions limitations/assumptions');
  }

  const total = structureScore + completenessScore + ambiguityScore + scopeScore;

  return {
    total: Math.min(10, Math.round(total * 10) / 10), // Round to 1 decimal
    breakdown: {
      structure: structureScore,
      completeness: completenessScore,
      ambiguityDetection: ambiguityScore,
      scopeClarity: scopeScore
    },
    details
  };
}
