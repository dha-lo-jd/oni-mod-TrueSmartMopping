using System.Collections.Generic;
using UnityEngine;

namespace TrueSmartMopping
{
    /// <summary>
    /// Dedicated chore that mops a liquid body from its edge (smart mop).
    /// The duplicant mops a 3-wide range (its own cell and both neighbours) and always stands where that range
    /// covers the most liquid: the second cell from the edge unless the liquid is at most 2 cells wide.
    /// Created by AutoMopMonitor when a duplicant touches mop-able liquid.
    /// </summary>
    public class SmartMopChore : Chore<SmartMopChore.StatesInstance>
    {
        public const int MaxStepsPerChore = 60;
        // Vanilla MopPlacerConfig: Moppable.amountMoppedPerTick (mass bottled per 200ms work tick).
        public const float MopAmountPerTick = 20f;
        public const float MopAnimSpeed = 2f;
        // 200ms ticks spent pretending to mop after the liquid is gone.
        public const int IdleTicksBeforeNext = 3;

        public readonly bool manual;
        public readonly int startCell;

        // Smart mop orders that currently exist; this is what gets written to the save file.
        public static readonly System.Collections.Generic.List<SmartMopChore> Live = new System.Collections.Generic.List<SmartMopChore>();

        /// <summary>State of an order as stored in a save file; restoring it skips the stand search.</summary>
        public sealed class Saved
        {
            public int targetCell;
            public SimHashes element;
            public int[] remembered;
            public int[] memory;
        }

        public SmartMopChore(IStateMachineTarget master, ChoreProvider provider, int startCell, PriorityScreen.PriorityClass priorityClass, int priorityValue, bool manual = false, Saved saved = null)
            : base(Db.Get().ChoreTypes.Mop, master, provider, run_until_complete: true, null, null, null,
                priorityClass, priorityValue, is_preemptable: !manual, allow_in_context_menu: false, 0,
                add_to_daily_report: false, ReportManager.ReportType.WorkTime)
        {
            this.manual = manual;
            this.startCell = startCell;
            base.smi = new StatesInstance(this, startCell, saved);
            Live.Add(this);
            AddPrecondition(ChorePreconditions.instance.NotInTube);
            if (manual)
            {
                var prioritizable = master.GetComponent<Prioritizable>();
                if (prioritizable != null)
                {
                    SetPrioritizable(prioritizable);
                }
                AddPrecondition(ChorePreconditions.instance.IsScheduledTime, Db.Get().ScheduleBlockTypes.Work);
                if (base.smi.targetCell != Grid.InvalidCell)
                {
                    AddPrecondition(ChorePreconditions.instance.CanMoveToCell, base.smi.targetCell);
                }
            }
        }

        public override void Begin(Precondition.Context context)
        {
            smi.sm.worker.Set(context.consumerState.gameObject, smi);
            base.Begin(context);
        }

        public override void Cleanup()
        {
            smi.ReleaseClaim();
            Live.Remove(this);
            base.Cleanup();
        }

        // Chore<T> does not implement isComplete itself (only StandardChoreBase does),
        // so we track completion here; otherwise AutoMopMonitor never creates another chore.
        public bool done { get; private set; }

        public override bool isComplete
        {
            get { return done; }
            protected set { done = value; }
        }

        public void MarkDone()
        {
            done = true;
        }

        public class StatesInstance : GameStateMachine<States, StatesInstance, SmartMopChore, object>.GameInstance
        {
            // Standing cells claimed by active chores, used to keep duplicants from standing next to each other.
            public static readonly Dictionary<int, StatesInstance> Claims = new Dictionary<int, StatesInstance>();

            private int _targetCell = Grid.InvalidCell;
            public SimHashes element = SimHashes.Vacuum;
            public int steps;
            public HashSet<int> remembered = new HashSet<int>();
            public float mopTimer;
            public float startTime, totalMopped, moveStart, moveTime, mopStart, mopTime;
            public int moves;
            public int moveFailures;
            public int mopTicks;
            public int idleTicks;

            public int targetCell
            {
                get { return _targetCell; }
                set
                {
                    if (_targetCell != Grid.InvalidCell && Claims.TryGetValue(_targetCell, out var o) && o == this)
                    {
                        Claims.Remove(_targetCell);
                    }
                    _targetCell = value;
                    if (value != Grid.InvalidCell)
                    {
                        Claims[value] = this;
                    }
                    UpdateMarker();
                }
            }

            public bool cancelled;
            private GameObject marker;
            private int markerCell = Grid.InvalidCell;

            /// <summary>Shows the work order (mop placer visual) on the current standing cell; the cancel tool removes it.</summary>
            private void UpdateMarker()
            {
                int cell = _targetCell;
                if (cell == markerCell)
                {
                    return;
                }
                if (master.manual)
                {
                    MoveManualMarker(cell);
                    return;
                }
                RemoveMarker();
                if (cell == Grid.InvalidCell || Grid.Objects[cell, 8] != null)
                {
                    return;
                }
                var prefab = Assets.GetPrefab(new Tag("MopPlacer"));
                if (prefab == null)
                {
                    return;
                }
                var go = Util.KInstantiate(prefab);
                // Strip the vanilla errand so only this chore works the cell; keep the visual and Cancel handling ours.
                Object.DestroyImmediate(go.GetComponent<Moppable>());
                Object.DestroyImmediate(go.GetComponent<Cancellable>());
                Object.DestroyImmediate(go.GetComponent<SaveLoadRoot>());
                go.AddComponent<SmartMopMarker>().owner = this;
                var position = Grid.CellToPosCBC(cell, Grid.SceneLayer.FXFront);
                position.z -= 0.15f;
                go.transform.SetPosition(position);
                Grid.Objects[cell, 8] = go;
                go.SetActive(true);
                marker = go;
                markerCell = cell;
            }

