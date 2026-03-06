# Acceptance Criteria Test Matrix

This matrix maps the spec acceptance criteria to automated tests in `tests/ZeroMcp.Relay.Tests/AcceptanceCriteriaTests.cs`.

## CLI
- Install metadata/entry command -> `Cli_InstallMetadata_Exists`
- `configure init` scaffolds valid config -> `Cli_ConfigureInit_CreatesValidConfig`
- `configure add` supports auth models -> `Cli_ConfigureAdd_AllAuthTypes_WriteCorrectConfig`
- `configure list` shows status/tool count -> `Cli_ConfigureList_ReportsStatusAndToolCount`
- `configure test` validates spec+auth -> `Cli_ConfigureTest_ValidatesSpecAndAuthResolution`
- `configure remove` confirmation prompt -> `Cli_ConfigureRemove_PromptsForConfirmation`
- `configure remove` confirmed delete path -> `Cli_ConfigureRemove_Yes_RemovesEntry`
- `enable` / `disable` toggles without delete -> `Cli_ConfigureEnableDisable_TogglesWithoutRemoving`
- `tools list` merged enabled tools -> `Cli_ToolsList_ShowsAllEnabledTools`
- `tools inspect` detailed schema -> `Cli_ToolsInspect_ShowsSchema`
- `validate` exit code behavior -> `Cli_Validate_ExitCodeBehavior`
- `validate --strict` warning promotion -> `Cli_ValidateStrict_TreatsWarningsAsErrors`

## HTTP Runtime / MCP
- Default host/port -> `Http_DefaultRunOptions_BindLocalhost5000`
- MCP methods (`initialize`, `tools/list`, `tools/call`) -> `Http_McpRouter_HandlesInitializeToolsListToolsCall`
- Tool list merged across APIs -> `Http_McpTools_MergesTools`
- Health payload with per-API + totals -> `Http_Health_ReturnsApiStatusAndTotal`
- Outbound request auth + URL mapping correctness -> `Http_Dispatch_ConstructsUrlAndAuthHeadersCorrectly`
- 4xx/5xx -> MCP error mapping -> `Http_Dispatch_Maps4xx5xxToIsError`
- Timeout behavior -> `Http_Dispatch_RespectsTimeouts`
- Disabled APIs excluded from tools -> `Http_DisabledApis_ExcludedFromToolsList`
- Failed spec degrades while others run -> `Http_SpecFailure_DegradesWithRemainingApis`

## UI (Feature-Flagged)
- Routes absent when UI disabled -> `Ui_Routes_NotRegisteredWithoutEnableUi`
- Routes present when UI enabled -> `Ui_Routes_RegisteredWithEnableUi`
- API add flow persists config -> `Ui_AddFlow_WritesConfig`
- API test connection status report -> `Ui_TestConnection_ReportsStatusAndCount`
- Tool browser selected API filtering -> `Ui_ToolBrowser_CanFilterSelectedApi`
- Tool inspector full schema -> `Ui_ToolInspector_ShowsFullInputSchema`
- Tool invoke raw response -> `Ui_ToolInvoke_ReturnsRawResponse`
- Raw config secret masking -> `Ui_RawConfig_MasksSecrets`
- Status indicators map to runtime state -> `Ui_StatusIndicators_MapToApiState`

## Stdio
- Reads stdin and writes stdout -> `Stdio_ReadsStdinAndWritesStdout`
- Logging to stderr -> `Stdio_LogsToStderr`
- `--enable-ui` ignored warning path -> `Stdio_EnableUiFlag_EmitsIgnoredWarning`
- Startup spec failure emits JSON-RPC error + non-zero exit -> `Stdio_SpecLoadFailure_WritesJsonRpcError_AndExitsNonZero`
- Claude Desktop example args are parseable -> `Stdio_ClaudeDesktopExampleArgs_AreParseable`

## OpenAPI Ingestion
- OpenAPI 3.0/3.1/2.0 + YAML support -> `OpenApi_Loads30_31_20_JsonYaml`
- `$ref` chain resolution -> `OpenApi_RefChainsResolved`
- Missing `operationId` fallback + warning -> `OpenApi_MissingOperationId_GeneratesWarning`
- Include/exclude filter semantics -> `OpenApi_IncludeExcludeFilter_Works`
- Duplicate prefix validation failure -> `OpenApi_DuplicatePrefix_ShowsValidationError`
- Duplicate prefix startup failure behavior -> `OpenApi_DuplicatePrefix_StartFailsClearly`

## Secret Handling
- `env:` resolution works -> `SecretHandling_EnvReferencesResolve`
- Missing env does not crash and disables API -> `SecretHandling_MissingEnv_DisablesApiWithoutCrash`
- Missing env emits validation warning -> `SecretHandling_MissingEnv_ValidationWarns`
- `--env` file loads before resolution -> `SecretHandling_EnvFileLoadsBeforeResolution`
- Secrets omitted from health payload -> `SecretHandling_NoSecretsInHealthPayload`
