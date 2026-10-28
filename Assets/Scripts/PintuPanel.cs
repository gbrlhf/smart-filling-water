using UnityEngine;

// Pintu panel listrik: berputar pada engsel (pivot objek pintu).
public class PintuPanel : Interaktif
{
    public float sudutBuka = 105f;
    public float kecepatan = 6f;

    Quaternion rotTutup;
    float sudut, sudutTarget;

    protected override void Awake()
    {
        klikTembus = false;   // pintu menghalangi klik ke MCB di dalamnya
        ukuranKlikMin = 0.02f;
        base.Awake();
        rotTutup = transform.localRotation;
    }

    public bool Terbuka => sudutTarget > 0f;

    public override string Label => Terbuka ? "Tutup pintu panel" : "Buka pintu panel";

    public override void Interaksi()
    {
        sudutTarget = Terbuka ? 0f : sudutBuka;
    }

    void Update()
    {
        if (Mathf.Approximately(sudut, sudutTarget)) return;
        sudut = Mathf.MoveTowards(sudut, sudutTarget, Mathf.Max(20f, Mathf.Abs(sudutTarget - sudut) * kecepatan) * Time.deltaTime);
        transform.localRotation = rotTutup * Quaternion.AngleAxis(sudut, Vector3.up);
    }
}
