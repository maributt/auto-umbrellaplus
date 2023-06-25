using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.Toast;
using Dalamud.IoC;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Auto_UmbrellaPlus;

class Service
{
    [PluginService] static internal ChatGui Chat { get; private set; }
    [PluginService] static internal DataManager Data { get; private set; }
    [PluginService] static internal GameGui GameGui { get; private set; }
    [PluginService] static internal SigScanner SigScanner { get; private set; }
    [PluginService] static internal CommandManager Commands { get; private set; }
    [PluginService] static internal ClientState ClientState { get; private set; }
    [PluginService] public static ToastGui Toasts { get; private set; }
}
