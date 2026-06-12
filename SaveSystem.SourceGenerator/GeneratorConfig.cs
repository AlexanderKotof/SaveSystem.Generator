using Microsoft.CodeAnalysis.Diagnostics;

namespace SaveDataGenerator;

public record GeneratorConfig
{
    public bool EmitLogs { get; set; } = false;
    public bool EnableNullchecks { get; set; } = true;
    public string NullableContext { get; set; } = "enable"; // enable, annotations, disable

    public static GeneratorConfig Load(AnalyzerConfigOptions options)
    {
        return new GeneratorConfig
        {
            EmitLogs = options.GetBool("build_property.SaveDataGenerator_EmitLogs", true),
            EnableNullchecks = options.GetBool("build_property.SaveDataGenerator_EnableNullchecks", true),
            NullableContext = options.GetString("build_property.SaveDataGenerator_NullableContext") ?? "enable"
        };
    }
}
