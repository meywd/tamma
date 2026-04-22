/**
 * App — router tree for the user-facing dashboard SPA.
 *
 *   /login              public
 *   /register           public
 *   /verify-email       public (auto-verifies ?token=)
 *   /                   AuthGuard → AppLayout → DashboardHome
 *   /* (future)         AuthGuard → AppLayout → …
 */

import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { AuthProvider } from './hooks/useAuth';
import { AuthGuard } from './guards/AuthGuard';
import { AppLayout } from './layouts/AppLayout';
import { LoginPage } from './pages/auth/LoginPage';
import { RegisterPage } from './pages/auth/RegisterPage';
import { VerifyEmailPage } from './pages/auth/VerifyEmailPage';
import { DashboardHome } from './pages/DashboardHome';

export function App(): JSX.Element {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          <Route path="/register" element={<RegisterPage />} />
          <Route path="/verify-email" element={<VerifyEmailPage />} />
          <Route
            element={
              <AuthGuard>
                <AppLayout />
              </AuthGuard>
            }
          >
            <Route path="/" element={<DashboardHome />} />
          </Route>
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  );
}
