/**
 * Cloudflare Worker entry point with Durable Objects
 */

import { createPagesFunctionHandler } from '@react-router/cloudflare';

// Export the Durable Object for real-time events
export { EventBroadcaster } from './lib/events/event-broadcaster';

// Export default handler from React Router
export default createPagesFunctionHandler({
  // @ts-expect-error - build/server is generated at build time by React Router framework
  build: async () => import('./build/server'),
  mode: process.env.NODE_ENV,
});