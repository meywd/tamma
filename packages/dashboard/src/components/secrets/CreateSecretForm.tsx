import { useCallback, useState, type JSX } from 'react';
import type {
  CreateSecretBody,
  SecretPurpose,
} from '../../services/secrets/secrets-api-client.js';

/**
 * Story 29-4 / 29-5 — create-secret form. Fires a single POST to the
 * platform- or tenant-scoped create endpoint (the caller parameterises
 * the scope + onSubmit). Response is the reveal envelope; the parent
 * is responsible for exchanging `revealToken` once and mounting the
 * <SecretRevealModal /> with the plaintext.
 *
 * Validation:
 *   - Name: slug grammar (lower-kebab-case with optional `/` segments).
 *   - Purpose: enum.
 *   - Plaintext: 8–8192 chars (server enforces; client also pre-validates).
 *   - RotationDays: 0 → none, >=1 → every N days.
 */

const NAME_SLUG_RE = /^[a-z0-9]+(?:-[a-z0-9]+)*(?:\/[a-z0-9]+(?:-[a-z0-9]+)*)*$/;

const PURPOSES: SecretPurpose[] = [
  'Generic',
  'DbCredential',
  'ApiKey',
  'HmacSharedSecret',
  'WebhookSigning',
  'JwtSigning',
  'EncryptionKey',
  'OAuthClientSecret',
];

export interface CreateSecretFormProps {
  readonly scopeLabel: string;
  readonly onSubmit: (body: CreateSecretBody) => Promise<void>;
  readonly onCancel: () => void;
  /** True while the parent's create+reveal flow is in flight. */
  readonly submitting?: boolean;
}

export function CreateSecretForm({
  scopeLabel,
  onSubmit,
  onCancel,
  submitting,
}: CreateSecretFormProps): JSX.Element {
  const [name, setName] = useState('');
  const [purpose, setPurpose] = useState<SecretPurpose>('Generic');
  const [plaintext, setPlaintext] = useState('');
  const [rotationDays, setRotationDays] = useState<number | ''>('');
  const [errors, setErrors] = useState<Record<string, string>>({});

  const validate = useCallback((): boolean => {
    const next: Record<string, string> = {};
    if (!name.trim()) next.name = 'Name is required';
    else if (!NAME_SLUG_RE.test(name)) next.name = 'Name must be lower-kebab-case (e.g. db/app-role)';
    if (!plaintext) next.plaintext = 'Plaintext is required';
    else if (plaintext.length < 8) next.plaintext = 'Plaintext must be at least 8 characters';
    else if (plaintext.length > 8192) next.plaintext = 'Plaintext must be at most 8192 characters';
    if (typeof rotationDays === 'number' && rotationDays < 0)
      next.rotationDays = 'Rotation days cannot be negative';
    setErrors(next);
    return Object.keys(next).length === 0;
  }, [name, plaintext, rotationDays]);

  const handleSubmit = useCallback(
    async (e: React.FormEvent) => {
      e.preventDefault();
      if (!validate()) return;
      const body: CreateSecretBody = {
        name,
        purpose,
        plaintext,
        rotationDays: typeof rotationDays === 'number' ? rotationDays : 0,
      };
      await onSubmit(body);
    },
    [name, purpose, plaintext, rotationDays, onSubmit, validate],
  );

  return (
    <form
      onSubmit={handleSubmit}
      className="space-y-4 bg-white rounded-lg border border-gray-200 p-6 dark:bg-gray-800 dark:border-gray-700"
      aria-label={`Create ${scopeLabel} secret`}
    >
      <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">
        Create {scopeLabel} secret
      </h3>

      <div>
        <label htmlFor="secret-name" className="block text-sm font-medium text-gray-700 mb-1 dark:text-gray-300">
          Name
        </label>
        <input
          id="secret-name"
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="db/app-role"
          className="w-full font-mono text-sm px-3 py-2 border border-gray-300 rounded-md dark:border-gray-600"
          aria-invalid={Boolean(errors.name)}
          aria-describedby={errors.name ? 'secret-name-error' : undefined}
        />
        {errors.name ? (
          <p id="secret-name-error" className="text-xs text-red-600 mt-1 dark:text-red-400">
            {errors.name}
          </p>
        ) : (
          <p className="text-xs text-gray-500 mt-1 dark:text-gray-400">
            Lower-kebab-case with optional <code>/</code> segments.
          </p>
        )}
      </div>

      <div>
        <label htmlFor="secret-purpose" className="block text-sm font-medium text-gray-700 mb-1 dark:text-gray-300">
          Purpose
        </label>
        <select
          id="secret-purpose"
          value={purpose}
          onChange={(e) => setPurpose(e.target.value as SecretPurpose)}
          className="w-full text-sm px-3 py-2 border border-gray-300 rounded-md dark:border-gray-600"
        >
          {PURPOSES.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
      </div>

      <div>
        <label htmlFor="secret-plaintext" className="block text-sm font-medium text-gray-700 mb-1 dark:text-gray-300">
          Initial value (plaintext)
        </label>
        <input
          id="secret-plaintext"
          type="password"
          value={plaintext}
          onChange={(e) => setPlaintext(e.target.value)}
          className="w-full font-mono text-sm px-3 py-2 border border-gray-300 rounded-md dark:border-gray-600"
          aria-invalid={Boolean(errors.plaintext)}
          aria-describedby={errors.plaintext ? 'secret-plaintext-error' : 'secret-plaintext-help'}
        />
        {errors.plaintext ? (
          <p id="secret-plaintext-error" className="text-xs text-red-600 mt-1 dark:text-red-400">
            {errors.plaintext}
          </p>
        ) : (
          <p id="secret-plaintext-help" className="text-xs text-gray-500 mt-1 dark:text-gray-400">
            You&apos;ll see this value once on the next screen. Save it before closing.
          </p>
        )}
      </div>

      <div>
        <label htmlFor="secret-rotation-days" className="block text-sm font-medium text-gray-700 mb-1 dark:text-gray-300">
          Rotation cadence (days)
        </label>
        <input
          id="secret-rotation-days"
          type="number"
          min={0}
          value={rotationDays}
          onChange={(e) => {
            const raw = e.target.value;
            setRotationDays(raw === '' ? '' : Math.max(0, parseInt(raw, 10) || 0));
          }}
          placeholder="0 = manual only"
          className="w-32 text-sm px-3 py-2 border border-gray-300 rounded-md dark:border-gray-600"
          aria-invalid={Boolean(errors.rotationDays)}
        />
        {errors.rotationDays ? (
          <p className="text-xs text-red-600 mt-1 dark:text-red-400">{errors.rotationDays}</p>
        ) : (
          <p className="text-xs text-gray-500 mt-1 dark:text-gray-400">0 disables scheduled rotation.</p>
        )}
      </div>

      <div className="flex justify-end gap-3 pt-2 border-t border-gray-100 dark:border-gray-800">
        <button
          type="button"
          onClick={onCancel}
          disabled={submitting}
          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50 dark:bg-gray-800 dark:text-gray-300 dark:border-gray-600 dark:hover:bg-gray-800"
        >
          Cancel
        </button>
        <button
          type="submit"
          disabled={submitting}
          className="px-4 py-2 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md disabled:opacity-50"
        >
          {submitting ? 'Creating…' : 'Create secret'}
        </button>
      </div>
    </form>
  );
}
