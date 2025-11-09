-- Tamma Documentation Review Database Schema
-- Platform: Cloudflare D1 (SQLite)
-- Version: 1.0.0

-- Users table
CREATE TABLE IF NOT EXISTS users (
  id TEXT PRIMARY KEY NOT NULL,              -- UUID v7
  email TEXT UNIQUE NOT NULL,
  name TEXT NOT NULL,
  avatar_url TEXT,
  role TEXT DEFAULT 'viewer' NOT NULL,       -- viewer, reviewer, admin
  created_at INTEGER NOT NULL,               -- Unix timestamp (ms)
  updated_at INTEGER NOT NULL
);

CREATE INDEX idx_users_email ON users(email);
CREATE INDEX idx_users_role ON users(role);

-- Comments table (inline and document-level)
CREATE TABLE IF NOT EXISTS comments (
  id TEXT PRIMARY KEY NOT NULL,              -- UUID v7
  doc_path TEXT NOT NULL,                    -- 'docs/PRD.md'
  line_number INTEGER,                       -- NULL for doc-level comments
  line_content TEXT,                         -- Context snapshot of the line
  content TEXT NOT NULL,                     -- Markdown-supported comment
  user_id TEXT NOT NULL,
  parent_id TEXT,                            -- For threaded replies
  resolved BOOLEAN DEFAULT FALSE NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  FOREIGN KEY (parent_id) REFERENCES comments(id) ON DELETE CASCADE
);

CREATE INDEX idx_comments_doc_path ON comments(doc_path);
CREATE INDEX idx_comments_user_id ON comments(user_id);
CREATE INDEX idx_comments_line_number ON comments(line_number);
CREATE INDEX idx_comments_parent_id ON comments(parent_id);
CREATE INDEX idx_comments_resolved ON comments(resolved);

-- Edit suggestions
CREATE TABLE IF NOT EXISTS suggestions (
  id TEXT PRIMARY KEY NOT NULL,              -- UUID v7
  doc_path TEXT NOT NULL,
  line_start INTEGER NOT NULL,
  line_end INTEGER NOT NULL,
  original_text TEXT NOT NULL,
  suggested_text TEXT NOT NULL,
  description TEXT,                          -- Markdown-supported rationale
  status TEXT DEFAULT 'pending' NOT NULL,    -- pending, approved, rejected
  reviewed_by TEXT,                          -- User ID of reviewer
  reviewed_at INTEGER,
  user_id TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE,
  FOREIGN KEY (reviewed_by) REFERENCES users(id) ON DELETE SET NULL
);

CREATE INDEX idx_suggestions_doc_path ON suggestions(doc_path);
CREATE INDEX idx_suggestions_status ON suggestions(status);
CREATE INDEX idx_suggestions_user_id ON suggestions(user_id);
CREATE INDEX idx_suggestions_reviewed_by ON suggestions(reviewed_by);

-- Discussions (document-level threads)
CREATE TABLE IF NOT EXISTS discussions (
  id TEXT PRIMARY KEY NOT NULL,              -- UUID v7
  doc_path TEXT NOT NULL,
  title TEXT NOT NULL,
  description TEXT,
  status TEXT DEFAULT 'open' NOT NULL,       -- open, resolved, closed
  user_id TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE INDEX idx_discussions_doc_path ON discussions(doc_path);
CREATE INDEX idx_discussions_status ON discussions(status);
CREATE INDEX idx_discussions_user_id ON discussions(user_id);

-- Discussion messages
CREATE TABLE IF NOT EXISTS discussion_messages (
  id TEXT PRIMARY KEY NOT NULL,              -- UUID v7
  discussion_id TEXT NOT NULL,
  content TEXT NOT NULL,                     -- Markdown-supported
  user_id TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  FOREIGN KEY (discussion_id) REFERENCES discussions(id) ON DELETE CASCADE,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE INDEX idx_discussion_messages_discussion_id ON discussion_messages(discussion_id);
CREATE INDEX idx_discussion_messages_user_id ON discussion_messages(user_id);
CREATE INDEX idx_discussion_messages_created_at ON discussion_messages(created_at);

-- Document metadata (cached)
CREATE TABLE IF NOT EXISTS document_metadata (
  doc_path TEXT PRIMARY KEY NOT NULL,
  title TEXT NOT NULL,
  description TEXT,
  category TEXT,                             -- main, epic, story, research, retrospective
  epic_id TEXT,                              -- For stories
  story_id TEXT,                             -- For tasks
  word_count INTEGER,
  line_count INTEGER,
  last_modified INTEGER NOT NULL,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);

CREATE INDEX idx_document_metadata_category ON document_metadata(category);
CREATE INDEX idx_document_metadata_epic_id ON document_metadata(epic_id);
CREATE INDEX idx_document_metadata_story_id ON document_metadata(story_id);

-- Activity log (for future audit trail)
CREATE TABLE IF NOT EXISTS activity_log (
  id TEXT PRIMARY KEY NOT NULL,              -- UUID v7
  user_id TEXT NOT NULL,
  action TEXT NOT NULL,                      -- comment_created, suggestion_approved, etc.
  resource_type TEXT NOT NULL,               -- comment, suggestion, discussion
  resource_id TEXT NOT NULL,
  metadata TEXT,                             -- JSON string with additional context
  created_at INTEGER NOT NULL,
  FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
);

CREATE INDEX idx_activity_log_user_id ON activity_log(user_id);
CREATE INDEX idx_activity_log_resource_type ON activity_log(resource_type);
CREATE INDEX idx_activity_log_resource_id ON activity_log(resource_id);
CREATE INDEX idx_activity_log_created_at ON activity_log(created_at);

-- Insert default admin user (for initial setup)
-- Replace with actual user after first login
INSERT OR IGNORE INTO users (id, email, name, role, created_at, updated_at)
VALUES ('00000000-0000-0000-0000-000000000000', 'admin@example.com', 'Admin User', 'admin',
        strftime('%s', 'now') * 1000, strftime('%s', 'now') * 1000);
