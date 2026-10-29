using System.Collections.Generic;
using UnityEngine;

// Siklus mesin pengisi botol rotary 10 slot.
// Tiap langkah: turret berputar 36°, lalu berhenti sementara stasiun bekerja bersamaan:
//   infeed (36°) botol kosong masuk | isi (180°) pompa mengisi | capping (144°) tutup dipasang
//   buang (108°) botol jatuh ke kotak penampung.
// Waktu mesin hanya berjalan saat StatusMesin.Mode == Berjalan, jadi E-stop membekukan semua gerak.
public class MesinSimulasi : MonoBehaviour
{
    public static MesinSimulasi I { get; private set; }

    [Header("Waktu (detik)")]
    public float durasiPutar = 0.8f;
    public float durasiDiam = 2.6f;

    [Header("Botol")]
    public bool infeedOtomatis = true;   // nanti diganti operator
    public int kapasitasKotak = 30;

    [Header("Tampilan")]
    public Material materialTeks;        // mat_teks_dunia (shader MesinPengisi/TeksDunia)

    const int JumlahSlot = 10;
    const float SudutSlot = 36f;
    const float SudutInfeed = 36f, SudutIsi = 180f, SudutTutup = 144f, SudutBuang = 108f;
    const float JariSlot = 0.25f;
    const float NozzleTurun = 0.025f, KepalaTurun = 0.05f;

    class Botol
    {
        public GameObject go;
        public Transform cairan;
        public Renderer cairanR;
        public Transform tutup;
        public float isi;
        public bool bertutup;
    }

    class Jatuh
    {
        public Botol b;
        public Vector3 p0, p1, p2;
        public Quaternion r0, r1;
        public float t;
    }

    // referensi scene
    Transform pusat, turret, hub, bendera, poros;
    Transform nozzle, nozzleSlider, kepala, slider, chuck;
    GameObject templatBotol, templatCairan, templatTutup;
    Renderer ledStep, ledDir, ledEn, ledPompa, ledBuzzer, ledIr, ledStart, ledLimit;
    Transform aliran;
    TextMesh teksKotak, teksHmi;
    AudioSource suara;
    AudioClip[] bip;
    Vector3 titikTangkap;

    // status siklus
    readonly Botol[] slot = new Botol[JumlahSlot];
    readonly List<Jatuh> jatuh = new();
    readonly List<GameObject> diKotak = new();
    int langkah;          // jumlah langkah selesai
    float tLangkah;       // waktu di dalam langkah
    bool diamDimulai, sedangIsi, sedangTutup;
    Botol bIsi, bTutup, bInfeed;
    public int Selesai { get; private set; }
    public int IsiKotak => diKotak.Count + jatuh.Count;
    public string Tahap { get; private set; } = "-";
    // sudut turret saat ini: setelah MulaiDiam turret sudah di posisi langkah berikutnya
    float SudutSekarang => (langkah + (diamDimulai ? 1 : 0)) * SudutSlot;

    Vector3 posNozzle, posNozzleSlider, posKepala, posSlider;
    Quaternion rotTurret, rotHub, rotBendera, rotPoros;
    float startLed = -10f, bunyiSampai = -10f;
    MaterialPropertyBlock mpb;
    readonly Dictionary<Renderer, Color> warnaLed = new();

