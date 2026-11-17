#if DEBUG
// In-game measurements run by Gravitas.TestHarness:
//   python GravitasTestHarness/Gravitas.TestProbe/probe.py run --project <this repo> --tests '{"tags":["tsmbench"]}' --summary
// Results are logged as "[TSMBENCH] ..." lines in the run's Player.log. Needs HarnessDllPath in Local.Build.props.
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Gravitas.TestHarness;
using UnityEngine;

namespace TrueSmartMopping;

internal static class HarnessTests
{
    private const int InnerHeight = 5;
    private static int ax, ay, aw; // arena interior: lower-left cell and width
    private static string gasKind = "air"; // air / thin / vacuum: what fills the room around the liquid

    private static void Log(string s) => UnityEngine.Debug.Log("[TSMBENCH] " + s);

    // An empty (vacuum), visible, building-free rectangle near the start to build a sealed test room in.
    private static bool FindFreeRect(int w, int h, out int fx, out int fy)
    {
        int home = Arrange.HomeCell();
        var (hx, hy) = Arrange.XY(home);
        for (int dy = -45; dy <= 45; dy++)
        {
            for (int dx = -75; dx <= 75; dx++)
            {
                int x0 = hx + dx, y0 = hy + dy;
                if (x0 < 3 || y0 < 3 || x0 + w > Grid.WidthInCells - 3 || y0 + h > Grid.HeightInCells - 3) continue;
                bool ok = true;
                for (int y = 0; y < h && ok; y++)
                    for (int x = 0; x < w && ok; x++)
                    {
                        int c = Grid.XYToCell(x0 + x, y0 + y);
                        ok = Grid.IsVisible(c) && !Grid.Solid[c] && Grid.Element[c].id == SimHashes.Vacuum
                             && Grid.Objects[c, (int)ObjectLayer.Building] == null && Grid.Objects[c, (int)ObjectLayer.Pickupables] == null;
                    }
                if (ok) { fx = x0; fy = y0; return true; }
            }
        }
        fx = fy = -1;
        return false;
    }

    // Builds a sealed granite room (the interior is width x InnerHeight) with air inside, no liquid.
    // The test duplicants must not go to bed or take breaks in the middle of a measurement: make every schedule all work.
    private static void AlwaysWork()
    {
        foreach (var sch in ScheduleManager.Instance.GetSchedules())
            for (int i = 0; i < sch.GetBlocks().Count; i++)
                sch.SetBlockGroup(i, Db.Get().ScheduleGroups.Worktime);
    }

    private static IEnumerator BuildArena(int width)
    {
        AlwaysWork();
        yield return Wait.Ticks(2);
        if (!FindFreeRect(width + 2, InnerHeight + 2, out int fx, out int fy))
            throw new System.InvalidOperationException("no free " + (width + 2) + "x" + (InnerHeight + 2) + " area found near the start");
        ax = fx + 1; ay = fy + 1; aw = width;
        for (int y = 0; y < InnerHeight + 2; y++)
            for (int x = 0; x < width + 2; x++)
            {
                bool inner = x >= 1 && x <= width && y >= 1 && y <= InnerHeight;
                if (inner) SetGas(Arrange.Cell(fx + x, fy + y));
                else Arrange.SetElement(Arrange.Cell(fx + x, fy + y), SimHashes.Granite, 1000f, 293.15f);
            }
        yield return Wait.Ticks(3);
    }

    private static void SetGas(int cell)
    {
        if (gasKind == "vacuum") Arrange.SetElement(cell, SimHashes.Vacuum, 0f, 293.15f);
        else Arrange.SetElement(cell, SimHashes.Oxygen, gasKind == "thin" ? 0.15f : 1.5f, 293.15f);
    }

