/* ===========================================================================
   Sprachumstellung der Oberfläche
   ===========================================================================

   DER DEUTSCHE TEXT IST DER SCHLÜSSEL.

   In einer neu gebauten Anwendung wäre das die schlechtere Wahl -- dort
   gehören Schlüssel wie `kurse.titel` in den Quelltext. Diese Anwendung wurde
   aber mit fest eingebauten deutschen Texten geschrieben, und zwar an 1.711
   Stellen in index.html und app.js. Jede davon mit einem Schlüssel zu
   versehen hiesse, jede davon anzufassen, und jede Stelle ist eine
   Gelegenheit, die Anwendung zu zerlegen. Mit dem deutschen Text als
   Schlüssel bleiben beide Dateien unberührt.

   Der Preis dieser Wahl steht in docs/SPRACHEN.md und wird dort nicht
   beschönigt: Wer einen deutschen Text ändert, verliert dessen Übersetzung,
   und zwar STILL -- die Oberfläche zeigt dann wieder Deutsch. Deshalb meldet
   `/api/loc/` je Sprache, wieviele Einträge fehlen.

   WIE ES ARBEITET

   Nicht über einen Bauplan, sondern über das fertige DOM: Nach jeder
   Änderung werden Textknoten und die Attribute `title`, `placeholder` und
   `aria-label` durchgegangen und ersetzt. Damit ist es gleichgültig, ob ein
   Text aus index.html stammt oder aus einer Tabelle, die app.js gerade
   zusammengesetzt hat -- beide landen im selben DOM.

   Der deutsche URTEXT wird je Knoten gemerkt. Ohne ihn wäre der Wechsel
   Englisch -> Italienisch nicht möglich: Man müsste aus dem Englischen
   zurückübersetzen, und das geht nur, solange die Zuordnung eindeutig ist --
   was sie nie lange bleibt.
   ========================================================================= */

