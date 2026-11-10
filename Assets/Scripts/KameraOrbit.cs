using UnityEngine;
using UnityEngine.InputSystem;

// Kamera orbit untuk melihat mesin pengisi botol.
// Klik kanan + geser: putar | Klik tengah + geser: geser | Scroll: zoom
// WASD / Q E: pindah titik fokus | 1-5: sudut tetap | H: sembunyikan bantuan
[RequireComponent(typeof(Camera))]
public class KameraOrbit : MonoBehaviour
{
    [System.Serializable]
    public struct Sudut
    {
        public string nama;
        public Vector3 fokus;
        public float yaw, pitch, jarak;

        public Sudut(string nama, Vector3 fokus, float yaw, float pitch, float jarak)
        {
            this.nama = nama; this.fokus = fokus; this.yaw = yaw; this.pitch = pitch; this.jarak = jarak;
        }
    }

    public Sudut[] sudut =
    {
        new Sudut("Ruangan",       new Vector3(0f, 0.80f, -0.20f),    230f, 30f, 4.2f),
        new Sudut("Mesin",         new Vector3(0f, 1.00f, -0.25f),    200f, 30f, 1.2f),
        new Sudut("Control box",   new Vector3(-0.90f, 0.85f, -0.43f), 150f, 45f, 0.65f),
        new Sudut("Capping",       new Vector3(0.14f, 1.05f, -0.08f), 235f, 20f, 0.75f),
        new Sudut("Panel listrik", new Vector3(-3.70f, 1.40f, 2.00f), 270f, 10f, 1.2f),
    };
    public int sudutAwal = 0;

    [Header("Kontrol")]
    public float kecepatanPutar = 0.25f;   // derajat per piksel
    public float kecepatanGeser = 0.0015f; // per piksel per meter jarak
    public float kecepatanZoom = 0.0015f;
    public float kecepatanJalan = 1.5f;    // m/s untuk WASD
    public float jarakMin = 0.25f, jarakMax = 7f;
    public float pitchMin = -10f, pitchMax = 85f;
    public float kehalusan = 10f;          // makin besar makin cepat menyusul

    [Header("Batas ruangan (kamera tidak menembus dinding)")]
    public Vector3 batasMin = new Vector3(-3.60f, 0.20f, -2.80f);
    public Vector3 batasMax = new Vector3(3.60f, 3.00f, 2.40f);

    public bool tampilkanBantuan = true;

    Vector3 fokus, fokusTarget;
    float yaw, pitch, jarak, yawTarget, pitchTarget, jarakTarget;
    int aktif;

    void Start()
    {
        PilihSudut(sudutAwal, true);
    }

    public void PilihSudut(int i, bool langsung)
    {
        if (i < 0 || i >= sudut.Length) return;
        aktif = i;
        var s = sudut[i];
        fokusTarget = s.fokus; pitchTarget = s.pitch; jarakTarget = s.jarak;
        // putar lewat jalur terpendek
        yawTarget = yaw + Mathf.DeltaAngle(yaw, s.yaw);
        if (langsung)
        {
            fokus = fokusTarget; yaw = yawTarget; pitch = pitchTarget; jarak = jarakTarget;
            Terapkan();
        }
    }

    [ContextMenu("Terapkan sudut awal")]
    void TerapkanSudutAwal() => PilihSudut(sudutAwal, true);

    void Update()
    {
        BacaInput();
        float t = 1f - Mathf.Exp(-kehalusan * Time.unscaledDeltaTime);
        fokus = Vector3.Lerp(fokus, fokusTarget, t);
        yaw = Mathf.Lerp(yaw, yawTarget, t);
        pitch = Mathf.Lerp(pitch, pitchTarget, t);
        jarak = Mathf.Lerp(jarak, jarakTarget, t);
        Terapkan();
    }

    void BacaInput()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;

        if (kb != null)
        {
            for (int i = 0; i < sudut.Length && i < 9; i++)
                if (kb[Key.Digit1 + i].wasPressedThisFrame) PilihSudut(i, false);
            if (kb.hKey.wasPressedThisFrame) tampilkanBantuan = !tampilkanBantuan;

            var gerak = Vector3.zero;
            if (kb.wKey.isPressed) gerak.z += 1f;
            if (kb.sKey.isPressed) gerak.z -= 1f;
            if (kb.dKey.isPressed) gerak.x += 1f;
            if (kb.aKey.isPressed) gerak.x -= 1f;
            if (kb.eKey.isPressed) gerak.y += 1f;
            if (kb.qKey.isPressed) gerak.y -= 1f;
            if (gerak != Vector3.zero)
            {
                // maju/mundur mengikuti arah pandang di bidang lantai
                var arah = Quaternion.Euler(0f, yaw, 0f);
                var cepat = kecepatanJalan * (kb.leftShiftKey.isPressed ? 3f : 1f);
                fokusTarget += (arah * new Vector3(gerak.x, 0f, gerak.z) + Vector3.up * gerak.y) * cepat * Time.unscaledDeltaTime;
            }
        }

        if (mouse != null)
        {
            var d = mouse.delta.ReadValue();
            if (mouse.rightButton.isPressed)
            {
                yawTarget += d.x * kecepatanPutar;
                pitchTarget = Mathf.Clamp(pitchTarget - d.y * kecepatanPutar, pitchMin, pitchMax);
            }
            if (mouse.middleButton.isPressed)
            {
                var rot = Quaternion.Euler(pitch, yaw, 0f);
                fokusTarget -= (rot * Vector3.right * d.x + rot * Vector3.up * d.y) * kecepatanGeser * jarak;
            }
            float scroll = mouse.scroll.ReadValue().y;
            if (scroll != 0f)
                jarakTarget = Mathf.Clamp(jarakTarget * (1f - scroll * kecepatanZoom), jarakMin, jarakMax);
        }

        fokusTarget = Jepit(fokusTarget);
    }

    void Terapkan()
    {
        var rot = Quaternion.Euler(pitch, yaw, 0f);
        var posisi = fokus - rot * Vector3.forward * jarak;
        transform.SetPositionAndRotation(Jepit(posisi), rot);
    }

    Vector3 Jepit(Vector3 p) => Vector3.Max(batasMin, Vector3.Min(batasMax, p));

    void OnGUI()
    {
        if (!tampilkanBantuan) return;
        var teks = "Sudut: ";
        for (int i = 0; i < sudut.Length; i++)
            teks += (i == aktif ? "[" : "") + (i + 1) + " " + sudut[i].nama + (i == aktif ? "]" : "") + "   ";
        teks += "\nKlik kanan: putar   Klik tengah: geser   Scroll: zoom   WASD/QE: pindah fokus (Shift: cepat)   H: sembunyikan";
        GUI.Box(new Rect(10, 10, 720, 46), GUIContent.none);
        GUI.Label(new Rect(18, 14, 710, 40), teks);
    }
}
