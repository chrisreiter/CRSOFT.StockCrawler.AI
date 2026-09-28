using System.Diagnostics;
using Ingest.Core.Abstractions;
using Ingest.Core.Enums;
using Ingest.Infrastructure.Repositories;
using Ingest.Infrastructure.Datenbank;

namespace Ingest.Api.Scheduling;

/// <summary>
/// Füllt den <see cref="Kursspeicher"/> nach dem Start im Hintergrund.
///
/// <para><b>Warum es das braucht.</b> Der Speicher macht eine Kursansicht von
/// 144 auf 14 Millisekunden schnell — aber erst beim zweiten Aufruf. Der
/// erste zahlt weiter den vollen Preis des Backends, und nach jedem Neustart
/// ist das wieder jeder Wert. Wer morgens die Anwendung öffnet und drei
/// Diagramme ansieht, sieht dreimal die langsame Fassung; das ist genau der
/// Eindruck, den der Speicher beseitigen soll.</para>
///
/// <para><b>Nur Tagesbars, nur verfolgte Werte.</b> Bei rund 700 Werten sind
/// das 175.000 Bars und damit etwa 21 MB — ein Achtel des Speicherbudgets.
/// Stundendaten bleiben draussen: Drei Monate stündlich sind je Wert 2.160
/// Bars, über alle Werte 1,5 Millionen. Das füllte das Budget allein und
/// verdrängte dabei genau die Tagesreihen, die fast jeder Aufruf braucht.</para>
///
/// <para><b>Nacheinander, nicht nebenläufig.</b> Der Node beantwortet eine
/// Abfrage zügig und viele gleichzeitige schlecht; nebenläufiges Vorwärmen
/// würde mit dem Stundenlauf und der Oberfläche um dieselbe Verbindung
/// streiten. Zwei Minuten im Hintergrund sind der bessere Handel — und die
/// Pause zwischen den Werten hält den Node für echte Anfragen frei.</para>
///
/// <para>Auf einem Replikat (<c>Betrieb:Rolle=slave</c>) läuft es trotzdem:
/// Dort wird nur gelesen, und gerade dort zählt die Lesegeschwindigkeit.</para>
/// </summary>
public sealed class Vorwaermer : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly Kursspeicher _speicher;
    private readonly ILogger<Vorwaermer> _log;

    /// <summary>
    /// Wie weit zurück vorgewärmt wird. Zwölf Monate sind die Vorgabe der
    /// Kursansicht; vierundzwanzig decken auch den nächsthäufigen Ausschnitt
    /// ab, ohne den Speicher zu verdoppeln — Tagesbars sind dünn.
    /// </summary>
    private static readonly int VorwaermMonate = 24;

    public Vorwaermer(IServiceScopeFactory scopes, Kursspeicher speicher, ILogger<Vorwaermer> log)
    {
        _scopes = scopes;
        _speicher = speicher;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        /*  Erst die Anwendung hochkommen lassen. Wer sofort loslegt, streitet
            mit dem Aufbau der Oberfläche um dieselbe Datenbank -- und der
            Nutzer wartet dann auf etwas, das für ihn gedacht war.            */
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) { return; }

        try
        {
            using var scope = _scopes.CreateScope();
            var assets = scope.ServiceProvider.GetRequiredService<IAssetRepository>();
            var bars = scope.ServiceProvider.GetRequiredService<IPriceBarRepository>();

            var verfolgt = await assets.ListAsync(trackedOnly: true, limit: 5000, ct: ct);
            if (verfolgt.Count == 0) return;

            var prognosen = scope.ServiceProvider.GetRequiredService<IForecastRepository>();
            var spur = scope.ServiceProvider.GetRequiredService<IForecastTrackRepository>();

            var bis = DateTime.UtcNow;
            var von = bis.AddMonths(-VorwaermMonate);

            var uhr = Stopwatch.StartNew();
            var fertig = 0;

            foreach (var a in verfolgt)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await bars.GetManyAsync([a.AssetId], BarInterval.Daily, von, bis, ct);
                    fertig++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    /*  Ein Wert, der nicht lädt, darf das Vorwärmen nicht
                        beenden -- es ist eine Bequemlichkeit, keine Pflicht.  */
                    _log.LogDebug(ex, "Vorwärmen für {Symbol} übersprungen", a.Symbol);
                }

                // Kurz Luft lassen: echte Anfragen haben Vorrang.
                try { await Task.Delay(25, ct); }
                catch (OperationCanceledException) { break; }
            }

            _log.LogInformation(
                "Kursspeicher vorgewärmt: {Fertig} von {Gesamt} Werten, {Bars} Bars in {Sek:N0} s",
                fertig, verfolgt.Count, _speicher.GehalteneBars, uhr.Elapsed.TotalSeconds);

            /*  Zweiter Durchgang: die juengsten Prognosen und die
                Treffsicherheit je Wert.

                Getrennt vom ersten, weil die Reihenfolge zaehlt. Ein
                Diagramm ohne eingeblendete Prognose ist der haeufigste
                Aufruf; er soll als erstes schnell sein. Die Prognosen kosten
                je Wert rund eine halbe Sekunde (gemessen 466 bis 494 ms) --
                ueber 646 Werte also einige Minuten, die im Hintergrund
                niemanden stoeren, den Nutzer aber genau dann treffen wuerden,
                wenn er die Prognose einblendet.

                Der Speicherbedarf ist dabei winzig: Gehalten werden je Wert
                die NEUN juengsten Prognosen und eine Handvoll Guetezeilen,
                nicht die 2.392 gelesenen.                                   */
            var uhr2 = Stopwatch.StartNew();
            var fertig2 = 0;

            foreach (var a in verfolgt)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await prognosen.GetLatestAsync(a.AssetId, ct);
                    await prognosen.GetAccuracyAsync(a.AssetId, ct);
                    fertig2++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Prognose-Vorwaermen fuer {Symbol} uebersprungen", a.Symbol);
                }

                try { await Task.Delay(25, ct); }
                catch (OperationCanceledException) { break; }
            }

            _log.LogInformation(
                "Prognosespeicher vorgewärmt: {Fertig} von {Gesamt} Werten in {Sek:N0} s",
                fertig2, verfolgt.Count, uhr2.Elapsed.TotalSeconds);
            /*  HIER IST SCHLUSS — und das ist eine Korrektur, keine Sparmassnahme.

                Bis zum 28.09.2026 folgten hier zwei weitere Durchgaenge:
                Stundenreihen und Rueckblick. Beide waren gegen einen ruhigen
                Node gemessen (127 s und 167 s) und sahen dort guenstig aus.

                Im Betrieb sind sie es nicht. Gemessen am selben Tag: Der
                Rueckblick-Durchgang lief nach 38 Minuten noch immer, der Node
                hatte in dieser Zeit 16.119 Sekunden CPU verbraucht, und die
                Anwendung antwortete auf ihre eigenen Anfragen mit 500 und
                „Timeout during reading attempt". Die Vorwaermung hat also
                genau das kaputtgemacht, wofuer es sie gibt.

                Der Grund ist derselbe wie ueberall auf diesem Backend: Der
                Rueckblick liest je Wert aus `forecast` und `forecast_track`
                (9,3 Mio Zeilen), und eine ueber den Index gefundene Zeile
                kostet dort ein Vielfaches einer sequentiell gelesenen. Ueber
                646 Werte summiert sich das zu einer Last, die eine
                Nebenbeschaeftigung nicht haben darf.

                Geblieben sind die zwei billigen Durchgaenge: Tagesbars
                (97 s) und Prognosen (228 s). Sie decken den haeufigsten
                Aufruf ab. Wer eine Stundenreihe oder den Rueckblick oeffnet,
                zahlt den ersten Aufruf selbst — das ist der richtige Preis,
                solange das Vorwaermen teurer ist als das Warten.

                Die Lehre, und sie ist die allgemeinere: Eine
                Hintergrundaufgabe muss gegen den BETRIEB gemessen werden,
                nicht gegen ein ruhiges System. Ich habe hier dieselbe Falle
                gebaut, vor der ich in dieser Sitzung zweimal gewarnt habe.  */
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vorwärmen des Kursspeichers abgebrochen");
        }
    }
}