            // Manual orders: the chore master is the marker itself, so it is moved instead of recreated.
            private void MoveManualMarker(int cell)
            {
                var go = master.gameObject;
                if (go == null)
                {
                    return;
                }
                if (Grid.IsValidCell(markerCell) && Grid.Objects[markerCell, 8] == go)
                {
                    Grid.Objects[markerCell, 8] = null;
                }
                markerCell = cell;
                marker = go;
                if (cell == Grid.InvalidCell)
                {
                    go.DeleteObject();
                    return;
                }
                var position = Grid.CellToPosCBC(cell, Grid.SceneLayer.FXFront);
                position.z -= 0.15f;
                go.transform.SetPosition(position);
                if (Grid.Objects[cell, 8] == null)
                {
                    Grid.Objects[cell, 8] = go;
                }
            }

            private void RemoveMarker()
            {
                var go = marker;
                int cell = markerCell;
                marker = null;
                markerCell = Grid.InvalidCell;
                if (go == null)
                {
                    return;
                }
                if (Grid.IsValidCell(cell) && Grid.Objects[cell, 8] == go)
                {
                    Grid.Objects[cell, 8] = null;
                }
                go.DeleteObject();
            }

            /// <summary>Called by the marker when the player cancels the work order.</summary>
            public void OnMarkerCancelled(GameObject go)
            {
                if (master.manual ? go != master.gameObject : marker != go)
                {
                    return;
                }
                if (Grid.IsValidCell(markerCell) && Grid.Objects[markerCell, 8] == go)
                {
                    Grid.Objects[markerCell, 8] = null;
                }
                marker = null;
                markerCell = Grid.InvalidCell;
                cancelled = true;
                if (master.manual)
                {
                    master.MarkDone();
                    master.Cancel("SmartMop marker cancelled");
                    ReleaseClaim();
                    if (go != null)
                    {
                        go.DeleteObject();
                    }
                    return;
                }
                go.DeleteObject();
                GoTo(sm.finished);
            }

            public GameObject WorkerObject
            {
                get { return sm.worker.Get(this); }
            }

            public override void StopSM(string reason)
            {
                if (master.manual)
                {
                    Debug.Log("[TrueSmartMopping] StopSM manual: " + reason + " cell=" + _targetCell);
                }
                if (!master.manual || master.done)
                {
                    ReleaseClaim();
                }
                base.StopSM(reason);
            }

            /// <summary>Hard rule: a standing cell can only belong to one smart mop chore.</summary>
            public bool Blocked(int cell)
            {
                return ClaimedByOther(cell) || HasVanillaMop(cell) || (master.manual && HasOtherObject(cell));
            }

            // Vanilla mop orders (placers with a Moppable) must not be overlapped.
            private static bool HasVanillaMop(int cell)
            {
                if (!Grid.IsValidCell(cell))
                {
                    return false;
                }
                var o = Grid.Objects[cell, 8];
                return o != null && o.GetComponent<Moppable>() != null;
            }

            /// <summary>True when all remembered liquid and its neighbours can no longer be mopped.</summary>
            public bool IsExhausted()
            {
                using var scope = Scope();
                return SmartMopTargets.IsExhausted(standMemory, element, MopTool.maxMopAmt);
            }

            private bool HasOtherObject(int cell)
            {
                if (!Grid.IsValidCell(cell))
                {
                    return false;
                }
                var o = Grid.Objects[cell, 8];
                return o != null && o != master.gameObject && !(relay && IsGroupMarker(o));
            }

            /// <summary>Cell of the assigned worker; for a new automatic order, the starting cell (the duplicant's own).</summary>
            private int WorkerCell()
            {
                var w = WorkerObject;
                if (w != null)
                {
                    return Grid.PosToCell(w.transform.GetPosition());
                }
                return master.manual ? Grid.InvalidCell : autoStartCell;
            }

            private int autoStartCell = Grid.InvalidCell;

            // Whole liquid remembered by the stand search; kept until none of it is mop-able.
            private readonly HashSet<int> standMemory = new HashSet<int>();

            public HashSet<int> StandMemory
            {
                get { return standMemory; }
            }

            public StatesInstance(SmartMopChore master, int startCell, Saved saved = null) : base(master)
            {
                autoStartCell = startCell;
                if (saved != null)
                {
                    element = saved.element;
                    remembered = new HashSet<int>(saved.remembered);
                    standMemory.UnionWith(saved.memory);
                    startTime = Time.time;
                    targetCell = saved.targetCell;
                    return;
                }
                if (Grid.IsValidCell(startCell))
                {
                    element = Grid.Element[startCell].id;
                }
                targetCell = SmartMopTargets.FindStart(startCell, element, MopTool.maxMopAmt, Blocked,                 NearOther, WorkerCell(), standMemory);
                // Remember all liquid connected to the starting cell or its neighbours; the chore ends only when it is gone.
                remembered = SmartMopTargets.CollectConnected(SmartMopTargets.CellAndNeighbours(startCell), element, 4000);
                startTime = Time.time;
                Debug.Log("[TrueSmartMopping] chore start: remembered=" + remembered.Count + " from " + startCell + " to " + targetCell);
            }

            /// <summary>Re-collects the connected liquid around the current standing cell and merges it into the memory.</summary>
            public void RefreshRemembered()
            {
                using var scope = Scope();
                var fresh = SmartMopTargets.CollectConnected(SmartMopTargets.CellAndNeighbours(targetCell), element, 4000);
                remembered.UnionWith(fresh);
            }

            /// <summary>True when another chore stands on, or next to, the given cell.</summary>
            public bool NearOther(int cell)
            {
                return ClaimedByOther(cell) || ClaimedByOther(Grid.CellLeft(cell)) || ClaimedByOther(Grid.CellRight(cell));
            }

            private bool ClaimedByOther(int cell)
            {
                // During the relay an unstarted helper of the group only marks where the next duplicant will go.
                return Grid.IsValidCell(cell) && Claims.TryGetValue(cell, out var o) && o != this
                    && !(relay && o.Leader == Leader && o.WorkerObject == null);
            }

            public void ReleaseClaim()
            {
                targetCell = Grid.InvalidCell;
            }

            public bool HasStepBudget()
            {
                return steps < MaxStepsPerChore;
            }

            public int WindowCount()
            {
                using var scope = Scope();
                return SmartMopTargets.WindowCount(targetCell, element, MopTool.maxMopAmt);
            }

            /// <summary>Mops one tick worth of every mop-able cell within range of the standing cell.</summary>
            public void MopWindow()
            {
                using var scope = Scope();
                mopTicks++;
                MopCell(Grid.CellLeft(targetCell));
                MopCell(targetCell);
                MopCell(Grid.CellRight(targetCell));
            }

