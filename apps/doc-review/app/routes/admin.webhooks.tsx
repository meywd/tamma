/**
 * Webhook Admin UI
 *
 * Admin interface for managing webhook configurations and viewing events
 */

import { useLoaderData, useActionData, Form, useNavigation } from 'react-router';
import { data as json, redirect, type LoaderFunctionArgs, type ActionFunctionArgs } from 'react-router';
import { useState } from 'react';
import { getUser } from '~/lib/auth/session.server';
import { WebhookStorage } from '~/lib/webhooks/storage.server';
import type {
  WebhookEvent,
  WebhookStats,
  WebhookConfiguration
} from '~/lib/webhooks/types';

interface LoaderData {
  webhookUrls: {
    github: string;
    gitlab: string;
  };
  configurations: {
    github: WebhookConfiguration | null;
    gitlab: WebhookConfiguration | null;
  };
  stats: WebhookStats[];
  recentEvents: WebhookEvent[];
  user: {
    id: string;
    email: string;
    isAdmin: boolean;
  };
}

interface ActionData {
  success?: boolean;
  error?: string;
  message?: string;
}

/**
 * Check if user is admin
 */
async function requireAdmin(request: Request, env: any) {
  const user = await getUser(request, { env });

  if (!user) {
    throw redirect('/auth/login');
  }

  // Check if user is admin (implement your admin check logic)
  const isAdmin = user.email?.endsWith('@admin.com') ||
                  user.id === env.ADMIN_USER_ID;

  if (!isAdmin) {
    throw new Response('Unauthorized', { status: 403 });
  }

  return user;
}

export async function loader({ request, context }: LoaderFunctionArgs) {
  const env = context.cloudflare?.env as {
    DB: D1Database;
    GITHUB_WEBHOOK_SECRET?: string;
    GITLAB_WEBHOOK_TOKEN?: string;
  };

  // Require admin access
  const user = await requireAdmin(request, env);

  // Initialize storage
  const storage = new WebhookStorage(env.DB);

  // Get webhook URLs
  const url = new URL(request.url);
  const baseUrl = `${url.protocol}//${url.host}`;
  const webhookUrls = {
    github: `${baseUrl}/webhooks/github`,
    gitlab: `${baseUrl}/webhooks/gitlab`
  };

  // Get configurations
  const [githubConfig, gitlabConfig] = await Promise.all([
    storage.getWebhookConfig('github'),
    storage.getWebhookConfig('gitlab')
  ]);

  // Get statistics
  const stats = await storage.getWebhookStats();

  // Get recent events
  const { events: recentEvents } = await storage.listWebhookEvents({
    limit: 20
  });

  return json<LoaderData>({
    webhookUrls,
    configurations: {
      github: githubConfig,
      gitlab: gitlabConfig
    },
    stats,
    recentEvents,
    user: {
      id: user.id,
      email: user.email || '',
      isAdmin: true
    }
  });
}

