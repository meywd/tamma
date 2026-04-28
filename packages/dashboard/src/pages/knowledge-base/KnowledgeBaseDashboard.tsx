/**
 * Knowledge Base Dashboard
 *
 * Main dashboard page integrating all Knowledge Base management features.
 * Provides navigation between sections: overview, index management,
 * vector DB, RAG pipeline, MCP servers, context testing, and analytics.
 */

import { useState, useEffect, useCallback, type JSX } from 'react';
import { DashboardLayout } from '../../components/knowledge-base/dashboard/DashboardLayout.js';
import { QuickStatusPanel } from '../../components/knowledge-base/dashboard/QuickStatusPanel.js';
import { IndexStatusCard } from '../../components/knowledge-base/index-management/IndexStatusCard.js';
import { IndexingHistoryTable } from '../../components/knowledge-base/index-management/IndexingHistoryTable.js';
import { IndexConfigEditor } from '../../components/knowledge-base/index-management/IndexConfigEditor.js';
import { CollectionList } from '../../components/knowledge-base/vector-db/CollectionList.js';
import { CollectionStats } from '../../components/knowledge-base/vector-db/CollectionStats.js';
import { VectorSearchTest } from '../../components/knowledge-base/vector-db/VectorSearchTest.js';
import { StorageMetrics } from '../../components/knowledge-base/vector-db/StorageMetrics.js';
import { RAGConfigPanel } from '../../components/knowledge-base/rag/RAGConfigPanel.js';
import { RAGTestInterface } from '../../components/knowledge-base/rag/RAGTestInterface.js';
import { RAGMetricsChart } from '../../components/knowledge-base/rag/RAGMetricsChart.js';
import { MCPServerList } from '../../components/knowledge-base/mcp/MCPServerList.js';
import { ToolList } from '../../components/knowledge-base/mcp/ToolList.js';
import { ToolInvokePanel } from '../../components/knowledge-base/mcp/ToolInvokePanel.js';
import { ServerLogViewer } from '../../components/knowledge-base/mcp/ServerLogViewer.js';
import { ContextTestInterface } from '../../components/knowledge-base/context-testing/ContextTestInterface.js';
import { UsageChart } from '../../components/knowledge-base/analytics/UsageChart.js';
import { QualityMetrics } from '../../components/knowledge-base/analytics/QualityMetrics.js';
import { CostBreakdown } from '../../components/knowledge-base/analytics/CostBreakdown.js';
import { TokenUsageReport } from '../../components/knowledge-base/analytics/TokenUsageReport.js';
import { useIndexStatus } from '../../hooks/knowledge-base/useIndexStatus.js';
import { useVectorDB } from '../../hooks/knowledge-base/useVectorDB.js';
import { useRAGConfig } from '../../hooks/knowledge-base/useRAGConfig.js';
import { useMCPServers } from '../../hooks/knowledge-base/useMCPServers.js';
import { useContextTest } from '../../hooks/knowledge-base/useContextTest.js';
import { useKBAnalytics } from '../../hooks/knowledge-base/useKBAnalytics.js';
import type { MCPTool } from '@tamma/shared';

const NAV_ITEMS = [
  { id: 'overview', label: 'Overview', description: 'Quick status overview' },
  { id: 'index', label: 'Index Management', description: 'Codebase indexing' },
  { id: 'vector-db', label: 'Vector Database', description: 'Collections & search' },
  { id: 'rag', label: 'RAG Pipeline', description: 'Configuration & testing' },
  { id: 'mcp', label: 'MCP Servers', description: 'Server management' },
  { id: 'context-test', label: 'Context Test', description: 'Interactive testing' },
  { id: 'analytics', label: 'Analytics', description: 'Usage & costs' },
];

