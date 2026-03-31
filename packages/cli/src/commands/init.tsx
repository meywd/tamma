import React, { useState, useCallback, useEffect } from 'react';
import { render, Box, Text, useApp } from 'ink';
import TextInput from 'ink-text-input';
import SelectInput from 'ink-select-input';
import Spinner from 'ink-spinner';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { execSync, execFileSync } from 'node:child_process';
import dotenv from 'dotenv';
import { generateEnvFile, mergeIntoEnvFile, generateRepoConfigFile, generateProvidersFile } from '../config.js';
import { runPreflight } from '../preflight.js';
import type { PreflightResults } from '../preflight.js';
import { colorProp } from '../colors.js';

type Phase = 'preflight' | 'wizard' | 'postconfig' | 'done';
type WizardStep = 'token' | 'anthropicKey' | 'owner' | 'repo' | 'labels' | 'model' | 'budget' | 'workdir' | 'approval';

interface Answers {
  token: string;
  anthropicKey: string;
  owner: string;
  repo: string;
  labels: string;
  model: string;
  maxBudgetUsd: number;
  workingDirectory: string;
  approvalMode: string;
}

interface PostConfigCheck {
  name: string;
  status: 'pending' | 'running' | 'passed' | 'failed' | 'skipped';
  message?: string;
}

function PreflightDisplay({ results, onContinue }: { results: PreflightResults; onContinue: () => void }): React.JSX.Element {
  useEffect(() => {
    if (results.allRequiredPassed) {
      const timer = setTimeout(onContinue, 500);
      return () => { clearTimeout(timer); };
    }
    return undefined;
  }, [results.allRequiredPassed, onContinue]);

  return (
    <Box flexDirection="column" paddingX={1}>
      <Text bold {...colorProp('cyan')}>Pre-flight Checks</Text>
      <Text dimColor>{'─'.repeat(40)}</Text>
      {results.checks.map((check, i) => (
        <Box key={i}>
          <Text {...colorProp(check.passed ? 'green' : check.required ? 'red' : 'yellow')}>
            {check.passed ? ' ✓' : check.required ? ' ✗' : ' ○'}
          </Text>
          <Text> {check.name}: </Text>
          <Text dimColor>{check.message}</Text>
        </Box>
      ))}
      {!results.allRequiredPassed && (
        <Box marginTop={1}>
          <Text {...colorProp('red')} bold>Required checks failed. Please fix the issues above and try again.</Text>
        </Box>
      )}
    </Box>
  );
}

