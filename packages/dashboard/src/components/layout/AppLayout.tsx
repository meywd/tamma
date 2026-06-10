
import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar.js';
import { NavHeader } from './NavHeader.js';

import type { JSX } from "react";

export function AppLayout(): JSX.Element {
  return (
    <>
      <NavHeader />
      <div className="flex min-h-screen font-sans" style={{ paddingTop: 48 }}>
        <Sidebar />
        <main id="main-content" className="flex-1 p-8 bg-gray-50 overflow-auto dark:bg-gray-900">
          <Outlet />
        </main>
      </div>
    </>
  );
}
