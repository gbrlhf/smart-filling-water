using System.Collections.Generic;
using UnityEngine;

public enum StatusProduk
{
    OK,
    Reject_IsiKurang,
    Reject_TanpaTutup
}

// Siklus mesin pengisi botol rotary 10 slot.
// Tiap langkah: turret berputar 36°, lalu berhenti sementara stasiun bekerja bersamaan:
//   infeed (36°) botol kosong masuk | isi (180°) pompa mengisi | capping (144°) tutup dipasang
//   buang (108°) sensor QC memeriksa lalu botol disortir lewat bifurcated chute + pneumatic diverter
//   ke Kotak Penampung OK atau Kotak Penampung Reject (NG Bin).
// Waktu mesin hanya berjalan saat StatusMesin.Mode == Berjalan, jadi E-stop membekukan semua gerak.
public class MesinSimulasi : MonoBehaviour
{
    public static MesinSimulasi I { get; private set; }

    [Header("Waktu (detik)")]
    public float durasiPutar = 1.5f;
    public float durasiDiam = 2.6f;

    [Header("Botol & Kapasitas")]
    public bool infeedOtomatis = false;  // diisi oleh operator setelah setup peti dari rak
    public int kapasitasKotak = 30;
    public int kapasitasKotakReject = 12;

    [Header("Peluang Reject / Defect (0 - 1)")]
    [Range(0f, 1f)]
    [Tooltip("Peluang botol underfill (isi 30%-50%) atau pompa tersumbat")]
    public float peluangIsiGagal = 0.05f;

    [Range(0f, 1f)]
    [Tooltip("Peluang tutup botol gagal terpasang (missing cap / gagal drat)")]
    public float peluangTutupGagal = 0.05f;

    [Header("Tampilan")]
    public Material materialTeks;        // mat_teks_dunia (shader MesinPengisi/TeksDunia)

    const int JumlahSlot = 10;
    const float SudutSlot = 36f;
    const float SudutInfeed = 36f, SudutIsi = 180f, SudutTutup = 144f, SudutBuang = 108f;
    const float JariSlot = 0.25f;
    const float NozzleTurun = 0.025f, KepalaTurun = 0.05f;

    public class Botol
    {
        public GameObject go;
        public Transform cairan;
        public Renderer cairanR;
        public Transform tutup;
        public float isi;
        public float targetIsi = 1f;
        public bool isiGagal;
        public bool tutupGagal;
        public bool sudahDiisi;
        public bool sudahLewatCapping;
        public bool bertutup;
        public StatusProduk status = StatusProduk.OK;
    }

    class Jatuh
    {
        public Botol b;
        public bool keReject;
        public Vector3 p0, p1, p2;
        public Quaternion r0, r1;
        public float t;
    }

    // referensi scene
    Transform pusat, turret, hub, bendera, poros;
    Transform nozzle, nozzleSlider, kepala, slider, chuck;
    Transform diverter, pistonRod, sightGlassLiquid;
    GameObject templatBotol, templatCairan, templatTutup;
    Renderer ledStep, ledDir, ledEn, ledPompa, ledBuzzer, ledIr, ledStart, ledLimit, ledQc;
    Transform aliran;
    TextMesh teksJudulOk, teksCounterOk, teksJudulReject, teksCounterReject, teksSkalaTangki, teksHmi, teksMes;
    AudioSource suara;
    AudioClip[] bip;
    Vector3 titikTangkap, titikTangkapReject, posPistonAwal;

    // status siklus & MES
    readonly Botol[] slot = new Botol[JumlahSlot];
    readonly List<Jatuh> jatuh = new();
    readonly List<GameObject> diKotak = new();
    readonly List<GameObject> diKotakReject = new();
    int langkah;          // jumlah langkah selesai
    float tLangkah;       // waktu di dalam langkah
    bool diamDimulai, sedangIsi, sedangTutup;
    bool diverterAktif;
    float qcKedipSampai = -10f;
    float levelTangki = 0.92f; // proporsi isi Sanitary Buffer Tank (maks 20 L)
    Botol bIsi, bTutup, bInfeed;

