using SmartMacro.Core.Models;
using SmartMacro.Core.Validation;

namespace SmartMacro.Tests;

public class ValidatorTests
{
    private static Macro WithSteps(params MacroStep[] steps)
    {
        var macro = new Macro { Name = "Test" };
        macro.Steps.AddRange(steps);
        return macro;
    }

    [Fact]
    public void Valid_macro_has_no_errors()
    {
        var issues = MacroValidator.Validate(WithSteps(new DelayStep(), new KeyPressStep(), new TypeTextStep { Text = "a" }));
        Assert.False(issues.HasErrors());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public void Bad_names_are_errors(string name)
    {
        var macro = WithSteps(new DelayStep());
        macro.Name = name;
        Assert.Contains(MacroValidator.Validate(macro), i => i.Severity == IssueSeverity.Error && i.Property == nameof(Macro.Name));
    }

    [Fact]
    public void Name_too_long_is_error()
    {
        var macro = WithSteps();
        macro.Name = new string('x', MacroValidator.MaxNameLength + 1);
        Assert.True(MacroValidator.Validate(macro).HasErrors());
    }

    [Fact]
    public void Empty_macro_is_only_a_warning_but_cannot_execute()
    {
        var macro = WithSteps();
        var issues = MacroValidator.Validate(macro);
        Assert.False(issues.HasErrors());
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning);
        Assert.True(MacroValidator.ValidateForExecution(macro).HasErrors());
    }

    [Fact]
    public void All_disabled_steps_cannot_execute()
    {
        var macro = WithSteps(new DelayStep { Enabled = false });
        Assert.False(MacroValidator.Validate(macro).HasErrors());
        Assert.True(MacroValidator.ValidateForExecution(macro).HasErrors());
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(MacroValidator.MaxDelayMs, false)]
    [InlineData(MacroValidator.MaxDelayMs + 1, true)]
    public void Delay_range(int ms, bool expectError)
    {
        Assert.Equal(expectError, MacroValidator.ValidateStep(new DelayStep { DurationMs = ms }).HasErrors());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(120, false)]
    [InlineData(-120, false)]
    [InlineData(MacroValidator.MaxScrollDelta + 1, true)]
    [InlineData(int.MinValue, true)]
    public void Scroll_range(int delta, bool expectError)
    {
        Assert.Equal(expectError, MacroValidator.ValidateStep(new MouseScrollStep { Delta = delta }).HasErrors());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(254, false)]
    [InlineData(255, true)]
    public void Key_code_range(int vk, bool expectError)
    {
        Assert.Equal(expectError, MacroValidator.ValidateStep(new KeyPressStep { VirtualKey = vk }).HasErrors());
    }

    [Fact]
    public void Modifiers_with_hold_action_is_warning_only()
    {
        var issues = MacroValidator.ValidateStep(new KeyPressStep { Action = KeyAction.Down, Modifiers = KeyModifiers.Control });
        Assert.False(issues.HasErrors());
        Assert.Single(issues);
    }

    [Fact]
    public void Undefined_enum_values_are_errors()
    {
        Assert.True(MacroValidator.ValidateStep(new MouseClickStep { Button = (MacroMouseButton)99 }).HasErrors());
        Assert.True(MacroValidator.ValidateStep(new KeyPressStep { Modifiers = (KeyModifiers)64 }).HasErrors());
    }

    [Fact]
    public void Empty_text_is_error()
    {
        Assert.True(MacroValidator.ValidateStep(new TypeTextStep { Text = "" }).HasErrors());
        Assert.True(MacroValidator.ValidateStep(new TypeTextStep { Text = "a", CharacterDelayMs = -1 }).HasErrors());
    }

    [Fact]
    public void Position_outside_screen_is_warning()
    {
        var screen = new ScreenBounds(0, 0, 1920, 1080);
        var issues = MacroValidator.ValidateStep(new MouseMoveStep { X = 5000, Y = 10 }, screen);
        Assert.False(issues.HasErrors());
        Assert.Single(issues);

        Assert.Empty(MacroValidator.ValidateStep(new MouseMoveStep { X = 1919, Y = 1079 }, screen));
        // Current-position clicks ignore the coordinates.
        Assert.Empty(MacroValidator.ValidateStep(new MouseClickStep { UseCurrentPosition = true, X = -9999 }, screen));
    }

    [Fact]
    public void Step_issues_carry_index_and_id()
    {
        var bad = new DelayStep { DurationMs = -5 };
        var issues = MacroValidator.Validate(WithSteps(new DelayStep(), bad));
        var issue = Assert.Single(issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(1, issue.StepIndex);
        Assert.Equal(bad.Id, issue.StepId);
        Assert.StartsWith("Step 2:", issue.DisplayText);
    }

    [Fact]
    public void Duplicate_step_ids_are_errors()
    {
        var a = new DelayStep();
        var b = new DelayStep { Id = a.Id };
        Assert.True(MacroValidator.Validate(WithSteps(a, b)).HasErrors());
    }

    [Theory]
    [InlineData(-1, 1.0, true)]
    [InlineData(0, 1.0, false)]
    [InlineData(MacroValidator.MaxRepeatCount + 1, 1.0, true)]
    [InlineData(1, 0.05, true)]
    [InlineData(1, 10.0, false)]
    [InlineData(1, 10.5, true)]
    [InlineData(1, double.NaN, true)]
    public void Repeat_and_speed_ranges(int repeat, double speed, bool expectError)
    {
        var macro = WithSteps(new DelayStep());
        macro.RepeatCount = repeat;
        macro.Speed = speed;
        Assert.Equal(expectError, MacroValidator.Validate(macro).HasErrors());
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(MacroValidator.MaxDelayMs, false)]
    [InlineData(MacroValidator.MaxDelayMs + 1, true)]
    public void Repeat_delay_range(int ms, bool expectError)
    {
        var macro = WithSteps(new DelayStep());
        macro.RepeatDelayMs = ms;
        Assert.Equal(expectError, MacroValidator.Validate(macro).HasErrors());
    }

    [Fact]
    public void Old_files_without_playback_options_get_defaults()
    {
        var macro = SmartMacro.Core.Storage.MacroJson.Deserialize<Macro>("""{ "name": "Old", "steps": [] }""")!;
        Assert.Equal(1, macro.RepeatCount);
        Assert.Equal(1.0, macro.Speed);
        Assert.Equal(0, macro.RepeatDelayMs);
    }

    [Fact]
    public void Null_step_list_is_error()
    {
        var macro = new Macro { Name = "x", Steps = null! };
        Assert.True(MacroValidator.Validate(macro).HasErrors());
    }
}
