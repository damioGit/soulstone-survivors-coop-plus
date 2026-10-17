using Mono.Cecil;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: ApiDump <dllPath> <searchDir> [typeNameFilterCsv]");
    return 1;
}

string dllPath = args[0];
string searchDir = args[1];
string[]? filters = args.Length > 2
    ? args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    : null;

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(searchDir);
var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters { AssemblyResolver = resolver });

bool IsBoilerplateMember(string name) =>
    name.StartsWith("Native") || name.StartsWith("<") || name == "NativeClassPtr";

bool IsCompilerGeneratedType(TypeDefinition t) =>
    t.Name.Contains("d__") || t.Name.StartsWith("<") || t.Name.Contains("c__DisplayClass");

string TypeName(TypeReference t)
{
    if (t.IsGenericInstance)
    {
        var gi = (GenericInstanceType)t;
        var args = string.Join(", ", gi.GenericArguments.Select(TypeName));
        return $"{gi.ElementType.Name.Split('`')[0]}<{args}>";
    }
    return t.Name;
}

if (filters == null)
{
    // Census mode: just list public type names + base type, sorted.
    var types = module.Types
        .Where(t => t.IsPublic && !IsCompilerGeneratedType(t))
        .OrderBy(t => t.Name)
        .ToList();
    foreach (var t in types)
    {
        string baseType = t.BaseType != null && t.BaseType.Name != "Object" ? $" : {TypeName(t.BaseType)}" : "";
        Console.WriteLine($"{t.Name}{baseType}");
    }
    Console.Error.WriteLine($"-- {types.Count} public types --");
    return 0;
}

// Detail mode: dump members of matching types.
var matched = module.Types
    .Where(t => !IsCompilerGeneratedType(t) && filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
    .OrderBy(t => t.Name)
    .ToList();

foreach (var t in matched)
{
    string baseType = t.BaseType != null && t.BaseType.Name != "Object" ? $" : {TypeName(t.BaseType)}" : "";
    Console.WriteLine($"\nclass {t.Name}{baseType}");

    foreach (var f in t.Fields.Where(f => (f.IsPublic || f.IsFamily) && !IsBoilerplateMember(f.Name)))
    {
        string mod = f.IsStatic ? "static " : "";
        Console.WriteLine($"  field {mod}{TypeName(f.FieldType)} {f.Name}");
    }

    foreach (var p in t.Properties.Where(p => !IsBoilerplateMember(p.Name)))
    {
        bool pub = (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false);
        if (!pub) continue;
        string mod = (p.GetMethod?.IsStatic ?? p.SetMethod?.IsStatic ?? false) ? "static " : "";
        string acc = (p.GetMethod != null ? "get;" : "") + (p.SetMethod != null ? "set;" : "");
        Console.WriteLine($"  prop {mod}{TypeName(p.PropertyType)} {p.Name} {{ {acc} }}");
    }

    foreach (var m in t.Methods.Where(m => m.IsPublic && !m.IsGetter && !m.IsSetter && !IsBoilerplateMember(m.Name) && m.Name != ".cctor"))
    {
        string mod = m.IsStatic ? "static " : "";
        string ps = string.Join(", ", m.Parameters.Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));
        string name = m.Name == ".ctor" ? t.Name : m.Name;
        Console.WriteLine($"  method {mod}{TypeName(m.ReturnType)} {name}({ps})");
    }
}

Console.Error.WriteLine($"-- {matched.Count} matched types --");
return 0;
