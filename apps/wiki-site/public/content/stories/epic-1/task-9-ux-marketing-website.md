---
title: "Task 9: Marketing Website UI Implementation"
---

## Objective

Create a modern, responsive marketing website with compelling visuals, interactive elements, and conversion optimization to drive early adoption and community building.

## Acceptance Criteria

- [ ] Modern, responsive design with mobile-first approach
- [ ] Hero section with animated value proposition
- [ ] Interactive feature showcase with hover effects
- [ ] Email signup form with real-time validation
- [ ] Animated roadmap timeline with milestone details
- [ ] Social proof section with GitHub stats
- [ ] Dark/light theme toggle with smooth transitions
- [ ] Performance optimization (Lighthouse score 95+)
- [ ] SEO optimization with structured data
- [ ] Accessibility compliance (WCAG 2.1 AA)

## Technical Implementation

### Core UI Components

```typescript
// Website interfaces
export interface HeroSection {
  title: string;
  tagline: string;
  description: string;
  primaryCTA: CallToAction;
  secondaryCTA?: CallToAction;
  background: 'gradient' | 'animated' | 'video';
}

export interface Feature {
  id: string;
  title: string;
  description: string;
  icon: string;
  benefits: string[];
  comingSoon?: boolean;
}

export interface CallToAction {
  text: string;
  href: string;
  variant: 'primary' | 'secondary' | 'outline';
  external?: boolean;
}

export interface RoadmapMilestone {
  id: string;
  title: string;
  description: string;
  date: string;
  status: 'completed' | 'in-progress' | 'upcoming';
  features: string[];
}

export interface Testimonial {
  id: string;
  name: string;
  role: string;
  company: string;
  content: string;
  avatar?: string;
  rating: number;
}
```

### Hero Section Component

```typescript
export const HeroSection: React.FC<HeroProps> = ({
  title,
  tagline,
  description,
  primaryCTA,
  secondaryCTA,
  background = 'gradient'
}) => {
  const [isVisible, setIsVisible] = useState(false);
  const [mousePosition, setMousePosition] = useState({ x: 0, y: 0 });

  useEffect(() => {
    setIsVisible(true);

    const handleMouseMove = (e: MouseEvent) => {
      setMousePosition({ x: e.clientX, y: e.clientY });
    };

    window.addEventListener('mousemove', handleMouseMove);
    return () => window.removeEventListener('mousemove', handleMouseMove);
  }, []);

  const backgroundStyle = useMemo(() => {
    switch (background) {
      case 'animated':
        return {
          background: `radial-gradient(600px circle at ${mousePosition.x}px ${mousePosition.y}px, rgba(59, 130, 246, 0.1), transparent 40%)`,
        transition: 'background 0.3s ease'
        };
      case 'gradient':
      default:
        return {
          background: 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)'
        };
    }
  }, [background, mousePosition]);

  return (
    <section className="hero" style={backgroundStyle}>
      <div className={`hero-content ${isVisible ? 'visible' : ''}`}>
        <div className="hero-text">
          <h1 className="hero-title">
            <AnimatedText text={title} delay={0} />
          </h1>

          <p className="hero-tagline">
            <AnimatedText text={tagline} delay={200} />
          </p>

          <p className="hero-description">
            <AnimatedText text={description} delay={400} />
          </p>
        </div>

        <div className="hero-actions">
          <CTAButton
            {...primaryCTA}
            className="hero-cta primary"
            delay={600}
          />

          {secondaryCTA && (
            <CTAButton
              {...secondaryCTA}
              className="hero-cta secondary"
              delay={800}
            />
          )}
        </div>

        <div className="hero-stats">
          <StatItem value="70%" label="Autonomous Completion" delay={1000} />
          <StatItem value="8" label="AI Providers" delay={1200} />
          <StatItem value="7" label="Git Platforms" delay={1400} />
        </div>
      </div>

      <div className="hero-visual">
        <CodeAnimation />
        <FloatingElements />
      </div>
    </section>
  );
};

export const AnimatedText: React.FC<AnimatedTextProps> = ({ text, delay = 0 }) => {
  const [displayText, setDisplayText] = useState('');
  const [isTyping, setIsTyping] = useState(false);

  useEffect(() => {
    const timer = setTimeout(() => {
      setIsTyping(true);
      let index = 0;

      const typeInterval = setInterval(() => {
        if (index <= text.length) {
          setDisplayText(text.slice(0, index));
          index++;
        } else {
          setIsTyping(false);
          clearInterval(typeInterval);
        }
      }, 50);

      return () => clearInterval(typeInterval);
    }, delay);

    return () => clearTimeout(timer);
  }, [text, delay]);

  return (
    <span className={`animated-text ${isTyping ? 'typing' : ''}`}>
      {displayText}
      {isTyping && <span className="cursor">|</span>}
    </span>
  );
};

export const StatItem: React.FC<StatItemProps> = ({ value, label, delay = 0 }) => {
  const [isVisible, setIsVisible] = useState(false);
  const [count, setCount] = useState(0);

  useEffect(() => {
    const timer = setTimeout(() => {
      setIsVisible(true);

      if (typeof value === 'number') {
        let current = 0;
        const increment = value / 20;
        const countInterval = setInterval(() => {
          current += increment;
          if (current >= value) {
            setCount(value);
            clearInterval(countInterval);
          } else {
            setCount(Math.floor(current));
          }
        }, 50);

        return () => clearInterval(countInterval);
      } else {
        setCount(value);
      }
    }, delay);

    return () => clearTimeout(timer);
  }, [value, delay]);

  return (
    <div className={`stat-item ${isVisible ? 'visible' : ''}`}>
      <div className="stat-value">{count}{typeof value === 'number' && '%'}</div>
      <div className="stat-label">{label}</div>
    </div>
  );
};
```