    // Spawned duplicants are random: the Athletics attribute changes the walking speed a lot. Fix it to 0 for every test run.
    private static void NormalizeDupe(GameObject d)
    {
        var ath = Db.Get().Attributes.Athletics.Lookup(d);
        if (ath == null) return;
        float before = ath.GetTotalValue();
        ath.Add(new Klei.AI.AttributeModifier(Db.Get().Attributes.Athletics.Id, -before, "TSM bench normalisation"));
        Log("dupe athletics " + before.ToString("F1") + " -> " + ath.GetTotalValue().ToString("F1"));
    }

    // Duplicants must survive a vacuum for minutes: keep their breath full (test only).
    private static void KeepBreathing(IEnumerable<GameObject> dupes)
    {
        foreach (var d in dupes)
        {
            if (d == null) continue;
            var fx = d.GetComponent<Klei.AI.Effects>();
            if (fx != null)
            {
                // being chilly or wet slows them down; the tests measure the algorithm, not the dupe
                foreach (var id in new[] { "ColdAir", "WetFeet", "SoakingWet", "WarmTouch" })
                    if (id != "WarmTouch" && fx.HasEffect(id)) fx.Remove(id);
            }
            var temp = Db.Get().Amounts.Temperature.Lookup(d);
            if (temp != null) temp.SetValue(310.15f);
            var amt = Db.Get().Amounts.Breath.Lookup(d);
            if (amt != null) amt.SetValue(amt.GetMax());
        }
    }

    private static void ClearInterior()
    {
        for (int y = 0; y < InnerHeight; y++)
            for (int x = 0; x < aw; x++)
                SetGas(Arrange.Cell(ax + x, ay + y));
    }

    private static float LiquidMass()
    {
        float m = 0;
        for (int y = 0; y < InnerHeight; y++)
            for (int x = 0; x < aw; x++)
            {
                int c = Arrange.Cell(ax + x, ay + y);
                if (Grid.Element[c].IsLiquid) m += Grid.Mass[c];
            }
        return m;
    }

    // Liquid in the room: total by element and the individual cells (x,y relative to the room, mass).
    private static string Dump()
    {
        var byElem = new Dictionary<string, float>();
        var cells = new List<string>();
        for (int y = 0; y < InnerHeight; y++)
            for (int x = 0; x < aw; x++)
            {
                int c = Arrange.Cell(ax + x, ay + y);
                if (!Grid.Element[c].IsLiquid) continue;
                string n = Grid.Element[c].id.ToString();
                byElem[n] = (byElem.TryGetValue(n, out var m) ? m : 0) + Grid.Mass[c];
                if (cells.Count < 30) cells.Add(x + "," + y + ":" + Grid.Mass[c].ToString("F1"));
            }
        float chunks = 0; int chunkCount = 0;
        foreach (var pk in Components.Pickupables.Items)
        {
            if (pk == null) continue;
            int pc = Grid.PosToCell(pk);
            var (px, py) = Arrange.XY(pc);
            if (px < ax - 1 || px > ax + aw || py < ay - 1 || py > ay + InnerHeight) continue;
            var el = pk.GetComponent<PrimaryElement>();
            if (el != null && el.Element.IsLiquid) { chunks += pk.TotalAmount; chunkCount++; }
        }
        return string.Join(" ", byElem.Select(kv => kv.Key + "=" + kv.Value.ToString("F0"))) + " | liquid chunks=" + chunkCount + "/" + chunks.ToString("F0")
               + " | cells " + string.Join(" ", cells);
    }

    private static void Puddle(int x, int width, int rows, float mass)
    {
        for (int r = 0; r < rows; r++)
            for (int i = 0; i < width; i++)
                Arrange.SetElement(Arrange.Cell(ax + x + i, ay + r), SimHashes.Water, mass, 293.15f);
    }

