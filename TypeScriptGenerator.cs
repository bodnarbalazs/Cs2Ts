using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cs2Ts;

internal static class TypeScriptGenerator
{
    private static readonly Dictionary<string, string> TypeMap = new()
    {
        { "string", "string" },
        { "int", "number" },
        { "long", "number" },
        { "short", "number" },
        { "float", "number" },
        { "double", "number" },
        { "decimal", "number" },
        { "bool", "boolean" },
        { "DateTime", "Date" },
        { "Guid", "string" }
    };

    // Track imports needed for each file
    private static readonly Dictionary<string, HashSet<string>> ImportsNeeded = new();
    
    public static string Generate(BaseTypeDeclarationSyntax node, SyntaxTree tree, string relativePath)
    {
        // Clear imports for this file
        var fileKey = relativePath;
        ImportsNeeded[fileKey] = new HashSet<string>();
        
        var result = node switch
        {
            ClassDeclarationSyntax cls => GenerateInterface(cls, fileKey),
            RecordDeclarationSyntax rec => GenerateInterface(rec, fileKey),
            StructDeclarationSyntax st => GenerateInterface(st, fileKey),
            InterfaceDeclarationSyntax iface => GenerateInterface(iface, fileKey),
            EnumDeclarationSyntax @enum => GenerateEnum(@enum),
            _ => string.Empty
        };
        
        // Add imports if needed
        if (ImportsNeeded[fileKey].Count > 0)
        {
            var importSb = new StringBuilder();
            foreach (var import in ImportsNeeded[fileKey])
            {
                importSb.AppendLine($"import {{ {import.Split('.').Last()} }} from './{import}';");
            }
            importSb.AppendLine();
            return importSb.ToString() + result;
        }
        
        return result;
    }

    private static string GenerateInterface(BaseTypeDeclarationSyntax node, string fileKey)
    {
        var properties = node.DescendantNodes().OfType<PropertyDeclarationSyntax>();
        var sb = new StringBuilder();
        
        // Handle inheritance
        var baseTypes = GetBaseTypes(node);
        string inheritance = string.Empty;
        
        if (baseTypes.Count > 0)
        {
            var baseTypeNames = new List<string>();
            foreach (var baseType in baseTypes)
            {
                var typeName = baseType.ToString();
                baseTypeNames.Add(typeName);
                
                // Add import for base type
                ImportsNeeded[fileKey].Add(typeName);
            }
            
            inheritance = $" extends {string.Join(", ", baseTypeNames)}";
        }
        
        sb.AppendLine($"export interface {node.Identifier.Text}{inheritance} {{");
        
        foreach (var prop in properties)
        {
            var name = prop.Identifier.Text;
            var typeTs = MapType(prop.Type, fileKey);
            sb.AppendLine($"  {name}: {typeTs};");
        }
        
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static List<TypeSyntax> GetBaseTypes(BaseTypeDeclarationSyntax node)
    {
        var baseTypes = new List<TypeSyntax>();
        
        if (node is ClassDeclarationSyntax classDecl && classDecl.BaseList != null)
        {
            foreach (var baseType in classDecl.BaseList.Types)
            {
                // Skip interfaces, only include base classes
                var typeName = baseType.Type.ToString();
                if (!typeName.StartsWith("I") || !char.IsUpper(typeName[1]))
                {
                    baseTypes.Add(baseType.Type);
                }
            }
        }
        else if (node is InterfaceDeclarationSyntax interfaceDecl && interfaceDecl.BaseList != null)
        {
            foreach (var baseType in interfaceDecl.BaseList.Types)
            {
                baseTypes.Add(baseType.Type);
            }
        }
        
        return baseTypes;
    }

    private static string GenerateEnum(EnumDeclarationSyntax en)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"export enum {en.Identifier.Text} {{");
        foreach (var member in en.Members)
        {
            var value = member.EqualsValue != null ? " = " + member.EqualsValue.Value.ToString() : string.Empty;
            sb.AppendLine($"  {member.Identifier.Text}{value},");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string MapType(TypeSyntax typeSyntax, string fileKey)
    {
        var isNullable = false;
        var typeString = typeSyntax.ToString();
        var isArray = false;
        var originalType = typeString;

        // Handle nullable reference types and value types
        if (typeSyntax is NullableTypeSyntax nullableType)
        {
            isNullable = true;
            typeString = nullableType.ElementType.ToString();
        }
        else if (typeString.EndsWith("?", StringComparison.Ordinal))
        {
            isNullable = true;
            typeString = typeString.Substring(0, typeString.Length - 1);
        }

        if (typeString.EndsWith("[]", StringComparison.Ordinal))
        {
            isArray = true;
            typeString = typeString.Substring(0, typeString.Length - 2);
        }

        if (typeString.Contains('<') && typeString.Contains('>'))
        {
            var start = typeString.IndexOf('<');
            var end = typeString.LastIndexOf('>');
            var genericName = typeString.Substring(0, start).Trim();
            var innerPart = typeString.Substring(start + 1, end - start - 1);
            var args = innerPart.Split(',');
            if (genericName is "List" or "IEnumerable" or "ICollection" or "IList" or "HashSet")
            {
                var inner = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                return $"{inner}[]" + (isNullable ? " | null" : "");
            }
            if (genericName is "Dictionary" or "IDictionary")
            {
                var key = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                var val = MapType(SyntaxFactory.ParseTypeName(args[1].Trim()), fileKey);
                return $"Record<{key}, {val}>" + (isNullable ? " | null" : "");
            }
        }

        // Check if it's a primitive type
        if (TypeMap.TryGetValue(typeString, out var mapped))
        {
            typeString = mapped;
        }
        // Not a primitive type, might need an import
        else if (!typeString.Contains('.') && char.IsUpper(typeString[0]))
        {
            // Add to imports if it's a custom type (starts with uppercase and not a primitive)
            ImportsNeeded[fileKey].Add(typeString);
        }

        if (isArray) typeString += "[]";
        
        return isNullable ? $"{typeString} | null" : typeString;
    }
}
