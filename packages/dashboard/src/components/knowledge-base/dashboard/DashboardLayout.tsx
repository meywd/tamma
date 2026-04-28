/**
 * Dashboard Layout
 *
 * Main layout component for the Knowledge Base dashboard
 * with sidebar navigation and content area.
 */

import React, { type JSX } from 'react';

export interface NavItem {
  id: string;
  label: string;
  description?: string;
}

export interface DashboardLayoutProps {
  title: string;
  navItems: NavItem[];
  activeSection: string;
  onNavigate: (sectionId: string) => void;
  children: React.ReactNode;
}

export function DashboardLayout({
  title,
  navItems,
  activeSection,
  onNavigate,
  children,
}: DashboardLayoutProps): JSX.Element {
  return (
    <div
      data-testid="dashboard-layout"
      style={{
        display: 'flex',
        minHeight: '100vh',
        fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif',
      }}
    >
      {/* Sidebar */}
      <nav
        style={{
          width: '240px',
          backgroundColor: '#1f2937',
          color: '#f9fafb',
          padding: '24px 0',
          flexShrink: 0,
        }}
      >
        <div
          style={{
            padding: '0 20px',
            marginBottom: '32px',
            fontSize: '18px',
            fontWeight: 700,
            letterSpacing: '-0.025em',
          }}
        >
          {title}
        </div>
        <ul style={{ listStyle: 'none', margin: 0, padding: 0 }}>
          {navItems.map((item) => (
            <li key={item.id}>
              <button
                data-testid={`nav-${item.id}`}
                onClick={() => onNavigate(item.id)}
                style={{
                  display: 'block',
                  width: '100%',
                  padding: '10px 20px',
                  border: 'none',
                  background: activeSection === item.id ? '#374151' : 'transparent',
                  color: activeSection === item.id ? '#ffffff' : '#d1d5db',
                  textAlign: 'left',
                  cursor: 'pointer',
                  fontSize: '14px',
                  fontWeight: activeSection === item.id ? 600 : 400,
                  borderLeft: activeSection === item.id ? '3px solid #3b82f6' : '3px solid transparent',
                }}
              >
                {item.label}
              </button>
            </li>
          ))}
        </ul>
      </nav>

      {/* Main content */}
      <main
        style={{
          flex: 1,
          padding: '32px',
          backgroundColor: '#f9fafb',
          overflow: 'auto',
        }}
      >
        {children}
      </main>
    </div>
  );
}
