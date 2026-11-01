using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TrueSmartMopping
{
    public class AutoMopOverlayMode : OverlayModes.Mode
    {
        public static readonly HashedString ID = "TrueSmartMopping_AutoMop";

        public override HashedString ViewMode() => ID;

        public override string GetSoundName() => "Priorities";

        private const string Prefix = "STRINGS.UI.TOOLS.FILTERLAYERS.";
        private const string KEnabled = "TSM_ENABLED";
        private const string KClear = "TSM_CLEAR";
        private const string KLiquid = "TSM_LIQ_";

        private static void SetText(string key, string name, string tooltip)
        {
            Strings.Add(Prefix + key + ".NAME", name);
            Strings.Add(Prefix + key + ".TOOLTIP", tooltip);
        }

        private static ToolParameterMenu.ToggleData Toggle(string key, bool on)
        {
            return new ToolParameterMenu.ToggleData(key, on ? ToolParameterMenu.ToggleState.On : ToolParameterMenu.ToggleState.Off, true);
        }

        public static string[] Description => new[]
        {
            (string)STRINGS.OVERLAY.DESC.LINE_0,
            (string)STRINGS.OVERLAY.DESC.LINE_1,
            (string)STRINGS.OVERLAY.DESC.LINE_2,
            (string)STRINGS.OVERLAY.DESC.LINE_3
        };

        public override System.Collections.Generic.List<LegendEntry> GetCustomLegendData()
        {
            var list = new System.Collections.Generic.List<LegendEntry>();
            foreach (var line in Description) list.Add(Header(line));
            list.Add(Header(STRINGS.OVERLAY.SECTION.BASIC));
            list.Add(Header(STRINGS.OVERLAY.SECTION.PRIORITY));
            list.Add(Header(STRINGS.OVERLAY.SECTION.BLOCKED));
            list.Add(Header(STRINGS.OVERLAY.SECTION.TARGET));
            return list;
        }

        private static LegendEntry Header(string text)
        {
            return new LegendEntry(text, null, Color.white, null, null, displaySprite: false);
        }

        private void BuildFilters()
        {
            var list = new System.Collections.Generic.List<ToolParameterMenu.ToggleData>();

            SetText(KEnabled, STRINGS.OVERLAY.ENABLED.NAME, STRINGS.OVERLAY.ENABLED.TOOLTIP);
            list.Add(Toggle(KEnabled, ModSettings.AutoMopEnabled));

            foreach (var liquid in AutoMopOverlayHost.DiscoveredLiquids())
            {
                var e = ElementLoader.FindElementByHash(liquid);
                SetText(KLiquid + (int)liquid, e.name, STRINGS.OVERLAY.LIQUID.TOOLTIP);
                list.Add(Toggle(KLiquid + (int)liquid, !ModSettings.ExcludedLiquids.Contains(liquid)));
            }

            legendFilters = list.ToArray();
        }
        public override void Enable()
        {
            BuildFilters();
            AutoMopOverlayHost.Show();
        }

        public override void Disable()
        {
            AutoMopOverlayHost.Hide();
        }

        public override void OnFiltersChanged()
        {
            foreach (var f in legendFilters)
            {
                if (f.name == KEnabled) ModSettings.AutoMopEnabled = f.IsOn;
                else if (f.name.StartsWith(KLiquid))
                {
                    var liquid = (SimHashes)int.Parse(f.name.Substring(KLiquid.Length));
                    if (f.IsOn) ModSettings.ExcludedLiquids.Remove(liquid);
                    else ModSettings.ExcludedLiquids.Add(liquid);
                }
            }
        }
    }

    public static class AutoMopOverlayPatch
    {
        public static void Apply(HarmonyLib.Harmony harmony)
        {
            var target = AccessTools.Method(typeof(OverlayScreen), "RegisterModes");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(AutoMopOverlayPatch), nameof(RegisterPostfix)));

            var legendSpawn = AccessTools.Method(typeof(OverlayLegend), "OnSpawn");
            harmony.Patch(legendSpawn, postfix: new HarmonyMethod(typeof(AutoMopOverlayPatch), nameof(LegendSpawnPostfix)));

            var populate = AccessTools.Method(typeof(OverlayLegend), "PopulateGeneratedLegend");
            harmony.Patch(populate, postfix: new HarmonyMethod(typeof(AutoMopOverlayPatch), nameof(PopulatePostfix)));

            var initToggles = AccessTools.Method(typeof(OverlayMenu), "InitializeToggles");
            harmony.Patch(initToggles, postfix: new HarmonyMethod(typeof(AutoMopOverlayPatch), nameof(InitTogglesPostfix)));
        }

        private static GameObject priorityRow;

        private static void PopulatePostfix(OverlayLegend __instance)
        {
            var trav = Traverse.Create(__instance);
            var mode = trav.Field("currentMode").GetValue<OverlayModes.Mode>();
            if (mode == null || mode.ViewMode() != AutoMopOverlayMode.ID) return;
            var menu = trav.Field("filterMenu").GetValue<ToolParameterMenu>();
            if (menu == null) return;

            var container = menu.widgetContainer.transform;
            var units = trav.Field("activeUnitObjs").GetValue<System.Collections.Generic.List<GameObject>>();
            if (units.Count < AutoMopOverlayMode.Description.Length + 4) return;

            if (priorityRow != null) UnityEngine.Object.DestroyImmediate(priorityRow);
            if (clearRow != null) UnityEngine.Object.DestroyImmediate(clearRow);
            priorityRow = BuildPriorityRow(container);
            clearRow = BuildClearRow(container);

            var real = new System.Collections.Generic.HashSet<GameObject>();
            var dict = (System.Collections.IDictionary)typeof(ToolParameterMenu).GetField("widgets", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(menu);
            foreach (var w in dict.Values)
                real.Add((GameObject)w.GetType().GetField("gameObject").GetValue(w));
            var toggles = new System.Collections.Generic.List<Transform>();
            foreach (Transform c in container)
            {
                if (!real.Contains(c.gameObject) || c.gameObject == priorityRow || c.gameObject == clearRow || units.Contains(c.gameObject)) continue;
                toggles.Add(c);
            }

            int d = AutoMopOverlayMode.Description.Length;
            var order = new System.Collections.Generic.List<Transform>();
            for (int i = 0; i < d; i++) order.Add(units[i].transform);
            order.Add(units[d].transform);
            if (toggles.Count > 0) order.Add(toggles[0]);
            order.Add(units[d + 1].transform);
            if (priorityRow != null) order.Add(priorityRow.transform);
            order.Add(units[d + 2].transform);
            if (clearRow != null) order.Add(clearRow.transform);
            order.Add(units[d + 3].transform);
            for (int i = 1; i < toggles.Count; i++) order.Add(toggles[i]);

            foreach (var t in order)
            {
                if (t.parent != container) t.SetParent(container, false);
                t.gameObject.SetActive(true);
                var txt = t.GetComponentInChildren<LocText>(true);
                if (txt != null) txt.enabled = true;
            }
            for (int i = 0; i < order.Count; i++) order[i].SetSiblingIndex(i);
            foreach (Transform c in container)
                if (!order.Contains(c)) c.gameObject.SetActive(false);
        }

        private static GameObject clearRow;

        private static GameObject BuildClearRow(Transform parent)
        {
            var screen = ToolMenu.Instance != null ? ToolMenu.Instance.PriorityScreen : null;
            if (screen == null) return null;
            var src = Traverse.Create(screen).Field("buttons_basic").GetValue<System.Collections.Generic.List<PriorityButton>>();
            if (src == null || src.Count == 0) return null;
            var b = Util.KInstantiateUI<PriorityButton>(src[0].gameObject, parent.gameObject, true);
            b.gameObject.name = "TSM_ClearButton";
            b.text.text = string.Format(STRINGS.OVERLAY.CLEAR.FORMAT, ModSettings.ForbiddenCells.Count);
            b.text.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
            b.tooltip.SetSimpleTooltip(STRINGS.OVERLAY.CLEAR.TOOLTIP);
            var le = b.gameObject.GetComponent<UnityEngine.UI.LayoutElement>() ?? b.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            le.minHeight = 28;
            le.preferredHeight = 28;
            le.flexibleWidth = 1;
            b.onClick = p =>
            {
                ModSettings.ForbiddenCells.Clear();
                b.text.text = string.Format(STRINGS.OVERLAY.CLEAR.FORMAT, 0);
                b.toggle.isOn = false;
            };
            b.toggle.isOn = false;
            return b.gameObject;
        }
        private static GameObject BuildPriorityRow(Transform parent)
        {
            var screen = ToolMenu.Instance != null ? ToolMenu.Instance.PriorityScreen : null;
            if (screen == null) return null;
            var src = Traverse.Create(screen).Field("buttons_basic").GetValue<System.Collections.Generic.List<PriorityButton>>();
            if (src == null || src.Count == 0) return null;

            var row = new GameObject("TSM_PriorityRow", typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var layout = row.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childControlWidth = false;
            layout.childControlHeight = false;
            layout.spacing = 2;
            layout.padding = new RectOffset(8, 8, 2, 2);
            row.AddComponent<UnityEngine.UI.LayoutElement>().minHeight = 32;

            var buttons = new System.Collections.Generic.List<PriorityButton>();
            for (int n = 1; n <= 9; n++)
            {
                int num = n;
                var b = Util.KInstantiateUI<PriorityButton>(src[0].gameObject, row, true);
                b.playSelectionSound = true;
                b.text.text = num.ToString();
                b.priority = new PrioritySetting(PriorityScreen.PriorityClass.basic, num);
                b.tooltip.SetSimpleTooltip(string.Format(global::STRINGS.UI.PRIORITYSCREEN.BASIC, num));
                b.onClick = p =>
                {
                    ModSettings.AutoMopPriority = p.priority_value;
                    foreach (var o in buttons) o.toggle.isOn = o.priority.priority_value == p.priority_value;
                };
                buttons.Add(b);
            }
            foreach (var o in buttons) o.toggle.isOn = o.priority.priority_value == ModSettings.AutoMopPriority;
            return row;
        }

        private static void LegendSpawnPostfix(OverlayLegend __instance)
        {
            var list = (System.Collections.Generic.List<OverlayLegend.OverlayInfo>)AccessTools.Field(typeof(OverlayLegend), "overlayInfoList").GetValue(__instance);
            if (list.Exists(i => i.mode == AutoMopOverlayMode.ID)) return;
            list.Add(new OverlayLegend.OverlayInfo
            {
                name = STRINGS.OVERLAY.NAME,
                mode = AutoMopOverlayMode.ID,
                infoUnits = new System.Collections.Generic.List<OverlayLegend.OverlayInfoUnit>(),
                isProgrammaticallyPopulated = true
            });
        }

        private static void RegisterPostfix(OverlayScreen __instance)
        {
            var registerMode = AccessTools.Method(typeof(OverlayScreen), "RegisterMode");
            registerMode.Invoke(__instance, new object[] { new AutoMopOverlayMode() });
        }

        private static void InitTogglesPostfix(OverlayMenu __instance)
        {
            var infoType = typeof(OverlayMenu).GetNestedType("OverlayToggleInfo", BindingFlags.NonPublic);
            var list = (IList)AccessTools.Field(typeof(OverlayMenu), "overlayToggleInfos").GetValue(__instance);
            var ctor = AccessTools.Constructor(infoType, new[]
            {
                typeof(string), typeof(string), typeof(HashedString), typeof(string),
                typeof(Action), typeof(string), typeof(string)
            });
            var info = ctor.Invoke(new object[]
            {
                (string)STRINGS.OVERLAY.NAME, "overlay_liquidvent", AutoMopOverlayMode.ID, "", Action.NumActions,
                (string)STRINGS.OVERLAY.TOOLTIP,
                (string)STRINGS.OVERLAY.NAME
            });
            list.Add(info);
        }
    }
}
