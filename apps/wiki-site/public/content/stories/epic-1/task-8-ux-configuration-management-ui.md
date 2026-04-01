---
title: "Task 8: Configuration Management UI Implementation"
---

## Objective

Design and implement a user-friendly web interface for managing AI provider and Git platform configurations, providing visual configuration tools, validation feedback, and real-time status monitoring.

## Acceptance Criteria

- [ ] Web-based configuration dashboard with provider management interface
- [ ] Visual provider setup wizard with step-by-step guidance
- [ ] Real-time configuration validation with inline error messages
- [ ] Secure credential management with masked input fields
- [ ] Provider status monitoring with health checks
- [ ] Configuration import/export functionality
- [ ] Responsive design for desktop and mobile devices
- [ ] Dark/light theme toggle with system preference detection
- [ ] Configuration history with rollback capabilities
- [ ] Multi-environment support (dev/staging/prod)

## Technical Implementation

### Core UI Components

```typescript
// Configuration management interfaces
export interface ProviderConfigUI {
  id: string;
  name: string;
  type: 'ai' | 'git';
  status: 'connected' | 'disconnected' | 'error' | 'testing';
  config: ProviderConfig;
  lastTested?: Date;
  errors?: ConfigError[];
}

export interface ConfigError {
  field: string;
  message: string;
  severity: 'error' | 'warning' | 'info';
  code?: string;
}

export interface ConfigWizardStep {
  id: string;
  title: string;
  description: string;
  component: React.ComponentType<any>;
  validation: (data: any) => ConfigError[];
  skip?: boolean;
}

export interface ConfigEnvironment {
  id: string;
  name: string;
  isDefault: boolean;
  providers: ProviderConfigUI[];
  lastModified: Date;
}
```

### Configuration Dashboard Component

```typescript
export const ConfigurationDashboard: React.FC = () => {
  const [providers, setProviders] = useState<ProviderConfigUI[]>([]);
  const [selectedEnvironment, setSelectedEnvironment] = useState<string>('default');
  const [isWizardOpen, setIsWizardOpen] = useState(false);
  const [testingProvider, setTestingProvider] = useState<string | null>(null);

  return (
    <div className="config-dashboard">
      <Header
        title="Configuration Management"
        actions={
          <Button onClick={() => setIsWizardOpen(true)}>
            Add Provider
          </Button>
        }
      />

      <EnvironmentSelector
        environments={environments}
        selected={selectedEnvironment}
        onSelect={setSelectedEnvironment}
      />

      <ProviderGrid
        providers={providers}
        onTest={handleTestProvider}
        onEdit={handleEditProvider}
        onDelete={handleDeleteProvider}
        testingProvider={testingProvider}
      />

      <ConfigurationWizard
        isOpen={isWizardOpen}
        onClose={() => setIsWizardOpen(false)}
        onComplete={handleWizardComplete}
      />
    </div>
  );
};
```

### Provider Configuration Wizard

```typescript
export const ConfigurationWizard: React.FC<WizardProps> = ({ isOpen, onClose, onComplete }) => {
  const [currentStep, setCurrentStep] = useState(0);
  const [wizardData, setWizardData] = useState<WizardData>({});
  const [errors, setErrors] = useState<ConfigError[]>([]);

  const steps: ConfigWizardStep[] = [
    {
      id: 'provider-type',
      title: 'Select Provider Type',
      description: 'Choose whether you want to configure an AI provider or Git platform',
      component: ProviderTypeSelector,
      validation: validateProviderType
    },
    {
      id: 'provider-selection',
      title: 'Select Provider',
      description: 'Choose the specific provider you want to configure',
      component: ProviderSelector,
      validation: validateProviderSelection
    },
    {
      id: 'basic-config',
      title: 'Basic Configuration',
      description: 'Enter the basic configuration details',
      component: BasicConfigForm,
      validation: validateBasicConfig
    },
    {
      id: 'credentials',
      title: 'Authentication',
      description: 'Configure authentication credentials',
      component: CredentialForm,
      validation: validateCredentials
    },
    {
      id: 'advanced-settings',
      title: 'Advanced Settings',
      description: 'Configure advanced options and capabilities',
      component: AdvancedSettingsForm,
      validation: validateAdvancedSettings,
      skip: true
    },
    {
      id: 'test-connection',
      title: 'Test Connection',
      description: 'Verify your configuration works correctly',
      component: ConnectionTest,
      validation: validateConnection
    }
  ];

  const handleNext = async () => {
    const step = steps[currentStep];
    const stepErrors = step.validation(wizardData);

    if (stepErrors.length > 0) {
      setErrors(stepErrors);
      return;
    }

    setErrors([]);
    if (currentStep < steps.length - 1) {
      setCurrentStep(currentStep + 1);
    } else {
      await onComplete(wizardData);
      handleClose();
    }
  };

  const handleBack = () => {
    if (currentStep > 0) {
      setCurrentStep(currentStep - 1);
    }
  };

  const handleClose = () => {
    setCurrentStep(0);
    setWizardData({});
    setErrors([]);
    onClose();
  };

  return (
    <Modal isOpen={isOpen} onClose={handleClose} size="large">
      <WizardHeader
        title="Add Provider Configuration"
        currentStep={currentStep}
        totalSteps={steps.length}
        steps={steps}
      />

      <WizardContent>
        {React.createElement(steps[currentStep].component, {
          data: wizardData,
          onChange: setWizardData,
          errors: errors
        })}
      </WizardContent>

      <WizardActions>
        <Button
          variant="outline"
          onClick={handleBack}
          disabled={currentStep === 0}
        >
          Back
        </Button>

        <Button
          onClick={handleNext}
          loading={testingProvider !== null}
        >
          {currentStep === steps.length - 1 ? 'Complete' : 'Next'}
        </Button>
      </WizardActions>
    </Modal>
  );
};
```

