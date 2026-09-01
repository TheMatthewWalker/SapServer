// net48's BCL predates C# 9's `init` accessors and doesn't ship the marker
// type the compiler needs for them (System.Runtime.CompilerServices.IsExternalInit).
// Standard polyfill — this project uses `{ get; init; }` extensively across
// Models/, Configuration/, etc. See interop-stubs/SapNco.DevStub's copy of
// the same shim for why every net48 project needs its own.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
