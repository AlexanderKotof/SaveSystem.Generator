using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SaveDataGenerator
{
    [Generator]
    public class SaveDataSourceGenerator : ISourceGenerator
    {
        private static string GetLogPath()
        {
            try
            {
                // Пытаемся писать в Assets/Generated (относительно корня проекта)
                return Path.GetFullPath(Path.Combine("Temp", "Generated", "SourceGen_Debug.log"));
            }
            catch
            {
                // Фолбэк в TEMP, если права или путь недоступны
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
                // Если даже файл не пишется, выводим в стандартный вывод (попадает в Editor.log)
                Console.WriteLine($"[SaveGen LogFail] {ex.Message} | {message}");
            }
        }
        
        public void Initialize(GeneratorInitializationContext context) { }

        public void Execute(GeneratorExecutionContext context)
        {
            var created = new HashSet<INamedTypeSymbol>();
            
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

                            Log($"Generated {typeSymbol.Name}.SaveData.g.cs\nContent:\n{code}");
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

            if (members.Count == 0) return string.Empty;

            var usings = new HashSet<string>
            {
                "System"
            };

            var dtoFields = new List<string>();
            var toSaveLines = new List<string>();
            var applyLines = new List<string>();

            foreach (var m in members)
            {
                var typeInfo = ResolveType(m.Type, usings);

                if (typeInfo.Skip) continue;

                if (!string.IsNullOrEmpty(typeInfo.Namespace))
                    usings.Add(typeInfo.Namespace);

                var typeName = GetShortTypeName(typeInfo.TypeName, usings);
                
                var dtoPropName = typeInfo.IsConfig ? m.Name + "Id" : m.Name;
                dtoFields.Add($"public {typeName} {dtoPropName} {{ get; set; }}");

                // ToSaveData
                string readExpr = $"model.{m.Name}";

                if (typeInfo.IsReactive)
                    readExpr += ".Value";

                if (typeInfo.IsNestedSaveData)
                    readExpr += ".ToSaveData()";
                
                if (typeInfo.IsConfig)
                    readExpr += ".Id";

                toSaveLines.Add($"{dtoPropName} = {readExpr}");

                // ApplySaveData
                string writeExpr;

                if (typeInfo.IsReactive)
                {
                    writeExpr = $"model.{m.Name}.Value = {GetApplyValue(typeInfo, dtoPropName)};";
                }
                else if (typeInfo.IsConfig)
                {
                    //Reading only, left processing configs to higher layer
                    Log($"Config found {m.Name}!");
                    //writeExpr = $"model.{m.Name} = _resolver.Resolve({GetApplyValue(typeInfo, m.Name)});";
                    continue;
                }
                else if (m.SetMethod != null)
                {
                    writeExpr = $"model.{m.Name} = {GetApplyValue(typeInfo, dtoPropName)};";
                }
                else
                {
                    // get-only НЕ reactive → пропускаем
                    Log($"Trying to update get-only property {m.Name}!");
                    continue;
                }

                applyLines.Add(writeExpr);
            }

            if (dtoFields.Count == 0) return string.Empty;

            var sb = new StringBuilder();

            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine();
            
            // =========================
            // USINGS
            // =========================

            foreach (var u in usings.OrderBy(x => x))
                sb.AppendLine($"using {u};");

            sb.AppendLine();
            sb.AppendLine("#pragma warning disable");

            if (ns != null)
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
            }

            // =========================
            // DTO
            // =========================

            sb.AppendLine("[Serializable]");
            sb.AppendLine($"public struct {dtoName}");
            sb.AppendLine("{");

            foreach (var f in dtoFields)
                sb.AppendLine($"    {f}");

            sb.AppendLine("}");

            // =========================
            // EXTENSIONS
            // =========================

            sb.AppendLine($"public static class {type.Name}SaveExtensions");
            sb.AppendLine("{");

            // ToSaveData
            sb.AppendLine($"    public static {dtoName} ToSaveData(this {type.Name} model)");
            sb.AppendLine("    {");
            sb.AppendLine($"        return new {dtoName}");
            sb.AppendLine("        {");

            foreach (var l in toSaveLines)
                sb.AppendLine($"            {l},");

            sb.AppendLine("        };");
            sb.AppendLine("    }");

            // Apply
            sb.AppendLine($"    public static void ApplySaveData(this {type.Name} model, {dtoName} data)");
            sb.AppendLine("    {");

            foreach (var l in applyLines)
                sb.AppendLine($"        {l}");

            sb.AppendLine("    }");

            sb.AppendLine("}");

            if (ns != null)
                sb.AppendLine("}");

            sb.AppendLine("#pragma warning restore");

            return sb.ToString();
        }
        
        private static readonly Dictionary<string, string> _namesCache = new();

        private static string GetShortTypeName(string typeInfo, HashSet<string> usings)
        {
            if (_namesCache.TryGetValue(typeInfo, out var name))
            {
                return name;
            }
            
            string typeName = typeInfo;
            foreach (var ns in usings)
            {
                if (Equals(ns, "System"))
                    continue;
                
                string replce = ns + ".";
                if (typeName.Contains(replce))
                {
                    typeName = typeName.Replace(replce, string.Empty);
                }
            }
            _namesCache[typeInfo] = typeName;
            return typeName;
        }

        // =========================
        // TYPE RESOLUTION
        // =========================

        private static TypeInfo ResolveType(ITypeSymbol type, HashSet<string> usings)
        {
            var info = new TypeInfo();
            
            info.Namespace = type.ContainingNamespace?.ToDisplayString();

            if (type is not INamedTypeSymbol named)
            {
                info.TypeName = type.ToDisplayString();
                return info;
            }
            
            // --- вложенные SaveData
            if (HasSaveDataAttribute(named))
            {
                info.IsNestedSaveData = true;
                info.TypeName = $"{named.Name}SaveData";
                return info;
            }
            
            // --- вложенные SaveData
            if (IsConfig(named))
            {
                info.IsConfig = true;
                info.TypeName = $"string";
                return info;
            }
            
            info.TypeName = GetSerializedType(type, usings, out bool isReactive);
            info.IsReactive = isReactive;
            info.Skip = string.IsNullOrEmpty(info.TypeName);
            return info;
        }

        private static bool IsConfig(INamedTypeSymbol named)
        {
            return named.Name.Contains("Config") || named.ContainingNamespace.ToDisplayString().Contains("Configs");
        }

        private static string GetSerializedType(ITypeSymbol type, HashSet<string> usings, out bool isReactive)
        {
            isReactive = false;
            
            if (type == null || type.Kind == SymbolKind.ErrorType)
                return string.Empty;

            if (type is not INamedTypeSymbol named)
                return type.ToDisplayString();
            
            
            string defName = named.OriginalDefinition?.ToDisplayString() ?? string.Empty;
            string typeName = named.Name;

            // Пропускаем коллекции
            if (defName.Contains("ReactiveCollection") || typeName.Contains("ReactiveCollection"))
                return string.Empty;

            // UniRx ReactiveProperty<T>
            if ((defName.Contains("ReactiveProperty") || typeName.Contains("ReactiveProperty")) && named.TypeArguments.Length > 0)
            {
                var arg = named.TypeArguments[0];
                if (arg == null || arg.Kind == SymbolKind.ErrorType) return string.Empty;
                    
                usings.Add(arg.ContainingNamespace.ToDisplayString());
                isReactive = true;
                return arg.ToDisplayString();
            }

            // Кастомные реактивные (Vector3ReactiveProperty и т.д.)
            if (typeName.EndsWith("ReactiveProperty"))
            {
                isReactive = true;
                usings.Add("UnityEngine");
                return typeName.Replace("ReactiveProperty", "");
            }

            return type.ToDisplayString();
        }

        private static string GetApplyValue(TypeInfo info, string name)
        {
            if (info.IsNestedSaveData)
                return $"data.{name}.ToModel()"; // 👈 можно потом сгенерить обратный маппер

            return $"data.{name}";
        }

        // =========================
        // HELPERS
        // =========================

        private static bool HasSaveDataAttribute(ISymbol symbol)
        {
            return symbol.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "SaveData" or "SaveDataAttribute");
        }

        private class TypeInfo
        {
            public string TypeName;
            public string Namespace;
            public bool IsReactive;
            public bool IsNestedSaveData;
            public bool Skip;
            public bool IsConfig;
        }
    }
}