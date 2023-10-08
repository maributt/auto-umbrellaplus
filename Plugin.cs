using Dalamud;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Auto_UmbrellaPlus;
public partial class Plugin : IDalamudPlugin
{
    public static string Name => "Auto-umbrella+";
    public static string Cmd => "/autoumbrella";
    public static string CmdCpose => "/autoumbrellacpose";
    public static string CmdAlias => "/au";
    public static string CmdCposeAlias => "/auc";

    private readonly string[] SpecialCmds = new string[] { "silent", "autoswitch", "use", "list" };
    public string AuUsageMessage => $"Usage: {Cmd} [\"Umbrella name\"|Umbrella_id|{string.Join('|', SpecialCmds)}] [job]\nExamples:\n{Cmd} \"{ornamentSheet.GetRow(1).Singular}\" dark knight\n{Cmd} 1 DRK\n{Cmd} silent\n{CmdAlias} autoswitch\n{CmdAlias} use\n{CmdAlias}";

    private Configuration config;
    private readonly DalamudPluginInterface pluginInterface;
    private readonly ExcelSheet<Ornament> ornamentSheet;
    private readonly ExcelSheet<ClassJob> classJobSheet;

    private bool externalCall = false;
    private unsafe Dictionary<int, string> Gearsets
    {
        get
        {
            var gearsetModule = RaptureGearsetModule.Instance();
            var dict = new Dictionary<int, string>();
            for (int i = 0; i < 100; i++)
            {
                if (gearsetModule->IsValidGearset(i) && gearsetModule->GetGearset(i)->Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists))
                    dict.Add(i, GearsetName(i));
            }
            return dict;
        }
    }

    #region Sigs, Delegates, Hooks

#pragma warning disable CS0649

    [Signature(Signatures.OrnamentManagerOffsetSig, Offset = 0x5)]
    private readonly uint OrnamentManagerOffset;

    [Signature(Signatures.CurrentAutoUmbrellaOffsetSig, Offset = 0x6)]
    private readonly byte CurrentAutoUmbrellaOffset;

    [Signature(Signatures.SomeManagerOffsetSig, Offset = 0x6)]
    private readonly uint SomeManagerOffset;

