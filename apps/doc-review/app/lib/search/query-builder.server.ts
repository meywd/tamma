import type { Database } from '../db/client.server';

/**
 * Search Query Builder
 * Constructs and executes FTS5 queries with filters and pagination
 */

export interface SearchFilters {
  type?: 'documents' | 'comments' | 'suggestions' | 'discussions' | 'all';
  docPath?: string;
  userId?: string;
  status?: string;
  before?: string | Date;
  after?: string | Date;
  resolved?: boolean;
}

export interface SearchOptions {
  limit?: number;
  offset?: number;
  highlight?: boolean;
  snippetLength?: number;
}

export interface SearchResult {
  id: string;
  type: 'document' | 'comment' | 'suggestion' | 'discussion' | 'message';
  docPath: string;
  title?: string;
  content: string;
  snippet?: string;
  highlights?: string[];
  authorName?: string;
  authorId?: string;
  status?: string;
  createdAt?: number;
  score: number;
}

export class SearchQueryBuilder {
  private query: string = '';
  private filters: SearchFilters = {};
  private options: SearchOptions = {
    limit: 20,
    offset: 0,
    highlight: true,
    snippetLength: 150
  };

  constructor(private db: Database) {}

  /**
   * Set the search query
   */
  search(query: string): this {
    // Escape special FTS5 characters
    this.query = this.escapeFTS5Query(query);
    return this;
  }

  /**
   * Filter by content type
   */
  filterByType(type: SearchFilters['type']): this {
    this.filters.type = type;
    return this;
  }

  /**
   * Filter by document path
   */
  filterByDocPath(docPath: string): this {
    this.filters.docPath = docPath;
    return this;
  }

  /**
   * Filter by user ID
   */
  filterByUser(userId: string): this {
    this.filters.userId = userId;
    return this;
  }

  /**
   * Filter by status
   */
  filterByStatus(status: string): this {
    this.filters.status = status;
    return this;
  }

  /**
   * Filter by date range
   */
  filterByDateRange(before?: string | Date, after?: string | Date): this {
    if (before) this.filters.before = before;
    if (after) this.filters.after = after;
    return this;
  }

  /**
   * Filter by resolved status (for comments)
   */
  filterByResolved(resolved: boolean): this {
    this.filters.resolved = resolved;
    return this;
  }

  /**
   * Set pagination
   */
  paginate(limit: number, offset: number): this {
    this.options.limit = Math.min(limit, 100); // Max 100 results per page
    this.options.offset = offset;
    return this;
  }

  /**
   * Execute the search query
   */
  async execute(): Promise<{
    results: SearchResult[];
    total: number;
    facets: {
      types: Record<string, number>;
      statuses: Record<string, number>;
      authors: Array<{ id: string; name: string; count: number }>;
    };
  }> {
    const startTime = Date.now();
    const results: SearchResult[] = [];
    let total = 0;

    // Build facets object
    const facets = {
      types: {} as Record<string, number>,
      statuses: {} as Record<string, number>,
      authors: new Map<string, { name: string; count: number }>()
    };

    // Return empty results for empty queries
    if (!this.query || this.query === '""') {
      return {
        results: [],
        total: 0,
        facets: {
          types: {},
          statuses: {},
          authors: []
        }
      };
    }

    try {
      // Search based on type filter or search all
      const searchTypes = this.getSearchTypes();

      for (const type of searchTypes) {
        const typeResults = await this.searchByType(type);
        results.push(...typeResults);
      }

      // Sort by score (relevance) and then by recency
      results.sort((a, b) => {
        if (Math.abs(a.score - b.score) > 0.1) {
          return b.score - a.score; // Higher score first
        }
        // If scores are similar, sort by date
        return (b.createdAt || 0) - (a.createdAt || 0);
      });

      // Apply pagination to combined results
      total = results.length;
      const offset = this.options.offset ?? 0;
      const limit = this.options.limit ?? 20;
      const paginatedResults = results.slice(
        offset,
        offset + limit
      );

      // Build facets from all results (not just paginated)
      for (const result of results) {
        // Type facets
        facets.types[result.type] = (facets.types[result.type] || 0) + 1;

        // Status facets
        if (result.status) {
          facets.statuses[result.status] = (facets.statuses[result.status] || 0) + 1;
        }

        // Author facets
        if (result.authorId && result.authorName) {
          const author = facets.authors.get(result.authorId) || { name: result.authorName, count: 0 };
          author.count++;
          facets.authors.set(result.authorId, author);
        }
      }

      // Log search analytics
      await this.logSearchQuery(this.query, this.filters, total, Date.now() - startTime);

      return {
        results: paginatedResults,
        total,
        facets: {
          types: facets.types,
          statuses: facets.statuses,
          authors: Array.from(facets.authors.entries())
            .map(([id, data]) => ({ id, ...data }))
            .sort((a, b) => b.count - a.count)
            .slice(0, 10) // Top 10 authors
        }
      };
    } catch (error) {
      console.error('Search query failed:', error);
      throw new Error('Failed to execute search query');
    }
  }

