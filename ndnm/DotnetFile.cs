namespace Ndnm;

internal readonly record struct DotnetFile {
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("rid")]
    public string? RuntimeIdentifier { get; init; }

    [JsonPropertyName("url")]
    public required Uri Url { get; init; }

    [JsonPropertyName("hash")]
    public required string Sha512Hash { get; init; }
}
