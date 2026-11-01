using HarmonyLib;

namespace TrueSmartMopping;

[HarmonyPatch(typeof(MinionIdentity), "OnSpawn")]
public static class MinionIdentityPatch
{
    public static void Postfix(MinionIdentity __instance)
    {
        __instance.gameObject.AddOrGet<AutoMopMonitor>();
    }
}