    public int Selesai { get; private set; }              // Total Good count (OK)
    public int TotalReject { get; private set; }          // Total Defect/NG count
    public int RejectIsiKurang { get; private set; }
    public int RejectTanpaTutup { get; private set; }
    public int TotalDikemas { get; private set; }         // Botol OK yang sudah dikemas ke kardus
    public int KardusSelesai { get; private set; }        // Jumlah kardus di palet staging
    public StatusProduk StatusTerakhir { get; private set; } = StatusProduk.OK;

    // Indikator & Metrik MES
    public bool SetupSelesai { get; set; }
    public float SetupTime { get; set; }                  // Durasi ambil peti dari rak ke meja infeed
    public bool SedangStarving { get; private set; }
    public float StarvationDowntime { get; private set; } // Durasi tunggu bahan baku
    public bool BatchCompleted { get; set; }              // True saat 12 botol peti habis & selesai diproses
    public float BatchCycleTime { get; set; }             // Total waktu dari ambil peti di rak hingga kardus di palet
    public float FirstPassYield => (Selesai + TotalReject) > 0 ? (Selesai * 100f / (Selesai + TotalReject)) : 100f;

    public int IsiKotak => diKotak.Count + HitungJatuh(false);
    public int IsiKotakReject => diKotakReject.Count + HitungJatuh(true);
    public bool TurretDanJatuhKosong => HitungBotolDiTurret() == 0 && jatuh.Count == 0;
    public string Tahap { get; private set; } = "-";
    float SudutSekarang => (langkah + (diamDimulai ? 1 : 0)) * SudutSlot;

    Vector3 posNozzle, posNozzleSlider, posKepala, posSlider;
    Quaternion rotTurret, rotHub, rotBendera, rotPoros;
    float startLed = -10f, bunyiSampai = -10f;
    MaterialPropertyBlock mpb;
    readonly Dictionary<Renderer, Color> warnaLed = new();

    int HitungJatuh(bool reject)
    {
        int n = 0;
        for (int i = 0; i < jatuh.Count; i++)
            if (jatuh[i].keReject == reject) n++;
        return n;
    }

    public int HitungBotolDiTurret()
    {
        int n = 0;
        for (int i = 0; i < JumlahSlot; i++)
            if (slot[i] != null) n++;
        return n;
    }

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
        diverter = Cari(root, "reject_diverter");
        pistonRod = Cari(root, "pneumatic_piston_rod");
        if (pistonRod) posPistonAwal = pistonRod.localPosition;
        sightGlassLiquid = Cari(root, "sight_glass_liquid");

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

        var qcTr = Cari(root, "led_qc_status");
        if (qcTr)
        {
            ledQc = qcTr.GetComponent<Renderer>();
            ledQc.sharedMaterial.EnableKeyword("_EMISSION");
        }

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

        // Signage di sisi kiri luar masing-masing kotak penampung (2 TextMesh terpisah: anti-overlap)
        var papanOk = Cari(root, "box_counter_sign");
        if (papanOk)
        {
            BersihkanAnakTeks(papanOk);
            teksJudulOk = BuatTeksSamping(papanOk, new Vector3(-0.0065f, 0.022f, 0f), 0.0046f, "[ OK PRODUCT ]", new Color(0.75f, 1f, 0.80f));
            teksCounterOk = BuatTeksSamping(papanOk, new Vector3(-0.0065f, -0.016f, 0f), 0.0062f, "QTY: 00 / 30", new Color(0.25f, 1f, 0.35f));
        }

        var papanReject = Cari(root, "reject_counter_sign");
        if (papanReject)
        {
            BersihkanAnakTeks(papanReject);
            teksJudulReject = BuatTeksSamping(papanReject, new Vector3(-0.0065f, 0.022f, 0f), 0.0048f, "[ REJECT BIN ]", new Color(1f, 0.92f, 0.25f));
            teksCounterReject = BuatTeksSamping(papanReject, new Vector3(-0.0065f, -0.016f, 0f), 0.0062f, "NG: 00 / 12", new Color(1f, 0.30f, 0.22f));
        }

        // Skala Volume (L) pada Sight Glass Sanitary Buffer Tank
        var platSkala = Cari(root, "sight_glass_scale_plate");
        if (platSkala)
        {
            BersihkanAnakTeks(platSkala);
            teksSkalaTangki = BuatTeksDunia(platSkala, platSkala.position + new Vector3(0.004f, 0f, 0f), Quaternion.Euler(0f, -90f, 0f), 0.0026f, "20L\n15L\n10L\n5L", new Color(0.1f, 0.15f, 0.2f));
        }

