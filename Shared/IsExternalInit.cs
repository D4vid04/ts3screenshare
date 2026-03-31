// Polyfill: enables use of C# records and init setters in netstandard2.0
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
