using UnityEngine;
using UnityEngine.UI;

public class BottleVoyage : MonoBehaviour
{
    public BottleFluid fluid;
    // Tier selection
    int tier = 2;
    public Text countText, msText;
    public Slider tierSlider;
    // Display Mode
    bool displayMode; // 0: Water, 1: Particle

    void Start()
    {
        ResetFluid();
    }

    void Update()
    {
        msText.text = ((int)(Time.smoothDeltaTime * 1000)).ToString() + "ms";
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
