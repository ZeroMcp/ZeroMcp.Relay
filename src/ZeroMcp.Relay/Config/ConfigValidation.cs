namespace ZeroMcp.Relay.Config;

public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ConfigValidationIssue(ValidationSeverity Severity, string Code, string Message, string? ApiName = null);

public sealed class ConfigValidationResult
{
    public List<ConfigValidationIssue> Issues { get; } = [];

    public bool IsValid => Issues.TrueForAll(i => i.Severity != ValidationSeverity.Error);

    public void AddError(string code, string message, string? apiName = null)
        => Issues.Add(new ConfigValidationIssue(ValidationSeverity.Error, code, message, apiName));

    public void AddWarning(string code, string message, string? apiName = null)
        => Issues.Add(new ConfigValidationIssue(ValidationSeverity.Warning, code, message, apiName));
}