    // Vanilla placers on every liquid cell that MopTool.OnDragTool would accept right now.
    private static int PlaceVanillaMops()
    {
        int n = 0;
        var prefab = Assets.GetPrefab(new Tag("MopPlacer"));
        for (int y = 0; y < InnerHeight; y++)
            for (int x = 0; x < aw; x++)
            {
                int c = Arrange.Cell(ax + x, ay + y);
                if (Grid.Solid[c] || !Grid.Element[c].IsLiquid || Grid.Objects[c, 8] != null) continue;
                int below = Grid.CellBelow(c);
                if (!Grid.IsValidCell(below) || !Grid.Solid[below] || Grid.Mass[c] > MopTool.maxMopAmt) continue;
                var go = Grid.Objects[c, 8] = Util.KInstantiate(prefab);
                var pos = Grid.CellToPosCBC(c, Grid.SceneLayer.FXFront);
                pos.z -= 0.15f;
                go.transform.SetPosition(pos);
                go.SetActive(true);
                var pr = go.GetComponent<Prioritizable>();
                if (pr != null) pr.SetMasterPriority(new PrioritySetting(PriorityScreen.PriorityClass.basic, 5));
                n++;
            }
        return n;
    }

    // mode: "smart" = one smart-mop order; "vanilla1" = vanilla placers dragged once over the puddle; "vanilla2" = the player
    // drags again every 5 s (new films get new placers). Duplicants start at the left end of the room.
    private static IEnumerator FlatScenario(string name, string mode, int puddleWidth, int dupeCount, float massPerCell, string gas = "air")
    {
        gasKind = gas;
        ModSettings.AutoMopEnabled = false;
        yield return BuildArena(puddleWidth + 8);
        int px = 4;
        Puddle(px, puddleWidth, 1, massPerCell);
        yield return Wait.Ticks(2);
        float before = LiquidMass();
        var dupes = new List<GameObject>();
        for (int i = 0; i < dupeCount; i++)
        {
            var nd = Arrange.SpawnDuplicant(Arrange.Cell(ax + i, ay));
            dupes.Add(nd);
            yield return null;
            NormalizeDupe(nd);
        }
        SmartMopChore chore = null;
        if (mode == "smart")
        {
            bool ok = SmartMopTool.CreateOrder(Arrange.Cell(ax + px + puddleWidth / 2, ay), new PrioritySetting(PriorityScreen.PriorityClass.basic, 5));
            Check.True(ok, "order was not placed");
            chore = SmartMopChore.Live.Last();
        }
        else
        {
            PlaceVanillaMops();
        }
        double t0 = GameClock.Instance.GetTime();
        var marks = new SortedDictionary<string, double>();
        var sw = Stopwatch.StartNew();
        float cur = before;
        int s = 0;
        for (; s < 300; s++)
        {
            for (int sub = 0; sub < 5; sub++)
            {
                yield return Wait.Seconds(0.2f);
                // Movement trace: where every duplicant is, five times a second (see tools in the analysis notes).
                var sb = new System.Text.StringBuilder();
                sb.Append("[TSMTRACE] ").Append(name).Append(' ').Append(gas).Append(' ').Append(mode)
                  .Append(" t=").Append((GameClock.Instance.GetTime() - t0).ToString("F1"));
                foreach (var d in dupes)
                {
                    var (dx, dy) = Arrange.XY(Grid.PosToCell(d));
                    var dr = d.GetComponent<ChoreDriver>();
                    var ch = dr != null ? dr.GetCurrentChore() : null;
                    sb.Append(" | ").Append(dx - ax).Append(',').Append(dy - ay).Append(',').Append(ch != null ? ch.choreType.Id : "none");
                }
                sb.Append(" | liquid=").Append(LiquidMass().ToString("F2"));
                UnityEngine.Debug.Log(sb.ToString());
            }
            {
                // Liquid profile once a second: mass per column of the room (all rows summed), and the placer columns.
                var pb = new System.Text.StringBuilder();
                pb.Append("[TSMPROF] ").Append(name).Append(' ').Append(gas).Append(' ').Append(mode)
                  .Append(" t=").Append((GameClock.Instance.GetTime() - t0).ToString("F1")).Append(" m=");
                var pl = new System.Text.StringBuilder();
                for (int xx = 0; xx < aw; xx++)
                {
                    float colMass = 0f;
                    bool placer = false;
                    for (int yy = 0; yy < InnerHeight; yy++)
                    {
                        int cc = Arrange.Cell(ax + xx, ay + yy);
                        if (Grid.Element[cc].IsLiquid) colMass += Grid.Mass[cc];
                        if (Grid.Objects[cc, 8] != null) placer = true;
                    }
                    pb.Append(colMass.ToString("F2")).Append(xx < aw - 1 ? "," : "");
                    if (placer) pl.Append(xx).Append(',');
                }
                pb.Append(" placers=").Append(pl);
                UnityEngine.Debug.Log(pb.ToString());
            }
            KeepBreathing(dupes);
            if (s % 10 == 9)
            {
                int placers = 0, wet = 0;
                for (int yy = 0; yy < InnerHeight; yy++)
                    for (int xx = 0; xx < aw; xx++)
                    {
                        int cc = Arrange.Cell(ax + xx, ay + yy);
                        if (Grid.Objects[cc, 8] != null) placers++;
                        if (Grid.Element[cc].IsLiquid) wet++;
                    }
                var drv = dupes[0].GetComponent<ChoreDriver>();
                var cur0 = drv != null ? drv.GetCurrentChore() : null;
                var (wx, wy) = Arrange.XY(Grid.PosToCell(dupes[0]));
                UnityEngine.Debug.Log("[TSMDIAG] " + name + " " + gas + " " + mode + " t=" + (s + 1) + " chore=" + (cur0 != null ? cur0.choreType.Id : "none") + " placers=" + placers + " wetCells=" + wet + " liquid=" + LiquidMass().ToString("F1") + " dupeX=" + (wx - ax));
            }
            cur = LiquidMass();
            double t = GameClock.Instance.GetTime() - t0;
            float removed = before - cur;
            foreach (var f in new[] { 0.5f, 0.9f, 0.99f })
                if (removed >= f * before && !marks.ContainsKey("t" + (int)(f * 100))) marks["t" + (int)(f * 100)] = t;
            if (cur <= 0.05f && !marks.ContainsKey("t_zero")) marks["t_zero"] = t;
            if (mode == "vanilla2" && s % 5 == 4) PlaceVanillaMops();
            if (cur <= 0.05f && (chore == null || chore.done)) break;
        }
        yield return Wait.Seconds(2f);
        cur = LiquidMass();
        string m = string.Join(" ", marks.Select(kv => kv.Key + "=" + kv.Value.ToString("F0")));
        string extra = chore != null
            ? " done=" + chore.done + " moves=" + chore.smi.moves + " moveTime=" + chore.smi.moveTime.ToString("F1") + " mopTime=" + chore.smi.mopTime.ToString("F1")
            : "";
        Log(name + " gas=" + gas + " mode=" + mode + " width=" + puddleWidth + " dupes=" + dupeCount + " " + m + " residue_kg=" + cur.ToString("F2") + "/" + before.ToString("F0") + extra
            + " sim_s=" + s + " real_s=" + sw.Elapsed.TotalSeconds.ToString("F1"));
        foreach (var d in dupes) Arrange.Destroy(d);
        ModSettings.AutoMopEnabled = true;
        gasKind = "air";
        yield return null;
        if (mode == "smart") Check.True(cur <= 0.1f, "liquid was left: " + cur.ToString("F2") + " kg");
    }

