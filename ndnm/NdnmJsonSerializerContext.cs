namespace Ndnm;

[JsonSerializable(typeof(DotnetReleasesIndex))]
[JsonSerializable(typeof(DotnetChannel))]
internal sealed partial class NdnmJsonSerializerContext : JsonSerializerContext;
