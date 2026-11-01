using System;
using System.Collections.Generic;

namespace TrueSmartMopping;

/// <summary>
/// Picks the next cell for smart mopping. Right-hand cells are preferred on ties.
/// </summary>
public static class SmartMopTargets
{
    /// <summary>
    /// Returns an adjacent left/right cell holding the same liquid with more mass than
    /// <paramref name="previousMass"/>, i.e. the inflow source. Returns Grid.InvalidCell when none exists.
    /// </summary>
    public static int FindInflowSource(int cell, SimHashes element, float previousMass, float maxMopAmt)
    {
        if (!Grid.IsValidCell(cell))
        {
            return Grid.InvalidCell;
        }

        int right = Grid.CellRight(cell);
        if (IsLiquidOf(right, element) && Grid.Mass[right] > previousMass && Grid.Mass[right] <= maxMopAmt)
        {
            return right;
        }

        int left = Grid.CellLeft(cell);
        if (IsLiquidOf(left, element) && Grid.Mass[left] > previousMass && Grid.Mass[left] <= maxMopAmt)
        {
            return left;
        }

        return Grid.InvalidCell;
    }

    /// <summary>Start standing cell for a smart mop order: best stand around the clicked cell, avoiding liquid edges unless nothing else qualifies.</summary>
    public static int FindStart(int cell, SimHashes element, float maxMopAmt, System.Func<int, bool> blocked,
        System.Func<int, bool> nearOther, int workerCell = -1, HashSet<int> memory = null)
    {
        if (!Grid.IsValidCell(cell) || !IsLiquidOf(cell, element))
        {
            return Grid.InvalidCell;
        }

        return FindPreferredStand(cell, cell, element, maxMopAmt, blocked, nearOther, workerCell, memory);
    }