export async function action({ request, context }: ActionFunctionArgs) {
  const env = context.cloudflare?.env as {
    DB: D1Database;
    GITHUB_WEBHOOK_SECRET?: string;
    GITLAB_WEBHOOK_TOKEN?: string;
  };

  // Require admin access
  await requireAdmin(request, env);

  const formData = await request.formData();
  const actionType = formData.get('_action');
  const storage = new WebhookStorage(env.DB);

  try {
    switch (actionType) {
      case 'test_github': {
        // Test GitHub webhook
        const url = new URL(request.url);
        const webhookUrl = `${url.protocol}//${url.host}/webhooks/github`;

        const testPayload = {
          action: 'opened',
          number: 999,
          pull_request: {
            number: 999,
            title: 'Test PR from Admin UI',
            state: 'open',
            draft: false,
            user: {
              id: 1,
              login: 'test-user'
            },
            head: { ref: 'test-branch', sha: 'abc123' },
            base: { ref: 'main', sha: 'def456' },
            html_url: 'https://github.com/test/test/pull/999',
            created_at: new Date().toISOString(),
            updated_at: new Date().toISOString()
          },
          repository: {
            full_name: 'test/test'
          },
          sender: {
            login: 'test-user'
          }
        };

        const response = await fetch(webhookUrl, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'X-GitHub-Event': 'pull_request',
            'X-GitHub-Delivery': `test-${Date.now()}`,
            'X-Hub-Signature-256': 'sha256=test-signature'
          },
          body: JSON.stringify(testPayload)
        });

        if (response.ok) {
          return json<ActionData>({
            success: true,
            message: 'GitHub webhook test delivered successfully'
          });
        } else {
          return json<ActionData>({
            success: false,
            error: `GitHub webhook test failed: ${response.status} ${response.statusText}`
          });
        }
      }

      case 'test_gitlab': {
        // Test GitLab webhook
        const url = new URL(request.url);
        const webhookUrl = `${url.protocol}//${url.host}/webhooks/gitlab`;

        const testPayload = {
          object_kind: 'merge_request',
          event_type: 'merge_request',
          user: {
            id: 1,
            username: 'test-user'
          },
          project: {
            id: 1,
            path_with_namespace: 'test/test'
          },
          object_attributes: {
            iid: 999,
            title: 'Test MR from Admin UI',
            state: 'opened',
            action: 'open',
            source_branch: 'test-branch',
            target_branch: 'main',
            web_url: 'https://gitlab.com/test/test/-/merge_requests/999',
            created_at: new Date().toISOString(),
            updated_at: new Date().toISOString()
          }
        };

        const response = await fetch(webhookUrl, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'X-Gitlab-Event': 'Merge Request Hook',
            'X-Gitlab-Token': env.GITLAB_WEBHOOK_TOKEN || 'test-token'
          },
          body: JSON.stringify(testPayload)
        });

        if (response.ok) {
          return json<ActionData>({
            success: true,
            message: 'GitLab webhook test delivered successfully'
          });
        } else {
          return json<ActionData>({
            success: false,
            error: `GitLab webhook test failed: ${response.status} ${response.statusText}`
          });
        }
      }

      case 'clear_events': {
        // Clear old webhook events (older than 30 days)
        const deleted = await storage.cleanupOldEvents(30);
        return json<ActionData>({
          success: true,
          message: `Cleared ${deleted} old webhook events`
        });
      }

      case 'reprocess': {
        // Reprocess a specific event
        const eventId = formData.get('eventId') as string;
        if (!eventId) {
          return json<ActionData>({
            success: false,
            error: 'Event ID required'
          });
        }

        const event = await storage.getWebhookEvent(eventId);
        if (!event) {
          return json<ActionData>({
            success: false,
            error: 'Event not found'
          });
        }

        // Mark as unprocessed to reprocess
        await storage.markWebhookEventProcessed(eventId); // Reset

        return json<ActionData>({
          success: true,
          message: 'Event marked for reprocessing'
        });
      }

      default:
        return json<ActionData>({
          success: false,
          error: 'Unknown action'
        });
    }
  } catch (error) {
    console.error('Webhook admin action error:', error);
    return json<ActionData>({
      success: false,
      error: error instanceof Error ? error.message : 'An error occurred'
    });
  }
}

