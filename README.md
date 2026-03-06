# ZeroMcp.Relay

**Turn any OpenAPI spec into an MCP server with a single command.**

`ZeroMcp.Relay` is a standalone `dotnet tool` that converts one or more OpenAPI/Swagger specifications into a fully functional MCP server. Point it at spec URLs, configure authentication, and immediately expose every documented endpoint as an MCP tool — no code, no compilation, no framework knowledge required.

```
ZeroMcp          ← your own ASP.NET Core APIs (in-process, NuGet package)
ZeroMcp.Relay    ← any API with an OpenAPI spec (outbound HTTP relay, dotnet tool)
```

Both expose tools through the same MCP protocol and can be used side-by-side.

---

## Table of Contents

- [Installation](#installation)
- [Quick Start](#quick-start)
- [CLI Reference](#cli-reference)
- [Configuration](#configuration)
- [Authentication](#authentication)
- [HTTP Server Mode](#http-server-mode)
- [Config UI](#config-ui)
- [stdio Mode](#stdio-mode)
- [OpenAPI Ingestion](#openapi-ingestion)
- [Tool Generation](#tool-generation)
- [Deployment Patterns](#deployment-patterns)
- [Development](#development)

---

## Installation

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

The tool name is `mcprelay`. The package name is `ZeroMcp.Relay`.

---

## Quick Start

```bash
# 1. Scaffold a config file
mcprelay configure init

# 2. Add an API
mcprelay configure add -n petstore \
  -s https://petstore3.swagger.io/api/v3/openapi.json

# 3. Start with the config UI to manage APIs visually
mcprelay run --enable-ui
# → open http://localhost:5000/ui

# 4. Or start in production mode (no UI)
mcprelay run

# 5. Or start in stdio mode for Claude Desktop / Claude Code
mcprelay run --stdio
```

---

## CLI Reference

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

#### `configure init`

```bash
mcprelay configure init
```

Scaffolds a `relay.config.json` in the current directory. Safe to run in an existing project.

#### `configure add`

```
mcprelay configure add
  -n, --name         <string>   Required. Short identifier (e.g. "stripe")
  -s, --source       <url>      Required. OpenAPI/Swagger spec URL
  -k, --api-key      <value>    API key value or env var reference (e.g. env:STRIPE_KEY)
  -h, --header       <k=v>      Custom header (repeatable)
  -u, --username     <string>   Basic auth username
  -p, --password     <value>    Basic auth password or env var reference
  -b, --bearer       <value>    Bearer token or env var reference
      --prefix       <string>   Tool name prefix override (default: name)
      --timeout      <seconds>  Per-request timeout in seconds (default: 30)
      --disabled                Add the API but disable it immediately
      --config       <path>     Config file path
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

#### `configure remove`

```bash
mcprelay configure remove -n <name>          # prompts for confirmation
mcprelay configure remove -n <name> --yes    # skips confirmation
```

#### `configure list`

```bash
mcprelay configure list
```

```
NAME       SOURCE                              AUTH      ENABLED  TOOLS
stripe     https://...stripe.../spec3.json     bearer    yes      147
crm        https://internal-crm.../swagger     api-key   yes      34
github     https://...github.com/api...json    bearer    yes      512
```

#### `configure show`

```bash
mcprelay configure show -n <name>
```

Prints the full config entry for one API. Secrets are masked (`sk_live_****`).

#### `configure enable / disable`

```bash
mcprelay configure enable  -n <name>
mcprelay configure disable -n <name>
```

Toggles an API without removing it.

#### `configure test`

```bash
mcprelay configure test -n <name>
```

Fetches the spec URL, validates auth credentials resolve, and reports success or failure. Does not invoke any actual operations.

#### `configure set-secret`

```bash
mcprelay configure set-secret -n <name> --bearer   <value>
mcprelay configure set-secret -n <name> --api-key  <value>
mcprelay configure set-secret -n <name> --password <value>
```

Updates only the auth credential for an existing API.

### `mcprelay run`

```
mcprelay run
  --port              <int>     HTTP server port (default: 5000)
  --host              <string>  Bind address (default: localhost)
  --stdio                       Run in stdio mode instead of HTTP
  --enable-ui                   Enable the config UI (HTTP mode only)
  --config            <path>    Config file path
  --env               <path>    .env file to load before starting
  --validate-on-start           Fetch and parse all specs at startup (default: true)
  --lazy                        Skip spec fetching at startup; fetch on first tool call
```

Examples:

```bash
# Local setup session — UI enabled
mcprelay run --enable-ui

# Production — no UI, localhost only
mcprelay run

# Team server — bind to all interfaces
mcprelay run --host 0.0.0.0 --port 8080

# Load secrets from .env file
mcprelay run --env .env.production

# stdio for Claude Desktop / Claude Code
mcprelay run --stdio

# stdio with a specific config
mcprelay run --stdio --config ~/projects/myproject/relay.config.json
```

### `mcprelay tools`

```bash
mcprelay tools list                    # List all tools across enabled APIs
mcprelay tools list -n <api-name>      # Filter to one API
mcprelay tools inspect -t <tool-name>  # Full tool descriptor with input schema
mcprelay tools count                   # Total and per-API breakdown
```

### `mcprelay validate`

```bash
mcprelay validate                             # Validate config and specs
mcprelay validate --strict                    # Treat warnings as errors
mcprelay validate --config path/to/config.json
```

Fetches all spec URLs, parses them, and reports issues (missing `operationId`, malformed schemas, unresolvable env vars). Exits 0 on success, 1 on error. Designed for CI pipelines.

---

## Configuration

Configuration lives in `relay.config.json`. The config file path is resolved in order:

1. `--config <path>` flag (if provided)
2. `./relay.config.json` (current directory)
3. `~/.mcprelay/config.json` (home directory fallback)

### Full Example

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
| `apis[].source` | string | required | URL or `file://` path to OpenAPI spec |
| `apis[].baseUrl` | string? | null | Override base URL (uses spec `servers[0].url` if null) |
| `apis[].prefix` | string | = name | Prepended to tool names: `{prefix}_{operationId}` |
| `apis[].enabled` | bool | true | Whether this API's tools are included |
| `apis[].timeout` | int? | null | Per-API timeout override in seconds |
| `apis[].auth` | object | none | Authentication strategy (see below) |
| `apis[].headers` | object | `{}` | Static headers sent with every request |
| `apis[].include` | string[] | `[]` | Glob patterns for tool names to include (empty = all) |
| `apis[].exclude` | string[] | `[]` | Glob patterns for tool names to exclude |

### Secret Resolution

Any string value beginning with `env:` is resolved from the environment at startup:

```json
{ "token": "env:STRIPE_SECRET_KEY" }
```

If the referenced variable is not set, the API is disabled with a warning rather than starting with invalid credentials.

The `--env <path>` flag loads a `.env` file before secret resolution for local development.

---

## Authentication

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

---

## HTTP Server Mode

When started without `--stdio`, ZeroMcp.Relay runs an ASP.NET Core web server.

### Endpoints

```
POST /mcp          JSON-RPC 2.0 — initialize, tools/list, tools/call
GET  /mcp          Server info and handshake
GET  /mcp/tools    Tool list JSON (Inspector-compatible format)
GET  /health       Health check — per-API status and total tool count
```

### Health Endpoint

`GET /health` returns per-API status, tool counts, and overall system health:

```json
{
  "status": "degraded",
  "apis": [
    { "name": "stripe", "status": "ok", "toolCount": 147 },
    { "name": "crm", "status": "ok", "toolCount": 34 },
    { "name": "logistics", "status": "error", "error": "Spec fetch failed" }
  ],
  "totalTools": 181
}
```

Status values: `ok` (all APIs loaded), `degraded` (some failed), `error` (all failed).

### Port and Binding

Default: `http://localhost:5000`. Use `--host 0.0.0.0` for team/server deployments. TLS termination should be handled by a reverse proxy (nginx, Caddy, Traefik).

---

## Config UI

The config UI is a single-page application served when `--enable-ui` is passed. It provides a visual editor for `relay.config.json` and a tool browser.

```bash
mcprelay run --enable-ui
# → open http://localhost:5000/ui
```

The UI is **only** available when `--enable-ui` is explicitly passed. A standard `mcprelay run` exposes no UI surface at all — no endpoint registered, not even a 404.

| Context | Command | UI available |
|---|---|---|
| Local setup | `mcprelay run --enable-ui` | Yes |
| Production | `mcprelay run` | No |
| CI/CD | `mcprelay run` | No |
| Claude Desktop | `mcprelay run --stdio` | No |

### UI Endpoints (when enabled)

```
GET  /             Redirects to /ui
GET  /ui           Config UI SPA
GET  /ui/config    Masked config JSON
GET  /ui/apis      API list with status
POST /ui/apis      Add a new API
PUT  /ui/apis/{n}  Edit an existing API
DELETE /ui/apis/{n} Remove an API
POST /ui/apis/toggle/{n}     Enable/disable
POST /ui/apis/test/{n}       Test connection
POST /ui/apis/fetch-spec     Preview a spec URL
GET  /ui/tools               Tool list (optional ?api= filter)
GET  /ui/tools/{name}        Tool detail with input schema
POST /ui/tools/invoke        Invoke a tool
POST /admin/reload           Reload config and specs
```

### Features

**API management:**
- Add a new API (name, source URL, auth, prefix, timeout, headers, include/exclude)
- Preview an OpenAPI spec before saving (title, version, operation count, warnings)
- Edit an existing API's configuration
- Remove an API with confirmation
- Enable/disable without removing
- Test connection (validates spec fetch and auth resolution)

**Tool browsing:**
- List all tools for the selected API
- Search and filter by name or description
- Click a tool to inspect its full input schema
- Invoke a tool and see the raw response

**Status indicators:**
- Green dot: API loaded, spec fetched, auth resolved
- Yellow dot: API disabled
- Red dot: Spec fetch failed or auth env var missing

### Add API Flow

1. Click **+ Add API**
2. Enter name and spec URL
3. Click **Fetch Spec** — shows title, version, operation count, and any warnings
4. Select auth type, enter credentials (`env:VAR_NAME` references supported)
5. Optionally set prefix, timeout, include/exclude patterns
6. Click **Save** — writes to config file, API appears immediately

---

## stdio Mode

When `--stdio` is passed, ZeroMcp.Relay reads JSON-RPC from stdin and writes to stdout. No HTTP server is bound and `--enable-ui` is ignored.

All logging goes to stderr.

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

### Startup Behaviour

All enabled specs are fetched and secrets resolved before reading from stdin. If a spec fetch fails or a required secret is missing, a JSON-RPC error is written to stdout and the process exits with a non-zero code.

Use `--lazy` to defer spec fetching to the first tool call for each API, reducing startup latency.

---

## OpenAPI Ingestion

### Supported Formats

- OpenAPI 3.0.x (JSON and YAML)
- OpenAPI 3.1.x (JSON and YAML)
- OpenAPI 2.0 / Swagger (JSON and YAML)

### Spec Sources

- **HTTP/HTTPS URL** — fetched at startup or lazily on first call
- **Local file** — using `file://` prefix

```bash
mcprelay configure add -n local-api \
  -s file:///path/to/openapi.json \
  --bearer env:LOCAL_TOKEN
```

### Spec Caching

Specs are fetched at startup and held in memory for the process lifetime. A restart picks up new spec versions. `POST /admin/reload` (with `--enable-ui`) triggers a re-fetch without restarting.

### Failure Handling

If a spec URL is unreachable at startup, that API is marked unavailable and its tools excluded from `tools/list`. The remaining APIs continue working. Failures are reported via the health endpoint and the UI status indicators.

---

## Tool Generation

### Naming

Tool names are `{prefix}_{operationId}`, lowercased with non-alphanumeric characters replaced by underscores:

```
operationId: ChargeCreate  →  stripe_charge_create
operationId: GetCustomer   →  crm_get_customer
```

Operations without `operationId` get a name from the HTTP method and path:

```
GET  /customers/{id}  →  crm_get_customers_id
POST /orders          →  crm_post_orders
```

### Input Schema

Parameters are merged into a single flat JSON Schema object:

| OpenAPI source | MCP schema mapping |
|---|---|
| Path parameters | Required properties |
| Query parameters | Optional (or required if `required: true` in spec) |
| Header parameters | Optional properties (applied as outbound headers) |
| `requestBody` (object) | Properties expanded inline |
| `requestBody` (non-object) | Single `body` property |

`$ref` chains are fully resolved. Circular references are detected and broken with an open `{}` schema at the cycle point.

### Include / Exclude Filtering

Control which operations become tools using glob patterns:

```json
{
  "include": ["stripe_charge_*", "stripe_customer_*"],
  "exclude": ["stripe_*_test_*"]
}
```

`include` (if non-empty) acts as a whitelist. `exclude` then removes matches from the included set. Both empty means all operations are included.

### Multi-API Namespacing

Tool names are unique across all configured APIs thanks to the prefix system. The prefix gives LLMs a natural signal about which API a tool belongs to:

```
stripe_charge_create       ← Stripe API
crm_get_customer           ← internal CRM
gh_repos_list              ← GitHub API
```

Duplicate prefixes cause a startup error with a clear message.

---

## Deployment Patterns

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

# 2. Scaffold config
mcprelay configure init

# 3. Add APIs via UI
mcprelay run --enable-ui
# → open http://localhost:5000/ui, add APIs, save, close

# 4. Production run — no UI
mcprelay run

# 5. Or stdio for Claude Desktop
mcprelay run --stdio
```

---

## Development

### Repository Layout

```
ZeroMcp.Relay/
├── ZeroMcp.Relay/          Main tool source
│   ├── Cli/                CLI command handlers
│   ├── Config/             Config schema, validation, secrets
│   ├── Ingestion/          OpenAPI fetch, parse, tool generation
│   ├── Relay/              HTTP dispatch, auth strategies
│   ├── Server/             MCP transport (HTTP + stdio), routing
│   └── Ui/                 Embedded SPA assets
├── tests/
│   ├── ZeroMcp.Relay.Tests/    Unit and integration tests
│   └── ZeroMcp.Relay.Specs/    Test OpenAPI fixtures
└── samples/
    ├── BasicRelay/             Minimal sample config
    └── MultiApiRelay/          Multi-API sample config
```

### Build

```powershell
dotnet build "ZeroMcp.Relay.slnx" -v normal
```

### Test

```powershell
dotnet test "ZeroMcp.Relay.slnx" -v normal
```

### Acceptance Coverage

Acceptance test matrix: `tests/ACCEPTANCE_TEST_MATRIX.md`

### Target Frameworks

.NET 9.0 and .NET 10.0
