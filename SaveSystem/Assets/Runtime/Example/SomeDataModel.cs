using System;
using System.Collections.Generic;
using SaveSystem.Attributes;
using UniRx;

namespace SaveSystem.Example
{
    [SaveData]
    public class GameDataModel
    {
        [SaveData]
        public SomeDataModel SomeDataModel { get; } = new();

        // ...
    }

    [SaveData]
    public class SomeDataModel
    {
        [SaveData]
        public ReactiveProperty<int> Health { get; } = new ();

        [SaveData]
        public ReactiveCollection<GameConfigBase> Configs { get; } = new ();

        [SaveData]
        public IEnumerable<string> SomeSavedStrings => new [] { "abc", "def", "ghi", "jkl" };

        // ...
    }
}
