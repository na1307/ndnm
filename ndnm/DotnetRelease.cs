namespace Ndnm;

internal readonly record struct DotnetRelease {
    [JsonPropertyName("release-date")]
    public required DateOnly ReleaseDate { get; init; }

    [JsonPropertyName("release-version")]
    public required string ReleaseVersion { get; init; }

    [JsonPropertyName("runtime")]
    public DotnetRuntime? Runtime { get; init; }

    [JsonPropertyName("sdk")]
    public required DotnetSdk Sdk { get; init; }

    [JsonPropertyName("sdks")]
    public DotnetSdk[]? Sdks { get; init; }

    [JsonPropertyName("aspnetcore-runtime")]
    public DotnetRuntime? AspNetCoreRuntime { get; init; }
}
