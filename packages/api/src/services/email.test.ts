/**
 * Tests for email service and templates (Story 18-1).
 */

import { describe, it, expect, beforeEach } from 'vitest';
import {
  InMemoryEmailService,
  buildVerificationEmail,
  buildPasswordResetEmail,
  buildTenantInviteEmail,
} from './email.js';

describe('InMemoryEmailService', () => {
  let emailService: InMemoryEmailService;

  beforeEach(() => {
    emailService = new InMemoryEmailService();
  });

  it('should capture sent emails', async () => {
    await emailService.sendEmail({
      to: 'test@example.com',
      subject: 'Test',
      html: '<p>Test</p>',
      text: 'Test',
    });

    expect(emailService.sentEmails).toHaveLength(1);
    expect(emailService.sentEmails[0]!.to).toBe('test@example.com');
  });

  it('should filter emails by recipient', async () => {
    await emailService.sendEmail({ to: 'alice@test.com', subject: 'A', html: '', text: '' });
    await emailService.sendEmail({ to: 'bob@test.com', subject: 'B', html: '', text: '' });
    await emailService.sendEmail({ to: 'alice@test.com', subject: 'C', html: '', text: '' });

    expect(emailService.getEmailsTo('alice@test.com')).toHaveLength(2);
    expect(emailService.getEmailsTo('bob@test.com')).toHaveLength(1);
    expect(emailService.getEmailsTo('nobody@test.com')).toHaveLength(0);
  });

  it('should clear all emails', async () => {
    await emailService.sendEmail({ to: 'test@test.com', subject: 'X', html: '', text: '' });
    emailService.clear();
    expect(emailService.sentEmails).toHaveLength(0);
  });
});

describe('buildVerificationEmail', () => {
  it('should build a verification email with correct fields', () => {
    const email = buildVerificationEmail('user@test.com', 'Alice', 'abc123');
    expect(email.to).toBe('user@test.com');
    expect(email.subject).toContain('Verify');
    expect(email.html).toContain('Alice');
    expect(email.html).toContain('abc123');
    expect(email.text).toContain('abc123');
    expect(email.text).toContain('24 hours');
  });

  it('should escape HTML in name', () => {
    const email = buildVerificationEmail('user@test.com', '<script>alert("xss")</script>', 'token');
    expect(email.html).not.toContain('<script>');
    expect(email.html).toContain('&lt;script&gt;');
  });
});

describe('buildPasswordResetEmail', () => {
  it('should build a password reset email with correct fields', () => {
    const email = buildPasswordResetEmail('user@test.com', 'Bob', 'resettoken');
    expect(email.to).toBe('user@test.com');
    expect(email.subject).toContain('Reset');
    expect(email.html).toContain('Bob');
    expect(email.html).toContain('resettoken');
    expect(email.text).toContain('1 hour');
  });
});

describe('buildTenantInviteEmail', () => {
  it('should build an invite email with correct fields', () => {
    const email = buildTenantInviteEmail('user@test.com', 'AcmeCorp', 'Charlie', 'invitetoken', 'admin');
    expect(email.to).toBe('user@test.com');
    expect(email.subject).toContain('AcmeCorp');
    expect(email.html).toContain('Charlie');
    expect(email.html).toContain('invitetoken');
    expect(email.html).toContain('admin');
    expect(email.text).toContain('72 hours');
  });
});
