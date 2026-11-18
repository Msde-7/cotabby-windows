namespace Cotabby.Inference.Tests;

/// <summary>
/// A test that only runs when an environment variable is set, and otherwise shows as skipped
/// instead of passing without doing anything.
/// </summary>
public sealed class FactWhenEnvAttribute : FactAttribute
{
    /// <param name="requiredValue">The value the variable must have. Null accepts any non-empty value.</param>
    public FactWhenEnvAttribute(string variable, string? requiredValue = null)
    {
        string? value = Environment.GetEnvironmentVariable(variable);
        if (requiredValue is null ? string.IsNullOrEmpty(value) : value != requiredValue)
        {
            Skip = $"Set {variable}={requiredValue ?? "<value>"} to run this test.";
        }
    }
}
