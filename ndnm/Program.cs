using Microsoft.Extensions.DependencyInjection;

namespace Ndnm;

internal static class Program {
    private static Task<int> Main(string[] args) {
        ServiceCollection sc = new();

        sc.AddSingleton<HttpClient>();

        CommandApp app = new(new TypeRegistrar(sc));

        app.Configure(static c => {
            c.AddCommand<InstallCommand>("install").WithDescription("Installs a .NET version.").WithExample("install 10.0.100".Split(' '));
            c.UseAssemblyInformationalVersion();
#if DEBUG
            c.PropagateExceptions();
            c.ValidateExamples();
#endif
        });

        return app.RunAsync(args);
    }
}