export function KnowledgeBaseDashboard(): JSX.Element {
  const [activeSection, setActiveSection] = useState('overview');
  const [selectedLogServer, setSelectedLogServer] = useState<string | null>(null);
  const [selectedTool, setSelectedTool] = useState<MCPTool | null>(null);

  // Hooks
  const index = useIndexStatus();
  const vectorDB = useVectorDB();
  const rag = useRAGConfig();
  const mcp = useMCPServers();
  const contextTest = useContextTest();
  const analytics = useKBAnalytics();

  // Initial data loads
  useEffect(() => {
    void vectorDB.loadCollections();
    void mcp.loadServers();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (activeSection === 'rag') {
      void rag.loadConfig();
      void rag.loadMetrics();
    }
    if (activeSection === 'index' && index.config === null) {
      void index.loadConfig();
      void index.loadHistory();
    }
    if (activeSection === 'vector-db') {
      void vectorDB.loadStorageUsage();
    }
    if (activeSection === 'analytics') {
      void analytics.loadAll();
    }
  }, [activeSection]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleViewLogs = useCallback((serverName: string) => {
    setSelectedLogServer(serverName);
    void mcp.loadLogs(serverName);
  }, [mcp]);

  const handleViewTools = useCallback((serverName: string) => {
    void mcp.loadTools(serverName);
    setSelectedTool(null);
  }, [mcp]);

  const handleInvokeTool = useCallback((tool: MCPTool) => {
    setSelectedTool(tool);
  }, []);

  const renderSection = () => {
    switch (activeSection) {
      case 'overview':
        return (
          <div>
            <h2 style={{ margin: '0 0 24px 0', fontSize: '20px', fontWeight: 700 }}>Knowledge Base</h2>
            <QuickStatusPanel
              indexStatus={index.status}
              vectorCount={vectorDB.collections.reduce((sum, c) => sum + c.vectorCount, 0)}
              ragMetrics={rag.metrics}
              mcpServers={mcp.servers}
              onNavigate={setActiveSection}
            />
          </div>
        );

      case 'index':
        return (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
            <h2 style={{ margin: 0, fontSize: '20px', fontWeight: 700 }}>Index Management</h2>
            {index.status && (
              <IndexStatusCard
                status={index.status}
                onTriggerIndex={() => void index.triggerIndex()}
                onCancelIndex={() => void index.cancelIndex()}
              />
            )}
            <div style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
              <h3 style={{ margin: '0 0 12px 0', fontSize: '16px', fontWeight: 600 }}>Indexing History</h3>
              <IndexingHistoryTable history={index.history} />
            </div>
            {index.config && (
              <div style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
                <IndexConfigEditor
                  config={index.config}
                  onSave={(cfg) => void index.updateConfig(cfg)}
                />
              </div>
            )}
          </div>
        );

      case 'vector-db':
        return (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
            <h2 style={{ margin: 0, fontSize: '20px', fontWeight: 700 }}>Vector Database</h2>
            <div style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
              <CollectionList
                collections={vectorDB.collections}
                onSelect={(name) => void vectorDB.loadCollectionStats(name)}
                onCreate={(name, dims) => void vectorDB.createCollection(name, dims)}
                onDelete={(name) => void vectorDB.deleteCollection(name)}
              />
            </div>
            {vectorDB.selectedStats && (
              <CollectionStats stats={vectorDB.selectedStats} />
            )}
            {vectorDB.storageUsage && (
              <StorageMetrics usage={vectorDB.storageUsage} />
            )}
            <div style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
              <VectorSearchTest
                collections={vectorDB.collections.map((c) => c.name)}
                onSearch={(q, c, k) => vectorDB.search(c, q, k)}
                results={vectorDB.searchResults}
                loading={vectorDB.loading}
              />
            </div>
          </div>
        );

      case 'rag':
        return (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
            <h2 style={{ margin: 0, fontSize: '20px', fontWeight: 700 }}>RAG Pipeline</h2>
            {rag.config && (
              <div style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
                <RAGConfigPanel
                  config={rag.config}
                  onSave={(cfg) => void rag.updateConfig(cfg)}
                />
              </div>
            )}
            {rag.metrics && (
              <RAGMetricsChart metrics={rag.metrics} />
            )}
            <div style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
              <RAGTestInterface
                onTest={(query, opts) => rag.testQuery(query, opts)}
                result={rag.testResult}
                loading={rag.loading}
              />
            </div>
          </div>
        );

      case 'mcp':
        return (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
            <h2 style={{ margin: 0, fontSize: '20px', fontWeight: 700 }}>MCP Servers</h2>
            <MCPServerList
              servers={mcp.servers}
              onStart={(name) => void mcp.startServer(name)}
              onStop={(name) => void mcp.stopServer(name)}
              onRestart={(name) => void mcp.restartServer(name)}
              onViewTools={handleViewTools}
              onViewLogs={handleViewLogs}
            />
            {mcp.tools.length > 0 && (
              <div style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
                <ToolList tools={mcp.tools} onInvoke={handleInvokeTool} />
              </div>
            )}
            {selectedTool && (
              <ToolInvokePanel
                tool={selectedTool}
                onInvoke={(serverName, toolName, args) => mcp.invokeTool(serverName, toolName, args)}
                result={mcp.invokeResult}
                loading={mcp.loading}
              />
            )}
            {selectedLogServer && (
              <div style={{ border: '1px solid #e5e7eb', borderRadius: '8px', padding: '20px', backgroundColor: '#fff' }}>
                <ServerLogViewer
                  serverName={selectedLogServer}
                  logs={mcp.logs}
                  onRefresh={() => void mcp.loadLogs(selectedLogServer)}
                />
              </div>
            )}
          </div>
        );

      case 'context-test':
        return (
          <div>
            <ContextTestInterface
              onTest={contextTest.testContext}
              onFeedback={(requestId, chunkId, rating) => {
                void contextTest.submitFeedback(requestId, [{ chunkId, rating }]);
              }}
              result={contextTest.result}
              loading={contextTest.loading}
            />
          </div>
        );

      case 'analytics':
        return (
          <div style={{ display: 'flex', flexDirection: 'column', gap: '24px' }}>
            <h2 style={{ margin: 0, fontSize: '20px', fontWeight: 700 }}>Analytics & Reporting</h2>
            {analytics.usage && <UsageChart analytics={analytics.usage} />}
            {analytics.usage && <TokenUsageReport analytics={analytics.usage} />}
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '24px' }}>
              {analytics.quality && <QualityMetrics analytics={analytics.quality} />}
              {analytics.costs && <CostBreakdown analytics={analytics.costs} />}
            </div>
          </div>
        );

      default:
        return null;
    }
  };

  return (
    <DashboardLayout
      title="TAMMA KB"
      navItems={NAV_ITEMS}
      activeSection={activeSection}
      onNavigate={setActiveSection}
    >
      {/* Error display */}
      {(index.error || vectorDB.error || rag.error || mcp.error || contextTest.error || analytics.error) && (
        <div style={{
          padding: '12px 16px',
          backgroundColor: '#fef2f2',
          borderRadius: '8px',
          marginBottom: '16px',
          color: '#991b1b',
          fontSize: '14px',
        }}>
          {index.error || vectorDB.error || rag.error || mcp.error || contextTest.error || analytics.error}
        </div>
      )}

      {renderSection()}
    </DashboardLayout>
  );
}
