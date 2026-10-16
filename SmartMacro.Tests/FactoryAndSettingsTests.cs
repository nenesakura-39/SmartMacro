using SmartMacro.Core.Input;
using SmartMacro.Core.Logging;
using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

public class FactoryTests
{
    [Fact]
    public void Unique_names_get_numbered()
    {
        Assert.Equal("New Macro", MacroFactory.MakeUniqueName("New Macro", []));
        Assert.Equal("New Macro (2)", MacroFactory.MakeUniqueName("New Macro", ["new macro"]));
        Assert.Equal("New Macro (3)", MacroFactory.MakeUniqueName("New Macro", ["New Macro", "New Macro (2)"]));
    }

    [Fact]
    public void Unique_name_respects_max_length()
    {
        var longName = new string('a', MacroValidator.MaxNameLength);
        var result = MacroFactory.MakeUniqueName(longName, [longName]);
        Assert.True(result.Length <= MacroValidator.MaxNameLength);
        Assert.EndsWith("(2)", result);
    }

    [Fact]
    public void Duplicate_gets_new_ids_and_same_content()
    {
        var source = new Macro { Name = "Orig", Steps = { new DelayStep { DurationMs = 7 }, new TypeTextStep { Text = "x" } } };
        var copy = MacroFactory.Duplicate(source, ["Orig"]);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal("Orig (copy)", copy.Name);
        Assert.Equal(2, copy.Steps.Count);
        Assert.All(copy.Steps.Zip(source.Steps), p => Assert.NotEqual(p.First.Id, p.Second.Id));
        Assert.Equal(7, Assert.IsType<DelayStep>(copy.Steps[0]).DurationMs);
        Assert.False(MacroValidator.Validate(copy).HasErrors());
    }

    [Theory]
    [InlineData(StepKind.Delay)]
    [InlineData(StepKind.MouseMove)]
    [InlineData(StepKind.MouseClick)]
    [InlineData(StepKind.MouseScroll)]
    [InlineData(StepKind.KeyPress)]
    [InlineData(StepKind.TypeText)]
    [InlineData(StepKind.FindText)]
    public void New_steps_are_valid_and_match_kind(StepKind kind)
    {
        var step = MacroFactory.CreateStep(kind, 300);
        Assert.Equal(kind, step.Kind);
        Assert.False(MacroValidator.ValidateStep(step).HasErrors());
        Assert.False(string.IsNullOrWhiteSpace(StepDescriber.Describe(step)));
    }

    [Fact]
    public void Duplicate_step_copies_values_with_new_id()
    {
        var step = new KeyPressStep { VirtualKey = 0x41, Modifiers = KeyModifiers.Alt, Comment = "c" };
        var copy = Assert.IsType<KeyPressStep>(MacroFactory.DuplicateStep(step));
        Assert.NotEqual(step.Id, copy.Id);
        Assert.Equal(0x41, copy.VirtualKey);
        Assert.Equal(KeyModifiers.Alt, copy.Modifiers);
        Assert.Equal("c", copy.Comment);
    }

    [Fact]
    public void Describer_examples()
    {
        Assert.Equal("Wait 250 ms", StepDescriber.Describe(new DelayStep { DurationMs = 250 }));
        Assert.Equal("Wait 1.5 s", StepDescriber.Describe(new DelayStep { DurationMs = 1500 }));
        Assert.Equal("Press Ctrl + Shift + A",
            StepDescriber.Describe(new KeyPressStep { VirtualKey = 0x41, Modifiers = KeyModifiers.Control | KeyModifiers.Shift }));
        Assert.Equal("Scroll down 1 notch at cursor", StepDescriber.Describe(new MouseScrollStep { Delta = -120 }));
        Assert.Equal("VK 0xFE", VirtualKeys.GetName(0xFE));
    }
}

public class SettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Missing_file_gives_defaults_without_warning()
    {
        var result = await new JsonSettingsStore(_dir.Combine("settings.json")).LoadAsync();
        Assert.Null(result.Warning);
        Assert.True(result.Settings.ConfirmBeforeDelete);
    }

    [Fact]
    public async Task Round_trip()
    {
        var store = new JsonSettingsStore(_dir.Combine("settings.json"));
        await store.SaveAsync(new AppSettings { ConfirmBeforeDelete = false, DefaultDelayMs = 1234, ShowLogPanel = false });

        var result = await store.LoadAsync();
        Assert.Null(result.Warning);
        Assert.False(result.Settings.ConfirmBeforeDelete);
        Assert.Equal(1234, result.Settings.DefaultDelayMs);
        Assert.False(result.Settings.ShowLogPanel);
    }

    [Fact]
    public async Task Corrupt_file_gives_defaults_and_keeps_a_copy()
    {
        var path = _dir.Combine("settings.json");
        await File.WriteAllTextAsync(path, "{{{ nope");

        var result = await new JsonSettingsStore(path).LoadAsync();

        Assert.NotNull(result.Warning);
        Assert.True(result.Settings.ConfirmBeforeDelete);
        Assert.Single(Directory.GetFiles(_dir.Path, "settings.corrupt-*.json"));
    }

    [Fact]
    public async Task Out_of_range_values_are_clamped()
    {
        var path = _dir.Combine("settings.json");
        await File.WriteAllTextAsync(path, """{ "schemaVersion": 1, "defaultDelayMs": -50 }""");

        var result = await new JsonSettingsStore(path).LoadAsync();
        Assert.Equal(0, result.Settings.DefaultDelayMs);
    }
}