function WizardForm({
  preflightResults,
  onComplete,
}: {
  preflightResults: PreflightResults;
  onComplete: (answers: Answers) => void;
}): React.JSX.Element {
  const [step, setStep] = useState<WizardStep>('token');
  const [inputValue, setInputValue] = useState('');
  const [answers, setAnswers] = useState<Answers>({
    token: '',
    anthropicKey: '',
    owner: preflightResults.detectedOwner ?? '',
    repo: preflightResults.detectedRepo ?? '',
    labels: 'tamma',
    model: 'claude-sonnet-4-5',
    maxBudgetUsd: 1.0,
    workingDirectory: process.cwd(),
    approvalMode: 'cli',
  });

  // Pre-fill input with detected values when navigating to owner/repo steps
  useEffect(() => {
    if (step === 'owner' && preflightResults.detectedOwner) {
      setInputValue(preflightResults.detectedOwner);
    } else if (step === 'repo' && preflightResults.detectedRepo) {
      setInputValue(preflightResults.detectedRepo);
    } else if (step === 'budget') {
      setInputValue('1.00');
    } else if (step === 'workdir') {
      setInputValue(process.cwd());
    }
  }, [step, preflightResults]);

  const handleSubmit = useCallback(
    (value: string) => {
      setInputValue('');
      switch (step) {
        case 'token':
          setAnswers((a) => ({ ...a, token: value }));
          setStep('anthropicKey');
          break;
        case 'anthropicKey':
          setAnswers((a) => ({ ...a, anthropicKey: value }));
          setStep('owner');
          break;
        case 'owner':
          setAnswers((a) => ({ ...a, owner: value || a.owner }));
          setStep('repo');
          break;
        case 'repo':
          setAnswers((a) => ({ ...a, repo: value || a.repo }));
          setStep('labels');
          break;
        case 'labels':
          setAnswers((a) => ({ ...a, labels: value || 'tamma' }));
          setStep('model');
          break;
        case 'budget': {
          const parsed = parseFloat(value);
          setAnswers((a) => ({ ...a, maxBudgetUsd: isNaN(parsed) ? 1.0 : parsed }));
          setStep('workdir');
          break;
        }
        case 'workdir':
          setAnswers((a) => ({ ...a, workingDirectory: value || process.cwd() }));
          setStep('approval');
          break;
        default:
          break;
      }
    },
    [step],
  );

  const handleModelSelect = useCallback(
    (item: { value: string }) => {
      setAnswers((a) => {
        const updated = { ...a, model: item.value };
        return updated;
      });
      setStep('budget');
    },
    [],
  );

  const handleApprovalSelect = useCallback(
    (item: { value: string }) => {
      const finalAnswers = { ...answers, approvalMode: item.value };
      setAnswers(finalAnswers);
      onComplete(finalAnswers);
    },
    [answers, onComplete],
  );

  return (
    <Box flexDirection="column" paddingX={1}>
      <Text bold {...colorProp('cyan')}>Tamma Configuration Wizard</Text>
      <Text dimColor>{'─'.repeat(40)}</Text>

      {step === 'token' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>GitHub Token (or press Enter to use GITHUB_TOKEN env var):</Text>
          <TextInput value={inputValue} onChange={setInputValue} onSubmit={handleSubmit} mask="*" />
        </Box>
      )}

      {step === 'anthropicKey' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>Anthropic API Key (or press Enter to use ANTHROPIC_API_KEY env var):</Text>
          <TextInput value={inputValue} onChange={setInputValue} onSubmit={handleSubmit} mask="*" />
        </Box>
      )}

      {step === 'owner' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>GitHub Owner (organization or username){preflightResults.detectedOwner ? ` [${preflightResults.detectedOwner}]` : ''}:</Text>
          <TextInput value={inputValue} onChange={setInputValue} onSubmit={handleSubmit} />
        </Box>
      )}

      {step === 'repo' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>GitHub Repository name{preflightResults.detectedRepo ? ` [${preflightResults.detectedRepo}]` : ''}:</Text>
          <TextInput value={inputValue} onChange={setInputValue} onSubmit={handleSubmit} />
        </Box>
      )}

      {step === 'labels' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>Issue labels (comma-separated, default: tamma):</Text>
          <TextInput value={inputValue} onChange={setInputValue} onSubmit={handleSubmit} />
        </Box>
      )}

      {step === 'model' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>AI Model:</Text>
          <SelectInput
            items={[
              { label: 'Claude Sonnet 4.5 — fast, cost-effective', value: 'claude-sonnet-4-5' },
              { label: 'Claude Opus 4.6 — most capable', value: 'claude-opus-4-6' },
            ]}
            onSelect={handleModelSelect}
          />
        </Box>
      )}

      {step === 'budget' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>Max budget per issue in USD (default: 1.00):</Text>
          <TextInput value={inputValue} onChange={setInputValue} onSubmit={handleSubmit} />
        </Box>
      )}

      {step === 'workdir' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>Working directory (default: current directory):</Text>
          <TextInput value={inputValue} onChange={setInputValue} onSubmit={handleSubmit} />
        </Box>
      )}

      {step === 'approval' && (
        <Box flexDirection="column" marginTop={1}>
          <Text>Approval mode:</Text>
          <SelectInput
            items={[
              { label: 'CLI — manual approval before implementation', value: 'cli' },
              { label: 'Auto — approve all plans automatically', value: 'auto' },
            ]}
            onSelect={handleApprovalSelect}
          />
        </Box>
      )}
    </Box>
  );
}

function PostConfigDisplay({
  checks,
  configPath,
}: {
  checks: PostConfigCheck[];
  configPath: string;
}): React.JSX.Element {
  return (
    <Box flexDirection="column" paddingX={1}>
      <Text {...colorProp('green')} bold>Configuration saved to {configPath}</Text>
      <Text />
      <Text bold {...colorProp('cyan')}>Post-config validation</Text>
      <Text dimColor>{'─'.repeat(40)}</Text>
      {checks.map((check, i) => (
        <Box key={i}>
          {check.status === 'running' && (
            <Text {...colorProp('cyan')}> <Spinner type="dots" /></Text>
          )}
          {check.status === 'passed' && (
            <Text {...colorProp('green')}> ✓</Text>
          )}
          {check.status === 'failed' && (
            <Text {...colorProp('red')}> ✗</Text>
          )}
          {check.status === 'skipped' && (
            <Text {...colorProp('yellow')}> ○</Text>
          )}
          {check.status === 'pending' && (
            <Text dimColor> ·</Text>
          )}
          <Text> {check.name}</Text>
          {check.message !== undefined && <Text dimColor> — {check.message}</Text>}
        </Box>
      ))}
    </Box>
  );
}