export default function AdminWebhooks() {
  const data = useLoaderData<LoaderData>();
  const actionData = useActionData<ActionData>();
  const navigation = useNavigation();
  const [selectedEvent, setSelectedEvent] = useState<WebhookEvent | null>(null);
  const [showEventDetails, setShowEventDetails] = useState(false);

  const isSubmitting = navigation.state === 'submitting';

  return (
    <div className="container mx-auto px-4 py-8 max-w-7xl">
      <h1 className="text-3xl font-bold mb-8">Webhook Administration</h1>

      {/* Action feedback */}
      {actionData && (
        <div className={`mb-6 p-4 rounded-lg ${
          actionData.success
            ? 'bg-green-100 border border-green-400 text-green-700'
            : 'bg-red-100 border border-red-400 text-red-700'
        }`}>
          {actionData.message || actionData.error}
        </div>
      )}

      {/* Webhook URLs Section */}
      <section className="mb-8 bg-white rounded-lg shadow p-6">
        <h2 className="text-xl font-semibold mb-4">Webhook Endpoints</h2>

        <div className="space-y-4">
          {/* GitHub */}
          <div className="border rounded-lg p-4">
            <div className="flex items-center justify-between mb-2">
              <h3 className="font-semibold flex items-center">
                <svg className="w-5 h-5 mr-2" fill="currentColor" viewBox="0 0 24 24">
                  <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z"/>
                </svg>
                GitHub
              </h3>
              <span className={`px-2 py-1 text-xs rounded ${
                data.configurations.github?.active
                  ? 'bg-green-100 text-green-800'
                  : 'bg-gray-100 text-gray-600'
              }`}>
                {data.configurations.github?.active ? 'Active' : 'Not Configured'}
              </span>
            </div>

            <div className="mb-3">
              <label className="block text-sm font-medium text-gray-600 mb-1">
                Webhook URL
              </label>
              <div className="flex items-center">
                <input
                  type="text"
                  readOnly
                  value={data.webhookUrls.github}
                  className="flex-1 px-3 py-2 bg-gray-50 border border-gray-300 rounded-md text-sm font-mono"
                />
                <button
                  onClick={async () => navigator.clipboard.writeText(data.webhookUrls.github)}
                  className="ml-2 px-3 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600 text-sm"
                >
                  Copy
                </button>
              </div>
            </div>

            <div className="mb-3">
              <label className="block text-sm font-medium text-gray-600 mb-1">
                Secret Configuration
              </label>
              <p className="text-sm text-gray-500">
                {process.env.NODE_ENV === 'production'
                  ? 'Configure GITHUB_WEBHOOK_SECRET environment variable'
                  : 'Set GITHUB_WEBHOOK_SECRET in wrangler.toml vars'}
              </p>
            </div>

            <Form method="post" className="inline">
              <input type="hidden" name="_action" value="test_github" />
              <button
                type="submit"
                disabled={isSubmitting}
                className="px-4 py-2 bg-gray-800 text-white rounded-md hover:bg-gray-700 disabled:opacity-50"
              >
                {isSubmitting ? 'Testing...' : 'Test Webhook'}
              </button>
            </Form>
          </div>

          {/* GitLab */}
          <div className="border rounded-lg p-4">
            <div className="flex items-center justify-between mb-2">
              <h3 className="font-semibold flex items-center">
                <svg className="w-5 h-5 mr-2" fill="currentColor" viewBox="0 0 24 24">
                  <path d="M22.65 14.39L12 22.13 1.35 14.39a.84.84 0 0 1-.3-.94l1.22-3.78 2.44-7.51A.42.42 0 0 1 4.82 2a.43.43 0 0 1 .58 0 .42.42 0 0 1 .11.18l2.44 7.49h8.1l2.44-7.51A.42.42 0 0 1 18.6 2a.43.43 0 0 1 .58 0 .42.42 0 0 1 .11.18l2.44 7.51L23 13.45a.84.84 0 0 1-.35.94z"/>
                </svg>
                GitLab
              </h3>
              <span className={`px-2 py-1 text-xs rounded ${
                data.configurations.gitlab?.active
                  ? 'bg-green-100 text-green-800'
                  : 'bg-gray-100 text-gray-600'
              }`}>
                {data.configurations.gitlab?.active ? 'Active' : 'Not Configured'}
              </span>
            </div>

            <div className="mb-3">
              <label className="block text-sm font-medium text-gray-600 mb-1">
                Webhook URL
              </label>
              <div className="flex items-center">
                <input
                  type="text"
                  readOnly
                  value={data.webhookUrls.gitlab}
                  className="flex-1 px-3 py-2 bg-gray-50 border border-gray-300 rounded-md text-sm font-mono"
                />
                <button
                  onClick={async () => navigator.clipboard.writeText(data.webhookUrls.gitlab)}
                  className="ml-2 px-3 py-2 bg-blue-500 text-white rounded-md hover:bg-blue-600 text-sm"
                >
                  Copy
                </button>
              </div>
            </div>

            <div className="mb-3">
              <label className="block text-sm font-medium text-gray-600 mb-1">
                Token Configuration
              </label>
              <p className="text-sm text-gray-500">
                {process.env.NODE_ENV === 'production'
                  ? 'Configure GITLAB_WEBHOOK_TOKEN environment variable'
                  : 'Set GITLAB_WEBHOOK_TOKEN in wrangler.toml vars'}
              </p>
            </div>

            <Form method="post" className="inline">
              <input type="hidden" name="_action" value="test_gitlab" />
              <button
                type="submit"
                disabled={isSubmitting}
                className="px-4 py-2 bg-orange-600 text-white rounded-md hover:bg-orange-700 disabled:opacity-50"
              >
                {isSubmitting ? 'Testing...' : 'Test Webhook'}
              </button>
            </Form>
          </div>
        </div>
      </section>

      {/* Statistics Section */}
      <section className="mb-8 bg-white rounded-lg shadow p-6">
        <h2 className="text-xl font-semibold mb-4">Webhook Statistics</h2>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          {data.stats.map((stat) => (
            <div key={stat.provider} className="border rounded-lg p-4">
              <h3 className="font-semibold capitalize mb-2">{stat.provider}</h3>
              <div className="space-y-1 text-sm">
                <div className="flex justify-between">
                  <span className="text-gray-600">Total Events:</span>
                  <span className="font-medium">{stat.total}</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-gray-600">Processed:</span>
                  <span className="font-medium text-green-600">{stat.processed}</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-gray-600">Failed:</span>
                  <span className="font-medium text-red-600">{stat.failed}</span>
                </div>
                <div className="flex justify-between">
                  <span className="text-gray-600">Pending:</span>
                  <span className="font-medium text-yellow-600">{stat.pending}</span>
                </div>
                {stat.lastReceived && (
                  <div className="mt-2 pt-2 border-t">
                    <span className="text-xs text-gray-500">
                      Last received: {new Date(stat.lastReceived).toLocaleString()}
                    </span>
                  </div>
                )}
              </div>
            </div>
          ))}
        </div>

        <div className="mt-4">
          <Form method="post" className="inline">
            <input type="hidden" name="_action" value="clear_events" />
            <button
              type="submit"
              disabled={isSubmitting}
              className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:opacity-50"
              onClick={(e) => {
                if (!confirm('Clear webhook events older than 30 days?')) {
                  e.preventDefault();
                }
              }}
            >
              Clear Old Events
            </button>
          </Form>
        </div>
      </section>

      {/* Recent Events Section */}
      <section className="bg-white rounded-lg shadow p-6">
        <h2 className="text-xl font-semibold mb-4">Recent Webhook Events</h2>

        <div className="overflow-x-auto">
          <table className="min-w-full divide-y divide-gray-200">
            <thead className="bg-gray-50">
              <tr>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase">
                  Provider
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase">
                  Event
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase">
                  PR/Branch
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase">
                  Status
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase">
                  Time
                </th>
                <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase">
                  Actions
                </th>
              </tr>
            </thead>
            <tbody className="bg-white divide-y divide-gray-200">
              {data.recentEvents.map((event) => (
                <tr key={event.id} className="hover:bg-gray-50">
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span className="capitalize text-sm">{event.provider}</span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span className="text-sm">
                      {event.eventType}
                      {event.eventAction && (
                        <span className="text-gray-500 ml-1">
                          ({event.eventAction})
                        </span>
                      )}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap text-sm">
                    {event.prNumber && (
                      <span className="font-mono">#{event.prNumber}</span>
                    )}
                    {event.branch && (
                      <span className="text-gray-500 ml-1">{event.branch}</span>
                    )}
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap">
                    <span className={`px-2 py-1 text-xs rounded ${
                      event.processed === 1
                        ? 'bg-green-100 text-green-800'
                        : event.processed === -1
                        ? 'bg-red-100 text-red-800'
                        : 'bg-yellow-100 text-yellow-800'
                    }`}>
                      {event.processed === 1 ? 'Processed' :
                       event.processed === -1 ? 'Failed' : 'Pending'}
                    </span>
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap text-sm text-gray-500">
                    {new Date(event.createdAt).toLocaleString()}
                  </td>
                  <td className="px-4 py-3 whitespace-nowrap text-sm">
                    <button
                      onClick={() => {
                        setSelectedEvent(event);
                        setShowEventDetails(true);
                      }}
                      className="text-blue-600 hover:text-blue-800 mr-2"
                    >
                      View
                    </button>
                    {event.processed === -1 && (
                      <Form method="post" className="inline">
                        <input type="hidden" name="_action" value="reprocess" />
                        <input type="hidden" name="eventId" value={event.id} />
                        <button
                          type="submit"
                          disabled={isSubmitting}
                          className="text-orange-600 hover:text-orange-800"
                        >
                          Retry
                        </button>
                      </Form>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      {/* Event Details Modal */}
      {showEventDetails && selectedEvent && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center p-4 z-50">
          <div className="bg-white rounded-lg max-w-4xl w-full max-h-[80vh] overflow-hidden">
            <div className="p-6 border-b">
              <div className="flex justify-between items-center">
                <h3 className="text-lg font-semibold">
                  Webhook Event Details
                </h3>
                <button
                  onClick={() => {
                    setShowEventDetails(false);
                    setSelectedEvent(null);
                  }}
                  className="text-gray-400 hover:text-gray-600"
                >
                  <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            </div>

            <div className="p-6 overflow-y-auto max-h-[60vh]">
              <div className="space-y-4">
                <div>
                  <h4 className="font-medium mb-1">Event ID</h4>
                  <p className="text-sm font-mono text-gray-600">{selectedEvent.id}</p>
                </div>

                <div>
                  <h4 className="font-medium mb-1">Provider</h4>
                  <p className="text-sm capitalize">{selectedEvent.provider}</p>
                </div>

                <div>
                  <h4 className="font-medium mb-1">Event Type</h4>
                  <p className="text-sm">
                    {selectedEvent.eventType}
                    {selectedEvent.eventAction && ` (${selectedEvent.eventAction})`}
                  </p>
                </div>

                {selectedEvent.error && (
                  <div>
                    <h4 className="font-medium mb-1 text-red-600">Error</h4>
                    <p className="text-sm text-red-600">{selectedEvent.error}</p>
                  </div>
                )}

                <div>
                  <h4 className="font-medium mb-1">Payload</h4>
                  <pre className="text-xs bg-gray-100 p-3 rounded overflow-x-auto">
                    {JSON.stringify(JSON.parse(selectedEvent.payload), null, 2)}
                  </pre>
                </div>

                {selectedEvent.headers && (
                  <div>
                    <h4 className="font-medium mb-1">Headers</h4>
                    <pre className="text-xs bg-gray-100 p-3 rounded overflow-x-auto">
                      {JSON.stringify(JSON.parse(selectedEvent.headers), null, 2)}
                    </pre>
                  </div>
                )}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}