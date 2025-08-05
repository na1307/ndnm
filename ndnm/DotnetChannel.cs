namespace Ndnm;

internal sealed record class DotnetChannel : DotnetChannelBase {
    [JsonPropertyName("releases")]
    public required DotnetRelease[] Releases { get; init; }
}
