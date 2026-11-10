using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum ModeOperator
{
    Setup,
    Infeed,
    Packaging,
    Restock
}

// Siklus lengkap Operator MES:
// 1. SETUP / MATERIAL FETCH: Saat START ditekan, meja infeed awalnya kosong. Operator berjalan ke Rak Penyimpanan Besi
//    (lantai atas kanan), mengambil peti berisi 12 botol kosong, membawanya ke meja infeed, dan meletakkannya.
//    Durasi ini dicatat sebagai Machine Setup / Preparation Time.
// 2. PRODUKSI / INFEED: Operator mengambil botol satu per satu dari peti dan menaruhnya ke slot infeed turret.
// 3. SECONDARY PACKAGING (END-OF-BATCH): Ketika 12 botol dari peti habis dimasukkan dan seluruh botol selesai dipilah
//    ke Kotak OK / Kotak Reject (BATCH COMPLETED), operator beralih ke Tugas = "Pengemasan ke Kardus":
//    mengambil kardus kosong, berjalan ke Kotak OK, memindahkan botol OK ke kardus, menutup kardus, dan menumpuknya
//    di Palet Samping (Finished Goods Staging Area), lalu mencatat Batch Cycle Time & First Pass Yield (FPY).
// 4. Siklus berlanjut ke batch berikutnya (mengambil peti baru di rak).
[RequireComponent(typeof(Animator))]
public class OperatorController : MonoBehaviour
{
    public MesinSimulasi simulasi;

    [Header("Gerak")]
    public float kecepatanJalan = 0.9f;
    public float kecepatanPutar = 360f;   // derajat per detik
    public float kecepatanAnimasi = 2.4f;

    [Header("Titik Rute & Koordinat Area Kerja")]
    [Tooltip("Tempat berdiri kerja: dari sini tangan kanan tepat di atas slot infeed saat animasi place")]
    public Vector3 posisiKerja = new Vector3(0.37f, 0f, -0.86f);

    [Tooltip("Titik berdiri di depan Rak Penyimpanan Besi (sebelah kanan) untuk mengambil peti botol kosong")]
    public Vector3 posisiRakPeti = new Vector3(-0.92f, 0f, -2.15f);
    public Vector3 titikLihatRakPeti = new Vector3(-0.92f, 1.45f, -2.68f);

    [Tooltip("Titik berdiri di samping Meja Infeed untuk meletakkan peti botol")]
    public Vector3 posisiTaruhPeti = new Vector3(0.68f, 0f, -0.95f);
    public Vector3 titikLihatMejaPeti = new Vector3(1.00f, 0.85f, -0.60f);

    [Tooltip("Titik berdiri di depan Kardus Kemasan Kosong untuk Secondary Packaging")]
    public Vector3 posisiAmbilKardus = new Vector3(-2.30f, 0f, -1.85f);
    public Vector3 titikLihatKardus = new Vector3(-2.70f, 0.30f, -2.10f);

    [Tooltip("Titik berdiri di samping Kotak Penampung OK saat mengemas botol OK ke dalam kardus")]
    public Vector3 posisiKotakOk = new Vector3(0.60f, 0f, -0.68f);
    public Vector3 titikLihatKotakOk = new Vector3(0.60f, 0.53f, -0.18f);

    [Tooltip("Titik berdiri di dekat Palet Penumpukan (Finished Goods Staging Area) untuk menyimpan kardus jadi")]
    public Vector3 posisiPaletStaging = new Vector3(-2.35f, 0f, -1.30f);
    public Vector3 titikLihatPaletStaging = new Vector3(-3.10f, 0.25f, -1.30f);

    // waktu kejadian di dalam klip (detik, sebelum dikali kecepatan)
    const float DurasiAmbil = 1.46f, SaatPegang = 0.55f;
    const float DurasiTaruh = 1.96f, SaatLepas = 0.9f;
    const float DurasiKembali = 0.96f;
    const float DurasiMasukSlot = 0.25f;  // detik nyata
    const float TinggiLeher = 0.17f;      // dari dasar botol ke leher

    Animator an;
    Transform grup, pegangan, tanganKiri, dada, titikInfeed;
    Transform petiMejaBody, petiRakGroup, templatKardus, paletStaging;
    Vector3 posGrup0, offsetBadan;
    Quaternion rotGrup0;
    float yawAwal, yaw;
    Vector3 posisi;
    readonly List<GameObject> botolPeti = new();
    readonly List<GameObject> kardusDiPalet = new();
    GameObject dibawa, petiDibawa, kardusDibawa;

