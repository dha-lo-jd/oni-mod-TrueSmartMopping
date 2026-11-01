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
                return SmartMopTargets.IsExhausted(standMemory, element, MopTool.maxMopAmt);
            }

            private bool HasOtherObject(int cell)
            {
                return Grid.IsValidCell(cell) && Grid.Objects[cell, 8] != null && Grid.Objects[cell, 8] != master.gameObject;
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
                return Grid.IsValidCell(cell) && Claims.TryGetValue(cell, out var o) && o != this;
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
                return SmartMopTargets.WindowCount(targetCell, element, MopTool.maxMopAmt);
            }

            /// <summary>Mops one tick worth of every mop-able cell within range of the standing cell.</summary>
            public void MopWindow()
            {
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
                totalMopped += amount;
                Moppable.MopCell(cell, amount, (mass_cb, data) =>                 SpawnChunk(mass_cb));
            }

                            // Mirrors Moppable.OnCellMopped: chunk appears at the standing (placer) cell with a random x offset.
                            private void SpawnChunk(Sim.MassConsumedCallback mass_cb)
                            {
                                if (mass_cb.mass <= 0f)
                                {
                                    return;
                                }
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
                if (!HasStepBudget() || targetCell == Grid.InvalidCell)
                {
                    return false;
                }
                int best = SmartMopTargets.FindPreferredStand(targetCell, targetCell, element, MopTool.maxMopAmt, Blocked, NearOther, WorkerCell(), standMemory);
                if (best == Grid.InvalidCell || best == targetCell)
                {
                    return false;
                }
                steps++;
                Debug.Log("[TrueSmartMopping] reposition: " + targetCell + " -> " + best);
                targetCell = best;
                return true;
            }

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
                int found = Grid.InvalidCell;
                int bestScore = int.MaxValue;
                foreach (int c in remembered)
                {
                    if (!SmartMopTargets.IsMoppable(c, element, MopTool.maxMopAmt))
                    {
                        continue;
                    }
                    int score = Mathf.Abs(Grid.CellColumn(c) - Grid.CellColumn(own)) + Mathf.Abs(Grid.CellRow(c) - Grid.CellRow(own))
                        + (NearOther(c) ? 1000 : 0);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        found = c;
                    }
                }
                if (found == Grid.InvalidCell)
                {
                    return false;
                }
                next = SmartMopTargets.FindPreferredStand(found, own, element, MopTool.maxMopAmt, Blocked, NearOther, own, standMemory);
                if (next == Grid.InvalidCell)
                {
                    return false;
                }
                steps++;
                Debug.Log("[TrueSmartMopping] next target: " + targetCell + " -> " + next);
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
                    if (smi.IsExhausted())
                    {
                        smi.GoTo(finished);
                        return;
                    }
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
                            if (++smi.idleTicks >= IdleTicksBeforeNext)
                            {
                                smi.GoTo(chooseNext);
                            }
                        }
                        else if (smi.mopTimer > 16f)
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
                    smi.master.MarkDone();
                    // Cancel removes the chore from the provider; otherwise run_until_complete restarts it endlessly.
                    smi.master.Cancel("SmartMopChore finished");
                });
            }
        }
    }
}