### Feature Showcase Component

```typescript
export const FeatureShowcase: React.FC = () => {
  const [selectedFeature, setSelectedFeature] = useState<string | null>(null);
  const [isInView, setIsInView] = useState(false);

  const features: Feature[] = [
    {
      id: 'autonomous',
      title: 'Autonomous Development',
      description: 'From GitHub issue to merged PR without human intervention',
      icon: 'robot',
      benefits: [
        '70%+ completion rate',
        'Self-maintaining codebase',
        'Quality gate enforcement'
      ]
    },
    {
      id: 'multi-provider',
      title: 'Multi-Provider Flexibility',
      description: 'Support for 8 AI providers and 7 Git platforms',
      icon: 'layers',
      benefits: [
        'No vendor lock-in',
        'Automatic failover',
        'Cost optimization'
      ]
    },
    {
      id: 'quality',
      title: 'Production-Ready Quality',
      description: 'Comprehensive testing and never gets stuck',
      icon: 'shield',
      benefits: [
        'Mandatory escalation',
        'Comprehensive testing',
        'Transparent operations'
      ]
    },
    {
      id: 'self-maintenance',
      title: 'Self-Maintenance',
      description: 'Tamma maintains its own codebase',
      icon: 'settings',
      benefits: [
        'Proves production readiness',
        'Reduces maintenance burden',
        'Continuous improvement'
      ]
    }
  ];

  useEffect(() => {
    const observer = new IntersectionObserver(
      ([entry]) => setIsInView(entry.isIntersecting),
      { threshold: 0.1 }
    );

    const element = document.getElementById('feature-showcase');
    if (element) observer.observe(element);

    return () => observer.disconnect();
  }, []);

  return (
    <section id="feature-showcase" className={`feature-showcase ${isInView ? 'in-view' : ''}`}>
      <div className="container">
        <SectionHeader
          title="Why Tamma?"
          subtitle="The autonomous development platform that maintains itself"
        />

        <div className="features-grid">
          {features.map((feature, index) => (
            <FeatureCard
              key={feature.id}
              feature={feature}
              index={index}
              isSelected={selectedFeature === feature.id}
              onSelect={() => setSelectedFeature(feature.id)}
            />
          ))}
        </div>

        <FeatureDetail
          feature={features.find(f => f.id === selectedFeature)}
          onClose={() => setSelectedFeature(null)}
        />
      </div>
    </section>
  );
};

export const FeatureCard: React.FC<FeatureCardProps> = ({
  feature,
  index,
  isSelected,
  onSelect
}) => {
  const [isHovered, setIsHovered] = useState(false);

  return (
    <motion.div
      className={`feature-card ${isSelected ? 'selected' : ''}`}
      initial={{ opacity: 0, y: 50 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ delay: index * 0.1 }}
      whileHover={{ scale: 1.05 }}
      onHoverStart={() => setIsHovered(true)}
      onHoverEnd={() => setIsHovered(false)}
      onClick={onSelect}
    >
      <div className="feature-icon">
        <Icon name={feature.icon} size={48} />
        <div className="icon-glow" />
      </div>

      <h3 className="feature-title">{feature.title}</h3>
      <p className="feature-description">{feature.description}</p>

      <div className="feature-benefits">
        {feature.benefits.map((benefit, idx) => (
          <div key={idx} className="benefit-item">
            <Icon name="check" size={16} />
            <span>{benefit}</span>
          </div>
        ))}
      </div>

      <div className="feature-action">
        <Button variant="outline" size="sm">
          Learn More
        </Button>
      </div>

      {isHovered && (
        <motion.div
          className="feature-glow"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
        />
      )}
    </motion.div>
  );
};
```

