using System.Collections.Generic;
using UnityEngine;

namespace TrueSmartMopping
{
    public class AutoMopOverlayHost : MonoBehaviour
    {
        private static AutoMopOverlayHost instance;
        private static Texture2D pixel;

        private bool dragging;
        private Vector2Int dragStart;
        private Vector2Int dragEnd;
        private bool dragAddsForbidden;

        private static readonly Color ForbiddenColor = new Color(0.4f, 0f, 0f, 0.45f);
        private static readonly Color SelectionColor = new Color(0.2f, 0.6f, 1f, 0.35f);

        public static void Show()
        {
            if (instance == null)
            {
                var go = new GameObject("TrueSmartMopping_OverlayHost");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<AutoMopOverlayHost>();
            }
            instance.enabled = true;
        }

        public static void Hide()
        {
            if (instance != null) instance.enabled = false;
        }

        private static Texture2D Pixel
        {
            get
            {
                if (pixel == null) pixel = Texture2D.whiteTexture;
                return pixel;
            }
        }

        private void Update()
        {
            if (Camera.main == null) return;
            Vector3 mouse = Input.mousePosition;
            if (Input.GetMouseButtonDown(0) && !IsOverUi())
            {
                var cell = CellUnderMouse(mouse);
                if (Grid.IsValidCell(cell))
                {
                    dragging = true;
                    dragStart = CellXY(cell);
                    dragEnd = dragStart;
                    dragAddsForbidden = !ModSettings.ForbiddenCells.Contains(cell);
                }
            }
            if (dragging)
            {
                var cell = CellUnderMouse(mouse);
                if (Grid.IsValidCell(cell)) dragEnd = CellXY(cell);
                if (Input.GetMouseButtonUp(0))
                {
                    dragging = false;
                    ApplyRect(dragStart, dragEnd, dragAddsForbidden);
                }
            }
        }

        private void OnGUI()
        {
            DrawForbiddenCells();
            if (dragging) DrawRect(dragStart, dragEnd, SelectionColor);
        }

        private void DrawForbiddenCells()
        {
            var cam = Camera.main;
            if (cam == null) return;
            foreach (int cell in ModSettings.ForbiddenCells)
            {
                DrawCellRect(CellXY(cell), ForbiddenColor);
            }
        }

        private void DrawRect(Vector2Int a, Vector2Int b, Color color)
        {
            int minX = Mathf.Min(a.x, b.x), maxX = Mathf.Max(a.x, b.x);
            int minY = Mathf.Min(a.y, b.y), maxY = Mathf.Max(a.y, b.y);
            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                    DrawCellRect(new Vector2Int(x, y), color);
        }

        private void DrawCellRect(Vector2Int xy, Color color)
        {
            int cell = Grid.XYToCell(xy.x, xy.y);
            if (!Grid.IsValidCell(cell)) return;
            Vector3 world = Grid.CellToPosCCC(cell, Grid.SceneLayer.Ground);
            Vector3 sBL = Camera.main.WorldToScreenPoint(world + new Vector3(-0.5f, -0.5f, 0f));
            Vector3 sTR = Camera.main.WorldToScreenPoint(world + new Vector3(0.5f, 0.5f, 0f));
            float x = sBL.x, w = sTR.x - sBL.x;
            float y = Screen.height - sTR.y, h = sTR.y - sBL.y;
            var prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(x, y, w, h), Pixel);
            GUI.color = prev;
        }

        private static bool IsOverUi()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }

        public static void Rebuild(OverlayModes.Mode mode)
        {
            if (instance != null) instance.StartCoroutine(instance.RebuildNextFrame(mode));
        }

        private System.Collections.IEnumerator RebuildNextFrame(OverlayModes.Mode mode)
        {
            yield return null;
            if (OverlayLegend.Instance != null) OverlayLegend.Instance.SetLegend(mode, true);
        }

        public static IEnumerable<SimHashes> DiscoveredLiquids()
        {
            foreach (SimHashes element in System.Enum.GetValues(typeof(SimHashes)))
            {
                var e = ElementLoader.FindElementByHash(element);
                if (e == null || e.IsGas || e.IsSolid || !e.IsLiquid) continue;
                if (!DiscoveredResources.Instance.IsDiscovered(element.CreateTag())) continue;
                yield return element;
            }
        }

        private static void ApplyRect(Vector2Int a, Vector2Int b, bool forbid)
        {
            int minX = Mathf.Min(a.x, b.x), maxX = Mathf.Max(a.x, b.x);
            int minY = Mathf.Min(a.y, b.y), maxY = Mathf.Max(a.y, b.y);
            for (int x = minX; x <= maxX; x++)
                for (int y = minY; y <= maxY; y++)
                {
                    int cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell)) continue;
                    if (forbid) ModSettings.ForbiddenCells.Add(cell);
                    else ModSettings.ForbiddenCells.Remove(cell);
                }
        }

        private static int CellUnderMouse(Vector3 mouse)
        {
            Vector3 world = Camera.main.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, -Camera.main.transform.position.z));
            return Grid.PosToCell(world);
        }

        private static Vector2Int CellXY(int cell)
        {
            Grid.CellToXY(cell, out int x, out int y);
            return new Vector2Int(x, y);
        }
    }
}
