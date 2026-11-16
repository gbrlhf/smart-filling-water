using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// Pengiriman satu arah ke MES API. Tidak mengendalikan proses mesin.
public class FillingMachineApi : MonoBehaviour
{
    public string baseUrl = "https://filling-machine-api.zamazzam.me";
    public string mesinId = "mesin-01";
    public string pengguna = "naya";
    [Min(1f)] public float intervalTelemetri = 2f;
    public bool kirimAktif = true;
    [SerializeField] string statusKoneksi = "Belum tersambung";
    public string StatusKoneksi => statusKoneksi;
    public int Antrean => antrean.Count;
    public string SesiId { get; private set; }
    readonly List<Paket> antrean = new List<Paket>();
    Paket sedangDikirim;
    MesinSimulasi mesin;
    StatusMesin status;
    OperatorController operatorMesin;
    string fileAntrean;
    string apiKey;
    ModeMesin modeTerakhir;
    float telemetriBerikut;
    public static string Utc() => DateTime.UtcNow.ToString("O");

    [Serializable] class Health { public string status, database; }
    [Serializable] public class Paket { public string endpoint, json; }
    [Serializable] class Simpanan { public List<Paket> paket = new List<Paket>(); }
    [Serializable] public class MCB { public bool utama, mesin, kontrol, lampu; }
    [Serializable] public class GPIO { public bool step, dir, en, pompa, buzzer, ir, start, limit; }
    [Serializable] public class Telemetri
    {
        public string waktu, mesin_id, sesi_id, pengguna, id_kirim, mode, tahap, operator_tugas;
        public bool darurat;
        public MCB mcb;
        public GPIO gpio;
        public int langkah, botol_selesai, isi_kotak, sisa_peti;
        public float sudut_turret_deg;
        public int botol_ok, botol_reject, reject_isi_kurang, reject_tanpa_tutup, total_dikemas, kardus_selesai;
        public float first_pass_yield, setup_time, batch_cycle_time, starvation_downtime;
    }
    [Serializable] public class EventData
    {
        public string waktu, mesin_id, sesi_id, pengguna, id_kirim, jenis, sumber, mode_sebelum, mode_sesudah, pesan;
    }
    [Serializable] public class BotolData
    {
        public string mesin_id, sesi_id, pengguna, id_kirim;
        public int nomor, slot;
        public string waktu_masuk, waktu_isi_mulai, waktu_isi_selesai, waktu_tutup, waktu_keluar;
        public float level_isi;
        public bool bertutup;
        public string status;
    }

    void Start()
    {
        mesin = GetComponent<MesinSimulasi>();
        status = StatusMesin.I;
        operatorMesin = FindAnyObjectByType<OperatorController>();
        if (!mesin || !status) { enabled = false; return; }
        // Rahasia tidak diserialisasi ke scene maupun repository.
        apiKey = Environment.GetEnvironmentVariable("FILLING_MACHINE_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = Environment.GetEnvironmentVariable("API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            string keyFile = Path.Combine(Application.persistentDataPath, "filling-machine-api-key.txt");
            try { if (File.Exists(keyFile)) apiKey = File.ReadAllText(keyFile).Trim(); }
            catch (Exception) { Debug.LogWarning("API: file kunci tidak dapat dibaca."); }
        }
        SesiId = Guid.NewGuid().ToString("N");
        fileAntrean = Path.Combine(Application.persistentDataPath, "filling-machine-api-outbox.json");
        try
        {
            if (File.Exists(fileAntrean))
            {
                var data = JsonUtility.FromJson<Simpanan>(File.ReadAllText(fileAntrean));
                if (data == null || data.paket == null) throw new InvalidDataException("Format antrean tidak valid");
                antrean.AddRange(data.paket);
            }
        }
        catch (Exception e)
        {
            statusKoneksi = "Antrean lokal tidak dapat dibaca; pengiriman dinonaktifkan";
            Debug.LogError("API: antrean lama dipertahankan: " + e.Message);
            fileAntrean = null; enabled = false; return;
        }
        modeTerakhir = status.Mode;
        status.ModeBerubah += ModeBerubah;
        status.PesanDicatat += PesanDicatat;
        mesin.BotolSelesaiDiproses += BotolSelesai;
        CatatEvent("sesi_mulai", "sistem", "Sesi Unity dimulai", status.Mode, status.Mode);
        StartCoroutine(Pengirim());
    }

