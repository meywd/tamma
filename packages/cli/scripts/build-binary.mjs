/**
 * packages/cli/scripts/build-binary.mjs
 *
 * Compiles standalone Tamma CLI binaries using `bun build --compile`.
 * This script runs under node but shells out to bun for compilation.
 *
 * Usage:
 *   node packages/cli/scripts/build-binary.mjs
 *   node packages/cli/scripts/build-binary.mjs --target darwin-arm64
 *   node packages/cli/scripts/build-binary.mjs --output-dir ./my-binaries
 */

import { execFileSync, execSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, statSync, copyFileSync, unlinkSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const cliDir = join(__dirname, '..');
const pkg = JSON.parse(readFileSync(join(cliDir, 'package.json'), 'utf-8'));
const version = pkg.version;

// Verify bun is available
try {
  const bunVersion = execSync('bun --version', { encoding: 'utf-8', stdio: ['pipe', 'pipe', 'pipe'] }).trim();
  console.log(`Using bun ${bunVersion}`);
} catch {
  console.error('Error: bun is required for binary compilation but was not found.');
  console.error('Install bun: https://bun.sh/docs/installation');
  process.exit(1);
}

const TARGETS = [
  { platform: 'darwin-arm64', bunTarget: 'bun-darwin-arm64' },
  { platform: 'darwin-x64', bunTarget: 'bun-darwin-x64' },
  { platform: 'linux-x64', bunTarget: 'bun-linux-x64' },
  { platform: 'linux-arm64', bunTarget: 'bun-linux-arm64' },
  { platform: 'windows-x64', bunTarget: 'bun-windows-x64', exeExt: '.exe' },
];

// Parse args
const args = process.argv.slice(2);
const targetArg = args.includes('--target') ? args[args.indexOf('--target') + 1] : null;
const outputDirArg = args.includes('--output-dir') ? args[args.indexOf('--output-dir') + 1] : null;
const outputDir = resolve(outputDirArg ?? join(cliDir, 'dist/binaries'));

// Filter targets
const selectedTargets = targetArg
  ? TARGETS.filter((t) => t.platform === targetArg)
  : TARGETS;

if (selectedTargets.length === 0) {
  console.error(`Unknown target: ${targetArg}`);
  console.error(`Valid targets: ${TARGETS.map((t) => t.platform).join(', ')}`);
  process.exit(1);
}

// Ensure output directory exists
mkdirSync(outputDir, { recursive: true });

// Get git commit hash
let commit = 'unknown';
try {
  commit = execSync('git rev-parse HEAD', { encoding: 'utf-8', stdio: ['pipe', 'pipe', 'pipe'] }).trim();
} catch {
  console.warn('Warning: could not determine git commit hash');
}

const entryPoint = join(cliDir, 'src/index.tsx');
const assets = [];

for (const target of selectedTargets) {
  const exeExt = target.exeExt ?? '';
  const binaryName = `tamma-${version}-${target.platform}${exeExt}`;
  const outfile = join(outputDir, binaryName);

  console.log(`\nBuilding ${binaryName}...`);

  const cmdArgs = [
    'build', '--compile', '--minify',
    entryPoint,
    `--target=${target.bunTarget}`,
    `--outfile`, outfile,
    `--define`, `TAMMA_VERSION="'${version}'"`,
  ];

  try {
    execFileSync('bun', cmdArgs, { encoding: 'utf-8', stdio: 'inherit', cwd: cliDir });
  } catch (err) {
    console.error(`Failed to build ${target.platform}: ${err.message}`);
    process.exit(1);
  }

  // Generate SHA256 checksum for the raw binary
  const fileBuffer = readFileSync(outfile);
  const sha256 = createHash('sha256').update(fileBuffer).digest('hex');
  const checksumFile = `${outfile}.sha256`;
  writeFileSync(checksumFile, `${sha256}  ${binaryName}\n`);

  const size = statSync(outfile).size;

  // Create .tar.gz archive (Homebrew requires archives, not raw binaries).
  // Archive contains a single file named "tamma" (matching Homebrew formula's bin.install).
  // On Windows the staged file is "tamma.exe" so it is directly runnable when extracted.
  // Homebrew is mac/linux only, but we ship the archive for Windows as well for parity
  // and so the checksums file is uniform across platforms.
  const stagedExeName = exeExt ? `tamma${exeExt}` : 'tamma';
  const tarName = `tamma-${version}-${target.platform}.tar.gz`;
  const tarPath = join(outputDir, tarName);
  const stagingDir = join(outputDir, `_staging-${target.platform}`);
  mkdirSync(stagingDir, { recursive: true });
  copyFileSync(outfile, join(stagingDir, stagedExeName));
  execSync(`tar -czf "${tarPath}" -C "${stagingDir}" ${stagedExeName}`, { stdio: 'inherit' });
  // Clean up staging
  unlinkSync(join(stagingDir, stagedExeName));

  // Generate SHA256 checksum for the tar.gz
  const tarBuffer = readFileSync(tarPath);
  const tarSha256 = createHash('sha256').update(tarBuffer).digest('hex');
  writeFileSync(`${tarPath}.sha256`, `${tarSha256}  ${tarName}\n`);
  const tarSize = statSync(tarPath).size;

  assets.push({
    name: binaryName,
    platform: target.platform,
    sha256,
    size,
    tarName,
    tarSha256,
    tarSize,
  });

  console.log(`  ${binaryName}: ${(size / 1024 / 1024).toFixed(1)} MB (sha256: ${sha256.slice(0, 12)}...)`);
  console.log(`  ${tarName}: ${(tarSize / 1024 / 1024).toFixed(1)} MB (sha256: ${tarSha256.slice(0, 12)}...)`);
}

// Write manifest
const manifest = {
  version,
  buildDate: new Date().toISOString(),
  commit,
  assets,
};

const manifestPath = join(outputDir, 'manifest.json');
writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
console.log(`\nManifest written to ${manifestPath}`);

// Summary
console.log('\nBuild summary:');
for (const asset of assets) {
  console.log(`  ${asset.name}: ${(asset.size / 1024 / 1024).toFixed(1)} MB`);
}
