using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cs2Ts;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.WriteLine("Usage: Cs2Ts <outputFolder>");
            return;
        }

        var outputRoot = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(outputRoot);

        var projectRoot = Directory.GetCurrentDirectory();
        var sourceFiles = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                                   .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                               !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                               !p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase));

        var tasks = new List<Task>();
        foreach (var file in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(projectRoot, file);
            var fileText = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(fileText);
            var root = await tree.GetRootAsync();

            var convertNodes = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                                   .Where(n => HasConvertToTsAttribute(n.AttributeLists));

            foreach (var node in convertNodes)
            {
                tasks.Add(Task.Run(() =>
                {
                    var tsContent = TypeScriptGenerator.Generate(node, tree);
                    var targetPath = Path.Combine(outputRoot, Path.ChangeExtension(relativePath, ".ts"));
                    var targetDir = Path.GetDirectoryName(targetPath)!;
                    Directory.CreateDirectory(targetDir);
                    File.WriteAllText(targetPath, tsContent);
                    Console.WriteLine($"Generated {relativePath} -> {Path.GetRelativePath(projectRoot, targetPath)}");
                }));
            }
        }

        await Task.WhenAll(tasks);
    }

    static bool HasConvertToTsAttribute(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var list in lists)
        {
            foreach (var attr in list.Attributes)
            {
                var name = attr.Name.ToString();
                if (name is "ConvertToTs" or "ConvertToTsAttribute") return true;
            }
        }
        return false;
    }
}
