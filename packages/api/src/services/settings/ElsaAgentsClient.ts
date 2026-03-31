/**
 * ELSA Agents REST API Client
 *
 * Wraps the ELSA Agents REST API to provide typed access for syncing agent
 * definitions (prompts, execution settings) from the Tamma Dashboard.
 *
 * The ELSA Agents module exposes endpoints at /elsa/api/agents/* for CRUD
 * operations on AgentDefinition entities stored in PostgreSQL.
 */

// ---------------------------------------------------------------------------
// Types matching ELSA Agents REST API shapes
// ---------------------------------------------------------------------------

export interface ElsaInputVariableConfig {
  name: string;
  description: string;
  type: string;
}

export interface ElsaOutputVariableConfig {
  description: string;
  type: string;
}

export interface ElsaExecutionSettingsConfig {
  maxTokens?: number;
  temperature: number;
  topP?: number;
  presencePenalty?: number;
  frequencyPenalty?: number;
  responseFormat?: string;
}

export interface ElsaAgentConfig {
  name: string;
  description: string;
  promptTemplate: string;
  inputVariables: ElsaInputVariableConfig[];
  outputVariable: ElsaOutputVariableConfig;
  executionSettings: ElsaExecutionSettingsConfig;
}

export interface ElsaAgentDefinition {
  id: string;
  name: string;
  description: string;
  agentConfig: ElsaAgentConfig;
}

interface ElsaAgentInputModel {
  name: string;
  description: string;
  agentConfig: ElsaAgentConfig;
}

// ---------------------------------------------------------------------------
// Client
// ---------------------------------------------------------------------------

export interface ElsaAgentsClientConfig {
  baseUrl: string;
  apiKey: string;
  requestTimeoutMs?: number;
}

export class ElsaAgentsClient {
  private readonly baseUrl: string;
  private readonly apiKey: string;
  private readonly requestTimeoutMs: number;

  constructor(config: ElsaAgentsClientConfig) {
    let url = config.baseUrl;
    while (url.length > 0 && url.endsWith('/')) {
      url = url.slice(0, -1);
    }
    this.baseUrl = url;
    this.apiKey = config.apiKey;
    this.requestTimeoutMs = config.requestTimeoutMs ?? 10_000;
  }

  async listAgents(): Promise<ElsaAgentDefinition[]> {
    const response = await this.request<{ items: ElsaAgentDefinition[] }>(
      'GET',
      '/elsa/api/agents',
    );
    return response.items ?? [];
  }

  async findAgentByName(name: string): Promise<ElsaAgentDefinition | null> {
    const agents = await this.listAgents();
    return agents.find((a) => a.name === name) ?? null;
  }

  async getAgent(id: string): Promise<ElsaAgentDefinition> {
    return this.request<ElsaAgentDefinition>('GET', `/elsa/api/agents/${encodeURIComponent(id)}`);
  }

  async createAgent(input: ElsaAgentInputModel): Promise<ElsaAgentDefinition> {
    return this.request<ElsaAgentDefinition>('POST', '/elsa/api/agents', input);
  }

  async updateAgent(id: string, input: ElsaAgentInputModel): Promise<ElsaAgentDefinition> {
    return this.request<ElsaAgentDefinition>(
      'POST',
      `/elsa/api/agents/${encodeURIComponent(id)}`,
      input,
    );
  }

  async deleteAgent(id: string): Promise<void> {
    await this.request<void>('DELETE', `/elsa/api/agents/${encodeURIComponent(id)}`);
  }

  // ---------------------------------------------------------------------------
  // Internal
  // ---------------------------------------------------------------------------

  private async request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const url = `${this.baseUrl}${path}`;
    const headers: Record<string, string> = {
      Authorization: `ApiKey ${this.apiKey}`,
      Accept: 'application/json',
    };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
    }

    const response = await fetch(url, {
      method,
      headers,
      signal: AbortSignal.timeout(this.requestTimeoutMs),
      ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    });

    if (!response.ok) {
      const text = await response.text().catch(() => '');
      throw new Error(`ELSA Agents API ${method} ${path} returned ${response.status}: ${text}`);
    }

    const contentType = response.headers.get('content-type') ?? '';
    if (contentType.includes('application/json')) {
      return (await response.json()) as T;
    }
    return undefined as unknown as T;
  }
}