            private void MopCell(int cell)
            {
                if (!SmartMopTargets.IsMoppable(cell, element, MopTool.maxMopAmt))
                {
                    return;
                }
                float amount = Mathf.Min(Grid.Mass[cell], MopAmountPerTick);
                Moppable.MopCell(cell, amount, (mass_cb, data) =>                 SpawnChunk(mass_cb));
            }

                            // Mirrors Moppable.OnCellMopped: chunk appears at the standing (placer) cell with a random x offset.
                            private void SpawnChunk(Sim.MassConsumedCallback mass_cb)
                            {
                                if (mass_cb.mass <= 0f)
                                {
                                    return;
                                }
                                totalMopped += mass_cb.mass; // what the simulation actually removed
                                steps = 0; // progress: the step budget only stops an order that keeps walking without removing anything
                                var elem = ElementLoader.elements[mass_cb.elemIdx];
                                var pos = Grid.CellToPosCCC(targetCell, Grid.SceneLayer.Ore);
                                var go = LiquidSourceManager.Instance.CreateChunk(elem, mass_cb.mass, mass_cb.temperature,
                                    mass_cb.diseaseIdx, mass_cb.diseaseCount, pos);
                                go.transform.SetPosition(go.transform.GetPosition() + new Vector3((Random.value - 0.5f) * 0.5f, 0f, 0f));
                            }

            /// <summary>
            /// Re-searches the optimal standing cell (called every tick while the order exists, since the liquid flows).
            /// A different cell must stay optimal for <see cref="RepositionWaitTicks"/> consecutive ticks before moving there.
            /// </summary>
            public bool TryReposition()
            {
                using var scope = Scope();
                if (targetCell == Grid.InvalidCell || repositions >= MaxRepositions)
                {
                    return false;
                }
                if (relay)
                {
                    pendingStand = Grid.InvalidCell;
                    pendingTicks = 0;
                    if (!sweep)
                    {
                        EnterSweep();
                    }
                    // keeps the stand memory following the liquid as it spreads
                    SmartMopTargets.FindPreferredStand(targetCell, targetCell, element, MopTool.maxMopAmt, Blocked, NearOther, WorkerCell(), standMemory);
                    if (!relayPlaced && WorkerObject != null && (Leader == this || Leader.relayPlaced))
                    {
                        // The relay has just started: the leader goes to the end of the liquid, the others ahead of it.
                        int stand = RelayStand(false);
                        if (stand == Grid.InvalidCell)
                        {
                            return false;
                        }
                        relayPlaced = true;
                        if (stand != targetCell)
                        {
                            Debug.Log("[TrueSmartMopping] relay: " + targetCell + " -> " + stand + " (start)");
                            targetCell = stand;
                            return true;
                        }
                    }
                    return false;
                }
                if (UpdateDryFront())
                {
                    // Dry front: the order stays until its range is completely dry, then TryFindNext moves it along the edge.
                    pendingStand = Grid.InvalidCell;
                    pendingTicks = 0;
                    if (!dryFrontPlaced)
                    {
                        // Entering the stage: go to the end of the liquid once, wherever the order stood.
                        dryFrontPlaced = true;
                        int edge = FindEdgeStand(WorkerCell());
                        if (edge != Grid.InvalidCell && edge != targetCell)
                        {
                            Debug.Log("[TrueSmartMopping] dry front: " + targetCell + " -> " + edge + " (start)");
                            targetCell = edge;
                            return true;
                        }
                    }
                    return false;
                }
                int best = SmartMopTargets.FindPreferredStand(targetCell, targetCell, element, MopTool.maxMopAmt, Blocked, NearOther, WorkerCell(), standMemory);
                bool rich = false;
                // Low yield: the current range holds only a trickle while a clearly richer one exists (the liquid has flowed away).
                float here = SmartMopTargets.WindowPotential(targetCell, element, MopTool.maxMopAmt, MopAmountPerTick);
                if (here < LowYieldKg)
                {
                    int richer = FindRichStand(WorkerCell(), targetCell, out float potential, null, RichMaxDistance);
                    if (richer != Grid.InvalidCell && potential >= Mathf.Max(RichFactor * here, here + RichMinGainKg))
                    {
                        best = richer;
                        rich = true;
                    }
                }
                if (best == Grid.InvalidCell || best == targetCell)
                {
                    pendingStand = Grid.InvalidCell;
                    pendingTicks = 0;
                    return false;
                }
                // Hysteresis: the liquid keeps flowing, so near-equal stands flip back and forth (the duplicant then walks one cell
                // to and fro). Move only when the new stand covers at least two more cells of liquid, or the current one covers
                // none, and only once the same candidate has stayed best for RepositionWaitTicks consecutive checks.
                if (!rich && SmartMopTargets.WindowCount(best, element, MopTool.maxMopAmt) - WindowCount() < 2 && WindowCount() > 0)
                {
                    pendingStand = Grid.InvalidCell;
                    pendingTicks = 0;
                    return false;
                }
                if (best != pendingStand)
                {
                    pendingStand = best;
                    pendingTicks = 1;
                }
                else
                {
                    pendingTicks++;
                }
                if (pendingTicks < RepositionWaitTicks)
                {
                    return false;
                }
                pendingStand = Grid.InvalidCell;
                pendingTicks = 0;
                repositions++;
                Debug.Log("[TrueSmartMopping] reposition: " + targetCell + " -> " + best + (rich ? " (richer)" : ""));
                targetCell = best;
                return true;
            }

            // Two passes: the first one mops everything above SmartMopTargets.MinLiquidMass per cell and ignores the thin films
            // that keep creeping around; once only films are left (and the rest is thin enough that little flows in), a second
            // pass sweeps them with no lower limit (SweepMinMass = 0): an order never ends while any mop-able liquid is left.
            public const float SweepMinMass = 0f;
            public bool sweep;

            private float MinMass
            {
                get { return sweep ? SweepMinMass : SmartMopTargets.MinLiquidMass; }
            }

            private SmartMopTargets.ThresholdScope Scope()
            {
                return SmartMopTargets.Threshold(MinMass);
            }

