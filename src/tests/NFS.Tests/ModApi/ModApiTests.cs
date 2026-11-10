using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using NFS.ModApi;

namespace NFS.Tests.ModApi;

public class ModApiTests
{
    private static readonly Assembly Api = typeof(ModApiInfo).Assembly;

    [Fact]
    public void PublicApiMatchesApprovedSnapshot()
    {
        string actual = Describe(Api);
        string path = Path.Combine(AppContext.BaseDirectory, "ModApi", "ModApi.approved.txt");
        string? update = Environment.GetEnvironmentVariable("MODAPI_UPDATE_SNAPSHOT");
        if (!string.IsNullOrEmpty(update))
        {
            File.WriteAllText(SourceSnapshotPath(), actual);
            return;
        }
        string expected = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(expected == actual,
            "NFS.ModApi public API changed. If intended (bump ModApiInfo.Version accordingly), regenerate the snapshot: " +
            "set MODAPI_UPDATE_SNAPSHOT=1, run this test, review the diff of src/tests/NFS.Tests/ModApi/ModApi.approved.txt.\n" +
            "--- actual ---\n" + actual);
    }

    [Fact]
    public void ModApiReferencesNoNfsAssembly()
    {
        var bad = Api.GetReferencedAssemblies()
            .Where(a => a.Name!.StartsWith("NFS.", StringComparison.Ordinal)).Select(a => a.Name).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void VersionIsSemver()
    {
        Assert.Equal("1.1.0", ModApiInfo.Version);
        Assert.True(System.Version.TryParse(ModApiInfo.Version, out _));
    }

    private static string SourceSnapshotPath([CallerFilePath] string file = "")
        => Path.Combine(Path.GetDirectoryName(file)!, "ModApi.approved.txt");

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static string Describe(Assembly asm)
    {
        var sb = new StringBuilder();
        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            sb.Append(TypeHeader(t)).Append('\n');
            var lines = new List<string>();
            foreach (var m in t.GetMembers(All))
            {
                if (!IsVisible(m)) continue;
                if (m is MethodInfo mi && mi.IsSpecialName) continue;
                if (m is Type) continue;
                if (m.Name.StartsWith('<') || m.Name is "PrintMembers" or "EqualityContract") continue; // record plumbing
                lines.Add("  " + MemberText(m));
            }
            foreach (var l in lines.Order(StringComparer.Ordinal)) sb.Append(l).Append('\n');
        }
        return sb.ToString();
    }

    private static bool IsVisible(MemberInfo m) => m switch
    {
        MethodBase b => b.IsPublic || b.IsFamily || b.IsFamilyOrAssembly,
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyInfo p => (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false)
            || (p.GetMethod?.IsFamily ?? false) || (p.SetMethod?.IsFamily ?? false),
        EventInfo e => e.AddMethod?.IsPublic ?? false,
        _ => false,
    };

    private static string TypeHeader(Type t)
    {
        string kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct"
            : t.IsAbstract && t.IsSealed ? "static class" : t.IsAbstract ? "abstract class" : t.IsSealed ? "sealed class" : "class";
        string s = $"{kind} {t.FullName}";
        var bases = new List<string>();
        if (t.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum)) bases.Add(Name(b));
        bases.AddRange(t.GetInterfaces().Select(Name).Order(StringComparer.Ordinal));
        return bases.Count > 0 ? s + " : " + string.Join(", ", bases) : s;
    }

    private static string MemberText(MemberInfo m) => m switch
    {
        ConstructorInfo c => $"ctor({Params(c)})",
        MethodInfo mi => $"{(mi.IsStatic ? "static " : "")}{Name(mi.ReturnType)} {mi.Name}({Params(mi)})",
        PropertyInfo p => $"{(p.GetMethod?.IsStatic ?? p.SetMethod!.IsStatic ? "static " : "")}{Name(p.PropertyType)} {p.Name} {{ {(p.GetMethod != null ? "get; " : "")}{(p.SetMethod != null ? (IsInit(p) ? "init; " : "set; ") : "")}}}",
        FieldInfo f => f.IsLiteral
            ? $"const {Name(f.FieldType)} {f.Name} = {f.GetRawConstantValue()}"
            : $"{(f.IsStatic ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}{Name(f.FieldType)} {f.Name}",
        EventInfo e => $"event {Name(e.EventHandlerType!)} {e.Name}",
        _ => m.Name,
    };

    private static bool IsInit(PropertyInfo p)
        => p.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Any(x => x.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string Params(MethodBase m) => string.Join(", ", m.GetParameters().Select(p =>
        (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + Name(p.ParameterType) + " " + p.Name
        + (p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "")));

    private static string Name(Type t)
    {
        if (t.IsByRef) return Name(t.GetElementType()!);
        if (t.IsArray) return Name(t.GetElementType()!) + "[]";
        if (Nullable.GetUnderlyingType(t) is { } u) return Name(u) + "?";
        if (t.IsGenericType)
            return t.GetGenericTypeDefinition().FullName!.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
        return t.FullName ?? t.Name;
    }
}