    void Awake()
    {
        I = this;
        mpb = new MaterialPropertyBlock();
        var root = transform.parent;
        turret = Cari(root, "turret_plate");
        hub = Cari(root, "turret_hub");
        bendera = Cari(root, "turret_flag");
        poros = Cari(root, "nema17_shaft");
        nozzle = Cari(root, "nozzle");
        nozzleSlider = Cari(root, "nozzle_slider");
        kepala = Cari(root, "capper_head");
        slider = Cari(root, "capper_slider");
        chuck = Cari(root, "capper_chuck");
        templatBotol = Cari(root, "bottle").gameObject;
        templatCairan = Cari(root, "bottle_liquid").gameObject;
        templatTutup = Cari(root, "cap_01").gameObject;
        ledStep = Led(root, "led_gpio18_step");
        ledDir = Led(root, "led_gpio19_dir");
        ledEn = Led(root, "led_gpio23_en");
        ledPompa = Led(root, "led_gpio25_pump");
        ledBuzzer = Led(root, "led_gpio26_buzzer");
        ledIr = Led(root, "led_gpio27_ir");
        ledStart = Led(root, "led_gpio32_start");
        ledLimit = Led(root, "led_gpio33_limit");

        pusat = new GameObject("pusat_turret").transform;
        pusat.position = turret.position;
        posNozzle = nozzle.localPosition; posNozzleSlider = nozzleSlider.localPosition;
        posKepala = kepala.localPosition; posSlider = slider.localPosition;
        rotTurret = turret.localRotation; rotHub = hub.localRotation;
        rotBendera = bendera.localRotation; rotPoros = poros.localRotation;

        // botol contoh di scene jadi templat, disembunyikan
        templatBotol.SetActive(false);
        templatCairan.SetActive(false);

        aliran = BuatAliran();
        teksKotak = BuatTeks(Cari(root, "box_counter_sign"), new Vector3(0f, 0f, 0.0065f), 0.012f, "000");
        teksHmi = BuatTeks(Cari(root, "hmi_screen"), new Vector3(0f, 0f, 0.001f), 0.0019f, "");
        var buzzer = Cari(root, "buzzer");
        suara = buzzer.gameObject.AddComponent<AudioSource>();
        suara.spatialBlend = 1f; suara.minDistance = 0.5f; suara.maxDistance = 12f; suara.playOnAwake = false;
        bip = new[] { BuatBip(1), BuatBip(2), BuatBip(3) };
        titikTangkap = Cari(root, "box_catch_point").position;
    }

    void Start()
    {
        // isi awal: botol kosong di jalur infeed -> pengisian, supaya pengisian langsung terlihat
        foreach (var a in new[] { 0f, 324f, 288f, 252f, 216f })
            slot[SlotDi(a)] = BuatBotol(SlotDi(a));
        StatusMesin.I.ModeBerubah += m =>
        {
            if (m == ModeMesin.Berjalan) startLed = Time.time;
            if (m == ModeMesin.Darurat) Bunyi(3);
        };
    }

    void Update()
    {
        var st = StatusMesin.I;
        bool jalan = st.Mode == ModeMesin.Berjalan;
        float dt = jalan ? Time.deltaTime : 0f;

        if (jalan) Majukan(dt);
        else aliran.gameObject.SetActive(false);
        PerbaruiJatuh(dt);
        PerbaruiLed(st, jalan);
        PerbaruiTeks(st);
    }

    // ---------- siklus ----------

    void Majukan(float dt)
    {
        tLangkah += dt;
        if (tLangkah < durasiPutar)
        {
            Tahap = "Turret berputar";
            float p = Mathf.SmoothStep(0f, 1f, tLangkah / durasiPutar);
            PutarTurret((langkah + p) * SudutSlot);
            return;
        }
        if (!diamDimulai) MulaiDiam();

        float u = tLangkah - durasiPutar;
        sedangIsi = sedangTutup = false;
        PerbaruiInfeed(u);
        PerbaruiIsi(u);
        PerbaruiTutup(u);
        var aktif = new List<string>();
        if (bInfeed != null && u < 0.5f) aktif.Add("Botol masuk");
        if (sedangIsi) aktif.Add("Mengisi");
        if (sedangTutup) aktif.Add("Memasang tutup");
        if (jatuh.Count > 0) aktif.Add("Buang ke kotak");
        Tahap = aktif.Count > 0 ? string.Join(" + ", aktif) : "Menunggu";

        if (tLangkah >= durasiPutar + durasiDiam) SelesaiLangkah();
    }

    void MulaiDiam()
    {
        diamDimulai = true;
        PutarTurret((langkah + 1) * SudutSlot);
        int n = langkah + 1;

        // buang: botol bertutup lepas dari turret
        int kb = SlotDi(SudutBuang, n);
        if (slot[kb] != null && slot[kb].bertutup)
        {
            if (IsiKotak >= kapasitasKotak)
            {
                StatusMesin.I.Henti("Kotak penampung penuh: kosongkan dulu");
                Bunyi(2);
            }
            else
            {
                LepasKeKotak(slot[kb]);
                slot[kb] = null;
            }
        }

        bIsi = Ambil(SudutIsi, n, b => b.isi < 1f);
        bTutup = Ambil(SudutTutup, n, b => b.isi >= 1f && !b.bertutup);
        bInfeed = null;
        int ki = SlotDi(SudutInfeed, n);
        if (infeedOtomatis && slot[ki] == null) bInfeed = slot[ki] = BuatBotol(ki);
        if (bTutup != null) PasangTutup(bTutup);
    }

