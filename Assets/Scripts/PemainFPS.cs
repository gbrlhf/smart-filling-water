using UnityEngine;
using UnityEngine.InputSystem;

// Pemain first-person: jalan di ruangan dan berinteraksi dengan mesin.
// WASD: jalan | Shift: lari | C: jongkok | Mouse: lihat | Klik kiri / E: interaksi
// Tab: ganti ke kamera orbit | Esc: lepas kursor
[RequireComponent(typeof(CharacterController))]
public class PemainFPS : MonoBehaviour
{
    public Camera kamera;
    public KameraOrbit kameraOrbit;

    [Header("Gerak")]
    public float kecepatanJalan = 1.6f;
    public float kecepatanLari = 3.2f;
    public float sensitivitas = 0.1f;
    public float tinggiMata = 1.62f;
    public float tinggiJongkok = 1.05f;
    public float gravitasi = -9.81f;

    [Header("Interaksi")]
    public float jangkauan = 2.2f;

    CharacterController cc;
    float yaw, pitch, kecepatanJatuh, mata;
    Interaktif sasaran;
    bool modeOrbit;
    GUIStyle gayaTengah;

    const int IgnoreRaycast = 2;   // layer pemain, supaya raycast tidak kena badan sendiri

    void Start()
    {
        gameObject.layer = IgnoreRaycast;
        cc = GetComponent<CharacterController>();
        yaw = transform.eulerAngles.y;
        mata = tinggiMata;
        SetModeOrbit(false);
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;
        if (kb == null || mouse == null) return;

        if (kb.tabKey.wasPressedThisFrame) SetModeOrbit(!modeOrbit);
        if (modeOrbit) { sasaran = null; return; }

        if (kb.escapeKey.wasPressedThisFrame) KunciKursor(false);
        if (Cursor.lockState != CursorLockMode.Locked)
        {
            sasaran = null;
            if (mouse.leftButton.wasPressedThisFrame) KunciKursor(true);
            return;
        }

        // lihat
        var d = mouse.delta.ReadValue() * sensitivitas;
        yaw += d.x;
        pitch = Mathf.Clamp(pitch - d.y, -85f, 85f);
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        // jongkok
        mata = Mathf.Lerp(mata, kb.cKey.isPressed ? tinggiJongkok : tinggiMata, 1f - Mathf.Exp(-10f * Time.deltaTime));
        kamera.transform.SetLocalPositionAndRotation(new Vector3(0f, mata, 0f), Quaternion.Euler(pitch, 0f, 0f));

        // jalan
        var masukan = Vector2.zero;
        if (kb.wKey.isPressed) masukan.y += 1f;
        if (kb.sKey.isPressed) masukan.y -= 1f;
        if (kb.dKey.isPressed) masukan.x += 1f;
        if (kb.aKey.isPressed) masukan.x -= 1f;
        float cepat = kb.leftShiftKey.isPressed && !kb.cKey.isPressed ? kecepatanLari : kecepatanJalan;
        var gerak = Vector3.ClampMagnitude(transform.right * masukan.x + transform.forward * masukan.y, 1f) * cepat;
        if (cc.isGrounded && kecepatanJatuh < 0f) kecepatanJatuh = -1f;
        kecepatanJatuh += gravitasi * Time.deltaTime;
        cc.Move((gerak + Vector3.up * kecepatanJatuh) * Time.deltaTime);

        // interaksi
        sasaran = null;
        var ray = new Ray(kamera.transform.position, kamera.transform.forward);
        if (Physics.Raycast(ray, out var hit, jangkauan, ~(1 << IgnoreRaycast), QueryTriggerInteraction.Collide))
            sasaran = hit.collider.GetComponentInParent<Interaktif>();
        if (sasaran && (kb.eKey.wasPressedThisFrame || mouse.leftButton.wasPressedThisFrame))
            sasaran.Interaksi();
    }

    void SetModeOrbit(bool orbit)
    {
        modeOrbit = orbit;
        kamera.gameObject.SetActive(!orbit);
        if (kameraOrbit) kameraOrbit.gameObject.SetActive(orbit);
        KunciKursor(!orbit);
    }

    static void KunciKursor(bool kunci)
    {
        Cursor.lockState = kunci ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !kunci;
    }

    void OnGUI()
    {
        gayaTengah ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        var st = StatusMesin.I;

        if (modeOrbit)
        {
            GUI.Label(new Rect(10, Screen.height - 30, 400, 24), "Tab: kembali jalan (first-person)");
            return;
        }

        if (Cursor.lockState != CursorLockMode.Locked)
        {
            GUI.Box(new Rect(cx - 160, cy - 20, 320, 40), GUIContent.none);
            GUI.Label(new Rect(cx - 160, cy - 20, 320, 40), "Klik untuk lanjut", gayaTengah);
            return;
        }

        // crosshair
        GUI.Label(new Rect(cx - 10, cy - 12, 20, 24), sasaran ? "◉" : "·", gayaTengah);
        if (sasaran)
            GUI.Label(new Rect(cx - 200, cy + 18, 400, 26), "[E / Klik]  " + sasaran.Label, gayaTengah);

        if (st)
        {
            var listrik = !st.mcbUtama ? "MATI" : "ADA";
            GUI.Box(new Rect(10, 10, 300, 52), GUIContent.none);
            GUI.Label(new Rect(18, 14, 290, 22), "Listrik: " + listrik + "    Mesin: " + st.Mode.ToString().ToUpper());
            GUI.Label(new Rect(18, 36, 290, 22), "MCB  U:" + Onoff(st.mcbUtama) + "  M:" + Onoff(st.mcbMesin) + "  K:" + Onoff(st.mcbKontrol) + "  L:" + Onoff(st.mcbLampu));
            if (Time.time - st.WaktuPesan < 3f)
                GUI.Label(new Rect(cx - 250, cy + 50, 500, 26), st.Pesan, gayaTengah);
        }

        GUI.Label(new Rect(10, Screen.height - 30, 900, 24),
            "WASD: jalan   Shift: lari   C: jongkok   E / Klik: interaksi   Tab: kamera orbit   Esc: lepas kursor");
    }

    static string Onoff(bool b) => b ? "ON" : "OFF";
}