  /**
   * Search a specific content type
   */
  private async searchByType(type: string): Promise<SearchResult[]> {
    const results: SearchResult[] = [];

    switch (type) {
      case 'documents':
        results.push(...(await this.searchDocuments()));
        break;
      case 'comments':
        results.push(...(await this.searchComments()));
        break;
      case 'suggestions':
        results.push(...(await this.searchSuggestions()));
        break;
      case 'discussions':
        results.push(...(await this.searchDiscussions()));
        break;
      case 'messages':
        results.push(...(await this.searchDiscussionMessages()));
        break;
    }

    return results;
  }

  /**
   * Search documents
   */
  private async searchDocuments(): Promise<SearchResult[]> {
    // Documents don't have authors or status, so skip if those filters are applied
    if (this.filters.userId || this.filters.status) {
      return [];
    }

    let sql = `
      SELECT
        doc_path as id,
        doc_path,
        title,
        snippet(documents_fts, 2, '[', ']', '...', 30) as snippet,
        -rank as score
      FROM documents_fts
      WHERE documents_fts MATCH ?
    `;

    const params: any[] = [this.query];

    if (this.filters.docPath) {
      sql += ` AND doc_path = ?`;
      params.push(this.filters.docPath);
    }

    sql += ` ORDER BY rank LIMIT 100`;

    const result = await this.db.$client.prepare(sql).bind(...params).all();
    const rows = result.results as any[];

    return rows.map(row => ({
      id: row.id,
      type: 'document' as const,
      docPath: row.doc_path,
      title: row.title,
      content: row.title,
      snippet: row.snippet,
      score: row.score
    }));
  }

  /**
   * Search comments
   */
  private async searchComments(): Promise<SearchResult[]> {
    // Comments only have 'open' or 'resolved' status, not other statuses like 'pending', 'approved', etc.
    if (this.filters.status && this.filters.status !== 'open' && this.filters.status !== 'resolved') {
      return [];
    }

    let sql = `
      SELECT
        comment_id as id,
        doc_path,
        content,
        author_name,
        user_id,
        created_at,
        resolved,
        snippet(comments_fts, 2, '[', ']', '...', 30) as snippet,
        -rank as score
      FROM comments_fts
      WHERE comments_fts MATCH ?
    `;

    const params: any[] = [this.query];

    if (this.filters.docPath) {
      sql += ` AND doc_path = ?`;
      params.push(this.filters.docPath);
    }

    if (this.filters.userId) {
      sql += ` AND user_id = ?`;
      params.push(this.filters.userId);
    }

    // Map status filter to resolved boolean
    if (this.filters.status) {
      sql += ` AND resolved = ?`;
      params.push(this.filters.status === 'resolved' ? 1 : 0);
    } else if (this.filters.resolved !== undefined) {
      sql += ` AND resolved = ?`;
      params.push(this.filters.resolved ? 1 : 0);
    }

    if (this.filters.after) {
      const timestamp = new Date(this.filters.after).getTime();
      sql += ` AND created_at >= ?`;
      params.push(timestamp);
    }

    if (this.filters.before) {
      const timestamp = new Date(this.filters.before).getTime();
      sql += ` AND created_at <= ?`;
      params.push(timestamp);
    }

    sql += ` ORDER BY rank, created_at DESC LIMIT 100`;

    const result = await this.db.$client.prepare(sql).bind(...params).all();
    const rows = result.results as any[];

    return rows.map(row => ({
      id: row.id,
      type: 'comment' as const,
      docPath: row.doc_path,
      content: row.content,
      snippet: row.snippet,
      authorName: row.author_name,
      authorId: row.user_id,
      status: row.resolved ? 'resolved' : 'open',
      createdAt: row.created_at,
      score: row.score
    }));
  }