### Provider Status Grid

```typescript
export const ProviderGrid: React.FC<ProviderGridProps> = ({
  providers,
  onTest,
  onEdit,
  onDelete,
  testingProvider
}) => {
  return (
    <div className="provider-grid">
      {providers.map((provider) => (
        <ProviderCard
          key={provider.id}
          provider={provider}
          onTest={() => onTest(provider.id)}
          onEdit={() => onEdit(provider.id)}
          onDelete={() => onDelete(provider.id)}
          isTesting={testingProvider === provider.id}
        />
      ))}

      {providers.length === 0 && (
        <EmptyState
          icon="settings"
          title="No Providers Configured"
          description="Add your first AI or Git provider to get started"
          action={
            <Button onClick={() => setIsWizardOpen(true)}>
              Add Provider
            </Button>
          }
        />
      )}
    </div>
  );
};

export const ProviderCard: React.FC<ProviderCardProps> = ({
  provider,
  onTest,
  onEdit,
  onDelete,
  isTesting
}) => {
  const getStatusColor = (status: string) => {
    switch (status) {
      case 'connected': return 'green';
      case 'disconnected': return 'gray';
      case 'error': return 'red';
      case 'testing': return 'blue';
      default: return 'gray';
    }
  };

  return (
    <Card className="provider-card">
      <CardHeader>
        <div className="provider-info">
          <ProviderIcon type={provider.type} name={provider.name} />
          <div>
            <h3>{provider.name}</h3>
            <StatusBadge
              status={provider.status}
              color={getStatusColor(provider.status)}
            />
          </div>
        </div>

        <Dropdown
          trigger={<Button variant="ghost" icon="more" />}
          items={[
            { label: 'Edit', onClick: onEdit },
            { label: 'Test', onClick: onTest },
            { label: 'Delete', onClick: onDelete, variant: 'destructive' }
          ]}
        />
      </CardHeader>

      <CardContent>
        <div className="provider-details">
          <DetailRow label="Type" value={provider.type} />
          <DetailRow label="Status" value={provider.status} />
          {provider.lastTested && (
            <DetailRow
              label="Last Tested"
              value={formatRelativeTime(provider.lastTested)}
            />
          )}
        </div>

        {provider.errors && provider.errors.length > 0 && (
          <ErrorList errors={provider.errors} />
        )}
      </CardContent>

      <CardActions>
        <Button
          onClick={onTest}
          loading={isTesting}
          variant="outline"
          size="sm"
        >
          {isTesting ? 'Testing...' : 'Test Connection'}
        </Button>

        <Button onClick={onEdit} size="sm">
          Configure
        </Button>
      </CardActions>
    </Card>
  );
};
```

### Configuration Forms

