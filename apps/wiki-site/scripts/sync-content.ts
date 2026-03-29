/**
 * sync-content.ts
 *
 * Pre-build script that copies and transforms markdown files from
 * wiki/ and docs/stories/ into Starlight's src/content/docs/ directory.
 *
 * Transformations:
 * 1. Injects Starlight-compatible frontmatter (title, description, sidebar order)
 * 2. Converts GitHub wiki-style links to Starlight internal links
 * 3. Converts GitHub repository links to local paths
 * 4. Preserves content inside fenced code blocks
 *
 * Usage: tsx scripts/sync-content.ts
 */

import { readFileSync, writeFileSync, mkdirSync, rmSync, readdirSync, existsSync, statSync, cpSync } from 'node:fs';
import { join, basename, dirname, relative, extname } from 'node:path';

// Paths relative to apps/wiki-site/
const REPO_ROOT = join(import.meta.dirname, '..', '..', '..');
const WIKI_DIR = join(REPO_ROOT, 'wiki');
const STORIES_DIR = join(REPO_ROOT, 'docs', 'stories');
const OUTPUT_DIR = join(import.meta.dirname, '..', 'src', 'content', 'docs');

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function ensureDir(dir: string): void {
  mkdirSync(dir, { recursive: true });
}

function cleanOutput(): void {
  if (existsSync(OUTPUT_DIR)) {
    rmSync(OUTPUT_DIR, { recursive: true, force: true });
  }
  ensureDir(OUTPUT_DIR);
}

/**
 * Extract an existing YAML frontmatter block if present.
 * Returns [frontmatterBody, remainingContent].
 */
function extractFrontmatter(content: string): [string | null, string] {
  const match = content.match(/^---\n([\s\S]*?)\n---\n([\s\S]*)$/);
  if (match) {
    return [match[1]!, match[2]!];
  }
  return [null, content];
}

/**
 * Derive a human-readable title from a markdown file.
 * Prefers the first H1 heading; falls back to the filename.
 */
function deriveTitle(content: string, filename: string): string {
  const h1Match = content.match(/^#\s+(.+)$/m);
  if (h1Match) {
    return h1Match[1]!.trim();
  }
  // Fallback: filename without extension, kebab-to-title
  return basename(filename, extname(filename))
    .replace(/[-_]/g, ' ')
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

/**
 * Build frontmatter string for a Starlight page.
 */
function buildFrontmatter(title: string, opts: { order?: number; description?: string } = {}): string {
  const lines = ['---', `title: "${title.replace(/"/g, '\\"')}"`];
  if (opts.description) {
    lines.push(`description: "${opts.description.replace(/"/g, '\\"')}"`);
  }
  if (opts.order !== undefined) {
    lines.push(`sidebar:`);
    lines.push(`  order: ${opts.order}`);
  }
  lines.push('---');
  return lines.join('\n');
}

/**
 * Transform wiki-style and GitHub links to Starlight paths.
 * Skips content inside fenced code blocks.
 */
function transformLinks(content: string): string {
  const lines = content.split('\n');
  let inCodeBlock = false;
  const result: string[] = [];

  for (const line of lines) {
    if (line.trimStart().startsWith('```')) {
      inCodeBlock = !inCodeBlock;
      result.push(line);
      continue;
    }
    if (inCodeBlock) {
      result.push(line);
      continue;
    }

    let transformed = line;

    // Wiki-style links: [Text](Epics/Epic-1-Foundation) -> [Text](/epics/1-foundation/)
    transformed = transformed.replace(
      /\[([^\]]+)\]\(Epics\/Epic-([^)]+)\)/g,
      (_match, text, epicSlug) => {
        const slug = epicSlug.toLowerCase().replace(/\s+/g, '-');
        return `[${text}](/epics/${slug}/)`;
      }
    );

    // Wiki-style links: [Text](Roadmap) -> [Text](/roadmap/)
    transformed = transformed.replace(
      /\[([^\]]+)\]\((Home|Roadmap|Architecture|Epics|Stories|Contributing)\)/g,
      (_match, text, page) => {
        const slug = page.toLowerCase();
        if (slug === 'home') return `[${text}](/)`;
        if (slug === 'epics') return `[${text}](/epics/)`;
        if (slug === 'stories') return `[${text}](/stories/)`;
        return `[${text}](/${slug}/)`;
      }
    );

    // GitHub links: https://github.com/meywd/tamma/tree/main/docs/stories/epic-N -> /stories/epic-N/
    transformed = transformed.replace(
      /https:\/\/github\.com\/meywd\/tamma\/(?:tree|blob)\/main\/docs\/stories\/(epic-[^/)\s]+)/g,
      (_match, epicDir) => `/stories/${epicDir}/`
    );

    // GitHub links: https://github.com/meywd/tamma/blob/main/docs/architecture.md -> /architecture/
    transformed = transformed.replace(
      /https:\/\/github\.com\/meywd\/tamma\/blob\/main\/docs\/architecture\.md/g,
      '/architecture/'
    );

    // GitHub links: https://github.com/meywd/tamma/blob/main/docs/PRD.md -> leave as-is (external)
    // GitHub links: https://github.com/meywd/tamma/blob/main/docs/epics.md -> /epics/
    transformed = transformed.replace(
      /https:\/\/github\.com\/meywd\/tamma\/blob\/main\/docs\/epics\.md/g,
      '/epics/'
    );

    // GitHub tree links to docs/ directory
    transformed = transformed.replace(
      /https:\/\/github\.com\/meywd\/tamma\/tree\/main\/docs(?:\/)?/g,
      '/epics/'
    );

    result.push(transformed);
  }

  return result.join('\n');
}

