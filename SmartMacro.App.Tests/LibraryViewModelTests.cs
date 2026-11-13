using SmartMacro.Core.Models;
using SmartMacro.ViewModels;

namespace SmartMacro.AppTests;

public sealed class LibraryViewModelTests
{
    private static (LibraryViewModel Vm, FakeMacroRepository Repo, FakeDialogService Dialogs, List<Guid> DeletedIds) CreateHarness(
        bool confirmBeforeDelete = false)
    {
        var repo = new FakeMacroRepository();
        var log = new FakeLogService();
        var dialogs = new FakeDialogService();
        var deletedIds = new List<Guid>();
        var vm = new LibraryViewModel(
            repo,
            log,
            dialogs,
            () => confirmBeforeDelete,
            _ => Task.CompletedTask,
            id => deletedIds.Add(id),
            (_, _) => { });
        return (vm, repo, dialogs, deletedIds);
    }

    [Fact]
    public async Task LoadAsync_AppliesSortModesKeepingPinnedFirst()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var (vm, repo, _, _) = CreateHarness();
            var m1 = new Macro { Name = "Bravo", IsPinned = false, Steps = [new DelayStep { DurationMs = 100 }] };
            var m2 = new Macro { Name = "Alpha", IsPinned = false, Steps = [new DelayStep(), new DelayStep(), new DelayStep()] };
            var m3 = new Macro { Name = "Zeta", IsPinned = true, Steps = [new DelayStep()] };
            repo.Seed(m1, m2, m3);

            await vm.LoadAsync();

            var byPinnedAndName = vm.ItemsView.Cast<MacroListItemViewModel>().Select(x => x.Name).ToList();
            Assert.Equal(["Zeta", "Alpha", "Bravo"], byPinnedAndName);

            vm.SortMode = LibrarySortMode.MostSteps;
            var byMostSteps = vm.ItemsView.Cast<MacroListItemViewModel>().Select(x => x.Name).ToList();
            Assert.Equal(["Zeta", "Alpha", "Bravo"], byMostSteps);
        });
    }

    [Fact]
    public async Task TogglePinAndRename_ValidateAndPersist()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var (vm, repo, _, _) = CreateHarness();
            var macro = new Macro { Name = "Daily Farm", IsPinned = false };
            repo.Seed(macro);
            await vm.LoadAsync();

            var item = Assert.Single(vm.Items);
            vm.SelectedItem = item;
            await TestCommandHelper.RunAsync(vm.TogglePinCommand);
            Assert.True(vm.Items[0].IsPinned);
            Assert.True(repo.FindStored(macro.Id)!.IsPinned);

            var blankOk = await vm.RenameMacroAsync(vm.Items[0], "   ");
            Assert.False(blankOk);

            var renamedOk = await vm.RenameMacroAsync(vm.Items[0], "Nightly Farm");
            Assert.True(renamedOk);
            Assert.Equal("Nightly Farm", vm.Items[0].Name);
            Assert.Equal("Nightly Farm", repo.FindStored(macro.Id)!.Name);
        });
    }

    [Fact]
    public async Task DeleteAndTrashRestore_RoundTripCleanly()
    {
        await StaTestRunner.RunAsync(async () =>
        {
            var (vm, repo, _, deletedIds) = CreateHarness(confirmBeforeDelete: false);
            var macro = new Macro { Name = "Temporary Macro" };
            repo.Seed(macro);
            await vm.LoadAsync();

            vm.SelectedItem = Assert.Single(vm.Items);
            await TestCommandHelper.RunAsync(vm.DeleteCommand);

            Assert.Empty(vm.Items);
            Assert.Contains(macro.Id, deletedIds);

            await vm.RefreshTrashAsync();
            var trashed = Assert.Single(vm.TrashItems);
            Assert.Equal("Temporary Macro", trashed.Name);

            await TestCommandHelper.RunAsync(vm.RestoreTrashItemCommand, trashed);

            Assert.Single(vm.Items);
            Assert.Equal("Temporary Macro", vm.Items[0].Name);
            Assert.Empty(vm.TrashItems);
        });
    }
}
