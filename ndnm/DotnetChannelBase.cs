namespace Ndnm;

internal abstract record class DotnetChannelBase {
    [JsonPropertyName("channel-version")]
    public required string ChannelVersion { get; init; }

    [JsonPropertyName("latest-release")]
    public required string LatestRelease { get; init; }

    [JsonPropertyName("latest-release-date")]
    public required DateOnly LatestReleaseDate { get; init; }

    [JsonPropertyName("latest-runtime")]
    public required string LatestRuntime { get; init; }

    [JsonPropertyName("latest-sdk")]
    public required string LatestSdk { get; init; }

    [JsonPropertyName("support-phase")]
    public required SupportPhase SupportPhase { get; init; }

    [JsonPropertyName("release-type")]
    public required ReleaseType ReleaseType { get; init; }
}
