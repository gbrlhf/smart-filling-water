using UnityEngine;

// Tombol E-stop: sekali tekan terkunci di bawah, tekan lagi (putar-lepas) kembali ke atas.
public class TombolDarurat : Interaktif
{
    public float kedalaman = 0.006f;

    Vector3 posAwal;

    protected override void Awake()
    {
        base.Awake();
        posAwal = transform.localPosition;
    }

    public override string Label => StatusMesin.I.Darurat ? "Lepas E-STOP (putar)" : "Tekan E-STOP";

    public override void Interaksi()
    {
        bool tekan = !StatusMesin.I.Darurat;
        transform.localPosition = posAwal + (tekan ? Vector3.down * kedalaman : Vector3.zero);
        StatusMesin.I.SetDarurat(tekan);
    }
}
