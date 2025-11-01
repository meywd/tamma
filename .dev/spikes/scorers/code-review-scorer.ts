/**
 * Automated Scoring for Code Review Scenario
 *
 * Scores: 0-10 based on issue detection, severity accuracy, recommendations, and false positives
 */

export interface CodeReviewScore {
  total: number; // 0-10
  breakdown: {
    issueDetection: number; // 0-4
    severityAccuracy: number; // 0-2
    recommendations: number; // 0-2
    falsePositives: number; // 0-2 (penalty for incorrect issues)
  };
  details: string[];
}

// Known issues in the test code:
// function processUserData(data: any) {                    <-- Issue 1: 'any' type
//   const user = JSON.parse(data);                         <-- Issue 2: No error handling
//   const email = user.email.toLowerCase();                <-- Issue 3: No null check on user.email
//
//   fetch('https://api.example.com/users', {               <-- Issue 4: No error handling on fetch
//     method: 'POST',
//     body: JSON.stringify({ email })
//   });
//
//   return email;                                          <-- Issue 5: Not awaiting fetch (fire and forget)
// }

const KNOWN_ISSUES = [
  {
    keywords: ['any', 'type.*safety', 'typed', 'unknown', 'data.*any'],
    label: 'any type usage',
    severity: ['medium', 'high']
  },
  {
    keywords: ['error.*handl', 'try.*catch', 'JSON.*parse', 'parse.*fail', 'invalid.*json'],
    label: 'missing error handling for JSON.parse',
    severity: ['high', 'critical', 'medium']
  },
  {
    keywords: ['null.*check', 'undefined.*check', 'optional.*chain', '\\.email', 'user\\.email', 'property.*access'],
    label: 'missing null/undefined check',
    severity: ['medium', 'high']
  },
  {
    keywords: ['fetch.*error', 'network.*error', 'await.*fetch', 'async', 'promise'],
    label: 'no error handling for fetch',
    severity: ['medium', 'high']
  },
  {
    keywords: ['await', 'return.*await', 'promise', 'async', 'fire.*forget', 'not.*wait'],
    label: 'not awaiting async operation',
    severity: ['low', 'medium']
  }
];

export function scoreCodeReview(response: string): CodeReviewScore {
  const details: string[] = [];
  let issueDetectionScore = 0;
  let severityScore = 0;
  let recommendationsScore = 0;
  let falsePositivePenalty = 0;

  const lowerResponse = response.toLowerCase();

  // === Issue Detection Scoring (4 pts) ===
  const detectedIssues: string[] = [];

  for (const knownIssue of KNOWN_ISSUES) {
    const detected = knownIssue.keywords.some(keyword =>
      new RegExp(keyword, 'i').test(response)
    );

    if (detected) {
      detectedIssues.push(knownIssue.label);
    }
  }

  // Award 0.8 pts per issue detected (max 4 pts for 5 issues)
  issueDetectionScore = Math.min(4, detectedIssues.length * 0.8);

  if (detectedIssues.length >= 4) {
    details.push(`✓ Excellent issue detection: ${detectedIssues.length}/5 issues found`);
  } else if (detectedIssues.length >= 3) {
    details.push(`✓ Good issue detection: ${detectedIssues.length}/5 issues found`);
  } else if (detectedIssues.length >= 2) {
    details.push(`~ Fair issue detection: ${detectedIssues.length}/5 issues found`);
  } else {
    details.push(`✗ Poor issue detection: ${detectedIssues.length}/5 issues found`);
  }

  details.push(`  Detected: ${detectedIssues.join(', ')}`);

  // === Severity Accuracy Scoring (2 pts) ===
  const hasSeverityLevels = /critical|high|medium|low/i.test(response);
  const severityCount = (response.match(/critical|high|medium|low/gi) || []).length;

  // Check if severity assignments seem reasonable
  const criticalForTypeIssue = /any.*(?:critical|high)|critical.*any|high.*any/i.test(response);
  const highForErrorHandling = /error.*handl.*(?:critical|high)|(?:critical|high).*error/i.test(response);

  if (hasSeverityLevels && severityCount >= 3) {
    severityScore = 2;
    details.push('✓ Assigns appropriate severity levels');
  } else if (hasSeverityLevels) {
    severityScore = 1;
    details.push('~ Some severity classification');
  } else {
    details.push('✗ No severity levels assigned');
  }

  // === Recommendations Scoring (2 pts) ===
  const hasRecommendations = /recommend|should|fix|solution|instead|replace|use.*instead/i.test(response);
  const hasCodeExample = /```|`[^`]+`/.test(response);
  const recommendationCount = (response.match(/recommend|should.*use|fix.*by|solution.*is|instead.*use/gi) || []).length;

  const specificRecommendations = [
    /unknown.*instead.*any|any.*to.*unknown/i.test(response),
    /try.*catch|wrap.*in.*try/i.test(response),
    /optional.*chain|\?\.|nullish.*coalescing/i.test(response),
    /await.*fetch|make.*async|handle.*promise/i.test(response)
  ].filter(Boolean).length;

  if (hasRecommendations && specificRecommendations >= 2) {
    recommendationsScore = 2;
    details.push(`✓ Provides actionable recommendations (${specificRecommendations} specific fixes)`);
  } else if (hasRecommendations && recommendationCount >= 2) {
    recommendationsScore = 1.5;
    details.push('✓ Provides some recommendations');
  } else if (hasRecommendations) {
    recommendationsScore = 1;
    details.push('~ Basic recommendations');
  } else {
    details.push('✗ No actionable recommendations');
  }

  // === False Positives Penalty (up to -2 pts) ===
  // Check for common false positives that don't apply to this code

  const falsePositives: string[] = [];

  // The code is synchronous, but if review mentions async/await for JSON.parse (false positive)
  const incorrectAsyncForParse = /JSON\.parse.*async|await.*JSON\.parse/i.test(response);
  if (incorrectAsyncForParse) {
    falsePositives.push('incorrectly suggests async for JSON.parse');
  }

  // If review suggests issues that don't exist in the code
  const mentionsSecurity = /sql.*injection|xss|cross.*site/i.test(response);
  if (mentionsSecurity) {
    // This could be valid (API security), but in context of this specific code, it's reaching
    falsePositives.push('mentions security issues not directly in code');
  }

  // Calculate penalty (0.5 pts per false positive, max -2)
  falsePositivePenalty = Math.min(2, falsePositives.length * 0.5);

  if (falsePositives.length > 0) {
    details.push(`⚠ False positives detected: ${falsePositives.join(', ')} (-${falsePositivePenalty} pts)`);
  } else {
    details.push('✓ No obvious false positives');
  }

  const rawTotal = issueDetectionScore + severityScore + recommendationsScore - falsePositivePenalty;
  const total = Math.max(0, Math.min(10, Math.round(rawTotal * 10) / 10));

  return {
    total,
    breakdown: {
      issueDetection: issueDetectionScore,
      severityAccuracy: severityScore,
      recommendations: recommendationsScore,
      falsePositives: 2 - falsePositivePenalty // Show as positive contribution
    },
    details
  };
}
