namespace Ndnm;

internal readonly record struct DotnetSdk {
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("version-display")]
    public required string DisplayVersion { get; init; }

    [JsonPropertyName("runtime-version")]
    public string? RuntimeVersion { get; init; }

    [JsonPropertyName("files")]
    public required DotnetFile[] Files { get; init; }
}