/**
 * Remove the first H1 heading from content (Starlight uses the frontmatter title).
 */
function removeFirstH1(content: string): string {
  return content.replace(/^#\s+.+\n+/, '');
}

// ---------------------------------------------------------------------------
// Sync: wiki/ top-level pages
// ---------------------------------------------------------------------------

/** Map of wiki filename -> output path and sidebar order */
const WIKI_PAGE_MAP: Record<string, { outPath: string; order: number }> = {
  'Home.md': { outPath: 'index.md', order: 0 },
  'Roadmap.md': { outPath: 'roadmap.md', order: 1 },
  'Architecture.md': { outPath: 'architecture.md', order: 2 },
  'Epics.md': { outPath: 'epics/index.md', order: 0 },
  'Stories.md': { outPath: 'stories/index.md', order: 0 },
  'Contributing.md': { outPath: 'contributing.md', order: 99 },
};

function syncWikiTopLevel(): void {
  console.log('Syncing wiki/ top-level pages...');

  for (const [filename, config] of Object.entries(WIKI_PAGE_MAP)) {
    const srcPath = join(WIKI_DIR, filename);
    if (!existsSync(srcPath)) {
      console.warn(`  SKIP: ${filename} not found`);
      continue;
    }

    const raw = readFileSync(srcPath, 'utf-8');
    const [_existingFm, body] = extractFrontmatter(raw);
    const title = deriveTitle(body, filename);
    const cleanBody = removeFirstH1(body);
    const transformed = transformLinks(cleanBody);

    const frontmatter = buildFrontmatter(title, { order: config.order });
    const outPath = join(OUTPUT_DIR, config.outPath);
    ensureDir(dirname(outPath));
    writeFileSync(outPath, `${frontmatter}\n\n${transformed}`);
    console.log(`  ${filename} -> ${config.outPath}`);
  }
}

// ---------------------------------------------------------------------------
// Sync: wiki/Epics/ pages
// ---------------------------------------------------------------------------

function syncWikiEpics(): void {
  console.log('Syncing wiki/Epics/ pages...');

  const epicsDir = join(WIKI_DIR, 'Epics');
  if (!existsSync(epicsDir)) {
    console.warn('  SKIP: wiki/Epics/ not found');
    return;
  }

  const files = readdirSync(epicsDir).filter((f) => f.endsWith('.md'));
  const outDir = join(OUTPUT_DIR, 'epics');
  ensureDir(outDir);

  for (const file of files) {
    const raw = readFileSync(join(epicsDir, file), 'utf-8');
    const [_existingFm, body] = extractFrontmatter(raw);
    const title = deriveTitle(body, file);
    const cleanBody = removeFirstH1(body);
    const transformed = transformLinks(cleanBody);

    // Derive sort order from epic number
    const epicNumMatch = file.match(/Epic-(\d+(?:\.\d+)?)/);
    const order = epicNumMatch ? parseFloat(epicNumMatch[1]!) : 99;

    // Output filename: Epic-1-Foundation.md -> 1-foundation.md
    const outName = file
      .replace(/^Epic-/, '')
      .toLowerCase()
      .replace(/\s+/g, '-');

    const frontmatter = buildFrontmatter(title, { order });
    writeFileSync(join(outDir, outName), `${frontmatter}\n\n${transformed}`);
    console.log(`  Epics/${file} -> epics/${outName}`);
  }
}

// ---------------------------------------------------------------------------
// Sync: docs/stories/ pages
// ---------------------------------------------------------------------------

function syncStories(): void {
  console.log('Syncing docs/stories/ pages...');

  if (!existsSync(STORIES_DIR)) {
    console.warn('  SKIP: docs/stories/ not found');
    return;
  }

  const entries = readdirSync(STORIES_DIR, { withFileTypes: true });

  for (const entry of entries) {
    const srcPath = join(STORIES_DIR, entry.name);

    // Top-level markdown files (e.g., 4-1-event-schema-design.md)
    if (entry.isFile() && entry.name.endsWith('.md')) {
      syncStoryFile(srcPath, join(OUTPUT_DIR, 'stories', entry.name));
      continue;
    }

    // Epic directories (e.g., epic-1/, epic-6/)
    if (entry.isDirectory() && entry.name.startsWith('epic-')) {
      syncEpicStoryDir(srcPath, entry.name);
    }
  }
}

function syncEpicStoryDir(epicSrcDir: string, epicDirName: string): void {
  const outDir = join(OUTPUT_DIR, 'stories', epicDirName);
  ensureDir(outDir);

  const entries = readdirSync(epicSrcDir, { withFileTypes: true });

  for (const entry of entries) {
    const srcPath = join(epicSrcDir, entry.name);

    if (entry.isFile() && entry.name.endsWith('.md')) {
      // README.md becomes index.md, others keep their name
      const outName = entry.name === 'README.md' ? 'index.md' : entry.name;
      syncStoryFile(srcPath, join(outDir, outName));
    }

    if (entry.isDirectory() && (entry.name.startsWith('story-') || entry.name.startsWith('tasks'))) {
      // Recurse into story subdirectories
      syncStorySubDir(srcPath, outDir);
    }
  }
}

function syncStorySubDir(srcDir: string, epicOutDir: string): void {
  const files = readdirSync(srcDir, { withFileTypes: true });

  for (const file of files) {
    if (file.isFile() && file.name.endsWith('.md')) {
      syncStoryFile(join(srcDir, file.name), join(epicOutDir, file.name));
    }

    // Handle nested directories (e.g., story-3-1/tasks/)
    if (file.isDirectory()) {
      syncStorySubDir(join(srcDir, file.name), epicOutDir);
    }
  }
}

function syncStoryFile(srcPath: string, outPath: string): void {
  const raw = readFileSync(srcPath, 'utf-8');
  const [_existingFm, body] = extractFrontmatter(raw);
  const title = deriveTitle(body, basename(srcPath));
  const cleanBody = removeFirstH1(body);
  const transformed = transformLinks(cleanBody);

  // Derive order from story/task number if possible
  const numMatch = basename(srcPath).match(/^(\d+(?:\.\d+)?)-/);
  const order = numMatch ? parseFloat(numMatch[1]!) * 10 : undefined;

  const frontmatter = buildFrontmatter(title, { order });
  ensureDir(dirname(outPath));
  writeFileSync(outPath, `${frontmatter}\n\n${transformed}`);

  const relSrc = relative(REPO_ROOT, srcPath);
  const relOut = relative(join(import.meta.dirname, '..'), outPath);
  console.log(`  ${relSrc} -> ${relOut}`);
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------

function main(): void {
  console.log('=== Tamma Wiki Content Sync ===\n');
  console.log(`Repo root:   ${REPO_ROOT}`);
  console.log(`Wiki dir:    ${WIKI_DIR}`);
  console.log(`Stories dir: ${STORIES_DIR}`);
  console.log(`Output dir:  ${OUTPUT_DIR}\n`);

  cleanOutput();
  syncWikiTopLevel();
  syncWikiEpics();
  syncStories();

  // Count output files
  let count = 0;
  function countFiles(dir: string): void {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.isFile()) count++;
      if (entry.isDirectory()) countFiles(join(dir, entry.name));
    }
  }
  countFiles(OUTPUT_DIR);

  console.log(`\nDone. ${count} files synced to ${relative(process.cwd(), OUTPUT_DIR)}/`);
}

main();
