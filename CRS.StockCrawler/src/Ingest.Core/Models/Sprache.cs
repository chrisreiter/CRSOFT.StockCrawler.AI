namespace Ingest.Core.Models;

/// <summary>
/// Eine gefundene Sprachdatei, wie sie in der Auswahl oben rechts steht.
/// </summary>
/// <param name="Code">Das Kürzel aus dem Dateinamen — <c>loc.res.<b>it</b>.xml</c>.</param>
/// <param name="Name">
/// Der Name in der Sprache selbst („Deutsch“, „English“, „Italiano“). Eine
/// Sprachliste, die die Sprachen in einer FREMDEN Sprache benennt, ist genau
/// für den unbrauchbar, der sie braucht: Wer nur Italienisch liest, sucht
/// „Italiano“ und nicht „Italienisch“.
/// </param>
/// <param name="Texte">Wieviele Einträge die Datei hat — Grundlage für <see cref="Luecken"/>.</param>
/// <param name="Luecken">
/// Wieviele Einträge des deutschen Katalogs hier FEHLEN. Ohne diese Zahl sieht
/// eine halb übersetzte Sprache aus wie eine fertige, in der zufällig deutsche
/// Wörter stehen.
/// </param>
public sealed record Sprache(string Code, string Name, int Texte, int Luecken);

/// <summary>
/// Eine Sprachdatei mit ihren Einträgen.
/// </summary>
/// <param name="Texte">
/// Deutscher Text auf Zieltext. <b>Der deutsche Text ist der Schlüssel.</b>
///
/// <para>Das ist nachträglich entstanden und wäre in einer neu gebauten
/// Anwendung die schlechtere Wahl — dort gehören Schlüssel wie
/// <c>kurse.titel</c> hin. Hier standen 1.711 Texte ohne jeden Schlüssel in
/// <c>index.html</c> und <c>app.js</c>; sie alle mit Schlüsseln zu versehen
/// hiesse, jede dieser Stellen anzufassen, und jede Stelle ist eine
/// Gelegenheit, die Anwendung zu zerlegen. Mit dem deutschen Text als
/// Schlüssel bleibt beides unberührt.</para>
/// </param>
/// <param name="Offen">
/// Die Teilmenge, die schon VOR der Anmeldung ausgeliefert werden darf — die
/// Anmeldeseite braucht ihre eigenen Wörter. Welche das sind, entscheidet die
/// Sprachdatei selbst über <c>offen="1"</c> und nicht eine Liste im Quelltext,
/// die beim nächsten neuen Text jemand zu pflegen vergisst.
/// </param>
public sealed record Sprachdatei(
    string Code, string Name,
    IReadOnlyDictionary<string, string> Texte,
    IReadOnlyDictionary<string, string> Offen);
