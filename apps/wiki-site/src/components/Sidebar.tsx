import { useEffect, useState } from 'react';
import { NavLink } from 'react-router';

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

interface SectionGroup {
  label: string;
  items: ManifestEntry[];
}

export default function Sidebar() {
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});

  useEffect(() => {
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then(setManifest)
      .catch(() => {});
  }, []);

  const sections: SectionGroup[] = [];
  const sectionMap = new Map<string, ManifestEntry[]>();

  for (const entry of manifest) {
    const s = entry.section || 'Pages';
    if (!sectionMap.has(s)) sectionMap.set(s, []);
    sectionMap.get(s)!.push(entry);
  }

  for (const [label, items] of sectionMap) {
    sections.push({ label, items });
  }

  const toggle = (label: string) =>
    setCollapsed((c) => ({ ...c, [label]: !c[label] }));

  const linkClass = ({ isActive }: { isActive: boolean }) =>
    `block px-3 py-1.5 rounded text-sm truncate transition-colors ${
      isActive
        ? 'bg-zinc-700 text-white font-medium'
        : 'text-zinc-400 hover:text-zinc-200 hover:bg-zinc-800'
    }`;

  return (
    <nav className="w-64 shrink-0 h-screen overflow-y-auto bg-zinc-900 border-r border-zinc-800 p-4 flex flex-col gap-1">
      <NavLink to="/" className="text-lg font-bold text-white mb-4 block px-2">
        Tamma Docs
      </NavLink>

      <NavLink to="/" className={linkClass}>Home</NavLink>
      <NavLink to="/roadmap" className={linkClass}>Roadmap</NavLink>
      <NavLink to="/architecture" className={linkClass}>Architecture</NavLink>
      <NavLink to="/workflows" className={linkClass}>Workflows</NavLink>

      {sections.map(({ label, items }) => (
        <div key={label} className="mt-2">
          <button
            onClick={() => toggle(label)}
            className="flex items-center justify-between w-full px-3 py-1.5 text-xs font-semibold uppercase tracking-wider text-zinc-500 hover:text-zinc-300"
          >
            {label}
            <span className="text-[10px]">{collapsed[label] ? '+' : '-'}</span>
          </button>
          {!collapsed[label] && (
            <div className="ml-1 flex flex-col gap-0.5">
              {items.map((item) => (
                <NavLink key={item.path} to={item.path} className={linkClass}>
                  {item.title}
                </NavLink>
              ))}
            </div>
          )}
        </div>
      ))}
    </nav>
  );
}