  /**
   * Search suggestions
   */
  private async searchSuggestions(): Promise<SearchResult[]> {
    let sql = `
      SELECT
        suggestion_id as id,
        doc_path,
        description,
        original_text,
        suggested_text,
        author_name,
        user_id,
        status,
        created_at,
        snippet(suggestions_fts, 2, '[', ']', '...', 30) as snippet,
        -rank as score
      FROM suggestions_fts
      WHERE suggestions_fts MATCH ?
    `;

    const params: any[] = [this.query];

    if (this.filters.docPath) {
      sql += ` AND doc_path = ?`;
      params.push(this.filters.docPath);
    }

    if (this.filters.userId) {
      sql += ` AND user_id = ?`;
      params.push(this.filters.userId);
    }

    if (this.filters.status) {
      sql += ` AND status = ?`;
      params.push(this.filters.status);
    }

    if (this.filters.after) {
      const timestamp = new Date(this.filters.after).getTime();
      sql += ` AND created_at >= ?`;
      params.push(timestamp);
    }

    if (this.filters.before) {
      const timestamp = new Date(this.filters.before).getTime();
      sql += ` AND created_at <= ?`;
      params.push(timestamp);
    }

    sql += ` ORDER BY rank, created_at DESC LIMIT 100`;

    const result = await this.db.$client.prepare(sql).bind(...params).all();
    const rows = result.results as any[];

    return rows.map(row => ({
      id: row.id,
      type: 'suggestion' as const,
      docPath: row.doc_path,
      content: row.description || `${row.original_text} → ${row.suggested_text}`,
      snippet: row.snippet,
      authorName: row.author_name,
      authorId: row.user_id,
      status: row.status,
      createdAt: row.created_at,
      score: row.score
    }));
  }

  /**
   * Search discussions
   */
  private async searchDiscussions(): Promise<SearchResult[]> {
    let sql = `
      SELECT
        discussion_id as id,
        doc_path,
        title,
        description,
        author_name,
        user_id,
        status,
        created_at,
        snippet(discussions_fts, 3, '[', ']', '...', 30) as snippet,
        -rank as score
      FROM discussions_fts
      WHERE discussions_fts MATCH ?
    `;

    const params: any[] = [this.query];

    if (this.filters.docPath) {
      sql += ` AND doc_path = ?`;
      params.push(this.filters.docPath);
    }

    if (this.filters.userId) {
      sql += ` AND user_id = ?`;
      params.push(this.filters.userId);
    }

    if (this.filters.status) {
      sql += ` AND status = ?`;
      params.push(this.filters.status);
    }

    if (this.filters.after) {
      const timestamp = new Date(this.filters.after).getTime();
      sql += ` AND created_at >= ?`;
      params.push(timestamp);
    }

    if (this.filters.before) {
      const timestamp = new Date(this.filters.before).getTime();
      sql += ` AND created_at <= ?`;
      params.push(timestamp);
    }

    sql += ` ORDER BY rank, created_at DESC LIMIT 100`;

    const result = await this.db.$client.prepare(sql).bind(...params).all();
    const rows = result.results as any[];

    return rows.map(row => ({
      id: row.id,
      type: 'discussion' as const,
      docPath: row.doc_path,
      title: row.title,
      content: row.title,
      snippet: row.snippet,
      authorName: row.author_name,
      authorId: row.user_id,
      status: row.status,
      createdAt: row.created_at,
      score: row.score
    }));
  }

