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
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using System.Text;
using System;

namespace Auto_UmbrellaPlus;
public partial class Plugin : IDalamudPlugin
{
    public string Name => "Auto-umbrella+";
    public string Cmd => "/autoumbrella";
    public string CmdCpose => "/autoumbrellacpose";
    public string CmdAlias => "/au";
    public string CmdCposeAlias => "/auc";

    private readonly string[] SpecialCmds = new string[] { "silent", "autoswitch", "use", "list" };
    public string AuUsageMessage => $"Usage: {Cmd} [\"Umbrella name\"|Umbrella_id|{string.Join('|', SpecialCmds)}] [job]\nExamples:\n{Cmd} \"{ornamentSheet.GetRow(1).Singular}\" dark knight\n{Cmd} 1 DRK\n{Cmd} silent\n{CmdAlias} autoswitch\n{CmdAlias} use\n{CmdAlias}";

    private Configuration config;
    private readonly DalamudPluginInterface pluginInterface;
    private readonly ExcelSheet<Ornament> ornamentSheet;
    private readonly ExcelSheet<ClassJob> classJobSheet;

    private bool externalCall = false;
    private Dictionary<int, string> Gearsets = new();

    #region Regex Patterns
    [GeneratedRegex("\\d{1,3}")]
    private static partial Regex IdRegex();
    
    [GeneratedRegex("\"(.*?)\"")]
    private static partial Regex UmbrellaNameMatch();

    [GeneratedRegex("(^.*?) selected as auto-umbrella\\.$")]
    private static partial Regex EnAutoUmbrellaLogMessage();
    
    [GeneratedRegex("^Vous avez enregistré (.*?) comme accessoire à utiliser automatiquement par temps de pluie\\.$")]
    private static partial Regex FrAutoUmbrellaLogMessage();
    
    [GeneratedRegex("(^.*?)を雨天時に自動使用するパラソルとして登録しました。$")]
    private static partial Regex JpAutoUmbrellaLogMessage();
    
    [GeneratedRegex("^Dieser Schirm wird bei Regen automatisch verwendet: (.*?)\\.$")]
    private static partial Regex DeAutoUmbrellaLogMessage();
    #endregion

    #region Sigs, Offset(s), Delegates, Hooks
    private readonly nint OrnamentManagerOffset         = 0x878;
    private readonly nint CurrentAutoUmbrellaOffset     = 0x8AC; // not sigged
    private readonly nint SomeManagerOffset             = 0x620; 
    private const uint    OrnamentNoteBookId            = 383;

    private const string EquipGearsetSig        = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F9 41 0F B6 F0 48 8D 0D";
    private const string ChangePoseSig          = "E8 ?? ?? ?? ?? B0 01 E9 ?? ?? ?? ?? 48 8B CF 4C 89 6D C7";
    private const string DisableAutoUmbrellaSig = "E8 ?? ?? ?? ?? 48 8B 4F 10 0F B7 5F 44";
    private const string AutoUmbrellaSetSig     = "E8 ?? ?? ?? ?? 84 C0 74 1E 48 8B 4F 10";
    private const string AnimateIntoPoseSig     = "E8 ?? ?? ?? ?? 40 0F B6 DE 45 33 C9";
    private const string PosesArraySig          = "48 8D 05 ?? ?? ?? ?? 0F B6 1C 38";
    private const string SomeManagerOffsetSig   = "4C 8B F9 48 8D A8 ?? ?? ?? ??";
    private const string CurrentParasolCposeSig = "E8 ?? ?? ?? ?? 40 3A F0 75 19";
    private const string OrnamentManagerSig     = "74 66 48 81 C1 ?? ?? ?? ??";
    private const string ExecuteCommandSig      = "E8 ?? ?? ?? ?? 8D 43 0A";
    // TODO: hook gearset update functions to update gearset list when needed

