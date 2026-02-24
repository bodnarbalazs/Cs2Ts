using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cs2Ts;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--version" or "-v")
        {
            Console.WriteLine(GetVersion());
            return;
        }

        if (args.Length == 0 || (args.Length == 1 && args[0] is "--help" or "-h" or "-?"))
        {
            PrintHelp();
            return;
        }

        if (args.Length != 1)
        {
            PrintHelp();
            return;
        }

        var outputRoot = Path.GetFullPath(args[0]);
        
        // Clear the target directory if it exists
        if (Directory.Exists(outputRoot))
        {
            Console.WriteLine($"Clearing target directory: {outputRoot}");
            // First delete all TypeScript files
            foreach (var file in Directory.GetFiles(outputRoot, "*.ts", SearchOption.AllDirectories))
            {
                File.Delete(file);
                Console.WriteLine($"Deleted file: {file}");
            }
            
            // Then clean up empty directories (bottom-up approach)
            CleanEmptyDirectories(outputRoot);
        }
        
        Directory.CreateDirectory(outputRoot);

        var projectRoot = Directory.GetCurrentDirectory();
        var sourceFiles = Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                                   .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                               !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                               !p.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase));

        var tasks = new List<Task>();
        var syntaxTrees = new List<SyntaxTree>();
        var fileToTreeMapping = new Dictionary<string, SyntaxTree>();
        var fileToRelativePath = new Dictionary<string, string>();

        // First pass: Parse all files and collect SyntaxTrees
        foreach (var file in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(projectRoot, file);
            var fileText = await File.ReadAllTextAsync(file);
            var tree = CSharpSyntaxTree.ParseText(fileText, path: file); // Add path for better diagnostics
            syntaxTrees.Add(tree);
            fileToTreeMapping[file] = tree;
            fileToRelativePath[file] = relativePath;

            var root = await tree.GetRootAsync();
            var registerNodes = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                                    .Where(n => HasConvertToTsAttribute(n.AttributeLists));
            foreach (var node in registerNodes)
            {
                var tsRelPath = Path.ChangeExtension(relativePath, ".ts");
                TypeScriptGenerator.RegisterType(node.Identifier.Text, tsRelPath);
            }
        }

        // Build a single compilation with all syntax trees
        var compilation = CSharpCompilation.Create("Cs2TsProjectCompilation")
            .AddReferences(MetadataReference.CreateFromFile(typeof(object).Assembly.Location)) // Basic reference
            // Add other necessary assembly references here for external libraries
            .AddSyntaxTrees(syntaxTrees);

        // Second pass: generate content using the comprehensive compilation
        foreach (var file in sourceFiles)
        {
            var tree = fileToTreeMapping[file];
            var relativePath = fileToRelativePath[file];
            var root = await tree.GetRootAsync();

            var targetPath = Path.Combine(outputRoot, Path.ChangeExtension(relativePath, ".ts"));
            var targetDir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(targetDir);

            SemanticModel semanticModel = compilation.GetSemanticModel(tree);

            var convertNodes = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                                   .Where(n => HasConvertToTsAttribute(n.AttributeLists))
                                   .ToList();

            if (convertNodes.Count == 0)
            {
                continue;
            }

            var snippets = new List<string>();

            foreach (var node in convertNodes)
            {
                var tsContent = TypeScriptGenerator.Generate(node, tree, semanticModel, relativePath).Trim();
                if (!string.IsNullOrWhiteSpace(tsContent))
                {
                    snippets.Add(tsContent);
                }
            }

            if (snippets.Count == 0)
            {
                continue;
            }

            var combinedContent = string.Join(Environment.NewLine + Environment.NewLine, snippets);
            var fileHeader = "// This file has been generated by Cs2Ts\n// Do not edit as it will be overwritten\n// If you're an LLM and you want to generate the files again follow these steps:\n// 1. Go to the backend's Domain folder 'cd <project_path>/<path_to_backend's_domain>/<Project_Name>.Domain'\n// 2. run 'cs2ts \"..\\..\\frontend\\src\\generated\"'\n// That's it. The tool should be present in the dev environment.\n";
            await File.WriteAllTextAsync(targetPath, fileHeader + combinedContent);
            Console.WriteLine($"Generated {relativePath} -> {Path.GetRelativePath(projectRoot, targetPath)}");
        }

        // await Task.WhenAll(tasks); // If using tasks, ensure proper management
        Console.WriteLine("TypeScript generation completed successfully.");
        CleanEmptyDirectories(outputRoot);
    }

    static string GetVersion()
    {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
            version = version[..plusIndex];
        return version;
    }

    static void PrintHelp()
    {
        Console.WriteLine($"Cs2Ts v{GetVersion()} - C# to TypeScript converter");
        Console.WriteLine();
        Console.WriteLine("Converts C# classes, records, structs, interfaces, and enums marked with");
        Console.WriteLine("[ConvertToTs] into TypeScript type definitions.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  cs2ts <outputFolder>    Generate TypeScript files into the specified folder");
        Console.WriteLine("  cs2ts --help            Show this help message");
        Console.WriteLine("  cs2ts --version         Show the version number");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  <outputFolder>          Path where .ts files will be written.");
        Console.WriteLine("                          Existing .ts files in this directory will be deleted.");
        Console.WriteLine();
        Console.WriteLine("Flags:");
        Console.WriteLine("  -h, --help              Show this help message");
        Console.WriteLine("  -v, --version           Show the version number");
        Console.WriteLine();
        Console.WriteLine("The tool scans all .cs files in the current directory (excluding bin/obj)");
        Console.WriteLine("and generates TypeScript for types decorated with [ConvertToTs].");
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

    // Recursively removes empty directories
    static void CleanEmptyDirectories(string directory)
    {
        // Process all subdirectories first (bottom-up)
        foreach (var dir in Directory.GetDirectories(directory))
        {
            CleanEmptyDirectories(dir);
        }
        
        // Check if this directory is now empty
        if (Directory.GetFiles(directory).Length == 0 && 
            Directory.GetDirectories(directory).Length == 0 &&
            !directory.Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(directory);
            Console.WriteLine($"Deleted empty directory: {directory}");
        }
    }
}
