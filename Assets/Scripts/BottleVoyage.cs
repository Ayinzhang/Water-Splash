using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;

[DefaultExecutionOrder(-100)]
public class BottleVoyage : MonoBehaviour
{
    public BottleFluid fluid;
    public CapsuleCollider boatCollider;
    public Camera waterCamera;
    Vector3 boatTarget, dragOffset;
    bool dragging;
    readonly List<RaycastResult> uiHits = new List<RaycastResult>();
    // Tier selection
    int tier = 2;
    public Text countText, msText;
    public Slider tierSlider;
    // Display Mode
    bool displayMode; // 0: Water, 1: Particle

    void Start()
    {
        boatTarget = boatCollider.transform.position;
        ResetFluid();
    }

    void Update()
    {
        msText.text = ((int)(Time.smoothDeltaTime * 1000)).ToString() + "ms";
        UpdateBoat();
    }

    void UpdateBoat()
    {
        Transform boat = boatCollider.transform;
        Vector2 pointer = Input.mousePosition;
        uiHits.Clear();
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = pointer }, uiHits);
        bool onUI = uiHits.Count > 0;
        Ray ray = waterCamera.ScreenPointToRay(pointer);
        var plane = new Plane(Vector3.up, boat.position);
        if (Input.GetMouseButtonDown(0) && !onUI)
        {
            Physics.SyncTransforms();
            if (boatCollider.Raycast(ray, out RaycastHit hit, 100) && plane.Raycast(ray, out float distance))
            {
                dragOffset = boat.position - ray.GetPoint(distance);
                dragging = true;
            }
        }
        if (Input.GetMouseButtonUp(0) || onUI) dragging = false;
        if (dragging && plane.Raycast(ray, out float depth))
        {
            Vector3 target = ray.GetPoint(depth) + dragOffset;
            // Constrain the complete hull, not just its pivot.
            Vector3 delta = target - boat.position;
            fluid.GetBoatCapsule(out Vector3 a, out Vector3 b, out float radius);
            float lo = 0, hi = 1;
            for (int i = 0; i < 12; i++)
            {
                float t = (lo + hi) * .5f;
                bool inside = true;
                for (int j = 0; j <= 4; j++)
                    inside &= fluid.boundary.Contains(Vector3.Lerp(a, b, j / 4f) + delta * t, radius + .02f);
                if (inside) lo = t; else hi = t;
            }
            boatTarget = boat.position + delta * lo;
        }
        boat.position = boatTarget;
    }

    void ResetFluid()
    {
        fluid.Init(16384 << tier);
    }

    public void OnTierSliderChanged() => countText.text = (Mathf.Pow(2, tierSlider.value + 4)).ToString() + "K";

    public void OnTierSliderPointerUp()
    {
        if (tier == tierSlider.value) return;
        tier = (int)tierSlider.value; ResetFluid();
    }

    public void SwitchDisplayMode()
    {
        displayMode = !displayMode;
        // to do
    }
}
