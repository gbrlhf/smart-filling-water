using UnityEngine;

// MCB di DIN rail: klik untuk ON/OFF. Tuas berputar, indikator hijau/merah bergantian.
// Dipasang pada objek mcb_<nama>_body; tuas & indikator adalah anaknya.
public class SaklarMCB : Interaktif
{
    public JenisMcb jenis;
    public float sudutOff = -60f;

    Transform tuas, indikatorOn, indikatorOff;
    Quaternion rotTuasOn;

    protected override void Awake()
    {
        klikTembus = false;
        base.Awake();
        var awalan = name.Replace("_body", "");
        tuas = transform.Find(awalan + "_lever");
        indikatorOn = transform.Find(awalan + "_indicator_on");
        indikatorOff = transform.Find(awalan + "_indicator_off");
        if (tuas) rotTuasOn = tuas.localRotation;
    }

    void Start() => Terapkan(StatusMesin.I.AmbilMcb(jenis));

    public override string Label => "MCB " + jenis + ": " + (StatusMesin.I.AmbilMcb(jenis) ? "matikan (OFF)" : "nyalakan (ON)");

    public override void Interaksi()
    {
        bool on = !StatusMesin.I.AmbilMcb(jenis);
        StatusMesin.I.SetMcb(jenis, on);
        Terapkan(on);
    }

    void Terapkan(bool on)
    {
        if (tuas) tuas.localRotation = on ? rotTuasOn : rotTuasOn * Quaternion.AngleAxis(sudutOff, Vector3.forward);
        if (indikatorOn) indikatorOn.gameObject.SetActive(on);
        if (indikatorOff) indikatorOff.gameObject.SetActive(!on);
    }
}