  /**
   * Search discussion messages
   */
  private async searchDiscussionMessages(): Promise<SearchResult[]> {
    let sql = `
      SELECT
        dm.message_id as id,
        dm.discussion_id,
        dm.content,
        dm.author_name,
        dm.user_id,
        dm.created_at,
        d.doc_path,
        d.title as discussion_title,
        snippet(discussion_messages_fts, 2, '[', ']', '...', 30) as snippet,
        -dm.rank as score
      FROM discussion_messages_fts dm
      JOIN discussions_fts d ON dm.discussion_id = d.discussion_id
      WHERE dm.discussion_messages_fts MATCH ?
    `;

    const params: any[] = [this.query];

    if (this.filters.docPath) {
      sql += ` AND d.doc_path = ?`;
      params.push(this.filters.docPath);
    }

    if (this.filters.userId) {
      sql += ` AND dm.user_id = ?`;
      params.push(this.filters.userId);
    }

    if (this.filters.after) {
      const timestamp = new Date(this.filters.after).getTime();
      sql += ` AND dm.created_at >= ?`;
      params.push(timestamp);
    }

    if (this.filters.before) {
      const timestamp = new Date(this.filters.before).getTime();
      sql += ` AND dm.created_at <= ?`;
      params.push(timestamp);
    }

    sql += ` ORDER BY dm.rank, dm.created_at DESC LIMIT 100`;

    const result = await this.db.$client.prepare(sql).bind(...params).all();
    const rows = result.results as any[];

    return rows.map(row => ({
      id: row.id,
      type: 'message' as const,
      docPath: row.doc_path,
      title: `Message in: ${row.discussion_title}`,
      content: row.content,
      snippet: row.snippet,
      authorName: row.author_name,
      authorId: row.user_id,
      createdAt: row.created_at,
      score: row.score
    }));
  }

  /**
   * Get search types based on filter
   */
  private getSearchTypes(): string[] {
    if (this.filters.type === 'all' || !this.filters.type) {
      return ['documents', 'comments', 'suggestions', 'discussions', 'messages'];
    }

    if (this.filters.type === 'discussions') {
      // Include both discussions and messages
      return ['discussions', 'messages'];
    }

    return [this.filters.type];
  }

  /**
   * Escape special FTS5 characters
   */
  private escapeFTS5Query(query: string): string {
    if (!query) return '""';

    // Handle quoted phrases
    if (query.startsWith('"') && query.endsWith('"')) {
      return query;
    }

    // Escape special characters but preserve wildcards
    const escaped = query.replace(/[()]/g, '');

    // Handle prefix search (e.g., "search*")
    if (escaped.endsWith('*')) {
      return `"${escaped.slice(0, -1)}"*`;
    }

    // Default: wrap in quotes for exact phrase matching
    return `"${escaped}"`;
  }

  /**
   * Log search query for analytics
   */
  private async logSearchQuery(
    query: string,
    filters: SearchFilters,
    resultCount: number,
    responseTime: number
  ): Promise<void> {
    try {
      const id = crypto.randomUUID();
      const now = Date.now();

      await this.db.$client
        .prepare(`INSERT INTO search_queries (
          id, user_id, query, query_type, filters, result_count,
          response_time_ms, created_at
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)`)
        .bind(
          id,
          filters.userId || null,
          query,
          'full_text',
          JSON.stringify(filters),
          resultCount,
          responseTime,
          now
        )
        .run();

      // Update popular searches
      const existingResult = await this.db.$client
        .prepare(`SELECT id, search_count FROM search_popular WHERE query = ?`)
        .bind(query)
        .first();
      const existing = existingResult as { id: string; search_count: number } | null;

      if (existing) {
        await this.db.$client
          .prepare(`UPDATE search_popular
           SET search_count = search_count + 1,
               last_searched_at = ?,
               updated_at = ?
           WHERE id = ?`)
          .bind(now, now, existing.id)
          .run();
      } else {
        await this.db.$client
          .prepare(`INSERT INTO search_popular (
            id, query, search_count, last_searched_at, updated_at
          ) VALUES (?, ?, 1, ?, ?)`)
          .bind(crypto.randomUUID(), query, now, now)
          .run();
      }
    } catch (error) {
      console.error('Failed to log search query:', error);
      // Don't fail the search if analytics logging fails
    }
  }

  /**
   * Get search suggestions based on partial query
   */
  async getSuggestions(partialQuery: string, limit: number = 10): Promise<string[]> {
    if (!partialQuery || partialQuery.length < 2) {
      return [];
    }

    // Get popular searches that start with the partial query
    const result = await this.db.$client
      .prepare(`SELECT query FROM search_popular
       WHERE query LIKE ? || '%'
       ORDER BY search_count DESC, last_searched_at DESC
       LIMIT ?`)
      .bind(partialQuery, limit)
      .all();
    const suggestions = result.results as { query: string }[];

    return suggestions.map(s => s.query);
  }
}