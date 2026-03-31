/**
 * Validation Middleware
 * Comprehensive input validation and sanitization
 */

import { z, ZodError, ZodSchema } from 'zod';
import { Context } from 'hono';
import DOMPurify from 'isomorphic-dompurify';
import { ApiError } from '../utils/api-error';

/**
 * Sanitize HTML content to prevent XSS attacks
 */
export const sanitizeHtml = (value: string): string => {
  return DOMPurify.sanitize(value, {
    ALLOWED_TAGS: ['b', 'i', 'em', 'strong', 'a', 'p', 'br'],
    ALLOWED_ATTR: ['href', 'target'],
    ALLOW_DATA_ATTR: false,
    ALLOW_UNKNOWN_PROTOCOLS: false,
  });
};

/**
 * Remove all HTML tags
 */
export const stripHtml = (value: string): string => {
  return DOMPurify.sanitize(value, { ALLOWED_TAGS: [], ALLOWED_ATTR: [] });
};

/**
 * Common validation schemas
 */
export const validationSchemas = {
  // Email validation
  email: z
    .string()
    .email('Invalid email address')
    .toLowerCase()
    .trim()
    .max(255, 'Email must be less than 255 characters'),

  // Password validation with strength requirements
  password: z
    .string()
    .min(8, 'Password must be at least 8 characters long')
    .max(128, 'Password must be less than 128 characters')
    .regex(
      /^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[@$!%*?&])[A-Za-z\d@$!%*?&]/,
      'Password must contain uppercase, lowercase, number, and special character'
    ),

  // Name validation (allows letters, spaces, hyphens, apostrophes)
  name: z
    .string()
    .trim()
    .min(1, 'Name is required')
    .max(100, 'Name must be less than 100 characters')
    .regex(
      /^[a-zA-Z\s'-]+$/,
      'Name can only contain letters, spaces, hyphens, and apostrophes'
    )
    .transform(stripHtml),

  // Organization name validation
  organizationName: z
    .string()
    .trim()
    .min(1, 'Organization name is required')
    .max(255, 'Organization name must be less than 255 characters')
    .transform(sanitizeHtml),

  // Slug validation (URL-friendly identifier)
  slug: z
    .string()
    .trim()
    .min(3, 'Slug must be at least 3 characters')
    .max(100, 'Slug must be less than 100 characters')
    .regex(
      /^[a-z0-9-]+$/,
      'Slug can only contain lowercase letters, numbers, and hyphens'
    ),

  // UUID validation
  uuid: z.string().uuid('Invalid UUID format'),

  // Token validation
  token: z
    .string()
    .trim()
    .min(32, 'Invalid token')
    .max(128, 'Invalid token'),

  // Pagination parameters
  page: z.coerce
    .number()
    .int('Page must be an integer')
    .min(1, 'Page must be at least 1')
    .default(1),

  limit: z.coerce
    .number()
    .int('Limit must be an integer')
    .min(1, 'Limit must be at least 1')
    .max(100, 'Limit cannot exceed 100')
    .default(10),

  // Sort order
  sortOrder: z.enum(['asc', 'desc']).default('asc'),

  // Phone number validation (international format)
  phoneNumber: z
    .string()
    .regex(
      /^[+]?[(]?[0-9]{1,3}[)]?[-\s.]?[(]?[0-9]{1,4}[)]?[-\s.]?[0-9]{1,4}[-\s.]?[0-9]{1,9}$/,
      'Invalid phone number format'
    )
    .optional(),

  // URL validation
  url: z
    .string()
    .url('Invalid URL format')
    .max(2048, 'URL must be less than 2048 characters'),

  // Date validation
  date: z.string().datetime('Invalid date format'),

  // Boolean validation (handles string booleans)
  boolean: z.preprocess(
    (val) => {
      if (typeof val === 'string') {
        return val === 'true' || val === '1';
      }
      return Boolean(val);
    },
    z.boolean()
  ),
};

/**
 * Registration validation schema
 */
