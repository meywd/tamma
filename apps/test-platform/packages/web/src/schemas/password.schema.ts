import { z } from 'zod';

/**
 * Password requirements configuration
 * Used for validation and UI feedback
 */
export const PASSWORD_REQUIREMENTS = {
  minLength: 12,
  patterns: {
    uppercase: /[A-Z]/,
    lowercase: /[a-z]/,
    number: /[0-9]/,
    special: /[^A-Za-z0-9]/,
  },
} as const;

/**
 * Password validation schema with all security requirements
 */
export const passwordSchema = z
  .string()
  .min(PASSWORD_REQUIREMENTS.minLength, `Password must be at least ${PASSWORD_REQUIREMENTS.minLength} characters`)
  .regex(PASSWORD_REQUIREMENTS.patterns.uppercase, 'Must contain uppercase letter')
  .regex(PASSWORD_REQUIREMENTS.patterns.lowercase, 'Must contain lowercase letter')
  .regex(PASSWORD_REQUIREMENTS.patterns.number, 'Must contain number')
  .regex(PASSWORD_REQUIREMENTS.patterns.special, 'Must contain special character');

/**
 * Forgot password request schema
 */
export const forgotPasswordSchema = z.object({
  email: z.email('Please enter a valid email address'),
});

export type ForgotPasswordInput = z.infer<typeof forgotPasswordSchema>;

/**
 * Reset password schema with password confirmation
 */
export const resetPasswordSchema = z
  .object({
    password: passwordSchema,
    confirmPassword: z.string().min(1, 'Please confirm your password'),
  })
  .refine((data) => data.password === data.confirmPassword, {
    message: "Passwords don't match",
    path: ['confirmPassword'],
  });

export type ResetPasswordInput = z.infer<typeof resetPasswordSchema>;

/**
 * Change password schema (requires current password)
 */
export const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1, 'Current password is required'),
    password: passwordSchema,
    confirmPassword: z.string().min(1, 'Please confirm your password'),
  })
  .refine((data) => data.password === data.confirmPassword, {
    message: "Passwords don't match",
    path: ['confirmPassword'],
  });

export type ChangePasswordInput = z.infer<typeof changePasswordSchema>;

/**
 * Password requirement interface for UI components
 */
export interface PasswordRequirement {
  label: string;
  validator: (password: string) => boolean;
  met?: boolean;
}

/**
 * Get all password requirements for display
 */
export function getPasswordRequirements(password: string = ''): PasswordRequirement[] {
  return [
    {
      label: `At least ${PASSWORD_REQUIREMENTS.minLength} characters`,
      validator: (p) => p.length >= PASSWORD_REQUIREMENTS.minLength,
      met: password.length >= PASSWORD_REQUIREMENTS.minLength,
    },
    {
      label: 'Uppercase letter (A-Z)',
      validator: (p) => PASSWORD_REQUIREMENTS.patterns.uppercase.test(p),
      met: PASSWORD_REQUIREMENTS.patterns.uppercase.test(password),
    },
    {
      label: 'Lowercase letter (a-z)',
      validator: (p) => PASSWORD_REQUIREMENTS.patterns.lowercase.test(p),
      met: PASSWORD_REQUIREMENTS.patterns.lowercase.test(password),
    },
    {
      label: 'Number (0-9)',
      validator: (p) => PASSWORD_REQUIREMENTS.patterns.number.test(p),
      met: PASSWORD_REQUIREMENTS.patterns.number.test(password),
    },
    {
      label: 'Special character (!@#$%^&*)',
      validator: (p) => PASSWORD_REQUIREMENTS.patterns.special.test(p),
      met: PASSWORD_REQUIREMENTS.patterns.special.test(password),
    },
  ];
}

/**
 * Calculate password strength score (0-4)
 */
export function calculatePasswordStrength(password: string): {
  score: number;
  label: 'Weak' | 'Fair' | 'Good' | 'Strong' | 'Very Weak';
  color: string;
  percentage: number;
} {
  if (!password) {
    return { score: 0, label: 'Very Weak', color: 'bg-gray-300', percentage: 0 };
  }

  const requirements = getPasswordRequirements(password);
  const metCount = requirements.filter((req) => req.met).length;
  const score = metCount;

  // Determine label, color, and percentage based on score
  if (score === 0) {
    return { score: 0, label: 'Very Weak', color: 'bg-red-500', percentage: 10 };
  } else if (score <= 2) {
    return { score: 1, label: 'Weak', color: 'bg-red-500', percentage: 25 };
  } else if (score === 3) {
    return { score: 2, label: 'Fair', color: 'bg-yellow-500', percentage: 50 };
  } else if (score === 4) {
    return { score: 3, label: 'Good', color: 'bg-blue-500', percentage: 75 };
  } else {
    return { score: 4, label: 'Strong', color: 'bg-green-500', percentage: 100 };
  }
}
