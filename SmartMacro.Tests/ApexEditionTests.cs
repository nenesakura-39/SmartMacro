using SmartMacro.Core.Help;
using SmartMacro.Core.Models;
using SmartMacro.Core.Ocr;
using SmartMacro.Core.Playback;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Validation;
using SmartMacro.Core.Vision;

namespace SmartMacro.Tests;

public class ApexEditionTests
{
    [Fact]
    public void Avx2_and_Sse2_ScreenChangeDetector_ColorFinder_and_TemplateMatcher_work_across_odd_widths()
    {
        // Width = 38 exercises the 8-pixel AVX2 Vector256<byte> loop (4x8 = 32 px), the 4-pixel SSE2 Vector128<byte> tail (32..35),
        // and the 2-pixel scalar tail (36..37).
        const int width = 38;
        const int height = 12;
        var beforeImg = new BgraImage(width, height, new byte[width * height * 4]);
        var afterImg = new BgraImage(width, height, new byte[width * height * 4]);

        // Paint a 16x6 target patch at (18, 3) in 'afterImg' with RGB(220, 40, 60) -> BGRA(60, 40, 220, 255)
        for (var y = 3; y < 9; y++)
        {
            for (var x = 18; x < 34; x++)
            {
                var idx = (y * width + x) * 4;
                afterImg.Pixels[idx] = 60;
                afterImg.Pixels[idx + 1] = 40;
                afterImg.Pixels[idx + 2] = 220;
                afterImg.Pixels[idx + 3] = 255;
            }
        }

        // 1. ScreenChangeDetector
        var bounds = new ScreenRect(0, 0, width, height);
        var diff = ScreenChangeDetector.Compare(new ScreenFrame(bounds, beforeImg), new ScreenFrame(bounds, afterImg), tolerance: 10);
        Assert.Equal(16 * 6, diff.ChangedPixels);
        Assert.True(diff.ChangedPercent > 15.0);

        // 2. ColorFinder.Find & FindObjects
        var targetColor = new RgbColor(220, 40, 60);
        var colorResult = ColorFinder.Find(afterImg, targetColor, tolerance: 5, minPixels: 10);
        Assert.NotNull(colorResult.Largest);
        Assert.Equal(16 * 6, colorResult.Largest!.PixelCount);

        var objectsResult = ColorFinder.FindObjects(afterImg, targetColor, secondaryColor: null, tolerance: 5, new ColorSearchOptions { MinPixels = 10 });
        Assert.Single(objectsResult.Blobs);
        Assert.Equal(16 * 6, objectsResult.Blobs[0].PixelCount);

        // 3. TemplateMatcher (16x6 template exercises Avx2.SumAbsoluteDifferences width >= 16)
        var tplPixels = new byte[16 * 6 * 4];
        for (var i = 0; i < tplPixels.Length; i += 4)
        {
            tplPixels[i] = 60;
            tplPixels[i + 1] = 40;
            tplPixels[i + 2] = 220;
            tplPixels[i + 3] = 255;
        }
        // Add a distinctive corner pixel so the template isn't flat-colored
        tplPixels[0] = 250;
        afterImg.Pixels[(3 * width + 18) * 4] = 250;

        var tpl = new BgraImage(16, 6, tplPixels);
        var tplResult = TemplateMatcher.Find(afterImg, tpl, new TemplateMatchOptions { MinScore = 0.90 });
        Assert.NotNull(tplResult.Best);
        Assert.Equal(18, tplResult.Best!.Bounds.X);
        Assert.Equal(3, tplResult.Best.Bounds.Y);
    }

    [Fact]
    public void FormulaEvaluator_evaluates_math_functions_variables_and_safe_division()
    {
        var store = new VariableStore(new[]
        {
            new MacroVariableDefinition { Name = "gold", Type = MacroVariableType.Number, DefaultValue = "1000" },
            new MacroVariableDefinition { Name = "bonus", Type = MacroVariableType.Number, DefaultValue = "250" },
        });

        Assert.Equal(1400.0, FormulaEvaluator.Evaluate("({gold} + bonus) * 1.12", store), precision: 5);
        Assert.Equal(50.0, FormulaEvaluator.Evaluate("clamp(round(sqrt(2600)), 10, 50)", store), precision: 5);
        Assert.Equal(8.0, FormulaEvaluator.Evaluate("pow(2, 3)", store), precision: 5);
        Assert.Equal(7.0, FormulaEvaluator.Evaluate("min(12, max(3, abs(-7)))", store), precision: 5);
        Assert.Equal(0.0, FormulaEvaluator.Evaluate("{gold} / 0", store), precision: 5);
    }

