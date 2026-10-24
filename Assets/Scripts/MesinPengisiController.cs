using System.Collections.Generic;
using UnityEngine;

// Animasi siklus mesin pengisi botol (port dari anim_mesin_pengisi.py, 24 fps, frame 1..880).
// Semua status dihitung langsung dari nomor frame, jadi `frame` bisa diatur manual untuk debug.
public class MesinPengisiController : MonoBehaviour
{
    public float fps = 24f;
    public bool autoPlay = true;
    public float frame = 1f;

    const float FrameStart = 1f, FrameEnd = 880f;
    const int NormalCycles = 5;
    const float CycleFirst = 25f, CycleLen = 120f, MoveLen = 48f;
    const float SlotStepDeg = 36f, RingStartDeg = 36f, ShaftDegPerStep = -1080f;
    const float LiqStart = 0.01f, TankFull = 1f, TankDrop = 0.18f, TankEmpty = 0.01f;
    const float AlarmCycleStart = 625f, AlarmFlowStart = 677f, AlarmFlowEnd = 717f, AlarmBottleLevel = 0.55f;
    const float AlarmStart = 718f, AlarmEnd = 814f, AlarmBlink = 12f, BuzzPeriod = 3f, BuzzAmp = 0.05f;
    const float RefillStart = 815f, RefillEnd = 855f;
    const float FlowRamp = 4f, LedIntensity = 3f;
    const int NBottles = 9;

    static readonly Color Green = new Color(0.05f, 1f, 0.15f), Yellow = new Color(1f, 0.7f, 0f),
                          Red = new Color(1f, 0.03f, 0.02f), Off = new Color(0.03f, 0.03f, 0.03f);

    Transform ringPivot, shaft, lever, button, buzzer, tankLiquid, flowPivot, statusText;
    Transform[] liquids = new Transform[NBottles + 1];
    Material lampMat;
    TextMesh text;
    float buttonRestY;
    readonly Dictionary<string, Material> leds = new Dictionary<string, Material>();
    readonly Dictionary<string, Color> ledColors = new Dictionary<string, Color>
    {
        {"led_gpio18_step", Green}, {"led_gpio19_dir", Green}, {"led_gpio23_en", Green}, {"led_gpio25_pump", Green},
        {"led_gpio26_buzzer", Red}, {"led_gpio27_ir", Green}, {"led_gpio32_start", Green}, {"led_gpio33_limit", Green},
    };

    void Awake()
    {
        var all = new Dictionary<string, Transform>();
        foreach (var t in GetComponentsInChildren<Transform>(true)) all[t.name] = t;

        ringPivot = new GameObject("RingPivot").transform;
        ringPivot.SetParent(transform, false);
        ringPivot.position = all["ring_belt"].position;
        foreach (var n in new[] { "ring_belt", "center_hub", "spoke_1", "spoke_2", "spoke_3", "spoke_4", "ring_flag" })
            all[n].SetParent(ringPivot, true);

        var slots = new Transform[NBottles + 2];
        slots[1] = all["bottle"];
        for (int k = 2; k <= NBottles + 1; k++) slots[k] = all["bottle_slot_" + k];
        slots[NBottles + 1].gameObject.SetActive(false); // slot 10 kosong

        var liq0 = all["bottle_liquid"];
        liquids[1] = liq0;
        for (int k = 2; k <= NBottles; k++)
        {
            var c = Instantiate(liq0.gameObject, liq0.parent);
            c.name = "bottle_liquid_" + k;
            c.transform.position = new Vector3(slots[k].position.x, liq0.position.y, slots[k].position.z);
            liquids[k] = c.transform;
        }
        for (int k = 1; k <= NBottles; k++) { slots[k].SetParent(ringPivot, true); liquids[k].SetParent(ringPivot, true); }

        shaft = all["nema17_shaft"];
        lever = all["limit_lever"];
        button = all["start_button"];
        buttonRestY = button.localPosition.y;
        buzzer = all["buzzer"];
        tankLiquid = all["tank_liquid"];

        // aliran cairan dari ujung nozzle
        var nozzle = all["nozzle"];
        float tipY = nozzle.GetComponentInChildren<Renderer>().bounds.min.y;
        flowPivot = new GameObject("flow_stream").transform;
        flowPivot.SetParent(transform, false);
        flowPivot.position = new Vector3(nozzle.position.x, tipY, nozzle.position.z);
        var cyl = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        Destroy(cyl.GetComponent<Collider>());
        cyl.transform.SetParent(flowPivot, false);
        cyl.transform.localPosition = new Vector3(0f, -0.085f, 0f);
        cyl.transform.localScale = new Vector3(0.014f, 0.085f, 0.014f);
        cyl.GetComponent<Renderer>().sharedMaterial = tankLiquid.GetComponentInChildren<Renderer>().sharedMaterial;

        // lampu status di puncak tiang nozzle_arm
        var armR = all["nozzle_arm"].GetComponentInChildren<Renderer>();
        var lamp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        lamp.name = "status_lamp";
        Destroy(lamp.GetComponent<Collider>());
        lamp.transform.SetParent(transform, false);
        lamp.transform.localScale = Vector3.one * 0.03f;
        lamp.transform.position = new Vector3(0f, armR.bounds.max.y + 0.015f, armR.bounds.center.z);
        lampMat = NewMat();
        lamp.GetComponent<Renderer>().sharedMaterial = lampMat;

        // teks status
        statusText = new GameObject("txt_status").transform;
        statusText.SetParent(transform, false);
        statusText.position = new Vector3(0f, 0.65f, ringPivot.position.z);
        text = statusText.gameObject.AddComponent<TextMesh>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        statusText.GetComponent<MeshRenderer>().sharedMaterial = text.font.material;
        text.fontSize = 100;
        text.characterSize = 0.01f;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;

        foreach (var kv in ledColors)
        {
            var m = NewMat();
            all[kv.Key].GetComponentInChildren<Renderer>().sharedMaterial = m;
            leds[kv.Key] = m;
        }
        Evaluate(frame);
    }

