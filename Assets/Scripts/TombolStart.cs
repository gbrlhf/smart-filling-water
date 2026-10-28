using System.Collections;
using UnityEngine;

// Tombol START di control box: tertekan sebentar lalu kembali.
public class TombolStart : Interaktif
{
    public float kedalaman = 0.004f;

    Vector3 posAwal;
    bool sedangDitekan;

    protected override void Awake()
    {
        base.Awake();
        posAwal = transform.localPosition;
    }

    public override string Label => "Tekan START";

    public override void Interaksi()
    {
        if (!sedangDitekan) StartCoroutine(Tekan());
        StatusMesin.I.TekanStart();
    }

    IEnumerator Tekan()
    {
        sedangDitekan = true;
        transform.localPosition = posAwal + Vector3.down * kedalaman;
        yield return new WaitForSeconds(0.15f);
        transform.localPosition = posAwal;
        sedangDitekan = false;
    }
}
