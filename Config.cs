using Dalamud.Configuration;
using Dalamud.Plugin;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace Auto_UmbrellaPlus;
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    [JsonIgnore] private DalamudPluginInterface pluginInterface;

    public Dictionary<int, uint> GearsetIndexToParasol { get; set; } = new();
    public Dictionary<int, byte> GearsetIndexToCpose { get; set; } = new();
    public bool Silent;
    public bool AutoSwitch = true;

    public void Initialize(DalamudPluginInterface PluginInterface)
    {
        pluginInterface = PluginInterface;
    }

    public void Save()
    {
        pluginInterface.SavePluginConfig(this);
    }
}