export const registerSchema = z.object({
  email: validationSchemas.email,
  password: validationSchemas.password,
  firstName: validationSchemas.name,
  lastName: validationSchemas.name,
  organizationName: validationSchemas.organizationName.optional(),
  acceptTerms: z.boolean().refine((val) => val === true, {
    message: 'You must accept the terms and conditions',
  }),
});

/**
 * Login validation schema
 */
export const loginSchema = z.object({
  email: validationSchemas.email,
  password: z.string().min(1, 'Password is required'),
  rememberMe: validationSchemas.boolean.optional(),
});

/**
 * Email verification schemas
 */
export const verifyEmailSchema = z.object({
  token: validationSchemas.token,
});

export const resendVerificationSchema = z.object({
  email: validationSchemas.email,
});

/**
 * Password reset schemas
 */
export const requestPasswordResetSchema = z.object({
  email: validationSchemas.email,
});

export const resetPasswordSchema = z.object({
  token: validationSchemas.token,
  password: validationSchemas.password,
  confirmPassword: z.string(),
}).refine((data) => data.password === data.confirmPassword, {
  message: "Passwords don't match",
  path: ["confirmPassword"],
});

/**
 * Change password schema
 */
export const changePasswordSchema = z.object({
  currentPassword: z.string().min(1, 'Current password is required'),
  newPassword: validationSchemas.password,
  confirmPassword: z.string(),
}).refine((data) => data.newPassword === data.confirmPassword, {
  message: "Passwords don't match",
  path: ["confirmPassword"],
});

/**
 * Organization schemas
 */
export const createOrganizationSchema = z.object({
  name: validationSchemas.organizationName,
  slug: validationSchemas.slug.optional(),
  description: z
    .string()
    .max(1000, 'Description must be less than 1000 characters')
    .transform(sanitizeHtml)
    .optional(),
  website: validationSchemas.url.optional(),
});

export const updateOrganizationSchema = createOrganizationSchema.partial();

/**
 * User profile schema
 */
export const updateProfileSchema = z.object({
  firstName: validationSchemas.name.optional(),
  lastName: validationSchemas.name.optional(),
  phoneNumber: validationSchemas.phoneNumber,
  bio: z
    .string()
    .max(500, 'Bio must be less than 500 characters')
    .transform(sanitizeHtml)
    .optional(),
});

/**
 * Validation middleware factory
 */
export function validateBody<T extends ZodSchema>(schema: T) {
  return async (c: Context, next: Function) => {
    try {
      const body = await c.req.json();
      const validated = await schema.parseAsync(body);

      // Store validated data for use in route handler
      c.set('validatedBody', validated);

      await next();
    } catch (error) {
      if (error instanceof ZodError) {
        const errors = error.errors.map((err) => ({
          field: err.path.join('.'),
          message: err.message,
          code: err.code,
        }));

        return c.json(
          {
            error: 'Validation failed',
            details: errors,
          },
          400
        );
      }

      return c.json(
        {
          error: 'Invalid request data',
        },
        400
      );
    }
  };
}

/**
 * Validation middleware for query parameters
 */
export function validateQuery<T extends ZodSchema>(schema: T) {
  return async (c: Context, next: Function) => {
    try {
      const query = c.req.query();
      const validated = await schema.parseAsync(query);

      // Store validated data for use in route handler
      c.set('validatedQuery', validated);

      await next();
    } catch (error) {
      if (error instanceof ZodError) {
        const errors = error.errors.map((err) => ({
          field: err.path.join('.'),
          message: err.message,
          code: err.code,
        }));

        return c.json(
          {
            error: 'Invalid query parameters',
            details: errors,
          },
          400
        );
      }

      return c.json(
        {
          error: 'Invalid query parameters',
        },
        400
      );
    }
  };
}

/**
 * Validation middleware for route parameters
 */
