/**
 * Tamma Marketing Site - Cloudflare Worker
 * Serves static assets and handles API endpoints
 */

export interface Env {
  SIGNUPS: KVNamespace;
}

export interface SignupRequest {
  email: string;
}

/**
 * Validates email address format
 */
function isValidEmail(email: string): boolean {
  const emailRegex = /^[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}$/i;
  return emailRegex.test(email);
}

/**
 * Normalizes email address (lowercase, trim whitespace)
 */
function normalizeEmail(email: string): string {
  return email.trim().toLowerCase();
}

/**
 * Generates a rate limit key for an IP address
 */
function getRateLimitKey(ip: string): string {
  return `ratelimit:${ip}`;
}

/**
 * Checks if an IP address has exceeded the rate limit
 * Rate limit: 5 signups per hour per IP
 */
async function checkRateLimit(env: Env, ip: string): Promise<boolean> {
  const key = getRateLimitKey(ip);
  const currentCount = await env.SIGNUPS.get(key);

  if (currentCount) {
    const count = parseInt(currentCount, 10);
    if (count >= 5) {
      return false; // Rate limit exceeded
    }
  }

  return true; // Within rate limit
}

/**
 * Increments the rate limit counter for an IP address
 */
async function incrementRateLimit(env: Env, ip: string): Promise<void> {
  const key = getRateLimitKey(ip);
  const currentCount = await env.SIGNUPS.get(key);
  const newCount = currentCount ? parseInt(currentCount, 10) + 1 : 1;

  // Store with 1 hour expiration
  await env.SIGNUPS.put(key, newCount.toString(), { expirationTtl: 3600 });
}

/**
 * Handles email signup requests
 */
async function handleSignup(request: Request, env: Env): Promise<Response> {
  // CORS headers for browser requests
  const corsHeaders = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type',
  };

  if (request.method === 'OPTIONS') {
    return new Response(null, {
      status: 204,
      headers: corsHeaders,
    });
  }

  if (request.method !== 'POST') {
    return new Response('Method not allowed', {
      status: 405,
      headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
    });
  }

  try {
    // Parse request body
    const body = await request.json() as SignupRequest;
    const { email } = body;

    // Validate email presence
    if (!email) {
      return new Response('Email address is required', {
        status: 400,
        headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
      });
    }

    // Normalize email
    const normalizedEmail = normalizeEmail(email);

    // Validate email format
    if (!isValidEmail(normalizedEmail)) {
      return new Response('Invalid email address format', {
        status: 400,
        headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
      });
    }

    // Get client IP for rate limiting
    const clientIP = request.headers.get('CF-Connecting-IP') || 'unknown';

    // Check rate limit (basic anti-spam protection)
    const withinRateLimit = await checkRateLimit(env, clientIP);
    if (!withinRateLimit) {
      return new Response('Too many signup attempts. Please try again later.', {
        status: 429,
        headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
      });
    }

    // Check if email already exists
    const existingSignup = await env.SIGNUPS.get(`email:${normalizedEmail}`);
    if (existingSignup) {
      // Return success even if email exists (don't reveal if email is already signed up)
      return new Response('Success', {
        status: 200,
        headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
      });
    }

    // Store email in KV with metadata
    const signupData = {
      email: normalizedEmail,
      signupDate: new Date().toISOString(),
      ip: clientIP,
      userAgent: request.headers.get('User-Agent') || 'unknown',
    };

    await env.SIGNUPS.put(
      `email:${normalizedEmail}`,
      JSON.stringify(signupData),
      {
        metadata: {
          signupDate: signupData.signupDate,
        },
      }
    );

    // Increment rate limit counter
    await incrementRateLimit(env, clientIP);

    // Log signup (visible in Cloudflare Workers logs)
    console.log(`New signup: ${normalizedEmail} from ${clientIP}`);

    // Return success response
    return new Response('Success', {
      status: 200,
      headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
    });
  } catch (error) {
    // Log error for debugging
    console.error('Signup error:', error);

    // Return generic error response
    return new Response('Internal server error. Please try again later.', {
      status: 500,
      headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
    });
  }
}

/**
 * Main worker request handler
 */
export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);

    // Handle API endpoints
    if (url.pathname === '/signup') {
      return handleSignup(request, env);
    }

    // For all other requests, serve static assets
    // The assets are served automatically by Cloudflare Workers with [assets] configuration
    return new Response('Asset not found', { status: 404 });
  },
};