### Interactive Roadmap Component

```typescript
export const RoadmapSection: React.FC = () => {
  const [selectedMilestone, setSelectedMilestone] = useState<string | null>(null);
  const [activeEpic, setActiveEpic] = useState<string>('epic-1');

  const roadmap: RoadmapData = {
    'epic-1': {
      title: 'Foundation & Core Infrastructure',
      description: 'Multi-provider AI abstraction, Git platform integration',
      milestones: [
        {
          id: 'epic-1-complete',
          title: 'Epic 1 Complete',
          description: 'All core abstractions and providers implemented',
          date: '2025-11-30',
          status: 'in-progress',
          features: ['AI Provider Interface', 'Git Platform Integration', 'CLI Scaffolding']
        }
      ]
    },
    'epic-2': {
      title: 'Autonomous Development Workflow',
      description: 'Issue selection to PR merge automation',
      milestones: [
        {
          id: 'epic-2-start',
          title: 'Epic 2 Start',
          description: 'Begin autonomous development loop implementation',
          date: '2025-12-01',
          status: 'upcoming',
          features: ['Issue Selection', 'Plan Generation', 'Test-First Development']
        }
      ]
    }
  };

  return (
    <section className="roadmap-section">
      <div className="container">
        <SectionHeader
          title="Development Roadmap"
          subtitle="From foundation to production-ready autonomous platform"
        />

        <div className="epic-selector">
          {Object.keys(roadmap).map(epicId => (
            <button
              key={epicId}
              className={`epic-tab ${activeEpic === epicId ? 'active' : ''}`}
              onClick={() => setActiveEpic(epicId)}
            >
              {roadmap[epicId as keyof RoadmapData].title}
            </button>
          ))}
        </div>

        <div className="roadmap-timeline">
          {roadmap[activeEpic as keyof RoadmapData].milestones.map((milestone, index) => (
            <RoadmapMilestone
              key={milestone.id}
              milestone={milestone}
              index={index}
              isSelected={selectedMilestone === milestone.id}
              onSelect={() => setSelectedMilestone(milestone.id)}
            />
          ))}
        </div>

        <MilestoneDetail
          milestone={roadmap[activeEpic as keyof RoadmapData].milestones.find(m => m.id === selectedMilestone)}
          onClose={() => setSelectedMilestone(null)}
        />
      </div>
    </section>
  );
};

export const RoadmapMilestone: React.FC<RoadmapMilestoneProps> = ({
  milestone,
  index,
  isSelected,
  onSelect
}) => {
  const [isInView, setIsInView] = useState(false);

  useEffect(() => {
    const observer = new IntersectionObserver(
      ([entry]) => setIsInView(entry.isIntersecting),
      { threshold: 0.5 }
    );

    const element = document.getElementById(`milestone-${milestone.id}`);
    if (element) observer.observe(element);

    return () => observer.disconnect();
  }, [milestone.id]);

  const statusColor = {
    completed: '#10b981',
    'in-progress': '#3b82f6',
    upcoming: '#6b7280'
  }[milestone.status];

  return (
    <motion.div
      id={`milestone-${milestone.id}`}
      className={`roadmap-milestone ${isSelected ? 'selected' : ''}`}
      initial={{ opacity: 0, x: -50 }}
      animate={{ opacity: isInView ? 1 : 0, x: isInView ? 0 : -50 }}
      transition={{ delay: index * 0.2 }}
      onClick={onSelect}
    >
      <div className="milestone-marker">
        <div
          className="milestone-dot"
          style={{ backgroundColor: statusColor }}
        />
        <div className="milestone-connector" />
      </div>

      <div className="milestone-content">
        <div className="milestone-date">
          {formatDate(milestone.date)}
        </div>

        <h3 className="milestone-title">{milestone.title}</h3>

        <p className="milestone-description">{milestone.description}</p>

        <div className="milestone-status">
          <StatusBadge status={milestone.status} />
        </div>

        <div className="milestone-features">
          {milestone.features.map((feature, idx) => (
            <span key={idx} className="feature-tag">
              {feature}
            </span>
          ))}
        </div>
      </div>
    </motion.div>
  );
};
```

