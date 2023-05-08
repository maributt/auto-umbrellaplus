using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace Auto_UmbrellaPlus;
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; }
    public Dictionary<uint, uint> JobIdToParasol;
    public bool Silent;
    public bool AutoSwitch = true;

    [JsonIgnore] private DalamudPluginInterface pluginInterface;

    public void Initialize(DalamudPluginInterface PluginInterface)
    {
        pluginInterface = PluginInterface;
    }

    public void Save()
    {
        pluginInterface.SavePluginConfig(this);
    }
}