            /// <summary>Switches to the sweep pass; false when it already is one.</summary>
            private bool EnterSweep()
            {
                if (sweep)
                {
                    return false;
                }
                sweep = true;
                steps = 0;
                Debug.Log("[TrueSmartMopping] sweep pass: thin films only");
                using (Scope())
                {
                    if (!Grid.IsValidCell(targetCell))
                    {
                        return true;
                    }
                    var fresh = SmartMopTargets.CollectConnected(SmartMopTargets.CellAndNeighbours(targetCell), element, 4000);
                    remembered.UnionWith(fresh);
                }
                return true;
            }

            /// <summary>True when nothing is left to mop in either pass.</summary>
            public bool CheckFinished()
            {
                if (!IsExhausted())
                {
                    return false;
                }
                return !EnterSweep() || IsExhausted();
            }

            // Helper orders: a vanilla mop placer lets two duplicants work one puddle, so a manual order that has plenty of
            // liquid left out of its own reach hands out further orders (same priority, own stand) for idle duplicants.
            // Once a helper has a duplicant the orders work as a relay (see RelayStride).
            // Dry front. Skimming the richest ranges is fast while the liquid is thick, but it leaves a thin film on every cell
            // that hardly flows and has to be visited again cell by cell. So once no range holds more than DryFrontKg, the
            // order works like a vanilla errand: it stands at the end of the liquid (the end nearest to the worker), mops that
            // range until it is completely dry (no lower mass limit) and only then moves one range inward. The liquid then
            // shrinks from its ends and no film is left behind.
            public const float DryFrontKg = 12f;
            public const float DryFrontLeaveKg = 40f;
            public bool dryFront;
            private bool dryFrontPlaced;

            /// <summary>Enters (or, when thick liquid has come back, leaves) the dry front stage; true while in it.</summary>
            public bool UpdateDryFront()
            {
                if (relay)
                {
                    return false;
                }
                float max = 0f;
                using (Scope())
                {
                    foreach (int c in standMemory)
                    {
                        if (SmartMopTargets.IsStandable(c))
                        {
                            max = Mathf.Max(max, SmartMopTargets.WindowPotential(c, element, MopTool.maxMopAmt, MopAmountPerTick));
                        }
                    }
                }
                if (!dryFront && max <= DryFrontKg)
                {
                    dryFront = true;
                    EnterSweep();
                    Debug.Log("[TrueSmartMopping] dry front stage (richest range " + max.ToString("F1") + " kg)");
                }
                else if (dryFront && max > DryFrontLeaveKg)
                {
                    dryFront = false;
                    dryFrontPlaced = false;
                }
                return dryFront;
            }

            /// <summary>
            /// The stand for the dry front: second cell from the end of the liquid nearest to <paramref name="from"/>, so the
            /// range covers the end cell; a stand whose range would overlap another order's range is only taken as a last resort.
            /// The current stand is a valid answer (its range is not dry yet).
            /// </summary>
            private int FindEdgeStand(int from)
            {
                using var scope = Scope();
                int minCol = int.MaxValue, maxCol = int.MinValue;
                foreach (int c in standMemory)
                {
                    if (SmartMopTargets.IsMoppable(c, element, MopTool.maxMopAmt))
                    {
                        int col = Grid.CellColumn(c);
                        minCol = Mathf.Min(minCol, col);
                        maxCol = Mathf.Max(maxCol, col);
                    }
                }
                if (minCol == int.MaxValue)
                {
                    return Grid.InvalidCell;
                }
                int fromCol = Grid.IsValidCell(from) ? Grid.CellColumn(from) : (minCol + maxCol) / 2;
                bool leftEnd = Mathf.Abs(fromCol - minCol) <= Mathf.Abs(fromCol - maxCol);
                int wanted = maxCol - minCol >= 2 ? (leftEnd ? minCol + 1 : maxCol - 1) : (minCol + maxCol) / 2;
                int best = Grid.InvalidCell;
                int bestKey = int.MaxValue;
                foreach (int c in standMemory)
                {
                    if (!SmartMopTargets.IsStandable(c) || Blocked(c))
                    {
                        continue;
                    }
                    if (SmartMopTargets.WindowPotential(c, element, MopTool.maxMopAmt, MopAmountPerTick) <= 0f)
                    {
                        continue;
                    }
                    int col = Grid.CellColumn(c);
                    int key = (OverlapsOtherRange(c) ? 1000000 : 0) + Mathf.Abs(col - wanted) * 1000
                        + (Grid.IsValidCell(from) ? Mathf.Abs(col - fromCol) + Mathf.Abs(Grid.CellRow(c) - Grid.CellRow(from)) : 0);
                    if (key < bestKey)
                    {
                        bestKey = key;
                        best = c;
                    }
                }
                return best;
            }

            // Relay: two or more duplicants on one manual order. This is the fastest pattern of vanilla mopping: the duplicants
            // dry the liquid from one end, a little apart. The one behind stands at the end of the liquid, the one ahead holds
            // back what would flow into the range behind it, so that range dries out quickly; then the one behind goes ahead of
            // the foremost and holds back in turn. Each stays until its own range is dry. Measured on a flat 25-cell puddle of
            // 40 kg per cell: with one cell between neighbours (stride 2), two duplicants took 50-62 s (vanilla dragged again
            // every 5 s: 60-68 s; skimming the richest ranges: 77-81 s) and three took 40-43 s (vanilla 40-46 s).
            // A single duplicant has nobody to hold the liquid back, so it keeps skimming (relay alone left liquid behind).
            public const int DefaultRelayStride = 2;
            internal static int RelayStride = DefaultRelayStride; // columns between the stands of neighbours (tests vary it)
            public const float RelayHelperCheckSeconds = 0.4f;
            public const int RelayIdleTicks = 2;
            public StatesInstance leader; // helpers: the order that handed them out
            public bool relay;
            private int relayDir; // on the leader: +1 the relay runs right, -1 left, 0 not chosen yet
            private bool relayPlaced;

            private StatesInstance Leader
            {
                get { return leader ?? this; }
            }

            // The leader and its helpers that still exist.
            private IEnumerable<StatesInstance> Group()
            {
                var l = Leader;
                yield return l;
                foreach (var h in l.helpers)
                {
                    if (!h.done)
                    {
                        yield return h.smi;
                    }
                }
            }

