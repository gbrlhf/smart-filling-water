using UnityEngine;

// Kotak penampung botol jadi: klik untuk mengosongkan saat penuh (atau kapan saja).
public class KotakPenampung : Interaktif
{
    public MesinSimulasi simulasi;

    public override string Label => "Kosongkan kotak penampung (" + simulasi.IsiKotak + "/" + simulasi.kapasitasKotak + ")";

    public override void Interaksi() => simulasi.KosongkanKotak();
}
