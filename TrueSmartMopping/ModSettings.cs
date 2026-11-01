using System.Collections.Generic;

namespace TrueSmartMopping;

public static class ModSettings
{
    public static bool AutoMopEnabled = true;

    public static int AutoMopPriority = 5;

    /// <summary>Cells where auto-mop is suppressed.</summary>
    public static readonly HashSet<int> ForbiddenCells = new HashSet<int>();

    /// <summary>Liquids excluded from auto-mop. Liquids not listed are targeted.</summary>
    public static readonly HashSet<SimHashes> ExcludedLiquids = new HashSet<SimHashes>();

    public static void RestoreFromSave(bool enabled, int priority, IEnumerable<int> forbidden, IEnumerable<SimHashes> excluded)
    {
        AutoMopEnabled = enabled;
        AutoMopPriority = priority;

        ForbiddenCells.Clear();
        foreach (var cell in forbidden)
        {
            ForbiddenCells.Add(cell);
        }

        ExcludedLiquids.Clear();
        foreach (var liquid in excluded)
        {
            ExcludedLiquids.Add(liquid);
        }
    }

    public static bool IsLiquidTargeted(SimHashes element)
    {
        return !ExcludedLiquids.Contains(element);
    }
}
