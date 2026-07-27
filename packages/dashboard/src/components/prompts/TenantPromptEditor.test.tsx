// @vitest-environment jsdom
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Mock } from 'vitest';
import { TenantPromptEditor } from './TenantPromptEditor.js';
import type {
  PromptDetail,
  RenderedResult,
  UpsertPromptInput,
} from '../../hooks/useTenantPrompts.js';

type UpsertOverrideFn = (
  role: string,
  action: string,
  input: UpsertPromptInput,
) => Promise<PromptDetail>;
type DeleteOverrideFn = (role: string, action: string) => Promise<boolean>;
type RenderPreviewFn = (
  role: string,
  action: string,
  variables: Record<string, string>,
) => Promise<RenderedResult | null>;

const SYSTEM_DEFAULT: PromptDetail = {
  role: 'developer',
  action: 'implement',
  template: 'You are a {{role}} implementing {{task}}',
  systemPrompt: 'dev system',
  variables: ['role', 'task'],
  enableTools: true,
  maxTokens: 4096,
  source: 'system',
};

const USER_OVERRIDE: PromptDetail = {
  ...SYSTEM_DEFAULT,
  template: 'Custom: {{role}} doing {{task}}',
  source: 'user',
};

function setup(opts?: {
  detail?: PromptDetail;
  readOnly?: boolean;
  upsertOverride?: Mock<UpsertOverrideFn>;
  deleteOverride?: Mock<DeleteOverrideFn>;
  onSaved?: Mock<() => void>;
  onClose?: Mock<() => void>;
  renderPreview?: Mock<RenderPreviewFn>;
}) {
  const detail = opts?.detail ?? SYSTEM_DEFAULT;
  const getPrompt = vi.fn<(role: string, action: string) => Promise<PromptDetail | null>>()
    .mockResolvedValue(detail);
  const upsertOverride =
    opts?.upsertOverride ?? vi.fn<UpsertOverrideFn>().mockResolvedValue(detail);
  const deleteOverride =
    opts?.deleteOverride ?? vi.fn<DeleteOverrideFn>().mockResolvedValue(true);
  const renderPreview =
    opts?.renderPreview ?? vi.fn<RenderPreviewFn>().mockResolvedValue(null);
  const onSaved = opts?.onSaved ?? vi.fn<() => void>();
  const onClose = opts?.onClose ?? vi.fn<() => void>();

  render(
    <TenantPromptEditor
      open={true}
      role={detail.role}
      action={detail.action}
      isOverride={detail.source === 'user'}
      readOnly={opts?.readOnly ?? false}
      onClose={onClose}
      onSaved={onSaved}
      getPrompt={getPrompt}
      upsertOverride={upsertOverride}
      deleteOverride={deleteOverride}
      renderPreview={renderPreview}
    />,
  );

  return { getPrompt, upsertOverride, deleteOverride, renderPreview, onSaved, onClose };
}

describe('TenantPromptEditor', () => {
  const user = userEvent.setup();

  it('loads the prompt detail on open', async () => {
    const { getPrompt } = setup();
    await waitFor(() => expect(getPrompt).toHaveBeenCalledWith('developer', 'implement'));
    expect(await screen.findByDisplayValue(/You are a \{\{role\}\}/)).toBeInTheDocument();
  });

  it('shows the "system default" info banner when isOverride=false', async () => {
    setup();
    expect(
      await screen.findByText(/Saving will create an override/i),
    ).toBeInTheDocument();
  });

  it('shows the "override" info banner and Reset button when isOverride=true', async () => {
    setup({ detail: USER_OVERRIDE });
    expect(await screen.findByText(/This is a tenant override/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Reset to Default/i })).toBeInTheDocument();
  });

  it('calls upsertOverride on Save click with edited template', async () => {
    const upsertOverride = vi.fn<UpsertOverrideFn>().mockResolvedValue(SYSTEM_DEFAULT);
    const onSaved = vi.fn<() => void>();
    setup({ upsertOverride, onSaved });
    const textarea = await screen.findByLabelText(/^Template$/i);
    await user.clear(textarea);
    await user.type(textarea, 'New template body');
    await user.click(screen.getByRole('button', { name: /^Save$/i }));
    await waitFor(() =>
      expect(upsertOverride).toHaveBeenCalledWith(
        'developer',
        'implement',
        expect.objectContaining({ template: 'New template body' }),
      ),
    );
    expect(onSaved).toHaveBeenCalled();
  });

  it('calls deleteOverride after confirm on Reset click', async () => {
    const deleteOverride = vi.fn<DeleteOverrideFn>().mockResolvedValue(true);
    const onSaved = vi.fn<() => void>();
    setup({ detail: USER_OVERRIDE, deleteOverride, onSaved });
    await screen.findByText(/This is a tenant override/i);
    await user.click(screen.getByRole('button', { name: /Reset to Default/i }));
    // Confirm dialog appears; click the confirm action
    const confirmBtn = await screen.findByRole('button', { name: /^Reset$/i });
    await user.click(confirmBtn);
    await waitFor(() =>
      expect(deleteOverride).toHaveBeenCalledWith('developer', 'implement'),
    );
    expect(onSaved).toHaveBeenCalled();
  });

  it('hides Save and Reset buttons in read-only mode', async () => {
    setup({ detail: USER_OVERRIDE, readOnly: true });
    await screen.findByText(/This is a tenant override/i);
    expect(screen.queryByRole('button', { name: /^Save$/i })).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: /Reset to Default/i }),
    ).not.toBeInTheDocument();
  });

  it('auto-extracts variables from template and displays them', async () => {
    setup();
    await screen.findByDisplayValue(/You are a \{\{role\}\}/);
    const vars = await screen.findByTestId('extracted-variables');
    expect(vars.textContent).toContain('role');
    expect(vars.textContent).toContain('task');
  });
});