    void SelesaiLangkah()
    {
        PerbaruiInfeed(99f); PerbaruiIsi(99f); PerbaruiTutup(99f);
        langkah++;
        tLangkah = 0f;
        diamDimulai = false;
        bIsi = bTutup = bInfeed = null;
    }

    void PerbaruiInfeed(float u)
    {
        if (bInfeed == null) return;
        float p = Mathf.Clamp01(u / 0.5f);
        var go = bInfeed.go.transform;
        go.localPosition = new Vector3(go.localPosition.x, Mathf.Lerp(0.12f, 0f, p * p), go.localPosition.z);
    }

    void PerbaruiIsi(float u)
    {
        bool ada = bIsi != null;
        float turun = !ada ? 0f : Bentuk(u, 0.1f, 0.4f, 2.2f, 2.5f);
        nozzle.localPosition = posNozzle + Vector3.down * NozzleTurun * turun;
        nozzleSlider.localPosition = posNozzleSlider + Vector3.down * NozzleTurun * turun;

        bool pompa = ada && u >= 0.4f && u < 2.2f;
        if (ada)
        {
            bIsi.isi = Mathf.Max(bIsi.isi, Mathf.Clamp01((u - 0.4f) / 1.8f));
            AturCairan(bIsi);
            sedangIsi = pompa;
        }
        aliran.gameObject.SetActive(pompa);
        if (pompa)
        {
            // aliran dari ujung nozzle ke permukaan cairan
            float atas = nozzle.position.y - 0.06f;
            float bawah = bIsi.cairan.position.y + 0.13f * bIsi.isi;
            aliran.position = new Vector3(nozzle.position.x, (atas + bawah) / 2f, nozzle.position.z);
            aliran.localScale = new Vector3(0.006f, Mathf.Max(0.001f, (atas - bawah) / 2f), 0.006f);
        }
    }

    void PerbaruiTutup(float u)
    {
        bool ada = bTutup != null;
        float turun = !ada ? 0f : Bentuk(u, 0.3f, 0.6f, 1.6f, 1.9f);
        kepala.localPosition = posKepala + Vector3.down * KepalaTurun * turun;
        slider.localPosition = posSlider + Vector3.down * KepalaTurun * turun;
        if (!ada) return;

        // tutup jatuh dari chute ke mulut botol, lalu diputar chuck
        bTutup.tutup.localPosition = new Vector3(0f, Mathf.Lerp(0.06f, 0.03f, Mathf.Clamp01(u / 0.25f)), 0f);
        if (u >= 0.6f && u < 1.6f)
        {
            sedangTutup = true;
            float putar = 720f * Time.deltaTime;
            chuck.Rotate(0f, putar, 0f, Space.Self);
            bTutup.tutup.Rotate(0f, putar, 0f, Space.Self);
        }
        if (u >= 1.6f) bTutup.bertutup = true;
    }

    void PutarTurret(float sudut)
    {
        var r = Quaternion.Euler(0f, sudut, 0f);
        turret.localRotation = r * rotTurret;
        hub.localRotation = r * rotHub;
        bendera.localRotation = r * rotBendera;
        poros.localRotation = r * rotPoros;
    }

    // ---------- botol ----------

    Botol BuatBotol(int k)
    {
        var b = new Botol();
        b.go = new GameObject("botol_slot_" + (k + 1));
        var tr = b.go.transform;
        tr.SetParent(turret, false);
        // slot k ada di sudut 36k dari sumbu -Z saat turret di 0°, lalu ikut putaran turret sekarang
        float a = k * SudutSlot * Mathf.Deg2Rad;
        var putaran = Quaternion.Euler(0f, SudutSekarang, 0f);
        tr.SetPositionAndRotation(pusat.position + putaran * new Vector3(Mathf.Sin(a), 0f, -Mathf.Cos(a)) * JariSlot, putaran);

        // leher botol tergantung di pelat: badan 0,17 m di bawah titik slot
        Klon(templatBotol, tr, new Vector3(0f, -0.17f, 0f));
        var cairan = Klon(templatCairan, tr, new Vector3(0f, -0.169f, 0f));
        b.cairan = cairan.transform;
        b.cairanR = cairan.GetComponent<Renderer>();
        AturCairan(b);
        return b;
    }

    GameObject Klon(GameObject templat, Transform induk, Vector3 posLokal)
    {
        var go = Instantiate(templat, induk);
        go.SetActive(true);
        go.transform.localPosition = posLokal;
        go.transform.localRotation = Quaternion.identity;
        foreach (var c in go.GetComponentsInChildren<Collider>()) Destroy(c);
        return go;
    }

