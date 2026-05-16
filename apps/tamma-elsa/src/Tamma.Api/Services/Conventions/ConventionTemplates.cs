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
    // Key naming mirrors the TS source: bare language keys (csharp, rust,
    // go, java) even though the bodies mention .NET, cargo, goroutines,
    // Spring Boot — the first port added framework suffixes which diverged
    // from the TS contract, so those were renamed back.
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

    private static readonly ConventionTemplate TypescriptReactNative = new(
        Key: "typescript-react-native",
        Name: "TypeScript + React Native/Expo",
        Description: "TypeScript + React Native with Expo SDK, navigation, native modules",
        Conventions: @"# TypeScript + React Native/Expo Conventions

## Framework
- Expo SDK (latest) with expo-router for file-based navigation
- TypeScript strict mode; .tsx for components, .ts for utilities
- Use Expo modules first; eject to bare workflow only when truly needed

## Components & Navigation
- Functional components with hooks; no class components
- expo-router with file-based routing in app/ directory
- Use Stack, Tabs, and Drawer layouts from expo-router
- Platform-specific files: Component.ios.tsx / Component.android.tsx when needed

## Styling
- StyleSheet.create() for all styles — never inline style objects
- Use platform-specific styling via Platform.select() or Platform.OS checks
- Responsive design: use useWindowDimensions, percentage-based layouts, or flexbox
- No web CSS libraries; use react-native compatible styling only

## State & Data
- React Query (TanStack Query) for server state with offline persistence
- Zustand or useReducer for complex client state
- AsyncStorage for simple key-value persistence; expo-secure-store for secrets
- Never store sensitive data in plain AsyncStorage

## Testing
- Jest with @testing-library/react-native
- Test component behavior and accessibility
- Use detox for E2E testing on real devices/simulators
- Mock native modules in jest.setup.ts

## Performance
- Use FlatList/FlashList for long lists — never ScrollView with many children
- Memoize expensive renders with React.memo and useMemo
- Use Hermes engine (default with Expo)
- Optimize images: use expo-image, proper sizing, caching

## Error Handling
- Global error boundary for uncaught render errors
- Structured error handling for API calls with retry logic
- Use Sentry or Bugsnag for crash reporting");

    private static readonly ConventionTemplate Python = new(
        Key: "python",
        Name: "Python 3.11+",
        Description: "Python 3.11+ with type hints, pytest, ruff, black, asyncio",
        Conventions: @"# Python Conventions

## Language
- Python 3.11+ required; use latest language features (match/case, ExceptionGroup, tomllib)
- Type hints on ALL function signatures (params and return types)
- Use from __future__ import annotations for forward references

## Code Style
- Formatter: black (line length 88); Linter: ruff
- Files: snake_case.py; Classes: PascalCase; Functions/vars: snake_case; Constants: SCREAMING_SNAKE_CASE
- Docstrings: Google style on all public functions, classes, and modules
- Prefer dataclasses or Pydantic models over plain dicts for structured data
- Use pathlib.Path over os.path; use f-strings over .format()

## Async
- asyncio for I/O-bound concurrency; prefer async def over threading
- Use asyncio.gather() for concurrent tasks; asyncio.TaskGroup for structured concurrency
- Use async context managers (async with) for resource management
- Never mix sync and async code without explicit bridge (asyncio.to_thread)

## Error Handling
- Custom exception hierarchy inheriting from a base project exception
- Use specific exception types; never bare except:
- Context managers (with statement) for resource cleanup
- Logging with structlog or stdlib logging; never print() in production

## Testing
- pytest with fixtures; colocated tests/ directory or test_ prefix files
- pytest-asyncio for async tests; pytest-cov for coverage
- Use factories (factory_boy) or fixtures for test data
- Mock external services with responses or aioresponses libraries
- Coverage target: 80%+ line coverage

## Dependencies
- pyproject.toml for project config; uv or pip-tools for dependency management
- Pin all dependencies; use virtual environments always
- Separate dev dependencies from production

## Imports
- Order: 1) stdlib 2) third-party 3) local; one blank line between groups
- Use absolute imports; avoid wildcard imports (from x import *)");

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

    private static readonly ConventionTemplate Csharp = new(
        Key: "csharp",
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

    private static readonly ConventionTemplate Rust = new(
        Key: "rust",
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

    private static readonly ConventionTemplate Go = new(
        Key: "go",
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

    private static readonly ConventionTemplate Java = new(
        Key: "java",
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

    private static readonly ConventionTemplate Kotlin = new(
        Key: "kotlin",
        Name: "Kotlin",
        Description: "Kotlin with coroutines, Ktor/Spring, JUnit 5, idiomatic patterns",
        Conventions: @"# Kotlin Conventions

## Language
- Kotlin 1.9+; use data classes, sealed classes, extension functions idiomatically
- Prefer val over var; prefer immutable collections (listOf, mapOf)
- Use null safety: avoid !!, use ?.let, ?:, and safe casts (as?)
- Scope functions: let for null checks, apply for object configuration, run for transformations
- Use when expressions for exhaustive matching on sealed hierarchies

## Framework
- Ktor or Spring Boot 3 (Kotlin-first with coroutines support)
- Coroutines for all async operations; structured concurrency with coroutineScope
- Use Flow for reactive streams; suspend functions for one-shot async operations
- Dependency injection: Koin (lightweight) or Spring DI

## Code Style
- Kotlin Coding Conventions (official); ktlint or detekt for linting
- Files: PascalCase.kt for single class, camelCase.kt for top-level functions
- Classes: PascalCase; functions/properties: camelCase; constants: SCREAMING_SNAKE_CASE
- Extension functions in separate files grouped by receiver type
- Use named arguments for functions with 3+ parameters

## Error Handling
- Result<T> for operations that may fail; runCatching for exception wrapping
- Custom sealed class hierarchies for domain errors
- Never throw exceptions for expected control flow
- CoroutineExceptionHandler for top-level coroutine error handling

## Testing
- JUnit 5 with kotlin.test assertions
- MockK for mocking (Kotlin-native); Kotest for property-based testing
- Use runTest for coroutine tests; TestDispatcher for time control
- Test naming: `should do X when Y` (backtick function names)

## Database
- Exposed (Kotlin SQL framework) or Spring Data JPA
- Flyway for migrations; connection pooling with HikariCP");

    private static readonly ConventionTemplate Swift = new(
        Key: "swift",
        Name: "Swift 5.9+ (SwiftUI)",
        Description: "Swift 5.9+ with SwiftUI, async/await, Combine, XCTest, SPM",
        Conventions: @"# Swift (SwiftUI) Conventions

## Language
- Swift 5.9+ with strict concurrency checking enabled
- Use async/await for all asynchronous operations
- Prefer value types (struct, enum) over reference types (class) unless identity needed
- Use Swift's result builders for declarative APIs

## SwiftUI
- Declarative UI with SwiftUI Views; one View per file
- MVVM architecture: View observes @Observable ViewModel
- Use @State for local view state; @Binding for child → parent communication
- @Environment for app-wide dependencies (injected at root)
- Extract reusable view components; keep View body under 30 lines

## Code Style
- Swift API Design Guidelines (official); SwiftLint for enforcement
- Types: PascalCase; functions/properties: camelCase; protocols: PascalCase (-able, -ible suffix)
- Use trailing closures; omit argument labels where natural
- Prefer guard for early exits over nested if-let
- Mark classes final by default; use access control (private, internal, public) explicitly

## Error Handling
- Use Swift's typed throws (throws(MyError)) when available
- Define error enums conforming to Error and LocalizedError
- Result<Success, Failure> for synchronous operations that can fail
- Never force-unwrap (!) in production code; use guard let, if let, or nil coalescing

## Data & Networking
- Codable for JSON serialization; custom CodingKeys when API names differ
- URLSession with async/await for networking
- SwiftData or Core Data for persistence; UserDefaults for simple settings
- Keychain for secrets (use a wrapper library)

## Testing
- XCTest framework; test naming: test_method_condition_expectedResult
- Use @MainActor for tests touching UI state
- Mock protocols for dependency injection in tests
- XCUITest for UI tests; snapshot testing for layout verification

## Package Management
- Swift Package Manager (SPM) for dependencies
- Minimal dependencies; prefer Apple frameworks when sufficient");

    private static readonly ConventionTemplate SwiftUikit = new(
        Key: "swift-uikit",
        Name: "Swift + UIKit",
        Description: "Swift + UIKit with MVVM, delegates, programmatic UI or Storyboard",
        Conventions: @"# Swift + UIKit Conventions

## Architecture
- MVVM pattern: ViewController → ViewModel → Model/Service
- Coordinator pattern for navigation flow management
- Protocol-oriented design for dependency injection and testability
- Separate networking, persistence, and UI into distinct layers

## UIKit Patterns
- Prefer programmatic UI over Storyboards for better merge conflict handling
- Use Auto Layout with NSLayoutConstraint or SnapKit
- UITableView/UICollectionView with DiffableDataSource for list management
- Custom UIView subclasses for reusable components; override layoutSubviews sparingly

## Code Style
- Swift API Design Guidelines; SwiftLint for enforcement
- Types: PascalCase; functions/properties: camelCase
- ViewControllers: {Feature}ViewController; Views: {Feature}View; Cells: {Feature}Cell
- Delegate/DataSource protocols in extensions on the ViewController
- Mark sections with // MARK: - for organization

## Memory Management
- Use [weak self] in closures that capture self to prevent retain cycles
- Prefer value types; use class only for UIKit subclasses and reference identity
- Invalidate timers and remove observers in deinit
- Use Instruments (Leaks, Allocations) to verify memory behavior

## Async & Networking
- async/await for modern concurrency; Combine for reactive streams
- URLSession with async/await; Alamofire only if justified
- DispatchQueue.main.async for UI updates from background threads
- Operation/OperationQueue for complex task dependencies

## Testing
- XCTest for unit tests; protocol mocks for DI
- Separate UI logic into ViewModels for testability
- XCUITest for integration/UI tests
- Test ViewModels independently from ViewControllers");

    private static readonly ConventionTemplate DartFlutter = new(
        Key: "dart-flutter",
        Name: "Dart 3 + Flutter",
        Description: "Dart 3 + Flutter with Riverpod/Bloc, widget testing, null safety",
        Conventions: @"# Dart + Flutter Conventions

## Language
- Dart 3 with sound null safety; use records, patterns, sealed classes
- Prefer final for local variables; const for compile-time constants
- Use named parameters for functions with 2+ optional parameters
- Prefer expression bodies for simple one-line functions/getters

## Flutter Architecture
- Feature-first folder structure: lib/features/{feature}/{data,domain,presentation}/
- State management: Riverpod (preferred) or Bloc/Cubit
- Use go_router for declarative routing
- Separate business logic from widgets; keep build() methods lean

## Widgets
- Prefer StatelessWidget; use StatefulWidget only when widget lifecycle is needed
- Extract widgets into separate files when they exceed ~50 lines
- Use const constructors wherever possible for performance
- Key parameter on list items and conditionally rendered widgets

## Code Style
- dart format for formatting; dart analyze with strict rules
- Files: snake_case.dart; Classes: PascalCase; functions/vars: camelCase; constants: camelCase
- Private members: _ prefix; library-private: no prefix with part/part of
- Effective Dart guidelines for documentation comments (///)

## Error Handling
- Custom exception classes for domain errors; use sealed classes for error types
- try/catch at service boundaries; propagate typed errors to UI
- Use Either<Failure, Success> pattern (from dartz/fpdart) for functional error handling
- Never swallow exceptions silently

## Testing
- widget tests with flutter_test (WidgetTester, pumpWidget, find.*)
- Unit tests for services, repositories, and state management
- mockito for mocking; mocktail as alternative
- Golden tests for visual regression; integration_test for E2E
- Test naming: test('should do X when Y')

## Performance
- Use const widgets; avoid unnecessary rebuilds
- ListView.builder for long lists; never build all items eagerly
- Optimize images: cached_network_image, proper sizing
- Profile with Flutter DevTools; check for jank in release mode");

    private static readonly ConventionTemplate C = new(
        Key: "c",
        Name: "C (C11/C17)",
        Description: "C11/C17 with manual memory management, valgrind, CMake, assert",
        Conventions: @"# C Conventions

## Language
- C11 or C17 standard; compile with -std=c17 -Wall -Wextra -Werror -pedantic
- Use stdint.h fixed-width types (uint32_t, int64_t) over plain int for sizes
- Use stdbool.h for bool type; use stddef.h for size_t, NULL
- Prefer stack allocation; heap allocation only when size is dynamic or lifetime exceeds scope

## Memory Management
- Every malloc/calloc MUST have a corresponding free; document ownership in comments
- Check return value of malloc (never assume success)
- Set pointers to NULL after free to prevent use-after-free
- Use valgrind (memcheck) and AddressSanitizer (-fsanitize=address) in CI
- Prefer arena/pool allocators for groups of related allocations

## Code Style
- Files: snake_case.c/.h; Functions: snake_case; Types: PascalCase_t or snake_case_t
- Macros: SCREAMING_SNAKE_CASE; prefix with project name to avoid collisions
- Header guards: #ifndef PROJECT_MODULE_H / #define PROJECT_MODULE_H / #endif
- One function per logical operation; keep functions under 50 lines when practical
- Declare variables at the top of the block or at point of first use (C99+)

## Error Handling
- Return error codes (int/enum); 0 for success, negative for errors
- Use errno for system call errors; document error codes in header comments
- assert() for programming errors (invariants); return codes for runtime errors
- Goto for cleanup patterns (goto cleanup; with labels at function end)

## Build System
- CMake 3.20+ as build system; define targets with target_* functions
- Separate public headers (include/) from implementation (src/)
- Static analysis with clang-tidy; compile with both GCC and Clang

## Testing
- Unity or CMocka for unit testing; CTest for test orchestration
- Test each module independently; mock external dependencies
- Fuzz testing with AFL or libFuzzer for input parsing code

## Security
- Use snprintf over sprintf; strncpy over strcpy; bounds-check all array access
- Validate all external input (sizes, indices, format strings)
- Compile with stack protection (-fstack-protector-strong)");

    private static readonly ConventionTemplate Cpp = new(
        Key: "cpp",
        Name: "C++ (C++20)",
        Description: "C++20 with RAII, smart pointers, STL, CMake, Google Test",
        Conventions: @"# C++ Conventions

## Language
- C++20 standard; use concepts, ranges, std::format, coroutines where applicable
- RAII for all resource management (files, memory, locks, sockets)
- Smart pointers: unique_ptr for ownership, shared_ptr for shared ownership, never raw new/delete
- Prefer value semantics; move semantics for expensive-to-copy types
- Use std::optional, std::variant, std::expected for nullable/sum types

## Code Style
- Google C++ Style Guide or C++ Core Guidelines
- Files: snake_case.cpp/.h or .cc/.hpp; Classes: PascalCase; Functions: PascalCase or camelCase
- Namespaces: lowercase (project::module); avoid using namespace std; in headers
- Use constexpr and consteval for compile-time computation
- Prefer auto for complex types; explicit types for readability in function signatures

## STL & Libraries
- Use STL containers (vector, unordered_map, string_view) as defaults
- ranges and views for lazy, composable data pipelines
- std::span for non-owning array references
- Prefer algorithms (std::sort, std::find_if) over raw loops

## Error Handling
- Exceptions for exceptional situations; std::expected<T, E> for expected failures
- noexcept on functions that cannot throw (destructors, move operations)
- Never throw in destructors; catch exceptions at module boundaries
- Custom exception hierarchy inheriting from std::runtime_error

## Build System
- CMake 3.20+ with modern target-based approach (target_compile_features, target_link_libraries)
- vcpkg or Conan for dependency management
- Compile with -Wall -Wextra -Werror; enable sanitizers in debug builds

## Testing
- Google Test (gtest) + Google Mock (gmock) for unit testing
- CTest for test orchestration; benchmarks with Google Benchmark
- Test public interfaces; mock at interface boundaries
- Fuzz testing with libFuzzer for parsers and serializers

## Performance
- Profile before optimizing (perf, Instruments, VTune)
- Cache-friendly data layout (SoA over AoS when appropriate)
- Avoid premature virtual dispatch; prefer templates for static polymorphism");

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

    private static readonly ConventionTemplate PhpLaravel = new(
        Key: "php-laravel",
        Name: "PHP 8.2+ + Laravel",
        Description: "PHP 8.2+ with Laravel, Eloquent ORM, PHPUnit/Pest, PSR-12 standards",
        Conventions: @"# PHP + Laravel Conventions

## Language
- PHP 8.2+ with typed properties, enums, readonly classes, fibers
- Strict types: declare(strict_types=1); in every file
- Use union types, intersection types, and null-safe operator (?->)
- Prefer readonly properties and constructor promotion

## Framework
- Laravel 11+ with service container, facades, and middleware
- Follow Laravel directory conventions (app/Models, app/Http/Controllers, etc.)
- Use Form Requests for validation; Resources for API responses
- Artisan commands for admin operations; Queues for background work

## Eloquent ORM
- Models in app/Models; one model per table
- Use relationships, scopes, casts, accessors/mutators
- Eager load relationships (with()) to prevent N+1
- Database migrations: always reversible; use schema builder
- Seeders and factories for test data

## Code Style
- PSR-12 coding standard; Laravel Pint for formatting
- PHPStan or Larastan at level 8+ for static analysis
- Classes: PascalCase; methods/vars: camelCase; constants: SCREAMING_SNAKE_CASE
- Prefer dependency injection over facades in classes
- Single-action controllers for focused endpoints (InvokableController)

## Testing
- Pest (preferred) or PHPUnit for testing
- Feature tests for HTTP endpoints; Unit tests for isolated classes
- Database testing with RefreshDatabase trait
- Mock external services; use Http::fake() for API mocking
- Test naming: it('should create a user with valid data')

## Security
- Validate ALL input via Form Requests; never trust user data
- Use Laravel's built-in CSRF, XSS, SQL injection protections
- Encrypt sensitive data; use Laravel's encrypted attributes
- Rate limiting on authentication and API endpoints

## Architecture
- Service classes for business logic (not in controllers)
- Repository pattern optional (Eloquent is already repository-like)
- Events and listeners for decoupled side effects
- Jobs for async processing; Horizon for queue monitoring");

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

    private static readonly ConventionTemplate Scala = new(
        Key: "scala",
        Name: "Scala 3",
        Description: "Scala 3 with ZIO/Cats Effect, sbt, ScalaTest, functional programming",
        Conventions: @"# Scala 3 Conventions

## Language
- Scala 3 with new syntax (indentation-based, enum, extension methods, given/using)
- Prefer immutable data (val, case class, sealed trait hierarchies)
- Use opaque types for domain primitives (UserId, Email)
- Union types (A | B) and intersection types (A & B) for precise modeling
- Prefer match expressions over if/else chains for ADT handling

## Functional Programming
- ZIO or Cats Effect for pure functional I/O and effect management
- Use for-comprehensions for sequential effect composition
- Prefer type classes over inheritance for ad-hoc polymorphism
- Avoid side effects: IO/Task for all external interactions
- Use refined types or smart constructors for validated data

## Code Style
- scalafmt for formatting; scalafix for linting and rewrites
- Files: PascalCase.scala matching primary type; packages: lowercase dot-separated
- Types: PascalCase; methods/vals: camelCase; constants: PascalCase
- Prefer given instances over implicit vals; using clauses over implicit parameters
- Extension methods over implicit classes

## Error Handling
- Typed errors: ZIO[R, E, A] or EitherT/IO with custom error ADTs
- Never throw exceptions in pure code; use Either or ZIO error channel
- Accumulate errors with Validated (Cats) or ZIO.validate for parallel validation
- mapError/catchAll for error transformation at boundaries

## Testing
- ScalaTest (FlatSpec/WordSpec style) or ZIO Test
- Mocking with ZIO mock layers or ScalaMock
- Property-based testing with ScalaCheck or zio-test check
- Test naming: ""should produce X when given Y""
- In-memory test implementations over mocking when practical

## Build
- sbt with multi-project builds; dependencies in build.sbt
- Publish with sbt-ci-release; cross-compile for multiple Scala versions
- Wartremover or Scalafix for additional compile-time checks

## Architecture
- Layered ZIO: Repositories → Services → API (with ZLayer for DI)
- Tapir for type-safe HTTP endpoint definitions
- Circe or zio-json for JSON serialization");

    // ────────────────────────────────────────────────────────────────────
    // Action-triggered conventions — fire when the LLM call's action
    // matches the convention's keywords (e.g. writeCode, reviewCode).
    // ────────────────────────────────────────────────────────────────────

    private static readonly ConventionTemplate ActionWriteCode = new(
        Key: "action-write-code",
        Name: "Code Writing",
        Description: "Conventions for writing new code: incremental changes, compile-first, minimal scope",
        Conventions: @"# Code Writing Conventions

## Approach
- Work in small increments; compile/type-check after each logical change
- Write the simplest implementation that satisfies the requirement — no speculative generality
- Run existing tests before and after changes to catch regressions immediately
- Prefer editing existing files to creating new ones

## Scope & Abstraction
- Do not introduce abstractions until a pattern repeats at least three times
- One concern per function; one purpose per file
- Keep functions under 40 lines; if longer, extract a named helper
- Don't add features, helpers, or cleanup beyond what the task requires

## Code Hygiene
- Remove dead code; don't comment it out
- No TODO/FIXME without an associated issue/ticket reference
- Prefer standard library and existing project utilities over new dependencies
- Match surrounding code style — consistency over personal preference

## Safety
- Validate inputs at system boundaries (user input, external APIs)
- Never concatenate user input into SQL, shell commands, or templates
- Use parameterized queries, escaping functions, or type-safe builders
- Handle all possible error states — don't rely on happy path assumptions");

    private static readonly ConventionTemplate ActionReviewCode = new(
        Key: "action-review-code",
        Name: "Code Review",
        Description: "Conventions for reviewing PRs: bug severity, security lens, signal over noise",
        Conventions: @"# Code Review Conventions

## Priorities (highest to lowest)
1. Security vulnerabilities (injection, auth bypass, secrets exposure, SSRF)
2. Correctness bugs (logic errors, race conditions, null dereference, off-by-one)
3. Data integrity issues (missing transactions, inconsistent state, lost updates)
4. Performance regressions (N+1 queries, unbounded allocations, missing indexes)
5. API contract violations (breaking changes, missing validation, wrong status codes)
6. Maintainability concerns (naming, duplication, coupling) — lowest priority

## What NOT to flag
- Style/formatting issues handled by linters (let tools enforce these)
- Personal preference with no objective justification
- Theoretical future problems with no current evidence
- Trivial naming suggestions unless genuinely confusing

## Review Approach
- Verify the change matches the stated intent (PR description, linked issue)
- Check edge cases: empty inputs, boundary values, concurrent access, error paths
- Look for missing tests for new logic paths
- Consider rollback safety: can this change be reverted without data loss?
- Check for secrets, credentials, or PII in code or logs

## Feedback Style
- Lead with what the change gets right
- Frame issues as questions when uncertain: ""Could this race if...?""
- Severity-tag findings: [critical] [bug] [nit] [question]
- Provide suggested fix when flagging an issue, not just the problem");

    private static readonly ConventionTemplate ActionDesign = new(
        Key: "action-design",
        Name: "System Design",
        Description: "Conventions for architecture & system design: constraints-first, trade-off documentation",
        Conventions: @"# System Design Conventions

## Process
- Start with constraints: latency budget, throughput target, consistency requirement, team size
- Define system boundaries and data ownership before implementation details
- Document trade-offs explicitly: what you're gaining, what you're sacrificing, why
- Prefer boring, proven technology unless a novel approach has measurable advantage

## Architecture Principles
- Design for failure: every external dependency will be unavailable at some point
- Make the common path fast and the error path safe
- Prefer stateless services; push state to the database or cache layer
- Define API contracts (schemas, status codes, error formats) before implementation
- Separate reads from writes when access patterns differ significantly

## Documentation
- Record decisions as ADRs (context → decision → consequences)
- Diagrams: data flow for understanding, sequence for interactions, deployment for ops
- Specify what is NOT in scope to prevent scope creep
- Include capacity estimates: expected load, storage growth, scaling triggers

## Anti-Patterns to Avoid
- Distributed monolith: microservices that all deploy together
- Premature optimization: measure before you optimize
- Resume-driven development: picking tech for novelty over fitness
- Ignoring operational burden: every service is a pager at 3am");

    private static readonly ConventionTemplate ActionWriteTests = new(
        Key: "action-write-tests",
        Name: "Test Writing",
        Description: "Conventions for writing tests: TDD rhythm, one behavior per test, meaningful assertions",
        Conventions: @"# Test Writing Conventions

## TDD Rhythm
- RED: Write a failing test that defines the expected behavior
- GREEN: Write the minimal implementation to make it pass
- REFACTOR: Clean up while keeping tests green
- Commit at each green state — don't batch large untested changes

## Test Structure
- One behavior per test; test name describes the scenario and expected outcome
- Arrange-Act-Assert (or Given-When-Then) structure in every test
- Tests must be independent — no shared mutable state between tests
- Tests must be deterministic — no flakiness from timing, ordering, or randomness

## What to Test
- Happy path: the feature works as intended
- Edge cases: empty input, null/undefined, boundary values, maximum lengths
- Error cases: invalid input, network failures, permission denied
- State transitions: before/after for mutations

## What NOT to Test
- Implementation details (private methods, internal state shape)
- Framework/library behavior (trust that React renders, that Express routes)
- Trivial code with no logic (simple getters, pass-through functions)

## Mocking
- Mock at system boundaries: external APIs, databases, file system, clock
- Don't mock what you don't own — wrap the dependency, mock the wrapper
- Prefer real implementations over mocks when fast enough (in-memory DB, test server)
- Assert on mock interactions only when the side effect IS the behavior");

    private static readonly ConventionTemplate ActionDebug = new(
        Key: "action-debug",
        Name: "Debugging",
        Description: "Conventions for systematic debugging: reproduce, isolate, verify",
        Conventions: @"# Debugging Conventions

## Process (in order)
1. REPRODUCE: Get a reliable reproduction of the bug before anything else
2. ISOLATE: Narrow the scope — which commit, which file, which function, which input
3. UNDERSTAND: Read the code path; form a hypothesis about root cause
4. FIX: Make the minimal change that addresses root cause, not symptoms
5. VERIFY: Write a test that fails without the fix and passes with it
6. CHECK: Ensure no regressions — run the full relevant test suite

## Techniques
- Use git bisect to find the introducing commit for regressions
- Add targeted logging/tracing at decision points, not everywhere
- Check recent changes to the affected code path (git log -p -- file)
- Reproduce with minimal input — strip away unrelated complexity
- Check the obvious first: typos, wrong variable, stale cache, wrong environment

## Anti-Patterns
- Don't refactor while debugging — fix the bug, then clean up separately
- Don't apply speculative fixes — understand before changing
- Don't fix symptoms: if you're adding a null check, ask WHY it's null
- Don't ignore the stack trace — read it bottom to top
- Don't assume the bug is in your code — check dependencies, configs, data

## After the Fix
- Document the root cause in the commit message (not just ""fix bug"")
- Consider: are there similar patterns elsewhere that could have the same issue?
- Add monitoring/alerting if the failure was silent");

    private static readonly ConventionTemplate ActionRefactor = new(
        Key: "action-refactor",
        Name: "Refactoring",
        Description: "Conventions for safe refactoring: small steps, green tests, preserve behavior",
        Conventions: @"# Refactoring Conventions

## Core Principle
Refactoring changes structure without changing behavior. If behavior changes, it's not a refactoring — it's a rewrite.

## Process
- Ensure test coverage exists BEFORE refactoring (add tests first if needed)
- Make one structural change at a time; run tests between each step
- Commit after each successful step — small atomic commits, not one big bang
- If tests break, revert the last step and try a smaller change

## Safe Refactoring Patterns
- Rename: variable, function, class, file — update all references
- Extract: pull code into a named function/method/module
- Inline: replace a trivial abstraction with its implementation
- Move: relocate code to a more appropriate module/layer
- Replace conditional with polymorphism (when the conditional repeats)

## Scope Control
- Don't mix refactoring with feature work in the same commit
- Don't refactor code you don't understand yet — understand first, then refactor
- Limit blast radius: refactor one module/layer at a time
- Don't rename public API surfaces without a migration plan
- Preserve git blame where possible (use git mv for file moves)

## When NOT to Refactor
- Under time pressure with no test coverage
- Code that is about to be deleted/replaced
- Code you've never run or tested
- Hot paths in production without performance benchmarks");

    private static readonly ConventionTemplate ActionDocument = new(
        Key: "action-document",
        Name: "Documentation Writing",
        Description: "Conventions for writing docs: audience-aware, examples-first, explain WHY",
        Conventions: @"# Documentation Conventions

## Core Principles
- Explain WHY, not WHAT — code shows what; docs explain intent and context
- Lead with examples — a code snippet is worth a thousand words
- Write for the reader's level, not your own
- Keep docs adjacent to code — proximity reduces staleness

## Structure
- Start with the one-sentence summary: what does this do and why would I use it?
- Quickstart/example first, detailed reference after
- Use headings and bullet points for scanability
- Include: prerequisites, common use cases, error scenarios, migration notes

## What to Document
- Public APIs: parameters, return values, error conditions, examples
- Architecture decisions: the WHY (use ADRs for permanent record)
- Setup/onboarding: getting from zero to running in minimal steps
- Non-obvious behavior: gotchas, implicit dependencies, ordering requirements

## What NOT to Document
- Implementation details that change frequently (they'll go stale)
- Things the type system already expresses (parameter types, return types)
- Obvious code: don't write ""// increment counter"" above counter++
- Removed features — delete the docs when you delete the code

## Maintenance
- Treat stale docs as bugs — wrong docs are worse than no docs
- Review docs in PR review — if code changed, did the docs keep up?
- Date-stamp guides that reference specific versions");

    private static readonly ConventionTemplate ActionPlan = new(
        Key: "action-plan",
        Name: "Planning & Scoping",
        Description: "Conventions for task planning: decompose, define done, sequence by dependency",
        Conventions: @"# Planning Conventions

## Decomposition
- Break work into tasks that can be completed and verified independently
- Each task should be achievable in one focused session (< 4 hours)
- Define acceptance criteria for each task: what does ""done"" look like?
- Identify unknowns and spikes upfront — research before estimating

## Sequencing
- Order by dependency: foundation first, features on top
- Identify the critical path: what blocks everything else?
- Front-load risky/uncertain work — fail fast on unknowns
- Separate ""must have"" from ""nice to have"" explicitly

## Scoping
- Define what is NOT in scope as clearly as what IS in scope
- Don't gold-plate: the minimum viable solution that meets criteria wins
- Account for testing, documentation, and deployment — not just coding
- Include rollback/revert plan for risky changes

## Communication
- State assumptions explicitly — don't assume the reader shares your context
- Estimate in ranges, not points (""2-4 hours"" not ""3 hours"")
- Flag dependencies on other teams/systems early
- Update the plan when reality diverges — plans are living documents");

    // ────────────────────────────────────────────────────────────────────
    // Role-triggered conventions — fire when the agent's role matches.
    // ────────────────────────────────────────────────────────────────────

    private static readonly ConventionTemplate RoleSecurityReviewer = new(
        Key: "role-security-reviewer",
        Name: "Security Review",
        Description: "OWASP-aligned security review: injection, auth, secrets, access control, data protection",
        Conventions: @"# Security Review Conventions

## Injection Prevention
- Verify all user input is parameterized in SQL queries (no string concatenation)
- Check for command injection in shell/exec calls — use allowlists, not blocklists
- Verify template engines auto-escape output (XSS prevention)
- Check for SSRF: validate/allowlist URLs before server-side fetching
- Look for path traversal in file operations (../../../etc/passwd)

## Authentication & Authorization
- Verify auth checks on every endpoint — not just the happy path
- Check for IDOR: is the user authorized for THIS specific resource?
- Verify password/token comparison uses constant-time comparison
- Check session/token expiry and rotation
- Look for privilege escalation: can a regular user access admin endpoints?

## Secrets Management
- No hardcoded secrets, API keys, or credentials in source code
- Verify secrets are loaded from env vars or secret managers
- Check that secrets are excluded from logs, error messages, and stack traces
- Verify .env files are in .gitignore
- Check for secrets in CI/CD config files, Docker images, or client bundles

## Data Protection
- Verify PII is not logged or exposed in error responses
- Check encryption at rest for sensitive fields (passwords → bcrypt/argon2)
- Verify TLS for all external communications
- Check for mass assignment vulnerabilities (accepting arbitrary fields from user input)
- Verify proper CORS configuration (not wildcard * in production)

## Supply Chain
- Check for known vulnerabilities in dependencies (npm audit, cargo audit)
- Verify dependency lockfiles are committed and up to date
- Look for suspicious post-install scripts in new dependencies");

    private static readonly ConventionTemplate RoleArchitect = new(
        Key: "role-architect",
        Name: "Architect",
        Description: "Architecture conventions: boundaries, contracts, trade-offs, operational readiness",
        Conventions: @"# Architect Conventions

## System Boundaries
- Define clear ownership: which team/service owns which data?
- APIs are contracts — version them, document them, don't break them
- Services communicate via well-defined interfaces (REST, gRPC, events)
- Data flows in one direction through the pipeline — no circular dependencies

## Decision Making
- Every significant decision gets an ADR: Context → Decision → Consequences
- Evaluate at least two alternatives before choosing — document why others were rejected
- Consider: what's the cost of changing this decision later? (reversibility)
- Separate ""decisions we must get right now"" from ""decisions we can defer""

## Operational Readiness
- Every service must have: health check endpoint, structured logging, graceful shutdown
- Define SLOs before launch: availability target, latency p99, error budget
- Plan for failure: what happens when this dependency is down for 30 minutes?
- Capacity planning: what's the growth rate? When do we hit limits?

## Scalability Patterns
- Stateless services scale horizontally; push state to managed stores
- Cache aggressively but invalidate correctly (prefer TTL over manual invalidation)
- Use queues/events to decouple producers from consumers
- Partition/shard early if data volume is predictable to grow

## Anti-Patterns
- Distributed monolith: everything deploys together despite being ""microservices""
- Shared database between services: couples everything through the data layer
- Chatty services: 50 network calls to serve one user request
- Schemaless everything: ""flexibility"" that becomes chaos at scale");

    private static readonly ConventionTemplate RoleQaEngineer = new(
        Key: "role-qa-engineer",
        Name: "QA Engineer",
        Description: "QA conventions: coverage gaps, boundary testing, regression suites, test independence",
        Conventions: @"# QA Engineer Conventions

## Coverage Strategy
- Prioritize testing by risk: what breaks costs the most? Test that first
- Cover all public API paths: success, validation errors, auth errors, server errors
- Test state transitions: create → update → delete lifecycle
- Verify error messages are helpful and don't leak internal details

## Boundary & Edge Cases
- Empty/null/undefined inputs at every entry point
- Maximum length strings, integer overflow, special characters (unicode, emoji, null bytes)
- Concurrent access: two users editing the same resource simultaneously
- Clock/timezone sensitivity: DST transitions, UTC vs local, midnight boundaries

## Regression Testing
- Every bug fix must include a regression test that fails without the fix
- Regression suite runs on every PR — no exceptions
- Flaky tests are bugs: fix immediately or quarantine (never just re-run)
- Monitor test execution time — slow tests get skipped, defeating their purpose

## Test Data
- Tests create their own data — never depend on pre-existing database state
- Use factories/builders for test data — not raw object literals everywhere
- Clean up after tests (or use transactions that roll back)
- Test with realistic data volumes when performance matters

## Integration Testing
- Test the real integration points: database queries, external API contracts, message formats
- Use contract tests for service-to-service boundaries
- Test deployment artifacts (Docker images, compiled binaries) not just source code
- Verify health checks, graceful shutdown, and startup behavior");

    private static readonly ConventionTemplate RoleDevopsEngineer = new(
        Key: "role-devops-engineer",
        Name: "DevOps Engineer",
        Description: "DevOps conventions: idempotent deploys, rollback plans, observability, security hardening",
        Conventions: @"# DevOps Engineer Conventions

## Deployment
- Deployments must be idempotent — running the same deploy twice produces the same result
- Every deploy has a rollback plan: what command/action reverts to the previous state?
- Zero-downtime deploys: rolling updates, blue-green, or canary — never full-stop deploys
- Database migrations run BEFORE app deploy; they must be backward-compatible with N-1 app version
- No manual steps in deploy: if a human has to remember something, automate it

## Infrastructure as Code
- All infrastructure is defined in code (Terraform, Pulumi, CloudFormation) — no click-ops
- Infrastructure changes go through PR review like application code
- Environments are reproducible: destroy and recreate from code should work
- Secrets are never in IaC source — reference secret managers by ID/path

## Observability
- Every service emits: structured logs (JSON), metrics (counters/gauges/histograms), traces
- Correlation IDs propagate across all service boundaries
- Alerts fire on symptoms (SLO breach), not causes (CPU > 80%)
- Dashboards answer: ""Is the system healthy? If not, where is it broken?""

## Security Hardening
- Least privilege for all service accounts and IAM roles
- Network segmentation: services only reach what they need
- Secrets rotate on a schedule; compromised secrets rotate immediately
- Container images scanned for vulnerabilities; base images updated monthly
- No SSH to production; use exec/debug containers with audit trail

## Reliability
- Health checks: liveness (process alive), readiness (accepting traffic), startup (warm-up done)
- Graceful shutdown: drain connections, finish in-flight requests, then exit
- Circuit breakers on all external dependencies
- Capacity headroom: scale trigger at 60-70%, not 90%");

    private static readonly ConventionTemplate RoleTechLead = new(
        Key: "role-tech-lead",
        Name: "Tech Lead",
        Description: "Tech lead conventions: team consistency, decision documentation, unblocking, standards enforcement",
        Conventions: @"# Tech Lead Conventions

## Standards & Consistency
- Consistency across the codebase is more important than individual perfection
- Document conventions that are not enforceable by linters (architecture patterns, naming schemes)
- When two approaches are equally valid, pick one and enforce it — don't allow both
- New patterns require a migration plan for existing code (or accept the inconsistency explicitly)

## Decision Making
- Decisions are documented (ADRs): what was decided, why, what alternatives were considered
- Distinguish between reversible and irreversible decisions — spend time proportionally
- Default to the boring solution unless the interesting one has measurable advantage
- ""We'll fix it later"" must have a ticket — otherwise it's ""we'll never fix it""

## Code Health
- Technical debt is tracked and paid down regularly — not just accumulated
- Every PR should leave the code slightly better than it found it (boy scout rule)
- Enforce: no warnings in CI, no skipped tests, no TODO without ticket
- Major refactors get their own focused PR — don't mix with feature work

## Team Enablement
- Unblock others before starting your own work
- Review PRs within 4 hours — blocked PRs are the #1 velocity killer
- Pair on complex problems — two people arrive at better solutions faster
- Share context: why was this decision made? What's the history?

## Quality Gates
- CI must pass before merge — no exceptions, no ""I'll fix it in the next commit""
- Breaking changes require: migration guide, deprecation period, or feature flag
- Performance-sensitive changes require benchmarks (before vs after)
- Security-sensitive changes require security review");

    // ────────────────────────────────────────────────────────────────────
    // Cross-cutting conventions — broad keywords or always_apply.
    // ────────────────────────────────────────────────────────────────────

    private static readonly ConventionTemplate UniversalSafety = new(
        Key: "universal-safety",
        Name: "Universal Safety Rules",
        Description: "Always-on safety conventions: no secrets in code, input validation, output sanitization",
        Conventions: @"# Universal Safety Rules

## Secrets
- NEVER hardcode secrets, API keys, tokens, or passwords in source code
- NEVER log secrets, even at DEBUG level
- NEVER include secrets in error messages, stack traces, or user-facing responses
- Load secrets from environment variables or dedicated secret managers
- Verify .env, credentials.json, and key files are in .gitignore

## Input Validation
- Validate and sanitize ALL external input at system boundaries
- Use allowlists over blocklists (define what's valid, reject everything else)
- Parameterize all database queries — never concatenate user input into SQL
- Escape output for the target context (HTML, shell, SQL, regex)

## Dangerous Operations
- Never use eval(), exec(), or similar dynamic code execution with untrusted input
- Never construct shell commands from user input without proper escaping
- Never deserialize untrusted data without schema validation
- Never follow redirects to user-controlled URLs without validation (SSRF)

## Data Protection
- Hash passwords with bcrypt, scrypt, or argon2 — never MD5/SHA for passwords
- Use constant-time comparison for secrets and tokens
- Encrypt sensitive data at rest; use TLS for data in transit
- Minimize PII collection and retention — don't store what you don't need");

    private static readonly ConventionTemplate UniversalQuality = new(
        Key: "universal-quality",
        Name: "Universal Quality Standards",
        Description: "Always-on quality conventions: type-check, test, don't break APIs",
        Conventions: @"# Universal Quality Standards

## Before Committing
- Code compiles / type-checks without errors or new warnings
- All existing tests pass (don't commit with known test failures)
- New logic has corresponding tests (at minimum: happy path + one error case)
- No debugging artifacts left behind (console.log, TODO hacks, commented-out code)

## API Stability
- Public APIs are contracts: don't change signatures without versioning/deprecation
- Additive changes (new fields, new endpoints) are safe
- Removal or rename of existing fields/endpoints is a breaking change
- Breaking changes require: version bump, migration guide, deprecation period

## Dependencies
- Pin dependency versions (lockfile committed)
- Audit dependencies for known vulnerabilities before adding
- Prefer well-maintained packages with active communities
- One dependency per concern — don't add a kitchen-sink library for one utility

## Performance Awareness
- Don't introduce O(n²) or worse when O(n) exists
- Database queries in loops are almost always wrong (batch them)
- Don't allocate unbounded memory based on user input (pagination, limits)
- Measure before optimizing — profiler data beats intuition");

    private static readonly ConventionTemplate GitConventions = new(
        Key: "git-conventions",
        Name: "Git & PR Conventions",
        Description: "Commit messages, branch naming, PR discipline, atomic commits",
        Conventions: @"# Git & PR Conventions

## Commits
- Conventional commit format: type(scope): description (e.g. feat(auth): add JWT refresh)
- Types: feat, fix, refactor, test, docs, chore, perf, ci, build
- Subject line: imperative mood, < 72 chars, no period at end
- Body: explain WHY (motivation), not WHAT (the diff shows what)
- One logical change per commit — don't mix features with refactoring

## Branches
- Branch from main/develop; keep branches short-lived (< 3 days ideal)
- Naming: type/short-description (e.g. feat/user-auth, fix/null-pointer-crash)
- Rebase on target before merge to resolve conflicts in your branch
- Delete branch after merge — don't accumulate stale branches

## Pull Requests
- PR title: same format as commit subject (type(scope): description)
- PR description: what changed, why, how to test, any risks/considerations
- Keep PRs small (< 400 lines of logic change) — large PRs get rubber-stamped
- One concern per PR: don't mix feature + refactor + dependency update
- Mark draft until ready for review; don't push broken code for review

## Code Review Flow
- Address all comments before merging (resolve or explain why you disagree)
- Don't force-push after review started — add new commits instead
- Squash-merge to main for clean history (or rebase if commits are meaningful)
- CI must pass before merge — no exceptions");

    private static readonly ConventionTemplate ErrorHandling = new(
        Key: "error-handling",
        Name: "Error Handling & Resilience",
        Description: "Structured errors, retry patterns, circuit breakers, graceful degradation",
        Conventions: @"# Error Handling & Resilience Conventions

## Error Design
- Use structured error types with: code, message, context, retryable flag
- Errors must be actionable: tell the caller what went wrong and what they can do
- Internal errors: log full context (stack trace, input data, system state)
- External errors: return safe message without internal details (no stack traces to users)
- Distinguish: client errors (400s — bad input) from server errors (500s — our fault)

## Error Propagation
- Don't swallow exceptions silently — log at minimum, re-throw or return error
- Wrap errors with context as they bubble up: original error + where it happened
- Don't catch generic Exception unless you're at the top-level error boundary
- Let unrecoverable errors crash — don't mask them with fallback logic

## Retry & Backoff
- Retry only on transient failures (network timeout, 503, connection reset)
- Don't retry on: 400 (bad input won't fix itself), 401/403 (auth won't magically work)
- Exponential backoff with jitter: base * 2^attempt + random(0, base)
- Max attempts (3-5) and max delay (30s-60s) — don't retry forever
- Circuit breaker for cascading failures: open after N failures, half-open to probe

## Graceful Degradation
- Prefer partial results over total failure (serve cached data if fresh fetch fails)
- Timeouts on every external call — never wait forever
- Bulkheads: isolate failures so one broken dependency doesn't take down everything
- Health endpoints must report downstream dependency status");

    private static readonly ConventionTemplate ApiDesign = new(
        Key: "api-design",
        Name: "API Design",
        Description: "REST/GraphQL conventions: naming, status codes, versioning, pagination, errors",
        Conventions: @"# API Design Conventions

## URL Structure
- Nouns for resources, verbs for actions: /api/users (not /api/getUsers)
- Plural resource names: /api/issues, /api/comments
- Nesting for ownership: /api/repos/:id/issues (issues belong to repo)
- Max 2 levels of nesting; deeper relationships use query params or links

## HTTP Methods & Status Codes
- GET: read (200), POST: create (201), PUT/PATCH: update (200), DELETE (204)
- 400: client sent invalid input; 401: not authenticated; 403: not authorized
- 404: resource doesn't exist; 409: conflict (duplicate, version mismatch)
- 500: server error (our fault — include correlation ID for debugging)

## Request/Response
- Consistent envelope: { data, error, meta } or direct body with typed errors
- Pagination: cursor-based for large/mutable lists; offset for small/static
- Filtering: query params (GET /api/users?role=admin&active=true)
- Partial responses: fields query param or GraphQL field selection

## Versioning & Compatibility
- Version in URL path (/api/v1/) or header (Accept: application/vnd.app.v1+json)
- Additive changes (new fields, new endpoints) are non-breaking
- Removal/rename/type-change of existing fields IS breaking → new version
- Deprecation headers before removal; sunset period of at least one release cycle

## Error Format
- Consistent error shape: { error: { code, message, details? } }
- Machine-readable code for programmatic handling
- Human-readable message for developer debugging
- Correlation ID in every error response for log tracing");

    private static readonly ConventionTemplate DatabaseConventions = new(
        Key: "database-conventions",
        Name: "Database Conventions",
        Description: "Schema design, migrations, query patterns, indexing, transactions",
        Conventions: @"# Database Conventions

## Schema Design
- Every table has a primary key (prefer UUID or ULID for distributed systems)
- Use created_at and updated_at timestamps on every mutable table
- Foreign keys with appropriate ON DELETE behavior (CASCADE, SET NULL, RESTRICT)
- Column naming: snake_case, singular (user_id not users_id)
- Avoid nullable columns unless NULL has distinct semantic meaning from empty/default

## Migrations
- Migrations are forward-only and idempotent (IF NOT EXISTS, ON CONFLICT DO NOTHING)
- One concern per migration file — don't mix schema change with data migration
- Test migration against a copy of production data (size, edge cases)
- Backward-compatible migrations: new columns are nullable or have defaults
- Never drop columns in the same deploy that stops writing them — separate deploys

## Query Patterns
- Use indexes for all WHERE, JOIN, and ORDER BY columns in frequent queries
- Avoid SELECT * — specify columns explicitly
- Batch operations instead of loops (INSERT INTO ... VALUES (row1), (row2), ...)
- Use EXPLAIN ANALYZE to verify query plans before shipping
- N+1 queries are bugs: use JOINs, subqueries, or batch fetching

## Transactions
- Wrap multi-step mutations in transactions — partial success is data corruption
- Keep transactions short — don't hold locks while calling external services
- Use optimistic concurrency (version column) for low-contention updates
- Use advisory locks or SELECT FOR UPDATE for high-contention resources

## Safety
- Never execute raw user input as SQL — always parameterize
- Limit result sets (LIMIT/OFFSET or cursor) — never return unbounded rows
- Audit log for sensitive data changes (who changed what, when)
- Backup verification: regularly test restoring from backups");

    private static readonly ConventionTemplate Observability = new(
        Key: "observability",
        Name: "Observability & Monitoring",
        Description: "Structured logging, distributed tracing, metrics, alerting conventions",
        Conventions: @"# Observability Conventions

## Logging
- Structured logs (JSON): { level, timestamp, service, correlationId, message, ...context }
- Log levels: DEBUG (dev detail), INFO (milestones), WARN (degraded), ERROR (failures)
- Every request gets a correlation ID; propagate it across all service calls
- NEVER log: secrets, tokens, passwords, full credit card numbers, PII without masking
- DO log: request ID, user ID, action performed, duration, error codes

## Distributed Tracing
- Every service boundary creates a span: name, duration, status, attributes
- Propagate trace context (W3C traceparent header) across HTTP, gRPC, message queues
- Tag spans with: service.name, operation, tenant_id, user_id, error (boolean)
- Trace significant internal operations (DB queries, cache lookups, external API calls)

## Metrics
- Four golden signals: latency, traffic, errors, saturation
- Use histograms for latency (not averages — p50, p95, p99 matter)
- Counter for events (requests served, errors encountered, jobs processed)
- Gauge for current state (queue depth, connection pool size, active users)
- Label dimensions: endpoint, method, status_code, tenant (cardinality-aware)

## Alerting
- Alert on symptoms (SLO breach: error rate > 1%, p99 > 2s), not causes (CPU > 80%)
- Every alert must have a runbook: what to check, how to mitigate, who to escalate to
- Severity levels: page (immediate action), ticket (next business day), log (informational)
- Alert fatigue is worse than missing alerts — tune aggressively, deduplicate");

    // ────────────────────────────────────────────────────────────────────
    // Aggregates
    // ───��────────────────────────────────────────────────────────────────

    /// <summary>
    /// All shipped templates, in a stable listing order (matches the TS
    /// source insertion order from the deleted packages/api file).
    /// </summary>
    public static IReadOnlyList<ConventionTemplate> All { get; } = new ConventionTemplate[]
    {
        // Language/framework (20)
        TypescriptNode,
        TypescriptReact,
        TypescriptReactNative,
        Python,
        PythonDjango,
        PythonFastApi,
        Go,
        Rust,
        Java,
        Kotlin,
        Csharp,
        Swift,
        SwiftUikit,
        DartFlutter,
        C,
        Cpp,
        RubyRails,
        PhpLaravel,
        ElixirPhoenix,
        Scala,

        // Action-triggered (8)
        ActionWriteCode,
        ActionReviewCode,
        ActionDesign,
        ActionWriteTests,
        ActionDebug,
        ActionRefactor,
        ActionDocument,
        ActionPlan,

        // Role-triggered (5)
        RoleSecurityReviewer,
        RoleArchitect,
        RoleQaEngineer,
        RoleDevopsEngineer,
        RoleTechLead,

        // Cross-cutting (7)
        UniversalSafety,
        UniversalQuality,
        GitConventions,
        ErrorHandling,
        ApiDesign,
        DatabaseConventions,
        Observability,
    };

    private static readonly IReadOnlyDictionary<string, ConventionTemplate> ByKey =
        All.ToDictionary(t => t.Key, StringComparer.Ordinal);

    /// <summary>
    /// Looks up a template by key, returning <c>null</c> if none exists.
    /// </summary>
    public static ConventionTemplate? GetByKey(string key)
        => ByKey.TryGetValue(key, out var template) ? template : null;
}
