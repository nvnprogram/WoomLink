using System.Collections.Generic;
using System.IO;
using WoomLink.sead;

namespace WoomLink.Converter;

/// <summary>
/// Extracts LinkUserName values from actor YAML files and builds
/// a CRC32 hash-to-name lookup table for resolving xlink user hashes.
/// </summary>
public static class ActorYamlParser
{
    private static readonly string[] KeysToHash = { "LinkUserName:", "Name:" };

    public static Dictionary<uint, string> Parse(string path)
    {
        var map = new Dictionary<uint, string>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            foreach (var key in KeysToHash)
            {
                if (!trimmed.StartsWith(key)) continue;

                var value = trimmed[key.Length..].Trim();
                if (value.Length == 0) continue;

                uint hash = HashCrc32.CalcStringHash(value);
                map.TryAdd(hash, value);
            }
        }
        return map;
    }
}