    void AturCairan(Botol b)
    {
        b.cairanR.enabled = b.isi > 0.001f;
        b.cairan.localScale = new Vector3(1f, Mathf.Max(0.001f, b.isi), 1f);
    }

    void PasangTutup(Botol b)
    {
        var t = Klon(templatTutup, b.go.transform, new Vector3(0f, 0.06f, 0f));
        b.tutup = t.transform;
    }

    void LepasKeKotak(Botol b)
    {
        var tr = b.go.transform;
        tr.SetParent(transform.parent, true);
        int i = IsiKotak;
        // susunan di dasar kotak: 2 kolom (x) x 5 baris (z) x 3 lapis
        int kol = i % 2, baris = i / 2 % 5, lapis = i / 10;
        var pusatBotol = new Vector3(0.49f + kol * 0.22f, 0.425f + lapis * 0.066f, -0.32f + baris * 0.07f);
        // botol berbaring sepanjang X; pusat badan botol 0,07 m di bawah titik leher
        var r1 = Quaternion.Euler(0f, 0f, 90f);
        jatuh.Add(new Jatuh
        {
            b = b,
            p0 = tr.position,
            p1 = titikTangkap + Vector3.up * 0.12f,
            p2 = pusatBotol - r1 * new Vector3(0f, -0.07f, 0f),
            r0 = tr.rotation,
            r1 = r1,
        });
    }

    void PerbaruiJatuh(float dt)
    {
        for (int i = jatuh.Count - 1; i >= 0; i--)
        {
            var j = jatuh[i];
            j.t = Mathf.Min(1f, j.t + dt / 0.9f);
            float p = j.t * j.t;   // makin cepat seperti jatuh
            var a = Vector3.Lerp(j.p0, j.p1, p);
            var c = Vector3.Lerp(j.p1, j.p2, p);
            var tr = j.b.go.transform;
            tr.SetPositionAndRotation(Vector3.Lerp(a, c, p), Quaternion.Slerp(j.r0, j.r1, Mathf.Clamp01(p * 1.5f)));
            if (j.t >= 1f)
            {
                diKotak.Add(j.b.go);
                jatuh.RemoveAt(i);
                Selesai++;
                Bunyi(1);
            }
        }
    }

    // Dipakai operator: slot infeed kosong, turret diam, dan sisa waktu diam cukup untuk menaruh botol.
    public bool BisaTerimaBotol(float butuhDetik) =>
        StatusMesin.I.Mode == ModeMesin.Berjalan && diamDimulai
        && slot[SlotDi(SudutInfeed, langkah + 1)] == null
        && durasiPutar + durasiDiam - tLangkah >= butuhDetik;

    public bool TerimaBotol()
    {
        if (!diamDimulai) return false;
        int k = SlotDi(SudutInfeed, langkah + 1);
        if (slot[k] != null) return false;
        slot[k] = BuatBotol(k);
        return true;
    }

    public void KosongkanKotak()
    {
        foreach (var go in diKotak) Destroy(go);
        int n = diKotak.Count;
        diKotak.Clear();
        StatusMesin.I.Kabar("Kotak penampung dikosongkan (" + n + " botol)");
    }

    // ---------- LED, teks, bunyi ----------

    void PerbaruiLed(StatusMesin st, bool jalan)
    {
        bool listrik = st.AdaListrikKontrol;
        bool berputar = jalan && tLangkah < durasiPutar;
        bool pompa = jalan && aliran.gameObject.activeSelf;
        bool irAda = slot[SlotDi(SudutIsi, langkah + (diamDimulai ? 1 : 0))] != null;
        bool home = !berputar && Mathf.Repeat(SudutSekarang, 360f) < 0.1f;

        Nyala(ledStep, listrik && berputar && Mathf.Repeat(Time.time * 16f, 1f) < 0.5f);
        Nyala(ledDir, listrik && jalan);
        Nyala(ledEn, listrik && st.Mode == ModeMesin.Berjalan && st.AdaListrikMesin);
        Nyala(ledPompa, listrik && pompa);
        Nyala(ledBuzzer, listrik && Time.time < bunyiSampai);
        Nyala(ledIr, listrik && irAda && !berputar);
        Nyala(ledStart, listrik && Time.time - startLed < 0.4f);
        Nyala(ledLimit, listrik && home);
    }

