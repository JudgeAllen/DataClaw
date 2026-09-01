global using TwsHistory.Core.ProtobufCompat;

// Compatibility shim for the IB API's protoc-3.29 generated code, which calls
// MapField<TKey,TValue>.MergeFrom(...) — an API added in Google.Protobuf 3.16.
// This machine only has Google.Protobuf 3.15.8 in the offline NuGet cache, so
// the missing method is provided here as an extension. The global using above
// makes it visible to every compilation unit, including the generated files.
//
// Semantics match the upstream implementation: entries of "other" are added,
// and entries with an existing key replace the stored value.
namespace TwsHistory.Core.ProtobufCompat
{
    using Google.Protobuf.Collections;

    public static class MapFieldExtensions
    {
        public static void MergeFrom<TKey, TValue>(this MapField<TKey, TValue> map, MapField<TKey, TValue> other)
        {
            if (other == null) return;
            foreach (var kv in other)
            {
                if (map.ContainsKey(kv.Key))
                    map[kv.Key] = kv.Value;
                else
                    map.Add(kv.Key, kv.Value);
            }
        }
    }
}
