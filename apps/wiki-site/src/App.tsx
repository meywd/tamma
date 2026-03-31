import { Routes, Route } from 'react-router';
import Layout from './components/Layout';
import MarkdownPage from './components/MarkdownPage';
import HomePage from './components/HomePage';

export default function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route index element={<HomePage />} />
        <Route path="roadmap" element={<MarkdownPage path="roadmap.md" />} />
        <Route path="architecture" element={<MarkdownPage path="architecture.md" />} />
        <Route path="contributing" element={<MarkdownPage path="contributing.md" />} />
        <Route path="epics" element={<MarkdownPage path="epics/index.md" />} />
        <Route path="epics/:slug" element={<MarkdownPage prefix="epics" />} />
        <Route path="stories" element={<MarkdownPage path="stories/index.md" />} />
        <Route path="stories/:epic/:story" element={<MarkdownPage prefix="stories" />} />
        <Route path="workflows" element={<MarkdownPage path="workflows/index.md" />} />
        <Route path="workflows/:slug" element={<MarkdownPage prefix="workflows" />} />
        <Route path="*" element={<MarkdownPage />} />
      </Route>
    </Routes>
  );
}
