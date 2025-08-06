namespace Ndnm;

[JsonConverter(typeof(JsonStringEnumConverter<ReleaseType>))]
internal enum ReleaseType {
    Sts,
    Lts
}
