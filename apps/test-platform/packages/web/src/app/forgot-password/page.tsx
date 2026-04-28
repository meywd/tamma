'use client';

import React, { useState } from 'react';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { authService } from '@/services/auth.service';

const forgotPasswordSchema = z.object({
  email: z
    .email('Please enter a valid email address')
    .min(1, 'Email is required'),
});

type ForgotPasswordFormData = z.infer<typeof forgotPasswordSchema>;

const backgroundImages = [
  '/background/1.png',
  '/background/2.png',
  '/background/3.jpeg',
  '/background/4.jpeg',
  '/background/5.jpeg',
  '/background/6.jpeg',
  '/background/7.png',
  '/background/8.png',
  '/background/9.png',
  '/background/10.png',
];

export default function ForgotPasswordPage() {
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [isSuccess, setIsSuccess] = useState(false);
  const [apiError, setApiError] = useState<string | null>(null);
  const [backgroundImage] = useState(() => backgroundImages[Math.floor(Math.random() * backgroundImages.length)]);

  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<ForgotPasswordFormData>({
    resolver: zodResolver(forgotPasswordSchema),
  });

  const onSubmit = async (data: ForgotPasswordFormData) => {
    setIsSubmitting(true);
    setApiError(null);

    try {
      await authService.forgotPassword(data.email);
      setIsSuccess(true);
    } catch (error: any) {
      console.error('Forgot password failed:', error);
      setApiError(
        error.response?.data?.message ||
        'Failed to send reset email. Please try again.'
      );
    } finally {
      setIsSubmitting(false);
    }
  };

  if (isSuccess) {
    return (
      <div
        className="min-h-screen flex flex-col justify-center py-12 sm:px-6 lg:px-8 bg-cover bg-center bg-no-repeat"
        style={{
          backgroundImage: `url(${backgroundImage})`,
          backgroundSize: 'cover',
          position: 'relative'
        }}
      >
        {/* Dark overlay for better readability */}
        <div className="absolute inset-0 bg-black/40" style={{ zIndex: 0 }} />

        <div className="relative z-10">
        <div className="sm:mx-auto sm:w-full sm:max-w-md">
          <div className="flex justify-center">
            <img
              src="/logo.png"
              alt="Logo"
              className="h-32 w-32 object-contain"
            />
          </div>
          <h2 className="mt-6 text-center text-3xl font-extrabold text-gray-900">
            Check your email
          </h2>
        </div>

        <div className="mt-8 sm:mx-auto sm:w-full sm:max-w-md">
          <div className="bg-white/60 backdrop-blur-sm py-8 px-4 shadow sm:rounded-lg sm:px-10">
            <div className="text-center">
              <div className="mx-auto flex items-center justify-center h-12 w-12 rounded-full bg-green-100">
                <svg
                  className="h-6 w-6 text-green-600"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
                  />
                </svg>
              </div>
              <h3 className="mt-4 text-lg font-medium text-gray-900">
                Password Reset Email Sent
              </h3>
              <p className="mt-2 text-sm text-gray-600">
                If an account exists with this email, you will receive password reset instructions shortly.
              </p>
              <p className="mt-2 text-sm text-gray-500">
                Please check your inbox and follow the link to reset your password.
              </p>
            </div>

            <div className="mt-6 text-center">
              <a
                href="/login"
                className="font-medium text-primary-600 hover:text-primary-500"
              >
                Return to Login
              </a>
            </div>
          </div>
        </div>
        </div>
      </div>
    );
  }

  return (
    <div
      className="min-h-screen flex flex-col justify-center py-12 sm:px-6 lg:px-8 bg-cover bg-center bg-no-repeat"
      style={{
        backgroundImage: `url(${backgroundImage})`,
        backgroundSize: 'cover',
        position: 'relative'
      }}
    >
      {/* Dark overlay for better readability */}
      <div className="absolute inset-0 bg-black/40" style={{ zIndex: 0 }} />

      <div className="relative z-10">
      <div className="sm:mx-auto sm:w-full sm:max-w-md">
        {/* Logo/Branding */}
        <div className="flex justify-center">
          <img
            src="/logo.png"
            alt="Logo"
            className="h-32 w-32 object-contain"
          />
        </div>
        <h2 className="mt-6 text-center text-3xl font-extrabold text-gray-900">
          Reset your password
        </h2>
        <p className="mt-2 text-center text-sm text-gray-600">
          Enter your email address and we'll send you a link to reset your password.
        </p>
      </div>

      <div className="mt-8 sm:mx-auto sm:w-full sm:max-w-md">
        <div className="bg-white/60 backdrop-blur-sm py-8 px-4 shadow sm:rounded-lg sm:px-10">
          <form className="space-y-6" onSubmit={handleSubmit(onSubmit)} noValidate>
            {/* Error Alert */}
            {apiError && (
              <div
                className="rounded-md bg-red-50 p-4"
                role="alert"
                aria-live="polite"
              >
                <div className="flex">
                  <div className="flex-shrink-0">
                    <svg
                      className="h-5 w-5 text-red-400"
                      fill="currentColor"
                      viewBox="0 0 20 20"
                    >
                      <path
                        fillRule="evenodd"
                        d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.707 7.293a1 1 0 00-1.414 1.414L8.586 10l-1.293 1.293a1 1 0 101.414 1.414L10 11.414l1.293 1.293a1 1 0 001.414-1.414L11.414 10l1.293-1.293a1 1 0 00-1.414-1.414L10 8.586 8.707 7.293z"
                        clipRule="evenodd"
                      />
                    </svg>
                  </div>
                  <div className="ml-3">
                    <p className="text-sm font-medium text-red-800">{apiError}</p>
                  </div>
                </div>
              </div>
            )}

            {/* Email Field */}
            <div>
              <label
                htmlFor="email"
                className="block text-sm font-medium text-gray-700"
              >
                Email address
              </label>
              <div className="mt-1">
                <input
                  {...register('email')}
                  id="email"
                  type="email"
                  autoComplete="email"
                  autoFocus
                  placeholder="Enter your email"
                  className={`appearance-none block w-full px-3 py-2 border ${
                    errors.email ? 'border-red-300' : 'border-gray-300'
                  } rounded-md shadow-sm placeholder-gray-400 focus:outline-none focus:ring-primary-500 focus:border-primary-500 sm:text-sm`}
                  aria-invalid={errors.email ? 'true' : 'false'}
                  aria-describedby={errors.email ? 'email-error' : undefined}
                />
              </div>
              {errors.email && (
                <p className="mt-2 text-sm text-red-600" id="email-error" role="alert">
                  {errors.email.message}
                </p>
              )}
            </div>

            {/* Submit Button */}
            <div>
              <button
                type="submit"
                disabled={isSubmitting}
                className="w-full flex justify-center py-2 px-4 border border-transparent rounded-md shadow-sm text-sm font-medium text-white bg-primary-600 hover:bg-primary-700 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-primary-500 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {isSubmitting ? (
                  <>
                    <svg
                      className="animate-spin -ml-1 mr-3 h-5 w-5 text-white"
                      fill="none"
                      viewBox="0 0 24 24"
                    >
                      <circle
                        className="opacity-25"
                        cx="12"
                        cy="12"
                        r="10"
                        stroke="currentColor"
                        strokeWidth="4"
                      />
                      <path
                        className="opacity-75"
                        fill="currentColor"
                        d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"
                      />
                    </svg>
                    Sending reset link...
                  </>
                ) : (
                  'Send reset link'
                )}
              </button>
            </div>
          </form>

          {/* Back to Login */}
          <div className="mt-6 text-center">
            <a
              href="/login"
              className="font-medium text-primary-600 hover:text-primary-500"
            >
              Back to Login
            </a>
          </div>
        </div>
      </div>
      </div>
    </div>
  );
}
