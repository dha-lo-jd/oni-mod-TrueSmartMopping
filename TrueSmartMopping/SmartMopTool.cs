using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using STRINGS;
using UnityEngine;

namespace TrueSmartMopping
{
    /// <summary>
    /// Drag tool that places unassigned smart mop work orders. The start cell is searched with the smart mop
    /// algorithm and the order is handed out by the global chore provider like a vanilla mop order.
    /// </summary>
    public class SmartMopTool : DragTool
    {
        public const string ToolName = "SmartMopTool";

        protected override void OnPrefabInit()
        {
            base.OnPrefabInit();
            interceptNumberKeysForPriority = true;
        }

        protected override void OnActivateTool()
        {
            base.OnActivateTool();
            ToolMenu.Instance.PriorityScreen.Show();
        }

        protected override void OnDeactivateTool(InterfaceTool new_tool)
        {
            base.OnDeactivateTool(new_tool);
            ToolMenu.Instance.PriorityScreen.Show(false);
        }

        // Click only: the algorithm picks a single best cell, so drag selection is meaningless.
        public override void OnLeftClickDown(Vector3 cursor_pos)
        {
            PlaceOrder(Grid.PosToCell(cursor_pos));
        }

        protected override void OnDragTool(int cell, int distFromOrigin)
        {
        }

        private void PlaceOrder(int cell)
        {
            CreateOrder(cell, ToolMenu.Instance.PriorityScreen.GetLastSelectedPriority());
        }

        public static bool CreateOrder(int cell, PrioritySetting priority, SmartMopChore.Saved saved = null)
        {
            return CreateOrderChore(cell, priority, saved) != null;
        }

        public static SmartMopChore CreateOrderChore(int cell, PrioritySetting priority, SmartMopChore.Saved saved = null)
        {
            var placer = Assets.GetPrefab(new Tag("MopPlacer"));
            int stand;
            if (saved != null)
            {
                stand = saved.targetCell;
                if (!Grid.IsValidCell(stand))
                {
                    return null;
                }
            }
            else
            {
                if (!Grid.IsValidCell(cell) || Grid.Solid[cell] || !Grid.Element[cell].IsLiquid)
                {
                    return null;
                }
                var element = Grid.Element[cell].id;
                stand = SmartMopTargets.FindStart(cell, element, MopTool.maxMopAmt, IsClaimed,
                    c => IsClaimed(c) || IsClaimed(Grid.CellLeft(c)) || IsClaimed(Grid.CellRight(c)));
                if (stand == Grid.InvalidCell)
                {
                    return null;
                }
            }
            Debug.Log("[TrueSmartMopping] PlaceOrder click=" + cell + " stand=" + stand + " moppablesBefore=" + UnityEngine.Object.FindObjectsByType<Moppable>(FindObjectsSortMode.None).Length);
            var go = Util.KInstantiate(placer);
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Moppable>());
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Cancellable>());
            UnityEngine.Object.DestroyImmediate(go.GetComponent<SaveLoadRoot>());
            var marker = go.AddComponent<SmartMopMarker>();
            var prioritizable = go.AddOrGet<Prioritizable>();
            Prioritizable.AddRef(go);
            var position = Grid.CellToPosCBC(stand, Grid.SceneLayer.FXFront);
            position.z -= 0.15f;
            go.transform.SetPosition(position);
            go.SetActive(true);

            var chore = new SmartMopChore(marker, GlobalChoreProvider.Instance, cell, priority.priority_class,
                priority.priority_value, manual: true, saved: saved);
            marker.owner = chore.smi;
            if (chore.smi.targetCell == Grid.InvalidCell)
            {
                chore.Cancel("No stand cell");
                if (go != null)
                {
                    go.DeleteObject();
                }
                return null;
            }
            prioritizable.SetMasterPriority(priority);
            return chore;
        }

        internal static bool IsClaimed(int cell)
        {
            return SmartMopChore.StatesInstance.Claims.ContainsKey(cell) || (Grid.IsValidCell(cell) && Grid.Objects[cell, 8] != null);
        }
    }

    public static class SmartMopToolPatch
    {
        public static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(PlayerController), "OnPrefabInit"),
                postfix: new HarmonyMethod(typeof(SmartMopToolPatch), nameof(RegisterToolPostfix)));
            harmony.Patch(AccessTools.Method(typeof(GameInputMapping), "CompareActionKeyCodes"),
                prefix: new HarmonyMethod(typeof(SmartMopToolPatch), nameof(CompareKeyCodesPrefix)));
            harmony.Patch(AccessTools.Method(typeof(KButtonEvent), nameof(KButtonEvent.IsAction)),
                prefix: new HarmonyMethod(typeof(SmartMopToolPatch), nameof(IsActionPrefix)));
            harmony.Patch(AccessTools.Method(typeof(ToolMenu), "CreateBasicTools"),
                postfix: new HarmonyMethod(typeof(SmartMopToolPatch), nameof(AddButtonPostfix)));
        }

        // The smart mop button has no hotkey; vanilla asserts when comparing an unbound action.
        private static bool CompareKeyCodesPrefix(Action a, Action b, ref bool __result)
        {
            if (a == Action.NumActions || b == Action.NumActions)
            {
                __result = false;
                return false;
            }
            return true;
        }

        // Same for IsAction: U60 indexes an array by the action, which overflows for an unbound action
        // (seen in ToolMenu.OnKeyDown when debug mode is on).
        private static bool IsActionPrefix(Action action, ref bool __result)
        {
            if (action == Action.NumActions)
            {
                __result = false;
                return false;
            }
            return true;
        }

        private static void RegisterToolPostfix(PlayerController __instance)
        {
            var mop = __instance.tools.OfType<MopTool>().FirstOrDefault();
            if (mop == null)
            {
                return;
            }
            var go = new GameObject(SmartMopTool.ToolName);
            go.SetActive(false);
            go.transform.SetParent(__instance.gameObject.transform, false);
            var tool = go.AddComponent<SmartMopTool>();
            CopyFields(typeof(InterfaceTool), mop, tool);
            CopyFields(typeof(DragTool), mop, tool);
            go.SetActive(true);
            go.SetActive(false);
            __instance.tools = __instance.tools.Concat(new InterfaceTool[] { tool }).ToArray();
        }

        // Copies the prefab-configured (public / serialized) fields so the new tool looks and behaves like the mop tool.
        private static void CopyFields(Type type, object from, object to)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var field in type.GetFields(flags))
            {
                if (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null)
                {
                    field.SetValue(to, field.GetValue(from));
                }
            }
        }

        private static void AddButtonPostfix(ToolMenu __instance)
        {
            var collection = ToolMenu.CreateToolCollection(
                TrueSmartMopping.STRINGS.TOOLS.SMARTMOP.NAME, "icon_action_mop", Action.NumActions,
                SmartMopTool.ToolName,
                TrueSmartMopping.STRINGS.TOOLS.SMARTMOP.TOOLTIP,
                largeIcon: false);
            int index = __instance.basicTools.FindIndex(c => c.tools.Count > 0 && c.tools[0].toolName == "MopTool");
            __instance.basicTools.Insert(index >= 0 ? index + 1 : __instance.basicTools.Count, collection);
        }
    }
}
