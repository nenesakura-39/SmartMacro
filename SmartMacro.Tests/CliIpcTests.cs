using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Workflow;

namespace SmartMacro.Tests;

public class CliIpcTests
{
    [Fact]
    public void Parse_handles_run_stop_arm_disarm_help_and_modifiers()
    {
        var empty = CliCommandLine.Parse(Array.Empty<string>());
        Assert.Equal(CliActionKind.Activate, empty.Action);
        Assert.Null(empty.ParseError);
        Assert.True(empty.ShouldShowMainWindowOnStartup);

        var runWait = CliCommandLine.Parse(new[] { "--run", "Daily Farm", "--no-focus", "--wait", "--exit-after-run" });
        Assert.Null(runWait.ParseError);
        Assert.Equal(CliActionKind.RunMacro, runWait.Action);
        Assert.Equal("Daily Farm", runWait.MacroTarget);
        Assert.True(runWait.NoFocus);
        Assert.True(runWait.WaitForCompletion);
        Assert.True(runWait.ExitAfterRun);
        Assert.True(runWait.EffectiveWait);
        Assert.False(runWait.ShouldShowMainWindowOnStartup);

        var equalsRun = CliCommandLine.Parse(new[] { "--run=\"C:\\Macros\\Boss.smacro\"", "--tray", "--activate" });
        Assert.Null(equalsRun.ParseError);
        Assert.Equal(CliActionKind.RunMacro, equalsRun.Action);
        Assert.Equal(@"C:\Macros\Boss.smacro", equalsRun.MacroTarget);
        Assert.True(equalsRun.StartInTray);
        Assert.True(equalsRun.ActivateWindow);
        Assert.True(equalsRun.ShouldShowMainWindowOnStartup);

        Assert.Equal(CliActionKind.Stop, CliCommandLine.Parse(new[] { "--stop" }).Action);
        Assert.Equal(CliActionKind.ArmTriggers, CliCommandLine.Parse(new[] { "--arm" }).Action);
        Assert.Equal(CliActionKind.DisarmTriggers, CliCommandLine.Parse(new[] { "--disarm" }).Action);
        Assert.Equal(CliActionKind.Help, CliCommandLine.Parse(new[] { "--help" }).Action);
        Assert.Equal(CliActionKind.Help, CliCommandLine.Parse(new[] { "-h" }).Action);
        Assert.Equal(CliActionKind.Help, CliCommandLine.Parse(new[] { "/?" }).Action);
    }

    [Fact]
    public void Parse_reports_clear_errors_for_missing_targets_conflicts_and_unknown_flags()
    {
        var missing = CliCommandLine.Parse(new[] { "--run" });
        Assert.NotNull(missing.ParseError);

        var followedBySwitch = CliCommandLine.Parse(new[] { "--run", "--wait" });
        Assert.NotNull(followedBySwitch.ParseError);

        var emptyEquals = CliCommandLine.Parse(new[] { "--run=" });
        Assert.NotNull(emptyEquals.ParseError);

        var conflict = CliCommandLine.Parse(new[] { "--run", "Farm", "--stop" });
        Assert.NotNull(conflict.ParseError);

        var armConflict = CliCommandLine.Parse(new[] { "--arm", "--disarm" });
        Assert.NotNull(armConflict.ParseError);

        var unknown = CliCommandLine.Parse(new[] { "--stpo" });
        Assert.NotNull(unknown.ParseError);
    }

    [Fact]
    public void FormatRunCommand_and_BuildHelpText_produce_clean_output()
    {
        var cmd = CliCommandLine.FormatRunCommand(@"C:\Tools\SmartMacro.exe", "Daily Farm", noFocus: true, wait: true);
        Assert.Equal("\"C:\\Tools\\SmartMacro.exe\" --run \"Daily Farm\" --no-focus --wait", cmd);

        var help = CliCommandLine.BuildHelpText();
        Assert.Contains("--run", help, StringComparison.Ordinal);
        Assert.Contains("--stop", help, StringComparison.Ordinal);
        Assert.Contains("--arm", help, StringComparison.Ordinal);
        Assert.Contains("--disarm", help, StringComparison.Ordinal);
        Assert.Contains("--no-focus", help, StringComparison.Ordinal);
        Assert.Contains("--wait", help, StringComparison.Ordinal);
    }

