using Dalamud.Game;
using Dalamud.Plugin;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Logging;
using Lumina.Excel.GeneratedSheets;
using Lumina.Excel;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Threading;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;

namespace Auto_UmbrellaPlus;
public partial class Plugin : IDalamudPlugin
{
    public string Name => "Auto-umbrella+";
    public string Cmd => "/autoumbrella";
    public string CmdAlias => "/au";

    private readonly string[] SpecialCmds = new string[] { "silent", "autoswitch" };
    public string UsageMessage => $"Usage: {Cmd} [\"Umbrella name\"|Umbrella_id|silent|autoswitch] [job]\nExamples:\n{Cmd} \"{ornamentSheet.GetRow(1).Singular}\" {classJobSheet.GetRow(32).Name}\n{Cmd} 1 {classJobSheet.GetRow(32).Abbreviation}\n{Cmd} silent\n{CmdAlias} autoswitch\n{CmdAlias}";

    private readonly Configuration config;
    private readonly DalamudPluginInterface pluginInterface;
    private readonly ExcelSheet<Ornament> ornamentSheet;
    private readonly ExcelSheet<ClassJob> classJobSheet;

    private uint CurrentJodId;
    private byte t = 0;

    #region Regex Patterns
    [GeneratedRegex("\\d{1,3}")]
    private static partial Regex UmbrellaIdRegex();
    
    [GeneratedRegex("\".*?\"")]
    private static partial Regex UmbrellaNameMatch();

    [GeneratedRegex("^.*? selected as auto-umbrella\\.$")]
    private static partial Regex EnAutoUmbrellaLogMessage();
    
    [GeneratedRegex("^Vous avez enregistré .*? comme accessoire à utiliser automatiquement par temps de pluie\\.$")]
    private static partial Regex FrAutoUmbrellaLogMessage();
    
    [GeneratedRegex("^.*?を雨天時に自動使用するパラソルとして登録しました。$")]
    private static partial Regex JpAutoUmbrellaLogMessage();
    
    [GeneratedRegex("^Dieser Schirm wird bei Regen automatisch verwendet: .*?\\.$")]
    private static partial Regex DeAutoUmbrellaLogMessage();
    #endregion

    #region Sigs, Offset(s), Delegates, Hooks
    private const nint ManagerOffset = 0x860;
    private const string DisableAutoUmbrellaSig = "E8 ?? ?? ?? ?? 48 8B 4F 10 0F B7 5F 44";
    private const string AutoUmbrellaSetSig     = "E8 ?? ?? ?? ?? 84 C0 74 1E 48 8B 4F 10";
    private const string ExecuteCommandSig      = "E8 ?? ?? ?? ?? 8D 43 0A";

    private delegate long DisableAutoUmbrellaDelegate(nint MountManagerPtr);
    private delegate char AutoUmbrellaSetDelegate(nint MountManagerPtr, uint UmbrellaId);
    private delegate long ExecuteCommandDelegate(uint TriggerId, int a1, int a2, int a3, int a4);
    
    private static Hook<DisableAutoUmbrellaDelegate> DisableAutoUmbrellaHook;
    private static Hook<AutoUmbrellaSetDelegate> AutoUmbrellaSetHook;

    private readonly DisableAutoUmbrellaDelegate DisableAutoUmbrellaFn;
    private readonly AutoUmbrellaSetDelegate AutoUmbrellaSetFn;
    private readonly ExecuteCommandDelegate ExecuteCommand;
    #endregion


