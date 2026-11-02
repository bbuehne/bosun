using Bosun.UI;

namespace Bosun.Tests.UI;

/// <summary>
/// bs-ww9.1.3: when the main window's "No hosts configured yet" panel shows. Pure view-model tests;
/// no WPF, no config path on disk.
/// </summary>
public sealed class HostsEmptyStateViewModelTests
{
    private const string FictionalPath = @"C:\Users\example\AppData\Local\Bosun\hosts.toml";

    [Fact]
    public void IsHiddenUntilTheFirstUpdate()
    {
        var vm = new HostsEmptyStateViewModel();

        Assert.False(vm.IsVisible);
        Assert.False(vm.CanAddHost);
    }

    [Fact]
    public void IsVisible_WhenTheLoadedConfigHasZeroHosts()
    {
        var vm = new HostsEmptyStateViewModel();

        vm.Update(configuredHostCount: 0, canAddHost: true);

        Assert.True(vm.IsVisible);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void IsHidden_WhenAnyHostIsConfigured(int count)
    {
        var vm = new HostsEmptyStateViewModel();

        vm.Update(count, canAddHost: true);

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void IsHidden_WhenNoConfigIsInService_SoItNeverClaimsNoHostsAboutAFileThatDidNotLoad()
    {
        var vm = new HostsEmptyStateViewModel();

        vm.Update(configuredHostCount: null, canAddHost: true);

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void FollowsTheHostCount_AsHostsAreAddedAndRemoved()
    {
        var vm = new HostsEmptyStateViewModel();

        vm.Update(0, canAddHost: true);
        Assert.True(vm.IsVisible);
        vm.Update(1, canAddHost: true);
        Assert.False(vm.IsVisible);
        vm.Update(0, canAddHost: true);
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void CanAddHost_FollowsTheEditorBeingAvailable_AndStillShowsThePanelWithout()
    {
        var vm = new HostsEmptyStateViewModel();

        vm.Update(0, canAddHost: false);

        Assert.True(vm.IsVisible);
        Assert.False(vm.CanAddHost);
    }

    [Fact]
    public void PropertyChanged_IsRaisedOnlyWhenAValueChanges()
    {
        var vm = new HostsEmptyStateViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Update(0, canAddHost: false);
        vm.Update(0, canAddHost: false);

        Assert.Equal([nameof(HostsEmptyStateViewModel.IsVisible)], raised);
    }

    [Fact]
    public void UseHostsFile_ExposesTheFullPath()
    {
        var vm = new HostsEmptyStateViewModel();

        vm.UseHostsFile(FictionalPath);

        Assert.Equal(FictionalPath, vm.HostsFilePath);
    }

    [Fact]
    public void UseHostsFile_RejectsAnEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new HostsEmptyStateViewModel().UseHostsFile(""));
    }

    [Fact]
    public void TheWording_NamesTheStateAndTheNextStep_WithoutBlamingTheUser()
    {
        var vm = new HostsEmptyStateViewModel();

        Assert.Equal("No hosts configured yet", vm.Title);
        Assert.Contains("Add a host", vm.Detail);
        Assert.DoesNotContain("forgot", vm.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing", vm.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", vm.Detail, StringComparison.OrdinalIgnoreCase);
    }
}
