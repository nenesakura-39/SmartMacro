using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

public class RepositoryTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppPaths _paths;

    public RepositoryTests()
    {
        _paths = new AppPaths(_dir.Path);
        _paths.EnsureCreated();
    }

    public void Dispose() => _dir.Dispose();

    private JsonMacroRepository NewRepo() => new(_paths.MacrosDirectory, _paths.TrashDirectory);

    private static Macro SampleMacro(string name = "Sample") => new()
    {
        Name = name,
        Description = "desc",
        Steps =
        {
            new DelayStep { DurationMs = 250 },
            new MouseMoveStep { X = 10, Y = 20 },
            new MouseClickStep { Button = MacroMouseButton.Right, Action = MouseClickAction.DoubleClick, X = 5, Y = 6 },
            new MouseScrollStep { Delta = 240, Horizontal = true },
            new KeyPressStep { VirtualKey = 0x41, Modifiers = KeyModifiers.Control | KeyModifiers.Shift },
            new TypeTextStep { Text = "héllo\nworld", CharacterDelayMs = 5, Enabled = false, Comment = "note" },
        },
    };

    [Fact]
    public async Task Save_then_load_round_trips_every_step_type()
    {
        var repo = NewRepo();
        var macro = SampleMacro();
        await repo.SaveAsync(macro);

        var result = await NewRepo().LoadAllAsync();

        Assert.Empty(result.Problems);
        var loaded = Assert.Single(result.Macros);
        Assert.Equal(macro.Id, loaded.Id);
        Assert.Equal("Sample", loaded.Name);
        Assert.Equal(6, loaded.Steps.Count);
        Assert.Equal(250, Assert.IsType<DelayStep>(loaded.Steps[0]).DurationMs);
        var click = Assert.IsType<MouseClickStep>(loaded.Steps[2]);
        Assert.Equal(MacroMouseButton.Right, click.Button);
        Assert.Equal(MouseClickAction.DoubleClick, click.Action);
        var scroll = Assert.IsType<MouseScrollStep>(loaded.Steps[3]);
        Assert.True(scroll.Horizontal);
        var key = Assert.IsType<KeyPressStep>(loaded.Steps[4]);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, key.Modifiers);
        var text = Assert.IsType<TypeTextStep>(loaded.Steps[5]);
        Assert.Equal("héllo\nworld", text.Text);
        Assert.False(text.Enabled);
        Assert.Equal("note", text.Comment);
        Assert.Equal(macro.Steps.Select(s => s.Id), loaded.Steps.Select(s => s.Id));
    }

    [Fact]
    public async Task Save_sets_timestamps_and_writes_versioned_file()
    {
        var repo = NewRepo();
        var macro = SampleMacro();
        await repo.SaveAsync(macro);

        Assert.NotEqual(default, macro.CreatedAt);
        Assert.NotEqual(default, macro.ModifiedAt);

        var file = Assert.Single(Directory.GetFiles(_paths.MacrosDirectory, "*.json"));
        var json = await File.ReadAllTextAsync(file);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"type\": \"mouseClick\"", json);
    }

    [Fact]
    public async Task Saving_again_keeps_a_backup_of_previous_version()
    {
        var repo = NewRepo();
        var macro = SampleMacro("First");
        await repo.SaveAsync(macro);
        macro.Name = "Second";
        await repo.SaveAsync(macro);

        var file = Path.Combine(_paths.MacrosDirectory, macro.Id.ToString("N") + ".json");
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(file + ".bak"));
        Assert.Contains("First", await File.ReadAllTextAsync(file + ".bak"));
        Assert.Contains("Second", await File.ReadAllTextAsync(file));

        // The .bak must not show up as a second macro.
        var result = await NewRepo().LoadAllAsync();
        Assert.Single(result.Macros);
    }

    [Fact]
    public async Task Invalid_macro_is_rejected_and_nothing_is_written()
    {
        var repo = NewRepo();
        var macro = new Macro { Name = "  " };

        var ex = await Assert.ThrowsAsync<MacroValidationException>(() => repo.SaveAsync(macro));
        Assert.Contains(ex.Issues, i => i.Property == nameof(Macro.Name));
        Assert.Empty(Directory.GetFiles(_paths.MacrosDirectory));
    }

    [Fact]
    public async Task Corrupted_file_is_reported_and_left_untouched()
    {
        var bad = Path.Combine(_paths.MacrosDirectory, "broken.json");
        await File.WriteAllTextAsync(bad, "{ this is not json");
        await NewRepo().SaveAsync(SampleMacro("Good"));

        var result = await NewRepo().LoadAllAsync();

        Assert.Single(result.Macros);
        var problem = Assert.Single(result.Problems);
        Assert.Equal("broken.json", problem.FileName);
        Assert.Equal("{ this is not json", await File.ReadAllTextAsync(bad));
    }

    [Fact]
    public async Task Unknown_step_type_is_reported_not_loaded()
    {
        var file = Path.Combine(_paths.MacrosDirectory, "evil.json");
        await File.WriteAllTextAsync(file, """
            { "schemaVersion": 1, "macro": { "id": "6f9619ff-8b86-d011-b42d-00c04fc964ff", "name": "x",
              "steps": [ { "type": "System.Diagnostics.Process", "fileName": "calc.exe" } ] } }
            """);

        var result = await NewRepo().LoadAllAsync();

        Assert.Empty(result.Macros);
        Assert.Single(result.Problems);
    }

    [Fact]
    public async Task Newer_schema_version_is_refused()
    {
        var file = Path.Combine(_paths.MacrosDirectory, "future.json");
        await File.WriteAllTextAsync(file, """{ "schemaVersion": 99, "macro": { "name": "x", "steps": [] } }""");

        var result = await NewRepo().LoadAllAsync();

        Assert.Empty(result.Macros);
        Assert.Contains("newer version", Assert.Single(result.Problems).Message);
    }

    [Fact]
    public async Task Duplicate_ids_are_repaired_without_losing_either_macro()
    {
        var id = Guid.NewGuid();
        foreach (var name in new[] { "a", "b" })
        {
            await File.WriteAllTextAsync(Path.Combine(_paths.MacrosDirectory, name + ".json"),
                $$"""{ "schemaVersion": 1, "macro": { "id": "{{id}}", "name": "Macro {{name}}", "steps": [] } }""");
        }

        var first = await NewRepo().LoadAllAsync();
        Assert.Equal(2, first.Macros.Count);
        Assert.Equal(2, first.Macros.Select(m => m.Id).Distinct().Count());
        Assert.NotEmpty(first.Repairs);

        // The repair is persisted, so a second load is clean.
        var second = await NewRepo().LoadAllAsync();
        Assert.Equal(2, second.Macros.Count);
        Assert.Empty(second.Repairs);
        Assert.Equal(first.Macros.Select(m => m.Id).OrderBy(x => x), second.Macros.Select(m => m.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Null_steps_are_normalized()
    {
        await File.WriteAllTextAsync(Path.Combine(_paths.MacrosDirectory, "n.json"),
            """{ "schemaVersion": 1, "macro": { "id": "11111111-1111-1111-1111-111111111111", "name": "N", "steps": [ null, { "type": "delay", "durationMs": 5 } ] } }""");

        var result = await NewRepo().LoadAllAsync();

        var macro = Assert.Single(result.Macros);
        Assert.Single(macro.Steps);
        Assert.NotEmpty(result.Repairs);
    }

    [Fact]
    public async Task Type_discriminator_can_appear_after_other_properties()
    {
        await File.WriteAllTextAsync(Path.Combine(_paths.MacrosDirectory, "o.json"),
            """{ "macro": { "name": "Order", "steps": [ { "durationMs": 42, "type": "delay" } ] }, "schemaVersion": 1 }""");

        var result = await NewRepo().LoadAllAsync();

        Assert.Empty(result.Problems);
        Assert.Equal(42, Assert.IsType<DelayStep>(Assert.Single(Assert.Single(result.Macros).Steps)).DurationMs);
    }

    [Fact]
    public async Task Delete_moves_file_to_trash()
    {
        var repo = NewRepo();
        var macro = SampleMacro();
        await repo.SaveAsync(macro);

        Assert.True(await repo.DeleteAsync(macro.Id));

        Assert.Empty(Directory.GetFiles(_paths.MacrosDirectory, "*.json"));
        Assert.Single(Directory.GetFiles(_paths.TrashDirectory, "*.json"));
        Assert.Empty((await NewRepo().LoadAllAsync()).Macros);
        Assert.False(await repo.DeleteAsync(macro.Id));
    }

    [Fact]
    public async Task Save_after_load_writes_back_to_the_original_file()
    {
        var file = Path.Combine(_paths.MacrosDirectory, "custom-name.json");
        await File.WriteAllTextAsync(file,
            """{ "schemaVersion": 1, "macro": { "id": "22222222-2222-2222-2222-222222222222", "name": "Custom", "createdAt": "2026-01-01T00:00:00+00:00", "steps": [] } }""");

        var repo = NewRepo();
        var macro = Assert.Single((await repo.LoadAllAsync()).Macros);
        macro.Name = "Renamed";
        await repo.SaveAsync(macro);

        Assert.Single(Directory.GetFiles(_paths.MacrosDirectory, "*.json"));
        Assert.Contains("Renamed", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task Leftover_temp_files_are_ignored()
    {
        await File.WriteAllTextAsync(Path.Combine(_paths.MacrosDirectory, ".abc.json.123.tmp"), "garbage");
        await File.WriteAllTextAsync(Path.Combine(_paths.MacrosDirectory, ".hidden.json"), "garbage");

        var result = await NewRepo().LoadAllAsync();

        Assert.Empty(result.Macros);
        Assert.Empty(result.Problems);
    }
}