    [HarnessTest(Name = "tsmbench.flat_9", Tags = "tsmbench manual", TimeoutSeconds = 900)]
    public static IEnumerator Flat9() => FlatScenario("flat_9", "smart", 9, 1, 40f);

    [HarnessTest(Name = "tsmbench.flat_25", Tags = "tsmbench manual", TimeoutSeconds = 900)]
    public static IEnumerator Flat25() => FlatScenario("flat_25", "smart", 25, 1, 40f);

    [HarnessTest(Name = "tsmbench.flat_25_thick", Tags = "tsmbench manual", TimeoutSeconds = 900)]
    public static IEnumerator Flat25Thick() => FlatScenario("flat_25_thick", "smart", 25, 1, 140f);

    [HarnessTest(Name = "tsmbench.flat_25_two_dupes", Tags = "tsmbench manual", TimeoutSeconds = 900)]
    public static IEnumerator Flat25Two() => FlatScenario("flat_25_two_dupes", "smart", 25, 2, 40f);

    [HarnessTest(Name = "tsmvanilla.flat_9_once", Tags = "tsmvanilla manual", TimeoutSeconds = 900)]
    public static IEnumerator Van9Once() => FlatScenario("flat_9", "vanilla1", 9, 1, 40f);

    [HarnessTest(Name = "tsmvanilla.flat_9_redrag", Tags = "tsmvanilla manual", TimeoutSeconds = 900)]
    public static IEnumerator Van9Re() => FlatScenario("flat_9", "vanilla2", 9, 1, 40f);