            /// <summary>Relay is on while at least two duplicants work orders of the same group.</summary>
            public void UpdateRelay()
            {
                int working = 0;
                foreach (var m in Group())
                {
                    if (!m.master.done && m.WorkerObject != null)
                    {
                        working++;
                    }
                }
                bool now = master.manual && working >= 2;
                if (now == relay)
                {
                    return;
                }
                relay = now;
                relayPlaced = false;
                if (now && leader == null)
                {
                    relayDir = 0;
                }
                if (!now)
                {
                    dryFront = false;
                    dryFrontPlaced = false;
                }
                Debug.Log("[TrueSmartMopping] relay " + (now ? "on (" + working + " duplicants)" : "off"));
            }

            /// <summary>
            /// The relay stand. The first one placed (the leader) takes the end of the liquid nearest to its duplicant; the others
            /// stand RelayStride columns ahead of the foremost one placed. With <paramref name="leap"/> this order counts as placed
            /// too (moving on after its range is dry, or handing out a helper ahead of itself). Beyond the far end the stand is
            /// clamped to the liquid, so whatever flowed back is taken last. Invalid while the leader has not chosen a direction.
            /// </summary>
            private int RelayStand(bool leap)
            {
                using var scope = Scope();
                int minCol = int.MaxValue, maxCol = int.MinValue;
                foreach (int c in standMemory)
                {
                    if (SmartMopTargets.IsMoppable(c, element, MopTool.maxMopAmt))
                    {
                        int col = Grid.CellColumn(c);
                        minCol = Mathf.Min(minCol, col);
                        maxCol = Mathf.Max(maxCol, col);
                    }
                }
                if (minCol == int.MaxValue)
                {
                    return Grid.InvalidCell;
                }
                int from = WorkerCell();
                if (!Grid.IsValidCell(from))
                {
                    from = targetCell;
                }
                int row = Grid.IsValidCell(from) ? Grid.CellRow(from) : 0;
                var l = Leader;
                if (l.relayDir == 0)
                {
                    if (l != this)
                    {
                        return Grid.InvalidCell;
                    }
                    int fromCol = Grid.IsValidCell(from) ? Grid.CellColumn(from) : (minCol + maxCol) / 2;
                    relayDir = Mathf.Abs(fromCol - minCol) <= Mathf.Abs(fromCol - maxCol) ? 1 : -1;
                }
                int dir = l.relayDir;
                bool any = false;
                int front = 0;
                foreach (var m in Group())
                {
                    if ((m == this && !leap) || m.master.done || m.WorkerObject == null || !m.relayPlaced || !Grid.IsValidCell(m.targetCell))
                    {
                        continue;
                    }
                    int col = Grid.CellColumn(m.targetCell);
                    if (!any || (col - front) * dir > 0)
                    {
                        front = col;
                    }
                    any = true;
                }
                int wanted = any ? front + RelayStride * dir : (dir > 0 ? minCol : maxCol) + dir;
                int lo = minCol, hi = maxCol;
                if (hi - lo >= 2)
                {
                    lo++;
                    hi--;
                }
                wanted = Mathf.Clamp(wanted, lo, hi);
                int best = Grid.InvalidCell;
                int bestKey = int.MaxValue;
                foreach (int c in standMemory)
                {
                    if (!SmartMopTargets.IsStandable(c) || Blocked(c)
                        || SmartMopTargets.WindowPotential(c, element, MopTool.maxMopAmt, MopAmountPerTick) <= 0f)
                    {
                        continue;
                    }
                    int key = Mathf.Abs(Grid.CellColumn(c) - wanted) * 100 + Mathf.Abs(Grid.CellRow(c) - row);
                    if (key < bestKey)
                    {
                        bestKey = key;
                        best = c;
                    }
                }
                return best;
            }

            /// <summary>
            /// The rearmost duplicant of the relay first takes what has spread behind its range (towards the end the relay
            /// started from: a thin film keeps creeping outwards there). Left alone it is the last thing left, and everybody walks
            /// back the whole length for a few grams. Invalid when nothing is behind, or another duplicant stands further back.
            /// </summary>
            private int RearCleanupStand()
            {
                int dir = Leader.relayDir;
                if (dir == 0 || !Grid.IsValidCell(targetCell))
                {
                    return Grid.InvalidCell;
                }
                int my = Grid.CellColumn(targetCell);
                foreach (var m in Group())
                {
                    if (m == this || m.master.done || m.WorkerObject == null || !m.relayPlaced || !Grid.IsValidCell(m.targetCell))
                    {
                        continue;
                    }
                    if ((Grid.CellColumn(m.targetCell) - my) * dir < 0)
                    {
                        return Grid.InvalidCell;
                    }
                }
                using var scope = Scope();
                bool found = false;
                int rear = 0;
                foreach (int c in standMemory)
                {
                    int col = Grid.CellColumn(c);
                    if ((col - my) * dir <= -2 && SmartMopTargets.IsMoppable(c, element, MopTool.maxMopAmt))
                    {
                        if (!found || col * dir < rear * dir)
                        {
                            rear = col;
                        }
                        found = true;
                    }
                }
                if (!found)
                {
                    return Grid.InvalidCell;
                }
                int wanted = rear + dir;
                int row = Grid.CellRow(targetCell);
                int best = Grid.InvalidCell;
                int bestKey = int.MaxValue;
                foreach (int c in standMemory)
                {
                    if (!SmartMopTargets.IsStandable(c) || Blocked(c)
                        || SmartMopTargets.WindowPotential(c, element, MopTool.maxMopAmt, MopAmountPerTick) <= 0f)
                    {
                        continue;
                    }
                    int key = Mathf.Abs(Grid.CellColumn(c) - wanted) * 100 + Mathf.Abs(Grid.CellRow(c) - row);
                    if (key < bestKey)
                    {
                        bestKey = key;
                        best = c;
                    }
                }
                return best;
            }

            // True for the marker (chore master) of an order of this group.
            private bool IsGroupMarker(GameObject go)
            {
                foreach (var m in Group())
                {
                    if (m.master.gameObject == go)
                    {
                        return true;
                    }
                }
                return false;
            }