    void Update()
    {
        if (!mesin || !status || !kirimAktif || Time.realtimeSinceStartup < telemetriBerikut) return;
        telemetriBerikut = Time.realtimeSinceStartup + Mathf.Max(1f, intervalTelemetri);
        var data = new Telemetri {
            waktu = Utc(), mesin_id = mesinId, sesi_id = SesiId, pengguna = pengguna, id_kirim = Guid.NewGuid().ToString("N"),
            mode = status.Mode.ToString(), darurat = status.Darurat, tahap = mesin.Tahap,
            operator_tugas = operatorMesin ? operatorMesin.Tugas : "", langkah = mesin.LangkahApi,
            sudut_turret_deg = mesin.SudutApi, botol_selesai = mesin.Selesai + mesin.TotalReject,
            isi_kotak = mesin.IsiKotak, sisa_peti = operatorMesin ? operatorMesin.SisaPeti : 0,
            mcb = new MCB { utama = status.mcbUtama, mesin = status.mcbMesin, kontrol = status.mcbKontrol, lampu = status.mcbLampu },
            gpio = mesin.GpioApi(), botol_ok = mesin.Selesai, botol_reject = mesin.TotalReject,
            reject_isi_kurang = mesin.RejectIsiKurang, reject_tanpa_tutup = mesin.RejectTanpaTutup,
            total_dikemas = mesin.TotalDikemas, kardus_selesai = mesin.KardusSelesai,
            first_pass_yield = mesin.FirstPassYield, setup_time = mesin.SetupTime,
            batch_cycle_time = mesin.BatchCycleTime, starvation_downtime = mesin.StarvationDowntime
        };
        // Saat offline, pertahankan snapshot terbaru saja; event dan botol tidak dibuang.
        antrean.RemoveAll(p => p.endpoint == "/api/telemetri" && p != sedangDikirim);
        Tambah("/api/telemetri", "[" + JsonUtility.ToJson(data) + "]");
    }

