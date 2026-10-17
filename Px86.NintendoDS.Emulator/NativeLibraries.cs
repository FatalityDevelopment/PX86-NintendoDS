using System.Runtime.InteropServices;
using Silk.NET.Core.Loader;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Helps Silk.NET find the bundled native libraries (SDL2, OpenAL Soft).
    ///
    /// NuGet places them under <c>runtimes/&lt;rid&gt;/native</c>. With .NET 8's
    /// portable RIDs Silk.NET's own lookup can miss that folder (it then only
    /// tries the base directory and the system path), so this adds a resolver
    /// for the current RID and its generic fallback.
    /// </summary>
    public static class NativeLibraries
    {
        public static void Configure()
        {
            if (PathResolver.Default is not DefaultPathResolver resolver)
                return;

            string rid = RuntimeInformation.RuntimeIdentifier;
            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
            string[] rids = { rid, $"{os}-{arch}", os };

            resolver.Resolvers.Add(name => rids
                .Select(r => Path.Combine(AppContext.BaseDirectory, "runtimes", r, "native", name))
                .Where(File.Exists));
        }
    }
}
