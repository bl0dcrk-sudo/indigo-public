using System.Text.Json;
using System.Text.RegularExpressions;
using NFS.ModApi;

namespace NFS.Game.Mods;

/// <summary>Parses and validates <c>mod.json</c> manifests. Unknown top-level fields are ignored.</summary>
public static class ModManifestParser
{
    private const string ReservedIdPrefix = "nfs2se";
    private const int MinIdLength = 3;
    private const int MaxIdLength = 64;

    private static readonly Regex IdPattern = new(@"^[a-z0-9_-]+\.[a-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant);
    private static readonly Regex ApiRangePattern = new(@"^\^([0-9]+)\.([0-9]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex ApiVersionPattern = new(@"^([0-9]+)\.([0-9]+)(\.[0-9]+)?$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownCapabilities = new(StringComparer.Ordinal) { "cars" };

    /// <summary>
    /// Parses <paramref name="json"/> as a mod manifest. On failure returns false and sets
    /// <paramref name="error"/> to a short English message naming the offending field.
    /// </summary>
    public static bool TryParse(string json, out ModManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            error = "invalid JSON: " + e.Message;
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "manifest must be a JSON object";
                return false;
            }

            // id
            var id = RequireString(root, "id", out error);
            if (id is null) return false;
            if (id.Length < MinIdLength || id.Length > MaxIdLength || !IdPattern.IsMatch(id))
            {
                error = "field 'id' must be 3-64 characters of lowercase letters, digits, '.', '-', '_' in the form author.name";
                return false;
            }
            if (id.StartsWith(ReservedIdPrefix, StringComparison.Ordinal))
            {
                error = $"field 'id' must not start with reserved prefix '{ReservedIdPrefix}'";
                return false;
            }

            // version
            var version = RequireString(root, "version", out error);
            if (version is null) return false;
            if (!VersionPattern.IsMatch(version))
            {
                error = $"field 'version' must be semver MAJOR.MINOR.PATCH (got '{version}')";
                return false;
            }

            // api
            var api = RequireString(root, "api", out error);
            if (api is null) return false;
            if (!ApiRangePattern.IsMatch(api))
            {
                error = $"field 'api' must be in the form ^MAJOR.MINOR (got '{api}')";
                return false;
            }
            if (!IsApiCompatible(api, ModApiInfo.Version))
            {
                error = $"field 'api' {api} is incompatible with mod API {ModApiInfo.Version}";
                return false;
            }

            // kind
            var kindText = RequireString(root, "kind", out error);
            if (kindText is null) return false;
            ModKind kind;
            switch (kindText)
            {
                case "data":
                    kind = ModKind.Data;
                    break;
                case "code":
                    error = "code mods are not supported";
                    return false;
                default:
                    error = $"field 'kind' must be 'data' (got '{kindText}')";
                    return false;
            }

            // capabilities
            if (!root.TryGetProperty("capabilities", out var capsElement))
            {
                error = "field 'capabilities' is required";
                return false;
            }
            if (capsElement.ValueKind != JsonValueKind.Array)
            {
                error = "field 'capabilities' must be an array of strings";
                return false;
            }
            if (capsElement.GetArrayLength() == 0)
            {
                error = "field 'capabilities' must not be empty";
                return false;
            }
            var capabilities = new List<string>(capsElement.GetArrayLength());
            foreach (var item in capsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error = "field 'capabilities' must be an array of strings";
                    return false;
                }
                var cap = item.GetString()!;
                if (!KnownCapabilities.Contains(cap))
                {
                    error = $"field 'capabilities' has unknown capability '{cap}'";
                    return false;
                }
                capabilities.Add(cap);
            }

            manifest = new ModManifest(id, version, api, kind, capabilities);
            return true;
        }
    }

    /// <summary>
    /// True when a mod declaring <paramref name="range"/> (form <c>^MAJOR.MINOR</c>) can run on an API
    /// with version <paramref name="apiVersion"/> (semver): MAJOR must be equal and the range MINOR must
    /// not exceed the API MINOR. Malformed input is reported as incompatible.
    /// </summary>
    public static bool IsApiCompatible(string range, string apiVersion)
    {
        var r = ApiRangePattern.Match(range);
        var a = ApiVersionPattern.Match(apiVersion);
        if (!r.Success || !a.Success) return false;

        if (!int.TryParse(r.Groups[1].Value, out var rangeMajor) || !int.TryParse(r.Groups[2].Value, out var rangeMinor))
            return false;
        if (!int.TryParse(a.Groups[1].Value, out var apiMajor) || !int.TryParse(a.Groups[2].Value, out var apiMinor))
            return false;

        return rangeMajor == apiMajor && rangeMinor <= apiMinor;
    }

    private static string? RequireString(JsonElement root, string field, out string? error)
    {
        error = null;
        if (!root.TryGetProperty(field, out var element))
        {
            error = $"field '{field}' is required";
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"field '{field}' must be a string";
            return null;
        }
        return element.GetString();
    }
}