function DoneDisplay({ configPath, envPath, hasSkippedCredentials }: { configPath: string; envPath: string; hasSkippedCredentials: boolean }): React.JSX.Element {
  return (
    <Box flexDirection="column" paddingX={1} marginTop={1}>
      <Text bold {...colorProp('green')}>Setup complete!</Text>
      <Text />
      {hasSkippedCredentials && (
        <Text {...colorProp('yellow')}>Warning: Set missing credentials in .env before starting</Text>
      )}
      <Text bold>Next steps:</Text>
      <Text>  1. Run <Text {...colorProp('cyan')}>tamma start</Text> to begin processing issues</Text>
      <Text>  2. Label a GitHub issue with <Text {...colorProp('cyan')}>tamma</Text> to trigger processing</Text>
      <Text />
      <Text dimColor>Config: {configPath}</Text>
      <Text dimColor>Credentials: {envPath}</Text>
      <Text dimColor>Docs: https://github.com/your-org/tamma#readme</Text>
    </Box>
  );
}

function InitApp(): React.JSX.Element {
  const { exit } = useApp();
  const [phase, setPhase] = useState<Phase>('preflight');
  const [preflightResults, setPreflightResults] = useState<PreflightResults | null>(null);
  const [configPath, setConfigPath] = useState('');
  const [envPath, setEnvPath] = useState('');
  const [wizardAnswers, setWizardAnswers] = useState<Answers | null>(null);
  const [postChecks, setPostChecks] = useState<PostConfigCheck[]>([]);

  // Run preflight on mount
  useEffect(() => {
    const results = runPreflight();
    setPreflightResults(results);
    if (!results.allRequiredPassed) {
      setTimeout(() => { exit(); }, 100);
    }
  }, [exit]);

  const handlePreflightContinue = useCallback(() => {
    setPhase('wizard');
  }, []);

  const handleWizardComplete = useCallback((answers: Answers) => {
    // Phase 1: Write ~/.tamma/providers.json (user-level credentials)
    const tammaHome = path.join(os.homedir(), '.tamma');
    if (!fs.existsSync(tammaHome)) {
      fs.mkdirSync(tammaHome, { recursive: true });
    }
    const providersContent = generateProvidersFile([
      { name: 'anthropic', apiKey: answers.anthropicKey, defaultModel: answers.model },
    ]);
    const providersDest = path.join(tammaHome, 'providers.json');
    fs.writeFileSync(providersDest, providersContent, { encoding: 'utf-8', mode: 0o600 });

    // Phase 2: Write .tamma/config.json (repo-level project settings)
    const repoTammaDir = path.resolve('.tamma');
    if (!fs.existsSync(repoTammaDir)) {
      fs.mkdirSync(repoTammaDir, { recursive: true });
    }
    const repoConfigContent = generateRepoConfigFile({
      ...answers,
      providerName: 'anthropic',
    });
    const repoConfigDest = path.join(repoTammaDir, 'config.json');
    fs.writeFileSync(repoConfigDest, repoConfigContent, 'utf-8');
    setConfigPath(repoConfigDest);

    // Phase 3: Write .env with credentials (backward compat)
    const envDest = path.resolve('.env');
    let envContent: string;
    if (fs.existsSync(envDest)) {
      const existing = fs.readFileSync(envDest, 'utf-8');
      envContent = mergeIntoEnvFile(existing, { token: answers.token, anthropicKey: answers.anthropicKey });
    } else {
      envContent = generateEnvFile({ token: answers.token, anthropicKey: answers.anthropicKey });
    }
    fs.writeFileSync(envDest, envContent, { encoding: 'utf-8', mode: 0o600 });
    setEnvPath(envDest);

    // Load .env immediately so post-config checks can use credentials
    dotenv.config({ path: envDest, override: false });

    setWizardAnswers(answers);

    // Start post-config checks
    setPhase('postconfig');
    const checks: PostConfigCheck[] = [
      { name: 'Test GitHub API connection', status: 'pending' },
      { name: 'Test Claude CLI', status: 'pending' },
      { name: 'Check/create tamma label', status: 'pending' },
      { name: 'Update .gitignore', status: 'pending' },
    ];
    setPostChecks([...checks]);

    void runPostConfigChecks(answers, checks);
  }, []);

  const runPostConfigChecks = async (answers: Answers, checks: PostConfigCheck[]): Promise<void> => {
    // Check 1: Test GitHub API (via gh if available)
    checks[0]!.status = 'running';
    setPostChecks([...checks]);
    try {
      const target = `${answers.owner}/${answers.repo}`;
      execFileSync('gh', ['api', `repos/${target}`, '--jq', '.full_name'], {
        encoding: 'utf-8',
        timeout: 10000,
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      checks[0]!.status = 'passed';
      checks[0]!.message = `${target} accessible`;
    } catch {
      checks[0]!.status = 'skipped';
      checks[0]!.message = 'gh not available or repo not accessible';
    }
    setPostChecks([...checks]);

    // Check 2: Test Claude CLI
    checks[1]!.status = 'running';
    setPostChecks([...checks]);
    try {
      const output = execSync('claude --version', {
        encoding: 'utf-8',
        timeout: 5000,
        stdio: ['pipe', 'pipe', 'pipe'],
      }).trim();
      checks[1]!.status = 'passed';
      checks[1]!.message = output;
    } catch {
      checks[1]!.status = 'failed';
      checks[1]!.message = 'Claude CLI not found';
    }
    setPostChecks([...checks]);

    // Check 3: Create tamma label
    checks[2]!.status = 'running';
    setPostChecks([...checks]);
    try {
      execFileSync('gh', [
        'label', 'create', 'tamma',
        '--description', 'Tamma autonomous agent',
        '--color', '7B61FF',
        '--repo', `${answers.owner}/${answers.repo}`,
        '--force',
      ], {
        encoding: 'utf-8',
        timeout: 10000,
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      checks[2]!.status = 'passed';
      checks[2]!.message = 'tamma label created/updated';
    } catch {
      checks[2]!.status = 'skipped';
      checks[2]!.message = 'gh not available or insufficient permissions';
    }
    setPostChecks([...checks]);

    // Check 4: Update .gitignore (ensure .tamma/ and .env are listed)
    checks[3]!.status = 'running';
    setPostChecks([...checks]);
    try {
      const gitignorePath = path.resolve('.gitignore');
      let content = '';
      if (fs.existsSync(gitignorePath)) {
        content = fs.readFileSync(gitignorePath, 'utf-8');
      }
      const additions: string[] = [];
      if (!/^\.tamma\/\s*$/m.test(content)) additions.push('.tamma/');
      if (!/^\.env\s*$/m.test(content)) additions.push('.env');
      if (additions.length > 0) {
        const newContent = content.trimEnd() + '\n\n# Tamma\n' + additions.join('\n') + '\n';
        fs.writeFileSync(gitignorePath, newContent, 'utf-8');
        checks[3]!.status = 'passed';
        checks[3]!.message = `Added ${additions.join(', ')} to .gitignore`;
      } else {
        checks[3]!.status = 'passed';
        checks[3]!.message = '.tamma/ and .env already in .gitignore';
      }
    } catch {
      checks[3]!.status = 'failed';
      checks[3]!.message = 'Could not update .gitignore';
    }
    setPostChecks([...checks]);

    setPhase('done');
    setTimeout(() => { exit(); }, 100);
  };

  if (preflightResults === null) {
    return (
      <Box paddingX={1}>
        <Text><Spinner type="dots" /> Running pre-flight checks...</Text>
      </Box>
    );
  }

  return (
    <Box flexDirection="column">
      {phase === 'preflight' && (
        <PreflightDisplay results={preflightResults} onContinue={handlePreflightContinue} />
      )}

      {phase === 'wizard' && (
        <WizardForm preflightResults={preflightResults} onComplete={handleWizardComplete} />
      )}

      {phase === 'postconfig' && (
        <PostConfigDisplay checks={postChecks} configPath={configPath} />
      )}

      {phase === 'done' && (
        <>
          <PostConfigDisplay checks={postChecks} configPath={configPath} />
          <DoneDisplay
            configPath={configPath}
            envPath={envPath}
            hasSkippedCredentials={!wizardAnswers?.token || !wizardAnswers?.anthropicKey}
          />
        </>
      )}
    </Box>
  );
}

export async function initCommand(): Promise<void> {
  // Check for new-format config
  const newDest = path.resolve('.tamma', 'config.json');
  if (fs.existsSync(newDest)) {
    console.log(`Config file already exists at ${newDest}`);
    console.log('Delete it first if you want to regenerate.');
    return;
  }

  // Also check legacy config
  const legacyDest = path.resolve('tamma.config.json');
  if (fs.existsSync(legacyDest)) {
    console.log(`Legacy config file found at ${legacyDest}`);
    console.log('Consider migrating to .tamma/config.json (new layered format).');
    console.log('Delete the legacy file first if you want to regenerate.');
    return;
  }

  const { waitUntilExit } = render(<InitApp />);
  await waitUntilExit();
}