    public ModeOperator ModeKerja { get; private set; } = ModeOperator.Setup;
    public string Tugas { get; private set; } = "Menunggu START";
    public int BatchKe { get; private set; }
    public int JumlahRestock { get; private set; }
    public int SisaPeti { get { int n = 0; foreach (var b in botolPeti) if (b.activeSelf) n++; return n; } }
    public int KapasitasPeti => botolPeti.Count;

    void Awake()
    {
        an = GetComponent<Animator>();
        an.applyRootMotion = false;
        grup = transform.parent;
        var root = grup.parent;
        pegangan = Cari(an.GetBoneTransform(HumanBodyBones.RightHand), "hand_hold_R");
        tanganKiri = an.GetBoneTransform(HumanBodyBones.LeftHand);
        dada = an.GetBoneTransform(HumanBodyBones.Chest);
        titikInfeed = Cari(root, "infeed_place_point");

        var grupPetiMeja = root.Find("Peti/bottle_crate");
        if (grupPetiMeja) petiMejaBody = grupPetiMeja.Find("bottle_crate");
        petiRakGroup = Cari(root, "shelf_filled_crate");
        templatKardus = Cari(root, "karton_pack_01");
        paletStaging = Cari(root, "pallet_empty");

        for (int i = 1; i <= 12; i++) botolPeti.Add(Cari(root, "crate_bottle_" + i.ToString("00")).gameObject);

        // posisi dan arah badan awal (pose bind), dipakai untuk menggerakkan grup secara kaku
        var hips = an.GetBoneTransform(HumanBodyBones.Hips);
        var kanan = an.GetBoneTransform(HumanBodyBones.RightHand).position - an.GetBoneTransform(HumanBodyBones.LeftHand).position;
        kanan.y = 0f;
        var depan = Vector3.Cross(kanan.normalized, Vector3.up);
        yawAwal = yaw = Mathf.Atan2(depan.x, depan.z) * Mathf.Rad2Deg;
        posGrup0 = grup.position; rotGrup0 = grup.rotation;
        posisi = new Vector3(hips.position.x, 0f, hips.position.z);
        offsetBadan = posisi - new Vector3(posGrup0.x, 0f, posGrup0.z);
    }

    void Start()
    {
        an.speed = kecepatanAnimasi;
        simulasi.infeedOtomatis = false;
        // Kondisi Awal (Initial State): Meja infeed di samping mesin KOSONG (belum ada peti & botol)
        // sedangkan peti berisi botol kosong berada di Rak Penyimpanan Besi (lantai atas sebelah kanan)
        SetPetiMejaAktif(false);
        if (petiRakGroup) petiRakGroup.gameObject.SetActive(true);
        StartCoroutine(Kerja());
    }

    void SetPetiMejaAktif(bool aktif)
    {
        if (petiMejaBody) petiMejaBody.gameObject.SetActive(aktif);
        foreach (var b in botolPeti) b.SetActive(aktif);
    }

