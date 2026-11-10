using UnityEngine;

// Dasar untuk semua objek yang bisa diklik / ditekan E oleh pemain.
public abstract class Interaktif : MonoBehaviour
{
    [Tooltip("Ukuran minimum kotak klik, supaya tombol kecil gampang dibidik")]
    public float ukuranKlikMin = 0.04f;
    public bool klikTembus = true;   // true = trigger (tidak menghalangi jalan)

    public abstract string Label { get; }
    public abstract void Interaksi();

    protected virtual void Awake()
    {
        if (GetComponent<Collider>()) return;
        var mf = GetComponent<MeshFilter>();
        if (!mf || !mf.sharedMesh) return;
        var b = mf.sharedMesh.bounds;
        var box = gameObject.AddComponent<BoxCollider>();
        box.center = b.center;
        box.size = Vector3.Max(b.size, Vector3.one * ukuranKlikMin);
        box.isTrigger = klikTembus;
    }
}
