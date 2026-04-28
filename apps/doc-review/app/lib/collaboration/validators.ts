export class ValidationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ValidationError';
  }
}

type CommentInput = {
  docPath: string;
  content: string;
  lineNumber?: number;
  lineContent?: string;
  parentId?: string;
};

type CommentUpdateInput = {
  content?: string;
  resolved?: boolean;
};

type SuggestionInput = {
  docPath: string;
  description?: string;
  originalText: string;
  suggestedText: string;
  lineStart: number;
  lineEnd: number;
  sessionId?: string;
};

type SuggestionUpdateInput = {
  description?: string;
  status?: string;
};

type DiscussionInput = {
  docPath: string;
  title: string;
  message: string;
  sessionId: string;
};

type DiscussionCreateInput = {
  docPath: string;
  title: string;
  description?: string;
  sessionId?: string;
};

type DiscussionUpdateInput = {
  title?: string;
  description?: string;
  status?: 'open' | 'resolved' | 'closed';
};

type DiscussionMessageInput = {
  content: string;
};

type SessionInput = {
  title: string;
  summary?: string;
  docPaths: string[];
};

export function validateCommentPayload(payload: unknown): CommentInput {
  const data = ensureRecord(payload);
  const docPath = ensureString(data.docPath, 'docPath');
  const content = ensureString(data.content, 'content');
  const lineContent = optionalString(data.lineContent);
  const lineNumber = optionalNumber(data.lineNumber, 'lineNumber');
  const parentId = optionalString(data.parentId);

  return { docPath, content, lineContent, lineNumber, parentId };
}

export function validateCommentUpdatePayload(payload: unknown): CommentUpdateInput {
  const data = ensureRecord(payload);
  const content = optionalString(data.content);
  const resolved = optionalBoolean(data.resolved);

  if (content === undefined && resolved === undefined) {
    throw new ValidationError('At least one field (content or resolved) must be provided for update.');
  }

  return { content, resolved };
}

export function validateSuggestionPayload(payload: unknown): SuggestionInput {
  const data = ensureRecord(payload);

  return {
    docPath: ensureString(data.docPath, 'docPath'),
    description: optionalString(data.description),
    originalText: ensureString(data.originalText, 'originalText'),
    suggestedText: ensureString(data.suggestedText, 'suggestedText'),
    lineStart: ensureNumber(data.lineStart, 'lineStart'),
    lineEnd: ensureNumber(data.lineEnd, 'lineEnd'),
    sessionId: optionalString(data.sessionId),
  };
}

export function validateSuggestionUpdatePayload(payload: unknown): SuggestionUpdateInput {
  const data = ensureRecord(payload);
  const description = optionalString(data.description);
  const status = optionalSuggestionStatus(data.status);

  if (description === undefined && status === undefined) {
    throw new ValidationError('At least one field (description or status) must be provided for update.');
  }

  return { description, status };
}

export function validateDiscussionPayload(payload: unknown): DiscussionInput {
  const data = ensureRecord(payload);

  return {
    docPath: ensureString(data.docPath, 'docPath'),
    title: ensureString(data.title, 'title'),
    message: ensureString(data.message, 'message'),
    sessionId: ensureString(data.sessionId, 'sessionId'),
  };
}

export function validateDiscussionCreatePayload(payload: unknown): DiscussionCreateInput {
  const data = ensureRecord(payload);

  return {
    docPath: ensureString(data.docPath, 'docPath'),
    title: ensureString(data.title, 'title'),
    description: optionalString(data.description),
    sessionId: optionalString(data.sessionId),
  };
}

export function validateDiscussionUpdatePayload(payload: unknown): DiscussionUpdateInput {
  const data = ensureRecord(payload);
  const title = optionalString(data.title);
  const description = optionalString(data.description);
  const status = optionalDiscussionStatus(data.status);

  if (title === undefined && description === undefined && status === undefined) {
    throw new ValidationError('At least one field (title, description, or status) must be provided for update.');
  }

  return { title, description, status };
}

export function validateDiscussionMessagePayload(payload: unknown): DiscussionMessageInput {
  const data = ensureRecord(payload);

  return {
    content: ensureString(data.content, 'content'),
  };
}

export function validateSessionPayload(payload: unknown): SessionInput {
  const data = ensureRecord(payload);

  const docPathsValue = data.docPaths;

  if (!Array.isArray(docPathsValue)) {
    throw new ValidationError('docPaths must be an array.');
  }

  const docPaths = docPathsValue.filter((value): value is string => typeof value === 'string' && value.trim() !== '');

  if (docPaths.length === 0) {
    throw new ValidationError('At least one docPath is required.');
  }

  return {
    title: ensureString(data.title, 'title'),
    summary: optionalString(data.summary),
    docPaths,
  };
}

function ensureRecord(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object') {
    throw new ValidationError('Body must be a JSON object.');
  }

  return value as Record<string, unknown>;
}

function ensureString(value: unknown, field: string): string {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new ValidationError(`${field} is required.`);
  }

  return value.trim();
}

function optionalString(value: unknown): string | undefined {
  if (typeof value === 'string' && value.trim() !== '') {
    return value.trim();
  }
  return undefined;
}

function ensureNumber(value: unknown, field: string): number {
  if (typeof value === 'number' && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === 'string' && value.trim() !== '') {
    const parsed = Number(value);
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }

  throw new ValidationError(`${field} must be a number.`);
}

function optionalNumber(value: unknown, field: string): number | undefined {
  if (value === undefined || value === null || value === '') {
    return undefined;
  }

  return ensureNumber(value, field);
}

function optionalBoolean(value: unknown): boolean | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  if (typeof value === 'boolean') {
    return value;
  }

  if (typeof value === 'string') {
    const lower = value.toLowerCase().trim();
    if (lower === 'true' || lower === '1') return true;
    if (lower === 'false' || lower === '0') return false;
  }

  if (typeof value === 'number') {
    if (value === 1) return true;
    if (value === 0) return false;
  }

  return undefined;
}

function optionalSuggestionStatus(value: unknown): string | undefined {
  if (value === undefined || value === null || value === '') {
    return undefined;
  }

  if (typeof value === 'string') {
    const status = value.trim().toLowerCase();
    if (status === 'pending' || status === 'approved' || status === 'rejected' || status === 'deleted') {
      return status;
    }
  }

  throw new ValidationError('Status must be one of: pending, approved, rejected, deleted');
}

function optionalDiscussionStatus(value: unknown): 'open' | 'resolved' | 'closed' | undefined {
  if (value === undefined || value === null || value === '') {
    return undefined;
  }

  if (typeof value === 'string') {
    const status = value.trim().toLowerCase();
    if (status === 'open' || status === 'resolved' || status === 'closed') {
      return status;
    }
  }

  throw new ValidationError('Status must be one of: open, resolved, closed');
}
