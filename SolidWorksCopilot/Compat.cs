// Compat.cs — polyfill for C# 9+ features on .NET Framework 4.8.
//
// `record` types and `init`-only setters require the compiler to find
// System.Runtime.CompilerServices.IsExternalInit, which net48 does not ship.
// Declaring it ourselves is the standard, supported workaround.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