    [HarnessTest(Name = "tsmvanilla.flat_25_once", Tags = "tsmvanilla manual", TimeoutSeconds = 900)]
    public static IEnumerator Van25Once() => FlatScenario("flat_25", "vanilla1", 25, 1, 40f);

    [HarnessTest(Name = "tsmvanilla.flat_25_redrag", Tags = "tsmvanilla manual", TimeoutSeconds = 900)]
    public static IEnumerator Van25Re() => FlatScenario("flat_25", "vanilla2", 25, 1, 40f);

    [HarnessTest(Name = "tsmvanilla.flat_25_thick_redrag", Tags = "tsmvanilla manual", TimeoutSeconds = 900)]
    public static IEnumerator Van25ThickRe() => FlatScenario("flat_25_thick", "vanilla2", 25, 1, 140f);

    [HarnessTest(Name = "tsmvanilla.flat_25_two_dupes_redrag", Tags = "tsmvanilla manual", TimeoutSeconds = 900)]
    public static IEnumerator Van25TwoRe() => FlatScenario("flat_25_two_dupes", "vanilla2", 25, 2, 40f);

    [HarnessTest(Name = "tsmgas.thin_9_smart", Tags = "tsmgas manual", TimeoutSeconds = 900)]
    public static IEnumerator Gas_thin_9_smart() => FlatScenario("flat_9", "smart", 9, 1, 40f, "thin");

    [HarnessTest(Name = "tsmgas.thin_9_vanilla2", Tags = "tsmgas manual", TimeoutSeconds = 900)]
    public static IEnumerator Gas_thin_9_vanilla2() => FlatScenario("flat_9", "vanilla2", 9, 1, 40f, "thin");

    [HarnessTest(Name = "tsmgas.thin_25_smart", Tags = "tsmgas manual", TimeoutSeconds = 900)]
    public static IEnumerator Gas_thin_25_smart() => FlatScenario("flat_25", "smart", 25, 1, 40f, "thin");

    [HarnessTest(Name = "tsmgas.thin_25_vanilla2", Tags = "tsmgas manual", TimeoutSeconds = 900)]
    public static IEnumerator Gas_thin_25_vanilla2() => FlatScenario("flat_25", "vanilla2", 25, 1, 40f, "thin");

    [HarnessTest(Name = "tsmgas.vacuum_9_smart", Tags = "tsmgas manual", TimeoutSeconds = 900)]
    public static IEnumerator Gas_vacuum_9_smart() => FlatScenario("flat_9", "smart", 9, 1, 40f, "vacuum");

    [HarnessTest(Name = "tsmgas.vacuum_9_vanilla2", Tags = "tsmgas manual", TimeoutSeconds = 900)]
    public static IEnumerator Gas_vacuum_9_vanilla2() => FlatScenario("flat_9", "vanilla2", 9, 1, 40f, "vacuum");

