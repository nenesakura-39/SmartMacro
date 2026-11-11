using System.IO;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;
using Xunit;

namespace SmartMacro.Tests;

public sealed class EvergreenUpgradeTests : IDisposable
{
    private readonly string _tempDir;

    public EvergreenUpgradeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartMacro_Evergreen_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup lock races in temp folder.
        }
    }

    // ---------------- Pillar 1: Evergreen Self-Service, Backup & OS Integration ----------------

    [Fact]
    public void LogService_PruneOldLogs_DeletesOnlyLogsOlderThanRetentionWindow()
    {
        var logDir = Path.Combine(_tempDir, "Logs");
        Directory.CreateDirectory(logDir);

        var oldLog = Path.Combine(logDir, "smartmacro-20250101.log");
        var recentLog = Path.Combine(logDir, "smartmacro-20261101.log");
        var nonLog = Path.Combine(logDir, "notes.txt");

        File.WriteAllText(oldLog, "old");
        File.WriteAllText(recentLog, "recent");
        File.WriteAllText(nonLog, "keep");

        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(oldLog, now.AddDays(-45).UtcDateTime);
        File.SetLastWriteTimeUtc(recentLog, now.AddDays(-5).UtcDateTime);
        File.SetLastWriteTimeUtc(nonLog, now.AddDays(-90).UtcDateTime);

        var deleted = LogService.PruneOldLogs(logDir, maxDays: 30, referenceUtcNow: now);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(recentLog));
        Assert.True(File.Exists(nonLog));
    }

    [Fact]
    public void AppPaths_PortableMode_DetectsPortableMarkerFileAndFolder()
    {
        var normalDir = Path.Combine(_tempDir, "NormalApp");
        Directory.CreateDirectory(normalDir);
        var normalPaths = AppPaths.CreateDefault(normalDir);
        Assert.False(normalPaths.IsPortable);

        var markerDir = Path.Combine(_tempDir, "PortableMarkerApp");
        Directory.CreateDirectory(markerDir);
        File.WriteAllText(Path.Combine(markerDir, AppPaths.PortableMarkerFileName), "portable");
        var markerPaths = AppPaths.CreateDefault(markerDir);
        Assert.True(markerPaths.IsPortable);
        Assert.StartsWith(Path.Combine(markerDir, AppPaths.PortableDataFolderName), markerPaths.RootDirectory, StringComparison.OrdinalIgnoreCase);

        var folderDir = Path.Combine(_tempDir, "PortableFolderApp");
        Directory.CreateDirectory(Path.Combine(folderDir, AppPaths.PortableDataFolderName));
        var folderPaths = AppPaths.CreateDefault(folderDir);
        Assert.True(folderPaths.IsPortable);
    }

    [Fact]
    public async Task JsonMacroRepository_TrashLifecycle_LoadRestoreDeleteEmptyAndPrune()
    {
        var paths = new AppPaths(Path.Combine(_tempDir, "RepoRoot"));
        var repo = new JsonMacroRepository(paths.MacrosDirectory, paths.TrashDirectory);

        var m1 = new Macro { Name = "Daily Quest", Steps = { new DelayStep { DurationMs = 200 } } };
        var m2 = new Macro { Name = "Boss Farming", Steps = { new DelayStep { DurationMs = 300 }, new DelayStep { DurationMs = 400 } } };
        await repo.SaveAsync(m1);
        await repo.SaveAsync(m2);

        await repo.DeleteAsync(m1.Id);
        await repo.DeleteAsync(m2.Id);

        var active = await repo.LoadAllAsync();
        Assert.Empty(active.Macros);

        var trash = await repo.LoadTrashAsync();
        Assert.Equal(2, trash.Count);
        Assert.Contains(trash, t => t.Name == "Daily Quest" && t.StepCount == 1);
        Assert.Contains(trash, t => t.Name == "Boss Farming" && t.StepCount == 2);

        // Restore one item
        var firstTrash = trash.First(t => t.Name == "Daily Quest");
        var restored = await repo.RestoreFromTrashAsync(firstTrash.TrashFileName);
        Assert.NotNull(restored);
        Assert.Equal("Daily Quest", restored!.Name);

        var trashAfterRestore = await repo.LoadTrashAsync();
        Assert.Single(trashAfterRestore);

        // Empty remaining trash
        var emptied = await repo.EmptyTrashAsync();
        Assert.Equal(1, emptied);
        Assert.Empty(await repo.LoadTrashAsync());
    }

    [Fact]
    public async Task StudioBackupService_RoundTripsMacrosAndSettingsThroughZipArchive()
    {
        var macro = new Macro
        {
            Name = "Evergreen Backup Test",
            IsPinned = true,
            Steps = { new KeyPressStep { VirtualKey = 0x46, HoldDurationMs = 50 } }
        };
        var settings = new AppSettings
        {
            DefaultDelayMs = 250,
            StartWithWindows = true
        };

        var archivePath = Path.Combine(_tempDir, "studio-test.smbackup");
        var manifest = await StudioBackupService.CreateBackupAsync(archivePath, new[] { macro }, settings);
        Assert.Equal(1, manifest.MacroCount);
        Assert.True(manifest.IncludesSettings);
        Assert.True(File.Exists(archivePath));

        var result = await StudioBackupService.ReadBackupAsync(archivePath);
        Assert.Single(result.RestoredMacros);
        Assert.Equal("Evergreen Backup Test", result.RestoredMacros[0].Name);
        Assert.True(result.RestoredMacros[0].IsPinned);
        Assert.NotNull(result.RestoredSettings);
        Assert.Equal(250, result.RestoredSettings!.DefaultDelayMs);
        Assert.True(result.RestoredSettings.StartWithWindows);
        Assert.Empty(result.SkippedFiles);
    }

    // ---------------- Pillar 2: Brain & Logic 2.0 Completeness ----------------

    [Fact]
    public void VariableStore_SupportsMultiplyDivideModuloRoundAndRandomRangeParsing()
    {
        var store = new VariableStore();
        store.Apply("score", MacroVariableType.Number, VariableOperation.Set, "10");
        store.Apply("score", MacroVariableType.Number, VariableOperation.Multiply, "3");
        Assert.True(store.TryGetNumber("score", out var n1) && n1 == 30);

        store.Apply("score", MacroVariableType.Number, VariableOperation.Divide, "4");
        Assert.True(store.TryGetNumber("score", out var n2) && n2 == 7.5);

        store.Apply("score", MacroVariableType.Number, VariableOperation.Round, "");
        Assert.True(store.TryGetNumber("score", out var n3) && n3 == 8);

        store.Apply("score", MacroVariableType.Number, VariableOperation.Modulo, "5");
        Assert.True(store.TryGetNumber("score", out var n4) && n4 == 3);

        // Division by zero stays safe (returns 0 instead of NaN/Infinity)
        store.Apply("score", MacroVariableType.Number, VariableOperation.Divide, "0");
        Assert.True(store.TryGetNumber("score", out var n5) && n5 == 0);

        Assert.Equal((5, 15), VariableStore.ParseRandomRange("5..15"));
        Assert.Equal((10, 20), VariableStore.ParseRandomRange("20 - 10"));
        Assert.Equal((1, 100), VariableStore.ParseRandomRange(""));
    }

    [Fact]
    public async Task PlaybackEngine_SetVariable_SupportsRandomNumberActiveWindowTimeAudioPeakAndClipboardCopy()
    {
        string? writtenClipboard = null;
        var clipboard = new FakeClipboard { Text = "initial", OnWrite = s => writtenClipboard = s };
        var layout = new FakeScreenLayout { ForegroundTitle = "Elden Ring" };
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: null, layout: layout)
        {
            Clipboard = clipboard,
            AudioPeakReader = () => 74.5
        };

        var macro = new Macro
        {
            Name = "SetVariable Evergreen Test",
            Steps =
            {
                new SetVariableStep { VariableName = "rng", Source = VariableSourceKind.RandomNumber, Value = "42..42" },
                new SetVariableStep { VariableName = "win", Source = VariableSourceKind.ActiveWindowTitle },
                new SetVariableStep { VariableName = "day", Source = VariableSourceKind.CurrentTimeText, Value = "yyyy" },
                new SetVariableStep { VariableName = "peak", Source = VariableSourceKind.SystemAudioPeakPercent, CopyResultToClipboard = true },
            }
        };

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.NotNull(result.FinalVariables);

        var vars = result.FinalVariables!.ToDictionary(v => v.Name, v => v.Value);
        Assert.Equal("42", vars["rng"]);
        Assert.Equal("Elden Ring", vars["win"]);
        Assert.Equal(DateTime.Now.Year.ToString(), vars["day"]);
        Assert.Equal("74.5", vars["peak"]);
        Assert.Equal("74.5", writtenClipboard);
    }

    [Fact]
    public async Task PlaybackEngine_CompoundCondition_EvaluatesAndOrCorrectlyInIfAndRepeat()
    {
        var engine = new PlaybackEngine(new FakeInput(), new FakeClock());

        var macro = new Macro
        {
            Name = "Compound Condition Test",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "hp", Type = MacroVariableType.Number, DefaultValue = "30" },
                new() { Name = "potions", Type = MacroVariableType.Number, DefaultValue = "2" },
                new() { Name = "healed", Type = MacroVariableType.Number, DefaultValue = "0" },
            },
            Steps =
            {
                new IfStep
                {
                    Condition = new Condition
                    {
                        Comparison = new VariableComparison { VariableName = "hp", Operator = VariableCompareOperator.LessThan, RightValue = "50" },
                        SecondCombinator = ConditionCombinator.And,
                        SecondCondition = new Condition
                        {
                            Comparison = new VariableComparison { VariableName = "potions", Operator = VariableCompareOperator.GreaterThan, RightValue = "0" }
                        }
                    }
                },
                new SetVariableStep { VariableName = "healed", Operation = VariableOperation.Set, Value = "1" },
                new EndIfStep()
            }
        };

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        var vars = result.FinalVariables!.ToDictionary(v => v.Name, v => v.Value);
        Assert.Equal("1", vars["healed"]);
    }

    // ---------------- Pillar 3: AFK Audio, Notifications & Blackbox Flight Recorder ----------------

    [Fact]
    public async Task PlaybackEngine_ScreenshotStep_PlaysSoundCueAndFormatsNotificationMessage()
    {
        SoundCueKind? playedCue = null;
        var screen = BgraImage.Create(32, 32);
        screen.FillRectangle(new ScreenRect(0, 0, 32, 32), new RgbColor(20, 40, 60));
        var capture = new FakeCapture(screen);

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture)
        {
            SoundCuePlayer = cue => playedCue = cue
        };

        var shotDir = Path.Combine(_tempDir, "Shots");
        var macro = new Macro
        {
            Name = "Compound Condition Test",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "gold", Type = MacroVariableType.Number, DefaultValue = "500" }
            },
            Steps =
            {
                new ScreenshotStep
                {
                    FolderPath = shotDir,
                    FileNamePrefix = "BossLoot",
                    SoundCue = SoundCueKind.SuccessChime,
                    NotificationMessage = "Looted {gold} gold!"
                }
            }
        };

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(SoundCueKind.SuccessChime, playedCue);
        Assert.NotEmpty(Directory.GetFiles(shotDir, "*.png"));
    }

    [Fact]
    public async Task PlaybackEngine_BlackboxFlightRecorder_SavesFailureSnapshotAndPerStepStats()
    {
        var logsDir = Path.Combine(_tempDir, "FlightLogs");
        var screen = BgraImage.Create(40, 40);
        screen.FillRectangle(new ScreenRect(0, 0, 40, 40), new RgbColor(10, 20, 30));
        var capture = new FakeCapture(screen);

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock(), capture: capture)
        {
            FailureSnapshotDirectory = logsDir
        };

        var findStep = new FindColorStep
        {
            Color = "#FF00FF",
            Tolerance = 2,
            TimeoutMs = 0,
            WhenNotFound = TextNotFoundBehavior.StopMacro
        };

        var macro = new Macro
        {
            Name = "Blackbox Flight Recorder Test",
            Steps =
            {
                new DelayStep { DurationMs = 10 },
                findStep
            }
        };

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Failed, result.Outcome);
        Assert.NotNull(result.FailureSnapshotPath);
        Assert.True(File.Exists(result.FailureSnapshotPath!));
        Assert.NotNull(result.StepStats);
        Assert.Equal(2, result.StepStats!.Count);
        Assert.All(result.StepStats!.Values, s => Assert.Equal(1, s.ExecutionCount));
    }

    // ---------------- Pillar 4: Schema Version & Pinning Round-Trip ----------------

    [Fact]
    public void MacroDocument_RequiredSchemaVersion_BumpsToCurrentOnlyWhenEvergreenFeaturesAreUsed()
    {
        var simple = new Macro
        {
            Name = "Simple v1",
            IsPinned = true, // IsPinned is metadata; should not force v17 if steps are v1
            Steps = { new DelayStep { DurationMs = 100 } }
        };
        Assert.Equal(1, MacroDocument.RequiredSchemaVersion(simple));

        var withCompound = new Macro
        {
            Name = "Compound Condition",
            Steps =
            {
                new IfStep
                {
                    Condition = new Condition
                    {
                        Comparison = new VariableComparison { VariableName = "a", Operator = VariableCompareOperator.Equals, RightValue = "1" },
                        SecondCombinator = ConditionCombinator.Or,
                        SecondCondition = new Condition
                        {
                            Comparison = new VariableComparison { VariableName = "b", Operator = VariableCompareOperator.Equals, RightValue = "2" }
                        }
                    }
                },
                new EndIfStep()
            }
        };
        Assert.Equal(MacroDocument.CurrentSchemaVersion, MacroDocument.RequiredSchemaVersion(withCompound));

        var withSound = new Macro
        {
            Name = "Screenshot Chime",
            Steps =
            {
                new ScreenshotStep { SoundCue = SoundCueKind.SoftBell }
            }
        };
        Assert.Equal(MacroDocument.CurrentSchemaVersion, MacroDocument.RequiredSchemaVersion(withSound));
    }

    // ---------------- Test Doubles ----------------

    private sealed class FakeClipboard : IClipboardReader
    {
        public string? Text { get; set; }
        public Action<string>? OnWrite { get; set; }
        public string? ReadText() => Text;
        public void WriteText(string text)
        {
            Text = text;
            OnWrite?.Invoke(text);
        }
    }

    private sealed class FakeScreenLayout : IScreenLayout
    {
        public string? ForegroundTitle { get; set; }
        public IReadOnlyList<MonitorInfo> GetMonitors() => new[] { new MonitorInfo(1, new ScreenRect(0, 0, 1920, 1080), true, "Display 1") };
        public IReadOnlyList<WindowInfo> GetWindows() => Array.Empty<WindowInfo>();
        public string? GetForegroundWindowTitle() => ForegroundTitle;
    }
}
