/**
 * Quick Links Tab
 *
 * Links to external services: ELSA Studio, OpenSearch Dashboards, GitHub repo, RabbitMQ.
 * All open in new tabs.
 */

import { Card } from '../../components/common/Card.js';

import type { JSX } from "react";

interface QuickLink {
  name: string;
  description: string;
  url: string;
  icon: string;
}

const LINKS: QuickLink[] = [
  {
    name: 'ELSA Studio',
    description: 'Workflow designer and execution monitor',
    url: 'https://elsa.tamma.dev',
    icon: 'W',
  },
  {
    name: 'OpenSearch Dashboards',
    description: 'Log aggregation and search',
    url: 'https://logs.tamma.dev',
    icon: 'S',
  },
  {
    name: 'GitHub Repository',
    description: 'Source code, issues, and discussions',
    url: 'https://github.com/meywd/tamma',
    icon: 'G',
  },
  {
    name: 'RabbitMQ Management',
    description: 'Message queue monitoring and management',
    url: 'https://rabbitmq.tamma.dev',
    icon: 'R',
  },
];

const ICON_COLORS: Record<string, string> = {
  W: 'bg-purple-100 text-purple-700',
  S: 'bg-blue-100 text-blue-700',
  G: 'bg-gray-100 text-gray-700',
  R: 'bg-orange-100 text-orange-700',
};

export function QuickLinksTab(): JSX.Element {
  return (
    <div>
      <h2 className="text-lg font-semibold text-gray-900 mb-4">Quick Links</h2>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {LINKS.map((link) => (
          <a
            key={link.name}
            href={link.url}
            target="_blank"
            rel="noopener noreferrer"
            className="block no-underline"
          >
            <Card className="hover:shadow-md transition-shadow cursor-pointer">
              <div className="flex items-center gap-4">
                <div
                  className={`h-10 w-10 rounded-lg flex items-center justify-center text-lg font-bold ${
                    ICON_COLORS[link.icon] ?? 'bg-gray-100 text-gray-700'
                  }`}
                >
                  {link.icon}
                </div>
                <div>
                  <h3 className="text-sm font-semibold text-gray-900">{link.name}</h3>
                  <p className="text-sm text-gray-500">{link.description}</p>
                </div>
                <div className="ml-auto text-gray-400">
                  <svg
                    xmlns="http://www.w3.org/2000/svg"
                    className="h-4 w-4"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                    strokeWidth={2}
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14"
                    />
                  </svg>
                </div>
              </div>
            </Card>
          </a>
        ))}
      </div>
    </div>
  );
}
