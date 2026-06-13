# SaveSystem.SourceGenerator

![Version](https://img.shields.io/badge/version-1.0-blue) ![Unity](https://img.shields.io/badge/Unity-2021.3+-black) ![License](https://img.shields.io/badge/license-MIT-green)

A flexible Roslyn Source Generator for automatic mapping of complex Unity/C# game models to serialization-ready DTOs (Data Transfer Objects). Eliminate boilerplate code and ensure type-safe, maintainable save/load systems.

---

## 🎯 Overview

**SaveSystem.SourceGenerator** automatically generates:
- Serializable DTO structs (`YourModelSaveData`)
- Extension methods: `ToSaveData()` and `ApplySaveData()`

Mark your classes and properties with `[SaveData]`, and the generator handles the rest — including reactive properties, nested objects, collections, filtering, and property selection.

Perfect for Unity projects using **UniRx**.

The package has no dependencies. This means you can use any serializer you want.

## ✨ Key Features

### 🔹 Attribute-Driven Mapping
```csharp
[SaveData]
public class SomeDataModel
{
    [SaveData]
    public SerializableData SomeData { get; set; }
    
    [SaveData]
    public ReactiveProperty<int> Health { get; } = new();
}
```

### 🔹 ReactiveProperty Support
Automatically unwraps ReactiveProperty<T> values during serialization:
```csharp
// Generated:
Health = model.Health.Value,
// Applied:
model.Health.Value = data.Health;
```

### 🔹 Collection Handling
All collection types (List<T>, IEnumerable<T>, ReactiveCollection<T>, arrays) are converted to arrays for reliable serialization:
```csharp
[SaveData]
public IEnumerable<string> SomeSavedStrings => new[] { "abc", "def", "ghi" };
// Generated:
SomeSavedStrings = model.SomeSavedStrings.ToArray() ?? Array.Empty<string>();
```

### 🔹 Advanced Filtering & Selection
Use Filter and Select parameters to customize what gets saved:
```csharp
// Save only IDs from ScriptableObject references
[SaveData(Select = "Id")]
public ReactiveCollection<GameConfigBase> Configs { get; } = new();

// Filter + Select: save names of items matching a condition
[SaveData(Filter = "x => x.someData == 42", Select = "name")]
public IEnumerable<GameConfigBase> ConfigsWithSelector => Configs;
```

Generated output:
```csharp
ConfigsId = model.Configs.Select(x => x.Id).ToArray() ?? Array.Empty<Guid>(),
ConfigsWithSelector = model.ConfigsWithSelector
    .Where(x => x.someData == 42)
    .Select(x => x.name)
    .ToArray() ?? Array.Empty<string>(),
```

### 🔹 Nested SaveData Objects
Supports recursive generation for nested [SaveData] classes:
```csharp
[SaveData]
public class GameDataModelAggregate
{
    [SaveData]
    public SomeDataModel SomeDataModel { get; } = new();
}
// Generates: GameDataModelAggregateSaveData containing SomeDataModelSaveData
```

### 🔹 Get-Only Property Handling
Get-only properties are serialized but safely skipped during apply (with logged warning):
```csharp
// Generated ApplySaveData:
//*** model.SerializableDataGetOnly is get-only. Skip applying.
```

### 🔹 Configurable Logging & Null Checks
Control generator behavior via .editorconfig or build properties:
```ini
# .editorconfig or Directory.Build.props
build_property.SaveDataGenerator_EmitLogs = true
build_property.SaveDataGenerator_EnableNullchecks = true
build_property.SaveDataGenerator_NullableContext = enable
```

---

## 🚀 Getting Started
### 1. Install
You can simply add package through the Package Manager window. Press **[+]/Install package from git url...** and insert following:
```
https://github.com/AlexanderKotof/SaveSystem.git?path=/SaveSystem/Assets
```

Or just insert next line in ProjectRoot/Packages/manifest.json:
```
{
	"com.newbeedev.save-system-gen": "https://github.com/AlexanderKotof/SaveSystem.git?path=/SaveSystem/Assets",
	...
}
```

The generator source code is also available, so if you want to customize generator behaviour you will need to clone repository, update and build generator solution. After that will be possible to add updated Package from disk (see [documentation](https://docs.unity3d.com/6000.3/Documentation/Manual/upm-ui-local.html).

### 2. Annotate Your Models
```csharp
using SaveSystem.Attributes;

[SaveData]
public class PlayerData
{
    [SaveData]
    public string PlayerName { get; set; }
    
    [SaveData]
    public ReactiveProperty<int> Level { get; } = new(1);
    
    [SaveData(Select = "itemId")]
    public List<InventoryItem> Items { get; } = new();
}
```

### 3. Use Generated Extensions
```csharp
// Save
var dto = playerData.ToSaveData();
var json = JsonUtility.ToJson(dto);
PlayerPrefs.SetString("save", json);

// Load
var json = PlayerPrefs.GetString("save");
var dto = JsonUtility.FromJson<PlayerDataSaveData>(json);
playerData.ApplySaveData(dto);
```

## 📦 Generated Code Example
For the SomeDataModel class, the generator produces:
```csharp
[Serializable]
public struct SomeDataModelSaveData : ISaveData
{
    public SerializableData SerializableData { get; set; }
    public SerializableData SerializableDataGetOnly { get; set; }
    public int Health { get; set; }
    public Guid[] Configs { get; set; }
    public string[] SomeSavedStrings { get; set; }
    public Guid[] ConfigsIdWithFilter { get; set; }
    public string[] ConfigsWithSelector { get; set; }
}

public static class SomeDataModelSaveExtensions
{
    public static SomeDataModelSaveData ToSaveData(this SomeDataModel model)
    {
        if (model == null) {
            Debug.LogError($"Cannot convert Model {nameof(SomeDataModel)}: model is null.");
            return default;
        }
        return new SomeDataModelSaveData
        {
            SerializableData = model.SerializableData,
            SerializableDataGetOnly = model.SerializableDataGetOnly,
            Health = model.Health.Value,
            Configs = model.Configs.Select(x => x.Id).ToArray() ?? Array.Empty<Guid>(),
            SomeSavedStrings = model.SomeSavedStrings.ToArray() ?? Array.Empty<string>(),
            ConfigsIdWithFilter = model.ConfigsIdWithFilter
                .Where(x => x.someData == 42)
                .Select(x => x.Id)
                .ToArray() ?? Array.Empty<Guid>(),
            ConfigsWithSelector = model.ConfigsWithSelector
                .Where(x => x.someData == 42)
                .Select(x => x.name)
                .ToArray() ?? Array.Empty<string>(),
        };
    }

    public static void ApplySaveData(this SomeDataModel model, SomeDataModelSaveData data)
    {
        if (model == null) {
            Debug.LogError($"Can not apply save data! Model {nameof(SomeDataModel)} is null.");
            return;
        }
        model.SerializableData = data.SerializableData;
        //*** model.SerializableDataGetOnly is get-only. Skip applying.
        model.Health.Value = data.Health;
        //*** Data Collection: Configs
        //*** Data Collection: SomeSavedStrings
        //*** Data Collection: ConfigsIdWithFilter
        //*** Data Collection: ConfigsWithSelector
    }
}
```

|💡 **Note**: Collection restoration requires manual rehydration (e.g., resolving IDs back to objects). See SaveSystemExample.cs for patterns:
```csharp
private void ApplyLoadedData(GameDataModelAggregate model, GameDataModelAggregateSaveData dto)
{
    model.ApplySaveData(dto); // Generated method
    
    // Manual resolution for collections
    foreach (var id in dto.SomeDataModel.Configs)
    {
        model.SomeDataModel.Configs.Add(_configsMap[id]);
    }
}
```

---

## ⚙️ Configuration Reference
|Property | Default | Description |
| ------- | -------- | ----------- |
| SaveDataGenerator_EmitLogs | false | Enable/disable generator diagnostic logs |
| SaveDataGenerator_EnableNullchecks | true | Inject null-check guards in generated methods |
| SaveDataGenerator_NullableContext | "enable" | Nullable annotation context: enable, annotations, disable |

Set via:

* .editorconfig
* Directory.Build.props
* Project file <PropertyGroup>

---

## 🗓️ Roadmap
### ✅ Current (v1.0)

* Core DTO generation with [SaveData] attributes
* ReactiveProperty & collection support
* Filter/Select attributes for advanced mapping
* Nested model handling with recursive generation
* Unity-compatible output with null-safety
* Get-only property handling with safe skip logic

### 🔜 Planned

* Auto-tests suite for generator output validation and regression testing
* Auto-resolvers: Configure ID→object resolution patterns for collections via [SaveData(ResolveWith = "MyResolver")]
* Support for [field: SaveData] on auto-properties and primary constructors
* Async save/load helpers with progress callbacks
* JSON schema export for DTOs and validation tools

---

## 🤝 Contributing & Feedback
Found a bug? Have a feature request? Want to improve documentation?
👉 Open an issue or start a discussion in the repository.
We welcome:

* Bug reports with reproduction steps
* Feature proposals with use cases
* Documentation improvements
* Performance optimizations

---

## 📄 License
MIT License — feel free to use, modify, and distribute in personal and commercial projects.
	