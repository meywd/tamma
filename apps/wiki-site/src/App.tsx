import { Routes, Route } from 'react-router';
import Layout from './components/Layout';
import MarkdownPage from './components/MarkdownPage';
import HomePage from './components/HomePage';
import EpicsPage from './components/EpicsPage';
import WorkflowsPage from './components/WorkflowsPage';
import StoriesPage from './components/StoriesPage';

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<HomePage />} />
        <Route path="roadmap" element={<MarkdownPage path="roadmap.md" />} />
        <Route path="architecture" element={<MarkdownPage path="architecture.md" />} />
        <Route path="contributing" element={<MarkdownPage path="contributing.md" />} />
        <Route path="epics" element={<EpicsPage />} />
        <Route path="epics/:slug" element={<MarkdownPage prefix="epics" />} />
        <Route path="stories" element={<StoriesPage />} />
        <Route path="stories/:epic/:story" element={<MarkdownPage prefix="stories" />} />
        <Route path="workflows" element={<WorkflowsPage />} />
        <Route path="workflows/:slug" element={<MarkdownPage prefix="workflows" />} />
        <Route path="*" element={<MarkdownPage />} />
      </Route>
    </Routes>
  );
}
