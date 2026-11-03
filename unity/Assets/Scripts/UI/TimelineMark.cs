using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// One clickable span under the timeline (TwinUI.SetTimelineMarks): highlights on hover, runs onClick on click.
// Takes the pointer-down itself so the slider underneath doesn't start a drag.
public class TimelineMark : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerClickHandler {
    public Color color;
    public Action onClick, onEnter, onExit;
    Image img; RectTransform rt;

    void Awake() { img = GetComponent<Image>(); rt = (RectTransform)transform; }

    public void OnPointerEnter(PointerEventData e) {
        img.color = Color.Lerp(color, Color.white, 0.45f);
        rt.anchorMax = new Vector2(rt.anchorMax.x, 0.45f);  // grow upward while hovered
        onEnter?.Invoke();
    }

    public void OnPointerExit(PointerEventData e) {
        img.color = color;
        rt.anchorMax = new Vector2(rt.anchorMax.x, 0.22f);
        onExit?.Invoke();
    }

    public void OnPointerDown(PointerEventData e) { }
    public void OnPointerClick(PointerEventData e) => onClick?.Invoke();
}