### Email Signup Component

```typescript
export const EmailSignup: React.FC = () => {
  const [email, setEmail] = useState('');
  const [status, setStatus] = useState<'idle' | 'loading' | 'success' | 'error'>('idle');
  const [error, setError] = useState('');
  const [isFocused, setIsFocused] = useState(false);

  const validateEmail = (email: string): boolean => {
    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;
    return emailRegex.test(email);
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();

    if (!validateEmail(email)) {
      setError('Please enter a valid email address');
      return;
    }

    setStatus('loading');
    setError('');

    try {
      const response = await fetch('/api/signup', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email })
      });

      if (response.ok) {
        setStatus('success');
        setEmail('');
      } else {
        throw new Error('Signup failed');
      }
    } catch (err) {
      setStatus('error');
      setError('Something went wrong. Please try again.');
    }
  };

  return (
    <section className="email-signup">
      <div className="container">
        <SectionHeader
          title="Get Early Access"
          subtitle="Be the first to know when Tamma launches"
        />

        <form onSubmit={handleSubmit} className="signup-form">
          <div className={`input-group ${isFocused ? 'focused' : ''} ${error ? 'error' : ''}`}>
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              onFocus={() => setIsFocused(true)}
              onBlur={() => setIsFocused(false)}
              placeholder="Enter your email"
              className="email-input"
              disabled={status === 'loading'}
            />

            <Button
              type="submit"
              disabled={status === 'loading' || !validateEmail(email)}
              loading={status === 'loading'}
              className="submit-button"
            >
              {status === 'loading' ? 'Signing up...' : 'Notify Me'}
            </Button>
          </div>

          {error && (
            <motion.div
              className="error-message"
              initial={{ opacity: 0, y: -10 }}
              animate={{ opacity: 1, y: 0 }}
            >
              <Icon name="alert-circle" size={16} />
              {error}
            </motion.div>
          )}

          {status === 'success' && (
            <motion.div
              className="success-message"
              initial={{ opacity: 0, scale: 0.9 }}
              animate={{ opacity: 1, scale: 1 }}
            >
              <Icon name="check-circle" size={16} />
              Thanks for signing up! We\'ll keep you updated.
            </motion.div>
          )}
        </form>

        <div className="signup-benefits">
          <BenefitItem icon="bell" text="Launch notifications" />
          <BenefitItem icon="gift" text="Early access features" />
          <BenefitItem icon="users" text="Community updates" />
        </div>

        <p className="privacy-note">
          <Icon name="lock" size={14} />
          We respect your privacy. Unsubscribe at any time.
        </p>
      </div>
    </section>
  );
};

export const BenefitItem: React.FC<BenefitItemProps> = ({ icon, text }) => (
  <div className="benefit-item">
    <Icon name={icon} size={20} />
    <span>{text}</span>
  </div>
);
```

