using System.Security.Cryptography;
using System.Text;

namespace Ingest.Core.Analysis;

/// <summary>
/// Kennwörter hashen und prüfen.
///
/// <para><b>PBKDF2-HMAC-SHA256, 210.000 Runden, 16 Byte Salz je Benutzer.</b>
/// Die Rundenzahl folgt der Empfehlung des OWASP für dieses Verfahren. Sie ist
/// so gewählt, dass eine Prüfung auf gewöhnlicher Hardware im
/// Hundertstelsekundenbereich liegt — für eine Anmeldung nicht spürbar, für
/// das Durchprobieren von Kennwörtern teuer.</para>
///
/// <para><b>Warum aus der Standardbibliothek und nicht aus einem Paket.</b>
/// <c>Rfc2898DeriveBytes</c> steht in der Laufzeitumgebung; ein zusätzliches
/// Paket wäre eine weitere Abhängigkeit an genau der Stelle, an der man am
/// wenigsten überraschen will. Die Parameter stehen hier sichtbar, statt in
/// den Voreinstellungen einer fremden Bibliothek.</para>
///
/// <para><b>Das Format trägt seine eigenen Parameter</b> —
/// <c>pbkdf2$sha256$210000$salz$schlüssel</c>. Wer die Rundenzahl später
/// erhöht, kann alte Hashes weiter prüfen und beim nächsten erfolgreichen
/// Anmelden neu schreiben; es braucht keine Migration und keinen Stichtag.</para>
/// </summary>
public static class Kennwort
{
    private const int Runden = 210_000;
    private const int SalzLaenge = 16;
    private const int SchluesselLaenge = 32;
    private const string Verfahren = "pbkdf2";
    private const string Streuung = "sha256";

    /// <summary>
    /// Die kürzeste zulässige Länge. Acht Zeichen sind wenig; verlangt wird
    /// hier zwölf, weil ein öffentlich erreichbarer Server keine zweite
    /// Verteidigungslinie hat.
    /// </summary>
    public const int MindestLaenge = 12;

    public static string Hashen(string kennwort)
    {
        if (string.IsNullOrEmpty(kennwort))
            throw new ArgumentException("Kennwort ist leer", nameof(kennwort));

        var salz = RandomNumberGenerator.GetBytes(SalzLaenge);

        var schluessel = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(kennwort), salz, Runden,
            HashAlgorithmName.SHA256, SchluesselLaenge);

        return string.Join('$', Verfahren, Streuung, Runden,
                           Convert.ToBase64String(salz),
                           Convert.ToBase64String(schluessel));
    }

    /// <summary>
    /// Prüft ein Kennwort gegen einen gespeicherten Hash.
    ///
    /// <para>Der Vergleich läuft über <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// und nicht über <c>==</c>. Ein Vergleich, der beim ersten
    /// unterschiedlichen Byte abbricht, verrät über die Laufzeit, wie viele
    /// Bytes gestimmt haben — daraus lässt sich der Hash Byte für Byte
    /// erraten.</para>
    /// </summary>
    public static bool Stimmt(string kennwort, string? gespeichert)
    {
        if (string.IsNullOrWhiteSpace(gespeichert) || string.IsNullOrEmpty(kennwort))
            return false;

        var teile = gespeichert.Split('$');

        if (teile.Length != 5 || teile[0] != Verfahren || teile[1] != Streuung) return false;
        if (!int.TryParse(teile[2], out var runden) || runden < 1000) return false;

        byte[] salz, erwartet;

        try
        {
            salz = Convert.FromBase64String(teile[3]);
            erwartet = Convert.FromBase64String(teile[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salz.Length == 0 || erwartet.Length == 0) return false;

        var gerechnet = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(kennwort), salz, runden,
            HashAlgorithmName.SHA256, erwartet.Length);

        return CryptographicOperations.FixedTimeEquals(gerechnet, erwartet);
    }

    /// <summary>
    /// Ob ein gespeicherter Hash nach heutigen Maßstäben zu schwach ist und
    /// beim nächsten erfolgreichen Anmelden neu geschrieben werden sollte.
    /// </summary>
    public static bool VeraltetSich(string? gespeichert)
    {
        var teile = (gespeichert ?? "").Split('$');
        return teile.Length != 5
               || teile[0] != Verfahren
               || !int.TryParse(teile[2], out var runden)
               || runden < Runden;
    }

    /// <summary>
    /// Prüft ein neues Kennwort auf das Nötigste. Bewusst keine
    /// Zeichenklassen-Regeln: Sie erzwingen <c>Passwort1!</c> und verhindern
    /// nichts. Länge und die Abwesenheit der offensichtlichsten Kandidaten
    /// bringen mehr.
    /// </summary>
    public static string? Beanstandung(string kennwort)
    {
        if (string.IsNullOrWhiteSpace(kennwort))
            return "Kennwort ist leer.";

        if (kennwort.Length < MindestLaenge)
            return $"Kennwort muss mindestens {MindestLaenge} Zeichen haben.";

        if (kennwort.Length > 256)
            return "Kennwort ist unsinnig lang (mehr als 256 Zeichen).";

        string[] verbreitet =
        [
            "passwort", "password", "geheim", "123456", "qwertz", "qwerty",
            "admin", "kennwort", "willkommen", "welcome", "letmein"
        ];

        var klein = kennwort.ToLowerInvariant();

        return verbreitet.Any(v => klein.Contains(v))
            ? "Kennwort enthält eine sehr verbreitete Zeichenfolge."
            : null;
    }
}
