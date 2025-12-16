using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
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
        { "uint", "number" },
        { "long", "number" },
        { "ulong", "number" },
        { "short", "number" },
        { "float", "number" },
        { "double", "number" },
        { "decimal", "number" },
        { "bool", "boolean" },
        { "DateTime", "Date" },
        { "DateTimeOffset", "string" },
        { "System.DateTimeOffset", "string" },
        { "TimeSpan", "number" },
        { "System.TimeSpan", "number" },
        { "Guid", "string" },
        { "object", "unknown" }
    };

    [Flags]
    private enum ImportUsage
    {
        None = 0,
        Type = 1,
        Value = 2
    }

    // Track imports needed for each file (type-only vs value usage)
    private static readonly Dictionary<string, Dictionary<string, ImportUsage>> ImportsNeeded = new();
    // Track React imports needed for each file (type-only vs value usage)
    private static readonly Dictionary<string, Dictionary<string, ImportUsage>> ReactImportsNeeded = new();
    // Map type name -> relative path (without extension) where it is declared
    private static readonly Dictionary<string, string> TypeDeclarationPaths = new();
    private static readonly TextInfo InvariantTextInfo = CultureInfo.InvariantCulture.TextInfo;

    // Type hints to carry expected wire-format metadata into generated TS without performing any runtime translation.
    // Keyed by the unqualified CLR type name (and sometimes qualified, depending on how syntax is written).
    private static readonly Dictionary<string, string> TypeFormatHints = new(StringComparer.Ordinal)
    {
        { "DateTimeOffset", "ISO-8601 date-time string" },
        { "System.DateTimeOffset", "ISO-8601 date-time string" },
        { "TimeSpan", "Duration in milliseconds" },
        { "System.TimeSpan", "Duration in milliseconds" }
    };

    private static void MarkImport(Dictionary<string, Dictionary<string, ImportUsage>> store, string fileKey, string symbolName, ImportUsage usage)
    {
        if (!store.TryGetValue(fileKey, out var perFile))
        {
            perFile = new Dictionary<string, ImportUsage>(StringComparer.Ordinal);
            store[fileKey] = perFile;
        }

        if (perFile.TryGetValue(symbolName, out var existing))
        {
            perFile[symbolName] = existing | usage;
        }
        else
        {
            perFile[symbolName] = usage;
        }
    }

    private static void MarkLocalImport(string fileKey, string typeName, ImportUsage usage) =>
        MarkImport(ImportsNeeded, fileKey, typeName, usage);

    private static void MarkReactImport(string fileKey, string symbolName, ImportUsage usage) =>
        MarkImport(ReactImportsNeeded, fileKey, symbolName, usage);
    
    // Allows program to register all type names and their output paths before generation
    public static void RegisterType(string typeName, string relativePath)
    {
        var key = relativePath.Replace('\\','/');
        var noExt = System.IO.Path.ChangeExtension(key, null)?.Replace('\\','/') ?? key;
        TypeDeclarationPaths[typeName] = noExt;
    }

    public static string Generate(BaseTypeDeclarationSyntax node, SyntaxTree tree, SemanticModel semanticModel, string relativePath)
    {
        // Clear imports for this file
        var fileKey = relativePath.Replace('\\','/');
        ImportsNeeded[fileKey] = new Dictionary<string, ImportUsage>(StringComparer.Ordinal);
        ReactImportsNeeded[fileKey] = new Dictionary<string, ImportUsage>(StringComparer.Ordinal);
        
        var result = node switch
        {
            ClassDeclarationSyntax cls => cls.Modifiers.Any(SyntaxKind.StaticKeyword)
                ? GenerateStaticClassConstants(cls, fileKey, semanticModel)
                : GenerateClass(cls, fileKey, semanticModel),
            RecordDeclarationSyntax rec => GenerateInterface(rec, fileKey, semanticModel),
            StructDeclarationSyntax st => GenerateInterface(st, fileKey, semanticModel),
            InterfaceDeclarationSyntax iface => GenerateInterface(iface, fileKey, semanticModel),
            EnumDeclarationSyntax @enum => GenerateEnum(@enum, semanticModel),
            _ => string.Empty
        };

        // Remove self-import to avoid importing the type from its own file
        ImportsNeeded[fileKey].Remove(node.Identifier.Text);
        
        // Add imports if needed
        if (ImportsNeeded[fileKey].Count > 0)
        {
            var importSb = new StringBuilder();
            foreach (var import in ImportsNeeded[fileKey].OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                var importName = import.Key;
                var importPath = BuildRelativeImportPath(fileKey, importName);
                var usage = import.Value;
                var importKeyword = (usage & ImportUsage.Value) != 0 ? "import" : "import type";
                importSb.AppendLine($"{importKeyword} {{ {importName} }} from '{importPath}';");
            }
            importSb.AppendLine();
            result = importSb.ToString() + result;
        }
        
        // Add React imports if needed
        if (ReactImportsNeeded[fileKey].Count > 0)
        {
            var reactImportSb = new StringBuilder();
            foreach (var import in ReactImportsNeeded[fileKey].OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                var importName = import.Key;
                var usage = import.Value;
                var importKeyword = (usage & ImportUsage.Value) != 0 ? "import" : "import type";
                reactImportSb.AppendLine($"{importKeyword} {{ {importName} }} from 'react';");
            }
            reactImportSb.AppendLine();
            result = reactImportSb.ToString() + result;
        }
        
        return result;
    }

    private static string GenerateClass(ClassDeclarationSyntax cls, string fileKey, SemanticModel semanticModel)
    {
        var interfaceContent = GenerateInterface(cls, fileKey, semanticModel);

        var staticFields = cls.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(SyntaxKind.StaticKeyword) && (f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) || f.Modifiers.Any(SyntaxKind.ConstKeyword)));

        var staticExports = GenerateStaticFieldExports(staticFields, fileKey, semanticModel);

        if (string.IsNullOrWhiteSpace(staticExports))
        {
            return interfaceContent;
        }

        return interfaceContent + System.Environment.NewLine + staticExports;
    }

    private static string GenerateStaticClassConstants(ClassDeclarationSyntax cls, string fileKey, SemanticModel semanticModel)
    {
        var fields = cls.Members.OfType<FieldDeclarationSyntax>()
            .Where(f => f.Modifiers.Any(SyntaxKind.StaticKeyword) && (f.Modifiers.Any(SyntaxKind.ReadOnlyKeyword) || f.Modifiers.Any(SyntaxKind.ConstKeyword)));

        return GenerateStaticFieldExports(fields, fileKey, semanticModel);
    }

    private static string GenerateStaticFieldExports(IEnumerable<FieldDeclarationSyntax> fields, string fileKey, SemanticModel semanticModel)
    {
        var exportBlocks = new List<string>();

        foreach (var field in fields)
        {
            var tsType = MapType(field.Declaration.Type, fileKey);

            foreach (var variable in field.Declaration.Variables)
            {
                if (variable.Initializer == null) continue;

                var tsExpr = TryTranslateExpressionToTs(variable.Initializer.Value, fileKey, semanticModel);
                if (tsExpr == null) continue;

                var blockBuilder = new StringBuilder();
                var symbol = semanticModel.GetDeclaredSymbol(variable);
                var docComment = GetJsDocComment(symbol);
                if (!string.IsNullOrWhiteSpace(docComment))
                {
                    blockBuilder.AppendLine(docComment);
                }

                blockBuilder.Append($"export const {variable.Identifier.Text}: {tsType} = {tsExpr};");
                exportBlocks.Add(blockBuilder.ToString().TrimEnd());
            }
        }

        if (exportBlocks.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(System.Environment.NewLine + System.Environment.NewLine, exportBlocks);
    }

    private static string GenerateInterface(BaseTypeDeclarationSyntax node, string fileKey, SemanticModel semanticModel)
    {
        var properties = node.DescendantNodes().OfType<PropertyDeclarationSyntax>();
        var sb = new StringBuilder();

        var typeSymbol = semanticModel.GetDeclaredSymbol(node);
        var typeDocComment = GetJsDocComment(typeSymbol);
        if (!string.IsNullOrWhiteSpace(typeDocComment))
        {
            sb.AppendLine(typeDocComment);
        }
        
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
                MarkLocalImport(fileKey, typeName, ImportUsage.Type);
            }
            
            inheritance = $" extends {string.Join(", ", baseTypeNames)}";
        }
        
        sb.AppendLine($"export interface {node.Identifier.Text}{inheritance} {{");
        
        foreach (var prop in properties)
        {
            var name = ToCamelCase(prop.Identifier.Text);

            string typeTs;
            ExpressionSyntax? constantExpression = null;

            // Check for special attributes on the property
            bool hasReactNodeAttribute = HasAttribute(prop.AttributeLists, "ReactNode");
            bool hasHtmlElementAttribute = HasAttribute(prop.AttributeLists, "HtmlElement");

            if (prop.ExpressionBody != null) // e.g. public T Prop => expression;
            {
                constantExpression = prop.ExpressionBody.Expression;
            }
            else if (prop.AccessorList != null) // e.g. public T Prop { get => expression; }
            {
                var getter = prop.AccessorList.Accessors
                    .FirstOrDefault(acc => acc.IsKind(SyntaxKind.GetAccessorDeclaration) && acc.ExpressionBody != null);
                if (getter != null && getter.ExpressionBody != null)
                {
                    constantExpression = getter.ExpressionBody.Expression;
                }
            }

            if (constantExpression != null)
            {
                string? literalType = TryGetLiteralTypeTs(constantExpression, fileKey, semanticModel);
                if (literalType != null)
                {
                    typeTs = literalType;
                }
                else
                {
                    // Fallback if expression is too complex or not a recognized literal/enum
                    typeTs = MapType(prop.Type, fileKey);
                }
            }
            else
            {
                // Standard property (get; set; or get with block body)
                typeTs = MapType(prop.Type, fileKey);
            }
            
            if (hasReactNodeAttribute)
            {
                typeTs = "ReactNode";
                MarkReactImport(fileKey, "ReactNode", ImportUsage.Type);
            }
            else if (hasHtmlElementAttribute)
            {
                typeTs = "HTMLElement";
            }

            var propertySymbol = semanticModel.GetDeclaredSymbol(prop);
            var propertyDocComment = GetJsDocComment(propertySymbol);
            var hintLines = GetTypeHintLines(prop.Type);
            var mergedDoc = MergeJsDoc(propertyDocComment, hintLines);
            if (!string.IsNullOrWhiteSpace(mergedDoc))
            {
                sb.AppendLine(IndentJsDoc(mergedDoc, "  "));
            }
            
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
                // Always add the base type, whether it's a class or an interface
                baseTypes.Add(baseType.Type);
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

    private static string GenerateEnum(EnumDeclarationSyntax en, SemanticModel semanticModel)
    {
        var sb = new StringBuilder();

        var enumName = en.Identifier.Text;
        sb.AppendLine($"export const {enumName} = {{");

        foreach (var member in en.Members)
        {
            var memberName = member.Identifier.Text;
            var key = IsValidTsIdentifier(memberName) ? memberName : QuoteString(memberName);

            var fieldSymbol = semanticModel.GetDeclaredSymbol(member) as IFieldSymbol;
            var constValue = fieldSymbol?.ConstantValue;
            var valueText = FormatEnumValue(constValue) ?? (member.EqualsValue != null ? member.EqualsValue.Value.ToString() : "0");

            sb.AppendLine($"  {key}: {valueText},");
        }

        sb.AppendLine("} as const;");
        sb.AppendLine();
        sb.AppendLine($"export type {enumName} = (typeof {enumName})[keyof typeof {enumName}];");
        sb.AppendLine();
        sb.AppendLine($"export const {enumName}Name = {{");

        foreach (var member in en.Members)
        {
            var memberName = member.Identifier.Text;
            var fieldSymbol = semanticModel.GetDeclaredSymbol(member) as IFieldSymbol;
            var constValue = fieldSymbol?.ConstantValue;
            var valueText = FormatEnumValue(constValue) ?? (member.EqualsValue != null ? member.EqualsValue.Value.ToString() : "0");

            var key = FormatEnumReverseMapKey(valueText);
            sb.AppendLine($"  {key}: {QuoteString(memberName)},");
        }

        sb.AppendLine($"}} as const satisfies Record<{enumName}, keyof typeof {enumName}>;");
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
            
            // Handle Action and Func types
            if (genericName == "Action")
            {
                if (args.Length == 1)
                {
                    var argType = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                    return $"(arg: {argType}) => void" + (isNullable ? " | null" : "");
                }
                else if (args.Length == 2)
                {
                    var arg1Type = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                    var arg2Type = MapType(SyntaxFactory.ParseTypeName(args[1].Trim()), fileKey);
                    return $"(arg1: {arg1Type}, arg2: {arg2Type}) => void" + (isNullable ? " | null" : "");
                }
                // Add more overloads as needed
            }
            else if (genericName == "Func")
            {
                if (args.Length == 1)
                {
                    var returnType = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                    return $"() => {returnType}" + (isNullable ? " | null" : "");
                }
                else if (args.Length == 2)
                {
                    var argType = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                    var returnType = MapType(SyntaxFactory.ParseTypeName(args[1].Trim()), fileKey);
                    return $"(arg: {argType}) => {returnType}" + (isNullable ? " | null" : "");
                }
                else if (args.Length == 3)
                {
                    var arg1Type = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                    var arg2Type = MapType(SyntaxFactory.ParseTypeName(args[1].Trim()), fileKey);
                    var returnType = MapType(SyntaxFactory.ParseTypeName(args[2].Trim()), fileKey);
                    return $"(arg1: {arg1Type}, arg2: {arg2Type}) => {returnType}" + (isNullable ? " | null" : "");
                }
                // Add more overloads as needed
            }
            
            if (genericName is "List" or "IEnumerable" or "ICollection" or "IList" or "HashSet")
            {
                var inner = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                return $"{inner}[]" + (isNullable ? " | null" : "");
            }
            if (genericName is "Dictionary" or "IDictionary")
            {
                var key = MapType(SyntaxFactory.ParseTypeName(args[0].Trim()), fileKey);
                var val = MapType(SyntaxFactory.ParseTypeName(args[1].Trim()), fileKey);
                return $"Partial<Record<{key}, {val}>>" + (isNullable ? " | null" : "");
            }
        }

        // Handle non-generic Action
        if (typeString == "Action")
        {
            return "() => void" + (isNullable ? " | null" : "");
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
            MarkLocalImport(fileKey, typeString, ImportUsage.Type);
        }

        if (isArray) typeString += "[]";
        
        return isNullable ? $"{typeString} | null" : typeString;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> attributeLists, string attributeName)
    {
        foreach (var attributeList in attributeLists)
        {
            foreach (var attribute in attributeList.Attributes)
            {
                var name = attribute.Name.ToString();
                if (name == attributeName || name == attributeName + "Attribute")
                {
                    return true;
                }
            }
        }
        return false;
    }

    // Calculate relative import path between current file and the file where the imported type is declared
    private static string BuildRelativeImportPath(string currentFilePath, string importTypeName)
    {
        if(!TypeDeclarationPaths.TryGetValue(importTypeName, out var importPath))
        {
            // Fallback: same directory
            return "./" + importTypeName;
        }

        // Remove extension from current file path
        var currentDir = System.IO.Path.GetDirectoryName(currentFilePath)?.Replace('\\','/') ?? string.Empty;
        var targetPath = importPath.Replace('\\','/');
        var targetDir = System.IO.Path.GetDirectoryName(targetPath)?.Replace('\\','/') ?? string.Empty;

        if(string.Equals(currentDir, targetDir, System.StringComparison.OrdinalIgnoreCase))
        {
            return "./" + System.IO.Path.GetFileName(targetPath);
        }

        var currentParts = currentDir.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
        var targetParts = targetDir.Split('/', System.StringSplitOptions.RemoveEmptyEntries);

        int common = 0;
        int min = System.Math.Min(currentParts.Length, targetParts.Length);
        for(int i=0;i<min;i++)
        {
            if(string.Equals(currentParts[i], targetParts[i], System.StringComparison.OrdinalIgnoreCase))
                common++;
            else break;
        }

        var sb = new System.Text.StringBuilder();
        for(int i=common;i<currentParts.Length;i++)
        {
            sb.Append("../");
        }
        for(int i=common;i<targetParts.Length;i++)
        {
            sb.Append(targetParts[i]);
            sb.Append('/');
        }
        sb.Append(System.IO.Path.GetFileName(targetPath));
        var rel = sb.ToString();
        if(!rel.StartsWith("./") && !rel.StartsWith("../"))
            rel = "./"+rel;
        return rel;
    }

    private static string? TryGetLiteralTypeTs(ExpressionSyntax expression, string fileKey, SemanticModel semanticModel)
    {
        if (expression is MemberAccessExpressionSyntax memberAccess) // e.g., MyEnum.Value
        {
            var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);
            if (symbolInfo.Symbol is IFieldSymbol fieldSymbol && fieldSymbol.ContainingType.TypeKind == TypeKind.Enum)
            {
                var enumTypeName = fieldSymbol.ContainingType.Name;
                var enumMemberName = fieldSymbol.Name;

                if (TypeDeclarationPaths.ContainsKey(enumTypeName))
                {
                    // `typeof Enum.Member` needs a value import under `verbatimModuleSyntax`
                    MarkLocalImport(fileKey, enumTypeName, ImportUsage.Value);
                }
                return $"typeof {enumTypeName}.{enumMemberName}";
            }
        }
        else if (expression is LiteralExpressionSyntax literal)
        {
            switch (literal.Kind())
            {
                case SyntaxKind.StringLiteralExpression:
                    return literal.Token.Text; // Includes quotes, e.g., "\"text\""
                case SyntaxKind.NumericLiteralExpression:
                    return literal.Token.Text; // e.g., "123"
                case SyntaxKind.TrueLiteralExpression:
                    return "true";
                case SyntaxKind.FalseLiteralExpression:
                    return "false";
                // Potentially handle SyntaxKind.NullLiteralExpression -> "null"
            }
        }
        return null; // Not a recognized literal type
    }

    private static string? TryGetLiteralExpressionTs(ExpressionSyntax expression, string fileKey, SemanticModel semanticModel)
    {
        if (expression is MemberAccessExpressionSyntax memberAccess) // e.g., MyEnum.Value
        {
            var symbolInfo = semanticModel.GetSymbolInfo(memberAccess);
            if (symbolInfo.Symbol is IFieldSymbol fieldSymbol && fieldSymbol.ContainingType.TypeKind == TypeKind.Enum)
            {
                var enumTypeName = fieldSymbol.ContainingType.Name;
                var enumMemberName = fieldSymbol.Name;

                if (TypeDeclarationPaths.ContainsKey(enumTypeName))
                {
                    MarkLocalImport(fileKey, enumTypeName, ImportUsage.Value);
                }
                return $"{enumTypeName}.{enumMemberName}";
            }
        }
        else if (expression is LiteralExpressionSyntax literal)
        {
            switch (literal.Kind())
            {
                case SyntaxKind.StringLiteralExpression:
                    return literal.Token.Text;
                case SyntaxKind.NumericLiteralExpression:
                    return literal.Token.Text;
                case SyntaxKind.TrueLiteralExpression:
                    return "true";
                case SyntaxKind.FalseLiteralExpression:
                    return "false";
            }
        }
        return null;
    }

    private static string? TryTranslateExpressionToTs(ExpressionSyntax expression, string fileKey, SemanticModel semanticModel)
    {
        // Enum member or literal
        var literal = TryGetLiteralExpressionTs(expression, fileKey, semanticModel);
        if (literal != null)
            return literal;

        // Handle parenthesized
        if (expression is ParenthesizedExpressionSyntax paren && paren.Expression != null)
        {
            var inner = TryTranslateExpressionToTs(paren.Expression, fileKey, semanticModel);
            return inner != null ? $"({inner})" : null;
        }

        // Handle unary minus for numbers
        if (expression is PrefixUnaryExpressionSyntax prefix && prefix.OperatorToken.IsKind(SyntaxKind.MinusToken))
        {
            var inner = TryTranslateExpressionToTs(prefix.Operand, fileKey, semanticModel);
            return inner != null ? "-" + inner : null;
        }

        // Handle array creation with initializer: new T[] { ... }
        if (expression is ArrayCreationExpressionSyntax arrayCreation && arrayCreation.Initializer != null)
        {
            return TranslateArrayInitializer(arrayCreation.Initializer, fileKey, semanticModel);
        }

        // Handle implicit array creation: new[] { ... }
        if (expression is ImplicitArrayCreationExpressionSyntax implicitArray && implicitArray.Initializer != null)
        {
            return TranslateArrayInitializer(implicitArray.Initializer, fileKey, semanticModel);
        }

        // Handle collection expressions: [ a, b, c ] (C# 12)
        if (expression is CollectionExpressionSyntax collectionExpr)
        {
            var items = new List<string>();
            foreach (var element in collectionExpr.Elements)
            {
                if (element is ExpressionElementSyntax ee)
                {
                    var item = TryTranslateExpressionToTs(ee.Expression, fileKey, semanticModel);
                    if (item == null) return null;
                    items.Add(item);
                }
                else if (element is SpreadElementSyntax se)
                {
                    var spread = TryTranslateExpressionToTs(se.Expression, fileKey, semanticModel);
                    if (spread == null) return null;
                    items.Add("..." + spread);
                }
                else
                {
                    return null; // unsupported element type
                }
            }
            return "[" + string.Join(", ", items) + "]";
        }

        if (expression is ObjectCreationExpressionSyntax objectCreation)
        {
            return TranslateObjectCreation(objectCreation, fileKey, semanticModel);
        }

        if (expression is ImplicitObjectCreationExpressionSyntax implicitObjectCreation)
        {
            return TranslateObjectCreation(implicitObjectCreation, fileKey, semanticModel);
        }

        // Fallback: try simple identifier referencing an enum member without dot (unlikely) or const
        if (expression is IdentifierNameSyntax id)
        {
            var symbol = semanticModel.GetSymbolInfo(id).Symbol;
            if (symbol is IFieldSymbol fs && fs.IsConst)
            {
                // For consts, attempt to get constant value
                var constVal = fs.ConstantValue;
                if (constVal is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                if (constVal is bool b) return b ? "true" : "false";
                if (constVal != null) return constVal.ToString();
            }
        }

        return null; // unsupported expression
    }

    private static bool IsValidTsIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!(char.IsLetter(name[0]) || name[0] == '_' || name[0] == '$')) return false;
        for (int i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$')) return false;
        }

        // Minimal keyword set (common TS/JS reserved words)
        return name is not ("default" or "class" or "function" or "var" or "let" or "const" or "enum" or "export" or "import" or "extends" or "implements" or "interface" or "new" or "return" or "switch" or "case" or "throw");
    }

    private static string QuoteString(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("'", "\\'");
        return $"'{escaped}'";
    }

    private static string? FormatEnumValue(object? constantValue)
    {
        if (constantValue == null) return null;
        return constantValue switch
        {
            sbyte v => v.ToString(CultureInfo.InvariantCulture),
            byte v => v.ToString(CultureInfo.InvariantCulture),
            short v => v.ToString(CultureInfo.InvariantCulture),
            ushort v => v.ToString(CultureInfo.InvariantCulture),
            int v => v.ToString(CultureInfo.InvariantCulture),
            uint v => v.ToString(CultureInfo.InvariantCulture),
            long v => v.ToString(CultureInfo.InvariantCulture),
            ulong v => v.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(constantValue, CultureInfo.InvariantCulture)
        };
    }

    private static string FormatEnumReverseMapKey(string valueText)
    {
        // Prefer numeric literal keys when possible, but handle negatives with computed property.
        if (valueText.StartsWith("-", StringComparison.Ordinal))
        {
            return $"[{valueText}]";
        }
        return valueText;
    }

    private static string? TranslateObjectCreation(BaseObjectCreationExpressionSyntax objectCreation, string fileKey, SemanticModel semanticModel)
    {
        if (objectCreation.Initializer == null)
        {
            return "{}";
        }

        if (objectCreation.Initializer.Expressions.All(e => e is AssignmentExpressionSyntax))
        {
            return TranslateObjectInitializer(objectCreation.Initializer, fileKey, semanticModel);
        }

        // Collection initializer fallback
        return TranslateArrayInitializer(objectCreation.Initializer, fileKey, semanticModel);
    }

    private static string? TranslateArrayInitializer(InitializerExpressionSyntax initializer, string fileKey, SemanticModel semanticModel)
    {
        var items = new List<string>();
        foreach (var expr in initializer.Expressions)
        {
            var item = TryTranslateExpressionToTs(expr, fileKey, semanticModel);
            if (item == null) return null;
            items.Add(item);
        }
        return "[" + string.Join(", ", items) + "]";
    }

    private static string? TranslateObjectInitializer(InitializerExpressionSyntax initializer, string fileKey, SemanticModel semanticModel)
    {
        var properties = new List<string>();
        foreach (var expr in initializer.Expressions)
        {
            if (expr is not AssignmentExpressionSyntax assignment) return null;

            var propertyName = assignment.Left switch
            {
                IdentifierNameSyntax ident => ident.Identifier.Text,
                SimpleNameSyntax simple => simple.Identifier.Text,
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
                _ => assignment.Left.ToString()
            };

            propertyName = propertyName.TrimStart('@');
            var tsPropertyName = ToCamelCase(propertyName);

            var valueTs = TryTranslateExpressionToTs(assignment.Right, fileKey, semanticModel);
            if (valueTs == null) return null;

            if (valueTs.Contains('\n'))
            {
                valueTs = IndentMultiline(valueTs, "    ");
            }

            properties.Add($"  {tsPropertyName}: {valueTs}");
        }

        if (properties.Count == 0)
        {
            return "{}";
        }

        return "{\n" + string.Join(",\n", properties) + "\n}";
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Length == 1) return InvariantTextInfo.ToLower(name);

        if (char.IsUpper(name[0]))
        {
            if (name.Length > 1 && char.IsUpper(name[1]))
            {
                // Preserve acronyms (e.g., URLBuilder -> urlBuilder)
                var index = 1;
                while (index < name.Length && char.IsUpper(name[index]))
                {
                    index++;
                }
                var leading = name.Substring(0, index - 1);
                var rest = name.Substring(index - 1);
                return InvariantTextInfo.ToLower(leading) + rest;
            }

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        return name;
    }

    private static string IndentMultiline(string value, string indent)
    {
        var lines = value.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= 1) return value;

        var sb = new StringBuilder();
        sb.Append(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            sb.Append('\n');
            sb.Append(indent);
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }

    private static string? GetJsDocComment(ISymbol? symbol)
    {
        if (symbol == null) return null;
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: default);
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            var doc = XDocument.Parse(xml);
            var member = doc.Root;
            if (member == null) return null;

            var summaryLines = ExtractDocumentationLines(member.Element("summary"));
            var remarksLines = ExtractDocumentationLines(member.Element("remarks"));

            var combined = new List<string>();
            combined.AddRange(summaryLines);
            if (combined.Count > 0 && remarksLines.Count > 0)
            {
                combined.Add(string.Empty);
            }
            combined.AddRange(remarksLines);

            if (combined.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("/**");
            foreach (var line in combined)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    sb.AppendLine(" *");
                }
                else
                {
                    sb.AppendLine($" * {line}");
                }
            }
            sb.Append(" */");
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ExtractDocumentationLines(XElement? element)
    {
        if (element == null) return new List<string>();

        var text = element.Value;
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        return text.Replace("\r", string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
    }

    private static string IndentJsDoc(string jsDoc, string indent)
    {
        var normalized = jsDoc.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(indent);
            sb.Append(lines[i]);
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static List<string> GetTypeHintLines(TypeSyntax typeSyntax)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectTypeHintLines(typeSyntax, lines, seen);
        return lines;
    }

    private static void CollectTypeHintLines(TypeSyntax typeSyntax, List<string> lines, HashSet<string> seen)
    {
        // Unwrap nullable
        if (typeSyntax is NullableTypeSyntax nts)
        {
            CollectTypeHintLines(nts.ElementType, lines, seen);
            return;
        }

        // Handle array types
        if (typeSyntax is ArrayTypeSyntax ats)
        {
            CollectTypeHintLines(ats.ElementType, lines, seen);
            return;
        }

        // Handle generics: recurse into type arguments
        if (typeSyntax is GenericNameSyntax gns)
        {
            foreach (var arg in gns.TypeArgumentList.Arguments)
            {
                CollectTypeHintLines(arg, lines, seen);
            }
            return;
        }

        // Qualified generic name: e.g. System.Collections.Generic.List<TimeSpan>
        if (typeSyntax is QualifiedNameSyntax qns && qns.Right is GenericNameSyntax rightGeneric)
        {
            foreach (var arg in rightGeneric.TypeArgumentList.Arguments)
            {
                CollectTypeHintLines(arg, lines, seen);
            }
            // Also allow matching on the fully qualified name itself.
            TryAddTypeHint(qns.ToString(), lines, seen);
            TryAddTypeHint(qns.Right.Identifier.Text, lines, seen);
            return;
        }

        // Qualified non-generic: e.g. System.DateTimeOffset
        if (typeSyntax is QualifiedNameSyntax qnsNonGeneric)
        {
            TryAddTypeHint(qnsNonGeneric.ToString(), lines, seen);
            TryAddTypeHint(qnsNonGeneric.Right.Identifier.Text, lines, seen);
            return;
        }

        // Identifier: e.g. DateTimeOffset, TimeSpan
        if (typeSyntax is IdentifierNameSyntax ins)
        {
            TryAddTypeHint(ins.Identifier.Text, lines, seen);
            return;
        }

        // Fallback: attempt match on string form (covers some parsed generic cases)
        TryAddTypeHint(typeSyntax.ToString(), lines, seen);
    }

    private static void TryAddTypeHint(string typeName, List<string> lines, HashSet<string> seen)
    {
        if (TypeFormatHints.TryGetValue(typeName, out var hint) && seen.Add(hint))
        {
            lines.Add(hint);
        }
    }

    private static string? MergeJsDoc(string? existingJsDoc, List<string> extraLines)
    {
        if (extraLines.Count == 0)
        {
            return existingJsDoc;
        }

        if (string.IsNullOrWhiteSpace(existingJsDoc))
        {
            var sb = new StringBuilder();
            sb.AppendLine("/**");
            foreach (var line in extraLines)
            {
                sb.AppendLine($" * {line}");
            }
            sb.Append(" */");
            return sb.ToString();
        }

        var normalized = existingJsDoc.Replace("\r\n", "\n");
        var lines = normalized.Split('\n').ToList();

        // Insert before the closing */ (best-effort).
        var closeIndex = lines.FindLastIndex(l => l.TrimEnd().EndsWith("*/", StringComparison.Ordinal));
        if (closeIndex < 0) closeIndex = lines.Count;

        // Add a blank line separator if this doc already has content beyond the opening line.
        if (closeIndex > 1)
        {
            lines.Insert(closeIndex, " *");
            closeIndex++;
        }

        foreach (var extra in extraLines)
        {
            lines.Insert(closeIndex, $" * {extra}");
            closeIndex++;
        }

        return string.Join("\n", lines);
    }
}
