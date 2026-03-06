# ZeroMcp.Relay

`ZeroMcp.Relay` is a standalone `dotnet tool` (`mcprelay`) that converts OpenAPI specs into MCP tools.

## Status

Core v1 implementation is in place, including:
- CLI (`configure`, `run`, `tools`, `validate --strict`)
- OpenAPI ingestion (2.0/3.x JSON/YAML) with tool generation
- MCP transports (HTTP + stdio) and relay dispatch pipeline
- Full Config UI served behind `--enable-ui` with:
  - API management (add, edit, remove, enable/disable, test connection)
  - Spec fetch preview (title, version, operation count) before adding
  - Tool browser with search, per-tool schema inspector
  - Dark-themed responsive SPA embedded as a resource
- Acceptance test coverage and CI pipeline

## Repository Layout

- `src/ZeroMcp.Relay` - main tool source
- `tests/ZeroMcp.Relay.Tests` - unit/integration tests
- `tests/ZeroMcp.Relay.Specs` - test OpenAPI fixtures/helpers
- `samples/BasicRelay` - minimal sample config/docs
- `samples/MultiApiRelay` - multi-API sample config/docs

## Build

```powershell
dotnet build "ZeroMcp.Relay.slnx" -v normal
```

## Run

```powershell
dotnet run --project "src/ZeroMcp.Relay/ZeroMcp.Relay.csproj" -- --help
```

## Validate Config (Strict)

```powershell
dotnet run --project "src/ZeroMcp.Relay/ZeroMcp.Relay.csproj" -- validate --strict --config "samples/BasicRelay/relay.config.json"
```

## Acceptance Coverage

- Acceptance matrix: `tests/ACCEPTANCE_TEST_MATRIX.md`
- Full test suite:

```powershell
dotnet test "ZeroMcp.Relay.slnx" -v normal
```
