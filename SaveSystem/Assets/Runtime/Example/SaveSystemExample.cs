using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SaveSystem.Example
{
    public class SaveSystemExample : MonoBehaviour
    {
        public GameConfigBase[] gameConfigs;
        private Dictionary<Guid, GameConfigBase> _configsMap;

        [ContextMenu("Test")]
        public void Test()
        {
            _configsMap = gameConfigs.ToDictionary(v => v.Id, v => v);
            
            var model = new GameDataModel();

            model.SomeDataModel.Health.Value = 100;
            foreach (var config in gameConfigs)
            {
                model.SomeDataModel.Configs.Add(config);
               // model.SomeDataModel.ConfigsMap.Add(config.Id, config);
            }

            var json = Save(model);
            var model2 = new GameDataModel();

            Load(json, model2);

            Debug.Assert(model.SomeDataModel.Health.Value == model2.SomeDataModel.Health.Value);
            Debug.Assert(model2.SomeDataModel.Configs.Count == model.SomeDataModel.Configs.Count);
        }


        public string Save(GameDataModel model)
        {
            // Generated method
            var dto = model.ToSaveData();

            // serialize it as you want

            // write to file, send to server, etc.

            var json = JsonUtility.ToJson(dto);
            Debug.Log($"Data written:\n {json}");
            return json;
        }

        public void Load(string json, GameDataModel model)
        {
            var dto = JsonUtility.FromJson<GameDataModelSaveData>(json);

            // applying data from dto

            ApplyLoadedData(model, dto);

            // re-serialization for test purposes
            var outputJson = JsonUtility.ToJson(model.ToSaveData());
            Debug.Log($"Data read:\n {outputJson}");
        }

        private void ApplyLoadedData(GameDataModel model, GameDataModelSaveData dto)
        {
            // Generated method
            model.ApplySaveData(dto);

            //Note: not all properties can be automatically resolved, so we should fill them manually

            foreach (var id in dto.SomeDataModel.Configs)
            {
                model.SomeDataModel.Configs.Add(_configsMap[id]);
            }

            Debug.Log($"Saved strings: {string.Join(',', dto.SomeDataModel.SomeSavedStrings)}')");

        }
    }
}
