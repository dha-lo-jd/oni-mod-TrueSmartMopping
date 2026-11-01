using HarmonyLib;
using KMod;
using PeterHan.PLib.Core;
using PeterHan.PLib.Database;

namespace TrueSmartMopping;

public sealed class TrueSmartMoppingMod : UserMod2
{
    public override void OnLoad(Harmony harmony)
    {
        base.OnLoad(harmony);
        AutoMopOverlayPatch.Apply(harmony);
        SmartMopToolPatch.Apply(harmony);
        SmartMopSavePatch.Apply(harmony);
        PUtil.InitLibrary();
        new PLocalization().Register();
    }

    [HarmonyPatch(typeof(Localization), nameof(Localization.Initialize))]
    private static class Localization_Initialize_Patch
    {
        private static void Postfix()
        {
            Localization.RegisterForTranslation(typeof(STRINGS));
            LocString.CreateLocStringKeys(typeof(STRINGS), null);
        }
    }
}
