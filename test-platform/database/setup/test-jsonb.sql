-- JSONB Functionality Test Script
-- Story: 1.1 Database Schema Migration System
-- Task: 1 - Database Setup and Configuration
-- Subtask: 1.2 - Enable JSONB extension and verify functionality

-- Connect to test_platform database
\c test_platform

-- Display PostgreSQL version
SELECT version();

-- ============================================
-- STEP 1: Enable Required Extensions
-- ============================================
\echo '----------------------------------------'
\echo 'Installing JSONB-related extensions...'
\echo '----------------------------------------'

CREATE EXTENSION IF NOT EXISTS btree_gin;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";  -- For UUID generation

-- List installed extensions
SELECT extname, extversion
FROM pg_extension
WHERE extname IN ('btree_gin', 'btree_gist', 'uuid-ossp')
ORDER BY extname;

-- ============================================
-- STEP 2: Create Test Tables with JSONB
-- ============================================
\echo '----------------------------------------'
\echo 'Creating JSONB test tables...'
\echo '----------------------------------------'

-- Drop existing test tables if they exist
DROP TABLE IF EXISTS test_jsonb_performance CASCADE;
DROP TABLE IF EXISTS test_jsonb CASCADE;

-- Create main test table
CREATE TABLE test_jsonb (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    data JSONB NOT NULL,
    metadata JSONB DEFAULT '{}',
    tags TEXT[] DEFAULT '{}',
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Create performance test table
CREATE TABLE test_jsonb_performance (
    id SERIAL PRIMARY KEY,
    document JSONB NOT NULL,
    category VARCHAR(50),
    score NUMERIC(5,2),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

\echo 'Tables created successfully!'

-- ============================================
-- STEP 3: Insert Test Data
-- ============================================
\echo '----------------------------------------'
\echo 'Inserting test data...'
\echo '----------------------------------------'

-- Insert various JSONB structures
INSERT INTO test_jsonb (data, metadata, tags) VALUES
-- Simple object
('{"name": "Simple Test", "value": 123, "active": true}',
 '{"source": "manual", "version": 1}',
 '{"simple", "test"}'),

-- Nested object
('{"user": {"name": "John Doe", "email": "john@example.com", "age": 30}, "settings": {"theme": "dark", "notifications": {"email": true, "push": false}}}',
 '{"source": "api", "version": 2}',
 '{"user", "nested"}'),

-- Array of objects
('{"items": [{"id": 1, "name": "Item 1", "price": 10.50}, {"id": 2, "name": "Item 2", "price": 25.00}], "total": 35.50}',
 '{"source": "cart", "version": 1}',
 '{"array", "items"}'),

-- Mixed types
('{"string": "text", "number": 42, "boolean": false, "null_value": null, "array": [1, 2, 3], "object": {"key": "value"}}',
 '{"source": "test", "version": 1}',
 '{"mixed", "types"}'),

-- Large nested structure
('{"company": {"name": "Tech Corp", "employees": [{"id": 1, "name": "Alice", "department": "Engineering", "skills": ["Python", "JavaScript", "SQL"]}, {"id": 2, "name": "Bob", "department": "Marketing", "skills": ["SEO", "Content", "Analytics"]}], "locations": {"headquarters": {"city": "San Francisco", "country": "USA"}, "branches": [{"city": "London", "country": "UK"}, {"city": "Tokyo", "country": "Japan"}]}}}',
 '{"source": "company", "version": 3}',
 '{"company", "complex"}');

-- Insert performance test data
INSERT INTO test_jsonb_performance (document, category, score)
SELECT
    jsonb_build_object(
        'id', i,
        'title', 'Document ' || i,
        'content', 'This is test content for document ' || i,
        'tags', jsonb_build_array('tag' || (i % 5), 'category' || (i % 3)),
        'metrics', jsonb_build_object(
            'views', (random() * 1000)::int,
            'likes', (random() * 100)::int,
            'shares', (random() * 50)::int
        ),
        'metadata', jsonb_build_object(
            'author', 'Author ' || (i % 10),
            'department', CASE i % 4
                WHEN 0 THEN 'Engineering'
                WHEN 1 THEN 'Marketing'
                WHEN 2 THEN 'Sales'
                ELSE 'Support'
            END,
            'priority', CASE
                WHEN i % 10 = 0 THEN 'high'
                WHEN i % 5 = 0 THEN 'medium'
                ELSE 'low'
            END
        )
    ),
    CASE i % 4
        WHEN 0 THEN 'technical'
        WHEN 1 THEN 'business'
        WHEN 2 THEN 'marketing'
        ELSE 'general'
    END,
    (random() * 100)::numeric(5,2)
FROM generate_series(1, 1000) AS i;

\echo 'Test data inserted successfully!'

-- ============================================
-- STEP 4: Test JSONB Operations
-- ============================================
\echo '----------------------------------------'
\echo 'Testing JSONB operations...'
\echo '----------------------------------------'

-- Test 1: Basic key extraction
\echo 'Test 1: Extracting top-level keys'
SELECT
    data->>'name' as name,
    data->'value' as value,
    jsonb_typeof(data->'value') as value_type
FROM test_jsonb
WHERE data ? 'name'
LIMIT 3;

-- Test 2: Nested key extraction
\echo 'Test 2: Extracting nested values'
SELECT
    data->'user'->>'name' as user_name,
    data->'settings'->'notifications'->>'email' as email_notifications
FROM test_jsonb
WHERE data ? 'user';

-- Test 3: Array operations
\echo 'Test 3: Working with arrays'
SELECT
    jsonb_array_length(data->'items') as item_count,
    data->'items'->0->>'name' as first_item,
    data->'items'->1->>'price' as second_item_price
FROM test_jsonb
WHERE data ? 'items';

-- Test 4: Path operations
\echo 'Test 4: Using path operators'
SELECT
    data #> '{company,name}' as company_name,
    data #> '{company,employees,0,name}' as first_employee,
    data #>> '{company,locations,headquarters,city}' as hq_city
FROM test_jsonb
WHERE data @> '{"company": {}}';

-- Test 5: Containment queries
\echo 'Test 5: Containment operators'
SELECT COUNT(*) as count_with_engineering
FROM test_jsonb_performance
WHERE document @> '{"metadata": {"department": "Engineering"}}';

SELECT COUNT(*) as count_high_priority
FROM test_jsonb_performance
WHERE document @> '{"metadata": {"priority": "high"}}';

-- Test 6: Existence operators
\echo 'Test 6: Key existence checks'
SELECT
    COUNT(*) FILTER (WHERE document ? 'metrics') as has_metrics,
    COUNT(*) FILTER (WHERE document ?| array['tags', 'metadata']) as has_tags_or_metadata,
    COUNT(*) FILTER (WHERE document ?& array['id', 'title', 'content']) as has_all_required
FROM test_jsonb_performance;

-- ============================================
-- STEP 5: Create and Test Indexes
-- ============================================
\echo '----------------------------------------'
\echo 'Creating JSONB indexes...'
\echo '----------------------------------------'

-- GIN index for general JSONB queries
CREATE INDEX idx_test_jsonb_data_gin ON test_jsonb USING GIN (data);
CREATE INDEX idx_test_jsonb_metadata_gin ON test_jsonb USING GIN (metadata);

-- GIN index for performance table
CREATE INDEX idx_performance_document_gin ON test_jsonb_performance USING GIN (document);

-- Specific path indexes for frequently queried fields
CREATE INDEX idx_performance_department ON test_jsonb_performance USING BTREE ((document->'metadata'->>'department'));
CREATE INDEX idx_performance_priority ON test_jsonb_performance USING BTREE ((document->'metadata'->>'priority'));

-- Expression index for calculated values
CREATE INDEX idx_performance_view_count ON test_jsonb_performance USING BTREE (((document->'metrics'->>'views')::int));

\echo 'Indexes created successfully!'

-- ============================================
-- STEP 6: Performance Testing
-- ============================================
\echo '----------------------------------------'
\echo 'Testing query performance...'
\echo '----------------------------------------'

-- Enable timing
\timing on

-- Query 1: Without index (force sequential scan for comparison)
SET enable_seqscan = ON;
SET enable_indexscan = OFF;
SET enable_bitmapscan = OFF;

\echo 'Query without index (sequential scan):'
EXPLAIN (ANALYZE, BUFFERS)
SELECT COUNT(*)
FROM test_jsonb_performance
WHERE document @> '{"metadata": {"department": "Engineering"}}';

-- Query 2: With index
SET enable_seqscan = OFF;
SET enable_indexscan = ON;
SET enable_bitmapscan = ON;

\echo 'Query with GIN index:'
EXPLAIN (ANALYZE, BUFFERS)
SELECT COUNT(*)
FROM test_jsonb_performance
WHERE document @> '{"metadata": {"department": "Engineering"}}';

-- Reset settings
RESET enable_seqscan;
RESET enable_indexscan;
RESET enable_bitmapscan;

\timing off

-- ============================================
-- STEP 7: Advanced JSONB Features
-- ============================================
\echo '----------------------------------------'
\echo 'Testing advanced JSONB features...'
\echo '----------------------------------------'

-- Test JSONB aggregation
\echo 'JSONB Aggregation:'
SELECT
    category,
    jsonb_agg(
        jsonb_build_object(
            'id', document->'id',
            'title', document->'title',
            'views', document->'metrics'->'views'
        ) ORDER BY (document->'metrics'->>'views')::int DESC
    ) as top_documents
FROM test_jsonb_performance
WHERE (document->'metrics'->>'views')::int > 500
GROUP BY category
LIMIT 3;

-- Test JSONB modification
\echo 'JSONB Modification Functions:'
SELECT
    jsonb_set(
        data,
        '{user,status}',
        '"active"'
    ) as data_with_status
FROM test_jsonb
WHERE data ? 'user'
LIMIT 1;

-- Test JSONB concatenation
\echo 'JSONB Concatenation:'
SELECT
    data || '{"new_field": "new_value"}' as extended_data
FROM test_jsonb
LIMIT 1;

-- Test JSONB deletion
\echo 'JSONB Key Deletion:'
SELECT
    data - 'value' as data_without_value,
    data #- '{user,age}' as data_without_age
FROM test_jsonb
WHERE data ? 'user'
LIMIT 1;

-- ============================================
-- STEP 8: Summary Statistics
-- ============================================
\echo '----------------------------------------'
\echo 'Summary Statistics'
\echo '----------------------------------------'

SELECT
    'test_jsonb' as table_name,
    COUNT(*) as row_count,
    pg_size_pretty(pg_relation_size('test_jsonb')) as table_size,
    pg_size_pretty(pg_indexes_size('test_jsonb')) as index_size
UNION ALL
SELECT
    'test_jsonb_performance',
    COUNT(*),
    pg_size_pretty(pg_relation_size('test_jsonb_performance')),
    pg_size_pretty(pg_indexes_size('test_jsonb_performance'))
FROM test_jsonb_performance;

-- List all indexes
\echo 'Created Indexes:'
SELECT
    schemaname,
    tablename,
    indexname,
    pg_size_pretty(pg_relation_size(indexname::regclass)) as index_size
FROM pg_indexes
WHERE tablename IN ('test_jsonb', 'test_jsonb_performance')
ORDER BY tablename, indexname;

-- ============================================
-- STEP 9: Cleanup (Optional)
-- ============================================
\echo '----------------------------------------'
\echo 'Test completed successfully!'
\echo 'To cleanup test data, uncomment and run:'
\echo '-- DROP TABLE test_jsonb CASCADE;'
\echo '-- DROP TABLE test_jsonb_performance CASCADE;'
\echo '----------------------------------------'