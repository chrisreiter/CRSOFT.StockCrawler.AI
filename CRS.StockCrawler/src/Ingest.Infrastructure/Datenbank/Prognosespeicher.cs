using System.Collections.Concurrent;
using Ingest.Infrastructure.Repositories;
using Ingest.Core.Models;

namespace Ingest.Infrastructure.Datenbank;

/// <summary>
/// Die jüngsten Prognosen und die Treffsicherheit je Wert, im Arbeitsspeicher.
///
/// <para><b>Warum.</b> Beide Auskünfte hängen an Tabellen, die auf dem
/// EventMesh-DataCell-Backend teuer zu lesen sind: <c>forecast</c> mit 1,26
/// Millionen Zeilen und <c>forecast_score</c> mit 484.000. Gemessen am
/// 27.09.2026 über Npgsql, also mit dem Treiber der Anwendung: die jüngsten
/// Prognosen eines Wertes 466 bis 489 ms, die Treffsicherheit 41 bis 55 ms.
/// In der Kursansicht mit fünf Werten summierte sich allein der
/// Prognoseanteil auf <b>10,8 Sekunden</b>, während der SQL Server für die
/// ganze Antwort 109 ms braucht.</para>
///
/// <para><b>Warum es hier sicher ist.</b> Beide Mengen ändern sich an genau
/// zwei Stellen: wenn eine Prognose geschrieben wird (<c>InsertAsync</c>) und
/// wenn eine bewertet wird (<c>ScoreAsync</c>). Das sind Läufe des
/// Zeitplans, keine Nebenwirkungen irgendwo im Code — es gibt also einen
/// eindeutigen Ort, an dem verworfen werden muss, und der steht im selben
/// Repository. Eine Ablauffrist wäre hier die schlechtere Wahl: Sie wäre
/// entweder so kurz, dass sie nichts spart, oder so lang, dass die Ansicht
/// nach dem Stundenlauf die alte Prognose zeigt.</para>
///
/// <para><b>Größe.</b> Je Wert neun Prognosen und eine Handvoll
/// Güte-Zeilen; bei 700 verfolgten Werten sind das einige zehntausend kleine
/// Objekte, also wenige Megabyte. Eine Obergrenze wäre hier Zierde — anders
/// als beim <see cref="Kursspeicher"/>, wo ein einziger Wert mit
/// Stundendaten den Speicher füllen kann.</para>
/// </summary>
public sealed class Prognosespeicher
{
    private readonly ConcurrentDictionary<int, IReadOnlyList<Forecast>> _neueste = new();

    private readonly ConcurrentDictionary<int,
        IReadOnlyList<(int HorizonHours, int N, double Mape, double HitRate)>> _guete = new();

    /// <summary>Die Treffsicherheit über ALLE Werte — eigener Schlüssel, weil
    /// sie sich von jeder einzelnen Wertzeile unterscheidet.</summary>
    private IReadOnlyList<(int HorizonHours, int N, double Mape, double HitRate)>? _gueteGesamt;

    public IReadOnlyList<Forecast>? Neueste(int wert)
        => _neueste.TryGetValue(wert, out var v) ? v : null;

    public void LegeNeueste(int wert, IReadOnlyList<Forecast> prognosen)
        => _neueste[wert] = prognosen;

    public IReadOnlyList<(int HorizonHours, int N, double Mape, double HitRate)>? Guete(int? wert)
        => wert is null
            ? _gueteGesamt
            : _guete.TryGetValue(wert.Value, out var v) ? v : null;

    public void LegeGuete(int? wert,
        IReadOnlyList<(int HorizonHours, int N, double Mape, double HitRate)> zeilen)
    {
        if (wert is null) _gueteGesamt = zeilen;
        else _guete[wert.Value] = zeilen;
    }

    /*  Verworfen wird IMMER auch die Gesamtsicht: Eine neue Bewertung für
        einen Wert ändert auch den Durchschnitt über alle. Das zu vergessen
        wäre der klassische Zwischenspeicher-Fehler — die Einzelzeile stimmt,
        die Summe darüber nicht, und niemand sieht es.                        */
    public void Verwerfe(int wert)
    {
        _neueste.TryRemove(wert, out _);
        _guete.TryRemove(wert, out _);
        _gueteGesamt = null;

        foreach (var k in _verlauf.Keys.Where(k => k.Wert == wert).ToList())
            if (_verlauf.TryRemove(k, out var weg)) _verlaufZeilen -= weg.Zeilen.Length;

        foreach (var k in _spur.Keys.Where(k => k.Wert == wert).ToList())
            if (_spur.TryRemove(k, out var w2)) _spurZeilen -= w2.Zeilen.Length;
    }

    public void VerwerfeAlles()
    {
        _neueste.Clear();
        _guete.Clear();
        _gueteGesamt = null;
        _verlauf.Clear();
        _verlaufZeilen = 0;
        _spur.Clear();
        _spurZeilen = 0;
    }

