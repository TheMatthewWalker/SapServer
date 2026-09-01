// net48's BCL predates C# 9's `init` accessors and doesn't ship the marker
// type the compiler needs for them (System.Runtime.CompilerServices.IsExternalInit).
// This is the standard, widely-used polyfill — every net48 project in this
// solution that uses `{ get; init; }` properties needs its own copy, since
// the type must exist in whichever assembly's code actually declares an
// init-only property.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