    void ModeBerubah(ModeMesin mode)
    {
        string jenis = mode == ModeMesin.Darurat ? "estop_tekan" : mode == ModeMesin.Berjalan ? "START" : "mode_berubah";
        CatatEvent(jenis, "sistem", "Mode mesin berubah", modeTerakhir, mode);
        modeTerakhir = mode;
    }
    void PesanDicatat(string pesan)
    {
        string jenis = pesan.StartsWith("E-stop dilepas") ? "estop_lepas" : pesan.StartsWith("MCB ") ? "mcb_berubah" : "pesan_mesin";
        CatatEvent(jenis, "sistem", pesan, status.Mode, status.Mode);
    }
    void CatatEvent(string jenis, string sumber, string pesan, ModeMesin sebelum, ModeMesin sesudah)
    {
        if (!kirimAktif) return;
        Tambah("/api/event", JsonUtility.ToJson(new EventData { waktu = Utc(), mesin_id = mesinId, sesi_id = SesiId,
            pengguna = pengguna, id_kirim = Guid.NewGuid().ToString("N"), jenis = jenis, sumber = sumber,
            mode_sebelum = sebelum.ToString(), mode_sesudah = sesudah.ToString(), pesan = pesan }));
    }
    void BotolSelesai(MesinSimulasi.Botol botol)
    {
        if (!kirimAktif) return;
        var data = new BotolData { mesin_id = mesinId, sesi_id = SesiId,
            pengguna = pengguna, id_kirim = Guid.NewGuid().ToString("N"), nomor = botol.nomorApi, slot = botol.slotApi,
            waktu_masuk = botol.masukApi, waktu_isi_mulai = botol.isiMulaiApi, waktu_isi_selesai = botol.isiSelesaiApi,
            waktu_tutup = botol.tutupApi, waktu_keluar = Utc(), level_isi = botol.isi, bertutup = botol.bertutup,
            status = botol.status == StatusProduk.OK ? "OK" : botol.status == StatusProduk.Reject_IsiKurang ? "TIDAK_PENUH" : "TANPA_TUTUP" };
        string json = JsonUtility.ToJson(data);
        foreach (var key in new[] { "waktu_isi_mulai", "waktu_isi_selesai", "waktu_tutup" })
            json = json.Replace("\"" + key + "\":\"\",", "");
        Tambah("/api/botol", json);
    }
    void Tambah(string endpoint, string json)
    {
        antrean.Add(new Paket { endpoint = endpoint, json = json });
        Simpan();
    }
    void Simpan()
    {
        if (string.IsNullOrEmpty(fileAntrean)) return;
        try
        {
            string temp = fileAntrean + ".tmp";
            File.WriteAllText(temp, JsonUtility.ToJson(new Simpanan { paket = antrean }));
            if (File.Exists(fileAntrean)) File.Replace(temp, fileAntrean, fileAntrean + ".bak");
            else File.Move(temp, fileAntrean);
        }
        catch (Exception e) { Debug.LogWarning("API: gagal menyimpan antrean: " + e.Message); }
    }
    IEnumerator Pengirim()
    {
        float jeda = 2f;
        while (true)
        {
            if (!kirimAktif || antrean.Count == 0) { yield return new WaitForSecondsRealtime(1f); continue; }
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var alamat) || alamat.Scheme != "https")
            { statusKoneksi = "URL API harus HTTPS"; yield return new WaitForSecondsRealtime(10f); continue; }
            // Health memeriksa database juga. Jangan terus POST ketika backend gagal autentikasi.
            using (var health = UnityWebRequest.Get(baseUrl.TrimEnd('/') + "/api/health"))
            {
                health.timeout = 10;
                yield return health.SendWebRequest();
                if (health.result != UnityWebRequest.Result.Success)
                {
                    statusKoneksi = "API/database belum siap (HTTP " + health.responseCode + ")";
                    yield return new WaitForSecondsRealtime(jeda);
                    jeda = Mathf.Min(60f, jeda * 2f);
                    continue;
                }
                Health kondisi = null;
                try { kondisi = JsonUtility.FromJson<Health>(health.downloadHandler.text); } catch (ArgumentException) { }
                if (kondisi == null || kondisi.status == "error" || kondisi.database == "terputus")
                {
                    statusKoneksi = "Database API belum siap";
                    yield return new WaitForSecondsRealtime(30f);
                    continue;
                }
            }
            sedangDikirim = antrean[0];
            using (var request = new UnityWebRequest(baseUrl.TrimEnd('/') + sedangDikirim.endpoint, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sedangDikirim.json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrWhiteSpace(apiKey)) request.SetRequestHeader("X-Api-Key", apiKey);
                request.timeout = 10;
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    antrean.Remove(sedangDikirim); Simpan(); jeda = 2f;
                    statusKoneksi = "Terhubung; antrean " + antrean.Count;
                }
                else
                {
                    statusKoneksi = "Pengiriman tertunda (HTTP " + request.responseCode + ")";
                    Debug.LogWarning("API " + sedangDikirim.endpoint + ": " + request.responseCode + " " + request.error);
                    jeda = Mathf.Min(60f, jeda * 2f);
                }
            }
            sedangDikirim = null;
            yield return new WaitForSecondsRealtime(jeda == 2f ? 0.1f : jeda);
        }
    }
    void OnDestroy()
    {
        if (status) { status.ModeBerubah -= ModeBerubah; status.PesanDicatat -= PesanDicatat; }
        if (mesin) mesin.BotolSelesaiDiproses -= BotolSelesai;
        Simpan();
    }
}
