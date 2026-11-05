using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
namespace ConectaTI.App.Models;

public partial class RdpOptions : ObservableObject
{
    [ObservableProperty] private string _gatewayHostname = "";
    [ObservableProperty] private int _gatewayUsageMethod = 2;
    [ObservableProperty] private int _gatewayCredentialsSource = 4;
    [ObservableProperty] private int _gatewayProfileUsageMethod = 1;
    [ObservableProperty] private bool _promptCredentialOnce = true;
    [ObservableProperty] private string _additionalProperties = "";
    [ObservableProperty] private string _originalContent = "";
    public RdpOptions Clone() => JsonSerializer.Deserialize<RdpOptions>(JsonSerializer.Serialize(this))!;
    public bool SameValues(RdpOptions other) => JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
}
