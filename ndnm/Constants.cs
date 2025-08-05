using System.Runtime.InteropServices;

namespace Ndnm;

internal static class Constants {
    public const string NdnmName = "ndnm";
    public static readonly string NdnmPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), NdnmName);

    public static string OSName {
        get {
            if (OperatingSystem.IsLinux()) {
                return "linux";
            }

            throw new PlatformNotSupportedException();
        }
    }

    public static string OSArch
        => RuntimeInformation.OSArchitecture switch {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException(),
        };
}