    [Fact]
    public async Task SetVariableStep_with_Formula_source_updates_variable_and_validates()
    {
        var macro = new Macro
        {
            Name = "Formula Test",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "hp", Type = MacroVariableType.Number, DefaultValue = "80" },
                new() { Name = "shield", Type = MacroVariableType.Number, DefaultValue = "45" },
            },
            Steps = new List<MacroStep>
            {
                new SetVariableStep
                {
                    VariableName = "hp",
                    Operation = VariableOperation.Set,
                    Source = VariableSourceKind.Formula,
                    Value = "min(100, {hp} + {shield} * 0.5)",
                },
            },
        };

        Assert.Empty(MacroValidator.Validate(macro));
        Assert.Equal(17, MacroDocument.RequiredSchemaVersion(macro));

        var engine = new PlaybackEngine(new FakeInput(), new FakeClock());
        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task FlowControlStep_ExitLoop_ContinueLoop_and_StopMacro_work_as_expected()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());

        // Loop 5 times, skip iteration 2 via ContinueLoop, exit at iteration 3 via ExitLoop, then run post-loop step, then StopMacro.
        var macro = new Macro
        {
            Name = "FlowControl Test",
            Variables = new List<MacroVariableDefinition>
            {
                new() { Name = "i", Type = MacroVariableType.Number, DefaultValue = "0" },
            },
            Steps = new List<MacroStep>
            {
                new RepeatStep { Mode = RepeatMode.Times, Count = 5 },
                new SetVariableStep { VariableName = "i", Operation = VariableOperation.Add, Value = "1" },
                new IfStep
                {
                    Condition = new Condition
                    {
                        Comparison = new VariableComparison { VariableName = "i", Operator = VariableCompareOperator.Equals, RightValue = "2" },
                    },
                },
                new FlowControlStep { Action = FlowControlAction.ContinueLoop },
                new EndIfStep(),
                new MouseMoveStep { X = 10, Y = 10 },
                new IfStep
                {
                    Condition = new Condition
                    {
                        Comparison = new VariableComparison { VariableName = "i", Operator = VariableCompareOperator.Equals, RightValue = "3" },
                    },
                },
                new FlowControlStep { Action = FlowControlAction.ExitLoop },
                new EndIfStep(),
                new EndRepeatStep(),
                new MouseMoveStep { X = 99, Y = 99 },
                new FlowControlStep { Action = FlowControlAction.StopMacro },
                new MouseMoveStep { X = 500, Y = 500 },
            },
        };

        Assert.Empty(MacroValidator.Validate(macro));

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[]
        {
            "move 10,10",
            "move 10,10",
            "move 99,99",
        }, input.Calls);
    }

    [Fact]
    public void FlowControlStep_outside_Repeat_loop_reports_validation_error()
    {
        var macro = new Macro
        {
            Name = "Invalid Break",
            Steps = new List<MacroStep>
            {
                new FlowControlStep { Action = FlowControlAction.ExitLoop },
            },
        };

        var issues = MacroValidator.Validate(macro);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Error && i.Message.Contains("Repeat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Background_input_steps_dispatch_to_background_simulator_methods()
    {
        var input = new FakeInput();
        var engine = new PlaybackEngine(input, new FakeClock());

        var macro = new Macro
        {
            Name = "Background Test",
            Steps = new List<MacroStep>
            {
                new MouseClickStep { X = 120, Y = 240, Button = MacroMouseButton.Left, Action = MouseClickAction.Click, BackgroundWindowTitle = "Notepad" },
                new KeyPressStep { VirtualKey = 0x41, Action = KeyAction.Press, BackgroundWindowTitle = "Notepad" },
                new TypeTextStep { Text = "hello", BackgroundWindowTitle = "Notepad" },
            },
        };

        Assert.Empty(MacroValidator.Validate(macro));
        Assert.Equal(17, MacroDocument.RequiredSchemaVersion(macro));

        var result = await engine.RunAsync(macro);
        Assert.Equal(PlaybackOutcome.Completed, result.Outcome);
        Assert.Equal(new[]
        {
            "bgclick \"Notepad\" 120,240 Left Click",
            "bgkey \"Notepad\" 65 Press",
            "bgtext \"Notepad\" hello",
        }, input.Calls);
    }

    [Fact]
    public void DiskSpaceGuard_and_AppPaths_CLI_parser_work()
    {
        Assert.True(DiskSpaceGuard.HasEnoughFreeSpace(AppContext.BaseDirectory, out var freeBytes, minFreeBytes: 1024));
        Assert.True(freeBytes > 0);
        Assert.Throws<IOException>(() =>
            DiskSpaceGuard.EnsureEnoughFreeSpace(AppContext.BaseDirectory, "unit test", minFreeBytes: long.MaxValue));

        Assert.True(AppPaths.TryParseCliRunRequest(
            new[] { "--tray", "--run", "Daily Farming", "--exit-after-run" },
            out var target1,
            out var exit1));
        Assert.Equal("Daily Farming", target1);
        Assert.True(exit1);

        Assert.True(AppPaths.TryParseCliRunRequest(
            new[] { "--run=\"C:\\Macros\\Test.smacro\"" },
            out var target2,
            out var exit2));
        Assert.Equal(@"C:\Macros\Test.smacro", target2);
        Assert.False(exit2);

        Assert.True(HardwareAccelerationInfo.LogicalProcessorCount >= 1);
        Assert.InRange(HardwareAccelerationInfo.MaxVisionWorkers, 1, 8);
        Assert.False(string.IsNullOrWhiteSpace(HardwareAccelerationInfo.StatusText));

        Assert.NotEmpty(ManualCatalog.Filter("background postmessage"));
        Assert.NotEmpty(ManualCatalog.Filter("math formula clamp"));
    }

    [Fact]
    public void FormulaEvaluator_handles_OCR_currency_commas_negative_variables_and_scientific_notation()
    {
        var store = new VariableStore(new[]
        {
            new MacroVariableDefinition { Name = "price", Type = MacroVariableType.Text, DefaultValue = "$1,250.50" },
            new MacroVariableDefinition { Name = "offset", Type = MacroVariableType.Number, DefaultValue = "-15" },
            new MacroVariableDefinition { Name = "isBoosted", Type = MacroVariableType.Boolean, DefaultValue = "true" },
        });

        // Comma in "$1,250.50" must not be confused with function argument separator in min(...)
        Assert.Equal(1250.5, FormulaEvaluator.Evaluate("min({price}, 2000)", store), 3);
        // Double unary minus with negative variable: -({offset}) = +15
        Assert.Equal(25.0, FormulaEvaluator.Evaluate("10 - {offset}", store), 3);
        Assert.Equal(15.0, FormulaEvaluator.Evaluate("-{offset}", store), 3);
        // Boolean variable & scientific notation
        Assert.Equal(1500.0, FormulaEvaluator.Evaluate("1.5e3 * {isBoosted}", store), 3);
        Assert.Equal(1.0, FormulaEvaluator.Evaluate("true + false", store), 3);
    }

    [Fact]
    public void Background_click_with_drag_reports_helpful_warning()
    {
        var macro = new Macro
        {
            Name = "Bg Drag Warning",
            Steps = new List<MacroStep>
            {
                new MouseClickStep
                {
                    X = 10,
                    Y = 10,
                    EndX = 100,
                    EndY = 100,
                    Action = MouseClickAction.Drag,
                    BackgroundWindowTitle = "Notepad",
                },
            },
        };

        var issues = MacroValidator.Validate(macro);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Property == nameof(MouseClickStep.BackgroundWindowTitle));
    }
}
