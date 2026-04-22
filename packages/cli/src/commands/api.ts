/**
 * `tamma api` command
 *
 * Starts the Tamma API server (C# / ASP.NET Core) as a child process.
 *
 * Usage:
 *   tamma api                          # default port 3100
 *   tamma api --port 8080              # custom port
 *   tamma api --private-key-path ./key.pem  # private key from file
 *
 * The C# API binary is located via TAMMA_API_BINARY env var or the
 * well-known path relative to the tamma-elsa solution.
 */

import { spawn } from 'node:child_process';
import * as path from 'node:path';
import * as fs from 'node:fs';

export interface ApiCommandOptions {
  port?: number;
  host?: string;
  privateKeyPath?: string;
  verbose?: boolean;
}

function findApiBinary(): string {
  // 1. Explicit env var
  const envBinary = process.env['TAMMA_API_BINARY'];
  if (envBinary && fs.existsSync(envBinary)) return envBinary;

  // 2. Well-known paths relative to this repo
  const repoRoot = path.resolve(import.meta.dirname ?? __dirname, '..', '..', '..', '..');
  const candidates = [
    path.join(repoRoot, 'apps', 'tamma-elsa', 'src', 'Tamma.Api', 'bin', 'Release', 'net8.0', 'Tamma.Api.dll'),
    path.join(repoRoot, 'apps', 'tamma-elsa', 'src', 'Tamma.Api', 'bin', 'Debug', 'net8.0', 'Tamma.Api.dll'),
  ];

  for (const candidate of candidates) {
    if (fs.existsSync(candidate)) return candidate;
  }

  // 3. Fallback: assume `dotnet run` from the project directory
  return '';
}

async function waitForHealth(port: number, host: string, maxRetries = 30): Promise<void> {
  const url = `http://${host}:${port}/api/health`;
  for (let i = 0; i < maxRetries; i++) {
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // Not ready yet
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error(`API health check failed after ${maxRetries}s at ${url}`);
}

export async function apiCommand(options: ApiCommandOptions): Promise<void> {
  const port = options.port ?? 3100;
  const host = options.host ?? '0.0.0.0';
  const apiBinary = findApiBinary();

  const env: Record<string, string> = {
    ...process.env as Record<string, string>,
    ASPNETCORE_URLS: `http://+:${port}`,
  };

  if (options.privateKeyPath) {
    env['GitHub__PrivateKeyPath'] = options.privateKeyPath;
  }
  if (options.verbose) {
    env['Logging__LogLevel__Default'] = 'Debug';
  }

  let child: ReturnType<typeof spawn>;

  if (apiBinary && apiBinary.endsWith('.dll')) {
    console.log(`Starting Tamma API (C#) via dotnet ${apiBinary} on ${host}:${port}...`);
    child = spawn('dotnet', [apiBinary], {
      env,
      stdio: 'inherit',
    });
  } else {
    // Fallback: dotnet run from the project directory
    const repoRoot = path.resolve(import.meta.dirname ?? __dirname, '..', '..', '..', '..');
    const projectDir = path.join(repoRoot, 'apps', 'tamma-elsa', 'src', 'Tamma.Api');
    console.log(`Starting Tamma API (C#) via dotnet run from ${projectDir} on ${host}:${port}...`);
    child = spawn('dotnet', ['run', '--project', projectDir], {
      env,
      stdio: 'inherit',
    });
  }

  child.on('error', (err) => {
    console.error(`Failed to start API process: ${err.message}`);
    console.error('Ensure the .NET 8 SDK is installed: https://dotnet.microsoft.com/download');
    process.exit(1);
  });

  child.on('exit', (code) => {
    process.exit(code ?? 1);
  });

  // Forward signals for graceful shutdown
  const forwardSignal = (signal: NodeJS.Signals): void => {
    child.kill(signal);
  };
  process.on('SIGINT', () => forwardSignal('SIGINT'));
  process.on('SIGTERM', () => forwardSignal('SIGTERM'));

  // Wait for the health endpoint
  try {
    await waitForHealth(port, host === '0.0.0.0' ? '127.0.0.1' : host);
    console.log(`Tamma API healthy on ${host}:${port}`);
  } catch {
    console.warn('API started but health check did not pass within timeout. It may still be initializing.');
  }
}
