/**
 * @tamma/dashboard-user entry point. Mounts the React app at #root, wrapped
 * in the root ErrorBoundary (Story 45-2 AC7) so a render throw shows a
 * recoverable error instead of a blank page.
 */

import ReactDOM from 'react-dom/client';
import { App } from './App';
import { ErrorBoundary } from './components/ErrorBoundary';
import './index.css';

const rootElement = document.getElementById('root');
if (rootElement) {
  const root = ReactDOM.createRoot(rootElement);
  root.render(
    <ErrorBoundary>
      <App />
    </ErrorBoundary>,
  );
}
