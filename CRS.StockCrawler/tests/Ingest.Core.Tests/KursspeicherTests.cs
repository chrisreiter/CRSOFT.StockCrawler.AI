using Ingest.Core.Models;
using Ingest.Infrastructure.Datenbank;

namespace Ingest.Core.Tests;

/// <summary>
/// Prüfungen für das Fortschreiben des Kursspeichers.
///
/// <para>Der Anlass: Bis zum 28.09.2026 hat ein Schreibvorgang den gehaltenen
/// Abschnitt verworfen. Der Tageslauf schreibt für alle verfolgten Werte, also
/// war danach der ganze Speicher leer, und die unmittelbar folgenden Läufe
/// holten dieselben Bars einzeln wieder aus der Datenbank — gemessen 318.590
/// Bars und 92 Sekunden für Daten, welche die Anwendung selbst geschrieben
/// hatte.</para>
///
/// <para>Fortschreiben statt Wegwerfen ist schneller, aber es kann auch falsch
/// sein, und ein falscher Kurs ist schlimmer als ein langsamer. Diese Prüfungen
/// decken deshalb nicht das Tempo ab, sondern die drei Arten, auf die das
/// Verschmelzen danebengehen kann: Dubletten, verlorene Korrekturen und ein
/// stillschweigend nach unten verschobenes <c>AbUtc</c>.</para>
/// </summary>
public class KursspeicherTests
{
    private static PriceBar Bar(string tag, decimal schluss) =>
        new() { TsUtc = DateTime.Parse(tag), Close = schluss };

    private static readonly DateTime Ab = DateTime.Parse("2026-01-01");
    private static readonly DateTime Bis = DateTime.Parse("2030-01-01");

    [Fact]
    public void Ergaenze_haengt_neue_Bars_an_und_haelt_die_Reihenfolge()
    {
        var s = new Kursspeicher();
        s.Lege(1, "1d", Ab, [Bar("2026-01-02", 10m), Bar("2026-01-03", 11m)]);

        s.Ergaenze(1, "1d", [Bar("2026-01-04", 12m), Bar("2026-01-05", 13m)]);

        var alle = s.Hole(1, "1d", Ab, Bis)!;
        Assert.Equal(4, alle.Count);
        Assert.Equal([10m, 11m, 12m, 13m], alle.Select(b => b.Close));
        Assert.True(alle.Select(b => b.TsUtc).SequenceEqual(alle.Select(b => b.TsUtc).Order()));
    }

    [Fact]
    public void Ergaenze_ersetzt_bei_gleichem_Zeitstempel_durch_den_neuen_Wert()
    {
        // Der Inkrementlauf laedt die letzten Tage bewusst erneut, weil ein
        // Anbieter Kurse nachtraeglich korrigiert. Gaebe der alte Bar den
        // Ausschlag, haette der Speicher genau die Korrektur nicht, wegen der
        // nachgeladen wurde.
        var s = new Kursspeicher();
        s.Lege(1, "1d", Ab, [Bar("2026-01-02", 10m), Bar("2026-01-03", 11m)]);

        s.Ergaenze(1, "1d", [Bar("2026-01-03", 99m), Bar("2026-01-04", 12m)]);

        var alle = s.Hole(1, "1d", Ab, Bis)!;
        Assert.Equal(3, alle.Count);                       // keine Dublette
        Assert.Equal([10m, 99m, 12m], alle.Select(b => b.Close));
    }

    [Fact]
    public void Ergaenze_verschiebt_AbUtc_nicht_nach_unten()
    {
        // `AbUtc` ist die Zusage "ab hier ist die Reihe vollstaendig". Ein
        // aelterer Bar macht sie nicht wahr -- der Bereich dazwischen wurde nie
        // geladen. Wer weiter zurueck fragt, muss einen Fehlschlag bekommen.
        var s = new Kursspeicher();
        s.Lege(1, "1d", Ab, [Bar("2026-01-02", 10m)]);

        s.Ergaenze(1, "1d", [Bar("2025-06-01", 5m)]);

        Assert.Null(s.Hole(1, "1d", DateTime.Parse("2025-01-01"), Bis));

        var alle = s.Hole(1, "1d", Ab, Bis)!;
        Assert.Single(alle);
        Assert.Equal(10m, alle[0].Close);
    }

    [Fact]
    public void Ergaenze_legt_nichts_an_was_nicht_gehalten_wird()
    {
        // Was nicht im Speicher steht, muss auch nicht fortgeschrieben werden.
        // Ein Abschnitt aus dem Nichts haette kein belastbares `AbUtc`.
        var s = new Kursspeicher();

        s.Ergaenze(42, "1d", [Bar("2026-01-02", 10m)]);

        Assert.Equal(0, s.GehalteneReihen);
        Assert.Null(s.Hole(42, "1d", Ab, Bis));
    }

    [Fact]
    public void Zweite_Anfrage_trifft_den_Speicher()
    {
        // Der banale Test, der in dieser Anwendung schon einmal zu spaet kam:
        // Der erste Entwurf schluesselte nach dem angefragten Zeitraum, der aus
        // `jetzt - 12 Monate` kommt -- also jedes Mal ein neuer Schluessel und
        // nie ein Treffer, bei vollem Verwaltungsaufwand.
        var s = new Kursspeicher();
        s.Lege(1, "1d", Ab, [Bar("2026-01-02", 10m), Bar("2026-01-03", 11m)]);

        Assert.NotNull(s.Hole(1, "1d", Ab, Bis));
        Assert.NotNull(s.Hole(1, "1d", Ab.AddDays(1), Bis));

        Assert.Equal(2, s.Treffer);
        Assert.Equal(0, s.Fehlschlaege);
    }

    [Fact]
    public void Zaehler_trennen_Treffer_Fehlschlag_und_Ergaenzung()
    {
        var s = new Kursspeicher();
        s.Lege(1, "1d", Ab, [Bar("2026-01-02", 10m)]);

        s.Hole(1, "1d", Ab, Bis);                                  // Treffer
        s.Hole(2, "1d", Ab, Bis);                                  // Fehlschlag: nicht gehalten
        s.Hole(1, "1d", Ab.AddYears(-1), Bis);                     // Fehlschlag: vor AbUtc
        s.Ergaenze(1, "1d", [Bar("2026-01-03", 11m)]);             // Ergaenzung
        s.Ergaenze(2, "1d", [Bar("2026-01-03", 11m)]);             // nicht gehalten: zaehlt nicht

        Assert.Equal(1, s.Treffer);
        Assert.Equal(2, s.Fehlschlaege);
        Assert.Equal(1, s.Ergaenzungen);
    }
}
