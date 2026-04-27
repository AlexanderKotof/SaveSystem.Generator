using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;

namespace SaveDataGenerator
{
    [Generator]
    [SuppressMessage("ReSharper", "ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract")]
    public class SaveDataSourceGenerator : ISourceGenerator
    {
        private static string GetLogPath()
        {
            try
            {
                return Path.GetFullPath(Path.Combine("Temp", "Generated", "SourceGen_Debug.log"));
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "Unity_SourceGen_Debug.log");
            }
        }

        private static void Log(string message)
        {
            try
            {
                var logPath = GetLogPath();
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
                Console.WriteLine(message);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaveGen LogFail] {ex.Message} | {message}");
            }
        }

        public void Initialize(GeneratorInitializationContext context) { }

        public void Execute(GeneratorExecutionContext context)
        {
            var created = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            TryFindSaveDataAttribute(context);

            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                var semanticModel = context.Compilation.GetSemanticModel(tree);
                var types = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>();
                
               

                foreach (var typeDecl in types)
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                    if (typeSymbol == null) continue;
                    if (!HasSaveDataAttribute(typeSymbol)) continue;
                    if (!created.Add(typeSymbol)) continue;

                    Log($"Generating {typeSymbol.Name}...");

                    try
                    {
                        var code = Generate(typeSymbol);
                        if (!string.IsNullOrWhiteSpace(code))
                        {
                            context.AddSource($"{typeSymbol.Name}.SaveData.g.cs", SourceText.From(code, Encoding.UTF8));
                            Log($"Generated {typeSymbol.Name}.SaveData.g.cs\n");
                            //Log($"Content:\n{code}");
                        }
                        else
                        {
                            Log($"Generated empty output for {typeSymbol.Name}");
                        }
                    }
                    catch (Exception e)
                    {
                        Log("Exception: " + e.Message);
                    }
                }
            }
        }

        private void TryFindSaveDataAttribute(GeneratorExecutionContext context)
        {
            AttributeSyntax? saveDataAttributeDecl;
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                //TODO: find SaveDataAttribute first
                saveDataAttributeDecl = tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>()
                    .FirstOrDefault(s => s.Name.ToString().Contains("SaveData"));

                if (saveDataAttributeDecl != null)
                {
                    Log($"*****  Found save data attribute {saveDataAttributeDecl.ToString()}, {saveDataAttributeDecl.Name.ToString()}");
                    break;
                }
            }
        }

        // =========================
        // CORE GENERATION
        // =========================

        private static string Generate(INamedTypeSymbol type)
        {
            var dtoName = $"{type.Name}SaveData";
            var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();

            var members = type.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(HasSaveDataAttribute)
                .ToList();
            
            //TODO:
            // members.AddRange(type.GetMembers()
            //     .OfType<IFieldSymbol>()
            //     .Where(HasSaveDataAttribute));

            //Collecting props from parent types
            var iterate = type;
            while (iterate.BaseType != null)
            {
                iterate = iterate.BaseType;
                var ms = iterate.GetMembers().OfType<IPropertySymbol>().Where(HasSaveDataAttribute);
                members.AddRange(ms);
            }

            if (members.Count == 0) return string.Empty;

            var usings = new HashSet<string> { "System", "System.Collections.Generic", "System.Linq" };
            var dtoFields = new List<string>();
            var toSaveLines = new List<string>();
            var applyLines = new List<string>();

            foreach (var m in members)
            {
                CollectTypeContent(m, usings, dtoFields, toSaveLines, applyLines);
            }

            if (dtoFields.Count == 0) return string.Empty;

            return GenerateOutput(type, usings, ns, dtoName, dtoFields, toSaveLines, applyLines);
        }

        private static void CollectTypeContent(IPropertySymbol m, HashSet<string> usings, List<string> dtoFields, List<string> toSaveLines, List<string> applyLines)
        {
            var typeInfo = ResolveType(m.Type, usings);
            if (typeInfo.Skip) return;

            var dtoPropName = typeInfo.IsConfig ? m.Name + "Id" : m.Name;

            // 1. Поле в DTO
            dtoFields.Add($"public {typeInfo.DtoTypeName} {dtoPropName} {{ get; set; }}");

            // 2. ToSaveData
            string readExpr = $"model.{m.Name}";
            if (typeInfo.IsReactive) readExpr += ".Value";
            if (typeInfo.IsConfig) readExpr += ".Id";
            if (typeInfo.IsNestedSaveData) readExpr += ".ToSaveData()";

            if (typeInfo.IsCollection)
            {
                //TODO: add possibility custom selection of collection elements
                var elemMap = GetElementMapExpr(typeInfo.CollectionElementType!);
                toSaveLines.Add($"{dtoPropName} = {readExpr}?.Select(x => {elemMap}).ToArray() ?? Array.Empty<{typeInfo.CollectionElementType!.DtoTypeName}>()");
            }
            else
            {
                toSaveLines.Add($"{dtoPropName} = {readExpr}");
            }

            // 3. ApplySaveData
            var writeExpr = GetWriteExpression(m, typeInfo, dtoPropName);
            if (!string.IsNullOrEmpty(writeExpr))
            {
                applyLines.Add(writeExpr);
            }
        }

        private static string GetElementMapExpr(TypeInfo info)
        {
            if (info.IsNestedSaveData) return "x.ToSaveData()";
            if (info.IsReactive) return "x.Value";
            if (info.IsConfig) return "x.Id";
            return "x";
        }

        private static string GetWriteExpression(IPropertySymbol m, TypeInfo typeInfo, string dtoPropName)
        {
            var modelExpr = $"model.{m.Name}";
            var dataExpr = $"data.{dtoPropName}";

            if (typeInfo.IsCollection )
            {
                //TODO: can't be implemented right now
                // more complex logic required
                return $"//*** Data Collection: {m.Name}";
                
//                 var elemApply = GetElementApplyExpr(typeInfo.CollectionElementType!, modelExpr, dataExpr);
//                 return $"if ({dataExpr} != null)\n\t\t{{\n" +
//                        $"\t\t\tif ({modelExpr} is ICollection<{typeInfo.CollectionElementType!.ModelTypeName}> col)\n" +
//                        @"			{
//                 col.Clear();
// " +
//                        $"\t\t\t\tforeach (var x in {dataExpr}) col.Add({elemApply});\n" +
//                        @"            }
//         }";  

//             else
//             {
//                 // Fallback for non-ICollection properties
// " +
//                 $"\t\t\t\t{modelExpr} = {dataExpr}.Select(x => {elemApply}).ToList();\n" +
//                 @"            }"
            }

            if (typeInfo.IsNestedSaveData)
            {
                return $"//*** Nested Data Exist: {modelExpr}.ApplySaveData({dataExpr}); (Requires manual handling or generated ToModel)";
            }

            if (typeInfo.IsReactive)
            {
                return $"{modelExpr}.Value = {dataExpr};";
            }

            if (typeInfo.IsConfig)
            {
                Log($"Config found {m.Name}, skipping apply.");
                return string.Empty;
            }

            if (m.SetMethod is { DeclaredAccessibility: Accessibility.Public })
            {
                return $"{modelExpr} = {dataExpr};";
            }

            Log($"Trying to update get-only non reactive property {m.Name}!");
            return $"//*** {modelExpr} is get-only. Skip applying.";
        }

        private static string GetElementApplyExpr(TypeInfo info, string modelExpr, string dataExpr)
        {
            if (info.IsNestedSaveData) return $"new {info.ModelTypeName}().ApplySaveData(x); // or use DI/Factory in real project";
            if (info.IsReactive) return "x";
            return "x";
        }

        // =========================
        // OUTPUT GENERATION
        // =========================

        private static string GenerateOutput(INamedTypeSymbol type, HashSet<string> usings, string? ns, string dtoName, List<string> dtoFields, List<string> toSaveLines, List<string> applyLines)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine();

            foreach (var u in usings.OrderBy(x => x))
                sb.AppendLine($"using {u};");

            sb.AppendLine();
            sb.AppendLine("#pragma warning disable");

            if (ns != null)
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
            }

            sb.AppendLine("[Serializable]");
            sb.AppendLine($"public struct {dtoName} : ISaveData");
            sb.AppendLine("{");
            foreach (var f in dtoFields) sb.AppendLine($"    {f}");
            sb.AppendLine("}");

            sb.AppendLine($"public static class {type.Name}SaveExtensions");
            sb.AppendLine("{");

            // ToSaveData
            sb.AppendLine($"    public static {dtoName} ToSaveData(this {type.Name} model)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (model == null) return default;");
            sb.AppendLine($"        return new {dtoName}");
            sb.AppendLine("        {");
            foreach (var l in toSaveLines) sb.AppendLine($"            {l},");
            sb.AppendLine("        };");
            sb.AppendLine("    }");

            // ApplySaveData
            sb.AppendLine($"    public static void ApplySaveData(this {type.Name} model, {dtoName} data)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (model == null) return;");
            foreach (var l in applyLines) sb.AppendLine($"        {l}");
            sb.AppendLine("    }");

            sb.AppendLine("}");
            if (ns != null) sb.AppendLine("}");
            sb.AppendLine("#pragma warning restore");

            return sb.ToString();
        }
        
        // ✅ Заменена уязвимая логика вырезания неймспейсов
        private static string GetShortTypeName(ITypeSymbol type, HashSet<string> usings)
        {
            if (type == null) return "object";
            var ns = type.ContainingNamespace?.ToDisplayString();
            if (!string.IsNullOrEmpty(ns)) usings.Add(ns);

            if (type is INamedTypeSymbol named)
            {
                // Убираем арность (например, List`1 -> List)
                var baseName = named.Name.Contains('`') ? named.Name.Split('`')[0] : named.Name;
                if (named.IsGenericType)
                {
                    var args = string.Join(", ", named.TypeArguments.Select(a => GetShortTypeName(a, usings)));
                    return $"{baseName}<{args}>";
                }
                return baseName;
            }
            return type.Name;
        }
        
        // =========================
        // TYPE RESOLUTION & SHORT NAMES
        // =========================
        
        private static TypeInfo ResolveType(ITypeSymbol type, HashSet<string> usings)
        {
            if (type == null || type.Kind == SymbolKind.ErrorType) return TypeInfo.SkipType();
            usings.Add(type.ContainingNamespace?.ToDisplayString());

            var info = new TypeInfo();

            if (type is INamedTypeSymbol named)
            {
                // Проверка на коллекции
                if (TryResolveCollection(named, usings, out var colInfo))
                {
                    info.IsCollection = true;
                    info.CollectionElementType = colInfo;
                    info.DtoTypeName = $"{colInfo.DtoTypeName}[]";
                    info.ModelTypeName = named.ToDisplayString();
                    return info;
                }
                
                //TODO: проверка на словари
                
                // Проверка на UniRx ReactiveProperty<T>
                var defName = named.OriginalDefinition?.ToDisplayString() ?? string.Empty;
                if ((defName.Contains("ReactiveProperty") || named.Name.Contains("ReactiveProperty")) && named.TypeArguments.Length > 0)
                {
                    var inner = ResolveType(named.TypeArguments[0], usings);
                    inner.IsReactive = true;
                    return inner;
                }

                // Кастомные реактивные (наследуемые)
                if (named.Name.EndsWith("ReactiveProperty"))
                {
                    return ResolveType(named.BaseType!, usings);
                }
                
                // Проверка на Config
                if (IsConfig(named))
                {
                    info.IsConfig = true;
                    info.DtoTypeName = "string";
                    info.ModelTypeName = named.ToDisplayString();
                    return info;
                }
                
                // Вложенный SaveData
                if (HasSaveDataAttribute(named))
                {
                    info.IsNestedSaveData = true;
                    info.DtoTypeName = $"{named.Name}SaveData";
                    info.ModelTypeName = named.ToDisplayString();
                    return info;
                }
            }

            // Примитивы / обычные типы
            info.DtoTypeName = GetShortTypeName(type, usings);
            info.ModelTypeName = type.ToDisplayString();
            return info;
        }

        private static bool TryResolveCollection(INamedTypeSymbol named, HashSet<string> usings, out TypeInfo elementInfo)
        {
            elementInfo = TypeInfo.SkipType();
            var defName = named.OriginalDefinition?.ToDisplayString() ?? string.Empty;
            
            // Определяем, является ли тип коллекцией (массив, List, IList, ReactiveCollection и т.д.)
            bool isCollection = named.TypeKind == TypeKind.Array || 
                                defName.Contains("List") || defName.Contains("IList") || 
                                defName.Contains("ICollection") || defName.Contains("ReactiveCollection") ||
                                defName.Contains("IEnumerable");

            // Исключаем string (он реализует IEnumerable)
            if (isCollection && named.SpecialType == SpecialType.System_String) return false;
            if (!isCollection || !named.IsGenericType && named.TypeKind != TypeKind.Array) return false;

            ITypeSymbol? elementType = named.TypeKind == TypeKind.Array 
                ? ((IArrayTypeSymbol)named).ElementType 
                : named.TypeArguments.FirstOrDefault();

            if (elementType == null || elementType.Kind == SymbolKind.ErrorType) return false;

            elementInfo = ResolveType(elementType, usings);
            return true;
        }

        private static bool IsConfig(INamedTypeSymbol named) =>
            named.Name.Contains("Config") || named.ContainingNamespace.ToDisplayString().Contains("Configs");

        private static bool HasSaveDataAttribute(ISymbol symbol) =>
            symbol.GetAttributes().Any(a => a.AttributeClass?.Name is "SaveData" or "SaveDataAttribute");

        private class TypeInfo
        {
            public string DtoTypeName = string.Empty;
            public string ModelTypeName = string.Empty;
            public bool IsReactive;
            public bool IsNestedSaveData;
            public bool IsCollection;
            public bool Skip;
            public bool IsConfig;
            public TypeInfo? CollectionElementType;

            public static TypeInfo SkipType() => new() { Skip = true };
        }
    }
}