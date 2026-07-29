using System.Text.Json;

using Madorin.AI.Runtime.MConsole.Serialization;

namespace Madorin.AI.Runtime.MConsole.Testing;

public sealed class TestCaseStore(string casesDirectory)
{
    private readonly string _casesDirectory = Path.GetFullPath(casesDirectory);

    public async Task SaveAsync(
        MConsoleTestCase testCase,
        CancellationToken cancellationToken = default)
    {
        Validate(testCase);
        Directory.CreateDirectory(_casesDirectory);
        var path = GetPath(testCase.Name);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    testCase,
                    MConsoleJsonContext.Persistence.MConsoleTestCase,
                    cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<MConsoleTestCase> LoadAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(GetPath(name));
        var testCase = await JsonSerializer.DeserializeAsync(
                stream,
                MConsoleJsonContext.Default.MConsoleTestCase,
                cancellationToken)
            ?? throw new InvalidDataException($"Test case '{name}' is empty.");
        Validate(testCase);
        return testCase;
    }

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(_casesDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_casesDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Order(StringComparer.Ordinal)
            .ToArray()!;
    }

    public static TestStepDefinition RecordCommand(
        string stepId,
        string commandId,
        IReadOnlyDictionary<string, string?> arguments,
        IEnumerable<AssertionDefinition> assertions) => new(
            stepId,
            TestStepKind.Command,
            commandId,
            null,
            new Dictionary<string, string?>(arguments, StringComparer.Ordinal),
            assertions.ToArray());

    public static TestStepDefinition RecordHostAction(
        string stepId,
        string action,
        IReadOnlyDictionary<string, string?> arguments,
        IEnumerable<AssertionDefinition> assertions) => new(
            stepId,
            TestStepKind.Host,
            null,
            action,
            new Dictionary<string, string?>(arguments, StringComparer.Ordinal),
            assertions.ToArray());

    private string GetPath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Any(Path.GetInvalidFileNameChars().Contains))
        {
            throw new ArgumentException("Test case name contains invalid path characters.", nameof(name));
        }

        return Path.Combine(_casesDirectory, name + ".json");
    }

    private static void Validate(MConsoleTestCase testCase)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentException.ThrowIfNullOrWhiteSpace(testCase.Name);
        ArgumentNullException.ThrowIfNull(testCase.Steps);
        var duplicate = testCase.Steps
            .GroupBy(static step => step.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Test case contains duplicate step '{duplicate.Key}'.");
        }

        foreach (var step in testCase.Steps)
        {
            if (step.Kind is TestStepKind.Command && string.IsNullOrWhiteSpace(step.CommandId)
                || step.Kind is TestStepKind.Host && string.IsNullOrWhiteSpace(step.HostAction))
            {
                throw new InvalidDataException($"Test step '{step.Id}' has no executable action.");
            }
        }
    }
}
