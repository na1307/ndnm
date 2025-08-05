namespace Ndnm;

internal sealed class TypeResolver(IServiceProvider sp) : ITypeResolver {
    public object? Resolve(Type? type) => sp.GetService(type!);
}