    IEnumerator Kerja()
    {
        Main("idle");
        while (StatusMesin.I.Mode != ModeMesin.Berjalan) yield return null;

        // Rute awal dari depan ruangan menuju koridor belakang mesin
        Tugas = "Berjalan ke Rak Penyimpanan";
        yield return JalanKe(new Vector3(1.50f, 0f, 0.60f));
        yield return JalanKe(new Vector3(1.50f, 0f, -1.25f));

        while (true)
        {
            BatchKe++;
            float waktuMulaiBatch = Time.time;

            // =================================================================
            // TAHAP 1: SETUP / MATERIAL FETCH (Ambil Peti Botol dari Rak Besi)
            // =================================================================
            simulasi.BatchCompleted = false;
            simulasi.SetupSelesai = false;
            ModeKerja = ModeOperator.Setup;

            float waktuMulaiSetup = Time.time;
            yield return SiklusAmbilPetiDariRak(waktuMulaiSetup);
            simulasi.SetupTime = Time.time - waktuMulaiSetup;
            simulasi.SetupSelesai = true;

            // =================================================================
            // TAHAP 2: SIKLUS PRODUKSI MESIN (Infeed 12 Botol ke Turret)
            // =================================================================
            ModeKerja = ModeOperator.Infeed;
            yield return JalanKe(posisiKerja);

            while (SisaPeti > 0 || dibawa != null)
            {
                while (StatusMesin.I.Mode != ModeMesin.Berjalan)
                {
                    Main("idle");
                    yield return null;
                }

                if (!dibawa)
                {
                    var botol = BotolTerdekat();
                    if (!botol) break;

                    Tugas = "Infeed: Mengambil botol (" + SisaPeti + " sisa)";
                    yield return Hadap(botol.transform.position);
                    Main("pick");
                    yield return Tunggu(SaatPegang);
                    PegangBotol(botol);
                    yield return Tunggu(DurasiAmbil - SaatPegang);
                }

                Tugas = "Infeed: Menunggu slot turret";
                yield return Hadap(titikInfeed.position);
                Main("idle");
                while (!simulasi.BisaTerimaBotol(SaatLepas / kecepatanAnimasi + DurasiMasukSlot + 0.15f))
                {
                    simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;
                    yield return null;
                }

                Tugas = "Infeed: Menaruh botol ke turret";
                Main("place");
                yield return Tunggu(SaatLepas);
                yield return MasukkanKeSlot();
                yield return Tunggu(DurasiTaruh - SaatLepas - DurasiMasukSlot * kecepatanAnimasi);
                Main("return");
                yield return Tunggu(DurasiKembali);
                simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;
            }

            // Tunggu sampai seluruh botol dari peti selesai diisi, ditutup, dan dipilah ke Kotak OK / Reject
            Tugas = "Menunggu seluruh botol batch selesai diproses";
            yield return Hadap(titikInfeed.position);
            Main("idle");
            while (!simulasi.TurretDanJatuhKosong)
            {
                simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;
                yield return null;
            }

            // =================================================================
            // TAHAP 3: BATCH COMPLETED & SECONDARY PACKAGING KE KARDUS
            // =================================================================
            simulasi.BatchCompleted = true;
            ModeKerja = ModeOperator.Packaging;
            Tugas = "Pengemasan ke Kardus";
            StatusMesin.I.Kabar("BATCH #" + BatchKe + " COMPLETED! Operator memulai pengemasan ke kardus...");

            yield return SiklusPengemasanKardus(waktuMulaiBatch);

            simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;
            StatusMesin.I.Kabar("Batch #" + BatchKe + " selesai! Cycle Time: " + simulasi.BatchCycleTime.ToString("F1")
                + "s | FPY: " + simulasi.FirstPassYield.ToString("F1") + "%");

            yield return new WaitForSeconds(1.2f);

            // Siapkan kembali peti di rak untuk siklus batch berikutnya
            SetPetiMejaAktif(false);
            if (petiRakGroup) petiRakGroup.gameObject.SetActive(true);
        }
    }

    IEnumerator SiklusAmbilPetiDariRak(float t0)
    {
        Tugas = "Setup: Jalan ke Rak Penyimpanan";
        StatusMesin.I.Kabar("SETUP: Operator mengambil peti botol kosong dari rak penyimpanan...");

        yield return JalanKe(new Vector3(0.45f, 0f, -1.35f));
        yield return JalanKe(posisiRakPeti);
        yield return Hadap(titikLihatRakPeti);
        simulasi.SetupTime = Time.time - t0;

        // Animasi mengambil / mengangkat peti botol dari rak atas
        Tugas = "Setup: Mengangkat peti dari rak";
        Main("pick");
        yield return Tunggu(SaatPegang);
        if (petiRakGroup) petiRakGroup.gameObject.SetActive(false);
        BuatPetiDibawa();
        yield return Tunggu(DurasiAmbil - SaatPegang);
        simulasi.SetupTime = Time.time - t0;

        // Berjalan membawa peti menuju meja infeed di samping mesin
        Tugas = "Setup: Membawa peti ke Meja Infeed";
        yield return JalanKe(new Vector3(0.45f, 0f, -1.35f));
        yield return JalanKe(posisiTaruhPeti);
        yield return Hadap(titikLihatMejaPeti);
        simulasi.SetupTime = Time.time - t0;

        // Meletakkan peti di atas meja infeed
        Tugas = "Setup: Meletakkan peti di Meja Infeed";
        Main("place");
        yield return Tunggu(SaatLepas);
        if (petiDibawa) { Destroy(petiDibawa); petiDibawa = null; }
        SetPetiMejaAktif(true);
        JumlahRestock++;
        StatusMesin.I.Kabar("Peti botol diletakkan di meja infeed (12 botol siap). Siklus dimulai!");
        yield return Tunggu(DurasiTaruh - SaatPegang);
        Main("return");
        yield return Tunggu(DurasiKembali);
    }