            // Marching direction along the row in the dry front stage: +1 right, -1 left, 0 not chosen yet.
            private int marchDirection;
            public const int MarchMinTicks = 2;
            public const float MarchLeaveKg = 1f;

            /// <summary>
            /// Dry front, thin liquid: a range is emptied in one or two work ticks and what flows in afterwards is a trickle not
            /// worth waiting for. True when the order should move on to the next range.
            /// </summary>
            public bool MarchShouldLeave()
            {
                if (!dryFront || mopTicks < MarchMinTicks)
                {
                    return false;
                }
                using var scope = Scope();
                return SmartMopTargets.WindowPotential(targetCell, element, MopTool.maxMopAmt, MopAmountPerTick) < MarchLeaveKg;
            }

            /// <summary>
            /// The next stand of the march: the range right after the current one in the marching direction (skipping dry cells),
            /// turning round at the end of the liquid. Passes repeat until nothing is left.
            /// </summary>
            private int FindMarchStand(int from)
            {
                using var scope = Scope();
                if (!Grid.IsValidCell(from))
                {
                    return FindEdgeStand(from);
                }
                int row = Grid.CellRow(from);
                int fromCol = Grid.CellColumn(from);
                var liquid = new HashSet<int>();
                int minCol = int.MaxValue, maxCol = int.MinValue;
                foreach (int c in standMemory)
                {
                    if (SmartMopTargets.IsMoppable(c, element, MopTool.maxMopAmt))
                    {
                        int col = Grid.CellColumn(c);
                        liquid.Add(col);
                        minCol = Mathf.Min(minCol, col);
                        maxCol = Mathf.Max(maxCol, col);
                    }
                }
                if (liquid.Count == 0)
                {
                    return Grid.InvalidCell;
                }
                if (marchDirection == 0)
                {
                    marchDirection = Mathf.Abs(fromCol - minCol) <= Mathf.Abs(fromCol - maxCol) ? 1 : -1;
                }
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    // first liquid column beyond the current range, in the marching direction
                    int first = int.MaxValue;
                    foreach (int col in liquid)
                    {
                        int ahead = (col - fromCol) * marchDirection;
                        if (ahead >= 2 && ahead < first)
                        {
                            first = ahead;
                        }
                    }
                    if (first != int.MaxValue)
                    {
                        int wanted = fromCol + (first + 1) * marchDirection;
                        int best = Grid.InvalidCell;
                        int bestKey = int.MaxValue;
                        foreach (int c in standMemory)
                        {
                            if (!SmartMopTargets.IsStandable(c) || Blocked(c)
                                || SmartMopTargets.WindowPotential(c, element, MopTool.maxMopAmt, MopAmountPerTick) <= 0f)
                            {
                                continue;
                            }
                            int col = Grid.CellColumn(c);
                            if ((col - fromCol) * marchDirection < 1)
                            {
                                continue;
                            }
                            int key = (OverlapsOtherRange(c) ? 100000 : 0) + Mathf.Abs(col - wanted) * 100 + Mathf.Abs(Grid.CellRow(c) - row);
                            if (key < bestKey)
                            {
                                bestKey = key;
                                best = c;
                            }
                        }
                        if (best != Grid.InvalidCell)
                        {
                            return best;
                        }
                    }
                    marchDirection = -marchDirection; // end of the liquid: turn round
                }
                // nothing ahead either way: only the current range (or one overlapping it) still holds liquid
                return FindEdgeStand(from);
            }

            // True when the 3-wide range of this stand shares a cell with the range of another order's stand.
            private bool OverlapsOtherRange(int cell)
            {
                foreach (var claim in Claims)
                {
                    if (claim.Value != this && Grid.CellRow(claim.Key) == Grid.CellRow(cell)
                        && Mathf.Abs(Grid.CellColumn(claim.Key) - Grid.CellColumn(cell)) <= 2)
                    {
                        return true;
                    }
                }
                return false;
            }

            public const float NearBonus = 1f;

            // Score factor for standing next to another order: far from it while the window is rich, close to it (side by side,
            // so the one at the edge finishes quickly and they leapfrog, as with vanilla placers) in the thin tail.
            private float NearFactor(int cell, float potential)
            {
                if (!NearOther(cell))
                {
                    return 1f;
                }
                return potential >= NearPenaltyMinKg ? 0.1f : NearBonus;
            }

            public const float NearPenaltyMinKg = 20f;
            public const int MaxHelpers = 2;
            public const float HelperMinKg = 5f;
            public const float HelperCheckSeconds = 1f;
            public bool isHelper;
            private readonly List<SmartMopChore> helpers = new List<SmartMopChore>();
            private float nextHelperCheck;

            /// <summary>
            /// Hands out one more order for an idle duplicant; at most one unstarted helper at a time. Before the relay starts it
            /// goes to the richest free stand, during the relay ahead of the foremost duplicant.
            /// </summary>
            public void TrySpawnHelper()
            {
                if (isHelper || !master.manual || WorkerObject == null || Time.time < nextHelperCheck || targetCell == Grid.InvalidCell)
                {
                    return;
                }
                nextHelperCheck = Time.time + (relay ? RelayHelperCheckSeconds : HelperCheckSeconds);
                helpers.RemoveAll(h => h.done);
                if (helpers.Count >= MaxHelpers || helpers.Exists(h => h.smi.WorkerObject == null))
                {
                    return;
                }
                float bestKg;
                int best = relay ? RelayHelperStand(out bestKg) : RichHelperStand(out bestKg);
                if (best == Grid.InvalidCell)
                {
                    return;
                }
                var prioritizable = master.gameObject.GetComponent<Prioritizable>();
                var saved = new Saved
                {
                    targetCell = best,
                    element = element,
                    remembered = new List<int>(remembered).ToArray(),
                    memory = new List<int>(standMemory).ToArray(),
                };
                var priority = prioritizable != null ? prioritizable.GetMasterPriority() : new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);
                var helper = SmartMopTool.CreateOrderChore(best, priority, saved);
                if (helper != null)
                {
                    helper.smi.isHelper = true;
                    helper.smi.leader = this;
                    helper.smi.sweep = sweep;
                    helpers.Add(helper);
                    Debug.Log("[TrueSmartMopping] helper order at " + best + " (" + bestKg.ToString("F0") + " kg/tick)" + (relay ? " (relay)" : ""));
                }
            }

