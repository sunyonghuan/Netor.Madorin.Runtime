using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Skills;
using Madorin.AI.Runtime.Services.Tools;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class SkillsCatalogTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "madorin-skills-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task WorkspaceSourceWinsAndRefreshDoesNotMutateOldSnapshot()
    {
        var workspace = Path.Combine(_root, "workspace");
        var host = Path.Combine(_root, "host");
        var data = Path.Combine(_root, "data");
        WriteSkill(Path.Combine(workspace, ".madorin", "skills"), "demo", "workspace", "one");
        WriteSkill(host, "demo", "host", "two");

        await using var service = new SkillCatalogService(
            workspace,
            data,
            "runtime-test",
            [new SkillSourceDescriptor("host", SkillSourceKind.Directory, host)]);
        var first = await service.InitializeAsync();

        Assert.AreEqual("workspace", first.Skills.Single().SourceId);
        var firstBody = await File.ReadAllTextAsync(
            Path.Combine(first.Skills.Single().MaterializedPath, "SKILL.md"));
        StringAssert.Contains(firstBody, "workspace");
        StringAssert.Contains(firstBody, "one");

        var oldPath = first.Skills.Single().MaterializedPath;
        WriteSkill(Path.Combine(workspace, ".madorin", "skills"), "demo", "workspace", "changed");
        var second = await service.RefreshAsync();

        Assert.AreNotEqual(first.Version, second.Version);
        Assert.AreNotEqual(oldPath, second.Skills.Single().MaterializedPath);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(oldPath, "SKILL.md")), "one");
        StringAssert.Contains(await File.ReadAllTextAsync(
            Path.Combine(second.Skills.Single().MaterializedPath, "SKILL.md")), "changed");
    }

    [TestMethod]
    public async Task MaterializationIsInstanceIsolatedAndCopiesOnlyExposedFiles()
    {
        var workspace = Path.Combine(_root, "workspace");
        var data = Path.Combine(workspace, ".madorin");
        var source = Path.Combine(workspace, ".madorin", "skills");
        WriteSkill(source, "demo", "workspace", "one");
        var skillDirectory = Path.Combine(source, "demo");
        File.WriteAllText(Path.Combine(skillDirectory, "notes.md"), "notes");
        File.WriteAllBytes(Path.Combine(skillDirectory, "secret.bin"), [1, 2, 3]);

        await using var firstService = new SkillCatalogService(
            workspace,
            data,
            "runtime-one",
            sourceScope: SkillCatalogSourceScope.Workspace);
        await using var secondService = new SkillCatalogService(
            workspace,
            data,
            "runtime-two",
            sourceScope: SkillCatalogSourceScope.Workspace);

        var first = await firstService.InitializeAsync();
        var second = await secondService.InitializeAsync();
        var firstPath = first.Skills.Single().MaterializedPath;
        var secondPath = second.Skills.Single().MaterializedPath;

        Assert.AreNotEqual(firstPath, secondPath);
        StringAssert.Contains(firstPath, Path.Combine("skill-catalog", "instances"));
        Assert.IsFalse(IsSameOrUnder(firstPath, source));
        Assert.IsTrue(File.Exists(Path.Combine(firstPath, "SKILL.md")));
        Assert.IsTrue(File.Exists(Path.Combine(firstPath, "notes.md")));
        Assert.IsTrue(File.Exists(Path.Combine(firstPath, "scripts", "run.py")));
        Assert.IsFalse(File.Exists(Path.Combine(firstPath, "secret.bin")));
    }

    [TestMethod]
    public async Task EmptyAutomaticSourceDoesNotCreateMaterializationDirectory()
    {
        var workspace = Path.Combine(_root, "workspace");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(workspace);

        await using var service = new SkillCatalogService(
            workspace,
            data,
            "runtime-empty",
            sourceScope: SkillCatalogSourceScope.Workspace);
        var snapshot = await service.InitializeAsync();

        Assert.AreEqual("empty", snapshot.Version);
        Assert.IsEmpty(snapshot.Skills);
        Assert.IsFalse(Directory.Exists(Path.Combine(data, "skill-catalog")));
    }

    [TestMethod]
    public async Task FileWatcherRefreshesOnlyTheNextCapturedSnapshot()
    {
        var workspace = Path.Combine(_root, "工作区");
        var data = Path.Combine(_root, "data");
        var source = Path.Combine(workspace, ".madorin", "skills");
        WriteSkill(source, "demo", "workspace", "before");

        await using var service = new SkillCatalogService(
            workspace,
            data,
            "runtime-watch",
            sourceScope: SkillCatalogSourceScope.Workspace);
        var first = await service.InitializeAsync();
        var firstPath = first.Skills.Single().MaterializedPath;

        WriteSkill(source, "demo", "workspace", "after");
        await WaitForConditionAsync(() => service.IsStale, TimeSpan.FromSeconds(10));
        var second = await service.GetSnapshotForInvocationAsync();

        Assert.AreNotEqual(first.Version, second.Version);
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(firstPath, "SKILL.md")), "before");
        StringAssert.Contains(
            await File.ReadAllTextAsync(Path.Combine(second.Skills.Single().MaterializedPath, "SKILL.md")),
            "after");
    }

    [TestMethod]
    public async Task FailedRefreshPreservesPublishedSnapshot()
    {
        var workspace = Path.Combine(_root, "workspace");
        var host = Path.Combine(_root, "host");
        var data = Path.Combine(_root, "data");
        WriteSkill(host, "demo", "host", "one");

        await using var service = new SkillCatalogService(
            workspace,
            data,
            "runtime-failure",
            [new SkillSourceDescriptor("host", SkillSourceKind.Directory, host)]);
        var first = await service.InitializeAsync();
        Directory.Delete(host, recursive: true);

        var exception = await Assert.ThrowsExactlyAsync<SkillCatalogRefreshException>(
            () => service.RefreshAsync());

        Assert.AreEqual(RuntimeErrorCodes.SkillSourceNotFound, exception.Code);
        Assert.AreSame(first, service.Current);
    }

    [TestMethod]
    public async Task CaseEquivalentNamesWithinOneSourceDisableAllConflicts()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("A case-sensitive filesystem is required to create this collision.");
        }

        var workspace = Path.Combine(_root, "workspace");
        var data = Path.Combine(_root, "data");
        var source = Path.Combine(workspace, ".madorin", "skills");
        WriteSkill(source, "demo", "lower", "one");
        WriteSkill(source, "Demo", "upper", "two", frontmatterName: "demo");

        await using var service = new SkillCatalogService(
            workspace,
            data,
            "runtime-duplicate",
            sourceScope: SkillCatalogSourceScope.Workspace);
        var snapshot = await service.InitializeAsync();

        Assert.IsEmpty(snapshot.Skills);
        Assert.AreEqual(2, snapshot.Diagnostics.Count(static x => x.Code == RuntimeErrorCodes.SkillDuplicateInSource));
    }

    [TestMethod]
    public async Task MissingExplicitSourceFailsClosed()
    {
        await using var service = new SkillCatalogService(
            Path.Combine(_root, "workspace"),
            Path.Combine(_root, "data"),
            "runtime-test",
            [new SkillSourceDescriptor("missing", SkillSourceKind.Directory, Path.Combine(_root, "missing"))]);

        SkillCatalogRefreshException? exception = null;
        try
        {
            await service.InitializeAsync();
        }
        catch (SkillCatalogRefreshException caught)
        {
            exception = caught;
        }

        Assert.IsNotNull(exception);
        Assert.AreEqual(RuntimeErrorCodes.SkillSourceNotFound, exception!.Code);
    }

    [TestMethod]
    public void ScriptAuthorizationKeyIncludesHashAndInterpreter()
    {
        var first = SkillScriptAuthorization.CreateKey("demo", "A", "python3");
        var same = SkillScriptAuthorization.CreateKey("demo", "A", "python3");
        var changedHash = SkillScriptAuthorization.CreateKey("demo", "B", "python3");
        var changedInterpreter = SkillScriptAuthorization.CreateKey("demo", "A", "python");

        Assert.AreEqual(first, same);
        Assert.AreNotEqual(first, changedHash);
        Assert.AreNotEqual(first, changedInterpreter);
    }

    private static void WriteSkill(
        string sourceRoot,
        string name,
        string description,
        string marker,
        string? frontmatterName = null)
    {
        var directory = Path.Combine(sourceRoot, name);
        Directory.CreateDirectory(Path.Combine(directory, "scripts"));
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {frontmatterName ?? name}\ndescription: {description}\n---\n{marker}\n");
        File.WriteAllText(Path.Combine(directory, "scripts", "run.py"), "print('ok')\n");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("The expected filesystem change was not observed before the timeout.");
            }

            await Task.Delay(50);
        }
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullCandidate, fullRoot, comparison)
            || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }
}