    static Material NewMat()
    {
        var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        m.EnableKeyword("_EMISSION");
        return m;
    }

    static void SetEmissive(Material m, Color c, bool on)
    {
        m.SetColor("_BaseColor", on ? c * 0.3f : Off);
        m.SetColor("_EmissionColor", on ? c * LedIntensity : Color.black);
    }

    void Update()
    {
        if (autoPlay)
        {
            frame += Time.deltaTime * fps;
            if (frame > FrameEnd) frame = FrameStart;
        }
        Evaluate(frame);
    }

    void LateUpdate()
    {
        var cam = Camera.main;
        if (cam != null && statusText != null)
            statusText.rotation = Quaternion.LookRotation(statusText.position - cam.transform.position, Vector3.up);
    }

    static float Inv(float a, float b, float f) { return Mathf.Clamp01(Mathf.InverseLerp(a, b, f)); }
    static bool In(float f, float a, float b) { return f >= a && f <= b; }
    static float CycleStart(int n) { return n <= NormalCycles ? CycleFirst + (n - 1) * CycleLen : AlarmCycleStart; }

    void Led(string name, bool on) { SetEmissive(leds[name], ledColors[name], on); }

    void Evaluate(float f)
    {
        // posisi cincin & poros motor (Blender CCW dari atas = Unity yaw negatif)
        float theta = RingStartDeg, shaftDeg = 0f;
        for (int n = 1; n <= NormalCycles + 1; n++)
        {
            float s = CycleStart(n);
            if (f < s) break;
            float e = Mathf.SmoothStep(0f, 1f, Inv(s, s + MoveLen - 1, f));
            theta = RingStartDeg - SlotStepDeg * (n - 1) - SlotStepDeg * e;
            shaftDeg = ShaftDegPerStep * (n - 1 + e);
        }
        ringPivot.localRotation = Quaternion.Euler(0f, -theta, 0f);
        shaft.localRotation = Quaternion.Euler(0f, -shaftDeg, 0f);

        // tuas limit switch (hanya siklus 1)
        float lv = In(f, 69f, 75f) ? 25f * (1f - Mathf.Abs(f - 72f) / 3f) : 0f;
        lever.localRotation = Quaternion.Euler(0f, -lv, 0f);

        // tombol START
        float press = In(f, 12f, 20f) ? 1f - Mathf.Abs(f - 16f) / 4f : 0f;
        var bp = button.localPosition; bp.y = buttonRestY - 0.005f * press; button.localPosition = bp;

        // level cairan botol
        for (int k = 1; k <= NBottles; k++)
        {
            float lvl = LiqStart;
            if (k <= NormalCycles) lvl = Mathf.Lerp(LiqStart, 1f, Inv(CycleStart(k) + 52f, CycleStart(k) + 112f, f));
            else if (k == NormalCycles + 1) lvl = Mathf.Lerp(LiqStart, AlarmBottleLevel, Inv(AlarmFlowStart, AlarmFlowEnd, f));
            liquids[k].localScale = new Vector3(1f, lvl, 1f);
        }

        // level tangki
        float tank = TankFull;
        for (int n = 1; n <= NormalCycles; n++)
        {
            float t = Inv(CycleStart(n) + 52f, CycleStart(n) + 112f, f);
            if (t > 0f) tank = Mathf.Lerp(TankFull - TankDrop * (n - 1), TankFull - TankDrop * n, t);
        }
        float ta = Inv(AlarmFlowStart, AlarmFlowEnd, f);
        if (ta > 0f) tank = Mathf.Lerp(TankFull - TankDrop * NormalCycles, TankEmpty, ta);
        float tr = Inv(RefillStart, RefillEnd, f);
        if (tr > 0f) tank = Mathf.Lerp(TankEmpty, TankFull, tr);
        tankLiquid.localScale = new Vector3(1f, tank, 1f);

        // aliran & pompa, LED
        float flow = 0f; bool pump = false, moving = false, ir = false, step = false;
        for (int n = 1; n <= NormalCycles + 1; n++)
        {
            float s = CycleStart(n);
            if (In(f, s, s + MoveLen - 1)) { moving = true; step = ((int)((f - s) / 2f)) % 2 == 0; }
            if (n <= NormalCycles)
            {
                if (In(f, s + 40f, s + CycleLen - 1)) ir = true;
                if (In(f, s + 52f, s + 114f))
                {
                    pump = true;
                    flow = Mathf.Min(Inv(s + 52f, s + 52f + FlowRamp, f), Inv(s + 114f, s + 114f - FlowRamp, f));
                }
            }
            else
            {
                if (In(f, s + 40f, AlarmFlowEnd)) ir = true;
                if (In(f, AlarmFlowStart, AlarmFlowEnd)) { pump = true; flow = Inv(AlarmFlowStart, AlarmFlowStart + FlowRamp, f); }
            }
        }
        flowPivot.gameObject.SetActive(flow > 0f);
        flowPivot.localScale = new Vector3(1f, Mathf.Max(flow, 0.001f), 1f);

        bool inAlarm = In(f, AlarmStart, AlarmEnd);
        bool alarmOn = inAlarm && ((int)((f - AlarmStart) / AlarmBlink)) % 2 == 0;
        Led("led_gpio18_step", step);
        Led("led_gpio19_dir", moving);
        Led("led_gpio23_en", moving);
        Led("led_gpio25_pump", pump);
        Led("led_gpio26_buzzer", alarmOn);
        Led("led_gpio27_ir", ir);
        Led("led_gpio32_start", In(f, 12f, 20f));
        Led("led_gpio33_limit", In(f, 69f, 74f));

        float bs = 1f;
        if (alarmOn) bs = (((int)((f - AlarmStart) / BuzzPeriod)) % 2 == 0) ? 1f + BuzzAmp : 1f - BuzzAmp;
        buzzer.localScale = Vector3.one * bs;

        // teks & lampu status
        string label = "IDLE"; Color col = Green; bool lampOn = true; Color textCol = Color.white;
        if (inAlarm) { label = "ALARM: TANGKI KOSONG"; col = Red; lampOn = alarmOn; textCol = Red; }
        else if (f >= RefillStart || f < CycleFirst) { label = "IDLE"; col = Green; }
        else
        {
            for (int n = 1; n <= NormalCycles + 1; n++)
            {
                float s = CycleStart(n);
                if (In(f, s, s + MoveLen - 1)) { label = "MOVING"; col = Green; }
                else if (f >= s + MoveLen && f < (n <= NormalCycles ? s + CycleLen : AlarmStart)) { label = "FILLING"; col = Yellow; }
            }
        }
        text.text = label;
        text.color = textCol;
        SetEmissive(lampMat, col, lampOn);
    }
}
