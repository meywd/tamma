/**
 * Story 31-9 — onboarding platform picker API client. Mirrors the
 * `PlatformInstallEndpoints` C# routes:
 *
 *   GET  /api/onboarding/platforms      — picker source-of-truth
 *   POST /api/onboarding/install        — write installation
 *   GET  /api/onboarding/installations  — connected-platforms list
 *
 * Plaintext credential rule: this file IS the place we send the raw
 * token to the backend. The backend writes it to the Epic 29 cabinet
 * via `ISecretRevealService.IssueCreateAsync`. The browser must NOT
 * persist the value anywhere — the form holds it for the lifetime of
 * the submit click and clears it after the response settles.
 */
import { apiClient } from './client';

/** Mirror of the C# `PlatformKind` enum value names. */
export type PlatformKind =
  | 'GitHub'
  | 'Gitea'
  | 'Forgejo'
  | 'GitLab'
  | 'Bitbucket'
  | 'AzureDevOps';

/** Auth model the backend tells the picker to render per kind. */
export type AuthMode =
  | 'github_app'
  | 'personal_access_token'
  | 'coming_soon';

export interface PlatformDescriptor {
  kind: PlatformKind;
  displayName: string;
  available: boolean;
  capabilities: string[];
  authMode: AuthMode;
}

export interface PlatformListResponse {
  items: PlatformDescriptor[];
  count: number;
}

export interface PlatformInstallRequest {
  /** Mirror of `PlatformKind` value name (e.g. "Gitea"). */
  kind: PlatformKind;
  baseUrl: string;
  externalId?: string | null;
  /** Raw credential — see file-header note. */
  credentialPlaintext: string;
}

export interface PlatformInstallResponse {
  installationId: string;
  kind: PlatformKind;
  baseUrl: string;
  externalId: string | null;
  status: string;
}

export interface PlatformInstallErrorResponse {
  error: string;
  hint: string | null;
}

export interface PlatformConnection {
  installationId: string;
  kind: PlatformKind;
  baseUrl: string;
  externalId: string | null;
  status: string;
  isPrimary: boolean;
  createdAt: string;
}

export interface PlatformConnectionListResponse {
  items: PlatformConnection[];
  count: number;
}

/** GET /api/onboarding/platforms */
export async function listSupportedPlatforms(): Promise<PlatformListResponse> {
  return apiClient.get<PlatformListResponse>('/api/onboarding/platforms');
}

/** POST /api/onboarding/install */
export async function installPlatform(
  body: PlatformInstallRequest,
): Promise<PlatformInstallResponse> {
  return apiClient.post<PlatformInstallResponse>(
    '/api/onboarding/install',
    body,
  );
}

/** GET /api/onboarding/installations */
export async function listConnectedPlatforms(): Promise<PlatformConnectionListResponse> {
  return apiClient.get<PlatformConnectionListResponse>(
    '/api/onboarding/installations',
  );
}