```typescript
export const CredentialForm: React.FC<FormProps> = ({ data, onChange, errors }) => {
  const [showPassword, setShowPassword] = useState<Record<string, boolean>>({});

  const handleCredentialChange = (field: string, value: string) => {
    onChange({
      ...data,
      credentials: {
        ...data.credentials,
        [field]: value
      }
    });
  };

  const togglePasswordVisibility = (field: string) => {
    setShowPassword(prev => ({
      ...prev,
      [field]: !prev[field]
    }));
  };

  return (
    <FormSection title="Authentication Credentials">
      {data.providerType === 'ai' && (
        <>
          <FormField
            label="API Key"
            type="password"
            value={data.credentials?.apiKey || ''}
            onChange={(value) => handleCredentialChange('apiKey', value)}
            error={errors.find(e => e.field === 'apiKey')?.message}
            rightElement={
              <Button
                variant="ghost"
                size="sm"
                onClick={() => togglePasswordVisibility('apiKey')}
              >
                <Icon name={showPassword.apiKey ? 'eye-off' : 'eye'} />
              </Button>
            }
          />

          <FormField
            label="API Endpoint"
            type="url"
            value={data.credentials?.endpoint || ''}
            onChange={(value) => handleCredentialChange('endpoint', value)}
            error={errors.find(e => e.field === 'endpoint')?.message}
            placeholder="https://api.example.com"
          />
        </>
      )}

      {data.providerType === 'git' && (
        <>
          <FormField
            label="Personal Access Token"
            type="password"
            value={data.credentials?.token || ''}
            onChange={(value) => handleCredentialChange('token', value)}
            error={errors.find(e => e.field === 'token')?.message}
            rightElement={
              <Button
                variant="ghost"
                size="sm"
                onClick={() => togglePasswordVisibility('token')}
              >
                <Icon name={showPassword.token ? 'eye-off' : 'eye'} />
              </Button>
            }
          />

          <FormField
            label="Repository URL"
            type="url"
            value={data.credentials?.repositoryUrl || ''}
            onChange={(value) => handleCredentialChange('repositoryUrl', value)}
            error={errors.find(e => e.field === 'repositoryUrl')?.message}
            placeholder="https://github.com/user/repo"
          />
        </>
      )}

      <div className="credential-help">
        <Alert variant="info">
          <Icon name="info" />
          <span>
            Credentials are encrypted and stored securely.
            Consider using environment variables for production deployments.
          </span>
        </Alert>
      </div>
    </FormSection>
  );
};
```

### Real-time Status Monitoring

```typescript
export const StatusMonitor: React.FC = () => {
  const [providerStatuses, setProviderStatuses] = useState<Map<string, ProviderStatus>>(new Map());
  const [connectionLogs, setConnectionLogs] = useState<ConnectionLog[]>([]);

  useEffect(() => {
    const ws = new WebSocket('/api/config/status');

    ws.onmessage = (event) => {
      const status = JSON.parse(event.data);
      setProviderStatuses(prev => new Map(prev).set(status.providerId, status));

      if (status.type === 'connection_test') {
        setConnectionLogs(prev => [status, ...prev.slice(0, 99)]);
      }
    };

    return () => ws.close();
  }, []);

  return (
    <div className="status-monitor">
      <SectionHeader title="Real-time Status" />

      <div className="status-grid">
        {Array.from(providerStatuses.entries()).map(([id, status]) => (
          <StatusCard key={id} status={status} />
        ))}
      </div>

      <ActivityLog logs={connectionLogs} />
    </div>
  );
};
```

## Design System

### Color Palette

```css
:root {
  /* Primary Colors */
  --color-primary-50: #eff6ff;
  --color-primary-500: #3b82f6;
  --color-primary-600: #2563eb;
  --color-primary-900: #1e3a8a;

  /* Status Colors */
  --color-success: #10b981;
  --color-warning: #f59e0b;
  --color-error: #ef4444;
  --color-info: #6366f1;

  /* Neutral Colors */
  --color-gray-50: #f9fafb;
  --color-gray-100: #f3f4f6;
  --color-gray-500: #6b7280;
  --color-gray-900: #111827;

  /* Dark Mode */
  --color-bg-primary: #ffffff;
  --color-bg-secondary: #f9fafb;
  --color-text-primary: #111827;
  --color-text-secondary: #6b7280;
}

[data-theme='dark'] {
  --color-bg-primary: #111827;
  --color-bg-secondary: #1f2937;
  --color-text-primary: #f9fafb;
  --color-text-secondary: #d1d5db;
}
```

### Component Library

