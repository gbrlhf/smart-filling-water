using UnityEngine;

public enum ModeMesin { Mati, Siap, Berjalan, Darurat }
public enum JenisMcb { Utama, Mesin, Kontrol, Lampu }

// Status listrik dan mesin. Dipakai oleh tombol, E-stop, MCB, dan nanti simulasi mesin.
public class StatusMesin : MonoBehaviour
{
    public static StatusMesin I { get; private set; }

    [Header("MCB (true = ON)")]
    public bool mcbUtama = true;
    public bool mcbMesin = true;
    public bool mcbKontrol = true;
    public bool mcbLampu = true;

    [Header("Visual")]
    public Light[] lampuPlafon;
    public Renderer[] kapLampu;
    public Renderer lampuPower;
    public Renderer lampuFault;

    public ModeMesin Mode { get; private set; } = ModeMesin.Mati;
    public bool Darurat { get; private set; }
    public event System.Action<ModeMesin> ModeBerubah;

    public string Pesan { get; private set; }
    public float WaktuPesan { get; private set; } = -10f;

    public bool AdaListrikKontrol => mcbUtama && mcbKontrol;
    public bool AdaListrikMesin => mcbUtama && mcbMesin;

    bool jalan;
    MaterialPropertyBlock mpb;
    Color[] emisiKap;

    void Awake()
    {
        I = this;
        mpb = new MaterialPropertyBlock();
        emisiKap = new Color[kapLampu.Length];
        for (int i = 0; i < kapLampu.Length; i++)
            emisiKap[i] = kapLampu[i].sharedMaterial.GetColor("_EmissionColor");
        Hitung();
        TerapkanVisual();
    }

    public void TekanStart()
    {
        if (!mcbUtama) { Kabar("Tidak ada listrik: MCB utama OFF"); return; }
        if (!mcbKontrol) { Kabar("Control box mati: MCB kontrol OFF"); return; }
        if (Darurat) { Kabar("E-stop masih ditekan, lepas dulu"); return; }
        if (!mcbMesin) { Kabar("Motor & pompa tidak dapat listrik: MCB mesin OFF"); return; }
        if (jalan) { Kabar("Mesin sudah berjalan"); return; }
        jalan = true;
        Hitung();
        Kabar("START: mesin berjalan");
    }

    public void SetDarurat(bool tekan)
    {
        Darurat = tekan;
        if (tekan) jalan = false;
        Hitung();
        TerapkanVisual();
        Kabar(tekan ? "E-STOP ditekan: mesin berhenti" : "E-stop dilepas, tekan START untuk jalan lagi");
    }

    public bool AmbilMcb(JenisMcb j) => j switch
    {
        JenisMcb.Utama => mcbUtama,
        JenisMcb.Mesin => mcbMesin,
        JenisMcb.Kontrol => mcbKontrol,
        _ => mcbLampu,
    };

    public void SetMcb(JenisMcb j, bool on)
    {
        switch (j)
        {
            case JenisMcb.Utama: mcbUtama = on; break;
            case JenisMcb.Mesin: mcbMesin = on; break;
            case JenisMcb.Kontrol: mcbKontrol = on; break;
            case JenisMcb.Lampu: mcbLampu = on; break;
        }
        if (!AdaListrikKontrol || !AdaListrikMesin) jalan = false;
        Hitung();
        TerapkanVisual();
        Kabar("MCB " + j + ": " + (on ? "ON" : "OFF"));
    }

    void Hitung()
    {
        var m = !AdaListrikKontrol ? ModeMesin.Mati
              : Darurat ? ModeMesin.Darurat
              : jalan ? ModeMesin.Berjalan
              : ModeMesin.Siap;
        if (m == Mode) return;
        Mode = m;
        ModeBerubah?.Invoke(m);
    }

    void TerapkanVisual()
    {
        bool lampu = mcbUtama && mcbLampu;
        foreach (var l in lampuPlafon) if (l) l.enabled = lampu;
        for (int i = 0; i < kapLampu.Length; i++)
            SetEmisi(kapLampu[i], lampu ? emisiKap[i] : Color.black);
        SetEmisi(lampuPower, mcbUtama ? new Color(0.1f, 1f, 0.2f) * 5f : Color.black);
        SetEmisi(lampuFault, mcbUtama && Darurat ? new Color(1f, 0.05f, 0.03f) * 5f : Color.black);
    }

    void SetEmisi(Renderer r, Color c)
    {
        if (!r) return;
        r.GetPropertyBlock(mpb);
        mpb.SetColor("_EmissionColor", c);
        r.SetPropertyBlock(mpb);
    }

    void Kabar(string s)
    {
        Pesan = s;
        WaktuPesan = Time.time;
    }
}
