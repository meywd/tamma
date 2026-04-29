/**
 * App — router tree for the user-facing dashboard SPA.
 *
 *   /login                                public
 *   /register                             public
 *   /verify-email                         public (auto-verifies ?token=)
 *   /                                     AuthGuard → AppLayout → DashboardHome
 *   /alerts                               AuthGuard → AppLayout → TenantAlertFeed
 *   /settings/alerts                      AuthGuard → TenantAdminGuard → AppLayout → TenantAlertChannels
 *   /onboarding/platforms                 AuthGuard → TenantAdminGuard → AppLayout → PlatformPicker          (Story 31-9)
 *   /onboarding/platforms/:kind/install   AuthGuard → TenantAdminGuard → AppLayout → PlatformInstallForm     (Story 31-9)
 *   /settings/platforms                   AuthGuard → TenantAdminGuard → AppLayout → ConnectedPlatforms      (Story 31-9)
 *   /* (future)                           AuthGuard → AppLayout → …
 */

import { BrowserRouter, Routes, Route } from 'react-router-dom';
import { AuthProvider } from './hooks/useAuth';
import { AuthGuard } from './guards/AuthGuard';
import { TenantAdminGuard } from './guards/TenantAdminGuard';
import { AppLayout } from './layouts/AppLayout';
import { LoginPage } from './pages/auth/LoginPage';
import { RegisterPage } from './pages/auth/RegisterPage';
import { VerifyEmailPage } from './pages/auth/VerifyEmailPage';
import { DashboardHome } from './pages/DashboardHome';
import { TenantAlertFeed } from './pages/alerts/TenantAlertFeed';
import { TenantAlertChannels } from './pages/alerts/TenantAlertChannels';
import { PlatformPicker } from './pages/onboarding/PlatformPicker';
import { PlatformInstallForm } from './pages/onboarding/PlatformInstallForm';
import { ConnectedPlatforms } from './pages/settings/ConnectedPlatforms';

import type { JSX } from "react";

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
            <Route path="/alerts" element={<TenantAlertFeed />} />
            <Route
              path="/settings/alerts"
              element={
                <TenantAdminGuard>
                  <TenantAlertChannels />
                </TenantAdminGuard>
              }
            />
            <Route
              path="/onboarding/platforms"
              element={
                <TenantAdminGuard>
                  <PlatformPicker />
                </TenantAdminGuard>
              }
            />
            <Route
              path="/onboarding/platforms/:kind/install"
              element={
                <TenantAdminGuard>
                  <PlatformInstallForm />
                </TenantAdminGuard>
              }
            />
            <Route
              path="/settings/platforms"
              element={
                <TenantAdminGuard>
                  <ConnectedPlatforms />
                </TenantAdminGuard>
              }
            />
          </Route>
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  );
}
