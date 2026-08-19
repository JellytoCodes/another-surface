using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;

if (args.Length is < 2 or > 3)
{
    Console.Error.WriteLine("Usage: Decompiler <assembly> <output> [type]");
    return 2;
}

var settings = new DecompilerSettings
{
    ThrowOnAssemblyResolveErrors = false
};
var decompiler = new CSharpDecompiler(
    Path.GetFullPath(args[0]),
    settings);
var source = args.Length == 3
    ? decompiler.DecompileTypeAsString(new FullTypeName(args[2]))
    : decompiler.DecompileWholeModuleAsString();
await File.WriteAllTextAsync(Path.GetFullPath(args[1]), source);
return 0;
