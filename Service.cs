using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.Toast;
using Dalamud.IoC;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Plugin.Services;

namespace Auto_UmbrellaPlus;

class Service
{
    [PluginService] public static IChatGui Chat { get; private set; }
    [PluginService] public static IDataManager Data { get; private set; }
    [PluginService] public static IGameGui GameGui { get; private set; }
    [PluginService] public static ISigScanner SigScanner { get; private set; }
    [PluginService] public static ICommandManager Commands { get; private set; }
    [PluginService] public static IClientState ClientState { get; private set; }
    [PluginService] public static IToastGui Toasts { get; private set; }
    [PluginService] public static IPluginLog PluginLog { get; private set; }
    [PluginService] public static IGameInteropProvider GameInteropProvider { get; private set; }
}