const Loc = (() => {
  'use strict';

  const SPEICHER = 'crs.sprache';
  const KATALOG = 'de';

  /* So sieht der Text zur Laufzeit im DOM aus: Ein Absatz in index.html steht
     dort eingerückt und über drei Zeilen umbrochen. Ohne diese Vereinheit-
     lichung fände kein einziger mehrzeiliger Text seinen Eintrag.            */
  const schluessel = t => t.replace(/\s+/g, ' ').trim();

  let code = KATALOG;
  let wb = null;                    // Map deutsch -> Ziel; null heisst Deutsch
  let bruch = null;                 // Regex über alle Schlüssel, längste zuerst
  let sprachen = [];

  const urKnoten = new WeakMap();   // Textknoten  -> { ur, gesetzt }
  const urAttr = new WeakMap();     // Element     -> { attr: { ur, gesetzt } }

  const ATTRIBUTE = ['title', 'placeholder', 'aria-label'];

  /* Was nie angefasst wird. `data-loc-aus` ist der Ausweg für alles, was
     schon in der richtigen Sprache dasteht -- die Sprachauswahl selbst
     nennt ihre Einträge in der jeweiligen Sprache, „Italiano" und nicht
     „Italienisch". Wer das übersetzte, machte die Liste genau für den
     unbrauchbar, der sie braucht.                                            */
  const AUS = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEXTAREA', 'CANVAS']);

  const ueberspringen = el =>
    !el || AUS.has(el.nodeName) || el.hasAttribute?.('data-loc-aus');

  // ------------------------------------------------------------ Übersetzen --

  function text(roh) {
    if (!wb) return roh;

    const k = schluessel(roh);
    if (!k) return roh;                       // reiner Leerraum

    const vorn = roh.match(/^\s*/)[0];
    const hinten = roh.match(/\s*$/)[0];

    const treffer = wb.get(k);
    if (treffer !== undefined) return vorn + treffer + hinten;

    /*  Zusammengesetzte Texte.

        app.js baut Sätze aus Bruchstücken: `n + ' Positionen, zusammen ' + x`.
        Im DOM steht davon EIN Knoten -- „3 Positionen, zusammen 614,93." --,
        und den gibt es im Katalog nicht, weil dort nur das Bruchstück steht.
        Deshalb der zweite Anlauf: die bekannten Bruchstücke im Text ersetzen.

        Nur als RÜCKFALL, nie zuerst. Andersherum zerschnitte die Ersetzung
        auch Texte, die als Ganzes im Katalog stehen, und lieferte für sie ein
        schlechteres Ergebnis als der genaue Treffer.                         */
    /*  Die Längengrenze ist eine Kostenbremse, kein Sicherheitsriegel.

        Sie stand zuerst bei 400 Zeichen — und schnitt damit ausgerechnet die
        serverseitig erzeugten Hinweisabsätze ab, die mit 400 bis 900 Zeichen
        die längsten der Anwendung sind. Sichtbar wurde es daran, dass „Kurs
        des jeweiligen Tages" übersetzt war und „Gerechnet in" im selben
        Absatz nicht: Der Knoten lag knapp über der Grenze und wurde als
        Ganzes übersprungen.                                                  */
    if (!bruch || k.length > 3000) return roh;

    const ersetzt = k.replace(bruch, m => wb.get(m) ?? m);
    return ersetzt === k ? roh : vorn + ersetzt + hinten;
  }

  function knoten(n) {
    let z = urKnoten.get(n);

    /*  Steht etwas anderes da, als wir zuletzt geschrieben haben, hat app.js
        den Knoten neu befüllt -- dann ist das Neue der Urtext. Ohne diese
        Prüfung übersetzte die Anwendung nach dem ersten Neuaufbau einer
        Tabelle dauerhaft den Text von vorgestern.                            */
    if (!z || n.data !== z.gesetzt) z = { ur: n.data, gesetzt: null };

    const neu = text(z.ur);
    if (n.data !== neu) n.data = neu;

    z.gesetzt = neu;
    urKnoten.set(n, z);
  }

  function attribute(el) {
    for (const a of ATTRIBUTE) {
      if (!el.hasAttribute(a)) continue;

      let alle = urAttr.get(el);
      if (!alle) urAttr.set(el, alle = {});

      const jetzt = el.getAttribute(a);
      let z = alle[a];
      if (!z || jetzt !== z.gesetzt) z = alle[a] = { ur: jetzt, gesetzt: null };

      const neu = text(z.ur);
      if (jetzt !== neu) el.setAttribute(a, neu);
      z.gesetzt = neu;
    }
  }

  function durchgang(wurzel) {
    const w = document.createTreeWalker(
      wurzel, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT, {
        acceptNode(n) {
          if (n.nodeType === Node.ELEMENT_NODE)
            return ueberspringen(n) ? NodeFilter.FILTER_REJECT
                                    : NodeFilter.FILTER_ACCEPT;
          return n.data && /\S/.test(n.data) ? NodeFilter.FILTER_ACCEPT
                                             : NodeFilter.FILTER_REJECT;
        }
      });

    let n;
    while ((n = w.nextNode()))
      n.nodeType === Node.TEXT_NODE ? knoten(n) : attribute(n);
  }

  // ------------------------------------------------------------ Beobachter --

  /*  Ein Beobachter statt Aufrufen an jeder Stelle, die etwas zeichnet.

      app.js schreibt an über hundert Stellen `innerHTML`. Jede davon um einen
      Aufruf zu ergänzen hiesse wieder, hundert Stellen anzufassen -- und die
      eine vergessene fiele erst auf, wenn jemand genau diese Ansicht auf
      Englisch öffnet.                                                        */
  let beobachter = null;
  let geplant = false;
  let amArbeiten = false;

  function bald() {
    if (geplant || amArbeiten) return;
    geplant = true;

    /*  Gesammelt statt je Änderung. Der Aufbau einer Tabelle erzeugt hunderte
        Meldungen; ein voller Durchgang je Meldung wäre hundertmal dieselbe
        Arbeit. Ein Durchgang über das ganze Dokument kostet wenige
        Millisekunden -- weniger, als die Buchführung je Meldung kosten würde. */
    requestAnimationFrame(() => {
      geplant = false;
      if (!wb) return;

      amArbeiten = true;
      try { durchgang(document.body); }
      finally { amArbeiten = false; }
    });
  }

  function beobachten() {
    if (beobachter) return;
    beobachter = new MutationObserver(() => bald());
    beobachter.observe(document.body, {
      childList: true, subtree: true, characterData: true,
      attributes: true, attributeFilter: ATTRIBUTE
    });
  }

  // ----------------------------------------------------------- Umschalten --

  async function hole(pfad) {
    const r = await fetch(pfad, { credentials: 'same-origin' });
    if (!r.ok) throw new Error(pfad + ' — ' + r.status);
    return r.json();
  }

  async function setze(neu, merken = true) {
    if (neu === code && wb === null && neu === KATALOG) return;

    if (neu === KATALOG) {
      /*  Zurück auf Deutsch heisst: den gemerkten Urtext wieder hinschreiben.
          Aus dem Englischen zurückzuübersetzen wäre der naheliegende Weg und
          der falsche -- zwei deutsche Texte können dieselbe englische
          Entsprechung haben, und dann ist die Rückrichtung geraten.          */
      wb = null;
      code = KATALOG;
      amArbeiten = true;
      try {
        const w = document.createTreeWalker(document.body,
          NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
        let n;
        while ((n = w.nextNode())) {
          if (n.nodeType === Node.TEXT_NODE) {
            const z = urKnoten.get(n);
            if (z && n.data === z.gesetzt && n.data !== z.ur) {
              n.data = z.ur;
              z.gesetzt = z.ur;
            }
          } else {
            const alle = urAttr.get(n);
            if (!alle) continue;
            for (const a of ATTRIBUTE) {
              const z = alle[a];
              if (z && n.getAttribute(a) === z.gesetzt) {
                n.setAttribute(a, z.ur);
                z.gesetzt = z.ur;
              }
            }
          }
        }
      } finally { amArbeiten = false; }
    } else {
      const d = await hole('/api/loc/' + encodeURIComponent(neu));

      wb = new Map(Object.entries(d.texte));
      code = d.code;

      /*  Die Bruchstück-Regex. Zwei Riegel, beide aus einem Fehler gelernt.

          ERSTENS nur MEHRWORTIGE Schlüssel. Der erste Entwurf nahm auch
          einzelne Wörter, und im Browser stand daraufhin „Pricebewegung" und
          „Hourbar": „Kurs" und „Stunde" sind eigene Einträge und wurden
          mitten im zusammengesetzten Wort ersetzt. Ein einzelnes Wort, das
          allein im DOM steht, findet ohnehin den genauen Treffer — als
          Bruchstück wird es nicht gebraucht und richtet nur Schaden an.

          ZWEITENS Wortgrenzen. Deutsche Zusammensetzungen haben zwischen
          „Kurs" und „bewegung" keine Lücke, an der ein Leser den Schnitt
          erkennen würde — die Regex braucht deshalb die ausdrückliche
          Bedingung, dass links und rechts kein Buchstabe steht. \b genügt
          dafür nicht, weil es Umlaute nicht als Wortzeichen führt.

          Längste zuerst: Stünden „im Sperrbereich" und „im Sperrbereich ab"
          beide im Katalog, gewänne sonst das kürzere und liesse „ab" stehen. */
      const WORTZEICHEN = /[A-Za-z0-9ÄÖÜäöüß]/;
      const VOR = '(?<![A-Za-z0-9ÄÖÜäöüß])';
      const NACH = '(?![A-Za-z0-9ÄÖÜäöüß])';

      /*  Die Grenze gilt JE SCHLÜSSEL, nicht für die ganze Alternation.

          Der erste Entwurf klammerte sie einmal aussen herum — und damit
          fanden alle Schlüssel, die mit einem Satzzeichen beginnen, nie ihren
          Text: „, umgerechnet über EURUSD=X …" steht im DOM hinter dem „D"
          von USD, also wurde dort eine Wortgrenze verlangt, wo es keine geben
          KANN. Betroffen war der ganze serverseitige Teil, denn dessen
          Bruchstücke stehen naturgemäss zwischen zwei Werten und fangen
          deshalb mit Komma, Punkt oder Klammer an.

          Verneinter Vorausblick statt „^ oder Nicht-Buchstabe": Er greift am
          Zeichenkettenanfang von selbst, ohne Sonderfall.                    */
      /*  NUR mehrwortige Schlüssel — und diesmal aus einem gemessenen Grund.

          Naheliegend wäre, auch einzelne Wörter aufzunehmen: Dann liessen
          sich zusammengesetzte Beschriftungen wie „Streng (Vgl.)" oder
          „eingezahlt 1.000,00" übersetzen, für die es keinen genauen Treffer
          gibt. Ausprobiert und verworfen.

          Gemessen an 1.859 Textknoten dieser Seite: Mit den mehrwortigen
          Schlüsseln (rund 1.200 Zweige) läuft ein voller Durchgang in
          **24 ms**. Nimmt man alle Schlüssel ab fünf Zeichen dazu (rund 2.270
          Zweige), kam derselbe Durchgang binnen 45 Sekunden NICHT zum Ende —
          die Messung lief in die Zeitgrenze, dreimal hintereinander. Eine
          genauere Zahl steht bewusst nicht hier; gemessen ist, dass es die
          Grenze reisst.

          Der Grund liegt in der Bauart: Jeder Zweig trägt einen Rückblick,
          und die Maschine probiert an jeder Stelle jeden Zweig. Das wächst
          nicht linear mit der Zahl der Zweige.

          Der Preis ist benannt: Beschriftungen aus einem Wort plus Zahl
          bleiben deutsch. Das ist eine sichtbare Lücke und eine schnelle
          Oberfläche — die umgekehrte Wahl wäre eine vollständige Übersetzung
          und eine unbenutzbare Anwendung.                                    */
      const roh = [...wb.keys()]
        .filter(k => k.length >= 6 && k.length <= 200 && /\s/.test(k))
        .sort((a, b) => b.length - a.length)
        .map(k => (WORTZEICHEN.test(k[0]) ? VOR : '')
                + k.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
                + (WORTZEICHEN.test(k[k.length - 1]) ? NACH : ''));

      bruch = roh.length ? new RegExp(roh.join('|'), 'g') : null;

      amArbeiten = true;
      try { durchgang(document.body); }
      finally { amArbeiten = false; }
    }

    document.documentElement.lang = code;
    if (merken) try { localStorage.setItem(SPEICHER, code); } catch { /* egal */ }

    const feld = document.getElementById('sprache');
    if (feld && feld.value !== code) feld.value = code;

    beobachten();
    document.dispatchEvent(new CustomEvent('sprache', { detail: { code } }));
  }

  // -------------------------------------------------------------- Auswahl --

  function auswahlBauen() {
    const feld = document.getElementById('sprache');
    const kasten = document.getElementById('sprachwahl');
    if (!feld || !kasten) return;

    /*  Nur zeigen, wenn es etwas zu wählen gibt. Eine Auswahl mit einem
        einzigen Eintrag ist keine Wahl, sondern eine Behauptung.             */
    kasten.hidden = sprachen.length < 2;

    feld.innerHTML = sprachen.map(s =>
      '<option value="' + s.code + '">' + s.name
      + (s.luecken > 0 ? ' (' + s.luecken + ')' : '') + '</option>').join('');

    feld.value = code;

    feld.title = sprachen.some(s => s.luecken > 0)
      ? 'Sprache der Oberfläche. Die Zahl in Klammern ist die Anzahl noch '
        + 'nicht übersetzter Texte — dort erscheint weiter Deutsch.'
      : 'Sprache der Oberfläche';

    feld.onchange = () => setze(feld.value).catch(e => console.error('[Loc]', e));
  }

  // ---------------------------------------------------------------- Start --

  async function starten() {
    let gewuenscht = KATALOG;
    try { gewuenscht = localStorage.getItem(SPEICHER) || KATALOG; } catch { /* egal */ }

    try {
      sprachen = await hole('/api/loc/');
    } catch (e) {
      console.warn('[Loc] Sprachliste nicht erreichbar — bleibt deutsch', e);
      return;
    }

    /*  Eine gemerkte Sprache, deren Datei verschwunden ist, darf nicht in
        eine leere Oberfläche führen. Dieselbe Lehre wie beim gemerkten
        Ollama-Endpunkt: Die Auswahl überlebt das Gewählte nicht von selbst.  */
    if (!sprachen.some(s => s.code === gewuenscht)) gewuenscht = KATALOG;

    auswahlBauen();
    if (gewuenscht !== KATALOG) await setze(gewuenscht, false);
    beobachten();
  }

  return {
    starten,
    setze,
    get code() { return code; },
    get sprachen() { return sprachen; },
    /** Der übersetzte Text zu einem deutschen — für Meldungen ausserhalb des DOM. */
    t: text
  };
})();

if (document.readyState === 'loading')
  document.addEventListener('DOMContentLoaded', () => Loc.starten());
else
  Loc.starten();
