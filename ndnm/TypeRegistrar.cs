using Microsoft.Extensions.DependencyInjection;

namespace Ndnm;

internal sealed class TypeRegistrar(IServiceCollection sc) : ITypeRegistrar {
    public void Register(Type service, Type implementation) => sc.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => sc.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => sc.AddSingleton(service, _ => factory());

    public ITypeResolver Build() => new TypeResolver(sc.BuildServiceProvider());
}