#pragma warning restore CS0649

    private const uint OrnamentNoteBookId = (uint)FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.OrnamentNoteBook;

    private delegate nint   ChangePoseDelegate(nint unk1, nint unk2);
    private delegate long   EquipGearsetDelegate(nint a1, int destGearsetIndex, ushort a3);
    private delegate long   DisableAutoUmbrellaDelegate(nint MountManagerPtr);
    private delegate char   AutoUmbrellaSetDelegate(nint MountManagerPtr, uint UmbrellaId);
    private delegate long   ExecuteCommandDelegate(uint TriggerId, int a1, int a2, int a3, int a4);
    private delegate void   AnimateIntoPoseDelegate(nint SomeManager, uint unk1, ushort PoseIndex, uint unk2);
    private delegate ushort CurrentUmbrellaCposeDelegate(nint SomeManager);
    private delegate uint   GetAvailablePosesDelegate(PoseType poseType);

    private static Hook<EquipGearsetDelegate>           EquipGearsetHook;
    private static Hook<DisableAutoUmbrellaDelegate>    DisableAutoUmbrellaHook;
    private static Hook<AutoUmbrellaSetDelegate>        AutoUmbrellaSetHook;
    private static Hook<ChangePoseDelegate>             ChangePoseHook;

    private readonly DisableAutoUmbrellaDelegate        DisableAutoUmbrellaFn;
    private readonly AutoUmbrellaSetDelegate            AutoUmbrellaSetFn;
    private readonly ExecuteCommandDelegate             ExecuteCommand;
    private readonly AnimateIntoPoseDelegate            AnimateIntoPoseFn;
    private readonly CurrentUmbrellaCposeDelegate       CurrentUmbrellaCposeFn;
    private readonly GetAvailablePosesDelegate          GetAvailablePosesFn;
    #endregion

    #region Helpers
    private unsafe uint CurrentOrnamentId => ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)Service.ClientState.LocalPlayer.Address)->Ornament.OrnamentId;
    private uint CurrentAutoUmbrella => (uint)Marshal.ReadInt32(Service.ClientState.LocalPlayer.Address + (nint)OrnamentManagerOffset + CurrentAutoUmbrellaOffset);

    private uint AvailablePosesCount => GetAvailablePosesFn(PoseType.Umbrella);
    private static bool UnkCondition(Ornament ornament) => ornament.Unknown1 == 1 && ornament.Unknown2 == 1 && ornament.Unknown3 == 1 && ornament.Unknown4 == 2; //unk1=1, unk2=1, unk3=1, unk4=2 seems to be a pattern common to all umbrellas
    private void PrintSetAutoUmbrella(int gearsetIndex, Ornament Umbrella) => Service.Chat.Print($"[{Name}] {Umbrella.Singular} has been set as the auto-umbrella for gearset \"{GearsetName(gearsetIndex)}\".");
    private void PrintDisabledAutoUmbrella(int gearsetIndex) => Service.Chat.Print($"[{Name}] Auto-umbrella disabled for gearset \"{GearsetName(gearsetIndex)}\".");
    private void PrintNotice(string Message) => Service.Chat.Print($"[{Name}] {Message}");
    private void PrintError(string Message) => Service.Chat.PrintError($"[{Name}] {Message}");
    private unsafe int CurrentGearsetIndex => RaptureGearsetModule.Instance()->CurrentGearsetIndex;
    private unsafe string GearsetNameAlt(int gearsetIndex) => string.Join("", SeString.Parse(RaptureGearsetModule.Instance()->GetGearset(gearsetIndex)->Name,47).TextValue.Where(c=>(byte)c!=0));
    private unsafe string GearsetName(int gearsetIndex) => SplitOnByte(Encoding.UTF8.GetString(RaptureGearsetModule.Instance()->GetGearset(gearsetIndex)->Name, 47), 0);
    private static string SplitOnByte(string str, byte split)
    {
        int splitIndex = str.IndexOf((char)split);
        if (splitIndex == -1)
            return str;
        string firstHalf = str.Substring(0, splitIndex);
        string secondHalf = str.Substring(splitIndex + 1);
        Service.PluginLog.Verbose($"first half: [{string.Join(' ', firstHalf.Select(c => (byte)c))}] {firstHalf} second half: [{string.Join(' ', secondHalf.Select(c => (byte)c))}] {secondHalf}");
        return firstHalf;
    }
    private unsafe bool IsRaining => EnvManager.Instance()->ActiveWeather == 7 || EnvManager.Instance()->ActiveWeather == 8;
    private unsafe bool IsAutoUmbrellaEquipped => IsUmbrellaEquipped && (CurrentOrnamentId == CurrentAutoUmbrella || CurrentOrnamentId == config.GearsetIndexToParasol[CurrentGearsetIndex]);
    private unsafe bool IsUmbrellaEquipped => CurrentOrnamentId!=0 && ornamentSheet.Where(row => UnkCondition(row) && row.RowId == CurrentOrnamentId).Any();
    private static byte CurrentUmbrellaCpose => Marshal.ReadByte(Service.SigScanner.GetStaticAddressFromSig(Signatures.PosesArraySig) + 0x5);
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
        Task.Run(() => 
        {
            Thread.Sleep(1);
            AgentModule.Instance()->GetAgentByInternalID(agentInternalID)->Show();
        });
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
            ClientLanguage.English => RegexPatterns.EnAutoUmbrellaLogMessage(),
            ClientLanguage.French => RegexPatterns.FrAutoUmbrellaLogMessage(),
            ClientLanguage.Japanese => RegexPatterns.JpAutoUmbrellaLogMessage(),
            ClientLanguage.German => RegexPatterns.DeAutoUmbrellaLogMessage(),
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
        // as described in FFXIVClientStructs' PoseType ^
        var umbrellaCposeIntPtr = Service.SigScanner.GetStaticAddressFromSig(Signatures.PosesArraySig) + 0x5;
        if (newPose == CurrentUmbrellaCpose || newPose < 0 || newPose > AvailablePosesCount)
            return;
        Marshal.WriteByte(umbrellaCposeIntPtr, newPose);
        if (!IsUmbrellaEquipped)
            return;
        AnimateIntoPoseFn(Service.ClientState.LocalPlayer.Address + (nint)SomeManagerOffset, 5, newPose, 0);
        ExecuteCommand(505, 5, newPose, 0, 0);
        ExecuteCommand(506, 5, newPose, 0, 0);
    }
    private unsafe void AutoUmbrellaSet(Ornament Umbrella)
    {
        if (CurrentAutoUmbrella == Umbrella.RowId || Umbrella.RowId == 0) return;

        var ManagerPtr = Service.ClientState.LocalPlayer.Address + (nint)OrnamentManagerOffset;
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

        var ManagerPtr = Service.ClientState.LocalPlayer.Address + (nint)OrnamentManagerOffset;
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
        Service.PluginLog.Verbose($"OnGearsetSwitch({CurrentGearsetIndex}, {destGearsetIndex});");
        OnGearsetSwitch(CurrentGearsetIndex, destGearsetIndex);
        return EquipGearsetHook.Original(a1, destGearsetIndex, a3);
    }

    private nint ChangePoseDetour(nint unk1, nint unk2)
    {
        Service.PluginLog.Debug($"{Service.SigScanner.GetStaticAddressFromSig(Signatures.PosesArraySig):X}");
        Service.PluginLog.Debug($"isUmbrellaEquipped: {IsUmbrellaEquipped}, CurrentOrnamentId: {CurrentOrnamentId}");
        if (!IsUmbrellaEquipped) return ChangePoseHook.Original(unk1, unk2);

        var nextCpose = (byte)((CurrentUmbrellaCpose + 1) % AvailablePosesCount+1);
        config.GearsetIndexToCpose[CurrentGearsetIndex] = nextCpose;
        config.Save();
        
        return ChangePoseHook.Original(unk1, unk2);
    }
    #endregion

    public Plugin(DalamudPluginInterface PluginInterface)
    {
        pluginInterface = PluginInterface;
        pluginInterface.Create<Service>();
        Service.GameInteropProvider.InitializeFromAttributes(this);
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
            Service.PluginLog.Error("Ornament/ClassJob sheet is null");
            PrintError("Ornament/ClassJob sheet is null");
            return;
        }

        var DisableAutoUmbrellaPtr  = Service.SigScanner.ScanText(Signatures.DisableAutoUmbrellaSig);
        var AutoUmbrellaSetPtr      = Service.SigScanner.ScanText(Signatures.AutoUmbrellaSetSig);

        DisableAutoUmbrellaFn       = Marshal.GetDelegateForFunctionPointer<DisableAutoUmbrellaDelegate>(DisableAutoUmbrellaPtr);
        AutoUmbrellaSetFn           = Marshal.GetDelegateForFunctionPointer<AutoUmbrellaSetDelegate>(AutoUmbrellaSetPtr);
        ExecuteCommand              = Marshal.GetDelegateForFunctionPointer<ExecuteCommandDelegate>(Service.SigScanner.ScanText(Signatures.ExecuteCommandSig));
        AnimateIntoPoseFn           = Marshal.GetDelegateForFunctionPointer<AnimateIntoPoseDelegate>(Service.SigScanner.ScanText(Signatures.AnimateIntoPoseSig));
        CurrentUmbrellaCposeFn      = Marshal.GetDelegateForFunctionPointer<CurrentUmbrellaCposeDelegate>(Service.SigScanner.ScanText(Signatures.CurrentParasolCposeSig));
        GetAvailablePosesFn         = Marshal.GetDelegateForFunctionPointer<GetAvailablePosesDelegate>(Service.SigScanner.ScanText(Signatures.GetAvailablePosesSig));

        DisableAutoUmbrellaHook     = Service.GameInteropProvider.HookFromAddress<DisableAutoUmbrellaDelegate>(DisableAutoUmbrellaPtr, DisableAutoUmbrellaDetour);
        AutoUmbrellaSetHook         = Service.GameInteropProvider.HookFromAddress<AutoUmbrellaSetDelegate>(AutoUmbrellaSetPtr, AutoUmbrellaSetDetour);
        EquipGearsetHook            = Service.GameInteropProvider.HookFromAddress<EquipGearsetDelegate>(Service.SigScanner.ScanText(Signatures.EquipGearsetSig), EquipGearsetDetour);
        ChangePoseHook              = Service.GameInteropProvider.HookFromAddress<ChangePoseDelegate>(Service.SigScanner.ScanText(Signatures.ChangePoseSig), ChangePoseDetour);

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
    // note: cant write cpose for umbrella if umbrella isn't out? -- seems irrelevant now, should work, leaving this here as a memo
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

        var idMatch = RegexPatterns.IdRegex().Match(args);
        if (!idMatch.Success)
        {
            return;
        }

        var cposeId = byte.Parse(idMatch.Value);
        args = args.Replace(idMatch.Value, "").Trim();
        if (cposeId < 0 || cposeId > AvailablePosesCount)
        {
            PrintError($"Invalid pose specified ({cposeId}) the number must be between 0 and {AvailablePosesCount}.");
            return;
        }
        if (args.Length == 0)
        {
            CmdSetUmbrellaCpose(new KeyValuePair<int, string>(CurrentGearsetIndex, GearsetName(CurrentGearsetIndex)), cposeId);
            return;
        }
        var gearsetMatches = Gearsets.Where(gearset => gearset.Value.Contains(args, System.StringComparison.Ordinal));
        if (!gearsetMatches.Any())
        {
            PrintError($"Could not find a gearset (partially) matching the name \"{args}\"");
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

        var ornamentNameMatch = RegexPatterns.UmbrellaNameMatch().Match(args);
        var idMatch = RegexPatterns.IdRegex().Match(args);

        // if neither a parasol name or an id are found print the usage syntax
        if (!ornamentNameMatch.Success && !idMatch.Success)
        {
            Service.Chat.PrintError(AuUsageMessage);
            return;
        }

#nullable enable
        Ornament? ornament = ornamentNameMatch.Success
            ? ornamentSheet.Where(ornament => ornament.Singular.ToString().Contains(ornamentNameMatch.Groups[1].Value, System.StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
            : ornamentSheet.GetRow(uint.Parse(idMatch.Value));

        if (ornament == null)
        {
            if (!config.Silent)
                PrintError($"Could not find a valid ornament for name/id {(ornamentNameMatch.Success ? ornamentNameMatch.Value : idMatch.Value)}.");
            return;
        }
#nullable disable

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
        Service.PluginLog.Debug($"Switching Cpose from {config.GearsetIndexToCpose[lastGearsetIndex]} to {config.GearsetIndexToCpose[destGearsetIndex]}");
        ChangeUmbrellaCpose(config.GearsetIndexToCpose[destGearsetIndex]);

        Service.PluginLog.Verbose($"Switched Gearset from \"{GearsetName(lastGearsetIndex)}\" (#{lastGearsetIndex}) to \"{GearsetName(destGearsetIndex)}\" (#{destGearsetIndex})");
        // if (for some reason) an entry for the given jobs switched from/to hasn't been made yet, create one
        if (!config.GearsetIndexToParasol.ContainsKey(lastGearsetIndex))
            config.GearsetIndexToParasol[lastGearsetIndex] = CurrentAutoUmbrella;
        if (!config.GearsetIndexToParasol.ContainsKey(destGearsetIndex))
            config.GearsetIndexToParasol[destGearsetIndex] = CurrentAutoUmbrella;

        var lastParasol = ornamentSheet.GetRow(config.GearsetIndexToParasol[lastGearsetIndex]);
        var destParasol = ornamentSheet.GetRow(config.GearsetIndexToParasol[destGearsetIndex]);

        Service.PluginLog.Verbose($"Switched Umbrella from {lastParasol.Singular} (#{lastParasol.RowId}) to {destParasol.Singular} (#{destParasol.RowId})");

        //                                                     in case there is a mismatch
        if (lastParasol.RowId == destParasol.RowId || CurrentAutoUmbrella == destParasol.RowId)
            return;

        if (destParasol.RowId == 0)
        {
            Service.PluginLog.Verbose($"Calling DisableAutoUmbrella()");
            DisableAutoUmbrella();
            return;
        }

        Service.PluginLog.Verbose($"Calling AutoUmbrellaSet({destGearsetIndex}, {destParasol.RowId})");
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
        if (config.GearsetIndexToCpose[Gearset.Key] == CposeId)
        {
            if (!config.Silent)
                PrintError($"Gearset \"{GearsetName(Gearset.Key)}\" is already set to use pose #{CposeId}.");
            return;
        }
        if (Gearset.Key == CurrentGearsetIndex) 
            ChangeUmbrellaCpose(CposeId);
        
        config.GearsetIndexToCpose[Gearset.Key] = CposeId;

        if (!config.Silent)
            PrintNotice($"Gearset \"{Gearset.Value}\" is now set to use pose #{CposeId}.");
        config.Save();
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
                ActionManager.Instance()->UseAction(ActionType.Ornament, CurrentAutoUmbrella);
                break;
            case "list":
                Service.Chat.Print($"Below are the gearsets you've registered parasols for... (count: {config.GearsetIndexToParasol.Count})");
                foreach (var entry in config.GearsetIndexToParasol)
                {
                    var Message = new List<Payload>()
                        {
                            new UIForegroundPayload(JobToColor(RaptureGearsetModule.Instance()->GetGearset(entry.Key)->ClassJob)),
                            new TextPayload($"{GearsetName(entry.Key)}: "),
                            new UIForegroundPayload(0),
                            new TextPayload(entry.Value == 0 ? $"None" : $"{ornamentSheet.GetRow(entry.Value).Singular}")
                        };
                    if (config.GearsetIndexToCpose.TryGetValue(entry.Key, out var cposeEntry))
                        Message.Add(new TextPayload($" (#{cposeEntry})"));
                    Service.Chat.Print(new XivChatEntry() { Message = new SeString(Message) });
                };
                break;
        }
        config.Save();
    }
    #endregion
}