    private unsafe uint CurrentOrnamentId => ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)Service.ClientState.LocalPlayer.Address)->Ornament.OrnamentId;
    private static uint CurrentAutoUmbrella => (uint)Marshal.ReadInt32(Service.ClientState.LocalPlayer.Address + 0x890);
    private static bool UnkCondition(Ornament ornament) => ornament.Unknown1 == 1 && ornament.Unknown2 == 1 && ornament.Unknown3 == 1 && ornament.Unknown4 == 2; //unk1=1, unk2=1, unk3=1, unk4=2 seems to be a pattern common to all umbrellas
    private void PrintSetAutoUmbrella(ClassJob Job, Ornament Umbrella) => Service.Chat.Print($"[{Name}] {Umbrella.Singular} has been set as {Job.Abbreviation}'s auto-umbrella.");
    private void PrintDisabledAutoUmbrella(ClassJob Job) => Service.Chat.Print($"[{Name}] Auto-umbrella disabled for {Job.Abbreviation}.");
    private void PrintNotice(string Message) => Service.Chat.Print($"[{Name}] {Message}");
    private void PrintError(string Message) => Service.Chat.PrintError($"[{Name}] {Message}");

    private unsafe bool IsAutoUmbrellaEquipped() => CurrentOrnamentId != 0
            && (CurrentOrnamentId == config.JobIdToParasol[CurrentJodId] || CurrentOrnamentId == CurrentAutoUmbrella)
            && ornamentSheet.Where(row => UnkCondition(row) && row.RowId == CurrentOrnamentId).Any();

    private static bool TryGetCurrentAutoUmbrella(out uint AutoUmbrellaId)
    {
        AutoUmbrellaId = 0;
        if (Service.ClientState.LocalPlayer == null) return false;
        AutoUmbrellaId = CurrentAutoUmbrella;
        return true;
    }

    private void AutoUmbrellaSet(Ornament Umbrella) => AutoUmbrellaSet(classJobSheet.GetRow(Service.ClientState.LocalPlayer.ClassJob.GameData.RowId), Umbrella);
    private unsafe void AutoUmbrellaSet(ClassJob Job, Ornament Umbrella)
    {
        if (Job.RowId != Service.ClientState.LocalPlayer.ClassJob.GameData.RowId)
        {
            config.JobIdToParasol[Job.RowId] = Umbrella.RowId;
            if (!config.Silent)
                PrintSetAutoUmbrella(Job, Umbrella);
            return;
        }

        if (!config.Silent)
            PrintSetAutoUmbrella(Job, Umbrella);
        if (CurrentAutoUmbrella == Umbrella.RowId) return;

        var ManagerPtr = Service.ClientState.LocalPlayer.Address + ManagerOffset;
        if (ManagerPtr == nint.Zero) return;
        if (IsAutoUmbrellaEquipped() 
            && EnvManager.Instance()->ActiveWeather == 7
            || EnvManager.Instance()->ActiveWeather == 8)
        {
            ExecuteCommand(109, 0, 0, 0, 0);
        }
            
        AutoUmbrellaSetFn(ManagerPtr, Umbrella.RowId);
        RefreshAddonIfFound(382);
        return;
    }
    private void DisableAutoUmbrella() => DisableAutoUmbrella(classJobSheet.GetRow(Service.ClientState.LocalPlayer.ClassJob.GameData.RowId));
    private void DisableAutoUmbrella(ClassJob Job)
    {
        if (Job.RowId != Service.ClientState.LocalPlayer.ClassJob.GameData.RowId)
        {
            config.JobIdToParasol[Job.RowId] = 0;
            if (!config.Silent)
                PrintDisabledAutoUmbrella(Job);
            return;
        }

        if (!config.Silent)
            PrintDisabledAutoUmbrella(Job);
        if (CurrentAutoUmbrella == 0) return;

        var ManagerPtr = Service.ClientState.LocalPlayer.Address + ManagerOffset;
        if (ManagerPtr == nint.Zero) return;

        DisableAutoUmbrellaFn(ManagerPtr);
        RefreshAddonIfFound(382);
        if (IsAutoUmbrellaEquipped())
            ExecuteCommand(109, 0, 0, 0, 0);
        return;
    }
    private unsafe void RefreshAddonIfFound(uint agentInternalID)
    {
        var addonPtr = Service.GameGui.GetAddonByName($"{(AgentId)agentInternalID}");
        if (addonPtr == nint.Zero) return;

        ((AtkUnitBase*)addonPtr)->FireCallbackInt(-1);
        new Thread(() =>
        {
            Thread.Sleep(1);
            AgentModule.Instance()->GetAgentByInternalID(agentInternalID)->Show();
        }).Start();
    }

    private long DisableAutoUmbrellaDetour(nint MountManagerPtr)
    {
        PluginLog.Debug($"[DisableAutoUmbrellaDetour] MountManagerPtr: {MountManagerPtr} (0x{MountManagerPtr:X})");
        config.JobIdToParasol[Service.ClientState.LocalPlayer.ClassJob.Id] = 0;
        return DisableAutoUmbrellaHook.Original(MountManagerPtr);
    }
    private char AutoUmbrellaSetDetour(nint MountManagerPtr, uint UmbrellaId)
    {
        PluginLog.Debug($"[AutoUmbrellaSetDetour] MountManagerPtr: {MountManagerPtr} (0x{MountManagerPtr:X}), UmbrellaId: {UmbrellaId} (0x{UmbrellaId:X})");
        config.JobIdToParasol[Service.ClientState.LocalPlayer.ClassJob.Id] = UmbrellaId;
        return AutoUmbrellaSetHook.Original(MountManagerPtr, UmbrellaId);
    }

    public Plugin(DalamudPluginInterface PluginInterface)
    {
        pluginInterface = PluginInterface;
        pluginInterface.Create<Service>();

        config = (Configuration)pluginInterface.GetPluginConfig() ?? new Configuration();
        config.JobIdToParasol ??= new();

        ornamentSheet = Service.Data.Excel.GetSheet<Ornament>(Service.ClientState.ClientLanguage.ToLumina());
        classJobSheet = Service.Data.Excel.GetSheet<ClassJob>(Service.ClientState.ClientLanguage.ToLumina());

        if (ornamentSheet == null || classJobSheet == null)
        {
            PluginLog.Error("Ornament / ClassJob sheet is null");
            PrintError("Ornament / ClassJob sheets is null");
            return;
        }

        var DisableAutoUmbrellaPtr = Service.SigScanner.ScanText(DisableAutoUmbrellaSig);
        var AutoUmbrellaSetPtr = Service.SigScanner.ScanText(AutoUmbrellaSetSig);
        var ExecuteCommandPtr = Service.SigScanner.ScanText(ExecuteCommandSig);

        DisableAutoUmbrellaFn = Marshal.GetDelegateForFunctionPointer<DisableAutoUmbrellaDelegate>(DisableAutoUmbrellaPtr);
        AutoUmbrellaSetFn = Marshal.GetDelegateForFunctionPointer<AutoUmbrellaSetDelegate>(AutoUmbrellaSetPtr);
        ExecuteCommand = Marshal.GetDelegateForFunctionPointer<ExecuteCommandDelegate>(ExecuteCommandPtr);

        DisableAutoUmbrellaHook = Hook<DisableAutoUmbrellaDelegate>.FromAddress(DisableAutoUmbrellaPtr, DisableAutoUmbrellaDetour);
        AutoUmbrellaSetHook = Hook<AutoUmbrellaSetDelegate>.FromAddress(AutoUmbrellaSetPtr, AutoUmbrellaSetDetour);

        if (TryGetCurrentAutoUmbrella(out var autoUmbrellaId))
            config.JobIdToParasol[Service.ClientState.LocalPlayer.ClassJob.GameData.RowId] = autoUmbrellaId;

        Service.Commands.AddHandler(Cmd, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = $"sets your auto-umbrella for your current job / any given job\n{UsageMessage}",
            ShowInHelp = true
        });
        Service.Commands.AddHandler(CmdAlias, new Dalamud.Game.Command.CommandInfo(OnCommand) { 

            HelpMessage = $"alias of {Cmd}",
            ShowInHelp = true
        });

        Service.Toasts.Toast += OnToast;
        Service.Framework.Update += OnFrameworkUpdate;
        Service.Chat.ChatMessage += OnChatMessage;
        DisableAutoUmbrellaHook.Enable();
        AutoUmbrellaSetHook.Enable();
    }
    public void Dispose()
    {
        pluginInterface.SavePluginConfig(config);
        Service.Framework.Update -= OnFrameworkUpdate;
        Service.Toasts.Toast -= OnToast;
        Service.Chat.ChatMessage -= OnChatMessage;
        Service.Commands.RemoveHandler(Cmd);
        Service.Commands.RemoveHandler(CmdAlias);
        DisableAutoUmbrellaHook.Dispose();
        AutoUmbrellaSetHook.Dispose();
    }

    public void OnFrameworkUpdate(Framework framework)
    {
        if (t++ < 20 || Service.ClientState.LocalPlayer == null || !config.AutoSwitch)
            return;
        
        t = 0;

        if (CurrentJodId == 0)
            CurrentJodId = Service.ClientState.LocalPlayer.ClassJob.Id;

        if (CurrentJodId == Service.ClientState.LocalPlayer.ClassJob.Id)
            return;

        OnJobSwitch(CurrentJodId, Service.ClientState.LocalPlayer.ClassJob.Id);
        CurrentJodId = Service.ClientState.LocalPlayer.ClassJob.Id;
    }
    public unsafe void OnCommand(string command, string args)
    {
        args = args.Trim();
        var CurrentJob = classJobSheet.GetRow(Service.ClientState.LocalPlayer.ClassJob.GameData.RowId);
        if (args.Length == 0)
        {
            if (config.JobIdToParasol.TryGetValue(Service.ClientState.LocalPlayer.ClassJob.GameData.RowId, out uint UmbrellaId))
            {
                var Umbrella = ornamentSheet.GetRow(UmbrellaId);
                if (Umbrella.RowId != 0)
                    AutoUmbrellaSet(Umbrella);
                else
                    DisableAutoUmbrella();
                return;
            }
            PrintError($"No saved umbrella was found for your current job.");
            return;
        }
            
        if (SpecialCmds.Contains(args.ToLower()))
        {
            CmdSpecial(args);
            return;
        }

        var ornamentNameMatch = UmbrellaNameMatch().Match(args);
        var ornamentIdMatch = UmbrellaIdRegex().Match(args);

        // if neither a parasol name or an id are found print the usage syntax
        if (!ornamentNameMatch.Success && !ornamentIdMatch.Success)
        {
            Service.Chat.PrintError(UsageMessage);
            return;
        }

        Ornament ornament = ornamentNameMatch.Success
            ? ornamentSheet.Where(ornament => ornament.Singular == ornamentNameMatch.Value.Replace("\"", "")).FirstOrDefault()
            : ornamentSheet.GetRow(uint.Parse(ornamentIdMatch.Value));

        if (ornament == null)
        {
            if (!config.Silent)
                PrintError($"Could not find a valid ornament for name/id {(ornamentNameMatch.Success ? ornamentNameMatch.Value : ornamentIdMatch.Value)}.");
            return;
        }

        args = args.Replace($"\"{ornament.Singular}\"", "").Replace($"{ornament.RowId}", "").Trim().ToLower();
        ClassJob job = classJobSheet
            .Where(classJob => classJob.Name.ToString().ToLower() == args || classJob.Abbreviation.ToString().ToLower() == args).FirstOrDefault()
            ?? CurrentJob;
        CmdSetAutoUmbrella(job, ornament);
    }
    private void OnJobSwitch(uint lastJobId, uint currentJobId)
    {
        if (!config.AutoSwitch) return;
        var lastJob = classJobSheet.GetRow(lastJobId);
        var currentJob = classJobSheet.GetRow(currentJobId);

        PluginLog.Debug($"Switched Job from {lastJob.Abbreviation} to {currentJob.Abbreviation}");

        // if (for some reason) an entry for the given jobs switched from/to hasn't been made yet, create one
        TryGetCurrentAutoUmbrella(out var currentAutoUmbrella);
        if (!config.JobIdToParasol.ContainsKey(lastJobId))
            config.JobIdToParasol[lastJobId] = currentAutoUmbrella;
        if (!config.JobIdToParasol.ContainsKey(currentJobId))
            config.JobIdToParasol[currentJobId] = currentAutoUmbrella;

        var lastParasol = ornamentSheet.GetRow(config.JobIdToParasol[lastJobId]);
        var currentParasol = ornamentSheet.GetRow(config.JobIdToParasol[currentJobId]);

        PluginLog.Debug($"Switched Umbrella from {lastParasol.Singular} to {currentParasol.Singular}");

        //                                                     in case there is a mismatch
        if (lastParasol.RowId == currentParasol.RowId || CurrentAutoUmbrella == currentParasol.RowId)
            return;

        if (currentParasol.RowId == 0)
        {
            PluginLog.Debug($"Calling DisableAutoUmbrella()");
            DisableAutoUmbrella(currentJob);
            return;
        }

        PluginLog.Debug($"Calling AutoUmbrellaSet({currentParasol.RowId})");
        AutoUmbrellaSet(currentJob, currentParasol);
    }
    private void OnChatMessage(XivChatType type, uint senderId, ref SeString sender, ref SeString message, ref bool isHandled) => AppendJob(ref message, ref isHandled);
    private void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled) => AppendJob(ref message, ref isHandled);
    private void AppendJob(ref SeString message, ref bool isHandled)
    {
        if (isHandled) return;
        Regex pattern = Service.ClientState.ClientLanguage switch
        {
            ClientLanguage.English => EnAutoUmbrellaLogMessage(),
            ClientLanguage.French => FrAutoUmbrellaLogMessage(),
            ClientLanguage.Japanese => JpAutoUmbrellaLogMessage(),
            ClientLanguage.German => DeAutoUmbrellaLogMessage(),
            _ => null
        };
        if (pattern == null) return;
        if (pattern.Match(message.ToString()).Success)
            message.Append(new TextPayload($" ({classJobSheet.GetRow(Service.ClientState.LocalPlayer.ClassJob.GameData.RowId).Abbreviation})"));
    }

    public unsafe void CmdSetAutoUmbrella(ClassJob Job, Ornament Umbrella)
    {
        if (Umbrella.RowId == 0)
        {
            DisableAutoUmbrella(Job);
            return;
        }

        if (!UnkCondition(Umbrella))
        {
            if (!config.Silent)
                PrintError($"{Umbrella.Singular} is not an umbrella so it cannot be set as an auto-umbrella.");
            return;
        }

        if (!PlayerState.Instance()->IsOrnamentUnlocked(Umbrella.RowId))
        {
            if (!config.Silent)
                PrintError($"{Umbrella.Singular} is not unlocked so it cannot set it as an auto-umbrella.");
            return;
        }

        AutoUmbrellaSet(Job, Umbrella);
    }
    private void CmdSpecial(string args)
    {
        switch (args.ToLower())
        {
            case "silent":
                config.Silent = !config.Silent;
                PrintNotice($"Log messages on command execution will no{(config.Silent?" longer":"w")} be displayed.");
                break;
            case "autoswitch":
                config.AutoSwitch = !config.AutoSwitch;
                PrintNotice($"Auto-umbrella will no{(!config.AutoSwitch ? " longer" : "w")} be switched automatically on job change.");
                break;
        }
    }
}