        teksHmi = BuatTeks(Cari(root, "hmi_screen"), new Vector3(0f, 0f, 0.001f), 0.00145f, "");
        var layarMes = Cari(root, "mes_screen");
        if (layarMes)
        {
            BersihkanAnakTeks(layarMes);
            teksMes = BuatTeks(layarMes, new Vector3(0f, 0.01f, 0.02f), 0.0040f, "");
            teksMes.transform.rotation = Quaternion.Euler(0f, 145f, 0f);
        }

        var buzzer = Cari(root, "buzzer");
        suara = buzzer.gameObject.AddComponent<AudioSource>();
        suara.spatialBlend = 1f; suara.minDistance = 0.5f; suara.maxDistance = 12f; suara.playOnAwake = false;
        bip = new[] { BuatBip(1), BuatBip(2), BuatBip(3) };
        titikTangkap = Cari(root, "box_catch_point").position;
        var trRejectCatch = Cari(root, "reject_catch_point");
        titikTangkapReject = trRejectCatch ? trRejectCatch.position : new Vector3(0.365f, 0.725f, 0.020f);
    }

    void Start()
    {
        // Sesuai Alur 3: Saat awal permainan, meja infeed & turret masih kosong
        // karena operator akan mengambil peti botol kosong dari Rak Penyimpanan Besi terlebih dahulu.
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

        if (jalan)
        {
            Majukan(dt);
            if (SedangStarving) StarvationDowntime += dt;
        }
        else
        {
            aliran.gameObject.SetActive(false);
        }

        PerbaruiDiverter(Time.deltaTime);
        PerbaruiSightGlass();
        PerbaruiJatuh(dt);
        PerbaruiLed(st, jalan);
        PerbaruiTeks(st);
    }

    // ---------- siklus ----------

    void Majukan(float dt)
    {
        if (BatchCompleted)
        {
            Tahap = "BATCH COMPLETED (Pengemasan)";
            aliran.gameObject.SetActive(false);
            SedangStarving = false;
            return;
        }

        if (!SetupSelesai)
        {
            Tahap = "SETUP: Ambil Peti dari Rak";
            aliran.gameObject.SetActive(false);
            SedangStarving = false;
            return;
        }

        tLangkah += dt;
        if (tLangkah < durasiPutar)
        {
            Tahap = SedangStarving ? "Idle / Starving (Turret berputar)" : "Turret berputar";
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

        int ki = SlotDi(SudutInfeed, langkah + 1);
        if (u >= 0.6f && slot[ki] == null && bInfeed == null && HitungBotolDiTurret() == 0)
            SedangStarving = true;
        else if (slot[ki] != null || bInfeed != null)
            SedangStarving = false;

        var aktif = new List<string>();
        if (bInfeed != null && u < 0.5f) aktif.Add("Botol masuk");
        if (sedangIsi) aktif.Add(bIsi != null && bIsi.isiGagal ? "Mengisi (Underfill)" : "Mengisi");
        if (sedangTutup) aktif.Add(bTutup != null && bTutup.tutupGagal ? "Gagal Tutup" : "Memasang tutup");
        if (jatuh.Count > 0) aktif.Add(diverterAktif ? "Diverter -> Reject Bin" : "Chute -> Kotak OK");
        if (SedangStarving) aktif.Add("STARVING");
        Tahap = aktif.Count > 0 ? string.Join(" + ", aktif) : "Menunggu";

        if (tLangkah >= durasiPutar + durasiDiam) SelesaiLangkah();
    }

    void MulaiDiam()
    {
        diamDimulai = true;
        PutarTurret((langkah + 1) * SudutSlot);
        int n = langkah + 1;

        // Stasiun Buang & Sensor QC (108°): periksa botol yang sudah melewati stasiun pengisian/capping
        int kb = SlotDi(SudutBuang, n);
        if (slot[kb] != null && (slot[kb].sudahDiisi || slot[kb].sudahLewatCapping || slot[kb].bertutup))
        {
            var bBuang = slot[kb];
            EvaluasiQC(bBuang);
            StatusTerakhir = bBuang.status;

            if (bBuang.status == StatusProduk.OK)
            {
                diverterAktif = false;
                if (IsiKotak >= kapasitasKotak)
                {
                    StatusMesin.I.Henti("Kotak penampung OK penuh: kosongkan dulu!");
                    Bunyi(3);
                }
                else
                {
                    LepasKeKotak(bBuang, false);
                    slot[kb] = null;
                }
            }
            else
            {
                // Botol REJECT terdeteksi Sensor QC -> Aktifkan Diverter Flap + 2x Bip Pendek
                diverterAktif = true;
                qcKedipSampai = Time.time + 2.4f;
                Bunyi(2);
                StatusMesin.I.Kabar("QC REJECT (" + bBuang.status + ") -> Diverter mengarahkan ke Kotak Reject");

                if (IsiKotakReject >= kapasitasKotakReject)
                {
                    StatusMesin.I.Henti("Wadah reject penuh, segera kosongkan!");
                    Bunyi(3);
                }
                else
                {
                    LepasKeKotak(bBuang, true);
                    slot[kb] = null;
                }
            }
        }
        else
        {
            diverterAktif = false;
        }

        // Stasiun Isi (180°)
        bIsi = Ambil(SudutIsi, n, b => !b.sudahDiisi);
        if (bIsi != null)
        {
            if (Random.value < peluangIsiGagal)
            {
                bIsi.isiGagal = true;
                bIsi.targetIsi = Random.Range(0.30f, 0.50f); // Underfill 30% - 50%
                bIsi.status = StatusProduk.Reject_IsiKurang;
            }
            else
            {
                bIsi.isiGagal = false;
                bIsi.targetIsi = 1f;
            }
        }

        // Stasiun Capping (144°)
        bTutup = Ambil(SudutTutup, n, b => b.sudahDiisi && !b.sudahLewatCapping);
        if (bTutup != null)
        {
            if (Random.value < peluangTutupGagal)
            {
                bTutup.tutupGagal = true;
                if (bTutup.status == StatusProduk.OK)
                    bTutup.status = StatusProduk.Reject_TanpaTutup;
            }
            else
            {
                bTutup.tutupGagal = false;
                PasangTutup(bTutup);
            }
        }

        // Stasiun Infeed (36°)
        bInfeed = null;
        int ki = SlotDi(SudutInfeed, n);
        if (infeedOtomatis && slot[ki] == null)
        {
            bInfeed = slot[ki] = BuatBotol(ki);
            SedangStarving = false;
        }
    }

    void EvaluasiQC(Botol b)
    {
        if (b.isi < 0.85f || b.isiGagal)
            b.status = StatusProduk.Reject_IsiKurang;
        else if (!b.bertutup || b.tutupGagal)
            b.status = StatusProduk.Reject_TanpaTutup;
        else
            b.status = StatusProduk.OK;
    }

    void SelesaiLangkah()
    {
        PerbaruiInfeed(99f); PerbaruiIsi(99f); PerbaruiTutup(99f);
        if (bIsi != null)
        {
            bIsi.sudahDiisi = true;
            levelTangki = Mathf.Clamp(levelTangki - 0.015f * bIsi.targetIsi, 0.25f, 0.95f);
        }
        if (bTutup != null) bTutup.sudahLewatCapping = true;
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

        float durasiPompa = ada ? 1.8f * bIsi.targetIsi : 1.8f;
        bool pompa = ada && u >= 0.4f && u < (0.4f + durasiPompa);
        if (ada)
        {
            float capai = Mathf.Clamp01((u - 0.4f) / 1.8f);
            bIsi.isi = Mathf.Max(bIsi.isi, Mathf.Min(bIsi.targetIsi, capai));
            AturCairan(bIsi);
            sedangIsi = pompa;
            if (u >= 2.2f) bIsi.sudahDiisi = true;
        }
        aliran.gameObject.SetActive(pompa);
        if (pompa)
        {
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

        if (bTutup.tutupGagal)
        {
            if (u >= 0.6f && u < 1.6f)
            {
                sedangTutup = true;
                chuck.Rotate(0f, 720f * Time.deltaTime, 0f, Space.Self);
            }
            if (u >= 1.6f)
            {
                bTutup.bertutup = false;
                bTutup.sudahLewatCapping = true;
            }
            return;
        }

        if (bTutup.tutup != null)
            bTutup.tutup.localPosition = new Vector3(0f, Mathf.Lerp(0.06f, 0.03f, Mathf.Clamp01(u / 0.25f)), 0f);
        if (u >= 0.6f && u < 1.6f)
        {
            sedangTutup = true;
            float putar = 720f * Time.deltaTime;
            chuck.Rotate(0f, putar, 0f, Space.Self);
            if (bTutup.tutup != null) bTutup.tutup.Rotate(0f, putar, 0f, Space.Self);
        }
        if (u >= 1.6f)
        {
            bTutup.bertutup = true;
            bTutup.sudahLewatCapping = true;
        }
    }

    void PerbaruiDiverter(float dt)
    {
        if (diverter)
        {
            // Saat OK: sudut 0° (jalur OK terbuka). Saat Reject: sudut +42° (flap membelokkan botol ke chute Reject)
            float targetY = diverterAktif ? 42f : 0f;
            var e = diverter.localEulerAngles;
            float y = Mathf.MoveTowardsAngle(e.y, targetY, 260f * dt);
            diverter.localRotation = Quaternion.Euler(0f, y, 0f);
        }
        if (pistonRod)
        {
            Vector3 targetPos = posPistonAwal + new Vector3(0f, 0f, diverterAktif ? -0.025f : 0f);
            pistonRod.localPosition = Vector3.MoveTowards(pistonRod.localPosition, targetPos, 0.15f * dt);
        }
    }

    void PerbaruiSightGlass()
    {
        if (!sightGlassLiquid) return;
        float h = 0.115f * levelTangki;
        sightGlassLiquid.localScale = new Vector3(0.012f, h, 0.012f);
        sightGlassLiquid.localPosition = new Vector3(0.146f, 0.045f + h, 0f);
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
        float a = k * SudutSlot * Mathf.Deg2Rad;
        var putaran = Quaternion.Euler(0f, SudutSekarang, 0f);
        tr.SetPositionAndRotation(pusat.position + putaran * new Vector3(Mathf.Sin(a), 0f, -Mathf.Cos(a)) * JariSlot, putaran);

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

    void LepasKeKotak(Botol b, bool keReject)
    {
        var tr = b.go.transform;
        tr.SetParent(transform.parent, true);

        if (!keReject)
        {
            int i = IsiKotak;
            // susunan rapi di dasar kotak OK (Z = -0.180): 2 kolom (x) x 5 baris (z) x 3 lapis
            int kol = i % 2, baris = i / 2 % 5, lapis = i / 10;
            var pusatBotol = new Vector3(0.49f + kol * 0.22f, 0.425f + lapis * 0.066f, -0.32f + baris * 0.07f);
            var r1 = Quaternion.Euler(0f, 0f, 90f);
            jatuh.Add(new Jatuh
            {
                b = b,
                keReject = false,
                p0 = tr.position,
                // p1 tepat melalui bukaan lubang meja (X=0.35, Y=0.74, Z=-0.173) lalu meluncur di atas chute OK
                p1 = new Vector3(0.38f, 0.72f, -0.173f),
                p2 = pusatBotol - r1 * new Vector3(0f, -0.07f, 0f),
                r0 = tr.rotation,
                r1 = r1,
            });
        }
        else
        {
            int i = IsiKotakReject;
            // susunan rapi di dasar Kotak Reject (NG Bin di Z = +0.250): 2 kolom x 4 baris x 2 lapis
            int kol = i % 2, baris = i / 2 % 4, lapis = i / 8;
            var pusatBotol = new Vector3(0.49f + kol * 0.22f, 0.425f + lapis * 0.066f, 0.13f + baris * 0.08f);
            var r1 = Quaternion.Euler(0f, 0f, 90f);
            jatuh.Add(new Jatuh
            {
                b = b,
                keReject = true,
                p0 = tr.position,
                // p1 mengikuti arah belokan diverter & meluncur di atas reject_chute_ramp tanpa menembus meja!
                p1 = titikTangkapReject + new Vector3(0f, 0.08f, 0f),
                p2 = pusatBotol - r1 * new Vector3(0f, -0.07f, 0f),
                r0 = tr.rotation,
                r1 = r1,
            });
        }
    }

    void PerbaruiJatuh(float dt)
    {
        for (int i = jatuh.Count - 1; i >= 0; i--)
        {
            var j = jatuh[i];
            j.t = Mathf.Min(1f, j.t + dt / 0.85f);
            float p = Mathf.SmoothStep(0f, 1f, j.t);
            var a = Vector3.Lerp(j.p0, j.p1, p);
            var c = Vector3.Lerp(j.p1, j.p2, p);
            var tr = j.b.go.transform;
            tr.SetPositionAndRotation(Vector3.Lerp(a, c, p), Quaternion.Slerp(j.r0, j.r1, Mathf.Clamp01(p * 1.4f)));
            if (j.t >= 1f)
            {
                var selesaiBotol = j.b;
                bool keReject = j.keReject;
                jatuh.RemoveAt(i);

                if (!keReject)
                {
                    diKotak.Add(selesaiBotol.go);
                    Selesai++;
                    Bunyi(1);
                }
                else
                {
                    diKotakReject.Add(selesaiBotol.go);
                    TotalReject++;
                    if (selesaiBotol.status == StatusProduk.Reject_IsiKurang) RejectIsiKurang++;
                    else if (selesaiBotol.status == StatusProduk.Reject_TanpaTutup) RejectTanpaTutup++;

                    if (diKotakReject.Count >= kapasitasKotakReject)
                    {
                        StatusMesin.I.Henti("Wadah reject penuh, segera kosongkan!");
                        Bunyi(3);
                    }
                }
            }
        }
    }

    // Dipakai operator: slot infeed kosong, turret diam, dan sisa waktu diam cukup untuk menaruh botol.
    public bool BisaTerimaBotol(float butuhDetik) =>
        StatusMesin.I.Mode == ModeMesin.Berjalan && SetupSelesai && !BatchCompleted && diamDimulai
        && slot[SlotDi(SudutInfeed, langkah + 1)] == null
        && durasiPutar + durasiDiam - tLangkah >= butuhDetik;

    public bool TerimaBotol()
    {
        if (!diamDimulai) return false;
        int k = SlotDi(SudutInfeed, langkah + 1);
        if (slot[k] != null) return false;
        slot[k] = BuatBotol(k);
        SedangStarving = false;
        return true;
    }

    // Dipanggil operator saat Secondary Packaging: memindahkan semua botol OK dari Kotak OK ke dalam Kardus Kemasan
    public int KemasBotolOkKeKardus()
    {
        int n = diKotak.Count;
        foreach (var go in diKotak) Destroy(go);
        diKotak.Clear();
        TotalDikemas += n;
        KardusSelesai++;
        levelTangki = 0.92f; // isi ulang otomatis buffer tank untuk batch berikutnya
        return n;
    }

    public void KosongkanKotak()
    {
        foreach (var go in diKotak) Destroy(go);
        int n = diKotak.Count;
        diKotak.Clear();
        StatusMesin.I.Kabar("Kotak penampung OK dikosongkan (" + n + " botol)");
    }

    public void KosongkanKotakReject()
    {
        foreach (var go in diKotakReject) Destroy(go);
        int n = diKotakReject.Count;
        diKotakReject.Clear();
        StatusMesin.I.Kabar("Wadah reject dikosongkan (" + n + " botol defect)");
    }

    // ---------- LED, teks, bunyi ----------

    void PerbaruiLed(StatusMesin st, bool jalan)
    {
        bool listrik = st.AdaListrikKontrol;
        bool berputar = jalan && SetupSelesai && !BatchCompleted && tLangkah < durasiPutar;
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

        if (ledQc)
        {
            ledQc.GetPropertyBlock(mpb);
            if (!listrik)
            {
                mpb.SetColor("_BaseColor", new Color(0.2f, 0.2f, 0.2f));
                mpb.SetColor("_EmissionColor", Color.black);
            }
            else if (Time.time < qcKedipSampai)
            {
                bool kedip = Mathf.Repeat(Time.time * 10f, 1f) < 0.5f;
                var merah = new Color(1f, 0.15f, 0.1f);
                mpb.SetColor("_BaseColor", merah);
                mpb.SetColor("_EmissionColor", kedip ? merah * 8f : Color.black);
            }
            else
            {
                var hijau = new Color(0.2f, 0.95f, 0.35f);
                mpb.SetColor("_BaseColor", hijau);
                mpb.SetColor("_EmissionColor", hijau * 3.5f);
            }
            ledQc.SetPropertyBlock(mpb);
        }
    }

    void PerbaruiTeks(StatusMesin st)
    {
        if (teksJudulOk) teksJudulOk.text = "[ OK PRODUCT ]";
        if (teksCounterOk) teksCounterOk.text = "QTY: " + diKotak.Count.ToString("00") + " / " + kapasitasKotak.ToString("00");

        if (teksJudulReject) teksJudulReject.text = "[ REJECT BIN ]";
        if (teksCounterReject) teksCounterReject.text = "NG: " + diKotakReject.Count.ToString("00") + " / " + kapasitasKotakReject.ToString("00");

        if (!st.AdaListrikKontrol)
        {
            teksHmi.text = "";
            if (teksMes) teksMes.text = "";
            return;
        }
        var tahap = st.Mode == ModeMesin.Berjalan ? (BatchCompleted ? "BATCH COMPLETED" : Tahap)
                  : st.Mode == ModeMesin.Darurat ? "E-STOP" : "Tekan START";
        teksHmi.text = (BatchCompleted ? "BATCH COMPLETED" : st.Mode.ToString().ToUpper())
            + "\nOK:" + Selesai + " NG:" + TotalReject + " FPY:" + FirstPassYield.ToString("F0") + "%"
            + "\nSetup:" + SetupTime.ToString("F1") + "s Cycle:" + BatchCycleTime.ToString("F1") + "s"
            + "\n" + tahap;

        if (teksMes)
        {
            teksMes.text = "MES PRODUCTION DASHBOARD\n"
                + "Status : " + (BatchCompleted ? "BATCH COMPLETED" : st.Mode.ToString().ToUpper()) + "\n"
                + "Good: " + Selesai + " | Reject: " + TotalReject + " | Boxed: " + TotalDikemas + "\n"
                + "FPY : " + FirstPassYield.ToString("F1") + "% | Cartons: " + KardusSelesai + "\n"
                + "Setup: " + SetupTime.ToString("F1") + "s | BatchCycle: " + BatchCycleTime.ToString("F1") + "s";
        }
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

    static void BersihkanAnakTeks(Transform induk)
    {
        for (int i = induk.childCount - 1; i >= 0; i--)
            Destroy(induk.GetChild(i).gameObject);
    }

    TextMesh BuatTeksSamping(Transform papan, Vector3 offsetLokalDunia, float ukuran, string isi, Color warna)
    {
        var b = papan.GetComponent<Renderer>().bounds;
        // papan di sisi kiri luar kotak (X = 0.343), menghadap ke arah -X (rotasi Y = 90°)
        return BuatTeksDunia(papan, b.center + offsetLokalDunia, Quaternion.Euler(0f, 90f, 0f), ukuran, isi, warna);
    }

    TextMesh BuatTeksDunia(Transform induk, Vector3 posDunia, Quaternion rotDunia, float ukuran, string isi, Color warna)
    {
        var go = new GameObject("teks_label");
        go.transform.SetParent(induk, true);
        go.transform.SetPositionAndRotation(posDunia, rotDunia);
        var t = go.AddComponent<TextMesh>();
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.font = font;
        go.GetComponent<MeshRenderer>().sharedMaterial = new Material(materialTeks) { mainTexture = font.material.mainTexture };
        t.fontSize = 64;
        t.characterSize = ukuran;
        t.anchor = TextAnchor.MiddleCenter;
        t.alignment = TextAlignment.Center;
        t.color = warna;
        t.text = isi;
        return t;
    }

    TextMesh BuatTeks(Transform papan, Vector3 geser, float ukuran, string isi)
    {
        var b = papan.GetComponent<Renderer>().bounds;
        return BuatTeksDunia(papan, b.center + geser, Quaternion.Euler(0f, 180f, 0f), ukuran, isi, new Color(0.3f, 1f, 0.4f));
    }

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