            /// <summary>When the order ends, helpers nobody has taken are withdrawn (nobody walks there to find nothing).</summary>
            public void CancelUnstartedHelpers()
            {
                foreach (var h in helpers)
                {
                    if (!h.done && h.smi.WorkerObject == null)
                    {
                        h.MarkDone();
                        h.Cancel("SmartMop leader finished");
                        h.smi.ReleaseClaim();
                    }
                }
                // helpers still at work stay in the group (they keep relaying among themselves)
                helpers.RemoveAll(h => h.done);
            }

            private int RelayHelperStand(out float kg)
            {
                kg = 0f;
                if (!relayPlaced)
                {
                    return Grid.InvalidCell;
                }
                int best = RelayStand(true);
                if (best == targetCell)
                {
                    return Grid.InvalidCell;
                }
                using (Scope())
                {
                    kg = best == Grid.InvalidCell ? 0f : SmartMopTargets.WindowPotential(best, element, MopTool.maxMopAmt, MopAmountPerTick);
                }
                return best;
            }

            private int RichHelperStand(out float kg)
            {
                int best = Grid.InvalidCell;
                kg = HelperMinKg;
                using (Scope())
                {
                    foreach (int c in standMemory)
                    {
                        if (!SmartMopTargets.IsStandable(c) || Blocked(c) || TooCloseToAnyOrder(c))
                        {
                            continue;
                        }
                        float p = SmartMopTargets.WindowPotential(c, element, MopTool.maxMopAmt, MopAmountPerTick);
                        if (NearOther(c) && p >= NearPenaltyMinKg)
                        {
                            continue;
                        }
                        if (p > kg)
                        {
                            kg = p;
                            best = c;
                        }
                    }
                }
                return best;
            }

            // Another order's stand within reach of this window (the 3-wide ranges would overlap).
            private bool TooCloseToAnyOrder(int cell)
            {
                foreach (int claimed in Claims.Keys)
                {
                    if (Grid.CellRow(claimed) == Grid.CellRow(cell) && Mathf.Abs(Grid.CellColumn(claimed) - Grid.CellColumn(cell)) <= 1)
                    {
                        return true;
                    }
                }
                return false;
            }

