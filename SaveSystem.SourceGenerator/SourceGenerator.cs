using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;

namespace SaveDataGenerator
{
    [Generator]
    public class SaveDataSourceGenerator : ISourceGenerator
    {
        private AttributeSyntax? _saveDataAttribute;
        private static GeneratorConfig _config = default!;

        private static bool NullableEnabled => _config.EnableNullchecks;
        private static string n => NullableEnabled ? "?" : String.Empty;

        public void Initialize(GeneratorInitializationContext context) { }

        public void Execute(GeneratorExecutionContext context)
        {
            try
            {
                _config = GeneratorConfig.Load(context.AnalyzerConfigOptions.GlobalOptions);
            }
            catch (Exception ex)
            {
                Logger.Log($"Load config exception: {ex}");
                _config = new();
            }

            var stopwatch = Stopwatch.StartNew();
            Logger.Log("*** Begin source code generation ***");

            if (!_config.EmitLogs)
                Logger.Disable();

            try
            {
                TryFindSaveDataAttribute(context);
            }
            catch (Exception ex)
            {
                Logger.Log($"Save Data attribute not found: {ex}");
            }

            var created = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
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

                    Logger.Log($"Generating {typeSymbol.Name}...");

                    try
                    {
                        var code = Generate(typeSymbol);
                        if (!string.IsNullOrWhiteSpace(code))
                        {
                            context.AddSource($"{typeSymbol.Name}.SaveData.g.cs", SourceText.From(code, Encoding.UTF8));
                            Logger.Log($"Generated {typeSymbol.Name}.SaveData.g.cs\n");
                        }
                        else
                        {
                            Logger.Log($"Generated empty output for {typeSymbol.Name}");
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.Log("Exception: " + e.Message);
                    }
                }
            }

            Logger.Log($"*** End source code generation, elapsed {stopwatch.Elapsed.TotalMilliseconds}ms ***");
        }

        private void TryFindSaveDataAttribute(GeneratorExecutionContext context)
        {
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                var saveDataAttributeDecl = tree.GetRoot().DescendantNodes().OfType<AttributeSyntax>()
                    .FirstOrDefault(s => s.Name.ToString().Contains("SaveData"));

                if (saveDataAttributeDecl != null)
                {
                    Logger.Log($"*****  Found save data attribute {saveDataAttributeDecl.ToString()}, {saveDataAttributeDecl.Name.ToString()}");
                    _saveDataAttribute = saveDataAttributeDecl;
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

            // TODO: add fields support later
            // members.AddRange(type.GetMembers()
            //     .OfType<IFieldSymbol>()
            //     .Where(HasSaveDataAttribute));

            // Collect props from parent types
            var iterate = type;
            while (iterate.BaseType != null)
            {
                iterate = iterate.BaseType;
                var ms = iterate.GetMembers().OfType<IPropertySymbol>().Where(HasSaveDataAttribute);
                members.AddRange(ms);
            }

            if (members.Count == 0) return string.Empty;

            var usings = new HashSet<string>
            {
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "UnityEngine",
                "SaveSystem.Interfaces"
            };
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
            var selector = AttributeHelper.GetSaveDataSelector(m);
            var typeInfo = ResolveType(m.Type, usings, selector);

            if (typeInfo.Skip) return;

            var dtoPropName = string.Concat(m.Name, selector);
            dtoFields.Add($"public {typeInfo.DtoTypeName} {dtoPropName} {{ get; set; }}");

            // ToSaveData
            string readExpr = $"model.{m.Name}";
            if (typeInfo.IsReactive) readExpr += ".Value";

            // If it is nested data, but not selected
            if (typeInfo.IsNestedSaveData && selector == null) readExpr += $".ToSaveData()";

            if (typeInfo.IsCollection)
            {
                var elemMap = GetElementMapExpr(typeInfo.CollectionElementType!, selector, out var selectRequired);
                var filter = AttributeHelper.GetSaveDataFilter(m);
                var filterExpr = filter == null ? string.Empty : $".Where({filter})";
                var selectorExpr = selectRequired ? $".Select(x => {elemMap})" : string.Empty;

                var saveLine = $"{dtoPropName} = {readExpr}{n}{filterExpr}{selectorExpr}{(NullableEnabled ?
                    $".ToArray() ?? Array.Empty<{typeInfo.CollectionElementType!.DtoTypeName}>()" : ".ToArray()")}";
                toSaveLines.Add(saveLine);
            }
            else
            {
                if (selector != null) readExpr += $"{n}.{selector}";
                toSaveLines.Add($"{dtoPropName} = {readExpr}");
            }

