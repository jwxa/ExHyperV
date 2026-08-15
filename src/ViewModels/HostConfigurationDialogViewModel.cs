using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services.Remote.Configuration;
using ExHyperV.Services.Remote.Preflight;
using ExHyperV.Services.Remote.Profiles;

namespace ExHyperV.ViewModels;

public partial class HostConfigurationDialogViewModel(
    HostProfile profile,
    HostPreflightPlan approvedPlan) : ObservableObject
{
    [ObservableProperty] private string _confirmationText = string.Empty;

    public string TargetText => $"{profile.DisplayName} · {profile.Address}";
    public string ChangeCountText => $"将应用 {approvedPlan.Changes.Count} 项已审查修改";
    public IReadOnlyList<HostPreflightChangeItemViewModel> PlannedChanges { get; } =
        approvedPlan.Changes.Select((change, index) => new HostPreflightChangeItemViewModel(index + 1, change)).ToArray();
    public bool IsConfirmationExact => HostConfigurationConfirmation.IsExact(ConfirmationText);

    partial void OnConfirmationTextChanged(string value) => OnPropertyChanged(nameof(IsConfirmationExact));
}
