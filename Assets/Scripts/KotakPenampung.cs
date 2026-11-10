using UnityEngine;

// Kotak penampung botol (OK maupun Reject/NG Bin): klik atau tekan E untuk mengosongkan.
public class KotakPenampung : Interaktif
{
    public MesinSimulasi simulasi;
    public bool adalahKotakReject;

    public override string Label => adalahKotakReject
        ? "Kosongkan Wadah Reject (" + simulasi.IsiKotakReject + "/" + simulasi.kapasitasKotakReject + ")"
        : "Kosongkan Kotak Penampung OK (" + simulasi.IsiKotak + "/" + simulasi.kapasitasKotak + ")";

    public override void Interaksi()
    {
        if (adalahKotakReject) simulasi.KosongkanKotakReject();
        else simulasi.KosongkanKotak();
    }
}

