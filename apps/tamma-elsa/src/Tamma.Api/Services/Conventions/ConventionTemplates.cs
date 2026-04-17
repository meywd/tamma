namespace Tamma.Api.Services.Conventions;

/// <summary>
/// Preset coding convention starter templates that users can choose from
/// when setting up their repo. Each template contains the key coding rules
/// an LLM needs to write correct code in a given language/framework.
///
/// The <see cref="ConventionTemplate.Conventions"/> body is injected into
/// LLM prompts via the <c>{{conventions}}</c> template variable, so the
/// wording here is load-bearing and must be preserved byte-for-byte when
/// ported.
/// </summary>
public static class ConventionTemplates
{
    // ────────────────────────────────────────────────────────────────────
    // Template definitions — bodies ported verbatim from the deleted
    // packages/api/src/services/convention-templates.ts (commit 9e9a57c~1).
    // Do not edit the body text without a strong reason; LLM behaviour
    // depends on exact wording.
    //
    // NOTE: The private template fields MUST be declared above the public
    // `All` / `ByKey` aggregates below — C# runs field initializers in
    // declaration order, so if the aggregates came first they would see
    // default-initialized (null) template references.
    // ────────────────────────────────────────────────────────────────────

    private static readonly ConventionTemplate TypescriptReact = new(
        Key: "typescript-react",
        Name: "TypeScript + React/Next.js",
        Description: "TypeScript + React 19/Next.js 15, RSC, hooks, Tailwind CSS, Vitest/RTL",
        Conventions: @"# TypeScript + React/Next.js Conventions

## Framework
- Next.js 15 with App Router; React 19 with Server Components by default
- Add ""use client"" only when using hooks, event handlers, or browser APIs
- Use Server Actions for mutations (form submissions, data writes)
- Prefer React Server Components for data fetching — no useEffect for initial data

## Components
- One component per file; file name matches component name in PascalCase (UserProfile.tsx)
- Functional components only — no class components
- Props interface named {ComponentName}Props, defined above the component
- Extract reusable logic into custom hooks (use{Feature} naming)
- Prefer composition over prop drilling; use React context sparingly

## State & Data
- Use React hook form + zod for form validation
- Server state: Next.js fetch with cache/revalidate or React Query
- Client state: useState for local, useReducer for complex, Zustand for global
- Never mutate state directly; always use setter functions or immutable updates

## Styling
- Tailwind CSS with utility-first approach; use cn() helper for conditional classes
- Component variants via cva (class-variance-authority)
- No inline styles; no CSS modules unless migrating legacy code

## Testing
- Vitest + React Testing Library (RTL)
- Test behavior, not implementation — query by role, text, label
- Use userEvent over fireEvent for realistic interaction testing
- Mock API calls with MSW; test loading, error, and success states

## TypeScript
- Strict mode enabled; no any — use unknown + type guards
- Use satisfies operator for type-safe object literals
- Prefer discriminated unions over optional properties for variant types

## Performance
- Use React.memo, useMemo, useCallback only when profiling shows need
- Lazy-load heavy components with next/dynamic or React.lazy
- Images via next/image; fonts via next/font");

    private static readonly ConventionTemplate TypescriptNode = new(
        Key: "typescript-node",
        Name: "TypeScript + Node.js",
        Description: "TypeScript 5+ with Node.js, strict mode, ESM, async/await, Pino logging, Vitest",
        Conventions: @"# TypeScript + Node.js Conventions

## Language & Runtime
- TypeScript 5+ with strict mode enabled (strict, noImplicitAny, noImplicitReturns, noFallthroughCasesInSwitch, exactOptionalPropertyTypes)
- Node.js 22 LTS, ESM modules only (type: ""module"" in package.json)
- Use .js extensions in all import paths (required for ESM)

## Code Style
- Files: kebab-case (e.g. event-store.ts, plugin-manager.ts)
- Interfaces: I prefix (IEventStore, IPluginManifest)
- Classes: PascalCase; Functions: camelCase; Constants: SCREAMING_SNAKE_CASE
- Boolean functions: is/has/should prefix (isRetryable(), hasCapability())
- Prefer interfaces over type aliases for object shapes
- Use readonly where possible; never mutate state — always create new objects

## Async & Error Handling
- ALWAYS use async/await, NEVER .then()/.catch() chains
- Use structured custom error classes with code, context, retryable, severity
- All async operations must have proper error handling with try/catch
- Implement exponential backoff retry for network/external service calls

## Testing
- Vitest 3.x with colocated *.test.ts files
- Mock external APIs using MSW (Mock Service Worker)
- Coverage targets: 80% line, 75% branch, 85% function
- Test naming: describe('ClassName', () => it('should do X when Y'))

## Logging
- Pino for structured JSON logging; never console.log in production
- Log levels: DEBUG (dev), INFO (milestones), WARN (recoverable), ERROR (failures)
- ALWAYS redact API keys, tokens, passwords from logs

## Imports
- Order: 1) Node.js built-ins 2) External deps 3) Internal packages 4) Relative imports
- Prefer named exports over default exports