    public int GehalteneWerte => _neueste.Count + _guete.Count;

    /*  Der Verlauf je Wert und Horizont -- unbefenstert.

        Der Rueckblick im Diagramm braucht Prognose und Ist-Wert ueber einen
        Zeitraum. Gemessen ueber Npgsql kostet das zwei Abfragen: die
        Prognosen im Fenster 177 bis 182 ms (300 Zeilen, also 600 �s je Zeile
        -- der bekannte Preis fuer eine ueber den Index rekonstruierte Zeile)
        und die Bewertungen 51 bis 70 ms.

        Gehalten wird OHNE Zeitfenster, und das ist der Punkt: Das Fenster
        wandert mit jedem Tag, der Bestand je Wert und Horizont nicht. Wer
        nach Fenster schluesselte, haette bei jedem Aufruf einen neuen
        Schluessel und nie einen Treffer -- genau dieser Fehler ist mir beim
        Kursspeicher zuerst unterlaufen.

        Gedeckelt nach Zeilen, aus demselben Grund wie dort: 700 Werte mal
        neun Horizonte mal einige hundert Zeilen waeren sonst Millionen
        Objekte. Verdraengt wird der am laengsten nicht gelesene Eintrag.    */
    private sealed class Verlaufseintrag<T>
    {
        public required T[] Zeilen { get; init; }
        public long Zugriff;
    }

    private readonly ConcurrentDictionary<(int Wert, int Horizont), Verlaufseintrag<ForecastVsActual>> _verlauf = new();
    private const int VerlaufBudget = 400_000;
    private long _uhr;
    private int _verlaufZeilen;

    public ForecastVsActual[]? Verlauf(int wert, int horizont)
    {
        if (!_verlauf.TryGetValue((wert, horizont), out var e)) return null;
        e.Zugriff = Interlocked.Increment(ref _uhr);
        return e.Zeilen;
    }

    public void LegeVerlauf(int wert, int horizont, ForecastVsActual[] zeilen)
    {
        _verlauf[(wert, horizont)] = new Verlaufseintrag<ForecastVsActual>
        {
            Zeilen = zeilen,
            Zugriff = Interlocked.Increment(ref _uhr),
        };

        _verlaufZeilen = _verlauf.Values.Sum(e => e.Zeilen.Length);

        if (_verlaufZeilen <= VerlaufBudget) return;

        foreach (var eintrag in _verlauf.OrderBy(e => e.Value.Zugriff))
        {
            if (_verlaufZeilen <= VerlaufBudget) break;
            if (_verlauf.TryRemove(eintrag.Key, out var weg)) _verlaufZeilen -= weg.Zeilen.Length;
        }
    }

    public int GehalteneVerlaufszeilen => _verlaufZeilen;

    /*  Die Rueckrechnungsspur je Wert, Horizont und Intervall -- unbefenstert.

        `forecast_track` haelt 9,3 Millionen Zeilen. Gemessen ueber Npgsql fuer
        einen Wert bei Horizont 24 und Tagesintervall: im Fenster 293 bis 847
        ms fuer 225 Zeilen, unbefenstert 1.146 bis 1.176 ms fuer 1.229 Zeilen.
        Das Fenster spart also fast nichts und verhindert jeden Treffer im
        Speicher -- deshalb wird auch hier unbefenstert gehalten.

        Geschrieben wird diese Tabelle ausschliesslich vom Rueckrechnungslauf
        (`SchreibeAsync`), und der ist selten. Ein Eintrag ist damit lange
        gueltig.                                                              */
    private readonly ConcurrentDictionary<(int Wert, int Horizont, string Intervall), Verlaufseintrag<TrackRow>> _spur = new();
    private const int SpurBudget = 400_000;
    private int _spurZeilen;

    public TrackRow[]? Spur(int wert, int horizont, string intervall)
    {
        if (!_spur.TryGetValue((wert, horizont, intervall), out var e)) return null;
        e.Zugriff = Interlocked.Increment(ref _uhr);
        return e.Zeilen;
    }

    public void LegeSpur(int wert, int horizont, string intervall, TrackRow[] zeilen)
    {
        _spur[(wert, horizont, intervall)] = new Verlaufseintrag<TrackRow>
        {
            Zeilen = zeilen,
            Zugriff = Interlocked.Increment(ref _uhr),
        };

        _spurZeilen = _spur.Values.Sum(e => e.Zeilen.Length);

        if (_spurZeilen <= SpurBudget) return;

        foreach (var eintrag in _spur.OrderBy(e => e.Value.Zugriff))
        {
            if (_spurZeilen <= SpurBudget) break;
            if (_spur.TryRemove(eintrag.Key, out var weg)) _spurZeilen -= weg.Zeilen.Length;
        }
    }

    public int GehalteneSpurzeilen => _spurZeilen;


}
