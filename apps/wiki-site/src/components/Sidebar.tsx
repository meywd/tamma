import { useEffect, useRef, useState } from 'react';
import { NavLink, useLocation } from 'react-router';

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

interface SidebarProps {
  /** Controls visibility on mobile (< md). On md+ the sidebar is always visible. */
  mobileOpen?: boolean;
  /** Called when the mobile drawer requests to close (Escape, close button, route change). */
  onCloseMobile?: () => void;
}

export default function Sidebar({ mobileOpen = false, onCloseMobile }: SidebarProps) {
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [search, setSearch] = useState('');
  const location = useLocation();
  const closeButtonRef = useRef<HTMLButtonElement | null>(null);

  // Focus the close button when the drawer opens (a11y: keyboard users land inside)
  useEffect(() => {
    if (mobileOpen) {
      closeButtonRef.current?.focus();
    }
  }, [mobileOpen]);

  useEffect(() => {
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => {
        setManifest(data);
        // Auto-expand current section
        const current = data.find((e) => e.path === location.pathname);
        if (current?.section) {
          setExpanded((prev) => ({ ...prev, [current.section]: true }));
        }
      })
      .catch(() => {});
  }, []);

  // Group by section
  const epicPages: ManifestEntry[] = [];
  const workflowPages: ManifestEntry[] = [];
  const storyEpics = new Map<string, ManifestEntry[]>();

  for (const entry of manifest) {
    if (search && !entry.title.toLowerCase().includes(search.toLowerCase())) continue;

    if (entry.section === 'Epics') {
      epicPages.push(entry);
    } else if (entry.section === 'Workflows') {
      workflowPages.push(entry);
    } else if (entry.section.startsWith('Epic ')) {
      if (!storyEpics.has(entry.section)) storyEpics.set(entry.section, []);
      storyEpics.get(entry.section)!.push(entry);
    }
  }

  // Sort story epics numerically
  const sortedStoryEpics = [...storyEpics.entries()].sort((a, b) => {
    const numA = parseFloat(a[0].replace('Epic ', '').replace('-', '.'));
    const numB = parseFloat(b[0].replace('Epic ', '').replace('-', '.'));
    return numA - numB;
  });

  const toggle = (s: string) => setExpanded((prev) => ({ ...prev, [s]: !prev[s] }));

  const navClass = ({ isActive }: { isActive: boolean }) =>
    `block px-3 py-1.5 rounded-md text-[13px] leading-snug transition-colors ${
      isActive
        ? 'bg-white/10 text-white font-medium'
        : 'text-zinc-400 hover:text-zinc-200 hover:bg-white/5'
    }`;

  const sectionHeader = (label: string, key: string, count?: number) => (
    <button
      onClick={() => toggle(key)}
      className="flex items-center justify-between w-full px-3 py-2 text-[11px] font-semibold uppercase tracking-widest text-zinc-500 hover:text-zinc-400 transition-colors"
    >
      <span className="truncate">{label}{count ? ` (${count})` : ''}</span>
      <svg
        className={`w-3 h-3 shrink-0 transition-transform ${expanded[key] ? 'rotate-90' : ''}`}
        fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
      >
        <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
      </svg>
    </button>
  );

  const renderItems = (items: ManifestEntry[], max = 50) => (
    <div className="flex flex-col gap-0.5 mb-1">
      {items.slice(0, max).map((item) => (
        <NavLink key={item.path} to={item.path} className={navClass}>
          {item.title.length > 45 ? item.title.slice(0, 45) + '...' : item.title}
        </NavLink>
      ))}
      {items.length > max && (
        <span className="px-3 py-1 text-[11px] text-zinc-600">+{items.length - max} more</span>
      )}
    </div>
  );

  return (
    <aside
      id="site-sidebar"
      aria-label="Site navigation"
      aria-hidden={!mobileOpen ? undefined : false}
      className={[
        // Mobile: fixed drawer sliding in from the left
        'fixed inset-y-0 left-0 z-40 w-[82vw] max-w-[300px] h-screen overflow-y-auto bg-[#111113] border-r border-zinc-800/60 flex flex-col',
        'transition-transform duration-200 ease-out will-change-transform',
        mobileOpen ? 'translate-x-0' : '-translate-x-full',
        // Desktop (md+): pinned inline, always visible, no translation
        'md:static md:translate-x-0 md:w-[260px] md:max-w-none md:shrink-0 md:z-auto',
      ].join(' ')}
    >
      {/* Header */}
      <div className="px-4 pt-5 pb-3 flex items-center justify-between gap-2">
        <NavLink to="/" className="flex items-center gap-2.5 text-white font-semibold text-[15px] tracking-tight">
          <img src="/logo.png" alt="Tamma" className="w-7 h-7 rounded-full" />
          Tamma Docs
        </NavLink>
        <button
          type="button"
          ref={closeButtonRef}
          aria-label="Close navigation menu"
          onClick={onCloseMobile}
          className="md:hidden inline-flex items-center justify-center w-8 h-8 -mr-1 rounded-md text-zinc-400 hover:text-white hover:bg-white/5 transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-400/60"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2} aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      </div>

      {/* Search */}
      <div className="px-3 pb-3">
        <input
          type="text"
          placeholder="Search..."
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="w-full px-3 py-1.5 bg-white/5 border border-zinc-700/50 rounded-md text-[13px] text-zinc-300 placeholder:text-zinc-600 outline-none focus:border-zinc-500 transition-colors"
        />
      </div>

      {/* Nav */}
      <nav className="flex-1 overflow-y-auto px-2 pb-4 flex flex-col gap-0.5">
        {/* Top pages */}
        <div className="mb-1">
          <NavLink to="/" className={navClass}>Home</NavLink>
          <NavLink to="/roadmap" className={navClass}>Roadmap</NavLink>
          <NavLink to="/architecture" className={navClass}>Architecture</NavLink>
          <NavLink to="/deployment" className={navClass}>Deployment</NavLink>
          <NavLink to="/agent-dispatch" className={navClass}>Agent Dispatch</NavLink>
          <NavLink to="/security" className={navClass}>Security</NavLink>
          <NavLink to="/github-integration" className={navClass}>GitHub Integration</NavLink>
          <NavLink to="/testing" className={navClass}>Testing</NavLink>
          <NavLink to="/port-audit" className={navClass}>Port Audit</NavLink>
        </div>

        <div className="h-px bg-zinc-800/60 mx-2 my-1" />

        {/* Epics section */}
        <div>
          {sectionHeader('Epics', 'epics-section', epicPages.length)}
          {expanded['epics-section'] && (
            <div className="flex flex-col gap-0.5 mb-1">
              <NavLink to="/epics" className={navClass}>Overview</NavLink>
              {epicPages.map((item) => (
                <NavLink key={item.path} to={item.path} className={navClass}>
                  {item.title.length > 45 ? item.title.slice(0, 45) + '...' : item.title}
                </NavLink>
              ))}
            </div>
          )}
        </div>

        {/* Workflows section */}
        <div>
          {sectionHeader('Workflows', 'workflows-section', workflowPages.length)}
          {expanded['workflows-section'] && (
            <div className="flex flex-col gap-0.5 mb-1">
              <NavLink to="/workflows" className={navClass}>Overview</NavLink>
              {workflowPages.map((item) => (
                <NavLink key={item.path} to={item.path} className={navClass}>
                  {item.title.replace(/^Tamma\s+/, '').length > 45
                    ? item.title.replace(/^Tamma\s+/, '').slice(0, 45) + '...'
                    : item.title.replace(/^Tamma\s+/, '')}
                </NavLink>
              ))}
            </div>
          )}
        </div>

        <div className="h-px bg-zinc-800/60 mx-2 my-1" />

        {/* Stories by Epic */}
        <div>
          {sectionHeader('Stories', 'stories-section', manifest.filter(e => e.section.startsWith('Epic ')).length)}
          {expanded['stories-section'] && (
            <div className="ml-1">
              <NavLink to="/stories" className={navClass}>Overview</NavLink>
              {sortedStoryEpics.map(([section, items]) => (
                <div key={section}>
                  <button
                    onClick={() => toggle(section)}
                    className="flex items-center justify-between w-full px-3 py-1.5 text-[12px] text-zinc-500 hover:text-zinc-400 transition-colors"
                  >
                    <span className="truncate">{section}</span>
                    <span className="text-[10px] text-zinc-600">{items.length}</span>
                  </button>
                  {expanded[section] && renderItems(items, 20)}
                </div>
              ))}
            </div>
          )}
        </div>
      </nav>

      {/* Footer */}
      <div className="px-4 py-3 border-t border-zinc-800/60 text-[11px] text-zinc-600">
        <a href="https://github.com/meywd/tamma" target="_blank" rel="noopener" className="hover:text-zinc-400 transition-colors">
          GitHub
        </a>
        <span className="mx-2">·</span>
        <NavLink to="/contributing" className="hover:text-zinc-400 transition-colors">
          Contributing
        </NavLink>
      </div>
    </aside>
  );
}