            // Reposition bookkeeping: separate from the step budget, which only counts "go to the next liquid" moves.
            public const int RepositionWaitTicks = 2;
            public const int MaxRepositions = 200;
            // The low-yield jump only goes to a neighbouring window (one 3-wide range away); farther liquid is reached once the
            // current range is empty. Long jumps made the duplicant circle around liquid that kept flowing back.
            public const int RichMaxDistance = 3;
            public const float LowYieldKg = 5f;
            public const float RichFactor = 2f;
            public const float RichMinGainKg = 4f;
            private int repositions;
            private int pendingStand = Grid.InvalidCell;
            private int pendingTicks;
            /// <summary>
            /// The standing cell with the best value of (mass removable per work tick) / (distance from <paramref name="from"/> + 3),
            /// skipping blocked cells and <paramref name="exclude"/>; cells next to another order count a tenth.
            /// </summary>
            private int FindRichStand(int from, int exclude, out float potential, Navigator nav = null, int maxDistance = int.MaxValue)
            {
                potential = 0f;
                int best = Grid.InvalidCell;
                float bestScore = 0f;
                var top = nav != null ? new List<KeyValuePair<float, int>>() : null;
                var cells = standMemory.Count > 0 ? standMemory : remembered;
                foreach (int c in cells)
                {
                    if (c == exclude || !SmartMopTargets.IsStandable(c) || Blocked(c))
                    {
                        continue;
                    }
                    float p = SmartMopTargets.WindowPotential(c, element, MopTool.maxMopAmt, MopAmountPerTick);
                    if (p <= 0f)
                    {
                        continue;
                    }
                    if (maxDistance != int.MaxValue && Grid.IsValidCell(from)
                        && Mathf.Abs(Grid.CellColumn(c) - Grid.CellColumn(from)) + Mathf.Abs(Grid.CellRow(c) - Grid.CellRow(from)) > maxDistance)
                    {
                        continue;
                    }
                    int dist = Grid.IsValidCell(from)
                        ? Mathf.Abs(Grid.CellColumn(c) - Grid.CellColumn(from)) + Mathf.Abs(Grid.CellRow(c) - Grid.CellRow(from))
                        : 0;
                    float score = p / (dist + 3f) * NearFactor(c, p);
                    top?.Add(new KeyValuePair<float, int>(score, c));
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = c;
                        potential = p;
                    }
                }
                if (top != null && top.Count > 1)
                {
                    // Re-rank the best few by the real walking cost: a stand on the far side of a wall or a deep pool can be
                    // close in a straight line and far (or unreachable) on foot.
                    top.Sort((a, b) => b.Key.CompareTo(a.Key));
                    bestScore = 0f;
                    best = Grid.InvalidCell;
                    for (int i = 0; i < top.Count && (i < RerankCount || (best == Grid.InvalidCell && i < 3 * RerankCount)); i++)
                    {
                        int c = top[i].Value;
                        int cost = nav.GetNavigationCost(c);
                        if (cost < 0)
                        {
                            continue;
                        }
                        float p = SmartMopTargets.WindowPotential(c, element, MopTool.maxMopAmt, MopAmountPerTick);
                        float score = p / (cost / NavCostPerCell + 3f) * NearFactor(c, p);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = c;
                            potential = p;
                        }
                    }
                }
                return best;
            }

            // Navigator costs are about 10 per cell walked.
            public const int RerankCount = 4;
            public const float NavCostPerCell = 10f;

            public int moveWaitTicks;

            /// <summary>
            /// After the current range is clear, looks at the 5 cells around the duplicant (and the old standing cell)
            /// and picks the best standing cell for the liquid found there.
            /// </summary>
            public bool TryFindNext(out int next)
            {
                next = Grid.InvalidCell;
                if (!HasStepBudget())
                {
                    return false;
                }
                int own = Grid.PosToCell(WorkerObject.transform.GetPosition());
                float potential;
                if (relay)
                {
                    int stand = RearCleanupStand();
                    bool behind = stand != Grid.InvalidCell;
                    if (!behind)
                    {
                        stand = RelayStand(true);
                    }
                    if (stand != Grid.InvalidCell)
                    {
                        steps++;
                        Debug.Log("[TrueSmartMopping] relay: " + targetCell + " -> " + stand + (behind ? " (behind)" : ""));
                        next = stand;
                        return true;
                    }
                }
                if (UpdateDryFront())
                {
                    int edge = FindMarchStand(own);
                    if (edge != Grid.InvalidCell)
                    {
                        steps++;
                        Debug.Log("[TrueSmartMopping] dry front: " + targetCell + " -> " + edge);
                        next = edge;
                        return true;
                    }
                }
                using (Scope())
                {
                    next = FindRichStand(own, Grid.InvalidCell, out potential, WorkerObject.GetComponent<Navigator>());
                }
                if (next == Grid.InvalidCell && EnterSweep())
                {
                    using (Scope())
                    {
                        next = FindRichStand(own, Grid.InvalidCell, out potential, WorkerObject.GetComponent<Navigator>());
                    }
                }
                if (next == Grid.InvalidCell)
                {
                    return false;
                }
                steps++;
                Debug.Log("[TrueSmartMopping] next target: " + targetCell + " -> " + next + " (" + potential.ToString("F0") + " kg/tick)");
                return true;
            }
        }

        public class States : GameStateMachine<States, StatesInstance, SmartMopChore>
        {
            public State moveToTarget;
            public State mopping;
            public State chooseNext;
            public State moveFailed;
            public State finished;
            public TargetParameter worker;

            public override void InitializeStates(out BaseState default_state)
            {
                default_state = moveToTarget;
                Target(worker);

                root.Update((smi, dt) =>
                {
                    if (smi.cancelled || smi.master.done)
                    {
                        return;
                    }
                    smi.UpdateRelay();
                    if (smi.CheckFinished())
                    {
                        smi.GoTo(finished);
                        return;
                    }
                    smi.TrySpawnHelper();
                    // The order switches to the better cell immediately; a duplicant that is mopping follows after a short wait.
                    if (smi.TryReposition() && smi.GetCurrentState() == moveToTarget)
                    {
                        smi.GoTo(moveToTarget);
                    }
                }, UpdateRate.SIM_200ms);

                moveToTarget
                    .Enter(smi =>
                    {
                        smi.moveStart = Time.time;
                        smi.moves++;
                        Debug.Log("[TrueSmartMopping] moving to " + smi.targetCell);
                    })
                    .Exit(smi => smi.moveTime += Time.time - smi.moveStart)
                    .MoveTo(smi => smi.targetCell, mopping, moveFailed, update_cell: true);

                moveFailed.Enter(smi =>
                {
                    smi.moveFailures++;
                    if (smi.moveFailures >= 2)
                    {
                        smi.GoTo(finished);
                    }
                    else
                    {
                        smi.GoTo(chooseNext);
                    }
                });

                mopping
                    .Enter(smi =>
                    {
                        smi.mopStart = Time.time;
                        smi.moveFailures = 0;
                        smi.mopTicks = 0;
                        smi.idleTicks = 0;
                        smi.moveWaitTicks = 0;
                        smi.RefreshRemembered();
                        Debug.Log("[TrueSmartMopping] mopping at " + smi.targetCell);
                        smi.sm.worker.Get<KBatchedAnimController>(smi).PlaySpeedMultiplier = MopAnimSpeed;
                    })
                    .ToggleAnims("anim_mop_dirtywater_kanim")
                    .PlayAnim("working_pre", KAnim.PlayMode.Once)
                    .QueueAnim("working_loop", true)
                    .ToggleEffect("BotMopping")
                    .Update((smi, dt) =>
                    {
                        smi.mopTimer += dt;
                        if (Grid.PosToCell(smi.WorkerObject.transform.GetPosition()) != smi.targetCell)
                        {
                            if (++smi.moveWaitTicks >= IdleTicksBeforeNext)
                            {
                                smi.moveWaitTicks = 0;
                                smi.GoTo(moveToTarget);
                            }
                        }
                        else if (smi.WindowCount() == 0)
                        {
                            // Vanilla keeps working for a moment after the liquid is gone; pretend to mop before moving on.
                            if (++smi.idleTicks >= (smi.relay ? StatesInstance.RelayIdleTicks : smi.dryFront ? 1 : IdleTicksBeforeNext))
                            {
                                smi.GoTo(chooseNext);
                            }
                        }
                        else if (!smi.relay && (smi.mopTimer > 16f || smi.MarchShouldLeave()))
                        {
                            smi.GoTo(chooseNext);
                        }
                        else
                        {
                            smi.MopWindow();
                        }
                    }, UpdateRate.SIM_200ms)
                    .Exit(smi =>
                    {
                        smi.mopTime += Time.time - smi.mopStart;
                        smi.mopTimer = 0f;
                        smi.sm.worker.Get<KBatchedAnimController>(smi).PlaySpeedMultiplier = 1f;
                    });

                chooseNext
                    .Enter(smi =>
                    {
                        if (smi.TryFindNext(out int next))
                        {
                            smi.targetCell = next;
                            smi.GoTo(moveToTarget);
                        }
                        else
                        {
                            smi.GoTo(finished);
                        }
                    });

                finished.Enter(smi =>
                {
                    float total = Time.time - smi.startTime;
                    Debug.Log("[TrueSmartMopping] chore finished: total=" + total.ToString("F1") + "s move=" + smi.moveTime.ToString("F1") + "s mop=" + smi.mopTime.ToString("F1") + "s moves=" + smi.moves + " mopped=" + smi.totalMopped.ToString("F1") + "kg rate=" + (smi.totalMopped / Mathf.Max(total, 0.01f)).ToString("F1") + "kg/s");
                    smi.ReleaseClaim();
                    smi.CancelUnstartedHelpers();
                    smi.master.MarkDone();
                    // Cancel removes the chore from the provider; otherwise run_until_complete restarts it endlessly.
                    smi.master.Cancel("SmartMopChore finished");
                });
            }
        }
    }
}
