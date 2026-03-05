# ZeroMcp.Relay

`ZeroMcp.Relay` is a standalone `dotnet tool` (`mcprelay`) that converts OpenAPI specs into MCP tools.

## Status

This repository is in active bootstrap. The current milestone includes:
- Solution and project scaffolding
- .NET tool packaging baseline
- Initial CLI command tree placeholders
- Test project setup

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

## Run (Current Placeholder)

```powershell
dotnet run --project "src/ZeroMcp.Relay/ZeroMcp.Relay.csproj" -- --help
```