public class LogServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Logs_to_memory_file_and_event()
    {
        var log = new LogService(_dir.Path);
        LogEntry? raised = null;
        log.EntryAdded += (_, e) => raised = e;

        log.Error("Something failed", new InvalidOperationException("boom"));

        Assert.NotNull(raised);
        Assert.Equal(AppLogLevel.Error, raised!.Level);
        Assert.Contains("boom", raised.Details);
        Assert.Single(log.GetRecent());
        var file = Assert.Single(Directory.GetFiles(_dir.Path, "*.log"));
        Assert.Contains("Something failed", File.ReadAllText(file));
    }

    [Fact]
    public void Memory_buffer_is_capped()
    {
        var log = new LogService(null);
        for (var i = 0; i < LogService.MaxInMemoryEntries + 50; i++) log.Info($"m{i}");
        Assert.Equal(LogService.MaxInMemoryEntries, log.GetRecent().Count);
    }
}

public class HotkeyTests
{
    [Fact]
    public void Defaults_are_valid_and_distinct()
    {
        Assert.Null(HotkeyBinding.ProblemWithSet(HotkeyBinding.DefaultRecord, HotkeyBinding.DefaultPlay, HotkeyBinding.DefaultPause));
        Assert.Equal("Ctrl+Shift+F9", HotkeyBinding.DefaultRecord.ToString());
    }

    [Theory]
    [InlineData(KeyModifiers.None, 0x41, true)]      // plain A would fire while typing
    [InlineData(KeyModifiers.None, 0x77, false)]     // plain F8 is OK
    [InlineData(KeyModifiers.Control, 0x1B, true)]   // Esc is the emergency stop
    [InlineData(KeyModifiers.Control, 0x7B, true)]   // F12 reserved
    [InlineData(KeyModifiers.Control, 0x11, true)]   // modifier only
    [InlineData(KeyModifiers.Alt, 0x52, false)]      // Alt+R
    public void Single_binding_rules(KeyModifiers modifiers, int vk, bool expectProblem)
    {
        Assert.Equal(expectProblem, new HotkeyBinding(modifiers, vk).Problem() is not null);
    }

    [Fact]
    public void Duplicates_are_rejected()
    {
        var same = new HotkeyBinding(KeyModifiers.Alt, 0x52);
        Assert.NotNull(HotkeyBinding.ProblemWithSet(same, same, HotkeyBinding.DefaultPause));
    }

    [Fact]
    public void All_keys_include_left_and_right_modifiers()
    {
        var keys = HotkeyBinding.DefaultRecord.AllKeys();
        Assert.Contains(0x78, keys);
        Assert.Contains(0xA2, keys);
        Assert.Contains(0xA1, keys);
    }

    [Fact]
    public async Task Settings_round_trip_custom_shortcuts_and_fix_bad_ones()
    {
        using var dir = new TempDir();
        var store = new JsonSettingsStore(dir.Combine("settings.json"));
        await store.SaveAsync(new AppSettings { RecordHotkey = new HotkeyBinding(KeyModifiers.Alt, 0x52) });
        var loaded = (await store.LoadAsync()).Settings;
        Assert.Equal(new HotkeyBinding(KeyModifiers.Alt, 0x52), loaded.RecordHotkey);

        var broken = new AppSettings { RecordHotkey = new HotkeyBinding(KeyModifiers.None, 0x41) };
        broken.Sanitize();
        Assert.Equal(HotkeyBinding.DefaultRecord, broken.RecordHotkey);
    }
}

public class ImportExportTests
{
    [Fact]
    public void Export_then_import_gives_a_fresh_copy()
    {
        var original = new Macro { Name = "Mine", Steps = { new DelayStep { DurationMs = 5 }, new FindTextStep { Text = "OK" } } };
        var json = MacroFileFormat.Serialize(original);

        Assert.True(MacroFileFormat.TryParse(json, out var parsed, out var error), error);
        var imported = MacroFileFormat.PrepareForImport(parsed!, new[] { "Mine" });

        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal("Mine (2)", imported.Name);
        Assert.Equal(2, imported.Steps.Count);
        Assert.DoesNotContain(imported.Steps, s => original.Steps.Any(o => o.Id == s.Id));
        Assert.False(SmartMacro.Core.Validation.MacroValidator.Validate(imported).HasErrors());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"schemaVersion\": 99, \"macro\": { \"name\": \"x\" } }")]
    [InlineData("{ \"schemaVersion\": 1 }")]
    [InlineData("{ \"schemaVersion\": 1, \"macro\": { \"name\": \"x\", \"steps\": [ { \"type\": \"runProgram\", \"path\": \"cmd.exe\" } ] } }")]
    public void Bad_files_are_refused_with_a_reason(string json)
    {
        Assert.False(MacroFileFormat.TryParse(json, out var macro, out var error));
        Assert.Null(macro);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Import_without_name_gets_a_default_name()
    {
        Assert.True(MacroFileFormat.TryParse("""{ "schemaVersion": 1, "macro": { "steps": [ null ] } }""", out var parsed, out _));
        var imported = MacroFileFormat.PrepareForImport(parsed!, Array.Empty<string>());
        Assert.Equal("Imported Macro", imported.Name);
        Assert.Empty(imported.Steps);
    }
}
