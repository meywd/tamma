/**
 * Prod-pruned import-graph guard.
 *
 * This test catches a class of failure that is INVISIBLE to `tsc` and to normal
 * unit tests: the sidecar's runtime image is built with `pnpm install --prod`,
 * which prunes devDependencies. `typescript` is a devDependency of
 * `@tamma/intelligence` (its prod deps are only `chromadb` + `pg`). If any module
 * STATICALLY imported on the sidecar's boot path transitively value-imports
 * `typescript`, `node dist/server.js` dies at ESM link time with
 * `ERR_MODULE_NOT_FOUND: Cannot find package 'typescript'` — before any
 * try/catch can degrade — and the container crash-loops.
 *
 * The offender was the `@tamma/intelligence/indexer` barrel, which re-exports the
 * TypeScript chunker (`indexer/chunking/typescript-chunker.ts`, a value-import of
 * `typescript`). The sidecar only needs `EmbeddingService`, so it now imports the
 * narrow `@tamma/intelligence/embedding` subpath instead, and the chunker's
 * `typescript` import was made lazy (`await import('typescript')`).
 *
 * These tests walk the COMPILED `dist` graph (where type-only imports are already
 * erased — i.e. ground truth for what the runtime actually links) and assert:
 *   (A) the subpaths the sidecar imports never statically reach `typescript` nor
 *       the chunker; and
 *   (B) even the full `./indexer` barrel has NO static `typescript` import
 *       anywhere in its reachable set (the chunker stays lazy).
 */

import { readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

// ── Locate the built @tamma/intelligence package (deterministic path walk) ──
const testFile = fileURLToPath(import.meta.url);
const marker = `${path.sep}packages${path.sep}`;
const packagesDir = testFile.slice(0, testFile.lastIndexOf(marker)) + marker;
const intelDir = path.join(packagesDir, 'intelligence');
const intelPkg = JSON.parse(
  readFileSync(path.join(intelDir, 'package.json'), 'utf8'),
) as {
  exports: Record<string, { import: string }>;
  devDependencies?: Record<string, string>;
};

/** devDependencies are PRUNED from the `--prod` runtime image. */
const intelDevDeps = new Set(Object.keys(intelPkg.devDependencies ?? {}));

/** Resolve a `@tamma/intelligence/<subpath>` export to its built dist file. */
function resolveSubpath(subpath: string): string {
  const entry = intelPkg.exports[`.${subpath}`];
  if (!entry?.import) throw new Error(`No export for .${subpath} in @tamma/intelligence`);
  return path.resolve(intelDir, entry.import);
}

/**
 * Extract the STATIC import/re-export/side-effect specifiers from a compiled
 * (single-line-import) ES module. Dynamic `import('x')` is intentionally NOT
 * matched — that is the lazy escape hatch and does not force link-time
 * resolution.
 */
function staticSpecifiers(source: string): string[] {
  const specs: string[] = [];
  for (const line of source.split('\n')) {
    const fromMatch = /^\s*(?:import|export)\b[^\n]*?\bfrom\s*["']([^"']+)["']/.exec(line);
    if (fromMatch?.[1]) {
      specs.push(fromMatch[1]);
      continue;
    }
    const sideEffect = /^\s*import\s*["']([^"']+)["']\s*;?\s*$/.exec(line);
    if (sideEffect?.[1]) specs.push(sideEffect[1]);
  }
  return specs;
}

interface CrawlResult {
  /** Every dist file statically reachable from the entries. */
  reachedFiles: Set<string>;
  /** Every bare (non-relative) specifier seen anywhere in the reachable set. */
  bareSpecifiers: Set<string>;
}

/**
 * BFS the static import graph starting from `entries`, following ONLY relative
 * specifiers (which keeps us inside the intelligence package's dist). Bare
 * specifiers (`typescript`, `chromadb`, `@tamma/shared`, …) are recorded but not
 * descended into — we only need to know whether `typescript` is ever pulled.
 */
function crawl(entries: string[]): CrawlResult {
  const reachedFiles = new Set<string>();
  const bareSpecifiers = new Set<string>();
  const queue = [...entries];

  while (queue.length > 0) {
    const file = queue.pop()!;
    if (reachedFiles.has(file)) continue;
    reachedFiles.add(file);

    if (!existsSync(file)) {
      throw new Error(`Reached a dist file that does not exist (build stale?): ${file}`);
    }
    const source = readFileSync(file, 'utf8');
    for (const spec of staticSpecifiers(source)) {
      if (spec.startsWith('.')) {
        queue.push(path.resolve(path.dirname(file), spec));
      } else {
        bareSpecifiers.add(spec);
      }
    }
  }
  return { reachedFiles, bareSpecifiers };
}

describe('prod import graph — the sidecar boot path never pulls `typescript`', () => {
  it('(A) the subpaths the sidecar imports never reach the chunker or `typescript`', () => {
    // Exactly the @tamma/intelligence entrypoints env-composition.ts imports.
    const entries = ['/embedding', '/vector-store', '/rag'].map(resolveSubpath);
    const { reachedFiles, bareSpecifiers } = crawl(entries);

    expect(bareSpecifiers.has('typescript')).toBe(false);
    const reachedChunker = [...reachedFiles].some((f) => f.includes('typescript-chunker'));
    expect(reachedChunker).toBe(false);

    // Generalise beyond `typescript`: NO pruned devDependency of
    // @tamma/intelligence may be statically reachable from the sidecar boot path.
    const leakedDevDeps = [...bareSpecifiers].filter((s) => intelDevDeps.has(s));
    expect(leakedDevDeps).toEqual([]);
  });

  it('(B) even the full `./indexer` barrel has no STATIC `typescript` import (chunker is lazy)', () => {
    const { reachedFiles, bareSpecifiers } = crawl([resolveSubpath('/indexer')]);

    // Positive control: the crawler DOES reach the chunker via the barrel…
    const reachedChunker = [...reachedFiles].some((f) => f.includes('typescript-chunker'));
    expect(reachedChunker).toBe(true);

    // …yet no reachable module statically links `typescript`.
    expect(bareSpecifiers.has('typescript')).toBe(false);
  });
});

describe('env-composition.ts wiring (source-level guard)', () => {
  it('imports EmbeddingService from the narrow /embedding subpath, not the /indexer barrel', () => {
    const envComp = readFileSync(
      path.join(packagesDir, 'intelligence-server', 'src', 'env-composition.ts'),
      'utf8',
    );
    // Ignore comments so a mention of `/indexer` in prose does not fail the test.
    const code = envComp
      .replace(/\/\*[\s\S]*?\*\//g, '')
      .split('\n')
      .filter((l) => !l.trim().startsWith('//'))
      .join('\n');

    expect(code).toContain("from '@tamma/intelligence/embedding'");
    expect(code).not.toContain("from '@tamma/intelligence/indexer'");
  });
});
