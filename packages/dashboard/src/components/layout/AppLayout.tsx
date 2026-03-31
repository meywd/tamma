
import { Outlet } from 'react-router-dom';
import { Sidebar } from './Sidebar.js';
import { NavHeader } from './NavHeader.js';

export function AppLayout(): JSX.Element {
  return (
    <>
      <NavHeader />
      <div className="flex min-h-screen font-sans" style={{ paddingTop: 48 }}>
        <Sidebar />
        <main className="flex-1 p-8 bg-gray-50 overflow-auto">
          <Outlet />
        </main>
      </div>
    </>
  );
}