export function validateParams<T extends ZodSchema>(schema: T) {
  return async (c: Context, next: Function) => {
    try {
      const params = c.req.param();
      const validated = await schema.parseAsync(params);

      // Store validated data for use in route handler
      c.set('validatedParams', validated);

      await next();
    } catch (error) {
      if (error instanceof ZodError) {
        const errors = error.errors.map((err) => ({
          field: err.path.join('.'),
          message: err.message,
          code: err.code,
        }));

        return c.json(
          {
            error: 'Invalid route parameters',
            details: errors,
          },
          400
        );
      }

      return c.json(
        {
          error: 'Invalid route parameters',
        },
        400
      );
    }
  };
}

/**
 * Sanitization utilities
 */
export const sanitization = {
  /**
   * Sanitize object recursively
   */
  sanitizeObject<T extends Record<string, any>>(obj: T, config?: {
    allowHtml?: boolean;
    maxDepth?: number;
  }): T {
    const { allowHtml = false, maxDepth = 10 } = config || {};

    const sanitizeValue = (value: any, depth: number): any => {
      if (depth > maxDepth) {
        throw new Error('Maximum sanitization depth exceeded');
      }

      if (value === null || value === undefined) {
        return value;
      }

      if (typeof value === 'string') {
        return allowHtml ? sanitizeHtml(value) : stripHtml(value);
      }

      if (Array.isArray(value)) {
        return value.map(item => sanitizeValue(item, depth + 1));
      }

      if (typeof value === 'object') {
        const sanitized: Record<string, any> = {};
        for (const key in value) {
          if (value.hasOwnProperty(key)) {
            sanitized[key] = sanitizeValue(value[key], depth + 1);
          }
        }
        return sanitized;
      }

      return value;
    };

    return sanitizeValue(obj, 0) as T;
  },

  /**
   * Remove null bytes from string (prevents null byte injection)
   */
  removeNullBytes(str: string): string {
    return str.replace(/\0/g, '');
  },

  /**
   * Escape SQL wildcards for LIKE queries.
   * Backslashes are escaped first to prevent double-escaping issues,
   * then SQL wildcard characters (% and _) are escaped.
   */
  escapeSqlWildcards(str: string): string {
    // Escape backslashes first, then wildcards (order matters)
    return str.replace(/\\/g, '\\\\').replace(/[%_]/g, '\\$&');
  },

  /**
   * Normalize unicode characters
   */
  normalizeUnicode(str: string): string {
    return str.normalize('NFC');
  },

  /**
   * Remove control characters except for tabs and newlines
   * Covers both C0 control characters (U+0000-U+001F) and
   * C1 control characters (U+0080-U+009F), excluding tab (0x09),
   * newline (0x0A), and carriage return (0x0D).
   */
  removeControlCharacters(str: string): string {
    return str.replace(/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]/g, '');
  },
};

/**
 * Security headers middleware
 */
export const securityHeaders = async (c: Context, next: Function) => {
  // Set security headers
  c.header('X-Content-Type-Options', 'nosniff');
  c.header('X-Frame-Options', 'DENY');
  c.header('X-XSS-Protection', '1; mode=block');
  c.header('Referrer-Policy', 'strict-origin-when-cross-origin');
  c.header('Permissions-Policy', 'geolocation=(), microphone=(), camera=()');

  await next();
};

/**
 * Content type validation middleware
 */
export const validateContentType = (allowedTypes: string[]) => {
  return async (c: Context, next: Function) => {
    const contentType = c.req.header('content-type');

    if (!contentType) {
      return c.json(
        {
          error: 'Content-Type header is required',
        },
        400
      );
    }

    const isAllowed = allowedTypes.some(type =>
      contentType.toLowerCase().includes(type.toLowerCase())
    );

    if (!isAllowed) {
      return c.json(
        {
          error: `Invalid Content-Type. Allowed types: ${allowedTypes.join(', ')}`,
        },
        415
      );
    }

    await next();
  };
};

export default {
  validateBody,
  validateQuery,
  validateParams,
  sanitization,
  securityHeaders,
  validateContentType,
  schemas: validationSchemas,
};