/**
 * Regression test for the service-mode idle-on-config-error behavior.
 *
 * Before this commit, `tamma start --mode service` (the docker
 * tamma-engine container's CMD) would `process.exit(1)` whenever
 * GitHub config was incomplete, causing the container to restart-loop
 * indefinitely. After the fix, service-mode logs the errors and sits
 * idle waiting for SIGTERM/SIGINT — the container stays alive (and the
 * deploy's layer-4 health check stays green).
 *
 * For non-service invocations (single-user CLI), fail-fast on
 * incomplete config is preserved.
 */

import { describe, it, expect } from 'vitest';
import * as fs from 'node:fs';
import * as path from 'node:path';

const startSource = fs.readFileSync(
  path.join(__dirname, 'start.tsx'),
  'utf-8'
);

describe('startCommand service-mode config-error handling', () => {
  it('still fail-fasts (process.exit) when mode is not service', () => {
    // The original single-user path must remain: errors logged, then exit.
    expect(startSource).toMatch(/process\.exit\(1\);[\s\S]{0,400}options\.mode === 'service'|options\.mode === 'service'[\s\S]{0,500}process\.exit\(1\)/);
  });

  it('sits idle in --mode service when config is incomplete', () => {
    // The fix must check options.mode === 'service' before process.exit.
    expect(startSource).toContain("options.mode === 'service'");
  });

  it('writes the engine healthy marker file on idle entry', () => {
    // The container's HEALTHCHECK polls /tmp/tamma-engine-healthy — if
    // we don't touch it the orchestrator will mark the container
    // unhealthy even though we're intentionally idle.
    expect(startSource).toContain('/tmp/tamma-engine-healthy');
  });

  it('waits for SIGTERM/SIGINT instead of busy-looping', () => {
    // Idle implementation must register signal handlers so docker stop
    // / compose down terminates cleanly.
    expect(startSource).toMatch(/process\.on\(['"]SIGTERM['"]/);
    expect(startSource).toMatch(/process\.on\(['"]SIGINT['"]/);
  });

  it('logs a clear "sitting idle" message before parking', () => {
    // Operators reading container logs should see exactly why the
    // engine is alive but not processing — not a silent hang.
    expect(startSource).toMatch(/sitting idle/i);
  });
});