    private delegate nint   ChangePoseDelegate(nint unk1, nint unk2);
    private delegate long   EquipGearsetDelegate(nint a1, int destGearsetIndex, ushort a3);
    private delegate long   DisableAutoUmbrellaDelegate(nint MountManagerPtr);
    private delegate char   AutoUmbrellaSetDelegate(nint MountManagerPtr, uint UmbrellaId);
    private delegate long   ExecuteCommandDelegate(uint TriggerId, int a1, int a2, int a3, int a4);
    private delegate void   AnimateIntoPoseDelegate(nint SomeManager, uint unk1, ushort PoseIndex, uint unk2);
    private delegate ushort CurrentUmbrellaCposeDelegate(nint SomeManager);

    private static Hook<EquipGearsetDelegate>           EquipGearsetHook;
    private static Hook<DisableAutoUmbrellaDelegate>    DisableAutoUmbrellaHook;
    private static Hook<AutoUmbrellaSetDelegate>        AutoUmbrellaSetHook;
    private static Hook<ExecuteCommandDelegate>         ExecuteCommandHook;
    private static Hook<ChangePoseDelegate>             ChangePoseHook;

    private readonly DisableAutoUmbrellaDelegate        DisableAutoUmbrellaFn;
    private readonly AutoUmbrellaSetDelegate            AutoUmbrellaSetFn;
    private readonly ExecuteCommandDelegate             ExecuteCommand;
    private readonly AnimateIntoPoseDelegate            AnimateIntoPoseFn;
    private readonly CurrentUmbrellaCposeDelegate       CurrentUmbrellaCposeFn;


    #endregion

