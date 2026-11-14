# CodeForge

CodeForge is a repository analysis service for .NET and JavaScript/TypeScript
projects. Submit a public GitHub repository and CodeForge will clone it, build
applicable .NET projects, collect structured engineering signals, and present
the results in a web report and downloadable PDF.

The goal is not only to answer whether a repository builds. CodeForge brings
compiler diagnostics, tests, coverage, complexity, and dependency structure
together so developers can understand what needs attention and why.

## Features

- **Repository build analysis** using the repository's existing .NET build and
  analyzer configuration.
- **Structured compiler and Roslyn diagnostics** with severity, rule ID,
  source location, diagnostic message, and official documentation links when
  available.
- **Trusted .NET test execution** with passed, failed, skipped, and executed
  counts parsed from TRX reports.
- **Test coverage** with line and branch counts, per-file uncovered lines, and
  correlations between uncovered code and complex functions.
- **Code complexity analysis** for C#, JavaScript, TypeScript, JSX, and TSX
  using Lizard.
- **JavaScript and TypeScript dependency analysis** using Madge, including
  circular dependencies and a Graphviz dependency diagram.
- **.NET project dependency diagrams** with explicit, configurable forbidden
  project-reference rules.
- **Analysis lifecycle reporting** that distinguishes queued, running,
  finalizing, completed, completed-with-errors, build-failed, and
  infrastructure-failed outcomes.
- **Persistent analysis artifacts** stored in Azure Blob Storage and reused by
  the Analysis page and PDF report.
- **Reproducibility metadata**, including the analyzed commit SHA, worker
  version, .NET SDK, and build configuration.

## Analysis report

The Analysis page starts with a concise repository-health overview:

- Build status
- Test status and counts
- Line coverage
- High-complexity functions
- JavaScript/TypeScript dependency cycles
- Architecture-rule violations

Detailed evidence is organized into quality and architecture sections. A
failed optional analyzer does not discard successful results from other stages.
The final report clearly identifies failed, incomplete, skipped, unsupported,
and not-applicable analysis stages.

PDF download remains disabled until the final persisted report is ready.

## How it works

```text
Browser
   |
   v
CodeForge API ---- PostgreSQL
   |                  ^
   | outbox message   |
   v                  |
Azure Service Bus     |
   |                  |
   v                  |
CodeForge Worker -----+
   |
   +-- clone repository
   +-- run static analyzers
   +-- discover .NET projects
   +-- build and collect diagnostics
   +-- optionally run trusted tests and coverage
   +-- render dependency diagrams
   +-- save report data and Blob artifacts
```

The API creates a job and an outbox record in the same database transaction.
The outbox publisher sends the job ID to Azure Service Bus. A worker processes
the repository, persists structured results, and marks the report ready only
after final persistence succeeds.

## Project structure

```text
src/
  CodeForge.Api/             HTTP API, Analysis UI, and PDF generation
  CodeForge.Infrastructure/  EF Core models, migrations, messaging, and Blob storage
  CodeForge.Worker/          Repository processing and analysis pipeline
tests/
  CodeForge.Tests/           Lifecycle and reporting tests
Dockerfile.api
Dockerfile.worker
compose.yaml
```

## Requirements

- .NET 10 SDK
- Docker with Docker Compose
- Azure Database for PostgreSQL
- Azure Service Bus with a session-enabled queue
- Azure Blob Storage
- Azure identities or credentials with the required data-plane roles

The worker image includes pinned analysis dependencies:

- Lizard `1.17.31`
- Madge `8.0.0`
- Graphviz
- Python and Node.js/npm

## Configuration

Create the local environment file:

```bash
cp .env.example .env
```

Configure the following values:

| Variable | Used by | Purpose |
|---|---|---|
| `DB_HOST` | API, worker, migration | PostgreSQL hostname |
| `DB_PORT` | API, worker, migration | PostgreSQL port, normally `5432` |
| `DB_NAME` | API, worker, migration | Database name |
| `DB_USER` | API, worker, migration | Database user |
| `PGPASSWORD` | API, worker, migration | Database password |
| `QUEUE_CONNECTION_STRING_SEND` | API/outbox | Service Bus send connection |
| `QUEUE_CONNECTION_STRING_LISTEN` | Worker | Service Bus receive connection |
| `QUEUE_NAME` | API, worker | Session-enabled queue name |
| `BLOB_SERVICE_URI` | API, worker | Blob service URI |
| `BLOB_CONTAINER_NAME` | API, worker | Report and artifact container |
| `DOTNET_TEST_EXECUTION_ENABLED` | Worker | Enables trusted test execution; defaults to `false` |

Do not load `.env` with Bash `source` when values contain shell-sensitive
characters. Docker Compose parses this file correctly.

## Run locally

Build and start the complete local stack:

```bash
docker compose up --build
```

Open <http://localhost:8080>.

Stop the stack with:

```bash
docker compose down
```

To run multiple workers:

