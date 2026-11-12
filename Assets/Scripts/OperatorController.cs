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
        OperatorSafetyWear.Pasang(an, yawAwal);
        posGrup0 = grup.position; rotGrup0 = grup.rotation;
        posisi = new Vector3(hips.position.x, 0f, hips.position.z);
        offsetBadan = posisi - new Vector3(posGrup0.x, 0f, posGrup0.z);
    }

    float KecepatanAktif => ModeKerja == ModeOperator.Infeed
        ? Mathf.Max(3.5f, Mathf.Max(kecepatanAnimasi, 4.38f / Mathf.Max(0.6f, simulasi.durasiPutar + simulasi.durasiDiam - 1.3f)))
        : 1f;
    GameObject kardusTerbuka;
    Renderer[] kulitKardus;
    Transform botolKemasan;
    bool kendaliTanganKemas;
    Vector3 targetGenggam;
    Quaternion rotasiBotolKemas;
    float bobotPegang, sudutMenunduk;

    void OnAnimatorIK(int layerIndex)
    {
        if (!an.isHuman) return;
        var barang = petiDibawa ? petiDibawa : kardusDibawa;
        bobotPegang = Mathf.MoveTowards(bobotPegang, barang ? 1f : 0f, Time.deltaTime * 4f);
        if (!barang) return;
        var bounds = BatasBarang(barang);
        var kanan = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
        Vector3 tengah = bounds.center + Vector3.up * bounds.extents.y * 0.25f;
        float lebar = Mathf.Abs(kanan.x) * bounds.extents.x + Mathf.Abs(kanan.z) * bounds.extents.z + 0.025f;
        float nunduk = kendaliTanganKemas ? Mathf.Clamp((an.GetBoneTransform(HumanBodyBones.Chest).position.y - targetGenggam.y) * 40f, 0f, 38f) : 0f;
        sudutMenunduk = Mathf.Lerp(sudutMenunduk, nunduk, 1f - Mathf.Exp(-12f * Time.deltaTime));
        var spine = an.GetBoneTransform(HumanBodyBones.Spine);
        if (spine && spine.parent)
        {
            Vector3 sumbu = spine.parent.InverseTransformDirection(kanan);
            an.SetBoneLocalRotation(HumanBodyBones.Spine, Quaternion.AngleAxis(sudutMenunduk, sumbu) * spine.localRotation);
        }
        an.SetLookAtWeight(kendaliTanganKemas ? 0.65f : 0f, 0.05f, 0.8f, 0.8f, 0.6f);
        if (kendaliTanganKemas) an.SetLookAtPosition(targetGenggam);
        AturTangan(AvatarIKGoal.LeftHand, tengah - kanan * lebar, bobotPegang);
        AturTangan(AvatarIKGoal.RightHand, kendaliTanganKemas
            ? targetGenggam - (pegangan.position - an.GetBoneTransform(HumanBodyBones.RightHand).position)
            : tengah + kanan * lebar, bobotPegang);
    }

    void LateUpdate()
    {
        // Posisi botol diwarisi dari genggaman setelah IK selesai, bukan dianimasikan sendiri.
        if (botolKemasan && botolKemasan.parent == pegangan)
            botolKemasan.rotation = rotasiBotolKemas;
    }

    IEnumerator GerakkanGenggaman(Vector3 tujuan, float durasi, Quaternion rotasi)
    {
        Vector3 awal = targetGenggam;
        Quaternion rotasiAwal = rotasiBotolKemas;
        for (float t = 0f; t < durasi * 0.85f;)
        {
            if (StatusMesin.I.Mode != ModeMesin.Berjalan) { yield return null; continue; }
            t += Time.deltaTime;
            float q = Mathf.Clamp01(t / (durasi * 0.85f));
            float u = q * q * q * (q * (q * 6f - 15f) + 10f);
            targetGenggam = Vector3.Lerp(awal, tujuan, u);
            rotasiBotolKemas = Quaternion.Slerp(rotasiAwal, rotasi, u);
            yield return null;
        }
        targetGenggam = tujuan;
        rotasiBotolKemas = rotasi;
        yield return new WaitForEndOfFrame();
    }

    void AturTangan(AvatarIKGoal tangan, Vector3 tujuan, float bobot)
    {
        an.SetIKPositionWeight(tangan, bobot);
        an.SetIKPosition(tangan, tujuan);
        bool kiri = tangan == AvatarIKGoal.LeftHand;
        var bahu = an.GetBoneTransform(kiri ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
        Vector3 samping = Quaternion.Euler(0f, yaw, 0f) * Vector3.right * (kiri ? -1f : 1f);
        var hint = kiri ? AvatarIKHint.LeftElbow : AvatarIKHint.RightElbow;
        an.SetIKHintPositionWeight(hint, bobot * 0.8f);
        an.SetIKHintPosition(hint, (bahu.position + tujuan) * 0.5f + samping * 0.18f - Vector3.up * 0.12f);
    }

    static Bounds BatasBarang(GameObject barang)
    {
        var renderers = barang.GetComponentsInChildren<Renderer>();
        var bounds = new Bounds(barang.transform.position, Vector3.zero);
        bool pertama = true;
        foreach (var renderer in renderers)
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
            if (pertama) { bounds = renderer.bounds; pertama = false; }
            else bounds.Encapsulate(renderer.bounds);
        }
        return bounds;
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


            // =================================================================
            // TAHAP 2: SIKLUS PRODUKSI MESIN (Infeed 12 Botol ke Turret)
            // =================================================================
            ModeKerja = ModeOperator.Infeed;
            yield return JalanKe(posisiKerja);
            simulasi.SetupSelesai = true;

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
                while (!simulasi.BisaTerimaBotol(SaatLepas / KecepatanAktif + DurasiMasukSlot + 0.15f))
                {
                    simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;
                    yield return null;
                }

                Tugas = "Infeed: Menaruh botol ke turret";
                Main("place");
                yield return Tunggu(SaatLepas);
                yield return MasukkanKeSlot();
                yield return Tunggu(DurasiTaruh - SaatLepas - DurasiMasukSlot * KecepatanAktif);
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
        Main("idle");
        // Kardus di sisi kiri, sehingga tangan kanan bebas menjangkau tampungan.
        if (kardusDibawa) kardusDibawa.transform.position -= Quaternion.Euler(0f, yaw, 0f) * Vector3.right * 0.24f;
        BukaKardusKemasan();
        var botolOk = simulasi.BotolOkUntukDikemas();
        int nomor = 0;
        foreach (var botol in botolOk)
        {
            if (!botol || !kardusDibawa) continue;
            Tugas = "Mengemas botol OK " + (++nomor) + "/" + botolOk.Length;
            var batas = BatasBarang(kardusDibawa);
            Vector3 posisiAmbil = botol.transform.position;
            Quaternion rotasiAmbil = botol.transform.rotation;
            targetGenggam = pegangan.position;
            rotasiBotolKemas = rotasiAmbil;
            kendaliTanganKemas = true;
            // Lewat sisi kanan kardus dulu, baru turun ke botol di tampungan.
            Vector3 samping = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            float lebar = Mathf.Abs(samping.x) * batas.extents.x + Mathf.Abs(samping.z) * batas.extents.z;
            float tinggiAman = Mathf.Max(batas.max.y + 0.23f, posisiAmbil.y + 0.23f);
            Vector3 luar = batas.center + samping * (lebar + 0.14f);
            luar.y = tinggiAman;
            if (nomor == 1) yield return GerakkanGenggaman(luar, 0.3f, rotasiAmbil);
            Vector3 atasTampungan = posisiAmbil; atasTampungan.y = tinggiAman;
            yield return GerakkanGenggaman(atasTampungan, 0.35f, rotasiAmbil);
            yield return GerakkanGenggaman(posisiAmbil, 0.35f, rotasiAmbil);
            // Genggam pada leher botol. Botol mengikuti tulang tangan sejak titik ini.
            botolKemasan = botol.transform;
            botolKemasan.SetParent(pegangan, true);
            botolKemasan.localPosition = Vector3.zero;
            yield return new WaitForSeconds(0.08f);
            yield return GerakkanGenggaman(atasTampungan, 0.4f, Quaternion.identity);

            Vector3 atasKardus = batas.center; atasKardus.y = tinggiAman;
            yield return GerakkanGenggaman(atasKardus, 0.45f, Quaternion.identity);
            Vector3 masuk = batas.center; masuk.y = batas.max.y + 0.08f;
            yield return GerakkanGenggaman(masuk, 0.4f, Quaternion.identity);
            // Lepas setelah botol masuk dari bukaan atas; penghitung tetap diselesaikan sekali per kardus.
            botolKemasan.SetParent(kardusDibawa.transform, true);
            botol.SetActive(false);
            botolKemasan = null;
            yield return GerakkanGenggaman(atasKardus, 0.3f, Quaternion.identity);
            simulasi.BatchCycleTime = Time.time - waktuMulaiBatch;
        }
        if (kardusDibawa)
        {
            var b = BatasBarang(kardusDibawa);
            var kanan = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            float lebar = Mathf.Abs(kanan.x) * b.extents.x + Mathf.Abs(kanan.z) * b.extents.z;
            Vector3 luar = b.center + kanan * (lebar + 0.06f); luar.y = targetGenggam.y;
            yield return GerakkanGenggaman(luar, 0.25f, Quaternion.identity);
            yield return GerakkanGenggaman(b.center + kanan * (lebar + 0.025f), 0.25f, Quaternion.identity);
        }
        kendaliTanganKemas = false;
        TutupKardusKemasan();
        Tugas = "Pengemasan ke Kardus (Menutup kardus)";
        Main("place");
        yield return Tunggu(DurasiTaruh);
        int jmlOk = simulasi.KemasBotolOkKeKardus();
        StatusMesin.I.Kabar(jmlOk + " botol OK selesai dikemas satu per satu.");
        Main("idle");
        yield return new WaitForSeconds(0.5f);

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
        petiDibawa.transform.SetParent(grup, true);
        var arah = Quaternion.Euler(0f, yaw, 0f);
        var hips = an.GetBoneTransform(HumanBodyBones.Hips);
        petiDibawa.transform.SetPositionAndRotation(hips.position + Vector3.up * 0.16f + arah * Vector3.forward * 0.32f, arah);

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
        kardusDibawa.transform.SetParent(grup, true);
        var arah = Quaternion.Euler(0f, yaw, 0f);
        var hips = an.GetBoneTransform(HumanBodyBones.Hips);
        kardusDibawa.transform.SetPositionAndRotation(hips.position + Vector3.up * 0.04f + arah * Vector3.forward * 0.42f, arah);

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

    void BukaKardusKemasan()
    {
        if (!kardusDibawa) return;
        Bounds b = BatasBarang(kardusDibawa);
        kulitKardus = kardusDibawa.GetComponentsInChildren<Renderer>();
        Material material = kulitKardus.Length > 0 ? kulitKardus[0].sharedMaterial : null;
        foreach (var renderer in kulitKardus) renderer.enabled = false;
        kardusTerbuka = new GameObject("kardus_terbuka");
        kardusTerbuka.transform.SetParent(kardusDibawa.transform, true);
        float x = Mathf.Max(0.26f, b.size.x), z = Mathf.Max(0.22f, b.size.z);
        float y = Mathf.Max(0.22f, b.size.y), tebal = 0.008f;
        Vector3 pusat = b.center;
        Vector3[] offsets = { new Vector3(0,-y/2,0), new Vector3(-x/2,0,0), new Vector3(x/2,0,0), new Vector3(0,0,-z/2), new Vector3(0,0,z/2) };
        Vector3[] ukuran = { new Vector3(x,tebal,z), new Vector3(tebal,y,z), new Vector3(tebal,y,z), new Vector3(x,y,tebal), new Vector3(x,y,tebal) };
        for (int i = 0; i < offsets.Length; i++)
        {
            var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(panel.GetComponent<Collider>());
            panel.transform.SetPositionAndRotation(pusat + offsets[i], Quaternion.identity);
            panel.transform.localScale = ukuran[i];
            panel.transform.SetParent(kardusTerbuka.transform, true);
            panel.GetComponent<Renderer>().sharedMaterial = material;
        }
    }

    void TutupKardusKemasan()
    {
        if (kardusTerbuka) Destroy(kardusTerbuka);
        if (kulitKardus != null)
            foreach (var renderer in kulitKardus) if (renderer) renderer.enabled = true;
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
        yield return new WaitForSeconds(Mathf.Max(0f, detikKlip) / KecepatanAktif);
    }

    void Main(string state) { an.speed = KecepatanAktif; an.CrossFadeInFixedTime(state, 0.12f); }

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



// Aksesori mengikuti tulang kepala, termasuk saat berjalan dan membungkuk.
static class OperatorSafetyWear
{
    public static void Pasang(Animator animator, float yaw)
    {
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (!head || head.Find("SafetyWear")) return;
        var invisible = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        invisible.SetColor("_BaseColor", new Color(0,0,0,0));
        invisible.SetFloat("_Surface", 1); invisible.SetFloat("_ZWrite", 0);
        invisible.SetFloat("_SrcBlend", 5); invisible.SetFloat("_DstBlend", 10);
        invisible.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); invisible.renderQueue = 3000;
        invisible.SetOverrideTag("RenderType", "Transparent");
        invisible.SetShaderPassEnabled("ShadowCaster", false);
        foreach (var renderer in animator.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
                if (materials[i] && (materials[i].name.Contains("mat_plastic_white") || materials[i].name.Contains("mat_helmet")))
                    materials[i] = invisible;
            renderer.sharedMaterials = materials;
        }
        var root = new GameObject("SafetyWear").transform;
        root.SetPositionAndRotation(head.position, Quaternion.Euler(0f, yaw, 0f));
        root.SetParent(head, true);
        TambahWajah(root);
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        var kuning = new Material(shader) { name = "Helmet safety yellow" };
        kuning.SetColor("_BaseColor", new Color(1f, 0.72f, 0.04f));
        kuning.SetFloat("_Smoothness", 0.35f);
        var hitam = new Material(shader) { name = "Safety glasses frame" };
        hitam.SetColor("_BaseColor", new Color(0.025f, 0.035f, 0.05f));
        var bening = new Material(shader) { name = "Safety clear lenses" };
        bening.SetColor("_BaseColor", new Color(0.75f, 0.92f, 1f, 0.18f));
        bening.SetFloat("_Surface", 1); bening.SetFloat("_ZWrite", 0);
        bening.SetFloat("_SrcBlend", 5); bening.SetFloat("_DstBlend", 10);
        bening.SetFloat("_Cull", 0); bening.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        bening.SetOverrideTag("RenderType", "Transparent"); bening.renderQueue = 3000;
        var verts = new List<Vector3>(); var triangles = new List<int>();
        const int rings = 10, segments = 32;
        for (int r = 0; r <= rings; r++)
        {
            float theta = (float)r / rings * Mathf.PI * 0.5f;
            for (int j = 0; j <= segments; j++)
            {
                float phi = (float)j / segments * Mathf.PI * 2f;
                verts.Add(new Vector3(0.115f * Mathf.Sin(theta) * Mathf.Cos(phi), 0.135f + 0.105f * Mathf.Cos(theta), 0.125f * Mathf.Sin(theta) * Mathf.Sin(phi)));
            }
        }
        for (int r = 0; r < rings; r++) for (int j = 0; j < segments; j++)
        {
            int a = r * (segments + 1) + j, b = a + segments + 1;
            triangles.Add(a); triangles.Add(a + 1); triangles.Add(b);
            triangles.Add(a + 1); triangles.Add(b + 1); triangles.Add(b);
        }
        var shell = new GameObject("Safety helmet shell"); shell.transform.SetParent(root, false);
        var mesh = new Mesh { name = "Hardhat dome" }; mesh.SetVertices(verts); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
        shell.AddComponent<MeshFilter>().sharedMesh = mesh;
        shell.AddComponent<MeshRenderer>().sharedMaterial = kuning;
        Piece(root, "Helmet rim", PrimitiveType.Cylinder, new Vector3(0,0.133f,0.015f), new Vector3(0.25f,0.004f,0.30f), kuning);
        Piece(root, "Front visor", PrimitiveType.Sphere, new Vector3(0,0.132f,0.105f), new Vector3(0.23f,0.012f,0.145f), kuning);
        Piece(root, "Crown reinforcement", PrimitiveType.Sphere, new Vector3(0,0.213f,0), new Vector3(0.022f,0.06f,0.205f), kuning);
        for (int side = -1; side <= 1; side += 2)
        {
            Piece(root, "Clear safety lens", PrimitiveType.Sphere, new Vector3(side*0.044f,0.076f,0.094f), new Vector3(0.091f,0.054f,0.014f), bening);
            Piece(root, "Upper lens frame", PrimitiveType.Cube, new Vector3(side*0.044f,0.105f,0.094f), new Vector3(0.092f,0.008f,0.012f), hitam);
            Piece(root, "Temple arm", PrimitiveType.Cube, new Vector3(side*0.091f,0.097f,0.034f), new Vector3(0.009f,0.009f,0.13f), hitam);
        }
        Piece(root, "Nose bridge", PrimitiveType.Cube, new Vector3(0,0.093f,0.10f), new Vector3(0.021f,0.008f,0.011f), hitam);
    }
    static void TambahWajah(Transform head)
    {
        var texture = Resources.Load<Texture2D>("OperatorFaceReference");
        if (!texture) return;
        var vertices = new List<Vector3>(); var uv = new List<Vector2>(); var indices = new List<int>();
        const int rings = 8, segments = 40;
        for (int ring = 0; ring <= rings; ring++)
        {
            float radius = (float)ring / rings;
            for (int j = 0; j <= segments; j++)
            {
                float angle = (float)j / segments * Mathf.PI * 2f;
                float x = radius * Mathf.Cos(angle), y = radius * Mathf.Sin(angle);
                vertices.Add(new Vector3(x * 0.078f, 0.055f + y * 0.103f, 0.057f + 0.046f * Mathf.Sqrt(Mathf.Max(0, 1f-radius*radius))));
                uv.Add(new Vector2(0.50f + x * 0.285f, 0.55f + y * 0.25f));
            }
        }
        for (int ring = 0; ring < rings; ring++) for (int j = 0; j < segments; j++)
        {
            int a=ring*(segments+1)+j, b=a+segments+1;
            indices.Add(a); indices.Add(b); indices.Add(b+1);
            indices.Add(a); indices.Add(b+1); indices.Add(a+1);
        }
        var mesh = new Mesh { name = "Face-only oval surface" };
        mesh.SetVertices(vertices); mesh.SetUVs(0,uv); mesh.SetTriangles(indices,0); mesh.RecalculateNormals();
        var go = new GameObject("Face reference"); go.transform.SetParent(head,false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        material.SetTexture("_BaseMap",texture); material.SetColor("_BaseColor",Color.white);
        material.SetFloat("_Smoothness",0.05f); material.SetFloat("_Cull",0);
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    static void Piece(Transform parent, string name, PrimitiveType shape, Vector3 pos, Vector3 scale, Material material)
    {
        var go = GameObject.CreatePrimitive(shape); go.name = name;
        Object.Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false); go.transform.localPosition = pos; go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = material;
    }
}
