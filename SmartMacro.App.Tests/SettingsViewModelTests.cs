using SmartMacro.Core.Models;
using SmartMacro.Core.Storage;
using SmartMacro.ViewModels;

namespace SmartMacro.AppTests;

public sealed class SettingsViewModelTests
{
    private static (SettingsViewModel Vm, FakeSettingsPersistence Store, List<AppSettings> Applied) CreateHarness()
    {
        var store = new FakeSettingsPersistence();
        var log = new FakeLogService();
        var dialogs = new FakeDialogService();
        var root = Path.Combine(Path.GetTempPath(), "SmartMacroAppTests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var applied = new List<AppSettings>();

        var vm = new SettingsViewModel(
            store,
            log,
            dialogs,
            paths,
            s => applied.Add(s),
            (_, _) => { });
        return (vm, store, applied);
    }

    [Fact]
    public async Task QuickSetAsync_UpdatesSavedSettingWithoutSavingUnsavedFormEdits()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var (vm, store, applied) = CreateHarness();
            await vm.LoadAsync();

            vm.DefaultDelayMs = 777;
            Assert.True(vm.IsDirty);

            await vm.QuickSetAsync(s => s.ShowRunOverlay = false);

            Assert.Equal(1, store.SaveCount);
            Assert.False(store.Stored.ShowRunOverlay);
            Assert.NotEqual(777, store.Stored.DefaultDelayMs);
            Assert.Equal(777, vm.DefaultDelayMs);
            Assert.False(vm.ShowRunOverlay);
            Assert.NotEmpty(applied);
        });
    }

    [Fact]
    public async Task ProfileRules_ValidateAndSaveThroughSaveCommand()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var (vm, store, _) = CreateHarness();
            await vm.LoadAsync();

            vm.AddProfileRuleCommand.Execute(null);
            var rule = Assert.Single(vm.ProfileRules);

            Assert.True(rule.HasValidationError);
            Assert.True(vm.HasRulesError);
            Assert.False(vm.IsValid);

            rule.WindowTitleMatch = "Notepad";
            rule.ProfileName = "Productivity";

            Assert.False(rule.HasValidationError);
            Assert.False(vm.HasRulesError);
            Assert.True(vm.IsValid);

            await TestCommandHelper.RunAsync(vm.SaveCommand);
            Assert.False(vm.IsDirty);
            Assert.NotNull(store.Stored.ProfileRules);
            var savedRule = Assert.Single(store.Stored.ProfileRules!);
            Assert.Equal("Notepad", savedRule.WindowTitleMatch);
            Assert.Equal("Productivity", savedRule.ProfileName);
        });
    }

    [Fact]
    public async Task DefaultDelayMs_OutOfBoundsMarksInvalidWithError()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var (vm, _, _) = CreateHarness();
            await vm.LoadAsync();

            vm.DefaultDelayMs = -10;
            Assert.False(vm.IsValid);
            Assert.NotNull(vm.DelayError);

            vm.DefaultDelayMs = AppSettings.MaxDefaultDelayMs + 1;
            Assert.False(vm.IsValid);
            Assert.NotNull(vm.DelayError);

            vm.DefaultDelayMs = 200;
            Assert.True(vm.IsValid);
            Assert.Null(vm.DelayError);
        });
    }
}
