# ZeroMcp.Relay — Product Specification

> **ZeroMcp for your own APIs. ZeroMcp.Relay for everything else.**

**This is an extension project to live alongside https://github/ZeroMcp/ZeroMcp.net** so should follow the same naming conventions, patterns, and documentation standards.


---

## Table of Contents

1. [Product Identity](#1-product-identity)
2. [Core Concepts](#2-core-concepts)
3. [Installation](#3-installation)
4. [CLI Reference](#4-cli-reference)
5. [Configuration Schema](#5-configuration-schema)
6. [Authentication Models](#6-authentication-models)
7. [HTTP Server Mode](#7-http-server-mode)
8. [Config UI (--enable-ui)](#8-config-ui---enable-ui)
9. [stdio Mode](#9-stdio-mode)
10. [OpenAPI Ingestion](#10-openapi-ingestion)
11. [Tool Generation](#11-tool-generation)
12. [Request Construction and Dispatch](#12-request-construction-and-dispatch)
13. [Multi-API Tool Namespacing](#13-multi-api-tool-namespacing)
14. [Resilience and Timeouts](#14-resilience-and-timeouts)
15. [Observability](#15-observability)
16. [Relationship to ZeroMcp Core](#16-relationship-to-zeromcp-core)
17. [Repository and Package Structure](#17-repository-and-package-structure)
18. [Deployment Patterns](#18-deployment-patterns)
19. [Roadmap and Future Considerations](#19-roadmap-and-future-considerations)
20. [Acceptance Criteria](#20-acceptance-criteria)

---

## 1. Product Identity

### What It Is

ZeroMcp.Relay is a standalone `dotnet tool` that turns any OpenAPI/Swagger specification into a fully functional MCP server. Point it at one or more OpenAPI spec URLs, configure authentication, and immediately expose every documented endpoint as an MCP tool — with no code, no compilation, and no framework knowledge required.

### What It Is Not

ZeroMcp.Relay is not a replacement for ZeroMcp core. It does not perform in-process dispatch, does not run inside your ASP.NET Core pipeline, and does not give tool calls access to your DI container or middleware. It is an outbound HTTP relay — it receives tool calls from an LLM and forwards them as real HTTP requests to external APIs.

### The One-Line Pitch

**Turn any OpenAPI spec into an MCP server with a single command.**

### The Family Relationship

```
ZeroMcp          ← your own ASP.NET Core APIs
                   in-process dispatch, zero duplication
                   installed as a NuGet package

ZeroMcp.Relay    ← any API with an OpenAPI spec
                   outbound HTTP relay, zero code
                   installed as a dotnet tool
```

Both expose tools through the same MCP protocol. Both can be targeted by the same MCP clients. They are complementary, not competing.

---

## 2. Core Concepts

### API

A configured external service. Has a name, an OpenAPI spec URL, an authentication strategy, and optional settings (timeout, prefix, enabled/disabled). Stored in the config file.

### Tool

An MCP tool generated from a single OpenAPI operation. The operation's `operationId` becomes the tool name (with optional namespace prefix). The operation's `summary` becomes the tool description. Parameters and request body are merged into a flat JSON Schema object — the same pattern ZeroMcp core uses.

### Config File

A JSON file (`relay.config.json`) that is the source of truth for all configured APIs and settings. The CLI edits it. The UI edits it. The server reads it at startup. It is designed to be committed to source control, with secrets stored as environment variable references rather than literal values.

### Run Modes

ZeroMcp.Relay has three run modes:

- **HTTP server** — binds a port, exposes `/mcp` endpoint, optionally exposes config UI when `--enable-ui` is passed
- **stdio** — reads JSON-RPC from stdin, writes to stdout; for Claude Desktop, Claude Code, and subprocess clients
- **Validate** — reads config and specs, reports errors, exits; for CI pipelines

---

## 3. Installation

```bash
# Install globally
dotnet tool install -g ZeroMcp.Relay

# Install locally (project-scoped)
dotnet tool install ZeroMcp.Relay

# Update
dotnet tool update -g ZeroMcp.Relay

# Verify
mcprelay --version
```

The tool name is `mcprelay`. The package name is `ZeroMcp.Relay`. This mirrors the ZeroMcp convention of a clean NuGet name and a short CLI name.

---

## 4. CLI Reference

### Top-Level Commands

```
mcprelay configure    Manage configured APIs
mcprelay run          Start the relay server
mcprelay tools        Inspect generated tools
mcprelay validate     Validate config and specs without starting
mcprelay --version    Print version
mcprelay --help       Print help
```

### `mcprelay configure`

```
mcprelay configure init
```
Scaffolds a `relay.config.json` in the current directory with commented examples. Safe to run in a project repo.

---

```
mcprelay configure add
  -n, --name         <string>   Required. Short identifier for this API (e.g. "stripe")
  -s, --source       <url>      Required. URL of the OpenAPI/Swagger JSON or YAML spec
  -k, --api-key      <value>    API key value or env var reference (e.g. env:STRIPE_KEY)
  -h, --header       <k=v>      Custom header to send with every request (repeatable)
  -u, --username     <string>   Basic auth username
  -p, --password     <value>    Basic auth password or env var reference
  -b, --bearer       <value>    Bearer token or env var reference
      --prefix       <string>   Tool name prefix override (default: name)
      --timeout      <seconds>  Per-request timeout in seconds (default: 30)
      --disabled                Add the API but disable it immediately
      --config       <path>     Config file path (default: relay.config.json,
                                then ~/.mcprelay/config.json)
```

Examples:

```bash
mcprelay configure add -n stripe \
  -s https://raw.githubusercontent.com/stripe/openapi/master/openapi/spec3.json \
  -b env:STRIPE_SECRET_KEY

mcprelay configure add -n crm \
  -s https://internal-crm.company.com/swagger/v1/swagger.json \
  -h "X-Tenant-Id=acme" \
  -k env:CRM_API_KEY

mcprelay configure add -n github \
  -s https://github.com/github/rest-api-description/raw/main/descriptions/api.github.com/api.github.com.json \
  -b env:GITHUB_TOKEN \
  --prefix gh
```

---

```
mcprelay configure remove -n <name>
```
Removes the named API from config. Prompts for confirmation unless `--yes` is passed.

---

```
mcprelay configure list
```
Lists all configured APIs with name, source URL, auth type, enabled status, and tool count.

```
NAME       SOURCE                              AUTH      ENABLED  TOOLS
stripe     https://...stripe.../spec3.json     bearer    yes      147
crm        https://internal-crm.../swagger     api-key   yes      34
github     https://...github.com/api...json    bearer    yes      512
logistics  https://...logistics.../openapi     none      no       28
```

---

```
mcprelay configure show -n <name>
```
Prints the full config entry for one API. Secrets are masked (`sk_live_****`).

---

```
mcprelay configure enable  -n <name>
mcprelay configure disable -n <name>
```
Toggles an API without removing it. Useful for temporarily suspending an integration.

---

```
mcprelay configure test -n <name>
```
Fetches the spec URL, parses it, fires a real OPTIONS or GET request to the API base URL, and reports success or failure. Validates that auth credentials resolve (env vars exist, tokens are non-empty). Does not invoke any actual operations.

---

```
mcprelay configure set-secret -n <name> --bearer   <value>
mcprelay configure set-secret -n <name> --api-key  <value>
mcprelay configure set-secret -n <name> --password <value>
```
Updates only the auth credential for an existing API without touching other settings.

---

### `mcprelay run`

```
mcprelay run
  --port              <int>     HTTP server port (default: 5000)
  --host              <string>  Bind address (default: localhost)
  --stdio                       Run in stdio mode instead of HTTP server
  --enable-ui                   Enable the config UI (HTTP mode only)
  --config            <path>    Config file path
  --env               <path>    .env file to load before starting
  --validate-on-start           Fetch and parse all specs at startup,
                                exit on error (default: true)
  --lazy                        Skip spec fetching at startup;
                                fetch per-API on first tool call
```

HTTP mode examples:

```bash
# Local setup session — UI enabled
mcprelay run --enable-ui

# Production run — no UI, localhost only
mcprelay run

# Team server — bind to all interfaces, specific port
mcprelay run --host 0.0.0.0 --port 8080

# Load secrets from .env file
mcprelay run --env .env.production
```

stdio mode examples:

```bash
# For Claude Desktop / Claude Code
mcprelay run --stdio

# With a project-local config file
mcprelay run --stdio --config ~/projects/myproject/relay.config.json
```

---

### `mcprelay tools`

```
mcprelay tools list
  -n, --name     <string>   Filter to one API
  --format       table|json Output format (default: table)

mcprelay tools inspect -t <tool-name>
```
Prints the full MCP tool descriptor for a single tool — name, description, and complete input schema.

```
mcprelay tools count
```
Prints total tool count and per-API breakdown.

---

### `mcprelay validate`

```
mcprelay validate
  --config <path>    Config file to validate
  --strict           Treat spec warnings as errors
```

Fetches all spec URLs, parses them, and reports any issues — missing `operationId`, malformed schemas, unreachable base URLs, unresolvable env var references. Exits with code 0 on success, 1 on error. Designed for CI pipelines.

---

## 5. Configuration Schema

`relay.config.json`:

```json
{
  "$schema": "https://zeromcp.dev/schemas/relay.config.json",
  "serverName": "My API Relay",
  "serverVersion": "1.0.0",
  "defaultTimeout": 30,
  "apis": [
    {
      "name": "stripe",
      "source": "https://raw.githubusercontent.com/stripe/openapi/master/openapi/spec3.json",
      "baseUrl": null,
      "prefix": "stripe",
      "enabled": true,
      "timeout": 60,
      "auth": {
        "type": "bearer",
        "token": "env:STRIPE_SECRET_KEY"
      },
      "headers": {},
      "include": [],
      "exclude": ["test_helpers.*", "radar.*"]
    },
    {
      "name": "crm",
      "source": "https://internal-crm.company.com/swagger/v1/swagger.json",
      "baseUrl": "https://internal-crm.company.com",
      "prefix": "crm",
      "enabled": true,
      "timeout": 30,
      "auth": {
        "type": "apikey",
        "header": "X-Api-Key",
        "value": "env:CRM_API_KEY"
      },
      "headers": {
        "X-Tenant-Id": "acme"
      },
      "include": [],
      "exclude": []
    }
  ]
}
```

### Field Reference

| Field | Type | Default | Description |
|---|---|---|---|
| `serverName` | string | `"ZeroMcp.Relay"` | Returned in MCP `initialize` response |
| `serverVersion` | string | `"1.0.0"` | Returned in MCP `initialize` response |
| `defaultTimeout` | int | `30` | Default per-request timeout in seconds |
| `apis[].name` | string | required | Short identifier, used as default prefix |
| `apis[].source` | string | required | URL or file path of OpenAPI JSON or YAML spec |
| `apis[].baseUrl` | string? | null | Override for API base URL (uses spec `servers[0].url` if null) |
| `apis[].prefix` | string | = name | Prepended to tool names: `{prefix}_{operationId}` |
| `apis[].enabled` | bool | true | Whether this API's tools are included |
| `apis[].timeout` | int? | null | Per-API timeout override in seconds |
| `apis[].auth` | object | none | Authentication strategy (see §6) |
| `apis[].headers` | object | `{}` | Static headers sent with every request |
| `apis[].include` | string[] | `[]` | Glob patterns for operationIds to include (empty = all) |
| `apis[].exclude` | string[] | `[]` | Glob patterns for operationIds to exclude |

### Secret Resolution

Secrets are never stored as literal values. Any string value beginning with `env:` is resolved from the environment at startup:

```json
{ "token": "env:STRIPE_SECRET_KEY" }
```

If the referenced environment variable is not set, ZeroMcp.Relay logs a warning and disables the API rather than starting with invalid credentials.

The `--env <path>` flag on `mcprelay run` loads a `.env` file before secret resolution, enabling local development without polluting the shell environment.

---

## 6. Authentication Models

### None
No auth headers added. For public APIs or internal APIs with network-level auth.

```json
{ "auth": { "type": "none" } }
```

### Bearer Token
Adds `Authorization: Bearer {token}` to every request.

```json
{ "auth": { "type": "bearer", "token": "env:MY_TOKEN" } }
```

### API Key (Header)
Adds a named header to every request.

```json
{ "auth": { "type": "apikey", "header": "X-Api-Key", "value": "env:MY_KEY" } }
```

### API Key (Query Parameter)
Appends a query parameter to every request URL.

```json
{ "auth": { "type": "apikey-query", "parameter": "api_key", "value": "env:MY_KEY" } }
```

### HTTP Basic
Adds `Authorization: Basic {base64(username:password)}`.

```json
{ "auth": { "type": "basic", "username": "myuser", "password": "env:MY_PASSWORD" } }
```

### OAuth2 Client Credentials *(v2)*
Acquires a token at startup and refreshes before expiry. Not in v1, but `IAuthStrategy` is an interface, not a static switch, so the architecture accommodates it cleanly.

---

## 7. HTTP Server Mode

When started without `--stdio`, ZeroMcp.Relay runs an ASP.NET Core web server exposing:

```
POST /mcp          JSON-RPC 2.0 — initialize, tools/list, tools/call
GET  /mcp          Server info and handshake
GET  /mcp/tools    Tool list JSON (Inspector-compatible format)
GET  /health       Health check — per-API status and total tool count
```

When `--enable-ui` is passed, additionally:

```
GET  /             Redirects to /ui
GET  /ui           Config UI
GET  /ui/*         Config UI static assets
```

### MCP Endpoint Behaviour

`tools/list` returns the merged tool list across all enabled APIs. Each tool name is prefixed (`stripe_charge_create`, `crm_get_customer`).

`tools/call` identifies the target API from the tool name prefix, reconstructs the HTTP request from tool arguments, dispatches it, and returns the response.

`initialize` returns the configured `serverName` and `serverVersion` with standard MCP capabilities.

### Port and Binding

Default: `http://localhost:5000`. Exposing to `0.0.0.0` for team or server deployments is explicit and intentional — never the default.

TLS is out of scope for v1. Team deployments should sit behind a reverse proxy (nginx, Caddy, Traefik) that handles TLS termination.

---

## 8. Config UI (`--enable-ui`)

The config UI is a single-page application served by the relay when `--enable-ui` is passed at startup. It is a visual editor for `relay.config.json` and a tool browser.

### Security Model

The UI only exists when `--enable-ui` is explicitly passed. A standard `mcprelay run` exposes no UI surface whatsoever — not a 404, not a redirect, no endpoint registered at all.

This gives a clean deployment story:

| Context | Command | UI available |
|---|---|---|
| Local setup session | `mcprelay run --enable-ui` | Yes |
| Production HTTP | `mcprelay run` | No |
| CI/CD container | `mcprelay run` | No |
| Claude Desktop | `mcprelay run --stdio` | No (no HTTP server at all) |

### UI Layout

```
┌─────────────────────────────────────────────────────────────┐
│  ZeroMcp.Relay                                v1.0.0  ●     │
├──────────────────┬──────────────────────────────────────────┤
│                  │                                          │
│  APIs            │  stripe                                  │
│  ─────────────   │  ──────────────────────────────────────  │
│  ● stripe        │  Source   https://...stripe.../spec3.json│
│  ● crm           │  Auth     Bearer  sk_live_****           │
│  ○ logistics     │  Prefix   stripe                         │
│                  │  Tools    147  ✓ loaded                  │
│  + Add API       │  Timeout  60s                            │
│                  │                                          │
│                  │  [Edit]  [Test Connection]  [Disable]    │
│                  │  [Remove]                                │
│                  │                                          │
│                  │  ── Tools ─────────────────────────────  │
│                  │  🔍 Search tools...                      │
│                  │                                          │
│                  │  stripe_charge_create                    │
│                  │  Create a new charge                     │
│                  │                                          │
│                  │  stripe_charge_retrieve                  │
│                  │  Retrieves the details of a charge       │
│                  │                                          │
└──────────────────┴──────────────────────────────────────────┘
```

### UI Capabilities

**API management:**
- Add a new API — name, source URL, auth type and credentials, prefix, timeout
- Edit an existing API
- Remove an API (with confirmation dialog)
- Enable / disable an API
- Test connection — fetches spec, fires health request, validates auth resolves

**Tool browsing:**
- List all tools for the selected API
- Search and filter by name or description
- Click a tool to inspect its full input schema
- Invoke a tool with a JSON arguments editor and see the raw response

**Config visibility:**
- "View raw config" shows the current `relay.config.json` with secrets masked
- Changes written to the config file immediately on save

**Status indicators:**
- Green dot: API loaded, spec fetched, auth resolved
- Yellow dot: API disabled
- Red dot: Spec fetch failed or auth env var missing

### Add API Flow

1. Click **+ Add API**
2. Enter name and spec URL
3. Click **Fetch Spec** — ZeroMcp.Relay fetches and parses the spec, shows tool count and any warnings
4. Select auth type, enter credentials (env var references suggested alongside literal entry)
5. Optionally set prefix, timeout, include/exclude patterns
6. Click **Save** — writes to config file, API appears in the left panel immediately

This is the zero-friction onboarding path. Name, URL, key, done. No JSON editing required.

---

## 9. stdio Mode

When `--stdio` is passed:

- No HTTP server is bound
- No UI is available (`--enable-ui` is ignored with a warning if both flags are passed)
- JSON-RPC messages are read from stdin, newline-delimited UTF-8
- Responses are written to stdout, newline-delimited
- All logging goes to stderr

### Claude Desktop Configuration

```json
{
  "mcpServers": {
    "relay": {
      "command": "mcprelay",
      "args": ["run", "--stdio"],
      "env": {
        "STRIPE_SECRET_KEY": "sk_live_...",
        "CRM_API_KEY": "..."
      }
    }
  }
}
```

With a project-local config:

```json
{
  "mcpServers": {
    "relay": {
      "command": "mcprelay",
      "args": [
        "run", "--stdio",
        "--config", "/path/to/project/relay.config.json"
      ]
    }
  }
}
```

### Startup Behaviour in stdio Mode

All enabled API specs are fetched and all secrets resolved before reading from stdin. If a spec fetch fails or a required secret is missing, ZeroMcp.Relay writes a JSON-RPC error to stdout and exits with a non-zero code — failing loudly rather than starting in a degraded state.

The `--lazy` flag defers spec fetching to the first tool call for each API, reducing startup latency at the cost of errors surfacing later and the first call being slower.

---

## 10. OpenAPI Ingestion

### Supported Formats

- OpenAPI 3.0.x JSON and YAML
- OpenAPI 3.1.x JSON and YAML
- OpenAPI 2.0 (Swagger) JSON and YAML

YAML specs are converted to JSON on load. The parsed representation is identical regardless of source format.

### Spec Sources

- HTTP/HTTPS URL (fetched at startup or lazily on first call)
- Local file path using `file://` prefix

```bash
mcprelay configure add -n local-api \
  -s file:///path/to/openapi.json \
  --bearer env:LOCAL_TOKEN
```

### Spec Caching

Specs are fetched at startup and held in memory for the lifetime of the process. ZeroMcp.Relay does not poll for spec changes while running — a restart picks up a new spec version. This is intentional: runtime tool set changes would invalidate in-progress LLM context.

A `POST /admin/reload` endpoint (HTTP mode with `--enable-ui`) or SIGHUP signal (Linux/macOS) triggers a re-fetch without a full restart.

### Spec Fetch Failures

If a spec URL is unreachable at startup, the API is marked unavailable and its tools excluded from `tools/list`. ZeroMcp.Relay continues running with the remaining APIs. The failure is reported in the health endpoint and the UI status indicator.

---

## 11. Tool Generation

### Naming

Tool names are constructed as `{prefix}_{operationId}`, lowercased with non-alphanumeric characters replaced by underscores.

```
operationId: ChargeCreate  →  stripe_charge_create
operationId: GetCustomer   →  crm_get_customer
```

If an operation has no `operationId`, a name is generated from the HTTP method and path:

```
GET  /customers/{id}  →  crm_get_customers_id
POST /orders          →  crm_post_orders
```

Auto-generated names are flagged as warnings in `mcprelay validate --strict`.

### Description

Taken from the operation's `summary`. If absent, `description` is used (truncated to 200 characters). If both are absent, the tool name is used as the description with a warning.

### Input Schema

Parameters are merged into a single flat JSON Schema object:

| OpenAPI source | MCP schema mapping |
|---|---|
| Path parameters | Required properties |
| Query parameters | Optional (or required if `required: true` in spec) |
| Header parameters | Optional properties (applied as outbound request headers) |
| `requestBody` (object) | Properties expanded inline |
| `requestBody` (non-object) | Single `body` property of the appropriate type |

`$ref` chains are fully resolved before schema generation. Circular references are detected and broken with a `{}` (any) schema at the cycle point, with a warning logged.

### Include / Exclude Filtering

`include` and `exclude` arrays accept glob patterns matched against the resolved tool name (after prefixing):

```json
{
  "include": ["stripe_charge_*", "stripe_customer_*"],
  "exclude": ["stripe_*_test_*"]
}
```

`include` takes precedence: if non-empty, only matching tools are registered. `exclude` then removes any matches from the included set. Both empty means all operations are included.

This is the primary mechanism for keeping tool counts manageable. LLMs perform better with focused, relevant tool sets than with 500 operations from a comprehensive API spec.

---

## 12. Request Construction and Dispatch

When `tools/call` arrives, ZeroMcp.Relay:

1. **Identifies the API** from the tool name prefix.
2. **Locates the operation** in the cached spec by `operationId`.
3. **Resolves the base URL** — from `apis[].baseUrl` if set, otherwise from the spec's `servers[0].url`.
4. **Constructs the URL** — substitutes path parameters from tool arguments, appends query parameters.
5. **Constructs the request body** — serialises relevant tool arguments as JSON with `Content-Type: application/json` if the operation has a `requestBody`.
6. **Applies auth** — adds the configured auth header or query parameter.
7. **Applies static headers** — adds `apis[].headers`.
8. **Dispatches via `HttpClient`** — using a named `IHttpClientFactory` client configured with the per-API timeout.
9. **Maps the response** — 2xx returns the response body as tool result content; 4xx/5xx returns a structured error block with status code and response body.

### Response Handling

Successful responses are returned as text content in the MCP `CallToolResult`. JSON responses are returned as-is. Non-JSON responses (plain text, XML) are returned as strings.

Error responses are not thrown as exceptions — they are returned as tool results with `isError: true`. This lets the LLM reason about the error rather than receiving an opaque tool failure.

---

## 13. Multi-API Tool Namespacing

With multiple APIs configured, tool names must be unique across the merged set. The prefix system guarantees this as long as prefixes are unique — duplicate prefixes cause a startup error with a clear message.

The merged `tools/list` contains tools from all enabled APIs in a single flat list. The prefix gives the LLM a natural signal about which API a tool belongs to:

```
stripe_charge_create       ← Stripe API
stripe_customer_retrieve   ← Stripe API
crm_get_customer           ← internal CRM
crm_create_opportunity     ← internal CRM
gh_repos_list              ← GitHub API
logistics_get_shipment     ← logistics provider
```

---

## 14. Resilience and Timeouts

### Timeouts

Per-request timeouts are applied at the `HttpClient` level. Precedence:

```
apis[].timeout  →  config.defaultTimeout  →  built-in default (30s)
```

A timeout produces a tool result with `isError: true` and a clear timeout message rather than an unhandled exception.

### Retries *(v2)*

Automatic retries are excluded from v1. Retrying a tool call that partially mutated state can cause double-writes. v1 makes one attempt and returns the result or error clearly. Retry policies will be added in v2 with opt-in configuration and idempotency key support.

### Circuit Breaker *(v2)*

Deferred to v2. In v1, a consistently failing API produces consistent tool errors. The health endpoint reports the failure clearly.

---

## 15. Observability

### Logging

Structured logging via `Microsoft.Extensions.Logging`. Console output by default.

| Level | Events |
|---|---|
| `Information` | Startup summary, spec load counts, tool registration |
| `Warning` | Missing `operationId`, spec fetch failure (degraded start), unresolved env vars |
| `Error` | Startup failure, dispatch exception |
| `Debug` | Per-request details, argument mapping, response codes |

In stdio mode, all log output goes to stderr.

### Health Endpoint

`GET /health` (HTTP mode only):

```json
{
  "status": "degraded",
  "apis": [
    {
      "name": "stripe",
      "status": "ok",
      "toolCount": 147,
      "specAge": "00:04:23"
    },
    {
      "name": "crm",
      "status": "ok",
      "toolCount": 34,
      "specAge": "00:04:23"
    },
    {
      "name": "logistics",
      "status": "error",
      "error": "Spec fetch failed: connection refused"
    }
  ],
  "totalTools": 181
}
```

Overall status is `ok` if all APIs are loaded, `degraded` if some failed, `error` if all failed.

### Request Correlation

Each tool call is assigned a correlation ID logged alongside the tool name, target API, HTTP method, path, response status, and duration. This makes it straightforward to trace a specific LLM tool call through to the outbound HTTP request in logs.

---

## 16. Relationship to ZeroMcp Core

ZeroMcp.Relay is a **standalone product** in the ZeroMcp organisation. It does not depend on the `ZeroMcp` NuGet package and is not an extension of it.

**Shared:**
- MCP protocol surface (Streamable HTTP transport, JSON-RPC 2.0, `initialize` / `tools/list` / `tools/call`)
- Tool schema conventions (flat merged input schema, `operationId`-derived names)
- Tool Inspector JSON format at `/mcp/tools`
- Brand, documentation site, GitHub organisation

**Not shared:**
- Code — ZeroMcp.Relay has its own transport implementation
- Dispatch — ZeroMcp uses in-process synthetic HttpContext; ZeroMcp.Relay uses outbound HttpClient
- Package type — ZeroMcp is a NuGet library; ZeroMcp.Relay is a dotnet tool

### Using Both Together

A team can run ZeroMcp (in-process, their own API) and ZeroMcp.Relay (outbound, third-party and auxiliary APIs) and point their LLM client at both. The LLM sees two MCP servers with distinct tool namespaces and uses them naturally alongside each other.

---

## 17. Repository and Package Structure

```
github.com/ZeroMcp/ZeroMcp.Relay

src/
  ZeroMcp.Relay/
    Cli/            ← command handlers (configure, run, tools, validate)
    Config/         ← config schema, file I/O, secret resolution
    Ingestion/      ← OpenAPI fetch, parse, $ref resolution, schema generation
    Relay/          ← HttpClient dispatch, auth strategies, request construction
    Server/         ← MCP transport (HTTP + stdio), JSON-RPC router
    Ui/             ← embedded SPA assets, UI API endpoints

tests/
  ZeroMcp.Relay.Tests/
  ZeroMcp.Relay.Specs/   ← sample OpenAPI specs for testing

samples/
  BasicRelay/            ← minimal relay.config.json + README
  MultiApiRelay/         ← multi-API example with filtering
```

### Target Frameworks

`.NET 9.0` and `.NET 10.0`, matching ZeroMcp core.

### Key Dependencies

- `System.CommandLine` — CLI parsing
- `Microsoft.OpenApi.Readers` — OpenAPI spec parsing
- `Microsoft.Extensions.Http` — `IHttpClientFactory`
- `Microsoft.AspNetCore` — HTTP server (HTTP mode)
- `Microsoft.Extensions.Logging` — structured logging

---

## 18. Deployment Patterns

### Local Developer (stdio)

```json
{
  "mcpServers": {
    "relay": {
      "command": "mcprelay",
      "args": ["run", "--stdio", "--config", "~/projects/myapi/relay.config.json"]
    }
  }
}
```

### Team Server (Docker)

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:9.0
RUN dotnet tool install -g ZeroMcp.Relay
ENV PATH="$PATH:/root/.dotnet/tools"
COPY relay.config.json /app/relay.config.json
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["mcprelay", "run", "--host", "0.0.0.0", "--port", "8080"]
```

```bash
docker run -p 8080:8080 \
  -e STRIPE_SECRET_KEY=sk_live_... \
  -e CRM_API_KEY=... \
  myrelay:latest
```

### CI Validation

```yaml
# GitHub Actions
- name: Validate relay config
  run: mcprelay validate --strict --config relay.config.json
  env:
    STRIPE_SECRET_KEY: ${{ secrets.STRIPE_SECRET_KEY }}
    CRM_API_KEY: ${{ secrets.CRM_API_KEY }}
```

### Standard Setup Workflow

```bash
# 1. Install
dotnet tool install -g ZeroMcp.Relay

# 2. Initialise config in project directory
mcprelay configure init

# 3. Configure APIs via UI
mcprelay run --enable-ui
# → open http://localhost:5000/ui, add APIs, close

# 4. Production run — no UI
mcprelay run

# 5. Or stdio for Claude Desktop
mcprelay run --stdio
```

---

## 19. Roadmap and Future Considerations

### v1 Scope (this spec)
- CLI: `configure`, `run`, `tools`, `validate`
- HTTP server mode and stdio mode
- Config UI behind `--enable-ui`
- Auth: bearer, API key (header + query), basic
- OpenAPI 2.0 and 3.x ingestion (JSON and YAML)
- Tool include/exclude filtering via glob patterns
- Health endpoint
- Structured logging with correlation IDs
- `mcprelay validate` for CI pipelines

### v2 Candidates
- OAuth2 client credentials (token acquisition and refresh)
- Spec hot-reload without restart
- Per-operation auth override
- Circuit breaker and retry with idempotency key support
- Tool result caching with configurable TTL
- `mcprelay configure add --interactive` wizard mode
- Multiple named profiles (`--profile production` vs `--profile staging`)

### Future Considerations
- Hosted SaaS tier — ZeroMcp.Relay Cloud — same config format, no self-hosting required
- Import from Postman collections and HAR files
- Integration with secret managers (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault)
- WebSocket and SSE response support for streaming APIs

---

## 20. Acceptance Criteria

### CLI
- [ ] `dotnet tool install -g ZeroMcp.Relay` installs `mcprelay` on PATH
- [ ] `mcprelay configure init` scaffolds a valid `relay.config.json`
- [ ] `mcprelay configure add` with all auth types writes correct config
- [ ] `mcprelay configure list` shows accurate status including tool count
- [ ] `mcprelay configure test` validates spec fetch and auth resolution
- [ ] `mcprelay configure remove` prompts for confirmation, removes entry
- [ ] `mcprelay configure enable/disable` toggles without removing
- [ ] `mcprelay tools list` shows all tools across all enabled APIs
- [ ] `mcprelay tools inspect` shows full schema for a single tool
- [ ] `mcprelay validate` exits 0 on valid config, 1 on errors
- [ ] `mcprelay validate --strict` treats warnings as errors

### HTTP Server Mode
- [ ] `mcprelay run` binds to `localhost:5000` by default
- [ ] `POST /mcp` handles `initialize`, `tools/list`, `tools/call`
- [ ] `GET /mcp/tools` returns merged tool list JSON
- [ ] `GET /health` returns per-API status and total tool count
- [ ] Tool calls dispatch correct outbound HTTP requests with correct auth
- [ ] 4xx/5xx responses returned as `isError: true` tool results, not exceptions
- [ ] Timeouts respected per-API and globally
- [ ] Disabled APIs excluded from `tools/list`
- [ ] Failed spec fetches degrade gracefully — remaining APIs still available

### Config UI
- [ ] UI not available without `--enable-ui` — no endpoint registered, not a 404
- [ ] `--enable-ui` serves UI at `/ui`
- [ ] Add API flow writes to config file on save
- [ ] Test connection reports spec fetch and auth resolution correctly
- [ ] Tool browser shows tools for selected API with search
- [ ] Tool inspector shows full input schema
- [ ] Tool invoke sends real request and shows raw response
- [ ] Secrets masked in UI and in "view raw config"
- [ ] Status indicators accurate (green/yellow/red per API)

### stdio Mode
- [ ] `mcprelay run --stdio` reads JSON-RPC from stdin, writes to stdout
- [ ] All logging goes to stderr in stdio mode
- [ ] `--enable-ui` ignored with warning when combined with `--stdio`
- [ ] Spec fetch failure at startup writes JSON-RPC error and exits non-zero
- [ ] Claude Desktop config example verified working end-to-end

### OpenAPI Ingestion
- [ ] OpenAPI 3.0, 3.1, and 2.0 JSON and YAML parsed correctly
- [ ] `$ref` chains fully resolved before schema generation
- [ ] Missing `operationId` generates name from method + path with warning
- [ ] `include` / `exclude` glob patterns filter tool set correctly
- [ ] Duplicate prefix at startup causes clear error and exit

### Secret Handling
- [ ] `env:VAR_NAME` references resolved from environment at startup
- [ ] Missing env var disables API with warning, does not crash
- [ ] `--env <path>` loads `.env` file before secret resolution
- [ ] Secrets never written to logs or health endpoint response