    void PerbaruiTeks(StatusMesin st)
    {
        teksKotak.text = Selesai.ToString("000");
        if (!st.AdaListrikKontrol) { teksHmi.text = ""; return; }
        var tahap = st.Mode == ModeMesin.Berjalan ? Tahap : st.Mode == ModeMesin.Darurat ? "E-STOP" : "Tekan START";
        teksHmi.text = st.Mode.ToString().ToUpper() + "\nBotol: " + Selesai + "\n" + tahap;
    }

    void Bunyi(int kali)
    {
        kali = Mathf.Clamp(kali, 1, bip.Length);
        suara.PlayOneShot(bip[kali - 1]);
        bunyiSampai = Time.time + 0.12f + 0.2f * (kali - 1);
    }

    void Nyala(Renderer r, bool on)
    {
        if (!r) return;
        r.GetPropertyBlock(mpb);
        mpb.SetColor("_EmissionColor", on ? warnaLed[r] * 6f : Color.black);
        r.SetPropertyBlock(mpb);
    }

    // ---------- bantu ----------

    int SlotDi(float sudut) => SlotDi(sudut, langkah);

    static int SlotDi(float sudut, int n) => ((Mathf.RoundToInt(sudut / SudutSlot) + n) % JumlahSlot + JumlahSlot) % JumlahSlot;

    Botol Ambil(float sudut, int n, System.Func<Botol, bool> syarat)
    {
        var b = slot[SlotDi(sudut, n)];
        return b != null && syarat(b) ? b : null;
    }

    // naik 0->1 di [a,b], tahan, turun 1->0 di [c,d]
    static float Bentuk(float u, float a, float b, float c, float d)
    {
        if (u <= a || u >= d) return 0f;
        if (u < b) return Mathf.SmoothStep(0f, 1f, (u - a) / (b - a));
        if (u <= c) return 1f;
        return Mathf.SmoothStep(1f, 0f, (u - c) / (d - c));
    }

    Renderer Led(Transform root, string nama)
    {
        var r = Cari(root, nama).GetComponent<Renderer>();
        r.sharedMaterial.EnableKeyword("_EMISSION");
        warnaLed[r] = r.sharedMaterial.GetColor("_BaseColor");
        return r;
    }

    Transform BuatAliran()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "aliran_cairan";
        Destroy(go.GetComponent<Collider>());
        go.GetComponent<Renderer>().sharedMaterial = templatCairan.GetComponent<Renderer>().sharedMaterial;
        go.transform.SetParent(transform, true);
        go.SetActive(false);
        return go.transform;
    }

    TextMesh BuatTeks(Transform papan, Vector3 geser, float ukuran, string isi)
    {
        var go = new GameObject("teks");
        var b = papan.GetComponent<Renderer>().bounds;
        go.transform.SetParent(papan, true);
        go.transform.SetPositionAndRotation(b.center + geser, Quaternion.Euler(0f, 180f, 0f));
        var t = go.AddComponent<TextMesh>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.font = font;
        // material font bawaan tembus benda saat ada OnGUI; pakai shader teks dengan uji kedalaman normal
        go.GetComponent<MeshRenderer>().sharedMaterial = new Material(materialTeks) { mainTexture = font.material.mainTexture };
        t.fontSize = 64; t.characterSize = ukuran; t.anchor = TextAnchor.MiddleCenter; t.alignment = TextAlignment.Center;
        t.color = new Color(0.3f, 1f, 0.4f);
        t.text = isi;
        return t;
    }

    // bunyi buzzer 2,7 kHz: `kali` bip 0,12 s dengan jeda 0,08 s
    static AudioClip BuatBip(int kali)
    {
        int rate = 44100, satu = rate * 12 / 100, jarak = rate * 20 / 100;
        var data = new float[jarak * (kali - 1) + satu];
        for (int k = 0; k < kali; k++)
            for (int i = 0; i < satu; i++)
            {
                float env = Mathf.Min(1f, i / 200f) * Mathf.Min(1f, (satu - i) / 400f);
                data[k * jarak + i] = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 2700f * i / rate)) * 0.15f * env;
            }
        var clip = AudioClip.Create("bip_buzzer_" + kali, data.Length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    static Transform Cari(Transform t, string n)
    {
        if (t.name == n) return t;
        foreach (Transform c in t) { var r = Cari(c, n); if (r) return r; }
        return null;
    }
}
