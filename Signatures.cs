namespace Auto_UmbrellaPlus
{
    internal static class Signatures
    {
        internal const string OrnamentManagerOffsetSig = "0F B7 57 44 48 81 C1 ?? ?? ?? ??";
        internal const string CurrentAutoUmbrellaOffsetSig = "45 33 C9 66 89 5E ??";
        internal const string SomeManagerOffsetSig = "4C 8B F9 48 8D A8 ?? ?? ?? ??";
        internal const string EquipGearsetSig = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F9 41 0F B6 F0 48 8D 0D";
        internal const string ExecuteEmoteSig = "E8 ?? ?? ?? ?? 40 84 ED 74 18";
        internal const string DisableAutoUmbrellaSig = "E8 ?? ?? ?? ?? 48 8B 4F 10 0F B7 5F 44";
        internal const string AutoUmbrellaSetSig = "E8 ?? ?? ?? ?? 84 C0 74 1E 48 8B 4F 10 0F B7 5F 44";
        internal const string AnimateIntoPoseSig = "E8 ?? ?? ?? ?? 40 0F B6 DE 45 33 C9";
        internal const string CurrentParasolCposeSig = "E8 ?? ?? ?? ?? 40 3A F0 75 19";
        internal const string ExecuteCommandSig = "E8 ?? ?? ?? ?? 8D 43 0A";
        internal const string PosesArraySig = "48 8D 05 ?? ?? ?? ?? 0F B6 1C 38";
        internal static string GetAvailablePosesSig = FFXIVClientStructs.FFXIV.Client.Game.Control.EmoteController.Addresses.GetAvailablePoses.String;
    }
}