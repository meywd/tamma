/**
 * `tamma api` command
 *
 * Starts the Tamma API server in SaaS mode (GitHub App auth).
 * Delegates to @tamma/api's startApiServer() which owns all deps.
 *
 * Usage:
 *   tamma api                          # default port 3100
 *   tamma api --port 8080              # custom port
 *   tamma api --private-key-path ./key.pem  # private key from file
 *
 * Required env vars:
 *   GITHUB_APP_ID, GITHUB_WEBHOOK_SECRET, and one of:
 *   GITHUB_APP_PRIVATE_KEY_PATH or GITHUB_APP_PRIVATE_KEY or --private-key-path
 */

import { startApiServer } from '@tamma/api';
import type { ApiServerOptions } from '@tamma/api';

export interface ApiCommandOptions {
  port?: number;
  host?: string;
  privateKeyPath?: string;
  verbose?: boolean;
}

export async function apiCommand(options: ApiCommandOptions): Promise<void> {
  const serverOptions: ApiServerOptions = {};
  if (options.port !== undefined) serverOptions.port = options.port;
  if (options.host !== undefined) serverOptions.host = options.host;
  if (options.privateKeyPath !== undefined) serverOptions.privateKeyPath = options.privateKeyPath;
  if (options.verbose) serverOptions.logLevel = 'debug';
  await startApiServer(serverOptions);
}
