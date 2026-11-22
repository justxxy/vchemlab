using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Vchem.UI
{
    /// <summary>
    /// M2 装置区交互组件。判定链（Vchem.Core）不感知这些组件：
    /// 拖拽/锚点只负责把学生的操作翻译成 DesignState 的节点、边与属性。
    /// </summary>

    /// <summary>左栏器材卡拖拽源：把器材拖进装置区画布。</summary>
    public sealed class PaletteDragSource : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public string ElementType;
        public PlanDesigner Owner;

        private GameObject _ghost;

        public void OnBeginDrag(PointerEventData e)
        {
            if (Owner == null) return;
            _ghost = new GameObject("DragGhost_" + ElementType);
            _ghost.transform.SetParent(Owner.CanvasRoot, false);
            RectTransform rt = _ghost.AddComponent<RectTransform>();
            // 锚点必须在 Canvas 中心：ScreenPointToLocalPointInRectangle 返回的是相对父级 pivot 的局部坐标
            rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(60, 70);
            // 幽灵＝半透明线稿：与画布上的器材同一份几何
            _ghost.AddComponent<CanvasRenderer>();
            StrokeGraphic g = _ghost.AddComponent<StrokeGraphic>();
            g.Scale = 0.5f;
            g.WidthBoost = 1.85f;   // 与左栏卡同档，幽灵和卡片线宽一致
            g.raycastTarget = false;
            g.color = new Color(1f, 1f, 1f, 0.82f);
            DeviceGlyph.Draw(g, ElementType, default(DeviceGlyph.State));
            Position(e);
        }

        public void OnDrag(PointerEventData e) { Position(e); }

        private void Position(PointerEventData e)
        {
            if (_ghost == null) return;
            Vector2 lp;
            // 画布是 ScreenSpaceCamera（localScale=0.01），相机为空时换算会整体错位，统一兜底到 Canvas 相机
            Camera cam = e.pressEventCamera != null ? e.pressEventCamera : Owner.UiCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Owner.CanvasRoot, e.position, cam, out lp);
            ((RectTransform)_ghost.transform).anchoredPosition = lp;
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (_ghost != null) { Destroy(_ghost); _ghost = null; }
            if (Owner != null && Owner.IsInDeviceViewport(e.position))
                Owner.DropElement(ElementType, e.position);
        }
    }

    /// <summary>装置区内已放置的器材：可在画布内拖动改位置；点一下＝点亮它的端口（删除态下＝删除）。
    /// 漏斗靠"下端与容器端口重合"接入：拖动中实时找吸附目标，松手落到端口上即装配。</summary>
    public sealed class DeviceItem : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler,
        IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public string NodeId;
        public PlanDesigner Owner;

        public void OnPointerEnter(PointerEventData e)
        {
            if (Owner != null) Owner.OnDeviceHover(NodeId, true);
        }

        public void OnPointerExit(PointerEventData e)
        {
            if (Owner != null) Owner.OnDeviceHover(NodeId, false);
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (Owner != null) Owner.OnDeviceClicked(NodeId);
        }

        public void OnBeginDrag(PointerEventData e)
        {
            transform.SetAsLastSibling();
            if (Owner != null) Owner.OnDeviceDragBegin(NodeId);
        }

        public void OnDrag(PointerEventData e)
        {
            if (Owner == null) return;
            Vector2 d = e.delta / Mathf.Max(0.01f, Owner.CanvasScaleFactor);
            RectTransform rt = (RectTransform)transform;
            rt.anchoredPosition = Owner.ClampDeviceFor(NodeId, rt.anchoredPosition + d);
            Owner.OnDeviceDragged(NodeId);
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (Owner != null) Owner.OnDeviceDragEnd(NodeId);
        }
    }

    /// <summary>左栏试剂卡拖拽源：把试剂拖到装置区里的器材上（锥形瓶 / 试管 / 长颈漏斗）。</summary>
    public sealed class ReagentDragSource : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public string ReagentId;
        public bool IsSolid;
        public string IconName;   // solid / liquid
        public PlanDesigner Owner;

        private GameObject _ghost;

        public void OnBeginDrag(PointerEventData e)
        {
            if (Owner == null) return;
            _ghost = new GameObject("DragGhost_" + ReagentId);
            _ghost.transform.SetParent(Owner.CanvasRoot, false);
            RectTransform rt = _ghost.AddComponent<RectTransform>();
            // 与 Canvas 同 pivot（中心）：否则 anchoredPosition 会与屏幕点错开半个画布
            rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(54, 63);
            rt.SetAsLastSibling();

            // 幽灵图：试剂瓶线稿 + 试剂名（让学生拖的时候知道拖的是什么）
            _ghost.AddComponent<CanvasRenderer>();
            StrokeGraphic g = _ghost.AddComponent<StrokeGraphic>();
            g.Scale = 0.45f;
            g.WidthBoost = 1.85f;   // 与左栏试剂卡同档
            g.raycastTarget = false;
            g.color = new Color(1f, 1f, 1f, 0.95f);
            DeviceGlyph.Draw(g, IconName, default(DeviceGlyph.State));

            GameObject lblGo = new GameObject("GhostLabel");
            lblGo.transform.SetParent(_ghost.transform, false);
            RectTransform lrt = lblGo.AddComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.zero; lrt.pivot = new Vector2(0.5f, 0f);
            lrt.anchoredPosition = new Vector2(0, -24); lrt.sizeDelta = new Vector2(150, 22);
            Text t = lblGo.AddComponent<Text>();
            t.font = Owner.UiFont;
            t.text = Owner.ReagentNameOf(ReagentId);
            t.fontSize = 14; t.fontStyle = FontStyle.Bold;
            t.color = new Color(0.85f, 0.42f, 0.12f);
            t.alignment = TextAnchor.MiddleCenter;
            t.font = Owner.UiFont;
            t.raycastTarget = false;

            Owner.BeginReagentDrag(ReagentId);
            Position(e);
        }

        public void OnDrag(PointerEventData e)
        {
            Position(e);
            if (Owner != null) Owner.HoverDropTarget(e.position);
        }

        private void Position(PointerEventData e)
        {
            if (_ghost == null) return;
            Vector2 lp;
            Camera cam = e.pressEventCamera != null ? e.pressEventCamera : Owner.UiCamera;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Owner.CanvasRoot, e.position, cam, out lp);
            ((RectTransform)_ghost.transform).anchoredPosition = lp;
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (_ghost != null) { Destroy(_ghost); _ghost = null; }
            if (Owner != null) Owner.DropReagent(ReagentId, IsSolid, e.position);
        }
    }

    /// <summary>器材上的语义端口：点击用于连线/液封判断，指针停在上面才显示它的名字。</summary>
    public sealed class AnchorPoint : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public string NodeId;
        public string Key;
        public PlanDesigner Owner;

        public void OnPointerEnter(PointerEventData e)
        {
            if (Owner != null) Owner.OnPortHover(NodeId, Key, true);
        }

        public void OnPointerExit(PointerEventData e)
        {
            if (Owner != null) Owner.OnPortHover(NodeId, Key, false);
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (Owner != null) Owner.OnAnchorClicked(NodeId, Key);
        }
    }
}
