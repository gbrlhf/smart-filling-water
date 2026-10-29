using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Operator mesin: setelah START pertama, berjalan ke belakang mesin, lalu terus
// mengambil botol kosong dari peti dan menaruhnya di slot infeed saat turret berhenti.
// Animasi Humanoid diputar di tempat; posisi & arah digerakkan skrip lewat grup induknya.
[RequireComponent(typeof(Animator))]
public class OperatorController : MonoBehaviour
{
    public MesinSimulasi simulasi;

    [Header("Gerak")]
    public float kecepatanJalan = 0.9f;
    public float kecepatanPutar = 220f;   // derajat per detik
    public float kecepatanAnimasi = 1.2f;

    [Tooltip("Titik jalan dari posisi awal ke titik berdiri kerja (y diabaikan)")]
    public Vector3[] rute =
    {
        new Vector3(1.50f, 0f, 0.60f),
        new Vector3(1.50f, 0f, -1.15f),
        new Vector3(0.47f, 0f, -1.15f),
    };

    [Tooltip("Tempat berdiri kerja: dari sini tangan kanan tepat di atas slot infeed saat animasi place")]
    public Vector3 posisiKerja = new Vector3(0.37f, 0f, -0.86f);

    // waktu kejadian di dalam klip (detik, sebelum dikali kecepatan)
    const float DurasiAmbil = 1.46f, SaatPegang = 0.55f;
    const float DurasiTaruh = 1.96f, SaatLepas = 0.9f;
    const float DurasiKembali = 0.96f;
    const float DurasiMasukSlot = 0.25f;  // detik nyata
    const float TinggiLeher = 0.17f;      // dari dasar botol ke leher

    Animator an;
    Transform grup, pegangan, titikInfeed;
    Vector3 posGrup0, offsetBadan;
    Quaternion rotGrup0;
    float yawAwal, yaw;
    Vector3 posisi;
    readonly List<GameObject> botolPeti = new();
    GameObject dibawa;

    public string Tugas { get; private set; } = "Menunggu START";
    public int SisaPeti { get { int n = 0; foreach (var b in botolPeti) if (b.activeSelf) n++; return n; } }
    public int KapasitasPeti => botolPeti.Count;

    void Awake()
    {
        an = GetComponent<Animator>();
        an.applyRootMotion = false;
        grup = transform.parent;
        var root = grup.parent;
        pegangan = Cari(an.GetBoneTransform(HumanBodyBones.RightHand), "hand_hold_R");
        titikInfeed = Cari(root, "infeed_place_point");
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
        StartCoroutine(Kerja());
    }

    IEnumerator Kerja()
    {
        Main("idle");
        while (StatusMesin.I.Mode != ModeMesin.Berjalan) yield return null;

        Tugas = "Berjalan ke posisi kerja";
        foreach (var t in rute) yield return JalanKe(t);
        yield return JalanKe(posisiKerja);

        while (true)
        {
            if (!dibawa)
            {
                var botol = BotolTerdekat();
                if (!botol)
                {
                    Tugas = "Peti kosong";
                    StatusMesin.I.Kabar("Peti botol kosong: isi ulang peti");
                    Main("idle");
                    while (!BotolTerdekat()) yield return null;
                    continue;
                }
                Tugas = "Mengambil botol";
                yield return Hadap(botol.transform.position);
                Main("pick");
                yield return Tunggu(SaatPegang);
                Pegang(botol);
                yield return Tunggu(DurasiAmbil - SaatPegang);
            }

            Tugas = "Menunggu slot infeed";
            yield return Hadap(titikInfeed.position);
            Main("idle");
            while (!simulasi.BisaTerimaBotol(SaatLepas / kecepatanAnimasi + DurasiMasukSlot + 0.15f)) yield return null;

            Tugas = "Menaruh botol";
            Main("place");
            yield return Tunggu(SaatLepas);
            yield return MasukkanKeSlot();
            yield return Tunggu(DurasiTaruh - SaatLepas - DurasiMasukSlot * kecepatanAnimasi);
            Main("return");
            yield return Tunggu(DurasiKembali);
        }
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

    void Pegang(GameObject botolPetiAsal)
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
        foreach (var b in botolPeti) b.SetActive(true);
        StatusMesin.I.Kabar("Peti diisi ulang: " + botolPeti.Count + " botol");
    }

    static Transform Cari(Transform t, string n)
    {
        if (t.name == n) return t;
        foreach (Transform c in t) { var r = Cari(c, n); if (r) return r; }
        return null;
    }
}
