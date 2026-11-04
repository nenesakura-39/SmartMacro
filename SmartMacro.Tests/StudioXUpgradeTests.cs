using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.Core.Workflow;
using Xunit;

namespace SmartMacro.Tests;

public sealed class StudioXUpgradeTests
{
    [Fact]
    public void AppSettings_StudioXDefaults_CloneAndSanitizeWork()
    {
        var defaults = new AppSettings();
        Assert.Equal(StudioTheme.ObsidianNeon, defaults.Theme);
        Assert.Equal(StudioAccent.NeonBrand, defaults.Accent);
        Assert.Equal(StudioDensity.Comfortable, defaults.Density);
        Assert.False(defaults.ReduceMotion);
        Assert.False(defaults.SidebarPinned);

        var custom = new AppSettings
        {
            Theme = StudioTheme.MidnightBlue,
            Accent = StudioAccent.CyberViolet,
            Density = StudioDensity.Compact,
            ReduceMotion = true,
            SidebarPinned = true,
        };
        var cloned = custom.Clone();
        Assert.Equal(StudioTheme.MidnightBlue, cloned.Theme);
        Assert.Equal(StudioAccent.CyberViolet, cloned.Accent);
        Assert.Equal(StudioDensity.Compact, cloned.Density);
        Assert.True(cloned.ReduceMotion);
        Assert.True(cloned.SidebarPinned);

        var invalid = new AppSettings
        {
            Theme = (StudioTheme)999,
            Accent = (StudioAccent)(-5),
            Density = (StudioDensity)42,
        };
        invalid.Sanitize();
        Assert.Equal(StudioTheme.ObsidianNeon, invalid.Theme);
        Assert.Equal(StudioAccent.NeonBrand, invalid.Accent);
        Assert.Equal(StudioDensity.Comfortable, invalid.Density);
    }

    [Fact]
    public async Task JsonSettingsStore_RoundTripsStudioXAppearanceSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartMacro_StudioX_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsFile = Path.Combine(tempDir, "settings.json");
            var store = new JsonSettingsStore(settingsFile);

            var saved = new AppSettings
            {
                Theme = StudioTheme.MidnightBlue,
                Accent = StudioAccent.EmeraldPulse,
                Density = StudioDensity.Compact,
                ReduceMotion = true,
                SidebarPinned = false,
            };
            await store.SaveAsync(saved);

            var loaded = await store.LoadAsync();
            Assert.Null(loaded.Warning);
            Assert.Equal(StudioTheme.MidnightBlue, loaded.Settings.Theme);
            Assert.Equal(StudioAccent.EmeraldPulse, loaded.Settings.Accent);
            Assert.Equal(StudioDensity.Compact, loaded.Settings.Density);
            Assert.True(loaded.Settings.ReduceMotion);
            Assert.False(loaded.Settings.SidebarPinned);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void StudioCommandCatalog_SupportsBrainAndBlueprintQuickCommands()
    {
        var variableCommands = StudioCommandCatalog.Filter("variable");
        Assert.Contains(variableCommands, c => c.Step == StepKind.SetVariable);

        var checkpointCommands = StudioCommandCatalog.Filter("checkpoint");
        Assert.Contains(checkpointCommands, c => c.Step == StepKind.Checkpoint);

        var subMacroCommands = StudioCommandCatalog.Filter("sub-macro");
        Assert.Contains(subMacroCommands, c => c.Step == StepKind.CallMacro);

        var blueprints = StudioCommandCatalog.Filter("blueprint");
        Assert.Equal(10, blueprints.Count);
    }

    [Fact]
    public void MacroBlueprints_AllBuildNonEmptyBalancedStepLists()
    {
        Assert.Equal(10, MacroBlueprints.All.Count);
        foreach (var bp in MacroBlueprints.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(bp.Title));
            var steps = MacroBlueprints.Build(bp.Blueprint);
            Assert.NotEmpty(steps);
        }
    }
}