### Theme System

```typescript
export const ThemeProvider: React.FC<ThemeProviderProps> = ({ children }) => {
  const [theme, setTheme] = useState<'light' | 'dark'>('light');
  const [isTransitioning, setIsTransitioning] = useState(false);

  useEffect(() => {
    const savedTheme = localStorage.getItem('tamma-theme') as 'light' | 'dark' | null;
    const systemTheme = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';

    setTheme(savedTheme || systemTheme);
  }, []);

  const toggleTheme = () => {
    setIsTransitioning(true);
    const newTheme = theme === 'light' ? 'dark' : 'light';

    setTheme(newTheme);
    localStorage.setItem('tamma-theme', newTheme);

    setTimeout(() => setIsTransitioning(false), 300);
  };

  useEffect(() => {
    document.documentElement.setAttribute('data-theme', theme);
    document.documentElement.classList.toggle('dark', theme === 'dark');
  }, [theme]);

  return (
    <ThemeContext.Provider value={{ theme, toggleTheme, isTransitioning }}>
      <div className={`theme-provider ${theme} ${isTransitioning ? 'transitioning' : ''}`}>
        {children}

        <ThemeToggle theme={theme} onToggle={toggleTheme} />
      </div>
    </ThemeContext.Provider>
  );
};

export const ThemeToggle: React.FC<ThemeToggleProps> = ({ theme, onToggle }) => {
  return (
    <button
      className="theme-toggle"
      onClick={onToggle}
      aria-label={`Switch to ${theme === 'light' ? 'dark' : 'light'} mode`}
    >
      <motion.div
        className="toggle-track"
        animate={{ backgroundColor: theme === 'dark' ? '#1f2937' : '#f3f4f6' }}
      >
        <motion.div
          className="toggle-thumb"
          animate={{ x: theme === 'dark' ? 24 : 0 }}
          transition={{ type: 'spring', stiffness: 500, damping: 30 }}
        >
          {theme === 'light' ? (
            <Icon name="sun" size={16} />
          ) : (
            <Icon name="moon" size={16} />
          )}
        </motion.div>
      </motion.div>
    </button>
  );
};
```

## Design System

### CSS Variables and Theming