    /// <summary>
    /// Standing cell chosen among the contiguous liquid span in the row of <paramref name="cell"/>. Priorities, compared
    /// in order a..e (higher is better): a: -1 if not mop-able; b: -1 if outermost cell; c: -1 if next to another
        /// smart mop order; c2: minus the distance from <paramref name="workerCell"/> (when given); d: minus the distance from the nearer edge; e: -1 if left of the middle. Unmop-able and blocked
    /// cells are never chosen, and an edge cell is chosen only when no other cell qualifies. Ties keep the current cell.
    /// </summary>
    public static int FindPreferredStand(int cell, int current, SimHashes element, float maxMopAmt,
        System.Func<int, bool> blocked, System.Func<int, bool> nearOther, int workerCell = -1,
        HashSet<int> memory = null)
    {
        blocked ??= _ => false;
        nearOther ??= _ => false;
        memory ??= new HashSet<int>();
        if (!Grid.IsValidCell(cell))
        {
            return Grid.InvalidCell;
        }

        // The remembered whole liquid is kept; once none of it (nor its neighbours) can be mopped, there is nothing to choose.
        if (IsExhausted(memory, element, maxMopAmt))
        {
            return Grid.InvalidCell;
        }

        if (!IsLiquidOf(cell, element) && !memory.Contains(cell))
        {
            cell = FindNearestMoppable(cell, element, maxMopAmt, 200);
            if (cell == Grid.InvalidCell)
            {
                return Grid.InvalidCell;
            }
        }

        // Add unremembered liquid cells adjacent to the remembered ones.
        var queue = new Queue<int>(memory);
        if (IsLiquidOf(cell, element) && memory.Add(cell))
        {
            queue.Enqueue(cell);
        }

        while (queue.Count > 0 && memory.Count < 4000)
        {
            foreach (int n in Neighbours(queue.Dequeue()))
            {
                if (IsLiquidOf(n, element) && memory.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }

        int right = cell;
        while (IsLiquidOf(Grid.CellRight(right), element) || memory.Contains(Grid.CellRight(right)))
        {
            right = Grid.CellRight(right);
        }

        int left = cell;
        while (IsLiquidOf(Grid.CellLeft(left), element) || memory.Contains(Grid.CellLeft(left)))
        {
            left = Grid.CellLeft(left);
        }

        int colL = Grid.CellColumn(left);
        int colR = Grid.CellColumn(right);
        int best = Grid.InvalidCell;
        int[] bestKey = null;
        for (int c = left; ; c = Grid.CellRight(c))
        {
            int col = Grid.CellColumn(c);
            bool edge = col == colL || col == colR;
            if (!blocked(c))
            {
                int[] key =
                {
                    IsMoppable(c, element, maxMopAmt) ? 0 : -1,
                    edge ? -1 : 0,
                    nearOther(c) ? -1 : 0,
                    Grid.IsValidCell(workerCell)
                        ? -(System.Math.Abs(col - Grid.CellColumn(workerCell)) + System.Math.Abs(Grid.CellRow(c) - Grid.CellRow(workerCell)))
                        : 0,
                    -System.Math.Min(col - colL, colR - col),
                    2 * col < colL + colR ? -1 : 0,
                    c == current ? 1 : 0
                };
                if (bestKey == null || CompareKeys(key, bestKey) > 0)
                {
                    bestKey = key;
                    best = c;
                }
            }

            if (c == right)
            {
                break;
            }
        }

        // A cell with priority a < 0 (not mop-able) counts as no cell chosen.
        return bestKey == null || bestKey[0] < 0 ? Grid.InvalidCell : best;
    }

    /// <summary>True when the memory is non-empty and none of its cells, nor their neighbours, can be mopped.</summary>
    public static bool IsExhausted(HashSet<int> memory, SimHashes element, float maxMopAmt)
    {
        if (memory.Count == 0)
        {
            return false;
        }

        foreach (int m in memory)
        {
            if (IsMoppable(m, element, maxMopAmt))
            {
                return false;
            }

            foreach (int n in Neighbours(m))
            {
                if (IsMoppable(n, element, maxMopAmt))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int CompareKeys(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return a[i] > b[i] ? 1 : -1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Breadth-first search through connected same-liquid cells starting at <paramref name="origin"/>
    /// (the origin itself is not required to hold liquid). Returns the nearest cell whose mass is
    /// at most <paramref name="maxMopAmt"/>, or Grid.InvalidCell when none is found within <paramref name="maxDepth"/> steps.
    /// </summary>
    public static int FindNearestMoppable(int origin, SimHashes element, float maxMopAmt, int maxDepth)
    {
        if (!Grid.IsValidCell(origin))
        {
            return Grid.InvalidCell;
        }

        var visited = new HashSet<int> { origin };
        var frontier = new List<int> { origin };
        for (int depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var nextFrontier = new List<int>();
            foreach (int current in frontier)
            {
                foreach (int other in Neighbours(current))
                {
                    if (!visited.Add(other) || !IsLiquidOf(other, element))
                    {
                        continue;
                    }

                    if (Grid.Mass[other] <= maxMopAmt)
                    {
                        return other;
                    }

                    nextFrontier.Add(other);
                }
            }

            frontier = nextFrontier;
        }

        return Grid.InvalidCell;
    }

    /// <summary>
    /// Returns the first mop-able same-liquid cell among the given cell and the two cells on each side, or Grid.InvalidCell.
    public static int FindLiquidAround(int cell, SimHashes element, float maxMopAmt)
    {
        if (!Grid.IsValidCell(cell))
        {
            return Grid.InvalidCell;
        }

        int right = Grid.CellRight(cell);
        int left = Grid.CellLeft(cell);
        int right2 = Grid.IsValidCell(right) ? Grid.CellRight(right) : Grid.InvalidCell;
        int left2 = Grid.IsValidCell(left) ? Grid.CellLeft(left) : Grid.InvalidCell;
        foreach (int c in new[] { cell, right, left, right2, left2 })
        {
            if (IsLiquidOf(c, element) && Grid.Mass[c] <= maxMopAmt && HasSolidBelow(c))
            {
                return c;
            }
        }

        return Grid.InvalidCell;
    }

    /// <summary>
    /// Collects every cell of liquid <paramref name="element"/> connected to any of the origin cells
    /// (the origins themselves are only seeds and need not hold liquid).
    /// </summary>
    public static HashSet<int> CollectConnected(IEnumerable<int> origins, SimHashes element, int maxVisited)
    {
        var result = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (int o in origins)
        {
            if (IsLiquidOf(o, element) && !ModSettings.ForbiddenCells.Contains(o) && result.Add(o))
            {
                queue.Enqueue(o);
            }
        }

        while (queue.Count > 0 && result.Count < maxVisited)
        {
            int current = queue.Dequeue();
            foreach (int other in Neighbours(current))
            {
                if (IsLiquidOf(other, element) && !ModSettings.ForbiddenCells.Contains(other) && result.Add(other))
                {
                    queue.Enqueue(other);
                }
            }
        }

        return result;
    }

    public static IEnumerable<int> CellAndNeighbours(int cell)
    {
        yield return cell;
        foreach (int n in Neighbours(cell))
        {
            yield return n;
        }
    }

    public static bool IsMoppable(int cell, SimHashes element, float maxMopAmt)
    {
        return IsLiquidOf(cell, element) && !ModSettings.ForbiddenCells.Contains(cell) &&
               Grid.Mass[cell] <= maxMopAmt && HasSolidBelow(cell);
    }

    /// <summary>Number of mop-able cells within the 3-wide mop range centred on <paramref name="stand"/>.</summary>
    public static int WindowCount(int stand, SimHashes element, float maxMopAmt)
    {
        if (!Grid.IsValidCell(stand))
        {
            return 0;
        }

        int n = IsMoppable(stand, element, maxMopAmt) ? 1 : 0;
        int left = Grid.CellLeft(stand);
        int right = Grid.CellRight(stand);
        if (IsMoppable(left, element, maxMopAmt)) n++;
        if (IsMoppable(right, element, maxMopAmt)) n++;
        return n;
    }

    /// <summary>
    /// Checks whether a cell is on the horizontal edge of liquid (left or right neighbor is not same liquid).
    /// </summary>
    public static bool IsLiquidEdge(int cell, SimHashes element)
    {
        if (!Grid.IsValidCell(cell) || !IsLiquidOf(cell, element))
        {
            return false;
        }

        int left = Grid.CellLeft(cell);
        int right = Grid.CellRight(cell);
        return !IsLiquidOf(left, element) || !IsLiquidOf(right, element);
    }

    /// <summary>
    /// Measures the contiguous horizontal width of the same liquid containing <paramref name="cell"/>.
    /// </summary>
    public static int GetHorizontalLiquidWidth(int cell, SimHashes element)
    {
        if (!Grid.IsValidCell(cell) || !IsLiquidOf(cell, element))
        {
            return 0;
        }

        int width = 1;
        int cur = Grid.CellLeft(cell);
        while (IsLiquidOf(cur, element))
        {
            width++;
            cur = Grid.CellLeft(cur);
        }

        cur = Grid.CellRight(cell);
        while (IsLiquidOf(cur, element))
        {
            width++;
            cur = Grid.CellRight(cur);
        }

        return width;
    }

    private static IEnumerable<int> Neighbours(int cell)
    {
        yield return Grid.CellRight(cell);
        yield return Grid.CellLeft(cell);
        yield return Grid.CellAbove(cell);
        yield return Grid.CellBelow(cell);
    }

    private static bool IsHorizontalEnd(int cell, SimHashes element)
    {
        return IsOpen(Grid.CellLeft(cell), element) || IsOpen(Grid.CellRight(cell), element);
    }

    private static bool IsOpen(int cell, SimHashes element)
    {
        return Grid.IsValidCell(cell) && !Grid.Solid[cell] && !IsLiquidOf(cell, element);
    }

    private static bool HasSolidBelow(int cell)
    {
        int below = Grid.CellBelow(cell);
        return Grid.IsValidCell(below) && Grid.Solid[below];
    }

    private static bool IsLiquidOf(int cell, SimHashes element)
    {
        return Grid.IsValidCell(cell)
               && !Grid.Solid[cell]
               && Grid.Element[cell].IsLiquid
               && Grid.Element[cell].id == element
               && Grid.Mass[cell] > 0f;
    }
}