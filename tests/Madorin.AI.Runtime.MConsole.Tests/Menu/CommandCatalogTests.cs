using Madorin.AI.Runtime.MConsole.Menu;

namespace Madorin.AI.Runtime.MConsole.Tests.Menu;

[TestClass]
public sealed class CommandCatalogTests
{
    [TestMethod]
    public void CreateDefault_CoversEightSourceBackedGroupsWithoutProviders()
    {
        var catalog = CommandCatalog.CreateDefault();

        Assert.HasCount(8, catalog.Groups);
        Assert.HasCount(41, catalog.Commands);
        Assert.IsFalse(catalog.Groups.Any(static group => group.Id == "providers"));
        Assert.IsTrue(catalog.Commands.Any(static command => command.Id == "skill.refresh"));
        Assert.IsTrue(catalog.Get("skill.list").ContractNote!.Contains(
            "当前源码",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void SessionArchiveCommands_MatchCurrentRuntimeContract()
    {
        var catalog = CommandCatalog.CreateDefault();

        AssertSessionArchiveCommand(catalog.Get("session.archive"), "archive");
        AssertSessionArchiveCommand(catalog.Get("session.unarchive"), "unarchive");
    }

    [TestMethod]
    public void DangerousCommands_AreMarkedForConfirmation()
    {
        var catalog = CommandCatalog.CreateDefault();
        var expected = new[]
        {
            "agent.delete", "memory.clear", "session.delete", "db.repair",
            "db.rebuild", "db.vacuum", "db.migrate", "storage.gc",
        };

        foreach (var commandId in expected)
        {
            var command = catalog.Get(commandId);
            Assert.AreEqual(CommandRiskLevel.Dangerous, command.RiskLevel, commandId);
            Assert.IsTrue(command.RequiresConfirmation, commandId);
        }
    }

    private static void AssertSessionArchiveCommand(
        CommandDefinition command,
        string commandName)
    {
        CollectionAssert.AreEqual(
            new[] { "session", commandName },
            command.CommandPath);
        Assert.AreEqual(CommandRiskLevel.Caution, command.RiskLevel);
        Assert.AreEqual(CommandReadWriteKind.WritesRuntimeState, command.ReadWriteKind);
        Assert.AreEqual(CommandLockKind.WorkspaceWrite, command.LockKind);
        Assert.HasCount(1, command.Parameters);
        Assert.AreEqual("session-id", command.Parameters[0].Name);
        Assert.IsTrue(command.Parameters[0].Required);
        Assert.IsTrue(command.Parameters[0].Positional);
    }
}
