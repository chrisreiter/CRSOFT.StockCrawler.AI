namespace Ingest.Core.Analysis;

/// <summary>
/// Definition der Merkmale — die <b>einzige</b> Stelle, an der Anzahl,
/// Reihenfolge und Bedeutung festgelegt sind.
///
/// Training und Inferenz müssen dieselbe Reihenfolge verwenden. Weicht sie ab,
/// bekommt das Modell zur Laufzeit stillschweigend andere Zahlen als beim
/// Training und liefert plausibel aussehenden Unsinn — ein Fehler, der ohne
/// gemeinsame Definition kaum auffällt. Deshalb wird der Merkmalssatz zusammen
/// mit dem Modell versioniert (<see cref="Version"/>) und beim Laden geprüft.
///
/// Der Zuschnitt folgt dem Ziel: nicht „prognostiziere Kurs X aus Kurs X",
/// sondern „prognostiziere X aus dem Zustand des <i>gesamten</i> Marktes".
/// Deshalb stehen neben den eigenen Kursmerkmalen bewusst Markt-, Klassen- und
/// Nachbarschaftsmerkmale.
/// </summary>
public static class FeatureSet
{
    public const string Version = "feat-1";

    /// <summary>Wie viele Bars Historie ein Wert braucht, damit alle Merkmale definiert sind.</summary>
    public const int MinHistoryBars = 70;

    /// <summary>Wie viele Nachbarn in die Umfeldmerkmale eingehen.</summary>
    public const int NeighbourCount = 8;

    // Reihenfolge ist bindend.
    public static readonly string[] Names =
    [
        // --- eigene Kursdynamik ---
        "r1", "r2", "r3", "r5", "r10", "r20",
        "vol20", "vol60",
        "ma20_dist", "ma50_dist", "z50",

        // --- eigener Kapitalfluss ---
        "flow_z20", "flow_share_bp", "flow_rot20",

        // --- Marktzustand (zum selben Zeitpunkt für alle Werte gleich) ---
        "mkt_r1", "mkt_r5", "mkt_disp", "mkt_breadth", "session_cov",

        // --- Anlageklasse des Werts ---
        "cls_r1", "cls_share", "cls_rot",

        // --- Umfeld: was machen die verwandten Werte? ---
        "nb_r1", "nb_r5", "lead_signal", "rel_str20",

        // --- statische Zugehörigkeit ---
        "is_stock", "is_etf", "is_crypto"
    ];

    public static int Count => Names.Length;

    // Feste Indizes, damit der Aufbau ohne Namenssuche auskommt.
    public const int R1 = 0, R2 = 1, R3 = 2, R5 = 3, R10 = 4, R20 = 5;
    public const int Vol20 = 6, Vol60 = 7;
    public const int Ma20Dist = 8, Ma50Dist = 9, Z50 = 10;
    public const int FlowZ20 = 11, FlowShareBp = 12, FlowRot20 = 13;
    public const int MktR1 = 14, MktR5 = 15, MktDisp = 16, MktBreadth = 17, SessionCov = 18;
    public const int ClsR1 = 19, ClsShare = 20, ClsRot = 21;
    public const int NbR1 = 22, NbR5 = 23, LeadSignal = 24, RelStr20 = 25;
    public const int IsStock = 26, IsEtf = 27, IsCrypto = 28;
}
