using System.Text.Json;
using CollectionServer.Services;

// Keep fixtures inside the explicit scratch directory; never use the real source tree.
if (args.Length != 1) throw new ArgumentException("Pass an empty scratch directory.");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new IOException("Refusing to overwrite a fixture directory.");
Directory.CreateDirectory(Path.Combine(root, "handlers"));
var code = Path.Combine(root, "handlers", "Example.cs");
File.WriteAllText(code, "class Example {}\n");
File.WriteAllText(Path.Combine(root, "settings.json"), "{\"secret\":\"fixture\"}");
var results = new List<object>();
void Check(bool pass, string name)
{
    if (!pass) throw new Exception("FAIL: " + name);
    results.Add(new { test = name, passed = true });
}
Check(SafeSourcePath.Resolve(root, "handlers/Example.cs") == code &&
      SafeSourcePath.Resolve(root, "Example.cs") == code, "allowed relative source and unambiguous filename");
Check(SafeSourcePath.Resolve(root, "../Example.cs") is null &&
      SafeSourcePath.Resolve(root, code) is null, "parent traversal and absolute paths blocked");
Check(SafeSourcePath.Resolve(root, "settings.json") is null &&
      SafeSourcePath.Resolve(root, "*.cs") is null, "configuration files and wildcard lookup blocked");
Check(SafeSourcePath.Resolve(Path.Combine(root, "missing"), "Example.cs") is null,
      "missing source root returns no evidence instead of crashing");
Directory.CreateDirectory(Path.Combine(root, "second"));
File.WriteAllText(Path.Combine(root, "second", "Example.cs"), "class Other {}\n");
Check(SafeSourcePath.Resolve(root, "Example.cs") is null, "ambiguous filename rejected");
Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
