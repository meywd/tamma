/**
 * Common utility functions shared across the Tamma platform
 */

/**
 * Returns a monotonically increasing timestamp (milliseconds).
 * Guarantees unique values even when called multiple times in the same millisecond.
 */
let _lastMonotonicTs = 0;
export function monotonicNow(): number {
  const now = Date.now();
  _lastMonotonicTs = now > _lastMonotonicTs ? now : _lastMonotonicTs + 1;
  return _lastMonotonicTs;
}

export async function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/**
 * Convert text to a URL/branch-safe slug.
 * Lowercase, replace non-alphanumeric with hyphens, collapse multiple hyphens, trim, limit to 50 chars.
 * Implemented without regex to avoid CodeQL ReDoS flags on uncontrolled input.
 */
export function slugify(text: string): string {
  const lower = text.toLowerCase();

  // Build hyphenated string: replace runs of non-alphanumeric chars with single '-'
  let hyphenated = '';
  let prevWasHyphen = false;
  for (let i = 0; i < lower.length; i++) {
    const code = lower.charCodeAt(i);
    // a-z: 97-122, 0-9: 48-57
    const isAlphaNum =
      (code >= 97 && code <= 122) || (code >= 48 && code <= 57);
    if (isAlphaNum) {
      hyphenated += lower[i];
      prevWasHyphen = false;
    } else if (!prevWasHyphen) {
      hyphenated += '-';
      prevWasHyphen = true;
    }
  }

  // Trim leading/trailing hyphens
  let start = 0;
  while (start < hyphenated.length && hyphenated[start] === '-') start++;
  let end = hyphenated.length;
  while (end > start && hyphenated[end - 1] === '-') end--;
  let slug = hyphenated.slice(start, end).slice(0, 50);
  // Trim trailing hyphen after truncation
  while (slug.endsWith('-')) slug = slug.slice(0, -1);
  if (slug === '') return 'untitled';
  return slug;
}

/**
 * Extract issue references (#123) from text.
 * Returns unique issue numbers.
 */
export function extractIssueReferences(text: string): number[] {
  const matches = text.matchAll(/#(\d+)/g);
  const numbers = new Set<number>();
  for (const match of matches) {
    const num = parseInt(match[1] ?? '0', 10);
    if (num > 0) {
      numbers.add(num);
    }
  }
  return [...numbers];
}