```css
:root {
  /* Color Palette */
  --color-primary-50: #eff6ff;
  --color-primary-100: #dbeafe;
  --color-primary-200: #bfdbfe;
  --color-primary-500: #3b82f6;
  --color-primary-600: #2563eb;
  --color-primary-700: #1d4ed8;
  --color-primary-900: #1e3a8a;

  --color-secondary-50: #f0fdf4;
  --color-secondary-100: #dcfce7;
  --color-secondary-500: #10b981;
  --color-secondary-600: #059669;
  --color-secondary-700: #047857;

  --color-accent-50: #fef3c7;
  --color-accent-100: #fde68a;
  --color-accent-500: #f59e0b;
  --color-accent-600: #d97706;

  /* Neutral Colors */
  --color-gray-50: #f9fafb;
  --color-gray-100: #f3f4f6;
  --color-gray-200: #e5e7eb;
  --color-gray-300: #d1d5db;
  --color-gray-400: #9ca3af;
  --color-gray-500: #6b7280;
  --color-gray-600: #4b5563;
  --color-gray-700: #374151;
  --color-gray-800: #1f2937;
  --color-gray-900: #111827;

  /* Typography */
  --font-sans: 'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
  --font-mono: 'JetBrains Mono', 'Fira Code', Consolas, monospace;

  /* Spacing */
  --space-xs: 0.25rem;
  --space-sm: 0.5rem;
  --space-md: 1rem;
  --space-lg: 1.5rem;
  --space-xl: 2rem;
  --space-2xl: 3rem;
  --space-3xl: 4rem;

  /* Border Radius */
  --radius-sm: 0.25rem;
  --radius-md: 0.5rem;
  --radius-lg: 0.75rem;
  --radius-xl: 1rem;

  /* Shadows */
  --shadow-sm: 0 1px 2px 0 rgba(0, 0, 0, 0.05);
  --shadow-md: 0 4px 6px -1px rgba(0, 0, 0, 0.1);
  --shadow-lg: 0 10px 15px -3px rgba(0, 0, 0, 0.1);
  --shadow-xl: 0 20px 25px -5px rgba(0, 0, 0, 0.1);

  /* Transitions */
  --transition-fast: 150ms ease;
  --transition-normal: 250ms ease;
  --transition-slow: 350ms ease;
}

[data-theme='dark'] {
  --color-bg-primary: var(--color-gray-900);
  --color-bg-secondary: var(--color-gray-800);
  --color-bg-tertiary: var(--color-gray-700);
  --color-text-primary: var(--color-gray-50);
  --color-text-secondary: var(--color-gray-300);
  --color-text-tertiary: var(--color-gray-400);
  --color-border: var(--color-gray-600);
}

[data-theme='light'] {
  --color-bg-primary: #ffffff;
  --color-bg-secondary: var(--color-gray-50);
  --color-bg-tertiary: var(--color-gray-100);
  --color-text-primary: var(--color-gray-900);
  --color-text-secondary: var(--color-gray-600);
  --color-text-tertiary: var(--color-gray-500);
  --color-border: var(--color-gray-200);
}
```

### Component Library

```typescript
export const Button: React.FC<ButtonProps> = ({
  variant = 'primary',
  size = 'md',
  loading = false,
  children,
  className = '',
  ...props
}) => {
  const baseClasses = 'btn';
  const variantClasses = `btn-${variant}`;
  const sizeClasses = `btn-${size}`;
  const loadingClasses = loading ? 'loading' : '';

  return (
    <button
      className={`${baseClasses} ${variantClasses} ${sizeClasses} ${loadingClasses} ${className}`}
      disabled={loading || props.disabled}
      {...props}
    >
      {loading && <Spinner size="sm" />}
      <span className="btn-text">{children}</span>
    </button>
  );
};

export const Card: React.FC<CardProps> = ({
  children,
  className = '',
  hover = false,
  ...props
}) => {
  return (
    <div className={`card ${hover ? 'card-hover' : ''} ${className}`} {...props}>
      {children}
    </div>
  );
};

export const Icon: React.FC<IconProps> = ({ name, size = 16, className = '' }) => {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={`icon icon-${name} ${className}`}
    >
      {/* Icon paths would be defined here */}
    </svg>
  );
};
```

## Performance Optimization

### Image Optimization

```typescript
export const OptimizedImage: React.FC<OptimizedImageProps> = ({
  src,
  alt,
  width,
  height,
  priority = false,
  className = ''
}) => {
  const [isLoaded, setIsLoaded] = useState(false);
  const [isInView, setIsInView] = useState(priority);

  useEffect(() => {
    if (priority) return;

    const observer = new IntersectionObserver(
      ([entry]) => setIsInView(entry.isIntersecting),
      { threshold: 0.1 }
    );

    const element = document.getElementById(`img-${src}`);
    if (element) observer.observe(element);

    return () => observer.disconnect();
  }, [src, priority]);

  const optimizedSrc = useMemo(() => {
    if (!isInView && !priority) return '';

    // Generate WebP version with quality optimization
    return `${src}?format=webp&quality=80&w=${width}&h=${height}`;
  }, [src, width, height, isInView, priority]);

  return (
    <div className={`optimized-image ${className} ${isLoaded ? 'loaded' : 'loading'}`}>
      <img
        id={`img-${src}`}
        src={optimizedSrc}
        alt={alt}
        width={width}
        height={height}
        loading={priority ? 'eager' : 'lazy'}
        onLoad={() => setIsLoaded(true)}
        style={{ opacity: isLoaded ? 1 : 0 }}
      />

      {!isLoaded && <ImageSkeleton width={width} height={height} />}
    </div>
  );
};
```