```bash
docker compose up --detach --scale worker=2
```

## Database migrations

Apply pending migrations through the migration service so the same environment
parsing and runtime image are used:

```bash
docker compose run --rm --build migrate
```

Running the command again is safe; EF Core reports when the database is already
up to date.

## Trusted test execution

Submitted test assemblies execute repository code. Test execution is therefore
disabled by default and requires both:

1. `DOTNET_TEST_EXECUTION_ENABLED=true` on the worker.
2. Explicit trusted-repository opt-in when the job is submitted.

Do not enable tests for arbitrary public repositories unless each job runs in
an isolated, disposable environment without access to cloud credentials,
database credentials, queue credentials, or the host.

Initial test support targets VSTest-compatible projects using
`Microsoft.NET.Test.Sdk`. When a supported `coverlet.collector` configuration is
available, CodeForge collects Cobertura coverage during the same test run.

## Static analysis

### Lizard complexity

Lizard analyzes C#, JavaScript, TypeScript, JSX, and TSX. CodeForge reports:

- Number of functions analyzed
- Functions over the cyclomatic-complexity threshold of 15
- The five highest-complexity functions
- Repository-relative source paths and line ranges

Generated files, dependencies, and common build-output folders are excluded.
Lizard produces metrics rather than a dependency diagram.

### Madge dependencies

Madge discovers JavaScript and TypeScript imports and reports:

- Modules and dependency edges
- Circular dependencies
- Unresolved-import limitations
- A PNG dependency graph rendered with Graphviz

CodeForge does not run submitted npm scripts or intentionally load executable
project configuration.

### .NET build findings

The worker collects compiler and repository-configured Roslyn analyzer
diagnostics as structured SARIF where supported. CodeForge does not enable an
additional analyzer ruleset. Findings from unsuccessful builds are retained
when available.

## .NET architecture rules

CodeForge discovers project references and draws arrows from each referencing
project to the project it depends on. Repositories can declare forbidden direct
dependencies in `.codeforge/architecture.json`:

```json
{
  "version": 1,
  "forbiddenDependencies": [
    {
      "id": "domain-no-infrastructure",
      "source": "src/Domain/Domain.csproj",
      "target": "src/Infrastructure/Infrastructure.csproj",
      "description": "Domain must not reference Infrastructure."
    }
  ]
}
```

Project paths must exactly match repository-relative `.csproj` paths. CodeForge
does not infer architecture intent from project or folder names. When no rules
are configured, it still produces the project graph and explicitly reports that
rule validation was skipped.

## Azure permissions

Use narrowly scoped data-plane roles:

- **Worker identity:** `Storage Blob Data Contributor` for uploading analysis
  artifacts to the report container.
- **API identity:** `Storage Blob Data Contributor` for serving artifacts and
  saving generated PDFs.
- **Container Apps queue scaler identity:** `Azure Service Bus Data Receiver`
  when the KEDA Service Bus rule uses the worker's managed identity.

The current application clients use the separately scoped Service Bus send and
listen connection strings described in the configuration table.

The worker owns repository workspaces and therefore uploads dependency graph
artifacts. The API reads those artifacts and generates the final PDF.

## Security considerations

Repository builds and tests can execute untrusted code. For an internet-facing
deployment:

- Use disposable per-job sandboxes.
- Restrict outbound network access.
- Do not expose the Docker socket or host directories.
- Do not provide arbitrary repositories with cloud or database credentials.
- Keep trusted test execution disabled unless the execution boundary is safe.
- Apply CPU, memory, process, and execution-time limits.

## Roadmap

Planned areas for future development include:

- **More language support** — add build, test, coverage, complexity, and
  dependency analysis for languages beyond .NET and JavaScript/TypeScript.
- **Vulnerability analysis** — detect vulnerable application dependencies,
  insecure package versions, exposed secrets, and relevant security findings.
- **More software-quality checks** — expand maintainability, reliability,
  duplication, style, architecture, and code-smell analysis.
- **Review priorities** — combine findings into an actionable “fix this first”
  list with supporting evidence.
- **Historical comparisons** — show newly introduced and resolved findings,
  test changes, coverage movement, and complexity trends between analyses.
- **Git context** — include analyzed branch or tag, commit author, commit time,
  and direct links to affected GitHub files.
- **Additional execution isolation** — move arbitrary repository processing
  into stronger disposable sandboxes.

## Development checks

Run the focused test suite:

```bash
dotnet test tests/CodeForge.Tests/CodeForge.Tests.csproj
```

Build the complete solution:

```bash
dotnet build CodeForge.slnx
```

Build the worker image and verify its installed tools:

```bash
docker build -f Dockerfile.worker -t codeforge-worker:local .
docker run --rm --entrypoint lizard codeforge-worker:local --version
docker run --rm --entrypoint madge codeforge-worker:local --version
docker run --rm --entrypoint dot codeforge-worker:local -V
```

## License

No license file is currently included. Add a license before distributing or
accepting external contributions.
