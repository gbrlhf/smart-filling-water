using UnityEngine;

// Peti botol kosong: klik untuk mengisi ulang 12 botol yang diambil operator.
public class PetiBotol : Interaktif
{
    public OperatorController operatorMesin;

    public override string Label => "Isi ulang peti botol (" + operatorMesin.SisaPeti + "/" + operatorMesin.KapasitasPeti + ")";

    public override void Interaksi() => operatorMesin.IsiUlangPeti();
}