## Date/Time
- dayjs with UTC plugin; always ISO 8601 with millisecond precision");

    private static readonly ConventionTemplate PythonFastApi = new(
        Key: "python-fastapi",
        Name: "Python + FastAPI",
        Description: "Python + FastAPI with Pydantic v2, async, SQLAlchemy 2.0, pytest",
        Conventions: @"# Python + FastAPI Conventions

## Framework
- FastAPI with Pydantic v2 for request/response validation
- Async by default — all endpoints and DB operations use async/await
- Dependency injection via Depends() for services, DB sessions, auth
- Use APIRouter for route organization; one router per domain

## Pydantic Models
- Pydantic v2 with model_config = ConfigDict(strict=True) for input validation
- Separate schemas: {Model}Create, {Model}Update, {Model}Response, {Model}InDB
- Use Field() for validation constraints and documentation
- Custom validators with @field_validator and @model_validator

## Database
- SQLAlchemy 2.0 with async engine (asyncpg for PostgreSQL)
- Alembic for migrations; never edit generated migrations
- Repository pattern: separate DB logic from route handlers
- Use async sessions with async_sessionmaker; always commit/rollback in try/finally

## Code Style
- black formatter; ruff linter; type hints everywhere
- Files: snake_case.py; one router per file
- Dependencies in deps.py; settings via pydantic-settings BaseSettings
- Environment config via .env file (never committed)

## Error Handling
- Custom HTTPException subclasses for domain errors
- Global exception handlers for consistent error response format
- Structured logging with structlog; correlation IDs on all requests

## Testing
- pytest with httpx.AsyncClient for endpoint testing
- pytest-asyncio for async test functions
- Use factory functions for test data; separate test database
- Test happy paths, validation errors, auth failures, and edge cases

## Performance
- Use connection pooling (SQLAlchemy pool_size + asyncpg)
- Background tasks via FastAPI BackgroundTasks or Celery for heavy work
- Redis for caching; rate limiting with slowapi");

    private static readonly ConventionTemplate PythonDjango = new(
        Key: "python-django",
        Name: "Python + Django",
        Description: "Python + Django with DRF, ORM patterns, migrations, management commands",
        Conventions: @"# Python + Django Conventions

## Framework
- Django 5+ with Django REST Framework (DRF) for APIs
- Follow Django's MTV (Model-Template-View) architecture
- Use class-based views for APIs; function-based views for simple endpoints
- Always use Django's built-in security features (CSRF, SQL injection prevention, XSS)

## Models & Database
- Fat models, thin views — business logic belongs in model methods or managers
- Always create migrations (makemigrations/migrate); never edit migrations manually
- Use Django ORM exclusively — no raw SQL unless performance-critical (then document why)
- Index frequently queried fields; use select_related/prefetch_related to avoid N+1
- Use django-model-utils for TimeStampedModel, SoftDeletableModel patterns

## DRF APIs
- Serializers for all input validation and output formatting
- Use ViewSets + Routers for RESTful endpoints
- Permission classes for authorization; token or session auth
- Pagination on all list endpoints (PageNumberPagination or CursorPagination)
- Filter backends with django-filter for query parameters

## Code Style
- black formatter; ruff linter; isort for imports
- Type hints on all function signatures
- Apps in snake_case directories; models in PascalCase
- Settings split: base.py, development.py, production.py, test.py

## Testing
- pytest-django with fixtures; use APIClient for endpoint tests
- Factory Boy for model factories; faker for test data
- Test models, serializers, views, and permissions separately
- Always test migrations in CI (--run-syncdb)

## Common Patterns
- Signals: use sparingly, prefer explicit method calls
- Celery for background tasks; Redis for caching and message broker
- Management commands for admin operations
- Custom middleware for request logging, performance monitoring");

    private static readonly ConventionTemplate CsharpAspnet = new(
        Key: "csharp-aspnet",
        Name: "C# .NET 8+",
        Description: "C# 12 with .NET 8+, records, pattern matching, async/await, xUnit, EF Core",
        Conventions: @"# C# .NET Conventions

## Language
- C# 12 with .NET 8+; use latest features (primary constructors, collection expressions, raw strings)
- Nullable reference types enabled project-wide (<Nullable>enable</Nullable>)
- File-scoped namespaces (namespace X;) over block-scoped
- Use records for immutable DTOs and value objects; record struct for small value types

## Code Style
- .editorconfig with Microsoft conventions; dotnet format for enforcement
- Classes/records: PascalCase; methods: PascalCase; params/locals: camelCase; constants: PascalCase
- Prefix interfaces with I (IRepository, IService); private fields with _ (_logger)
- Prefer pattern matching (is, switch expressions) over type casting
- Use collection expressions ([1, 2, 3]) and LINQ for data transformations

## Async
- async/await for all I/O operations; suffix async methods with Async
- Use ValueTask<T> for hot-path methods that often complete synchronously
- CancellationToken on all async public APIs
- Never use .Result or .Wait() — always await

## Error Handling
- Custom exception hierarchy for domain errors
- ProblemDetails for API error responses (RFC 7807)
- Global exception middleware for consistent error handling
- Use ILogger<T> for structured logging; never Console.Write in production

## Testing
- xUnit with FluentAssertions for assertions
- Moq or NSubstitute for mocking; AutoFixture for test data generation
- Test naming: Method_Scenario_ExpectedResult
- Integration tests with WebApplicationFactory<T> and Testcontainers

## Database
- Entity Framework Core with code-first migrations
- Repository pattern or CQRS with MediatR
- Always use .AsNoTracking() for read-only queries
- Seeding via IEntityTypeConfiguration

## DI & Architecture
- Built-in Microsoft.Extensions.DependencyInjection
- Register services by interface; prefer scoped lifetime for request-scoped services
- Options pattern (IOptions<T>) for configuration
- Minimal APIs for simple endpoints; controllers for complex domains");

    private static readonly ConventionTemplate RustActix = new(
        Key: "rust-actix",
        Name: "Rust",
        Description: "Rust with cargo, clippy, thiserror/anyhow error handling, async with tokio",
        Conventions: @"# Rust Conventions

## Language
- Rust stable (latest); use edition 2021+
- cargo for build, test, and dependency management
- clippy with default + pedantic lints; rustfmt for formatting

## Code Style
- Modules: snake_case; Types/Traits: PascalCase; Functions/vars: snake_case; Constants: SCREAMING_SNAKE_CASE
- Prefer &str over String in function parameters; return String when ownership is needed
- Use derive macros: Debug, Clone, PartialEq on most types; Serialize/Deserialize with serde
- Prefer impl Trait in argument position for flexibility; explicit types in return position
- Keep functions small; extract into separate functions rather than deep nesting

## Error Handling
- thiserror for library error types (custom enums implementing std::error::Error)
- anyhow for application-level error propagation (anyhow::Result, context())
- Use ? operator for error propagation; never .unwrap() in production code
- .expect(""reason"") only when invariant is guaranteed; document why

## Ownership & Borrowing
- Prefer borrowing (&T, &mut T) over cloning; clone only when truly needed
- Use Cow<str> when ownership is conditionally needed
- Lifetime annotations: keep simple, use 'static for owned data in async contexts
- Arc<T> for shared ownership across threads; Mutex/RwLock for interior mutability

## Async
- tokio runtime for async I/O; use #[tokio::main] for entry point
- async fn for I/O-bound operations; spawn_blocking for CPU-bound work
- Use tokio::select! for concurrent operations; tokio::sync for async-aware synchronization
- Pin futures when needed for self-referential async code

## Testing
- #[cfg(test)] module in each file for unit tests; tests/ directory for integration tests
- Use assert!, assert_eq!, assert_ne! macros
- mockall crate for trait mocking; tokio::test for async tests
- Property-based testing with proptest for complex logic

## Dependencies
- Minimal dependency footprint; audit with cargo-audit
- Feature flags for optional functionality
- Workspace (Cargo.toml) for multi-crate projects");

    private static readonly ConventionTemplate GoStdlib = new(
        Key: "go-stdlib",
        Name: "Go 1.21+",
        Description: "Go 1.21+ with modules, error handling, goroutines, standard library",
        Conventions: @"# Go Conventions

## Language
- Go 1.21+ with modules (go.mod); use latest language features (slog, slices, maps)
- Follow Effective Go and Go Code Review Comments guidelines
- Use standard library first; add dependencies only when clearly justified

## Code Style
- gofmt for formatting (non-negotiable); golangci-lint for linting
- Package names: short, lowercase, single-word (avoid util, common, misc)
- Exported names: PascalCase; unexported: camelCase; acronyms all-caps (HTTPClient, ID)
- Interfaces: -er suffix for single-method (Reader, Writer); descriptive for multi-method
- Accept interfaces, return structs

## Error Handling
- ALWAYS check and handle errors; never use _ for error return values
- Return errors, don't panic (panic only for truly unrecoverable situations)
- Wrap errors with fmt.Errorf(""context: %w"", err) for stack context
- Use errors.Is() and errors.As() for error checking; define sentinel errors as package vars
- Custom error types implement the error interface

## Concurrency
- goroutines for concurrent work; channels for communication between goroutines
- Use context.Context for cancellation, timeouts, and request-scoped values
- sync.WaitGroup for fan-out/fan-in; sync.Mutex for shared state (prefer channels)
- errgroup.Group for concurrent tasks with error propagation
- Never launch goroutines without a way to stop them (context or done channel)

## Testing
- Table-driven tests with t.Run() for subtests
- Test files: *_test.go colocated with source
- Use testify/assert for assertions; gomock or mockgen for interfaces
- Race detector enabled in CI: go test -race ./...

## Project Structure
- cmd/ for entry points; internal/ for private packages; pkg/ for public libraries
- One package per directory; avoid circular dependencies
- Keep main() thin — delegate to internal packages

## Logging
- log/slog (stdlib) for structured logging; never fmt.Println in production
- Log at appropriate levels: Debug, Info, Warn, Error");

    private static readonly ConventionTemplate JavaSpring = new(
        Key: "java-spring",
        Name: "Java 21+",
        Description: "Java 21+ with records, sealed classes, Spring Boot 3, JUnit 5, Maven/Gradle",
        Conventions: @"# Java Conventions

## Language
- Java 21+ with records, sealed classes, pattern matching, virtual threads
- Prefer records for immutable data carriers over Lombok @Data
- Use sealed interfaces/classes for closed type hierarchies
- switch expressions with pattern matching for type-safe dispatch

## Framework
- Spring Boot 3+ with Spring Web MVC or WebFlux
- Constructor injection (no field injection); @RequiredArgsConstructor with Lombok
- Use @RestController + @RequestMapping for APIs
- Spring Data JPA for database access; Flyway or Liquibase for migrations
- Spring Security for authentication and authorization

## Code Style
- Google Java Style Guide; format with google-java-format or Spotless
- Packages: reverse domain (com.example.project.module)
- Classes: PascalCase; methods/vars: camelCase; constants: SCREAMING_SNAKE_CASE
- One class per file; interfaces in their own files
- Prefer Optional over null returns; never pass Optional as parameter

## Error Handling
- Custom exception hierarchy extending RuntimeException for domain errors
- @ControllerAdvice with @ExceptionHandler for global API error handling
- Never catch Exception/Throwable except at top-level error boundaries
- Use try-with-resources for all AutoCloseable resources

## Testing
- JUnit 5 with @ParameterizedTest for data-driven tests
- Mockito for mocking; AssertJ for fluent assertions
- @SpringBootTest for integration tests; @WebMvcTest for controller tests
- Testcontainers for database integration tests
- Test naming: should{Action}When{Condition}

## Build
- Maven or Gradle (Kotlin DSL preferred for Gradle)
- Multi-module projects for large codebases
- Dependency management via BOM (Spring Boot parent POM)
- CI: build, test, SpotBugs/PMD static analysis, OWASP dependency check");

    private static readonly ConventionTemplate RubyRails = new(
        Key: "ruby-rails",
        Name: "Ruby + Rails",
        Description: "Ruby + Rails with MVC, ActiveRecord, RSpec, Rubocop, convention over configuration",
        Conventions: @"# Ruby + Rails Conventions

## Framework
- Rails 7+ with Hotwire (Turbo + Stimulus) for modern frontend
- Follow Rails conventions: convention over configuration
- MVC: fat models, skinny controllers; extract to service objects when models grow
- Use concerns for shared model behavior; keep concerns focused and small

## ActiveRecord
- Migrations: always reversible; never edit deployed migrations
- Scopes for reusable queries; avoid default_scope
- Validations in models; callbacks sparingly (prefer service objects for complex logic)
- Use includes/preload/eager_load to prevent N+1 queries
- Counter caches for has_many counts; database-level constraints alongside validations

## Code Style
- Rubocop with rubocop-rails, rubocop-rspec, rubocop-performance
- Files: snake_case.rb; Classes/Modules: PascalCase; methods/vars: snake_case; constants: SCREAMING_SNAKE_CASE
- Prefer symbols over strings for hash keys
- Use frozen_string_literal: true pragma in all files
- Prefer &: shorthand for simple blocks (users.map(&:name))

## API
- jbuilder or blueprinter for JSON serialization
- Versioned API routes (/api/v1/)
- Strong parameters for input sanitization
- Pagination with pagy (fast) or kaminari

## Background Jobs
- Sidekiq for background processing; Redis for caching and queues
- Keep jobs idempotent and retriable
- Use ActiveJob interface for portability

## Testing
- RSpec with FactoryBot for test factories; Faker for test data
- Request specs for API endpoints; model specs for validations/scopes
- Use shared_examples for common behavior tests
- VCR or WebMock for external API mocking
- Coverage target: 90%+; SimpleCov for coverage reporting

## Security
- Brakeman for static security analysis
- Use Rails security defaults; parameterize all user input
- Credentials: Rails encrypted credentials or environment variables");

    private static readonly ConventionTemplate ElixirPhoenix = new(
        Key: "elixir-phoenix",
        Name: "Elixir + Phoenix",
        Description: "Elixir + Phoenix with GenServer, LiveView, ExUnit, Ecto",
        Conventions: @"# Elixir + Phoenix Conventions

## Language
- Elixir 1.16+ with OTP 26+; leverage pattern matching, pipes, and protocols
- Prefer pipe operator (|>) for data transformation chains
- Use with for complex pattern matching with early exit
- Prefer keyword lists for options; maps for structured data
- Immutable data: transform, never mutate

## Phoenix Framework
- Phoenix 1.7+ with LiveView for interactive UIs
- Context modules (bounded contexts) for business logic — not controllers
- Controllers: thin; delegate to context modules
- LiveView for real-time features; use assigns and handle_event
- Use verified routes (~p) over path helpers

## Ecto
- Schemas in context directories; changesets for validation
- Migrations: always reversible; use Ecto.Migration
- Multi/Ecto.Multi for transactional operations
- Preload associations explicitly; never lazy-load
- Use Repo.all/get/one; avoid raw SQL unless performance-critical

## Code Style
- mix format for code formatting (non-negotiable)
- Credo for static analysis and code consistency
- Modules: PascalCase; functions: snake_case; atoms: snake_case
- Docs: @moduledoc and @doc on all public modules and functions
- Specs: @spec on all public functions for Dialyzer type checking

## OTP Patterns
- GenServer for stateful processes; Agent for simple state
- Supervisor trees for fault tolerance; let it crash philosophy
- Task for fire-and-forget async work; Task.async/await for parallel computation
- Registry for named process lookup; DynamicSupervisor for runtime process creation

## Testing
- ExUnit with descriptive test names
- Use setup/setup_all callbacks for test fixtures
- Mox for behavior-based mocking (define behaviors, mock in tests)
- DataCase for database tests; ConnCase for endpoint tests
- Property-based testing with StreamData for complex logic

## Deployment
- Mix releases for production deployment
- Config: config/runtime.exs for runtime configuration
- Health check endpoint; telemetry for observability");

    // ────────────────────────────────────────────────────────────────────
    // Aggregates
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All shipped templates, in a stable listing order.
    /// </summary>
    public static IReadOnlyList<ConventionTemplate> All { get; } = new ConventionTemplate[]
    {
        TypescriptReact,
        TypescriptNode,
        PythonFastApi,
        PythonDjango,
        CsharpAspnet,
        RustActix,
        GoStdlib,
        JavaSpring,
        RubyRails,
        ElixirPhoenix
    };

    private static readonly IReadOnlyDictionary<string, ConventionTemplate> ByKey =
        All.ToDictionary(t => t.Key, StringComparer.Ordinal);

    /// <summary>
    /// Looks up a template by key, returning <c>null</c> if none exists.
    /// </summary>
    public static ConventionTemplate? GetByKey(string key)
        => ByKey.TryGetValue(key, out var template) ? template : null;
}
