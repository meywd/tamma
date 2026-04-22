// @vitest-environment jsdom
import { render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { SecretRevealModal } from '../SecretRevealModal.js';

describe('SecretRevealModal', () => {
  const user = userEvent.setup();

  function renderModal(overrides?: Partial<React.ComponentProps<typeof SecretRevealModal>>) {
    const onClose = vi.fn();
    const utils = render(
      <SecretRevealModal
        open
        name="db/app-role"
        version={1}
        plaintext="hunter2-plaintext"
        expiresAt={new Date(Date.now() + 60_000).toISOString()}
        onClose={onClose}
        {...overrides}
      />,
    );
    return { ...utils, onClose };
  }

  it('renders the plaintext and the warning notice', () => {
    renderModal();

    expect(screen.getByText('Secret created: db/app-role')).toBeInTheDocument();
    expect(screen.getByText(/will not be shown again/i)).toBeInTheDocument();
    expect(screen.getByDisplayValue('hunter2-plaintext')).toBeInTheDocument();
  });

  it('disables the close button until the acknowledgement box is ticked', async () => {
    const { onClose } = renderModal();

    const closeBtn = screen.getByRole('button', { name: /close/i });
    expect(closeBtn).toBeDisabled();

    // Clicking close without acknowledgement is a no-op.
    await user.click(closeBtn);
    expect(onClose).not.toHaveBeenCalled();

    // Tick the box.
    await user.click(screen.getByRole('checkbox'));
    expect(closeBtn).toBeEnabled();

    await user.click(closeBtn);
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('copies the plaintext to the clipboard when Copy is clicked', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: { writeText },
    });

    renderModal();

    await user.click(screen.getByRole('button', { name: /copy/i }));

    expect(writeText).toHaveBeenCalledWith('hunter2-plaintext');
    expect(await screen.findByRole('button', { name: /copied!/i })).toBeInTheDocument();
  });

  it('falls back to a manual-copy notice when the clipboard API fails', async () => {
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: {
        writeText: vi.fn().mockRejectedValue(new Error('denied')),
      },
    });

    renderModal();

    await user.click(screen.getByRole('button', { name: /copy/i }));
    expect(await screen.findByText(/select the value manually/i)).toBeInTheDocument();
  });

  it('does not render when open=false', () => {
    renderModal({ open: false });
    expect(screen.queryByText('Secret created: db/app-role')).not.toBeInTheDocument();
  });

  it('escape does not dismiss until the user acknowledges', async () => {
    const { onClose } = renderModal();

    const dialog = screen.getByRole('dialog');
    fireEvent.keyDown(dialog, { key: 'Escape' });
    expect(onClose).not.toHaveBeenCalled();

    await user.click(screen.getByRole('checkbox'));
    fireEvent.keyDown(dialog, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('resets acknowledgement when a new plaintext prop lands', () => {
    const { rerender } = renderModal();

    const checkbox = screen.getByRole('checkbox');
    fireEvent.click(checkbox);
    expect(checkbox).toBeChecked();

    rerender(
      <SecretRevealModal
        open
        name="db/app-role"
        version={2}
        plaintext="rotated-new-plaintext"
        expiresAt={new Date(Date.now() + 60_000).toISOString()}
        onClose={() => {}}
      />,
    );

    expect(screen.getByRole('checkbox')).not.toBeChecked();
  });
});