    IEnumerator SiklusPengemasanKardus(float waktuMulaiBatch)
    {
        // 1. Berjalan ke area penyimpanan untuk mengambil kardus kemasan kosong
        Tugas = "Pengemasan ke Kardus (Ambil kardus)";
        yield return JalanKe(new Vector3(0.45f, 0f, -1.35f));
        yield return JalanKe(new Vector3(-1.60f, 0f, -1.35f));
        yield return JalanKe(posisiAmbilKardus);
        yield return Hadap(titikLihatKardus);
        simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;

        Main("pick");
        yield return Tunggu(SaatPegang);
        BuatKardusDibawa();
        yield return Tunggu(DurasiAmbil - SaatPegang);

        // 2. Berjalan membawa kardus ke dekat Kotak Penampung OK
        Tugas = "Pengemasan ke Kardus (Ke Kotak OK)";
        yield return JalanKe(new Vector3(-1.60f, 0f, -1.35f));
        yield return JalanKe(new Vector3(0.60f, 0f, -1.35f));
        yield return JalanKe(posisiKotakOk);
        yield return Hadap(titikLihatKotakOk);
        simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;

        // 3. Memindahkan botol-botol OK dari Kotak OK ke dalam kardus kemasan & menutup kardus
        Tugas = "Pengemasan ke Kardus (Isi & tutup kardus)";
        Main("place");
        yield return Tunggu(SaatLepas);
        int jmlOk = simulasi.KemasBotolOkKeKardus();
        StatusMesin.I.Kabar("Mengemas " + jmlOk + " botol OK ke dalam kardus kemasan...");
        yield return Tunggu(DurasiTaruh - SaatLepas);
        Main("pick");
        yield return Tunggu(DurasiAmbil);
        simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;

        // 4. Mengangkat kardus jadi dan memindahkannya ke Palet Samping (Finished Goods Staging Area)
        Tugas = "Pengemasan ke Kardus (Simpan ke Palet)";
        yield return JalanKe(new Vector3(0.60f, 0f, -1.35f));
        yield return JalanKe(posisiPaletStaging);
        yield return Hadap(titikLihatPaletStaging);
        simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;

        Main("place");
        yield return Tunggu(SaatLepas);
        TaruhKardusDiPalet();
        yield return Tunggu(DurasiTaruh - SaatLepas);
        Main("return");
        yield return Tunggu(DurasiKembali);
    }

    void BuatPetiDibawa()
    {
        if (petiDibawa) Destroy(petiDibawa);
        if (!petiRakGroup) return;
        petiDibawa = Instantiate(petiRakGroup.gameObject);
        petiDibawa.SetActive(true);
        petiDibawa.name = "peti_dibawa_operator";
        foreach (var c in petiDibawa.GetComponentsInChildren<Collider>()) Destroy(c);
        petiDibawa.transform.localScale = Vector3.one * 0.85f;
        // Dipegang di depan dada / kedua tangan operator
        Transform indukPegang = dada ? dada : pegangan;
        petiDibawa.transform.SetParent(indukPegang, false);
        petiDibawa.transform.localPosition = new Vector3(0f, -0.05f, -0.26f);
        petiDibawa.transform.localRotation = Quaternion.identity;
    }

    void BuatKardusDibawa()
    {
        if (kardusDibawa) Destroy(kardusDibawa);
        if (!templatKardus) return;
        kardusDibawa = Instantiate(templatKardus.gameObject);
        kardusDibawa.SetActive(true);
        kardusDibawa.name = "kardus_kemasan_dibawa";
        foreach (var c in kardusDibawa.GetComponentsInChildren<Collider>()) Destroy(c);
        kardusDibawa.transform.localScale = Vector3.one * 0.80f;
        Transform indukPegang = dada ? dada : pegangan;
        kardusDibawa.transform.SetParent(indukPegang, false);
        kardusDibawa.transform.localPosition = new Vector3(0f, -0.08f, -0.25f);
        kardusDibawa.transform.localRotation = Quaternion.identity;
    }

    void TaruhKardusDiPalet()
    {
        if (kardusDibawa)
        {
            Destroy(kardusDibawa);
            kardusDibawa = null;
        }
        if (!templatKardus) return;
        int idx = kardusDiPalet.Count;
        int kol = idx % 2, baris = (idx / 2) % 2, lapis = idx / 4;
        Vector3 pPalet = paletStaging ? paletStaging.position : new Vector3(-3.10f, 0f, -1.30f);
        Vector3 posSimpan = pPalet + new Vector3(-0.22f + kol * 0.44f, 0.144f + lapis * 0.305f, -0.18f + baris * 0.38f);

        var kardusJadi = Instantiate(templatKardus.gameObject, grup.parent);
        kardusJadi.name = "kardus_finished_goods_" + (idx + 1).ToString("00");
        kardusJadi.SetActive(true);
        kardusJadi.transform.SetPositionAndRotation(posSimpan, Quaternion.identity);
        kardusDiPalet.Add(kardusJadi);
    }

