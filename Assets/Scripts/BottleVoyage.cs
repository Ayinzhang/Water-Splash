using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[DefaultExecutionOrder(-100)]
public class BottleVoyage : MonoBehaviour
{
    public BottleFluid fluid;
    public Text countText, msText, modeText;
    public Slider tierSlider;
    [System.NonSerialized] public bool isDrag;
    static readonly List<RaycastResult> uiHits = new();
    CapsuleCollider hull;
    Quaternion heading;
    Vector2 lastPointer;
    float waterline;
    int tier = 2;

    public static bool OnUI(Vector2 pointer)
    {
        EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = pointer }, uiHits);
        return uiHits.Count > 0;
    }

    void Start()
    {
        Application.targetFrameRate = 114514;
        hull = GetComponent<CapsuleCollider>();
        heading = Quaternion.Euler(0, transform.eulerAngles.y, 0);
        waterline = transform.position.y - BottleBoundary.waterLevel;
        modeText.text = "Water  >  Particles";
        ResetFluid();
    }

    void Update()
    {
        msText.text = (int)(Time.smoothDeltaTime * 1000) + "ms";
        // Drag on the boat's plane, otherwise drift with the flow and float on the surface
        Vector2 pointer = Input.mousePosition;
        if (Input.GetMouseButtonDown(0)) { Physics.SyncTransforms(); isDrag = !OnUI(pointer) && hull.Raycast(Camera.main.ScreenPointToRay(pointer), out _, 100); }
        if (Input.GetMouseButtonUp(0)) isDrag = false;
        Vector4 water = fluid.waterSample;
        if (isDrag) Move(PlaneDelta(lastPointer, pointer));
        else if (water.w > 0) Move(new Vector3(water.y * Time.deltaTime, (water.x + waterline - transform.position.y) * (1 - Mathf.Exp(-5 * Time.deltaTime)), water.z * Time.deltaTime));
        lastPointer = pointer;
        // Tilt with the water normal
        Vector3 normal = Vector3.RotateTowards(Vector3.up, fluid.waterNormal, 18 * Mathf.Deg2Rad, 0);
        Quaternion last = transform.rotation;
        transform.rotation = Quaternion.Slerp(last, Quaternion.FromToRotation(Vector3.up, normal) * heading, 1 - Mathf.Exp(-4 * Time.deltaTime));
        if (!Inside(Vector3.zero)) transform.rotation = last;
        Capsule(out var a, out var b, out float r);
        fluid.SetBoat(a, b, r, transform.position, heading * Vector3.forward * ((Vector3.Distance(a, b) * .5f + r) * .7f), heading * Vector3.right * (r * 1.5f));
    }

    void Capsule(out Vector3 a, out Vector3 b, out float r) // uniform scale, Z axis capsule
    {
        float scale = transform.lossyScale.x;
        r = hull.radius * scale;
        Vector3 center = transform.TransformPoint(hull.center), half = transform.forward * (hull.height * scale * .5f - r);
        a = center - half; b = center + half;
    }

    bool Inside(Vector3 offset)
    {
        Capsule(out var a, out var b, out float r);
        for (int i = 0; i <= 4; i++) if (!fluid.boundary.Contains(Vector3.Lerp(a, b, i / 4f) + offset, r + .02f)) return false;
        return true;
    }

    void Move(Vector3 delta) // drop each axis of the motion that would push the hull out of the bottle
    {
        Vector3 accepted = Vector3.zero;
        foreach (int i in new[] { 0, 2, 1 })
        {
            Vector3 next = accepted; next[i] += delta[i];
            if (Inside(next)) accepted = next;
        }
        transform.position += accepted;
    }

    Vector3 PlaneDelta(Vector2 from, Vector2 to)
    {
        var plane = new Plane(Vector3.up, transform.position);
        Ray a = Camera.main.ScreenPointToRay(from), b = Camera.main.ScreenPointToRay(to);
        return plane.Raycast(a, out float da) && plane.Raycast(b, out float db) ? b.GetPoint(db) - a.GetPoint(da) : Vector3.zero;
    }

    void ResetFluid()
    {
        Capsule(out var a, out var b, out float r);
        fluid.SetBoat(a, b, r, transform.position, Vector3.zero, Vector3.zero);
        fluid.Init(16384 << tier);
    }

    public void OnTierSliderChanged() => countText.text = (16 << (int)tierSlider.value) + "K";

    public void OnTierSliderPointerUp()
    {
        if (tier == (int)tierSlider.value) return;
        tier = (int)tierSlider.value; ResetFluid();
    }

    public void SwitchDisplayMode()
    {
        fluid.renderSurface = !fluid.renderSurface;
        modeText.text = fluid.renderSurface ? "Water  >  Particles" : "Particles  >  Water";
    }
}
