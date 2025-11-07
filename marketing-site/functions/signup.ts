/**
 * Task 3 (#82) - Email Signup Handler (AC 3)
 * Cloudflare Pages Function for handling email signup submissions
 *
 * This function:
 * - Validates email addresses
 * - Implements basic anti-spam protection (rate limiting)
 * - Stores emails in Cloudflare KV
 * - Returns appropriate success/error responses
 */

interface Env {
  SIGNUPS: KVNamespace;
}

interface SignupRequest {
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
 * Handles POST requests to the signup endpoint
 */
export async function onRequestPost(context: {
  request: Request;
  env: Env;
}): Promise<Response> {
  const { request, env } = context;

  // CORS headers for browser requests
  const corsHeaders = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type',
  };

  try {
    // Parse request body
    let body: SignupRequest;
    try {
      body = await request.json() as SignupRequest;
    } catch (err) {
      return new Response('Invalid request body', {
        status: 400,
        headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
      });
    }
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
      // Increment rate limit even for existing emails to prevent email enumeration
      await incrementRateLimit(env, clientIP);

      // Return success even if email exists (don't reveal if email is already signed up)
      return new Response('Success', {
        status: 200,
        headers: { ...corsHeaders, 'Content-Type': 'text/plain' },
      });
    }

    // Increment rate limit counter BEFORE storing to prevent abuse if storage fails
    await incrementRateLimit(env, clientIP);

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
 * Handles OPTIONS requests for CORS preflight
 */
export async function onRequestOptions(): Promise<Response> {
  return new Response(null, {
    status: 204,
    headers: {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'POST, OPTIONS',
      'Access-Control-Allow-Headers': 'Content-Type',
    },
  });
}