    [Fact]
    public void StudioCommandCatalog_exposes_CopyCliCommand()
    {
        var matches = StudioCommandCatalog.Filter("cli");
        Assert.Contains(matches, c => c.Kind == StudioCommandKind.CopyCliCommand);
    }

    [Fact]
    public async Task CliIpc_forwards_commands_and_handles_concurrent_stop_during_wait()
    {
        var pipeName = CliIpcNames.GetPipeName($"test-{Guid.NewGuid():N}");
        Assert.Equal(pipeName, pipeName);
        Assert.NotEqual(pipeName, CliIpcNames.GetPipeName($"other-{Guid.NewGuid():N}"));

        var stoppedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitStartedSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterSentSignal = new TaskCompletionSource<CliCommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var server = new CliIpcServer(
            pipeName,
            async (req, ct) =>
            {
                if (req.Action == CliActionKind.RunMacro && req.WaitForCompletion)
                {
                    waitStartedSignal.TrySetResult(true);
                    await stoppedSignal.Task.WaitAsync(ct);
                    return new CliCommandResponse(
                        CliExitCodes.PlaybackFailedOrStopped,
                        "Stopped",
                        "Stopped macro.",
                        PlaybackOutcome.Stopped);
                }

                if (req.Action == CliActionKind.Stop)
                {
                    stoppedSignal.TrySetResult(true);
                    return new CliCommandResponse(CliExitCodes.Success, "Stopped", "Automation stopped.");
                }

                return new CliCommandResponse(CliExitCodes.Success, "Armed", "Macro triggers armed.");
            },
            afterResponseSent: (_, resp) =>
            {
                if (resp.Status == "Armed")
                    afterSentSignal.TrySetResult(resp);
                return Task.CompletedTask;
            });

        // 1. Standard request (--arm) + afterResponseSent hook
        var armResponse = await CliIpcClient.SendAsync(
            pipeName,
            new CliCommandRequest { Action = CliActionKind.ArmTriggers },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(armResponse);
        Assert.Equal(CliExitCodes.Success, armResponse!.ExitCode);
        Assert.Equal("Armed", (await afterSentSignal.Task.WaitAsync(TimeSpan.FromSeconds(2))).Status);

        // 2. Concurrent client: start --run --wait (stays open), then send --stop to unblock it
        var waitTask = CliIpcClient.SendAsync(
            pipeName,
            new CliCommandRequest { Action = CliActionKind.RunMacro, MacroTarget = "Farm", WaitForCompletion = true },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10));

        await waitStartedSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var stopResponse = await CliIpcClient.SendAsync(
            pipeName,
            new CliCommandRequest { Action = CliActionKind.Stop },
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));
        Assert.NotNull(stopResponse);
        Assert.Equal(CliExitCodes.Success, stopResponse!.ExitCode);

        var runResult = await waitTask;
        Assert.NotNull(runResult);
        Assert.Equal(CliExitCodes.PlaybackFailedOrStopped, runResult!.ExitCode);
        Assert.Equal(PlaybackOutcome.Stopped, runResult.PlaybackOutcome);
    }

    [Fact]
    public async Task CliIpcClient_retries_startup_race_and_returns_null_when_no_server_exists()
    {
        var pipeName = CliIpcNames.GetPipeName($"race-{Guid.NewGuid():N}");

        // Server starts 120 ms after client begins connecting -> retry loop succeeds
        CliIpcServer? delayedServer = null;
        var startServerTask = Task.Run(async () =>
        {
            await Task.Delay(120);
            delayedServer = new CliIpcServer(
                pipeName,
                (req, _) => Task.FromResult(new CliCommandResponse(CliExitCodes.Success, "Activated", req.Action.ToString())));
        });

        try
        {
            var reply = await CliIpcClient.SendAsync(
                pipeName,
                new CliCommandRequest { Action = CliActionKind.Activate },
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5));
            Assert.NotNull(reply);
            Assert.Equal(CliExitCodes.Success, reply!.ExitCode);
        }
        finally
        {
            await startServerTask;
            if (delayedServer is not null)
                await delayedServer.DisposeAsync();
        }

        // Non-existent pipe -> returns null cleanly within timeout
        var missingReply = await CliIpcClient.SendAsync(
            CliIpcNames.GetPipeName($"missing-{Guid.NewGuid():N}"),
            new CliCommandRequest { Action = CliActionKind.Activate },
            TimeSpan.FromMilliseconds(220),
            TimeSpan.FromSeconds(2));
        Assert.Null(missingReply);
    }
}