## Testing Strategy

### Component Tests

```typescript
describe('HeroSection', () => {
  it('should render hero content with animations', () => {
    render(<HeroSection {...mockHeroProps} />);

    expect(screen.getByText('Tamma')).toBeInTheDocument();
    expect(screen.getByText('Autonomous Development Platform')).toBeInTheDocument();
  });

  it('should handle mouse movement for animated background', () => {
    render(<HeroSection {...mockHeroProps} background="animated" />);

    fireEvent.mouseMove(window, { clientX: 100, clientY: 200 });

    const hero = screen.getByRole('banner');
    expect(hero).toHaveStyle({
      background: expect.stringContaining('100px 200px')
    });
  });
});

describe('EmailSignup', () => {
  it('should validate email format', async () => {
    render(<EmailSignup />);

    const input = screen.getByPlaceholderText('Enter your email');
    const button = screen.getByRole('button', { name: 'Notify Me' });

    fireEvent.change(input, { target: { value: 'invalid-email' } });
    fireEvent.click(button);

    expect(screen.getByText('Please enter a valid email address')).toBeInTheDocument();
  });

  it('should submit valid email successfully', async () => {
    global.fetch = jest.fn().mockResolvedValue({ ok: true });

    render(<EmailSignup />);

    const input = screen.getByPlaceholderText('Enter your email');
    const button = screen.getByRole('button', { name: 'Notify Me' });

    fireEvent.change(input, { target: { value: 'test@example.com' } });
    fireEvent.click(button);

    await waitFor(() => {
      expect(screen.getByText('Thanks for signing up!')).toBeInTheDocument();
    });
  });
});
```

### Performance Tests

```typescript
describe('Performance', () => {
  it('should load hero section within 500ms', async () => {
    const startTime = performance.now();

    render(<HeroSection {...mockHeroProps} />);

    await waitFor(() => {
      expect(screen.getByText('Tamma')).toBeInTheDocument();
    });

    const loadTime = performance.now() - startTime;
    expect(loadTime).toBeLessThan(500);
  });

  it('should have Lighthouse score above 95', async () => {
    // This would be run in actual browser testing environment
    const lighthouseResult = await runLighthouse('http://localhost:3000');

    expect(lighthouseResult.lhr.categories.performance.score).toBeGreaterThan(0.95);
    expect(lighthouseResult.lhr.categories.accessibility.score).toBeGreaterThan(0.95);
    expect(lighthouseResult.lhr.categories['best-practices'].score).toBeGreaterThan(0.95);
    expect(lighthouseResult.lhr.categories.seo.score).toBeGreaterThan(0.95);
  });
});
```

## Implementation Checklist

- [ ] Create hero section with animations
- [ ] Build feature showcase with interactive cards
- [ ] Implement animated roadmap timeline
- [ ] Create email signup with validation
- [ ] Add theme system with toggle
- [ ] Build responsive design system
- [ ] Implement performance optimizations
- [ ] Add SEO meta tags and structured data
- [ ] Create accessibility features
- [ ] Build component library
- [ ] Add micro-interactions and animations
- [ ] Implement error boundaries
- [ ] Create comprehensive test suite
- [ ] Add analytics integration
- [ ] Optimize for Core Web Vitals
- [ ] Create deployment configuration
