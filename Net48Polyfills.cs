// net48's BCL predates two .NET Core convenience APIs this codebase's
// Helpers/* rely on throughout: foreach-deconstruction of KeyValuePair, and
// Dictionary.GetValueOrDefault. The syntax itself still compiles under
// LangVersion=latest — only the underlying BCL members are missing — so
// these extension methods restore the exact same call sites unchanged
// rather than rewriting dozens of call sites across Helpers/ that have
// nothing to do with the NCo/net48 migration itself. GlobalUsings.cs imports
// this namespace project-wide. (Math.Clamp and Task.IsCompletedSuccessfully
// had only one call site each — fixed directly at the call site instead of
// polyfilled, since neither can be shimmed as an extension method: you
// cannot extend a static class, and C# has no extension-property syntax.)
namespace SapServer.Net48Polyfills
{
    using System.Collections.Generic;

    internal static class DictionaryPolyfillExtensions
    {
        // foreach (var (key, value) in someDictionary) — deconstruction lowers
        // to a Deconstruct call the real .NET Core BCL adds directly to
        // KeyValuePair<TKey,TValue>; net48's copy of that struct has no such
        // method, so C# falls back to extension-method lookup, which this
        // satisfies.
        public static void Deconstruct<TKey, TValue>(
            this KeyValuePair<TKey, TValue> kvp, out TKey key, out TValue value)
        {
            key   = kvp.Key;
            value = kvp.Value;
        }

        // Defined only for IReadOnlyDictionary (matching the real
        // System.Collections.Generic.CollectionExtensions.GetValueOrDefault
        // shape) — Dictionary<K,V> implements this interface too, so a plain
        // Dictionary call site resolves here without ambiguity. Adding a
        // second overload for IDictionary<K,V> would make every Dictionary<K,V>
        // call site ambiguous between the two, since Dictionary implements both.
        public static TValue? GetValueOrDefault<TKey, TValue>(
            this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
            => dictionary.TryGetValue(key, out var value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(
            this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
            => dictionary.TryGetValue(key, out var value) ? value : defaultValue;

        // No IReadOnlyDictionary ambiguity risk here — TryAdd only makes
        // sense on a mutable dictionary, so this is IDictionary-only.
        public static bool TryAdd<TKey, TValue>(
            this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key)) return false;
            dictionary.Add(key, value);
            return true;
        }
    }
}
