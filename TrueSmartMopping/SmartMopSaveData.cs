using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using HarmonyLib;
using KSerialization;
using UnityEngine;

namespace TrueSmartMopping;

/// <summary>
/// Lives on the SaveGame object so the smart mop orders (manual and automatic) are written to / read from the save file.
/// Each order is stored as it is at save time (stand cell, remembered liquid, priority, owner duplicant for automatic
/// orders) and recreated as-is after loading.
/// </summary>
[SerializationConfig(MemberSerialization.OptIn)]
public sealed class SmartMopSaveData : KMonoBehaviour
{
    [Serialize] private bool saveAutoMopEnabled = true;
    [Serialize] private int saveAutoMopPriority = 5;
    [Serialize] private int[] forbiddenCells = new int[0];
    [Serialize] private int[] excludedLiquids = new int[0];

    [Serialize] private int[] manual = new int[0];
    [Serialize] private int[] startCells = new int[0];
    [Serialize] private int[] targetCells = new int[0];
    [Serialize] private int[] elements = new int[0];
    [Serialize] private int[] classes = new int[0];
    [Serialize] private int[] values = new int[0];
    [Serialize] private int[] owners = new int[0];
    [Serialize] private int[] rememberedCounts = new int[0];
    [Serialize] private int[] memoryCounts = new int[0];
    [Serialize] private int[] rememberedFlat = new int[0];
    [Serialize] private int[] memoryFlat = new int[0];

    private bool restored;
    private float spawnTime;

    // No automatic order is created until 5 ticks (200 ms each) after the orders of a loaded game were restored.
    public const float AutoBlockSeconds = 5 * 0.2f;
    public static float RestoredAt = -1f;

    public static bool AutoMopBlocked
    {
        get { return RestoredAt < 0f || Time.time < RestoredAt + AutoBlockSeconds; }
    }

    protected override void OnSpawn()
    {
        base.OnSpawn();
        spawnTime = Time.unscaledTime;
    }

    [OnSerializing]
    private void OnSerializing()
    {
        saveAutoMopEnabled = ModSettings.AutoMopEnabled;
        saveAutoMopPriority = ModSettings.AutoMopPriority;
        forbiddenCells = ModSettings.ForbiddenCells.OrderBy(c => c).ToArray();
        excludedLiquids = ModSettings.ExcludedLiquids.Select(l => (int)l).OrderBy(v => v).ToArray();

        var man = new List<int>();
        var start = new List<int>();
        var target = new List<int>();
        var elem = new List<int>();
        var cls = new List<int>();
        var val = new List<int>();
        var own = new List<int>();
        var remCount = new List<int>();
        var memCount = new List<int>();
        var remFlat = new List<int>();
        var memFlat = new List<int>();
        foreach (var chore in SmartMopChore.Live)
        {
            if (chore.done || chore.smi == null || chore.smi.cancelled || chore.smi.targetCell == Grid.InvalidCell)
            {
                continue;
            }

            var priority = chore.masterPriority;
            int owner = -1;
            if (chore.manual)
            {
                var prioritizable = chore.gameObject != null ? chore.gameObject.GetComponent<Prioritizable>() : null;
                if (prioritizable != null)
                {
                    priority = prioritizable.GetMasterPriority();
                }
            }
            else
            {
                var id = chore.gameObject != null ? chore.gameObject.GetComponent<KPrefabID>() : null;
                if (id == null)
                {
                    continue;
                }
                owner = id.InstanceID;
            }

            man.Add(chore.manual ? 1 : 0);
            start.Add(chore.startCell);
            target.Add(chore.smi.targetCell);
            elem.Add((int)chore.smi.element);
            cls.Add((int)priority.priority_class);
            val.Add(priority.priority_value);
            own.Add(owner);
            remCount.Add(chore.smi.remembered.Count);
            memCount.Add(chore.smi.StandMemory.Count);
            remFlat.AddRange(chore.smi.remembered);
            memFlat.AddRange(chore.smi.StandMemory);
        }

        manual = man.ToArray();
        startCells = start.ToArray();
        targetCells = target.ToArray();
        elements = elem.ToArray();
        classes = cls.ToArray();
        values = val.ToArray();
        owners = own.ToArray();
        rememberedCounts = remCount.ToArray();
        memoryCounts = memCount.ToArray();
        rememberedFlat = remFlat.ToArray();
        memoryFlat = memFlat.ToArray();
    }

    // Waits until the world and the duplicants exist, then recreates the orders once.
    private void Update()
    {
        if (restored || Time.unscaledTime - spawnTime < 3f || GlobalChoreProvider.Instance == null
            || Game.Instance == null || Grid.Element == null)
        {
            return;
        }

        restored = true;
        RestoredAt = Time.time;
        ModSettings.RestoreFromSave(saveAutoMopEnabled, saveAutoMopPriority, forbiddenCells, excludedLiquids.Select(v => (SimHashes)v));
        SmartMopChore.Live.Clear();
        var monitors = Object.FindObjectsByType<AutoMopMonitor>(FindObjectsSortMode.None);
        int remPos = 0;
        int memPos = 0;
        for (int i = 0; i < manual.Length; i++)
        {
            var saved = new SmartMopChore.Saved
            {
                targetCell = targetCells[i],
                element = (SimHashes)elements[i],
                remembered = rememberedFlat.Skip(remPos).Take(rememberedCounts[i]).ToArray(),
                memory = memoryFlat.Skip(memPos).Take(memoryCounts[i]).ToArray(),
            };
            remPos += rememberedCounts[i];
            memPos += memoryCounts[i];
            var priority = new PrioritySetting((PriorityScreen.PriorityClass)classes[i], values[i]);
            if (manual[i] == 1)
            {
                SmartMopTool.CreateOrder(startCells[i], priority, saved);
                continue;
            }

            int owner = owners[i];
            var monitor = monitors.FirstOrDefault(m => m.GetComponent<KPrefabID>() != null && m.GetComponent<KPrefabID>().InstanceID == owner);
            if (monitor != null)
            {
                monitor.RestoreChore(startCells[i], priority, saved);
            }
        }
    }
}

public static class SmartMopSavePatch
{
    public static void Apply(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(SaveGame), "OnPrefabInit"),
            postfix: new HarmonyMethod(typeof(SmartMopSavePatch), nameof(AddDataPostfix)));
    }

    private static void AddDataPostfix(SaveGame __instance)
    {
        SmartMopChore.Live.Clear();
        SmartMopSaveData.RestoredAt = -1f;
        if (__instance.GetComponent<SmartMopSaveData>() == null)
        {
            __instance.gameObject.AddComponent<SmartMopSaveData>();
        }
    }
}