    #region Helpers
    private unsafe uint CurrentOrnamentId => ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)Service.ClientState.LocalPlayer.Address)->Ornament.OrnamentId;
    private uint CurrentAutoUmbrella => (uint)Marshal.ReadInt32(Service.ClientState.LocalPlayer.Address + CurrentAutoUmbrellaOffset);
    private static bool UnkCondition(Ornament ornament) => ornament.Unknown1 == 1 && ornament.Unknown2 == 1 && ornament.Unknown3 == 1 && ornament.Unknown4 == 2; //unk1=1, unk2=1, unk3=1, unk4=2 seems to be a pattern common to all umbrellas
    private void PrintSetAutoUmbrella(int gearsetIndex, Ornament Umbrella) => Service.Chat.Print($"[{Name}] {Umbrella.Singular} has been set as the auto-umbrella for gearset \"{GearsetName(gearsetIndex)}\".");
    private void PrintDisabledAutoUmbrella(int gearsetIndex) => Service.Chat.Print($"[{Name}] Auto-umbrella disabled for gearset \"{GearsetName(gearsetIndex)}\".");
    private void PrintNotice(string Message) => Service.Chat.Print($"[{Name}] {Message}");
    private void PrintError(string Message) => Service.Chat.PrintError($"[{Name}] {Message}");
    private unsafe int CurrentGearsetIndex => RaptureGearsetModule.Instance()->CurrentGearsetIndex;
    private unsafe string GearsetNameAlt(int gearsetIndex) => string.Join("", SeString.Parse(RaptureGearsetModule.Instance()->GetGearset(gearsetIndex)->Name,47).TextValue.Where(c=>(byte)c!=0));
    private unsafe string GearsetName(int gearsetIndex) => SplitOnByte(Encoding.UTF8.GetString(RaptureGearsetModule.Instance()->GetGearset(gearsetIndex)->Name, 47), 0);
    private unsafe string SplitOnByte(string str, byte split)
    {
        string result = "";
        string ndhalf = "";
        bool splitEncountered = false;
        var barray = str.Select(c=>(byte)c).ToList();
        for (int i = 0; i < barray.Count; i++)
        {
            var b = barray[i];
            if (splitEncountered)
            {
                ndhalf += (char)b;
                continue;
            }
            
            if (b == split)
            {
                splitEncountered = true;
                continue;
            }
            result += (char)b;
        }
        
        PluginLog.Verbose($"first half: [{string.Join(' ', result.Select(c => ((byte)c)))}] {result} second half: [{string.Join(' ', ndhalf.Select(c => ((byte)c)))}] {ndhalf}");
        return result;
    }
    private unsafe bool IsRaining => EnvManager.Instance()->ActiveWeather == 7 || EnvManager.Instance()->ActiveWeather == 8;
    private unsafe bool IsAutoUmbrellaEquipped => IsUmbrellaEquipped && (CurrentOrnamentId == CurrentAutoUmbrella || CurrentOrnamentId == config.GearsetIndexToParasol[CurrentGearsetIndex]);
    private unsafe bool IsUmbrellaEquipped => CurrentOrnamentId!=0 && ornamentSheet.Where(row => UnkCondition(row) && row.RowId == CurrentOrnamentId).Any();

    private byte CurrentUmbrellaCpose => Marshal.ReadByte(Service.SigScanner.GetStaticAddressFromSig(PosesArraySig) + 0x5);
    private bool TryGetCurrentAutoUmbrella(out uint AutoUmbrellaId)
    {
        AutoUmbrellaId = 0;
        if (Service.ClientState.LocalPlayer == null) return false;
        AutoUmbrellaId = CurrentAutoUmbrella;
        return true;
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
    private unsafe Dictionary<int, string> PopulateGearsetList()
    {
        var gearsetModule = RaptureGearsetModule.Instance();
        var dict = new Dictionary<int, string>();
        for (int i = 0; i < 100; i++)
        {
            if (gearsetModule->IsValidGearset(i) == 1 && gearsetModule->Gearset[i]->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                dict.Add(i, GearsetName(i));
        }
        return dict;
    }
    private static ushort JobToColor(byte classJob)
    {
        return classJob switch
        {
            1 or 19 => 34,  //GLA/PLD
            2 or 20 => 527, //PGL/MNK
            3 or 21 => 518, //MRD/WAR
            4 or 22 => 37,  //LNC/DRG
            5 or 23 => 42,  //ARC/BRD
            6 or 24 => 8,   //CNJ/WHM
            7 or 25 => 522, //THM/BLM
            26 or 27 => 41, //ARC/SMN
            28 => 56,       //SCH
            29 or 30 => 16, //ROG/NIN
            31 => 39,       //MCH
            32 => 541,      //DRK
            33 => 506,      //AST
            34 => 500,      //SAM
            35 => 508,      //RDM
            36 => 542,      //BLU
            37 => 520,      //GNB
            38 => 515,      //DNC
            39 => 9,        //RPR
            40 => 56,       //SGE
            >= 8 and <= 15 => 2,    // Crafters
            16 or 17 or 18 => 24,   // Gatherers
            _ => 0
        };
    }
    private void AppendGearsetName(ref SeString message, ref bool isHandled)
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
        var match = pattern.Match(message.ToString());
        if (match.Success)
            message = new SeString(new TextPayload($"{match.Groups[1]} selected as auto-umbrella for gearset \"{GearsetName(CurrentGearsetIndex)}\"."));
    }
    #endregion

    #region Game functions
    private void ChangeUmbrellaCpose(byte newPose)
    {
        // +0 = standing pose; +1 = weapon pose; +2 = chair sitting pose; +3 = ground sitting pose; +4 = sleeping pose; +5 = umbrella pose; +6 = other accessory pose;
        var umbrellaCposeIntPtr = Service.SigScanner.GetStaticAddressFromSig(PosesArraySig) + 0x5;
        if (newPose == CurrentUmbrellaCpose || newPose < 0 || newPose > 3)
            return;
        Marshal.WriteByte(umbrellaCposeIntPtr, newPose);
        if (!IsAutoUmbrellaEquipped)
            return;
        AnimateIntoPoseFn(Service.ClientState.LocalPlayer.Address + SomeManagerOffset, 5, newPose, 0);
        ExecuteCommand(505, 5, newPose, 0, 0);
        ExecuteCommand(506, 5, newPose, 0, 0);
    }

    private unsafe void AutoUmbrellaSet(Ornament Umbrella)
    {
        if (CurrentAutoUmbrella == Umbrella.RowId || Umbrella.RowId == 0) return;

        var ManagerPtr = Service.ClientState.LocalPlayer.Address + OrnamentManagerOffset;
        if (ManagerPtr == nint.Zero) return;
        // if it's rainy / storming, take off umbrella to automatically take the new one out
        // if done through a macro (not automatically) it would add a "/fashion" command line
        if (IsAutoUmbrellaEquipped && IsRaining)
            ExecuteCommand(109, 0, 0, 0, 0);

        externalCall = true;
        AutoUmbrellaSetFn(ManagerPtr, Umbrella.RowId);
        RefreshAddonIfFound(OrnamentNoteBookId);
    }
    private void DisableAutoUmbrella()
    {
        if (CurrentAutoUmbrella == 0) return;

        var ManagerPtr = Service.ClientState.LocalPlayer.Address + OrnamentManagerOffset;
        if (ManagerPtr == nint.Zero) return;
        DisableAutoUmbrellaFn(ManagerPtr);
        RefreshAddonIfFound(OrnamentNoteBookId);
        if (IsAutoUmbrellaEquipped)
            ExecuteCommand(109, 0, 0, 0, 0);
        return;
    }
    #endregion

    #region Detours
    private long DisableAutoUmbrellaDetour(nint MountManagerPtr)
    {
        config.GearsetIndexToParasol[CurrentGearsetIndex] = 0;
        config.Save();
        return DisableAutoUmbrellaHook.Original(MountManagerPtr);
    }
    private char AutoUmbrellaSetDetour(nint MountManagerPtr, uint UmbrellaId)
    {
        if (externalCall)
        {
            externalCall = false;
            return AutoUmbrellaSetHook.Original(MountManagerPtr, UmbrellaId);
        }
        config.GearsetIndexToParasol[CurrentGearsetIndex] = UmbrellaId;
        config.Save();
        return AutoUmbrellaSetHook.Original(MountManagerPtr, UmbrellaId);
    }
    private long EquipGearsetDetour(nint a1, int destGearsetIndex, ushort a3)
    {
        PluginLog.Verbose($"OnGearsetSwitch({CurrentGearsetIndex}, {destGearsetIndex});");
        OnGearsetSwitch(CurrentGearsetIndex, destGearsetIndex);
        return EquipGearsetHook.Original(a1, destGearsetIndex, a3);
    }

    private nint ChangePoseDetour(nint unk1, nint unk2)
    {
        PluginLog.Debug($"{Service.SigScanner.GetStaticAddressFromSig(PosesArraySig):X}");
        PluginLog.Debug($"isUmbrellaEquipped: {IsUmbrellaEquipped}, CurrentOrnamentId: {CurrentOrnamentId}");
        if (!IsUmbrellaEquipped) return ChangePoseHook.Original(unk1, unk2);

        var nextCpose = (byte)((CurrentUmbrellaCpose + 1) % 4); // TODO: replace 4 with a sig to get the max cpose index eventually tm
        config.GearsetIndexToCpose[CurrentGearsetIndex] = nextCpose;
        config.Save();
        
        return ChangePoseHook.Original(unk1, unk2);
    }
    #endregion

    public Plugin(DalamudPluginInterface PluginInterface)
    {
        pluginInterface = PluginInterface;
        pluginInterface.Create<Service>();

        config = (Configuration)pluginInterface.GetPluginConfig() ?? new Configuration();
        if (config.Version < 2)
        {
            config.GearsetIndexToCpose = new();
        }
        config.GearsetIndexToParasol ??= new();
        config.Initialize(pluginInterface);

        ornamentSheet = Service.Data.Excel.GetSheet<Ornament>(Service.ClientState.ClientLanguage.ToLumina());
        classJobSheet = Service.Data.Excel.GetSheet<ClassJob>(Service.ClientState.ClientLanguage.ToLumina());

        if (ornamentSheet == null || classJobSheet == null)
        {
            PluginLog.Error("Ornament/ClassJob sheet is null");
            PrintError("Ornament/ClassJob sheet is null");
            return;
        }

        var DisableAutoUmbrellaPtr  = Service.SigScanner.ScanText(DisableAutoUmbrellaSig);
        var AutoUmbrellaSetPtr      = Service.SigScanner.ScanText(AutoUmbrellaSetSig);
        var ExecuteCommandPtr       = Service.SigScanner.ScanText(ExecuteCommandSig);
        var EquipGearsetPtr         = Service.SigScanner.ScanText(EquipGearsetSig);
        var AnimateIntoPosePtr      = Service.SigScanner.ScanText(AnimateIntoPoseSig);
        var OrnamentManagerOffsetPtr= Service.SigScanner.ScanText(OrnamentManagerSig) + 0x5;
        var CurrentUmbrellaCposePtr = Service.SigScanner.ScanText(CurrentParasolCposeSig);
        var SomeManagerOffsetPtr    = Service.SigScanner.ScanText(SomeManagerOffsetSig) + 0x6;
        var ChangePosePtr           = Service.SigScanner.ScanText(ChangePoseSig);

        DisableAutoUmbrellaFn       = Marshal.GetDelegateForFunctionPointer<DisableAutoUmbrellaDelegate>(DisableAutoUmbrellaPtr);
        AutoUmbrellaSetFn           = Marshal.GetDelegateForFunctionPointer<AutoUmbrellaSetDelegate>(AutoUmbrellaSetPtr);
        ExecuteCommand              = Marshal.GetDelegateForFunctionPointer<ExecuteCommandDelegate>(ExecuteCommandPtr);
        AnimateIntoPoseFn           = Marshal.GetDelegateForFunctionPointer<AnimateIntoPoseDelegate>(AnimateIntoPosePtr);
        CurrentUmbrellaCposeFn      = Marshal.GetDelegateForFunctionPointer<CurrentUmbrellaCposeDelegate>(CurrentUmbrellaCposePtr); 

        EquipGearsetHook            = Hook<EquipGearsetDelegate>.FromAddress(EquipGearsetPtr, EquipGearsetDetour);
        DisableAutoUmbrellaHook     = Hook<DisableAutoUmbrellaDelegate>.FromAddress(DisableAutoUmbrellaPtr, DisableAutoUmbrellaDetour);
        AutoUmbrellaSetHook         = Hook<AutoUmbrellaSetDelegate>.FromAddress(AutoUmbrellaSetPtr, AutoUmbrellaSetDetour);
        ChangePoseHook              = Hook<ChangePoseDelegate>.FromAddress(ChangePosePtr, ChangePoseDetour);

        OrnamentManagerOffset       = Marshal.ReadInt32(OrnamentManagerOffsetPtr);
        SomeManagerOffset           = Marshal.ReadInt32(SomeManagerOffsetPtr);

        if (TryGetCurrentAutoUmbrella(out var autoUmbrellaId))
        {
            config.GearsetIndexToParasol[CurrentGearsetIndex] = autoUmbrellaId;
            config.GearsetIndexToCpose[CurrentGearsetIndex] = CurrentUmbrellaCpose;
        }

        Service.Commands.AddHandler(Cmd, new Dalamud.Game.Command.CommandInfo(OnAuCommand)
        {
            HelpMessage = $"sets your auto-umbrella for your current gearset / any given job or gearset\n{AuUsageMessage}",
            ShowInHelp = true
        });
        Service.Commands.AddHandler(CmdAlias, new Dalamud.Game.Command.CommandInfo(OnAuCommand) { 

            HelpMessage = $"alias of {Cmd}",
            ShowInHelp = true
        });
        Service.Commands.AddHandler(CmdCpose, new Dalamud.Game.Command.CommandInfo(OnAucCommand)
        {
            HelpMessage = $"sets your umbrella cpose for your current gearset / any given job or gearset",
            ShowInHelp = true
        });
        Service.Commands.AddHandler(CmdCposeAlias, new Dalamud.Game.Command.CommandInfo(OnAucCommand)
        {

            HelpMessage = $"alias of {CmdCpose}",
            ShowInHelp = true
        });

        Service.Toasts.Toast += OnToast;
        Service.Chat.ChatMessage += OnChatMessage;

        DisableAutoUmbrellaHook.Enable();
        AutoUmbrellaSetHook.Enable();
        EquipGearsetHook.Enable();
        ChangePoseHook.Enable();
    }
    public void Dispose()
    {
        pluginInterface.SavePluginConfig(config);

        Service.Toasts.Toast -= OnToast;
        Service.Chat.ChatMessage -= OnChatMessage;

        Service.Commands.RemoveHandler(Cmd);
        Service.Commands.RemoveHandler(CmdAlias);
        Service.Commands.RemoveHandler(CmdCpose);
        Service.Commands.RemoveHandler(CmdCposeAlias);

        DisableAutoUmbrellaHook.Dispose();
        AutoUmbrellaSetHook.Dispose();
        EquipGearsetHook.Dispose();
        ChangePoseHook.Dispose();
    }

    #region Events

    // todo: extract common logic to auc/au
    // fix: cant write cpose for umbrella if umbrella isn't out?
    public unsafe void OnAucCommand(string command, string args)
    {
        args = args.Trim();
        // /auc 3 job
        if (args.Length == 0)
        {
            if (!config.GearsetIndexToParasol.ContainsKey(CurrentGearsetIndex))
                config.GearsetIndexToParasol[CurrentGearsetIndex] = CurrentAutoUmbrella;
            if (!config.GearsetIndexToCpose.ContainsKey(CurrentGearsetIndex))
                config.GearsetIndexToCpose[CurrentGearsetIndex] = CurrentUmbrellaCpose;
            CmdSetUmbrellaCpose(new KeyValuePair<int, string>(CurrentGearsetIndex, GearsetName(CurrentGearsetIndex)), config.GearsetIndexToCpose[CurrentGearsetIndex]);
            return;
        }

        var idMatch = IdRegex().Match(args);
        if (!idMatch.Success)
        {
            return;
        }

        var cposeId = byte.Parse(idMatch.Value);
        Gearsets = PopulateGearsetList();
        args = args.Replace(idMatch.Value, "").Trim();
        if (args.Length == 0)
        {
            CmdSetUmbrellaCpose(new KeyValuePair<int, string>(CurrentGearsetIndex, GearsetName(CurrentGearsetIndex)), cposeId);
            return;
        }
        var gearsetMatches = Gearsets.Where(gearset => gearset.Value.Contains(args, System.StringComparison.Ordinal));
        if (!gearsetMatches.Any()) 
        {
            // no matching gearset msg
            return;
        }
        CmdSetUmbrellaCpose(gearsetMatches.First(), cposeId);
    }
    public unsafe void OnAuCommand(string command, string args)
    {
        args = args.Trim();
        if (args.Length == 0)
        {
            if (!config.GearsetIndexToParasol.ContainsKey(CurrentGearsetIndex))
                config.GearsetIndexToParasol[CurrentGearsetIndex] = CurrentAutoUmbrella;
            if (!config.GearsetIndexToCpose.ContainsKey(CurrentGearsetIndex))
                config.GearsetIndexToCpose[CurrentGearsetIndex] = CurrentUmbrellaCpose;

            var Umbrella = ornamentSheet.GetRow(config.GearsetIndexToParasol[CurrentGearsetIndex]);
            if (Umbrella.RowId != 0)
                AutoUmbrellaSet(Umbrella);
            else
                DisableAutoUmbrella();
            return;
        }

        if (SpecialCmds.Contains(args.ToLower()))
        {
            AuCmdSpecial(args);
            return;
        }

        var ornamentNameMatch = UmbrellaNameMatch().Match(args);
        var idMatch = IdRegex().Match(args);

        // if neither a parasol name or an id are found print the usage syntax
        if (!ornamentNameMatch.Success && !idMatch.Success)
        {
            Service.Chat.PrintError(AuUsageMessage);
            return;
        }

        Ornament? ornament = ornamentNameMatch.Success
            ? ornamentSheet.Where(ornament => ornament.Singular.ToString().Contains(ornamentNameMatch.Groups[1].Value, System.StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
            : ornamentSheet.GetRow(uint.Parse(idMatch.Value));

        if (ornament == null)
        {
            if (!config.Silent)
                PrintError($"Could not find a valid ornament for name/id {(ornamentNameMatch.Success ? ornamentNameMatch.Value : idMatch.Value)}.");
            return;
        }

        Gearsets = PopulateGearsetList();

        args = (ornamentNameMatch.Success 
            ? args.Replace(ornamentNameMatch.Value, "")
            : args.Replace(idMatch.Value, "")).Trim();

        if (args.Length == 0)
        {
            CmdSetAutoUmbrella(new KeyValuePair<int, string>(CurrentGearsetIndex, GearsetName(CurrentGearsetIndex)), ornament);
            return;
        }
            
        var gearsetMatches = Gearsets.Where(gearset => gearset.Value.Contains(args, System.StringComparison.Ordinal));
        if (!gearsetMatches.Any())
        {
            PrintError($"Could not find a gearset (partially) matching the name \"{args}\"");
            return;
        }

        CmdSetAutoUmbrella(gearsetMatches.First(), ornament);
    }
    private void OnGearsetSwitch(int lastGearsetIndex, int destGearsetIndex)
    {
        if (!config.AutoSwitch) return;

        if (!config.GearsetIndexToCpose.ContainsKey(lastGearsetIndex))
            config.GearsetIndexToCpose[lastGearsetIndex] = CurrentUmbrellaCpose;
        if (!config.GearsetIndexToCpose.ContainsKey(destGearsetIndex))
            config.GearsetIndexToCpose[destGearsetIndex] = CurrentUmbrellaCpose;
        PluginLog.Debug($"Switching Cpose from {config.GearsetIndexToCpose[lastGearsetIndex]} to {config.GearsetIndexToCpose[destGearsetIndex]}");
        ChangeUmbrellaCpose(config.GearsetIndexToCpose[destGearsetIndex]);

        PluginLog.Verbose($"Switched Gearset from \"{GearsetName(lastGearsetIndex)}\" (#{lastGearsetIndex}) to \"{GearsetName(destGearsetIndex)}\" (#{destGearsetIndex})");
        // if (for some reason) an entry for the given jobs switched from/to hasn't been made yet, create one
        if (!config.GearsetIndexToParasol.ContainsKey(lastGearsetIndex))
            config.GearsetIndexToParasol[lastGearsetIndex] = CurrentAutoUmbrella;
        if (!config.GearsetIndexToParasol.ContainsKey(destGearsetIndex))
            config.GearsetIndexToParasol[destGearsetIndex] = CurrentAutoUmbrella;

        var lastParasol = ornamentSheet.GetRow(config.GearsetIndexToParasol[lastGearsetIndex]);
        var destParasol = ornamentSheet.GetRow(config.GearsetIndexToParasol[destGearsetIndex]);

        PluginLog.Verbose($"Switched Umbrella from {lastParasol.Singular} (#{lastParasol.RowId}) to {destParasol.Singular} (#{destParasol.RowId})");

        //                                                     in case there is a mismatch
        if (lastParasol.RowId == destParasol.RowId || CurrentAutoUmbrella == destParasol.RowId)
            return;

        if (destParasol.RowId == 0)
        {
            PluginLog.Verbose($"Calling DisableAutoUmbrella()");
            DisableAutoUmbrella();
            return;
        }

        PluginLog.Verbose($"Calling AutoUmbrellaSet({destGearsetIndex}, {destParasol.RowId})");
        if (!config.Silent)
            PrintSetAutoUmbrella(destGearsetIndex, destParasol);
        AutoUmbrellaSet(destParasol);
    }
    private void OnChatMessage(XivChatType type, uint senderId, ref SeString sender, ref SeString message, ref bool isHandled) => AppendGearsetName(ref message, ref isHandled);
    private void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled) => AppendGearsetName(ref message, ref isHandled);
    #endregion

    #region Cmd logic

    public unsafe void CmdSetUmbrellaCpose(KeyValuePair<int, string> Gearset, byte CposeId)
    {
        if (Gearset.Key == CurrentGearsetIndex)
            ChangeUmbrellaCpose(CposeId);
        else
            config.GearsetIndexToCpose[Gearset.Key] = CposeId;
    }
    public unsafe void CmdSetAutoUmbrella(KeyValuePair<int, string> Gearset, Ornament Umbrella)
    {
        var shouldInvokeFn = Gearset.Key == CurrentGearsetIndex;

        if (!PlayerState.Instance()->IsOrnamentUnlocked(Umbrella.RowId))
        {
            if (!config.Silent)
                PrintError($"{Umbrella.Singular} is not unlocked so it cannot set it as an auto-umbrella.");
            return;
        }

        if (Umbrella.RowId == CurrentAutoUmbrella)
        {
            PrintError($"{Umbrella.Singular} is already set as the auto-umbrella for gearset \"{Gearset.Value}\"");
            return;
        }

        if (Umbrella.RowId == 0)
        {
            config.GearsetIndexToParasol[Gearset.Key] = 0;
            if (shouldInvokeFn)
                DisableAutoUmbrella();
            return;
        }

        if (!UnkCondition(Umbrella))
        {
            if (!config.Silent)
                PrintError($"{Umbrella.Singular} is not an umbrella so it cannot be set as an auto-umbrella.");
            return;
        }

        config.GearsetIndexToParasol[Gearset.Key] = Umbrella.RowId;
        config.Save();
        if (!config.Silent)
            PrintNotice($"{Umbrella.Singular} has been set as the auto-umbrella for gearset \"{Gearset.Value}\".");
        if (shouldInvokeFn)
            AutoUmbrellaSet(Umbrella);
    }
    private unsafe void AuCmdSpecial(string args)
    {
        switch (args.ToLower())
        {
            case "silent":
                config.Silent = !config.Silent;
                PrintNotice($"Log messages on command execution will no{(config.Silent?" longer":"w")} be displayed.");
                break;
            case "autoswitch" or "as":
                config.AutoSwitch = !config.AutoSwitch;
                PrintNotice($"Auto-umbrella will no{(!config.AutoSwitch ? " longer" : "w")} be switched automatically on job change.");
                break;
            case "reset":
                config = new();
                PrintNotice("Config has been reset.");
                config.Save();
                break;
            case "use":
                if (CurrentAutoUmbrella == 0) return;
                ActionManager.Instance()->UseAction(ActionType.Accessory, CurrentAutoUmbrella);
                break;
            case "list":
                Service.Chat.Print($"Below are the gearsets you've registered parasols for... (count: {config.GearsetIndexToParasol.Count})");
                foreach (var entry in config.GearsetIndexToParasol)
                {
                    var Message = new List<Payload>()
                        {
                            new UIForegroundPayload(JobToColor(RaptureGearsetModule.Instance()->Gearset[entry.Key]->ClassJob)),
                            new TextPayload($"{GearsetName(entry.Key)}: "),
                            new UIForegroundPayload(0),
                            new TextPayload(entry.Value == 0 ? $"None" : $"{ornamentSheet.GetRow(entry.Value).Singular}")
                        };
                    if (config.GearsetIndexToCpose.TryGetValue(entry.Key, out var cposeEntry))
                        Message.Add(new TextPayload($" (#{cposeEntry})"));
                    Service.Chat.PrintChat(new XivChatEntry() { Message = new SeString(Message) });
                };
                break;
        }
        config.Save();
    }
    #endregion
}
