namespace Ndnm;

internal sealed record class DotnetChannelReleasesIndex : DotnetChannelBase {
    [JsonPropertyName("releases.json")]
    public required Uri ReleasesJson { get; init; }
}