            // ApplySaveData
            var writeExpr = GetWriteExpression(m, typeInfo, dtoPropName);
            if (!string.IsNullOrEmpty(writeExpr))
            {
                applyLines.Add(writeExpr);
            }
        }

        private static string GetElementMapExpr(TypeInfo info, string? selector, out bool selectRequired)
        {
            selectRequired = true;
            if (!string.IsNullOrEmpty(selector)) return $"x.{selector}";
            if (info.IsNestedSaveData) return $"x.ToSaveData()";
            if (info.IsReactive) return "x.Value";

            selectRequired = false;
            return "x";
        }

        private static string GetWriteExpression(IPropertySymbol m, TypeInfo typeInfo, string dtoPropName)
        {
            var modelExpr = $"model.{m.Name}";
            var dataExpr = $"data.{dtoPropName}";

            if (typeInfo.IsCollection)
            {
                return $"//*** Data Collection: {m.Name}";
            }

            if (typeInfo.IsNestedSaveData && typeInfo.IsReactive)
            {
                return $"{modelExpr}.Value{n}.ApplySaveData({dataExpr});";
            }

            if (typeInfo.IsNestedSaveData)
            {
                return $"{modelExpr}{n}.ApplySaveData({dataExpr});";
            }

            if (typeInfo.IsReactive)
            {
                return $"{modelExpr}.Value = {dataExpr};";
            }

            if (typeInfo.HasSelector)
            {
                return $"//*** {m.Name} uses Select attribute. Auto-restore skipped.";
            }

            if (m.SetMethod is { DeclaredAccessibility: Accessibility.Public })
            {
                return $"{modelExpr} = {dataExpr};";
            }

            Logger.Log($"Trying to update get-only non reactive property {m.Name}!");
            return $"//*** {modelExpr} is get-only. Skip applying.";
        }

        // =========================
        // OUTPUT GENERATION
        // =========================

        private static string GenerateOutput(INamedTypeSymbol type, HashSet<string> usings, string? ns, string dtoName, List<string> dtoFields, List<string> toSaveLines, List<string> applyLines)
        {
            int indentation = 0;
            var sb = new StringBuilder();

            WriteLine("// *** This file is auto-generated by SaveSystem.SourceGenerator ***");
            WriteLine("// *** All changes will be missing ***");
            WriteLine();

            if (_config.EnableNullchecks)
            {
                WriteLine($"#nullable {_config.NullableContext}");
                WriteLine();
            }

            foreach (var u in usings.OrderBy(x => x))
                WriteLine($"using {u};");

            WriteLine();
            WriteLine("#pragma warning disable");

            if (ns != null)
            {
                WriteLine($"namespace {ns}");
                WriteLine("{");
            }

            indentation++;

            WriteLine("[Serializable]");
            WriteLine($"public struct {dtoName} : ISaveData");
            WriteLine("{");

            indentation++;

            foreach (var f in dtoFields) WriteLine($"{f}");

            indentation--;

            WriteLine("}");

            WriteLine($"public static class {type.Name}SaveExtensions");
            WriteLine("{");

            indentation++;

            WriteLine();

            // ToSaveData
            WriteLine($"public static {dtoName} ToSaveData(this {type.Name} model)");
            WriteLine("{");

            indentation++;

            WriteLine("if (model == null)");
            WriteLine("{");

            indentation++;
            WriteLine("Debug.LogError(\"Cannot convert Model {nameof(" + type.Name + ")}: model is null.\");");
            WriteLine("return default;");
            indentation--;

            WriteLine("}");

            WriteLine();
            WriteLine($"return new {dtoName}");
            WriteLine("{");

            indentation++;

            foreach (var l in toSaveLines) WriteLine($"{l},");

            indentation--;

            WriteLine("};");

            indentation--;

            WriteLine("}");

            WriteLine();

            // ApplySaveData
            WriteLine($"public static void ApplySaveData(this {type.Name} model, {dtoName} data)");
            WriteLine("{");

            indentation++;

            WriteLine(@"if (model == null)");
            WriteLine("{");

            indentation++;
            WriteLine("Debug.LogError(\"Can not apply save data! Model {nameof(" + type.Name + @")} is null."");");
            WriteLine("return;");
            indentation--;

            WriteLine("}");
            WriteLine();

            foreach (var l in applyLines) WriteLine($"{l}");

            WriteLine("}");

            indentation--;

            WriteLine("}");

            indentation--;

            if (ns != null) WriteLine("}");

            WriteLine("#pragma warning restore");

            return sb.ToString();

            void WriteLine(string? line = null)
            {
                if (line == null)
                {
                    sb.AppendLine();
                    return;
                }

                for (int i = 0; i < indentation; i++) sb.Append("\t");
                sb.AppendLine(line);
            }
        }

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

        private static TypeInfo ResolveType(ITypeSymbol type, HashSet<string> usings, string? selector = null)
        {
            if (type == null || type.Kind == SymbolKind.ErrorType) return TypeInfo.SkipType();

            var info = new TypeInfo();
            var ns = type.ContainingNamespace?.ToDisplayString();
            if (!string.IsNullOrEmpty(ns)) usings.Add(ns);

            if (type is INamedTypeSymbol named)
            {
                // 🔹 1. Коллекции
                if (TryResolveCollection(named, usings, out ITypeSymbol? elementType))
                {
                    // Если есть селектор — применяем его к типу элемента
                    if (!string.IsNullOrEmpty(selector))
                    {
                        var selectedType = GetPropertyType(elementType, selector);

                        Logger.Log($"Resolving selected type: success= {selectedType != null}");

                        if (selectedType != null)
                        {
                            var selectedInfo = ResolveType(selectedType, usings, null);
                            info.IsCollection = true;
                            info.CollectionElementType = selectedInfo;
                            info.DtoTypeName = $"{selectedInfo.DtoTypeName}[]";
                            info.ModelTypeName = named.ToDisplayString();


                            Logger.Log($"Resolved selected type:  ModelTypeName:{info.ModelTypeName}, CollectionElementType:{info.CollectionElementType}, DtoTypeName:{info.DtoTypeName}");

                            return info;
                        }
                    }

                    var elemInfo = ResolveType(elementType, usings, null);
                    info.IsCollection = true;
                    info.CollectionElementType = elemInfo;
                    info.DtoTypeName = $"{elemInfo.DtoTypeName}[]";
                    info.ModelTypeName = named.ToDisplayString();
                    return info;
                }

                // 🔹 2. ReactiveProperty<T> — проваливаемся внутрь с тем же селектором
                var defName = named.OriginalDefinition?.ToDisplayString() ?? string.Empty;
                if ((defName.Contains("ReactiveProperty") || named.Name.Contains("ReactiveProperty")) &&
                    named.TypeArguments.Length > 0)
                {
                    var inner = ResolveType(named.TypeArguments[0], usings, selector);
                    inner.IsReactive = true;
                    return inner;
                }

                if (named.Name.EndsWith("ReactiveProperty"))
                {
                    return ResolveType(named.BaseType!, usings, selector);
                }

                // 🔹 3. Селектор для не-коллекции
                if (!string.IsNullOrEmpty(selector))
                {
                    var selectedType = GetPropertyType(named, selector);
                    if (selectedType != null)
                    {
                        var selectedInfo = ResolveType(selectedType, usings, null);
                        selectedInfo.Selector = selector;
                        selectedInfo.ModelTypeName = named.ToDisplayString();
                        return selectedInfo;
                    }
                }

                // 🔹 4. Вложенный SaveData
                if (HasSaveDataAttribute(named))
                {
                    info.IsNestedSaveData = true;
                    info.DtoTypeName = $"{named.Name}SaveData";
                    info.ModelTypeName = named.ToDisplayString();
                    return info;
                }
            }
            // 🔹 5. Массивы
            else if (type is IArrayTypeSymbol arrayType)
            {
                var elementType = arrayType.ElementType;
                if (!string.IsNullOrEmpty(selector))
                {
                    var selectedType = GetPropertyType(elementType, selector);
                    if (selectedType != null)
                    {
                        var selectedInfo = ResolveType(selectedType, usings, null);
                        info.IsCollection = true;
                        info.CollectionElementType = selectedInfo;
                        info.DtoTypeName = $"{selectedInfo.DtoTypeName}[]";
                        info.ModelTypeName = type.ToDisplayString();
                        return info;
                    }
                }

                var elemInfo = ResolveType(elementType, usings, null);
                info.IsCollection = true;
                info.CollectionElementType = elemInfo;
                info.DtoTypeName = $"{elemInfo.DtoTypeName}[]";
                info.ModelTypeName = type.ToDisplayString();
                return info;
            }

            // 🔹 Примитивы / обычные типы
            info.DtoTypeName = GetShortTypeName(type, usings);
            info.ModelTypeName = type.ToDisplayString();
            return info;
        }

        // Вспомогательный метод: находит тип свойства по имени
        private static ITypeSymbol? GetPropertyType(ITypeSymbol type, string propertyName)
        {
            var current = type;
            while (current != null && current.Kind != SymbolKind.ErrorType)
            {
                if (current is INamedTypeSymbol named)
                {
                    var prop = named.GetMembers(propertyName)
                        .OfType<IPropertySymbol>()
                        .FirstOrDefault(p =>
                            p.DeclaredAccessibility != Accessibility.Private &&
                            !p.IsStatic);

                    if (prop != null) return prop.Type;
                }

                current = current.BaseType;
            }
            return null;
        }

        private static bool TryResolveCollection(INamedTypeSymbol named, HashSet<string> usings, out ITypeSymbol? elementType)
        {
            elementType = null;
            var defName = named.OriginalDefinition?.ToDisplayString() ?? string.Empty;

            bool isCollection = named.TypeKind == TypeKind.Array ||
                                defName.Contains("List") || defName.Contains("IList") ||
                                defName.Contains("ICollection") || defName.Contains("ReactiveCollection") ||
                                defName.Contains("IEnumerable");

            if (isCollection && named.SpecialType == SpecialType.System_String) return false;
            if (!isCollection || (!named.IsGenericType && named.TypeKind != TypeKind.Array)) return false;

            elementType = named.TypeKind == TypeKind.Array
                ? ((IArrayTypeSymbol)named).ElementType
                : named.TypeArguments.FirstOrDefault();

            return elementType != null && elementType.Kind != SymbolKind.ErrorType;
        }

        private static bool HasSaveDataAttribute(ISymbol symbol) =>
            GetSaveDataAttribute(symbol) != null;

        private static AttributeData? GetSaveDataAttribute(ISymbol symbol) =>
            symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "SaveData" or "SaveDataAttribute");

        private class TypeInfo
        {
            public string DtoTypeName = string.Empty;
            public string ModelTypeName = string.Empty;
            public bool IsReactive;
            public bool IsNestedSaveData;
            public bool IsCollection;
            public string Selector = string.Empty;
            public bool HasSelector => !string.IsNullOrEmpty(Selector);
            public bool Skip;
            public TypeInfo? CollectionElementType;

            public static TypeInfo SkipType() => new() { Skip = true };
        }
    }
}