    // botol dilepas dari tangan dan diluncurkan ke slot infeed (leher di titik slot)
    IEnumerator MasukkanKeSlot()
    {
        var tr = dibawa.transform;
        tr.SetParent(null, true);
        Vector3 p0 = tr.position, p1 = titikInfeed.position - Vector3.up * TinggiLeher;
        Quaternion r0 = tr.rotation;
        for (float t = 0f; t < 1f; t += Time.deltaTime / DurasiMasukSlot)
        {
            tr.SetPositionAndRotation(Vector3.Lerp(p0, p1, Mathf.SmoothStep(0f, 1f, t)), Quaternion.Slerp(r0, Quaternion.identity, t));
            yield return null;
        }
        if (simulasi.TerimaBotol()) { Destroy(dibawa); dibawa = null; }
        else tr.SetParent(pegangan, true);   // slot keburu terisi / turret berputar: tetap dipegang
    }

    IEnumerator JalanKe(Vector3 tujuan)
    {
        tujuan.y = 0f;
        var arah = tujuan - posisi;
        if (arah.magnitude < 0.01f) yield break;
        yield return Hadap(tujuan);
        Main("walk");
        while ((tujuan - posisi).magnitude > 0.01f)
        {
            while (StatusMesin.I.Mode == ModeMesin.Darurat || !StatusMesin.I.AdaListrikKontrol)
            {
                Main("idle");
                yield return null;
                Main("walk");
            }
            posisi = Vector3.MoveTowards(posisi, tujuan, kecepatanJalan * Time.deltaTime);
            var d = tujuan - posisi;
            if (d.sqrMagnitude > 1e-6f)
                yaw = Mathf.MoveTowardsAngle(yaw, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, kecepatanPutar * Time.deltaTime);
            Terapkan();
            yield return null;
        }
        Main("idle");
    }

    IEnumerator Hadap(Vector3 target)
    {
        var d = target - posisi;
        float tujuan = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        while (Mathf.Abs(Mathf.DeltaAngle(yaw, tujuan)) > 0.5f)
        {
            yaw = Mathf.MoveTowardsAngle(yaw, tujuan, kecepatanPutar * Time.deltaTime);
            Terapkan();
            yield return null;
        }
        yaw = tujuan;
        Terapkan();
    }

    IEnumerator Tunggu(float detikKlip)
    {
        yield return new WaitForSeconds(detikKlip / kecepatanAnimasi);
    }

    void Main(string state) => an.CrossFadeInFixedTime(state, 0.15f);

    // grup digeser & diputar kaku supaya badan berada di `posisi` dan menghadap `yaw`
    void Terapkan()
    {
        var r = Quaternion.Euler(0f, yaw - yawAwal, 0f);
        var p = posisi - r * offsetBadan;
        grup.SetPositionAndRotation(new Vector3(p.x, posGrup0.y, p.z), r * rotGrup0);
    }

    GameObject BotolTerdekat()
    {
        GameObject terbaik = null;
        float jarak = float.MaxValue;
        foreach (var b in botolPeti)
        {
            if (!b.activeSelf) continue;
            float d = (b.transform.position - posisiKerja).sqrMagnitude;
            if (d < jarak) { jarak = d; terbaik = b; }
        }
        return terbaik;
    }

    void PegangBotol(GameObject botolPetiAsal)
    {
        botolPetiAsal.SetActive(false);
        dibawa = Instantiate(botolPetiAsal);
        dibawa.SetActive(true);
        dibawa.name = "botol_dibawa";
        foreach (var c in dibawa.GetComponentsInChildren<Collider>()) Destroy(c);
        // pivot botol ada di dasar; botol digenggam di lehernya
        dibawa.transform.SetPositionAndRotation(pegangan.position - Vector3.up * TinggiLeher, Quaternion.identity);
        dibawa.transform.SetParent(pegangan, true);
    }

    public void IsiUlangPeti()
    {
        SetPetiMejaAktif(true);
        simulasi.SetupSelesai = true;
        simulasi.BatchCompleted = false;
        StatusMesin.I.Kabar("Peti diisi ulang manual: " + botolPeti.Count + " botol");
    }

    static Transform Cari(Transform t, string n)
    {
        if (t.name == n) return t;
        foreach (Transform c in t) { var r = Cari(c, n); if (r) return r; }
        return null;
    }
}