    [HarnessTest(Name = "tsmgas.vacuum_25_smart", Tags = "tsmgas manual", TimeoutSeconds = 900)]
    public static IEnumerator Gas_vacuum_25_smart() => FlatScenario("flat_25", "smart", 25, 1, 40f, "vacuum");

    [HarnessTest(Name = "tsmgas.vacuum_25_vanilla2", Tags = "tsmgas manual", TimeoutSeconds = 900)]
    public static IEnumerator Gas_vacuum_25_vanilla2() => FlatScenario("flat_25", "vanilla2", 25, 1, 40f, "vacuum");

    // CPU cost of the stand search itself (cold = rebuilds the memory, warm = reuses it) on a 3-row puddle.
    [HarnessTest(Name = "tsmbench.search_cost", Tags = "tsmbench manual", TimeoutSeconds = 300)]
    public static IEnumerator SearchCost()
    {
        ModSettings.AutoMopEnabled = false;
        yield return BuildArena(40);
        Puddle(2, 36, 3, 40f);
        yield return Wait.Ticks(2);
        int c = Arrange.Cell(ax + 20, ay);
        const int n = 300;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++)
            SmartMopTargets.FindPreferredStand(c, c, SimHashes.Water, MopTool.maxMopAmt, _ => false, _ => false, c, new HashSet<int>());
        double cold = sw.Elapsed.TotalMilliseconds / n;
        var mem = new HashSet<int>();
        SmartMopTargets.FindPreferredStand(c, c, SimHashes.Water, MopTool.maxMopAmt, _ => false, _ => false, c, mem);
        sw.Restart();
        for (int i = 0; i < n; i++)
            SmartMopTargets.FindPreferredStand(c, c, SimHashes.Water, MopTool.maxMopAmt, _ => false, _ => false, c, mem);
        double warm = sw.Elapsed.TotalMilliseconds / n;
        sw.Restart();
        for (int i = 0; i < n; i++) SmartMopTargets.IsExhausted(mem, SimHashes.Water, MopTool.maxMopAmt);
        double exh = sw.Elapsed.TotalMilliseconds / n;
        Log("search_cost memory=" + mem.Count + " cold_ms=" + cold.ToString("F3") + " warm_ms=" + warm.ToString("F3") + " exhausted_ms=" + exh.ToString("F3"));
        ClearInterior();
        ModSettings.AutoMopEnabled = true;
    }

    // A duplicant standing in a pool too deep to mop (cells over 150 kg): does auto-mop keep building chores?
    [HarnessTest(Name = "tsmbench.deep_pool_auto", Tags = "tsmbench", TimeoutSeconds = 300)]
    public static IEnumerator DeepPoolAuto()
    {
        ModSettings.AutoMopEnabled = true;
        yield return BuildArena(20);
        Puddle(2, 16, 3, 400f);
        yield return Wait.Ticks(2);
        var go = Arrange.SpawnDuplicant(Arrange.Cell(ax + 8, ay + 1));
        yield return null;
        yield return Wait.Seconds(3f);
        int live0 = SmartMopChore.Live.Count;
        long mem0 = System.GC.GetTotalMemory(false);
        int gc0 = System.GC.CollectionCount(0);
        var sw = Stopwatch.StartNew();
        yield return Wait.Seconds(20f);
        Log("deep_pool_auto live_delta=" + (SmartMopChore.Live.Count - live0) + " live=" + SmartMopChore.Live.Count
            + " gen0_collections=" + (System.GC.CollectionCount(0) - gc0) + " heap_delta_kb=" + ((System.GC.GetTotalMemory(false) - mem0) / 1024)
            + " real_s=" + sw.Elapsed.TotalSeconds.ToString("F1"));
        Arrange.Destroy(go);
        yield return null;
        ClearInterior();
    }
}
#endif
