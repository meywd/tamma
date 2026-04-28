// @vitest-environment jsdom
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { CreateSecretForm } from '../CreateSecretForm.js';

describe('CreateSecretForm', () => {
  const user = userEvent.setup();

  it('validates the name slug grammar before submitting', async () => {
    const onSubmit = vi.fn();
    const onCancel = vi.fn();
    render(
      <CreateSecretForm
        scopeLabel="Platform"
        onSubmit={onSubmit}
        onCancel={onCancel}
      />,
    );

    await user.type(screen.getByLabelText(/Name/), 'Not A Valid_Slug');
    await user.type(screen.getByLabelText(/Initial value/), 'long-enough-plaintext');

    await user.click(screen.getByRole('button', { name: /create secret/i }));

    expect(
      screen.getByText(/lower-kebab-case/),
    ).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('rejects plaintext shorter than 8 characters', async () => {
    const onSubmit = vi.fn();
    render(
      <CreateSecretForm
        scopeLabel="Platform"
        onSubmit={onSubmit}
        onCancel={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText(/Name/), 'db/app-role');
    await user.type(screen.getByLabelText(/Initial value/), 'short');
    await user.click(screen.getByRole('button', { name: /create secret/i }));

    expect(screen.getByText(/at least 8 characters/i)).toBeInTheDocument();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('submits the body with a 0 rotationDays default when empty', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(
      <CreateSecretForm
        scopeLabel="Platform"
        onSubmit={onSubmit}
        onCancel={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText(/Name/), 'db/app-role');
    await user.selectOptions(screen.getByLabelText(/Purpose/), 'DbCredential');
    await user.type(screen.getByLabelText(/Initial value/), 'long-enough-value-yes');

    await user.click(screen.getByRole('button', { name: /create secret/i }));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit).toHaveBeenCalledWith({
      name: 'db/app-role',
      purpose: 'DbCredential',
      plaintext: 'long-enough-value-yes',
      rotationDays: 0,
    });
  });

  it('submits with rotationDays when supplied', async () => {
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(
      <CreateSecretForm
        scopeLabel="Organization"
        onSubmit={onSubmit}
        onCancel={vi.fn()}
      />,
    );

    await user.type(screen.getByLabelText(/Name/), 'cranl/api-key');
    await user.type(screen.getByLabelText(/Initial value/), 'long-enough-value-yes');
    await user.type(screen.getByLabelText(/Rotation cadence/), '90');

    await user.click(screen.getByRole('button', { name: /create secret/i }));

    expect(onSubmit).toHaveBeenCalledWith(
      expect.objectContaining({ rotationDays: 90 }),
    );
  });

  it('cancel button invokes onCancel', async () => {
    const onCancel = vi.fn();
    render(
      <CreateSecretForm
        scopeLabel="Platform"
        onSubmit={vi.fn()}
        onCancel={onCancel}
      />,
    );

    await user.click(screen.getByRole('button', { name: /cancel/i }));
    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it('disables submit while submitting', () => {
    render(
      <CreateSecretForm
        scopeLabel="Platform"
        onSubmit={vi.fn()}
        onCancel={vi.fn()}
        submitting
      />,
    );

    expect(screen.getByRole('button', { name: /creating/i })).toBeDisabled();
  });
});
