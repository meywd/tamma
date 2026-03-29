import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import cloudflare from '@astrojs/cloudflare';
import { readdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';

// Dynamically generate story sidebar groups by scanning the synced content directory.
// Each epic-N directory under src/content/docs/stories/ becomes a collapsible sidebar group.
function generateStorySidebarGroups() {
  const storiesDir = join(import.meta.dirname, 'src/content/docs/stories');
  if (!existsSync(storiesDir)) {
    return [{ label: 'Overview', link: '/stories/' }];
  }

  const epicDirs = readdirSync(storiesDir, { withFileTypes: true })
    .filter((d) => d.isDirectory() && d.name.startsWith('epic-'))
    .sort((a, b) => {
      // Sort by epic number: epic-1, epic-1.5, epic-2, ..., epic-24
      const numA = parseFloat(a.name.replace('epic-', ''));
      const numB = parseFloat(b.name.replace('epic-', ''));
      return numA - numB;
    });

  const epicLabels = {
    'epic-1': 'Epic 1: Foundation',
    'epic-1.5': 'Epic 1.5: Infrastructure',
    'epic-2': 'Epic 2: Autonomous Loop',
    'epic-3': 'Epic 3: Quality Gates',
    'epic-4': 'Epic 4: Event Sourcing',
    'epic-5': 'Epic 5: Observability',
    'epic-6': 'Epic 6: Context & Knowledge',
    'epic-7': 'Epic 7: Mentorship',
    'epic-8': 'Epic 8: Distribution',
    'epic-9': 'Epic 9: Agent Management',
    'epic-10': 'Epic 10: Engine Core',
    'epic-11': 'Epic 11: Security',
    'epic-12': 'Epic 12: Tool Loop',
    'epic-13': 'Epic 13: Workflow Decomposition',
    'epic-14': 'Epic 14: ELSA Studio',
    'epic-15': 'Epic 15: Log Aggregation',
    'epic-16': 'Epic 16: Auth & Admin',
    'epic-17': 'Epic 17: Multi-Tenancy',
    'epic-18': 'Epic 18: User Auth',
    'epic-19': 'Epic 19: Agent Dispatch',
    'epic-20': 'Epic 20: Billing',
    'epic-21': 'Epic 21: Marketing & Dashboard',
    'epic-22': 'Epic 22: CLI Standalone',
    'epic-23': 'Epic 23: System Monitoring',
    'epic-24': 'Epic 24: Voice Conversation',
    'epic-25': 'Epic 25: Documentation Site',
  };

  const groups = epicDirs.map((dir) => ({
    label: epicLabels[dir.name] || dir.name,
    collapsed: true,
    autogenerate: { directory: `stories/${dir.name}` },
  }));

  return [{ label: 'Overview', link: '/stories/' }, ...groups];
}

export default defineConfig({
  site: 'https://wiki.tamma.dev',
  output: 'static',
  adapter: cloudflare(),
  integrations: [
    starlight({
      title: 'Tamma Docs',
      tagline: 'Autonomous development platform that maintains itself',
      logo: {
        light: './src/assets/logo-light.svg',
        dark: './src/assets/logo-dark.svg',
        replacesTitle: false,
      },
      favicon: '/favicon.svg',
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/meywd/tamma' },
      ],
      customCss: ['./src/styles/custom.css'],
      editLink: {
        baseUrl: 'https://github.com/meywd/tamma/edit/main/apps/wiki-site/',
      },
      sidebar: [
        { label: 'Home', link: '/' },
        { label: 'Roadmap', link: '/roadmap/' },
        { label: 'Architecture', link: '/architecture/' },
        {
          label: 'Epics',
          collapsed: false,
          autogenerate: { directory: 'epics' },
        },
        {
          label: 'Stories',
          collapsed: true,
          items: generateStorySidebarGroups(),
        },
        { label: 'Contributing', link: '/contributing/' },
      ],
      // Pagefind is enabled by default — zero-backend full-text search
      // Search indexes are built at build time and served as static WASM
      head: [
        {
          tag: 'meta',
          attrs: {
            name: 'description',
            content:
              'Documentation for Tamma — the autonomous development platform that maintains itself. Epics, stories, architecture, and roadmap.',
          },
        },
      ],
    }),
  ],
});
