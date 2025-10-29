/**
 * @tamma/dashboard
 * React observability dashboard for the Tamma platform
 */

import React from 'react';
import ReactDOM from 'react-dom/client';

function App(): JSX.Element {
  return (
    <div>
      <h1>Tamma Dashboard</h1>
      <p>Dashboard implementation coming soon (Epic 4)</p>
    </div>
  );
}

const rootElement = document.getElementById('root');
if (rootElement) {
  const root = ReactDOM.createRoot(rootElement);
  root.render(<App />);
}
