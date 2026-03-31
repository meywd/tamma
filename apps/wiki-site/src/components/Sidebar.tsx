import { useEffect, useState } from 'react';
import { NavLink, useLocation } from 'react-router';

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

export default function Sidebar() {
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [search, setSearch] = useState('');
  const location = useLocation();

  useEffect(() => {
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => {
        setManifest(data);
        // Auto-expand the current section
        const current = data.find((e) => e.path === location.pathname);
        if (current?.section) {
          setExpanded((prev) => ({ ...prev, [current.section]: true }));
        }
      })
      .catch(() => {});
  }, []);

  // Group by section
  const sections = new Map<string, ManifestEntry[]>();
  const topPages: ManifestEntry[] = [];

  for (const entry of manifest) {
    if (search && !entry.title.toLowerCase().includes(search.toLowerCase())) continue;
    if (entry.section === 'Pages') {
      topPages.push(entry);
    } else {
      if (!sections.has(entry.section)) sections.set(entry.section, []);
      sections.get(entry.section)!.push(entry);
    }
  }

  const toggle = (s: string) => setExpanded((prev) => ({ ...prev, [s]: !prev[s] }));

  const navClass = ({ isActive }: { isActive: boolean }) =>
    `block px-3 py-1.5 rounded-md text-[13px] leading-snug transition-colors ${
      isActive
        ? 'bg-white/10 text-white font-medium'
        : 'text-zinc-400 hover:text-zinc-200 hover:bg-white/5'
    }`;

  // Sort epics numerically
  const sortedSections = [...sections.entries()].sort((a, b) => {
    const numA = parseFloat(a[0].replace(/[^0-9.]/g, '')) || 999;
    const numB = parseFloat(b[0].replace(/[^0-9.]/g, '')) || 999;
    return numA - numB;
  });

  return (
    <aside className="w-[260px] shrink-0 h-screen overflow-y-auto bg-[#111113] border-r border-zinc-800/60 flex flex-col">
      {/* Header */}
      <div className="px-4 pt-5 pb-3">
        <NavLink to="/" className="flex items-center gap-2.5 text-white font-semibold text-[15px] tracking-tight">
          <div className="w-6 h-6 rounded bg-gradient-to-br from-blue-500 to-violet-600 flex items-center justify-center text-[11px] font-bold">T</div>
          Tamma Docs
        </NavLink>
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
        {/* Top-level pages */}
        <div className="mb-2">
          <NavLink to="/" className={navClass}>Home</NavLink>
          <NavLink to="/roadmap" className={navClass}>Roadmap</NavLink>
          <NavLink to="/architecture" className={navClass}>Architecture</NavLink>
          <NavLink to="/epics" className={navClass}>Epics Overview</NavLink>
          <NavLink to="/workflows" className={navClass}>Workflows</NavLink>
          <NavLink to="/stories" className={navClass}>Stories</NavLink>
        </div>

        <div className="h-px bg-zinc-800/60 mx-2 my-1" />

        {/* Sections */}
        {sortedSections.map(([section, items]) => (
          <div key={section}>
            <button
              onClick={() => toggle(section)}
              className="flex items-center justify-between w-full px-3 py-2 text-[11px] font-semibold uppercase tracking-widest text-zinc-500 hover:text-zinc-400 transition-colors"
            >
              <span className="truncate">{section}</span>
              <svg
                className={`w-3 h-3 shrink-0 transition-transform ${expanded[section] ? 'rotate-90' : ''}`}
                fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
              >
                <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
              </svg>
            </button>
            {expanded[section] && (
              <div className="flex flex-col gap-0.5 mb-1">
                {items.slice(0, 30).map((item) => (
                  <NavLink key={item.path} to={item.path} className={navClass}>
                    {item.title.length > 40 ? item.title.slice(0, 40) + '...' : item.title}
                  </NavLink>
                ))}
                {items.length > 30 && (
                  <span className="px-3 py-1 text-[11px] text-zinc-600">
                    +{items.length - 30} more
                  </span>
                )}
              </div>
            )}
          </div>
        ))}
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
