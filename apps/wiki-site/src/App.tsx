import { Routes, Route } from 'react-router';
import Layout from './components/Layout';
import MarkdownPage from './components/MarkdownPage';
import HomePage from './components/HomePage';
import RoadmapPage from './components/RoadmapPage';
import ArchitecturePage from './components/ArchitecturePage';
import EpicsPage from './components/EpicsPage';
import EpicDetailPage from './components/EpicDetailPage';
import WorkflowsPage from './components/WorkflowsPage';
import WorkflowDetailPage from './components/WorkflowDetailPage';
import StoriesPage from './components/StoriesPage';
import StoryDetailPage from './components/StoryDetailPage';

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<HomePage />} />
        <Route path="roadmap" element={<RoadmapPage />} />
        <Route path="architecture" element={<ArchitecturePage />} />
        <Route path="contributing" element={<MarkdownPage path="contributing.md" />} />
        <Route path="epics" element={<EpicsPage />} />
        <Route path="epics/:slug" element={<EpicDetailPage />} />
        <Route path="stories" element={<StoriesPage />} />
        <Route path="stories/:epic/:story" element={<StoryDetailPage />} />
        <Route path="workflows" element={<WorkflowsPage />} />
        <Route path="workflows/:slug" element={<WorkflowDetailPage />} />
        <Route path="*" element={<MarkdownPage />} />
      </Route>
    </Routes>
  );
}
