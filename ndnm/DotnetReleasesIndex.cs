namespace Ndnm;

internal readonly record struct DotnetReleasesIndex {
    [JsonPropertyName("releases-index")]
    public required DotnetChannelReleasesIndex[] Releases { get; init; }
}
