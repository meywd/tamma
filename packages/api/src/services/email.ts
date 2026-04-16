/**
 * Email Service
 *
 * Interface + implementations for sending emails.
 * ConsoleEmailService logs to stdout (dev/test).
 * A production implementation (e.g. SES, Resend) can be swapped in via DI.
 */

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

export interface EmailMessage {
  to: string;
  subject: string;
  html: string;
  text: string;
}

export interface IEmailService {
  sendEmail(message: EmailMessage): Promise<void>;
}

// ---------------------------------------------------------------------------
// Console implementation (dev/test)
// ---------------------------------------------------------------------------

export class ConsoleEmailService implements IEmailService {
  /** Captured messages for test assertions. */
  sent: EmailMessage[] = [];

  async sendEmail(message: EmailMessage): Promise<void> {
    this.sent.push(message);
    console.log(`[EMAIL] To: ${message.to} | Subject: ${message.subject}`);
  }
}

// ---------------------------------------------------------------------------
// Email template builders
// ---------------------------------------------------------------------------

/**
 * Build a tenant invite email.
 *
 * @param inviterName  Display name of the person who invited
 * @param tenantName   Name of the organization
 * @param rawToken     The raw invite token (only goes in the email, never stored)
 * @param frontendUrl  Base URL of the frontend
 * @param expiryHours  Number of hours until the invite expires
 */
export function buildTenantInviteEmail(
  inviterName: string,
  tenantName: string,
  rawToken: string,
  frontendUrl: string,
  expiryHours: number,
): EmailMessage {
  const inviteUrl = `${frontendUrl}/orgs/join?token=${encodeURIComponent(rawToken)}`;

  const html = `
<!DOCTYPE html>
<html>
<body style="font-family: sans-serif; max-width: 600px; margin: 0 auto; padding: 20px;">
  <h2>You've been invited to ${escapeHtml(tenantName)}</h2>
  <p>${escapeHtml(inviterName)} has invited you to join <strong>${escapeHtml(tenantName)}</strong> on Tamma.</p>
  <p>
    <a href="${escapeHtml(inviteUrl)}"
       style="display: inline-block; padding: 12px 24px; background: #2563eb; color: white; text-decoration: none; border-radius: 6px;">
      Accept Invitation
    </a>
  </p>
  <p style="color: #666; font-size: 14px;">
    This invitation expires in ${expiryHours} hours.
    If you did not expect this email, you can safely ignore it.
  </p>
</body>
</html>`.trim();

  const text = [
    `You've been invited to ${tenantName}`,
    '',
    `${inviterName} has invited you to join ${tenantName} on Tamma.`,
    '',
    `Accept the invitation: ${inviteUrl}`,
    '',
    `This invitation expires in ${expiryHours} hours.`,
  ].join('\n');

  return {
    to: '', // caller sets this
    subject: `Join ${tenantName} on Tamma`,
    html,
    text,
  };
}

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