```typescript
// Base UI Components
export const Button: React.FC<ButtonProps> = ({
  variant = 'primary',
  size = 'md',
  loading = false,
  children,
  ...props
}) => {
  return (
    <button
      className={`btn btn-${variant} btn-${size} ${loading ? 'loading' : ''}`}
      disabled={loading || props.disabled}
      {...props}
    >
      {loading && <Spinner size="sm" />}
      {children}
    </button>
  );
};

export const Card: React.FC<CardProps> = ({ children, className = '', ...props }) => {
  return (
    <div className={`card ${className}`} {...props}>
      {children}
    </div>
  );
};

export const Modal: React.FC<ModalProps> = ({
  isOpen,
  onClose,
  size = 'md',
  children
}) => {
  if (!isOpen) return null;

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div
        className={`modal modal-${size}`}
        onClick={(e) => e.stopPropagation()}
      >
        {children}
      </div>
    </div>
  );
};

export const FormField: React.FC<FormFieldProps> = ({
  label,
  type = 'text',
  value,
  onChange,
  error,
  rightElement,
  ...props
}) => {
  return (
    <div className="form-field">
      {label && <label className="form-label">{label}</label>}
      <div className="form-input-wrapper">
        <input
          type={type}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          className={`form-input ${error ? 'error' : ''}`}
          {...props}
        />
        {rightElement}
      </div>
      {error && <span className="form-error">{error}</span>}
    </div>
  );
};
```

## Testing Strategy

### Unit Tests

```typescript
describe('ConfigurationDashboard', () => {
  it('should display provider grid', () => {
    const mockProviders = [createMockProvider()];
    render(<ConfigurationDashboard providers={mockProviders} />);

    expect(screen.getByText('Configuration Management')).toBeInTheDocument();
    expect(screen.getByTestId('provider-grid')).toBeInTheDocument();
  });

  it('should open wizard when add provider clicked', () => {
    render(<ConfigurationDashboard providers={[]} />);

    fireEvent.click(screen.getByText('Add Provider'));
    expect(screen.getByTestId('configuration-wizard')).toBeInTheDocument();
  });
});

describe('ConfigurationWizard', () => {
  it('should validate provider type selection', async () => {
    const onComplete = jest.fn();
    render(<ConfigurationWizard isOpen onComplete={onComplete} />);

    fireEvent.click(screen.getByText('Next'));
    expect(screen.getByText('Please select a provider type')).toBeInTheDocument();
  });

  it('should complete wizard with valid data', async () => {
    const onComplete = jest.fn();
    render(<ConfigurationWizard isOpen onComplete={onComplete} />);

    // Fill out wizard steps
    await fillWizardSteps();

    fireEvent.click(screen.getByText('Complete'));
    expect(onComplete).toHaveBeenCalledWith(expect.objectContaining({
      providerType: 'ai',
      providerName: 'claude'
    }));
  });
});
```

### Integration Tests

```typescript
describe('Configuration Management Integration', () => {
  it('should save and load provider configuration', async () => {
    const configData = createValidConfig();

    // Save configuration
    const saveResponse = await fetch('/api/config/providers', {
      method: 'POST',
      body: JSON.stringify(configData),
    });
    expect(saveResponse.ok).toBe(true);

    // Load configuration
    const loadResponse = await fetch('/api/config/providers');
    const loadedConfig = await loadResponse.json();

    expect(loadedConfig).toContainEqual(
      expect.objectContaining({
        name: configData.name,
        type: configData.type,
      })
    );
  });

  it('should test provider connection', async () => {
    const testResponse = await fetch('/api/config/test', {
      method: 'POST',
      body: JSON.stringify({
        type: 'ai',
        name: 'claude',
        credentials: { apiKey: 'test-key' },
      }),
    });

    const result = await testResponse.json();
    expect(result.status).toBeOneOf(['success', 'error']);
  });
});
```

## Performance Requirements

### Loading Times

- Initial dashboard load: < 500ms
- Provider test connection: < 2000ms
- Configuration save: < 1000ms
- Wizard step transitions: < 100ms

### Memory Usage

- Dashboard idle: < 50MB
- During provider testing: < 100MB
- Configuration wizard: < 30MB

### Accessibility

- WCAG 2.1 AA compliance
- Keyboard navigation support
- Screen reader compatibility
- High contrast mode support

## Security Considerations

### Credential Handling

- Never log credentials to console
- Encrypt sensitive data at rest
- Use secure HTTP headers
- Implement CSRF protection

### Input Validation

- Sanitize all user inputs
- Validate URLs and API keys
- Prevent XSS attacks
- Rate limit configuration requests

## Implementation Checklist

- [ ] Create configuration dashboard component
- [ ] Implement provider configuration wizard
- [ ] Build provider status monitoring
- [ ] Add real-time connection testing
- [ ] Create responsive design system
- [ ] Implement dark/light theme support
- [ ] Add configuration import/export
- [ ] Build comprehensive error handling
- [ ] Create unit and integration tests
- [ ] Add accessibility features
- [ ] Implement security measures
- [ ] Optimize for performance
- [ ] Add documentation and help content
