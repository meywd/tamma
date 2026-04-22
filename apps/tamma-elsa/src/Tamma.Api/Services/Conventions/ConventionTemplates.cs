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
    // Aggregates
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// All shipped templates, in a stable listing order (matches the TS
    /// source insertion order from the deleted packages/api file).
    /// </summary>
    public static IReadOnlyList<ConventionTemplate> All { get; } = new ConventionTemplate[]
    {
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
    };

    private static readonly IReadOnlyDictionary<string, ConventionTemplate> ByKey =
        All.ToDictionary(t => t.Key, StringComparer.Ordinal);

    /// <summary>
    /// Looks up a template by key, returning <c>null</c> if none exists.
    /// </summary>
    public static ConventionTemplate? GetByKey(string key)
        => ByKey.TryGetValue(key, out var template) ? template : null;
}
