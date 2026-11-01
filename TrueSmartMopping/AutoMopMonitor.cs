using UnityEngine;

namespace TrueSmartMopping;

/// <summary>
/// Attached to each duplicant. When the duplicant moves into a cell holding
/// mop-able liquid, a vanilla Moppable is placed on that cell so the normal
/// mop chore picks it up.
/// </summary>
public sealed class AutoMopMonitor : KMonoBehaviour
{
    private int cellChangedHandle = -1;
    private SmartMopChore activeChore;
    private float activeChoreTime;
    private string lastTrace = "";
    private float nextCheckTime;
    private int suppressCell = Grid.InvalidCell;
    private float suppressUntil;
    private const float StaleChoreSeconds = 20f;
    private const float CheckIntervalSeconds = 0.5f;

    private bool IsDriverRunning(SmartMopChore chore)
    {
        ChoreDriver driver = GetComponent<ChoreDriver>();
        return driver != null && driver.GetCurrentChore() == chore;
    }

    protected override void OnSpawn()
    {
        base.OnSpawn();
        // The legacy CellChangeMonitor.RegisterCellChangedHandler throws InvalidCastException in U60.
        cellChangedHandle = Subscribe((int)GameHashes.CellChanged, OnCellChanged);
        OnMove();
    }

    private void OnCellChanged(object data)
    {
        OnMove();
    }

    private void Update()
    {
        if (Time.time < nextCheckTime)
        {
            return;
        }
        nextCheckTime = Time.time + CheckIntervalSeconds;
        OnMove();
    }

    protected override void OnCleanUp()
    {
        Unsubscribe(ref cellChangedHandle);
        base.OnCleanUp();
    }

    private void Trace(string reason)
    {
        if (reason != lastTrace)
        {
            lastTrace = reason;
            Debug.Log("[TrueSmartMopping] " + transform.name + ": " + reason);
        }
    }

    /// <summary>
    /// True when the duplicant is heading to / working on another smart mop order, or a chore with higher priority than the
    /// auto mop priority, other than a plain "move here".
    /// </summary>
    private bool IsBusyWithOtherOrder()
    {
        var driver = GetComponent<ChoreDriver>();
        var current = driver?.GetCurrentChore();
        if (current == null || current == activeChore)
        {
            return false;
        }
        if (current is SmartMopChore)
        {
            return true;
        }
        if (current.choreType == Db.Get().ChoreTypes.MoveTo)
        {
            return false;
        }
        var p = current.masterPriority;
        int auto = ModSettings.AutoMopPriority;
        return p.priority_class > PriorityScreen.PriorityClass.basic
               || (p.priority_class == PriorityScreen.PriorityClass.basic && p.priority_value > auto);
    }

    private void OnMove()
    {
        ChoreProvider provider = GetComponent<ChoreProvider>();
        if (activeChore != null && (activeChore.isComplete || activeChore.done))
        {
            if (activeChore.smi.cancelled)
            {
                suppressCell = Grid.PosToCell(transform.GetPosition());
                suppressUntil = Time.time + 30f;
            }
            activeChore = null;
        }
        if (!ModSettings.AutoMopEnabled)
        {
            Trace("disabled");
            return;
        }
        if (SmartMopSaveData.AutoMopBlocked)
        {
            Trace("waiting after load");
            return;
        }
        int cell = Grid.PosToCell(transform.GetPosition());
        if (cell == suppressCell && Time.time < suppressUntil)
        {
            Trace("cancelled by player");
            return;
        }
        if (!Grid.IsValidCell(cell) || Grid.Solid[cell])
        {
            Trace("invalid/solid cell");
            return;
        }
        if (!Grid.Element[cell].IsLiquid || Grid.Mass[cell] <= 0f)
        {
            Trace("no liquid");
            return;
        }
        if (ModSettings.ForbiddenCells.Contains(cell)
            || !ModSettings.IsLiquidTargeted(Grid.Element[cell].id))
        {
            Trace("forbidden/not targeted");
            return;
        }
        if (provider == null)
        {
            Trace("no provider");
            return;
        }
        if (IsBusyWithOtherOrder())
        {
            Trace("busy with another smart mop or higher priority chore");
            return;
        }
        if (activeChore != null)
        {
            bool started = IsDriverRunning(activeChore);
            if (started || Time.time - activeChoreTime < StaleChoreSeconds)
            {
                Trace("active chore running (started=" + started + ")");
                return;
            }
            Trace("removing stale chore");
            provider.RemoveChore(activeChore);
            activeChore.smi.ReleaseClaim();
            activeChore = null;
        }
        Trace("creating chore at cell " + cell);
        var chore = new SmartMopChore(this, provider, cell, PriorityScreen.PriorityClass.basic, ModSettings.AutoMopPriority);
        if (chore.smi.targetCell == Grid.InvalidCell)
        {
            Trace("no usable stand cell");
            return;
        }
        activeChore = chore;
        activeChoreTime = Time.time;
        provider.AddChore(activeChore);
        CancelMoveHere();
    }

    /// <summary>Recreates an automatic order of this duplicant from a save file.</summary>
    public void RestoreChore(int startCell, PrioritySetting priority, SmartMopChore.Saved saved)
    {
        var provider = GetComponent<ChoreProvider>();
        if (provider == null)
        {
            return;
        }
        if (activeChore != null)
        {
            provider.RemoveChore(activeChore);
            activeChore.smi.ReleaseClaim();
            activeChore = null;
        }
        var chore = new SmartMopChore(this, provider, startCell, priority.priority_class, priority.priority_value, false, saved);
        if (chore.smi.targetCell == Grid.InvalidCell)
        {
            return;
        }
        activeChore = chore;
        activeChoreTime = Time.time;
        provider.AddChore(chore);
    }

    /// <summary>A "move here" order gives way to the new auto mop order.</summary>
    private void CancelMoveHere()
    {
        var current = GetComponent<ChoreDriver>()?.GetCurrentChore();
        if (current == null || current.choreType != Db.Get().ChoreTypes.MoveTo)
        {
            return;
        }
        var move = gameObject.GetSMI<MoveToLocationMonitor.Instance>();
        if (move != null)
        {
            move.GoTo(move.sm.satisfied);
        }
    }
}
