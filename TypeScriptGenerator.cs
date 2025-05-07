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

    public static string Generate(BaseTypeDeclarationSyntax node, SyntaxTree tree)
    {
        return node switch
        {
            ClassDeclarationSyntax cls => GenerateInterface(cls),
            RecordDeclarationSyntax rec => GenerateInterface(rec),
            StructDeclarationSyntax st => GenerateInterface(st),
            InterfaceDeclarationSyntax iface => GenerateInterface(iface),
            EnumDeclarationSyntax @enum => GenerateEnum(@enum),
            _ => string.Empty
        };
    }

    private static string GenerateInterface(BaseTypeDeclarationSyntax node)
    {
        var properties = node.DescendantNodes().OfType<PropertyDeclarationSyntax>();
        var sb = new StringBuilder();
        sb.AppendLine($"export interface {node.Identifier.Text} {{");
        foreach (var prop in properties)
        {
            var name = prop.Identifier.Text;
            var typeTs = MapType(prop.Type);
            var optional = prop.Type is NullableTypeSyntax || prop.Type.ToString().EndsWith("?", StringComparison.Ordinal) ? "?" : string.Empty;
            sb.AppendLine($"  {name}{optional}: {typeTs};");
        }
        sb.AppendLine("}");
        return sb.ToString();
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

    private static string MapType(TypeSyntax typeSyntax)
    {
        var typeString = typeSyntax.ToString();
        var isArray = false;

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
                var inner = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()));
                return $"{inner}[]";
            }
            if (genericName is "Dictionary" or "IDictionary")
            {
                var key = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()));
                var val = MapType(SyntaxFactory.ParseTypeName(args[1].Trim()));
                return $"Record<{key}, {val}>";
            }
        }

        if (TypeMap.TryGetValue(typeString, out var mapped))
            typeString = mapped;

        if (isArray) typeString += "[]";
        return typeString;
    }
}
