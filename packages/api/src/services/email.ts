/**
 * Email Service (Story 18-1)
 *
 * Interface and implementations for sending transactional emails.
 * - InMemoryEmailService: Captures emails for testing
 * - SmtpEmailService: Real SMTP sending (when nodemailer is available)
 */

/** Represents an email to be sent. */
export interface EmailMessage {
  to: string;
  subject: string;
  html: string;
  text: string;
}

/** Interface for email sending. */
export interface IEmailService {
  /** Send an email. */
  sendEmail(message: EmailMessage): Promise<void>;
}

/** Email service configuration. */
export interface EmailConfig {
  smtpHost: string;
  smtpPort: number;
  smtpUser: string;
  smtpPass: string;
  fromAddress: string;
}

/**
 * In-memory email service for testing.
 * Captures all sent emails for inspection in tests.
 */
export class InMemoryEmailService implements IEmailService {
  readonly sentEmails: EmailMessage[] = [];

  async sendEmail(message: EmailMessage): Promise<void> {
    this.sentEmails.push(message);
  }

  /** Get all emails sent to a specific address. */
  getEmailsTo(address: string): EmailMessage[] {
    return this.sentEmails.filter((e) => e.to === address);
  }

  /** Clear all captured emails. */
  clear(): void {
    this.sentEmails.length = 0;
  }
}

/**
 * Console-logging email service for development.
 * Logs email details but does not actually send.
 */
export class ConsoleEmailService implements IEmailService {
  async sendEmail(message: EmailMessage): Promise<void> {
    console.log(`[EMAIL] To: ${message.to}`);
    console.log(`[EMAIL] Subject: ${message.subject}`);
    console.log(`[EMAIL] Text: ${message.text.substring(0, 200)}...`);
  }
}

// --- Email template helpers ---

const VERIFY_EMAIL_BASE_URL = process.env['VERIFY_EMAIL_URL'] ?? 'https://dash.tamma.dev/verify-email';
const RESET_PASSWORD_BASE_URL = process.env['RESET_PASSWORD_URL'] ?? 'https://dash.tamma.dev/reset-password';
const INVITE_BASE_URL = process.env['INVITE_URL'] ?? 'https://dash.tamma.dev/accept-invite';

/** Build a verification email message. */
export function buildVerificationEmail(to: string, name: string, token: string): EmailMessage {
  const verifyUrl = `${VERIFY_EMAIL_BASE_URL}?token=${encodeURIComponent(token)}`;
  return {
    to,
    subject: 'Verify your Tamma email address',
    html: `
      <div style="font-family: sans-serif; max-width: 600px; margin: 0 auto;">
        <h2>Welcome to Tamma, ${escapeHtml(name)}!</h2>
        <p>Please verify your email address by clicking the link below:</p>
        <p><a href="${escapeHtml(verifyUrl)}" style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">Verify Email</a></p>
        <p>Or copy and paste this URL into your browser:</p>
        <p style="word-break: break-all; color: #666;">${escapeHtml(verifyUrl)}</p>
        <p style="color: #999; font-size: 14px;">This link expires in 24 hours. If you did not create an account, you can ignore this email.</p>
      </div>
    `.trim(),
    text: `Welcome to Tamma, ${name}!\n\nPlease verify your email address by visiting:\n${verifyUrl}\n\nThis link expires in 24 hours. If you did not create an account, you can ignore this email.`,
  };
}

/** Build a password reset email message. */
export function buildPasswordResetEmail(to: string, name: string, token: string): EmailMessage {
  const resetUrl = `${RESET_PASSWORD_BASE_URL}?token=${encodeURIComponent(token)}`;
  return {
    to,
    subject: 'Reset your Tamma password',
    html: `
      <div style="font-family: sans-serif; max-width: 600px; margin: 0 auto;">
        <h2>Password Reset Request</h2>
        <p>Hi ${escapeHtml(name)},</p>
        <p>We received a request to reset your password. Click the link below to set a new password:</p>
        <p><a href="${escapeHtml(resetUrl)}" style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">Reset Password</a></p>
        <p>Or copy and paste this URL into your browser:</p>
        <p style="word-break: break-all; color: #666;">${escapeHtml(resetUrl)}</p>
        <p style="color: #999; font-size: 14px;">This link expires in 1 hour. If you did not request a password reset, you can ignore this email.</p>
      </div>
    `.trim(),
    text: `Hi ${name},\n\nWe received a request to reset your password. Visit the link below to set a new password:\n${resetUrl}\n\nThis link expires in 1 hour. If you did not request a password reset, you can ignore this email.`,
  };
}

/** Build a tenant invite email message. */
export function buildTenantInviteEmail(to: string, tenantName: string, inviterName: string, token: string, role: string): EmailMessage {
  const inviteUrl = `${INVITE_BASE_URL}?token=${encodeURIComponent(token)}`;
  return {
    to,
    subject: `You've been invited to join ${tenantName} on Tamma`,
    html: `
      <div style="font-family: sans-serif; max-width: 600px; margin: 0 auto;">
        <h2>You're invited!</h2>
        <p>${escapeHtml(inviterName)} has invited you to join <strong>${escapeHtml(tenantName)}</strong> on Tamma as a <strong>${escapeHtml(role)}</strong>.</p>
        <p><a href="${escapeHtml(inviteUrl)}" style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">Accept Invitation</a></p>
        <p>Or copy and paste this URL into your browser:</p>
        <p style="word-break: break-all; color: #666;">${escapeHtml(inviteUrl)}</p>
        <p style="color: #999; font-size: 14px;">This invitation expires in 72 hours.</p>
      </div>
    `.trim(),
    text: `${inviterName} has invited you to join ${tenantName} on Tamma as a ${role}.\n\nAccept the invitation:\n${inviteUrl}\n\nThis invitation expires in 72 hours.`,
  };
}

/** Simple HTML escaping for template values. */
function escapeHtml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}
