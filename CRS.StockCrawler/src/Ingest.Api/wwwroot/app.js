'use strict';

/* StockCrawler – Oberfläche.
   Bewusst ohne Framework und ohne Build-Schritt: die Seite wird direkt aus
   wwwroot ausgeliefert, es gibt nur diese eine Datei plus uPlot. */

// ------------------------------------------------------------------ Helfer

const $ = (sel, root = document) => root.querySelector(sel);

/* Maskiert Text, der per innerHTML in die Seite geht.

   Bisher brauchte es das nicht: Alles Angezeigte kam aus der eigenen Datenbank
   oder aus Symbolnamen. Die vast.ai-Liste kommt von einer fremden API -- GPU-
   Bezeichnung, Zustand und Fehlermeldung stehen dort so, wie der Anbieter sie
   liefert. Das ist kein Angriffsszenario mit hoher Wahrscheinlichkeit, aber ein
   Zeichen zu maskieren ist billiger, als sich darauf zu verlassen.            */
const esc = t => String(t ?? '').replace(/[&<>"']/g,
  c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

const statusEl = $('#status');
let busyCount = 0;

/* Was gerade im Statusfeld steht -- und ob es weggeraeumt werden darf.

   Der Fehler, den das behebt: `api()` setzte beim Start „laedt …" und loeschte
   im `finally`. Nach einer erfolgreichen Aktion laeuft aber fast immer sofort
   ein Nachladen -- beim Anlegen eines Benutzers etwa `ladeBenutzer()`. Dessen
   `api()` ueberschrieb „Benutzer angelegt" mit „laedt …" und loeschte danach
   alles. Die Erfolgsmeldung lebte wenige Millisekunden.

   Nach aussen sah das so aus, als tue die Schaltflaeche nichts: Der Benutzer
   WURDE angelegt, nur sagte es niemand. Gemeldet als „man kann keinen user
   anlegen, keine reaktion der ui". Fehler blieben stehen, weil danach nichts
   nachlaedt -- weshalb der Fehlerfall funktionierte und nur der Erfolgsfall
   stumm war. Das ist die unangenehmere Haelfte: Man sucht den Fehler dort, wo
   alles richtig laeuft.                                                     */
let statusArt = null;      // 'busy' | 'err' | 'ok'
let statusUhr = null;

function setStatus(msg, kind) {
  /* Eine Ladeanzeige darf eine stehende Meldung nicht verdraengen. Sie ist
     Beiwerk; die Meldung ist das Ergebnis. */
  if (kind === 'busy' && statusArt && statusArt !== 'busy') return;

  clearTimeout(statusUhr);
  statusEl.textContent = msg || '';
  statusEl.className = 'status' + (kind ? ' ' + kind : '');
  statusArt = msg ? (kind || 'ok') : null;

  /* Erfolg verschwindet nach sechs Sekunden von selbst -- ein „Benutzer
     angelegt", das eine Stunde spaeter noch dasteht, beschreibt nicht mehr die
     Gegenwart. Fehler bleiben, bis etwas anderes passiert: Sie muessen gelesen
     werden koennen, auch wenn man gerade woanders hingesehen hat. */
  if (statusArt === 'ok') statusUhr = setTimeout(() => setStatus(''), 6000);
}

async function api(path, opts = {}) {
  /* Zwei Antworten haben eine eigene Bedeutung, seit es eine Anmeldung gibt.

     401 heisst: Die Sitzung ist abgelaufen oder es wurde nie eine gegeben.
     Dann hilft keine Fehlermeldung, sondern das Anmeldefeld.

     403 heisst: angemeldet, aber diese Rolle darf das nicht. Der Server
     entscheidet das, nicht die Oberflaeche -- deshalb kann es auch dann
     auftreten, wenn hier eine Schaltflaeche sichtbar ist. Die Meldung sagt
     dann, was los ist, statt einen technischen Fehler zu zeigen.           */

  busyCount++;
  if (busyCount === 1) setStatus('lädt …', 'busy');
  try {
    const res = await fetch(path, opts);
    const text = await res.text();
    let body = null;
    if (text) {
      try { body = JSON.parse(text); } catch { body = text; }
    }
    if (res.status === 401) {
      zeigeAnmeldung('Die Sitzung ist abgelaufen. Bitte neu anmelden.');
      throw new Error('nicht angemeldet');
    }

    if (res.status === 403) {
      const m = (body && body.error ? body.error : 'Diese Rolle darf das nicht.')
              + (body && body.hinweis ? ' ' + body.hinweis : '');
      setStatus(m, 'err');
      throw new Error(m);
    }

    if (!res.ok) {
      const detail = body && body.error ? body.error : res.statusText;
      throw new Error(`${res.status} ${detail}`);
    }
    return body;
  } finally {
    busyCount--;

    /* Nur die eigene Ladeanzeige wegraeumen. Steht dort inzwischen ein
       Ergebnis -- Erfolg oder Fehler --, bleibt es stehen. */
    if (busyCount === 0 && statusArt === 'busy') setStatus('');
  }
}

async function guard(fn, doneMsg) {
  try {
    const r = await fn();
    if (doneMsg) setStatus(doneMsg);
    return r;
  } catch (e) {
    setStatus(e.message, 'err');
    console.error(e);
    return null;
  }
}

const fmtNum = (v, d = 2) =>
  v === null || v === undefined || Number.isNaN(v)
    ? '–'
    : Number(v).toLocaleString('de-DE', { minimumFractionDigits: d, maximumFractionDigits: d });

const fmtPct = (v, d = 2) => (v === null || v === undefined ? '–' : fmtNum(v, d) + ' %');

function fmtCap(v) {
  if (!v) return '–';
  if (v >= 1e12) return fmtNum(v / 1e12, 2) + ' Bio';
  if (v >= 1e9) return fmtNum(v / 1e9, 1) + ' Mrd';
  if (v >= 1e6) return fmtNum(v / 1e6, 1) + ' Mio';
  return fmtNum(v, 0);
}

function fmtDate(iso, withTime = true) {
  if (!iso) return '–';
  const d = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : iso + 'Z');
  if (Number.isNaN(d.getTime())) return '–';
  return withTime ? d.toLocaleString('de-DE') : d.toLocaleDateString('de-DE');
}

const CLASS_LABEL = { Stock: 'Aktie', Etf: 'Fonds/ETF', Crypto: 'Krypto', Index: 'Index' };

/* Farbpalette für die Kurven. Bewusst gut unterscheidbare Töne, weil
   bis zu einem Dutzend Linien übereinander liegen können. */
const PALETTE = [
  '#4c9aff', '#f2545b', '#35c46b', '#f6b73c', '#a78bfa', '#22d3ee',
  '#fb923c', '#f472b6', '#84cc16', '#60a5fa', '#facc15', '#2dd4bf',
  '#c084fc', '#fca5a5', '#4ade80', '#fcd34d', '#818cf8', '#67e8f9',
  '#f97316', '#ec4899', '#a3e635', '#38bdf8', '#eab308', '#14b8a6'
];
const colorFor = i => PALETTE[i % PALETTE.length];

function clearTable(tableSel) {
  const tb = $(tableSel + ' tbody');
  tb.innerHTML = '';
  return tb;
}

function row(tbody, cells) {
  const tr = document.createElement('tr');
  for (const c of cells) {
    const td = document.createElement('td');
    if (c && typeof c === 'object' && !(c instanceof Node)) {
      td.textContent = c.text ?? '';
      if (c.cls) td.className = c.cls;
      if (c.html) { td.textContent = ''; td.innerHTML = c.html; }
    } else if (c instanceof Node) {
      td.appendChild(c);
    } else {
      td.textContent = c ?? '';
    }
    tr.appendChild(td);
  }
  tbody.appendChild(tr);
  return tr;
}

function emptyRow(tbody, colspan, text) {
  const tr = document.createElement('tr');
  const td = document.createElement('td');
  td.colSpan = colspan;
  td.className = 'dim';
  td.textContent = text;
  tr.appendChild(td);
  tbody.appendChild(tr);
}

// ============================================================== Anmeldung ===

/* Die Oberflaeche haelt fest, wer angemeldet ist und mit welcher Rolle. Das
   ist Bequemlichkeit und keine Sicherheit: Die Rolle wird bei JEDER Abfrage
   erneut auf dem Server geprueft. Wer den Schleier hier im Browser wegraeumt,
   sieht eine Anwendung, deren Abfragen alle mit 401 antworten.             */

let wer = null;          // { login, rolle, istAdmin } oder null
let musseingerichtet = false;

/* Die Sitzung ist weg -- neu laden.

   Frueher blendete das einen Schleier ueber der geladenen Anwendung ein. Den
   gibt es nicht mehr: Ohne Sitzung liefert der Server gar keine Anwendung aus,
   sondern die Anmeldeseite. Ein Neuladen genuegt also, und es ist ehrlicher --
   der Zustand im Fenster gehoert zu einer Sitzung, die es nicht mehr gibt.

   Die Sperre gegen die Schleife ist kein Schmuck: Wuerde der Server aus einem
   anderen Grund als der Sitzung 401 liefern, laedt die Seite sonst endlos neu
   und man sieht nie, woran es lag.                                          */
function zeigeAnmeldung(meldung) {
  if (sessionStorage.getItem('sc_neuladen')) {
    setStatus((meldung || 'Nicht angemeldet.') + ' Neu laden hat nicht geholfen.', 'err');
    return;
  }

  sessionStorage.setItem('sc_neuladen', '1');
  location.reload();
}


/* Zeigt oben rechts, wer angemeldet ist, und blendet ein, was zur Rolle passt. */
function zeigeRolle() {
  const leiste = $('#wer');
  const band = $('#nurlesen-band');
  const verwaltung = $('#benutzerverwaltung');

  if (!wer) {
    if (leiste) leiste.hidden = true;
    if (band) band.hidden = true;
    if (verwaltung) verwaltung.hidden = true;
    return;
  }

  if (leiste) {
    leiste.hidden = false;
    $('#wer-name').textContent =
      (wer.anzeigename || wer.login) + ' · ' + (wer.istAdmin ? 'Verwalter' : 'Nutzer');
  }

  // Das Band steht dauerhaft -- die Einschraenkung gilt ja auch dauerhaft.
  if (band) band.hidden = wer.istAdmin;

  // Die Benutzerverwaltung ist auch LESEND nur fuer Verwalter.
  if (verwaltung) verwaltung.hidden = !wer.istAdmin;
}

async function pruefeAnmeldung() {
  try {
    const r = await fetch('/api/auth/status');
    const d = await r.json();

    musseingerichtet = !!d.eingerichtet;

    if (d.angemeldet) {
      wer = { login: d.login, anzeigename: d.anzeigename, rolle: d.rolle, istAdmin: d.istAdmin };
      sessionStorage.removeItem('sc_neuladen');   // Schleifensperre lösen
      zeigeRolle();
      return true;
    }
  } catch {
    // Server nicht erreichbar -- dann hilft das Anmeldefeld auch nicht, aber
    // es ist ehrlicher als eine leere Seite.
  }

  wer = null;
  zeigeRolle();
  zeigeAnmeldung();
  return false;
}


// ------------------------------------------------------------ Navigation

$('#tabs').addEventListener('click', e => {
  const btn = e.target.closest('button[data-view]');
  if (!btn) return;

  $$('#tabs button').forEach(b => b.classList.toggle('active', b === btn));
  $$('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + btn.dataset.view));

  /* Ansichtsumschalter UND Auto-Suche gehören zu den Kursen und wären sonst
     wirkungslos. Die Auto-Suche blieb bisher überall stehen und verbrauchte
     auf jeder Seite Breite in einer Zeile, die ohnehin zu eng war. */
  const beiKursen = btn.dataset.view === 'charts';
  $('#view-switch').style.display = beiKursen ? '' : 'none';
  $('#autofind').style.display = beiKursen ? '' : 'none';

  const init = VIEW_INIT[btn.dataset.view];
  if (init) init();
});

/* Verweise zwischen den Ansichten.

   Ein Hinweis, der auf eine andere Seite verweist, sollte auch dorthin
   führen -- sonst muss der Leser oben suchen, was gemeint war. Ein einziger
   Zuhörer am Dokument genügt: Die Hinweise werden beim Aufbau der Seite
   erzeugt, einzeln gesetzte Handler waeren dann weg.                        */
document.addEventListener('click', e => {
  const a = e.target.closest('a[data-goto]');
  if (!a) return;
  e.preventDefault();
  $(`#tabs button[data-view="${a.dataset.goto}"]`)?.click();
});

// =============================================================== Kurse ===

const chartState = {
  available: [],
  selected: [],           // { assetId, symbol, name, assetClass }
  view: 'line',           // line | candle | volume | matrix
  mode: 'overlay',
  interval: '1d',
  plots: []
};

const UP_COLOR = '#35c46b';
const DOWN_COLOR = '#f2545b';

/* Obergrenze der gleichzeitig dargestellten Werte. Nebeneinander und in der
   Matrix sind viele Werte sinnvoll; nur beim Überlagern wird es ab etwa einem
   Dutzend Linien unübersichtlich — dort warnt die Oberfläche, verbietet es
   aber nicht. Die Matrix-Schnittstelle deckelt bei 40, deshalb dieser Wert. */
const MAX_SELECTED = 40;

/* Gewählte Stichtage. Höchstens vier — mehr Fächer im selben Diagramm sind
   nicht mehr auseinanderzuhalten. */
const asOfDates = [];
const MAX_ASOF = 4;
const OVERLAY_ADVISORY = 12;

async function loadAvailable() {
  const cls = $('#chart-class').value;
  const search = $('#chart-search').value.trim();
  const sector = $('#chart-sector').value;
  const country = $('#chart-country').value;

  const params = new URLSearchParams({ limit: '300', tracked: 'true' });
  if (cls) params.set('cls', cls);
  if (search) params.set('search', search);
  if (sector) params.set('sector', sector);
  if (country) params.set('country', country);

  const list = await guard(() => api('/api/assets/?' + params));
  if (!list) return;

  chartState.available = list;
  renderAvailable();
}

/* Branchen und Länder kommen aus dem Bestand, nicht aus einer festen Liste.

   Der Grund ist praktisch: Die Einteilung stammt vom Anbieter und ändert sich
   mit jedem Universumslauf. Eine fest verdrahtete Liste wäre nach dem ersten
   neuen Wert falsch — und zwar unbemerkt, weil ein Filter auf eine nicht mehr
   existierende Branche einfach eine leere Auswahl ergibt.

   Mitgezählt wird jeweils, wie viele Werte dahinterstehen. Ohne diese Zahl
   wählt man Branchen aus, in denen ein einziger Wert steckt, und wundert sich
   über das Ergebnis. */
async function loadFacets() {
  const f = await guard(() => api('/api/assets/facets'));
  if (!f) return;

  const fill = (sel, items, allLabel) => {
    const el = $(sel);
    const keep = el.value;

    el.innerHTML = `<option value="">${allLabel}</option>`;

    (items || []).forEach(i => {
      const o = document.createElement('option');
      o.value = i.name;
      o.textContent = `${i.name} (${i.count})`;
      el.appendChild(o);
    });

    // Eine bereits getroffene Wahl darf beim Nachladen nicht verlorengehen.
    if (keep && [...el.options].some(o => o.value === keep)) el.value = keep;
  };

  fill('#chart-sector', f.sectors, 'alle Branchen');
  fill('#chart-country', f.countries, 'alle Länder');
}

function renderAvailable() {
  const ul = $('#chart-available');
  ul.innerHTML = '';
  const selIds = new Set(chartState.selected.map(s => s.assetId));

  for (const a of chartState.available) {
    if (selIds.has(a.assetId)) continue;

    const li = document.createElement('li');
    li.innerHTML =
      `<span class="sym">${a.symbol}</span>` +
      `<span class="nm">${a.name || ''}</span>`;
    li.onclick = () => {
      if (chartState.selected.length >= MAX_SELECTED) {
        setStatus(`Maximal ${MAX_SELECTED} Werte gleichzeitig`, 'err');
        return;
      }
      chartState.selected.push(a);
      renderAvailable();
      renderSelected();
      redrawSoon();
    };
    ul.appendChild(li);
  }

  if (!ul.children.length) {
    const li = document.createElement('li');
    li.className = 'nm';
    li.textContent = chartState.available.length ? 'alle ausgewählt' : 'nichts gefunden';
    ul.appendChild(li);
  }
}

function renderSelected() {
  const ul = $('#chart-selected');
  ul.innerHTML = '';
  $('#sel-count').textContent = chartState.selected.length;

  chartState.selected.forEach((a, i) => {
    const li = document.createElement('li');
    li.innerHTML =
      `<span class="swatch" style="background:${colorFor(i)}"></span>` +
      `<span class="sym">${a.symbol}</span>` +
      `<span class="nm">${a.name || ''}</span>`;
    li.title = 'Klicken zum Entfernen';
    li.onclick = () => {
      chartState.selected.splice(i, 1);
      renderAvailable();
      renderSelected();
      redrawSoon();
    };
    ul.appendChild(li);
  });
}

/* Auswahl geändert heißt: neu zeichnen.

   Vorher geschah das nur beim Klick auf „Anzeigen". Wer einen vierten Wert
   dazunahm, sah weiterhin die drei von vorher — und las das als „es wird immer
   einer weniger gezeigt als ausgewählt". Der Zähler links stand dabei schon auf
   vier, was den Eindruck bestätigte.

   Verzögert, weil man beim Zusammenstellen einer Auswahl mehrmals hintereinander
   klickt. Ohne die Verzögerung ginge für jeden Klick eine Abfrage über mehrere
   tausend Bars hinaus, von denen nur die letzte gebraucht wird. Der
   Zeichenschlüssel (`drawToken`) verwirft ohnehin, was zwischenzeitlich überholt
   wurde — die Verzögerung spart die Anfragen, er sichert die Reihenfolge. */
const redrawSoon = debounce(() => {
  if (chartState.selected.length || chartState.view === 'flow') drawCharts();
  else {
    // Nichts mehr ausgewählt: Diagramme fort, damit nicht der letzte Stand
    // stehen bleibt und wie eine aktuelle Anzeige aussieht.
    destroyPlots();
    $('#legend').innerHTML = '';
    reportSkipped(null);
    $('#charts-empty').style.display = 'block';
  }
}, 350);

function destroyPlots() {
  chartState.plots.forEach(p => p.plot.destroy());
  chartState.plots = [];
  $('#charts').innerHTML = '';
  $('#charts').classList.remove('grid-mode');
}

/* Laufende Zeichenanforderung. Jeder Aufruf zieht eine neue Nummer; wer beim
   Einhängen der Diagramme feststellt, dass inzwischen eine neuere Anforderung
   läuft, bricht ab.

   Ohne diese Sperre entstehen beim schnellen Umschalten doppelte Diagramme:
   Aufruf A leert den Bereich und wartet auf seine Daten, Aufruf B leert
   erneut und hängt seine Diagramme an — danach hängt A seine ebenfalls an,
   und beide stehen untereinander. */
let drawToken = 0;

function isStale(token) {
  return token !== drawToken;
}

async function drawCharts() {
  /* Die Flussansicht kommt ohne Auswahl aus — sie betrachtet standardmäßig
     den gesamten verfolgten Markt. Bei allen anderen wäre ein leeres
     Diagramm die Folge. */
  /* Das Depot kommt ohne Auswahl aus: Es zeigt immer auch die Werte, in denen
     etwas steckt. Waere es an die Auswahl gebunden, verschwaende eine Position
     aus der Ansicht, sobald jemand die Auswahl aendert -- das sieht aus wie
     verlorenes Geld und ist der Grund, warum diese Ansicht hier steht. */
  const needsSelection = chartState.view !== 'flow' && chartState.view !== 'invest'
    || chartState.view === 'flow' && flowScope() === 'sel';

  if (needsSelection && !chartState.selected.length) {
    setStatus('Erst Werte auswählen', 'err');
    return;
  }

  const token = ++drawToken;

  destroyPlots();
  $('#matrix').innerHTML = '';
  $('#flow').innerHTML = '';
  $('#invest').innerHTML = '';
  $('#charts-empty').style.display = 'none';

  /* Den Hinweis über ausgelassene Werte zuerst fortnehmen.

     Er wird erst gesetzt, wenn die Antwort da ist. Bliebe der alte stehen,
     stünde nach einem Wechsel auf Tagesauflösung immer noch „PEPE-USD hat keine
     1h-Bars" — eine Meldung über einen Zustand, den es nicht mehr gibt. */
  reportSkipped(null);

  switch (chartState.view) {
    case 'candle': return drawOhlcView('candle', token);
    case 'volume': return drawOhlcView('volume', token);
    case 'matrix': return drawMatrix(token);
    case 'flow': return drawFlow(token);
    case 'invest': return drawInvest(token);
    default: return drawLineView(token);
  }
}

/* Meldet Werte, für die keine Linie entstand — mit dem Grund des Servers. */
function reportSkipped(skipped) {
  const host = $('#charts-skipped');

  if (!host) return;

  if (!skipped || !skipped.length) {
    host.innerHTML = '';
    host.style.display = 'none';
    return;
  }

  host.style.display = '';

  /* Der Rat muss zur Lage passen.

     Vorher stand hier immer „mit Tagesintervall erscheinen sie meist" — auch
     dann, wenn das Diagramm bereits auf Tagesintervall stand. Ein Hinweis, der
     zu etwas rät, das der Nutzer gerade tut, ist schlimmer als keiner: Er
     schickt ihn auf eine Fährte, die es nicht gibt. */
  const rat = chartState.interval === '1h'
    ? 'Junge Kryptowerte haben oft keine Stundendaten — mit Tagesintervall '
    + 'erscheinen sie meist.'
    : 'Auch bei Tagesauflösung keine Daten. Das sind meist eingestellte oder nie '
    + 'gehandelte Symbole, die der Universum-Lauf mitführt — sie lassen sich '
    + 'unter „Auswahl" aus der Verfolgung nehmen.';

  host.innerHTML =
    `<b>${skipped.length} von der Auswahl nicht dargestellt:</b> ` +
    skipped.map(s => `${s.symbol} <span class="dim">(${s.reason})</span>`).join(' · ') +
    `<br><span class="dim">${rat}</span>`;
}

/* ------------------------------------------------------------ Linienansicht */

async function drawLineView(token) {
  const ids = chartState.selected.map(s => s.assetId).join(',');
  const rebase = $('#rebase').checked;

  const params = rangeParams();
  params.set('ids', ids);
  params.set('interval', chartState.interval);
  params.set('rebase', String(rebase));
  params.set('forecast', String($('#show-forecast').checked));

  /* Die Säulengewichte gehen mit an den Server.

     Ohne sie kommt die Prognose allein aus dem gespeicherten Ensemble, und die
     Regler unter „Säulen" wären ein Bedienelement, das aussieht, als täte es
     etwas. Mit ihnen mischt der Server nach Gewicht × Verdienst — wobei der
     Verdienst aus dem Sperrbereich kommt und nicht aus der Einstellung. */
  if ($('#apply-weights')?.checked) {
    const g = PILLAR_KEYS
      .map(k => {
        const on = $(`.pillar-on[data-key="${k}"]`);
        const w = $(`.pillar-weight[data-key="${k}"]`);
        return `${k}:${on && !on.checked ? 0 : (w?.value || 0)}`;
      })
      .join(',');

    params.set('gewichte', g);
  }

  if ($('#show-forecast-past').checked) {
    const hs = $$('#fc-horizons button.active').map(b => b.dataset.h);
    if (hs.length) {
      params.set('forecastPast', 'true');
      params.set('fcHorizons', hs.join(','));
    }
  }

  if (asOfDates.length) params.set('asOf', asOfDates.join(','));

  const data = await guard(() => api('/api/series/?' + params));
  if (!data || isStale(token)) return;

  /* Ausgelassene Werte gehoeren sichtbar gemeldet.

     Vorher verschwanden sie kommentarlos: Wer drei Werte waehlte und zwei
     Linien sah, suchte den Fehler in der Auswahl oder im Diagramm. Tatsaechlich
     fehlten die Bars -- meist, weil ein junges Kryptosymbol keine Stundendaten
     hat, das Diagramm aber auf Stundenintervall steht. Das kann der Nutzer
     nicht erraten, und die Oberflaeche darf es nicht verschweigen. */
  reportSkipped(data.skipped);

  if (!data.series.length) {
    $('#charts-empty').style.display = 'block';
    $('#legend').innerHTML = '';
    setStatus('Keine Daten für die Auswahl — evtl. noch nicht abgeholt', 'err');
    return;
  }

  // uPlot erwartet Sekunden, die API liefert Millisekunden.
  const xs = data.timestamps.map(t => t / 1000);

  renderLegend(data.series, rebase);

  const container = $('#charts');
  container.classList.toggle('grid-mode', chartState.mode === 'grid');

  /* Zwei Durchgänge, und das ist wesentlich: uPlot bekommt seine Breite als
     feste Pixelzahl. Würde sie direkt nach dem Einhängen der jeweiligen Box
     gemessen, läge für die erste Box erst ein einziges Grid-Kind vor — auto-fit
     streckt es dann über die volle Containerbreite. Sobald die nächste Box
     dazukommt, bricht das Grid auf mehrere Spalten um, das bereits gezeichnete
     Canvas behält aber seine alte Breite und ragt über die Nachbarn. */
  const pending = [];

  if (chartState.mode === 'overlay') {
    if (data.series.length > OVERLAY_ADVISORY) {
      setStatus(`${data.series.length} Linien übereinander — „nebeneinander" ist ab etwa ${OVERLAY_ADVISORY} meist lesbarer`);
    }
    pending.push(createBox(container, data.series, rebase,
      rebase ? 'Alle Werte, Start = 100' : 'Alle Werte, Originalskala'));
  } else {
    data.series.forEach((s, i) => {
      pending.push(createBox(container, [s], rebase, `${s.symbol} — ${s.name || ''}`, i));
    });
  }

  pending.forEach(p => renderPlot(p, xs, rebase));
}

/* ----------------------------------------------- Kerzen- und Volumenansicht */

/* Beide brauchen OHLCV je Wert, nicht die vereinheitlichte Serie. Kerzen
   lassen sich nicht sinnvoll überlagern — jeder Wert bekommt ein eigenes Feld. */
async function drawOhlcView(kind, token) {
  const container = $('#charts');
  container.classList.add('grid-mode');

  const range = rangeParams();
  range.set('interval', chartState.interval);

  const results = await Promise.all(chartState.selected.map(a =>
    guard(() => api(`/api/series/${a.assetId}?${range}`))));

  if (isStale(token)) return;

  const valid = results.filter(r => r && r.bars && r.bars.length);
  if (!valid.length) {
    $('#charts-empty').style.display = 'block';
    setStatus('Keine Bars für die Auswahl', 'err');
    return;
  }

  renderOhlcLegend(valid, kind);

  // Erst alle Rahmen einhängen, dann zeichnen — siehe Hinweis oben.
  const boxes = valid.map(r => {
    const box = document.createElement('div');
    box.className = 'chart-box';

    const h = document.createElement('div');
    h.className = 'chart-title';
    h.innerHTML = `${r.symbol} — ${r.name || ''}` +
      `<span class="sub">${kind === 'candle' ? 'OHLC' : 'Volumen'}, ${r.count} Bars</span>`;
    box.appendChild(h);

    const host = document.createElement('div');
    box.appendChild(host);
    container.appendChild(box);
    return { box, host, data: r };
  });

  boxes.forEach(b => kind === 'candle' ? renderCandles(b) : renderVolume(b));
}

function renderCandles({ box, host, data }) {
  const bars = data.bars;
  const xs = bars.map(b => b.t / 1000);
  const o = bars.map(b => b.o);
  const h = bars.map(b => b.h);
  const l = bars.map(b => b.l);
  const c = bars.map(b => b.c);

  /* Skala selbst bestimmen: uPlot rechnet ausgeblendete Reihen nicht in den
     Wertebereich ein, die Dochte würden sonst oben und unten abgeschnitten. */
  const lows = l.filter(v => v != null);
  const highs = h.filter(v => v != null);
  if (!lows.length || !highs.length) return;

  const lo = Math.min(...lows);
  const hi = Math.max(...highs);
  const pad = (hi - lo) * 0.06 || 1;

  const plot = new uPlot({
    width: plotWidth(box),
    height: 300,
    scales: { y: { range: [lo - pad, hi + pad] } },
    series: [
      { value: '{YYYY}-{MM}-{DD} {HH}:{mm}' },
      { label: 'Schluss', scale: 'y', show: false }
    ],
    axes: darkAxes(),
    legend: { show: false },
    cursor: { drag: { x: false, y: false } },
    plugins: [zoomPanPlugin()],
    hooks: { draw: [u => paintCandles(u, o, h, l, c)] }
  }, [xs, c], host);

  chartState.plots.push({ plot, box });
}

/* uPlot kennt keine Kerzen — sie werden im draw-Haken selbst gezeichnet. */
function paintCandles(u, o, h, l, c) {
  const ctx = u.ctx;
  const idxs = u.series[0].idxs;
  if (!idxs) return;

  const i0 = idxs[0], i1 = idxs[1];
  if (i0 == null || i1 == null) return;

  ctx.save();
  ctx.beginPath();
  ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height);
  ctx.clip();

  const count = Math.max(1, i1 - i0);
  const bodyW = Math.max(1, Math.min(14, (u.bbox.width / count) * 0.62));

  for (let i = i0; i <= i1; i++) {
    if (o[i] == null || c[i] == null) continue;

    const x = u.valToPos(u.data[0][i], 'x', true);
    const yO = u.valToPos(o[i], 'y', true);
    const yC = u.valToPos(c[i], 'y', true);
    const up = c[i] >= o[i];

    ctx.strokeStyle = up ? UP_COLOR : DOWN_COLOR;
    ctx.fillStyle = up ? UP_COLOR : DOWN_COLOR;

    // Docht von Hoch nach Tief
    if (h[i] != null && l[i] != null) {
      ctx.beginPath();
      ctx.lineWidth = Math.max(1, bodyW * 0.12);
      ctx.moveTo(x, u.valToPos(h[i], 'y', true));
      ctx.lineTo(x, u.valToPos(l[i], 'y', true));
      ctx.stroke();
    }

    // Körper; bei Eröffnung gleich Schluss bleibt ein Strich stehen.
    const top = Math.min(yO, yC);
    const height = Math.max(1, Math.abs(yC - yO));
    ctx.fillRect(x - bodyW / 2, top, bodyW, height);
  }

  ctx.restore();
}

function renderVolume({ box, host, data }) {
  const bars = data.bars;
  const xs = bars.map(b => b.t / 1000);
  const v = bars.map(b => (b.v == null ? null : Number(b.v)));
  const c = bars.map(b => b.c);
  const o = bars.map(b => b.o);

  const known = v.filter(x => x != null && x > 0);
  if (!known.length) {
    host.innerHTML = '<p class="hint">Für diesen Wert liegt kein Volumen vor.</p>';
    return;
  }

  /* Wertebereich selbst setzen. Die Datenreihe steht auf show:false, weil die
     Balken im draw-Haken gezeichnet werden — und uPlot rechnet ausgeblendete
     Reihen NICHT in den Achsenbereich ein. Ohne diese Angabe bliebe die Skala
     undefiniert und die Balken landeten außerhalb des sichtbaren Bereichs:
     das Diagramm wäre leer. */
  const maxV = Math.max(...known);

  const plot = new uPlot({
    width: plotWidth(box),
    height: 240,
    scales: { y: { range: [0, maxV * 1.05] } },
    series: [
      { value: '{YYYY}-{MM}-{DD} {HH}:{mm}' },
      { label: 'Volumen', scale: 'y', show: false }
    ],
    axes: darkAxes(fmtCompact),
    legend: { show: false },
    cursor: { drag: { x: false, y: false } },
    plugins: [zoomPanPlugin()],
    hooks: { draw: [u => paintVolume(u, v, o, c)] }
  }, [xs, v], host);

  chartState.plots.push({ plot, box });
}

/* Einfärbung nach Bar-Richtung: so wird sichtbar, ob hohes Volumen steigende
   oder fallende Kurse begleitet hat — der erste Schritt zum Kapitalfluss. */
function paintVolume(u, v, o, c) {
  const ctx = u.ctx;
  const idxs = u.series[0].idxs;
  if (!idxs) return;

  const i0 = idxs[0], i1 = idxs[1];
  if (i0 == null || i1 == null) return;

  ctx.save();
  ctx.beginPath();
  ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height);
  ctx.clip();

  const count = Math.max(1, i1 - i0);
  const barW = Math.max(1, Math.min(14, (u.bbox.width / count) * 0.7));

  // Nulllinie über die Skala bestimmen, nicht über den Kastenrand — so stimmt
  // die Grundlinie auch, wenn der Bereich später einmal nicht bei null beginnt.
  const base = u.valToPos(0, 'y', true);

  ctx.globalAlpha = 0.75;

  for (let i = i0; i <= i1; i++) {
    if (v[i] == null) continue;

    const x = u.valToPos(u.data[0][i], 'x', true);
    const y = u.valToPos(v[i], 'y', true);
    const up = o[i] == null || c[i] == null ? true : c[i] >= o[i];

    ctx.fillStyle = up ? UP_COLOR : DOWN_COLOR;
    ctx.fillRect(x - barW / 2, y, barW, Math.max(1, base - y));
  }

  ctx.globalAlpha = 1;
  ctx.restore();
}


/* ------------------------------------------------------------ Kapitalfluss */

function flowScope() {
  const b = $('#flow-scope button.active');
  return b ? b.dataset.scope : 'all';
}

/* Zeigt, wie sich der Umsatz im Markt verteilt und ob unterm Strich Geld
   hinein- oder abfließt.

   Die Netto-Größe stammt aus der Lage des Schlusskurses in der Tagesspanne:
   schließt ein Wert nahe dem Hoch, wurde eingesammelt, nahe dem Tief
   abgegeben. Mit dem Geldumsatz gewichtet ergibt das ein Maß für Kaufdruck.
   Es ist ausdrücklich KEIN beobachteter Geldfluss — wer von wem gekauft hat,
   steht in Kursdaten nicht. */
async function drawFlow(token) {
  const params = rangeParams();
  params.set('interval', chartState.interval);

  if (flowScope() === 'sel') {
    if (!chartState.selected.length) {
      setStatus('Für „nur Auswahl" erst Werte wählen', 'err');
      return;
    }
    params.set('ids', chartState.selected.map(s => s.assetId).join(','));
  }

  const d = await guard(() => api('/api/flow/market?' + params));
  if (!d || isStale(token)) return;

  if (!d.points || !d.points.length) {
    $('#charts-empty').style.display = 'block';
    setStatus('Keine Umsatzdaten im Zeitraum', 'err');
    return;
  }

  renderFlowSummary(d);
  renderFlowChart(d);

  // Beitragende und Rotationsverdacht nachladen — sie bremsen den Aufbau nicht.
  const idsParam = flowScope() === 'sel'
    ? '&ids=' + chartState.selected.map(s => s.assetId).join(',')
    : '';

  const [contrib, rot] = await Promise.all([
    guard(() => api(`/api/flow/contributors?interval=${chartState.interval}&recentDays=30&limit=12${idsParam}`)),
    guard(() => api(`/api/flow/rotation-pairs?interval=${chartState.interval}&months=6&limit=12${idsParam}`))
  ]);

  if (isStale(token)) return;

  if (contrib) renderContributors(contrib);
  if (rot) renderRotation(rot);
}

function renderFlowSummary(d) {
  const el = $('#legend');
  const netUp = d.totalNet >= 0;

  el.innerHTML =
    `<div class="item"><b>Betrachtungsraum</b>` +
    `<span class="dim">${d.assets} Werte, ${d.scope}</span></div>` +
    `<div class="item"><b>Umsatz gesamt</b>` +
    `<span class="dim">${fmtCompact(d.totalGross)} $</span></div>` +
    `<div class="item"><b>Netto</b>` +
    `<span class="chg ${netUp ? 'up' : 'down'}">${netUp ? '+' : ''}${fmtCompact(d.totalNet)} $</span>` +
    `<span class="dim">${fmtNum(d.netSharePct, 2)} % des Umsatzes</span></div>`;
}

function renderFlowChart(d) {
  const container = $('#charts');
  const box = document.createElement('div');
  box.className = 'chart-box';

  const h = document.createElement('div');
  h.className = 'chart-title';
  h.innerHTML = 'Kapitalfluss im Markt' +
    '<span class="sub">Balken: Netto je Bar · Linie: aufsummiert</span>';
  box.appendChild(h);

  const host = document.createElement('div');
  box.appendChild(host);
  container.appendChild(box);

  const xs = d.points.map(p => p.t / 1000);
  const net = d.points.map(p => p.netFlow);
  const cum = d.points.map(p => p.cumulativeNet);

  const lo = Math.min(...net, 0);
  const hi = Math.max(...net, 0);
  const pad = (hi - lo) * 0.08 || 1;

  const plot = new uPlot({
    width: plotWidth(box),
    height: 380,
    scales: {
      y: { range: [lo - pad, hi + pad] },
      // Eigene Achse rechts: die aufsummierte Größe ist um Größenordnungen
      // größer als die Tageswerte und würde die Balken sonst plattdrücken.
      cum: { range: (u, min, max) => [min, max] }
    },
    series: [
      { value: '{YYYY}-{MM}-{DD} {HH}:{mm}' },
      { label: 'Netto', scale: 'y', show: false },
      {
        label: 'aufsummiert',
        scale: 'cum',
        stroke: '#4c9aff',
        width: 2,
        value: (u, v) => (v === null ? '–' : fmtCompact(v) + ' $')
      }
    ],
    axes: [
      { stroke: '#8b97a6', grid: { stroke: '#2a323d', width: 1 }, ticks: { stroke: '#2a323d' }, font: AXIS_FONT },
      { stroke: '#8b97a6', grid: { stroke: '#2a323d', width: 1 }, ticks: { stroke: '#2a323d' },
        font: AXIS_FONT, values: (u, t) => t.map(fmtCompact), size: 70 },
      { scale: 'cum', side: 1, stroke: '#4c9aff', grid: { show: false },
        font: AXIS_FONT, values: (u, t) => t.map(fmtCompact), size: 70 }
    ],
    legend: { show: false },
    cursor: { drag: { x: false, y: false } },
    plugins: [zoomPanPlugin()],
    hooks: { draw: [u => paintNetBars(u, net)] }
  }, [xs, net, cum], host);

  chartState.plots.push({ plot, box });
}

/* Balken um die Nulllinie: grün nach oben heißt Kaufdruck, rot nach unten
   Verkaufsdruck. */
function paintNetBars(u, net) {
  const ctx = u.ctx;
  const idxs = u.series[0].idxs;
  if (!idxs) return;

  const i0 = idxs[0], i1 = idxs[1];
  if (i0 == null || i1 == null) return;

  ctx.save();
  ctx.beginPath();
  ctx.rect(u.bbox.left, u.bbox.top, u.bbox.width, u.bbox.height);
  ctx.clip();

  const count = Math.max(1, i1 - i0);
  const w = Math.max(1, Math.min(12, (u.bbox.width / count) * 0.7));
  const zero = u.valToPos(0, 'y', true);

  ctx.globalAlpha = 0.8;

  for (let i = i0; i <= i1; i++) {
    if (net[i] == null) continue;

    const x = u.valToPos(u.data[0][i], 'x', true);
    const y = u.valToPos(net[i], 'y', true);

    ctx.fillStyle = net[i] >= 0 ? UP_COLOR : DOWN_COLOR;
    ctx.fillRect(x - w / 2, Math.min(y, zero), w, Math.max(1, Math.abs(zero - y)));
  }

  ctx.globalAlpha = 1;
  ctx.restore();
}

function renderContributors(d) {
  const wrap = $('#flow');

  const table = (title, rows, note) => {
    const box = document.createElement('div');
    box.className = 'card';

    const body = rows.map(r => `<tr>
      <td><b>${r.symbol}</b></td>
      <td class="dim">${CLASS_LABEL[r.assetClass] || r.assetClass}${r.sector ? ' · ' + r.sector : ''}</td>
      <td class="num ${r.netFlow >= 0 ? 'up' : 'down'}">${fmtCompact(r.netFlow)} $</td>
      <td class="num">${fmtNum(r.sharePct, 3)} %</td>
      <td class="num ${r.shareChangePct >= 0 ? 'up' : 'down'}">${r.shareChangePct >= 0 ? '+' : ''}${fmtNum(r.shareChangePct, 1)} %</td>
    </tr>`).join('');

    box.innerHTML = `<h3>${title}</h3>${note ? `<p class="hint">${note}</p>` : ''}
      <table class="grid">
        <thead><tr><th>Symbol</th><th>Klasse</th><th class="num">Netto</th>
          <th class="num">Anteil</th><th class="num">Anteil Δ</th></tr></thead>
        <tbody>${body}</tbody>
      </table>`;
    return box;
  };

  const cols = document.createElement('div');
  cols.className = 'cols';
  cols.appendChild(table('Kapital fließt zu', d.zufluss,
    `Kaufdruck über ${d.recentDays} Tage — Schluss nahe dem Tageshoch bei hohem Umsatz.`));
  cols.appendChild(table('Kapital fließt ab', d.abfluss,
    'Verkaufsdruck — Schluss nahe dem Tagestief bei hohem Umsatz.'));
  wrap.appendChild(cols);
}

function renderRotation(d) {
  const wrap = $('#flow');

  const box = document.createElement('div');
  box.className = 'card';

  const rows = (d.pairs || []).map(p => `<tr>
    <td><b>${p.symbolA}</b></td>
    <td><b>${p.symbolB}</b></td>
    <td class="num down">${fmtNum(p.score, 3)}</td>
    <td class="num">${p.shareChangeA >= 0 ? '+' : ''}${fmtNum(p.shareChangeA, 3)} %</td>
    <td class="num">${p.shareChangeB >= 0 ? '+' : ''}${fmtNum(p.shareChangeB, 3)} %</td>
  </tr>`).join('');

  box.innerHTML = `<h3>Rotationsverdacht</h3>
    <p class="hint">${d.hinweis || ''}</p>
    <p class="hint">Gewinnt der eine regelmäßig Umsatzanteil, während der andere
      verliert, ergibt sich ein stark negativer Wert. Verglichen wird nur an
      Zeitpunkten, an denen <b>beide</b> gehandelt haben — sonst misst die
      Rechnung den Handelskalender statt der Rotation.</p>
    <table class="grid">
      <thead><tr><th>A</th><th>B</th><th class="num">Score</th>
        <th class="num">Anteil A Δ</th><th class="num">Anteil B Δ</th></tr></thead>
      <tbody>${rows || '<tr><td colspan="5" class="dim">Nichts Auffälliges gefunden.</td></tr>'}</tbody>
    </table>`;

  wrap.appendChild(box);
}

/* ------------------------------------------------------- Korrelationsmatrix */

/* Die Ansicht, die den Blick vom einzelnen Kurs auf das Geflecht lenkt:
   welche Werte laufen gemeinsam, welche gegenläufig, und wer läuft wem voraus. */
async function drawMatrix(token) {
  if (chartState.selected.length < 2) {
    setStatus('Für die Korrelation mindestens zwei Werte wählen', 'err');
    $('#charts-empty').style.display = 'block';
    return;
  }

  const params = rangeParams();
  params.set('ids', chartState.selected.map(s => s.assetId).join(','));
  params.set('interval', chartState.interval);

  const d = await guard(() => api('/api/analysis/matrix?' + params));
  if (!d || isStale(token)) return;

  $('#legend').innerHTML = '';
  const wrap = $('#matrix');
  const n = d.assets.length;

  const table = document.createElement('table');
  table.className = 'heatmap';

  const head = document.createElement('tr');
  head.appendChild(document.createElement('th'));
  d.assets.forEach(a => {
    const th = document.createElement('th');
    th.className = 'rot';
    th.innerHTML = `<span>${a.symbol}</span>`;
    head.appendChild(th);
  });
  table.appendChild(head);

  for (let i = 0; i < n; i++) {
    const tr = document.createElement('tr');
    const th = document.createElement('th');
    th.textContent = d.assets[i].symbol;
    th.title = d.assets[i].name || '';
    tr.appendChild(th);

    for (let j = 0; j < n; j++) {
      const td = document.createElement('td');
      const val = d.corr[i][j];
      const lag = d.lags[i][j];

      if (val == null) {
        td.className = 'na';
        td.textContent = '–';
        td.title = 'Kein gemeinsamer Zeitraum berechnet';
      } else {
        td.style.background = corrColor(val);
        td.textContent = val.toFixed(2);

        const lagText = lag
          ? `Vorlauf ${Math.abs(lag)} Bars — ` +
            (lag > 0 ? `${d.assets[i].symbol} läuft voraus` : `${d.assets[j].symbol} läuft voraus`)
          : 'gleichzeitig';

        td.title = `${d.assets[i].symbol} / ${d.assets[j].symbol}\nKorrelation ${val.toFixed(3)}\n${lagText}`;
        if (Math.abs(val) > 0.55) td.classList.add('strong');
      }
      tr.appendChild(td);
    }
    table.appendChild(tr);
  }

  wrap.appendChild(table);

  const legend = document.createElement('p');
  legend.className = 'hint';
  legend.innerHTML =
    'Korrelation der Log-Renditen: <b style="color:' + UP_COLOR + '">grün</b> gleichlaufend, ' +
    '<b style="color:' + DOWN_COLOR + '">rot</b> gegenläufig, blass = kein Zusammenhang. ' +
    'Zeiger über ein Feld zeigt zusätzlich den Vorlauf — wer sich zuerst bewegt.';
  wrap.appendChild(legend);
}

function corrColor(v) {
  const a = Math.min(1, Math.abs(v));
  return v >= 0
    ? `rgba(53, 196, 107, ${0.08 + a * 0.62})`
    : `rgba(242, 84, 91, ${0.08 + a * 0.62})`;
}

/* ------------------------------------------------------------------ Hilfen */

/* Zeitraum als Abfrageparameter. Ein ausdrücklich gesetztes Datum hat Vorrang;
   sonst gilt die Schnellwahl in Monaten. Dieselbe Auflösung nutzen ALLE
   Ansichten — auch die Korrelation, die deshalb live gerechnet wird. */
function rangeParams() {
  const from = $('#from-date').value;
  const to = $('#to-date').value;
  const months = parseInt($('#months').value, 10) || 12;

  const p = new URLSearchParams();

  if (from) p.set('from', from + 'T00:00:00');
  if (to) p.set('to', to + 'T23:59:59');

  // 0 steht für „alles" — die Schnittstelle deckelt bei 1200 Monaten.
  if (!from) p.set('months', String(months === 0 ? 1200 : months));

  return p;
}

function readMonths() {
  const m = parseInt($('#months').value, 10) || 12;
  return m === 0 ? 1200 : m;
}

function plotWidth(box) {
  return Math.max(280, box.clientWidth - 22);
}

const AXIS_FONT = '12px "Segoe UI", system-ui, -apple-system, sans-serif';

/* Eigene Leinwand nur zum Messen. uPlots Zeichenkontext ist mit dem
   Geräte-Pixelverhältnis skaliert; hier wird in CSS-Pixeln gemessen, und genau
   die erwartet uPlot für die Achsenbreite. */
const _measureCtx = document.createElement('canvas').getContext('2d');

function textWidth(s) {
  _measureCtx.font = AXIS_FONT;
  return _measureCtx.measureText(String(s)).width;
}

/** Tausenderpunkte, aber ohne unnötige Nachkommastellen. */
function fmtAxisNumber(v) {
  if (v == null) return '';
  const a = Math.abs(v);

  // Unter 1 braucht es Nachkommastellen, sonst stünde bei Cent-Werten nur 0.
  const digits = a >= 100 ? 0 : a >= 1 ? 2 : a >= 0.01 ? 4 : 8;

  return Number(v).toLocaleString('de-DE', {
    minimumFractionDigits: 0,
    maximumFractionDigits: digits
  });
}

function darkAxes(valueFmt) {
  const base = {
    stroke: '#8b97a6',
    grid: { stroke: '#2a323d', width: 1 },
    ticks: { stroke: '#2a323d' },
    font: AXIS_FONT
  };

  const fmt = valueFmt || fmtAxisNumber;

  const y = Object.assign({}, base, {
    values: (u, ticks) => ticks.map(fmt),

    /* Achsenbreite an die längste Beschriftung anpassen. uPlot reserviert
       sonst pauschal 50 Pixel — bei Bitcoin (120.000) oder Volumen in
       Milliarden wird die Zahl dann links abgeschnitten und zeigt „20.000"
       statt „120.000". */
    size: (u, values) => {
      if (!values || !values.length) return 50;

      let widest = 0;
      for (const v of values) widest = Math.max(widest, textWidth(v));

      // Zuschlag für Teilstrich und Abstand zum Diagramm.
      return Math.min(120, Math.ceil(widest) + 16);
    }
  });

  return [base, y];
}

function fmtCompact(v) {
  if (v == null) return '';
  const a = Math.abs(v);
  if (a >= 1e9) return (v / 1e9).toFixed(1) + ' Mrd';
  if (a >= 1e6) return (v / 1e6).toFixed(1) + ' Mio';
  if (a >= 1e3) return (v / 1e3).toFixed(0) + ' Tsd';
  return String(Math.round(v));
}

const avg = arr => (arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : 0);

/* Geldumsatz einer Bar. Die Einheit des Volumens hängt an der Anlageklasse:
   Aktien und ETFs melden Stück, Krypto meldet bereits Dollar. Ohne diese
   Unterscheidung stünde für Bitcoin das Siebzigtausendfache in der Legende —
   dieselbe Regel wie serverseitig in FlowMetrics. */
function moneyFlow(assetClass, bar) {
  const v = Number(bar.v || 0);
  if (!v) return 0;
  return assetClass === 'Crypto' ? v : v * Number(bar.c || 0);
}

function renderOhlcLegend(list, kind) {
  const el = $('#legend');
  el.innerHTML = '';

  list.forEach(r => {
    const bars = r.bars;
    const first = bars[0];
    const last = bars[bars.length - 1];
    const chg = first.c > 0 ? (last.c - first.c) / first.c * 100 : 0;
    const up = chg >= 0;

    const div = document.createElement('div');
    div.className = 'item';
    div.innerHTML =
      `<b>${r.symbol}</b>` +
      `<span class="dim">${CLASS_LABEL[r.assetClass] || r.assetClass}</span>` +
      `<span class="chg ${up ? 'up' : 'down'}">${up ? '+' : ''}${fmtNum(chg, 2)} %</span>` +
      (kind === 'volume'
        ? `<span class="dim">Ø ${fmtCompact(avg(bars.map(b => moneyFlow(r.assetClass, b))))} $ Umsatz</span>`
        : `<span class="dim">${fmtNum(last.c, 2)} ${r.currency || ''}</span>`);
    el.appendChild(div);
  });
}


/* ------------------------------------------------------- Zoomen und Schieben

   Belegung wie in gängigen Chart-Anwendungen:

     Ziehen          verschiebt den Ausschnitt nach links und rechts
     Mausrad         zoomt um die Zeigerposition — nicht um die Mitte, sonst
                     „wandert" die Stelle weg, die man ansehen will
     Umschalt+Rad    verschiebt (praktisch am Notebook)
     waagrechtes Rad verschiebt (Trackpad, Zweifingerwischen)
     Doppelklick     setzt auf den vollen Bereich zurück

   uPlots eigenes Aufziehen zum Zoomen ist dafür abgeschaltet: Ziehen kann nur
   eines von beidem sein, und Verschieben ist die Geste, die man am häufigsten
   braucht. Zoomen übernimmt das Rad.

   Der Zoom wirkt nur auf die Zeitachse. Die Y-Achse skaliert sich weiterhin
   selbst — bei Kursen will man den sichtbaren Bereich ausgefüllt sehen. */
function zoomPanPlugin() {
  return {
    hooks: {
      ready: u => {
        const over = u.over;
        const xs = u.data[0];
        if (!xs || xs.length < 2) return;

        const full = { min: xs[0], max: xs[xs.length - 1] };
        const total = full.max - full.min;

        // Nicht weiter hinein als auf etwa zehn Datenpunkte.
        const minSpan = Math.max(total / Math.max(10, xs.length) * 10, 1);

        const span = () => {
          const s = u.scales.x;
          return (s.max ?? full.max) - (s.min ?? full.min);
        };

        /** Setzt den Ausschnitt und hält ihn im Datenbereich. */
        function apply(min, max) {
          const width = Math.min(total, Math.max(minSpan, max - min));

          if (min < full.min) { min = full.min; max = min + width; }
          if (max > full.max) { max = full.max; min = max - width; }
          if (min < full.min) min = full.min;

          u.setScale('x', { min, max });
        }

        /** Verschiebt um einen Anteil der sichtbaren Breite. */
        function shiftBy(fraction) {
          const s = u.scales.x;
          const min = s.min ?? full.min;
          const max = s.max ?? full.max;
          const d = (max - min) * fraction;
          apply(min + d, max + d);
        }

        /*  FADENKREUZ, nicht Greifhand.

            `grab` ueber der ganzen Flaeche sagt „hier wird gezogen" und
            verdeckt, dass dieselbe Flaeche vor allem zum Ablesen da ist. Die
            Hand erschien auch dann, wenn gar nichts zu ziehen war, und liess
            den Eindruck entstehen, der Zeiger reagiere nicht mehr auf die
            einzelnen Kurse. Verschoben wird weiterhin mit gedrueckter Taste --
            erst dann wechselt die Form auf `grabbing`, und das ist der
            Moment, in dem sie etwas aussagt.                                 */
        const RUHE = 'crosshair';
        over.style.cursor = RUHE;

        // ------------------------------------------------------------ Rad
        over.addEventListener('wheel', e => {
          e.preventDefault();

          const s = u.scales.x;
          const min = s.min ?? full.min;
          const max = s.max ?? full.max;
          const width = max - min;
          if (!(width > 0)) return;

          /* Waagrechtes Wischen oder Umschalt+Rad verschiebt, statt zu zoomen.
             Am Trackpad ist das die natürliche Geste. */
          if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
            const amount = (e.deltaX || e.deltaY) > 0 ? 0.15 : -0.15;
            shiftBy(amount);
            return;
          }

          const rect = over.getBoundingClientRect();
          const frac = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width));
          const anchor = min + width * frac;

          const newWidth = width * (e.deltaY < 0 ? 0.8 : 1.25);
          apply(anchor - newWidth * frac, anchor - newWidth * frac + newWidth);
        }, { passive: false });

        // -------------------------------------------------------- Ziehen
        let dragging = false;
        let startX = 0;
        let startMin = 0;
        let startMax = 0;

        over.addEventListener('mousedown', e => {
          if (e.button !== 0 && e.button !== 1) return;

          e.preventDefault();
          dragging = true;
          startX = e.clientX;

          const s = u.scales.x;
          startMin = s.min ?? full.min;
          startMax = s.max ?? full.max;

          over.style.cursor = 'grabbing';
        });

        window.addEventListener('mousemove', e => {
          if (!dragging) return;

          const rect = over.getBoundingClientRect();
          const width = startMax - startMin;
          const shift = (e.clientX - startX) / rect.width * width;

          apply(startMin - shift, startMax - shift);
        });

        window.addEventListener('mouseup', () => {
          if (!dragging) return;
          dragging = false;
          over.style.cursor = RUHE;
        });

        // --------------------------------------------------- Zurücksetzen
        over.addEventListener('dblclick', () => apply(full.min, full.max));

        // ------------------------------------------------------ Tastatur
        over.tabIndex = 0;
        over.addEventListener('keydown', e => {
          if (e.key === 'ArrowLeft') { e.preventDefault(); shiftBy(-0.15); }
          else if (e.key === 'ArrowRight') { e.preventDefault(); shiftBy(0.15); }
          else if (e.key === 'Home') { e.preventDefault(); apply(full.min, full.min + span()); }
          else if (e.key === 'End') { e.preventDefault(); apply(full.max - span(), full.max); }
        });
      }
    }
  };
}

/// Legt Rahmen und Überschrift an, zeichnet aber noch nicht.
function createBox(container, series, rebase, title, colorOffset) {
  const box = document.createElement('div');
  box.className = 'chart-box';

  const h = document.createElement('div');
  h.className = 'chart-title';
  const achse = $('#logscale')?.checked ? ', logarithmische Achse' : '';
  h.innerHTML = title
    + `<span class="sub">${rebase ? 'indexiert (Start = 100)' : 'Originalwerte'}${achse}</span>`;
  box.appendChild(h);

  const plotHost = document.createElement('div');
  box.appendChild(plotHost);
  container.appendChild(box);

  return { box, plotHost, series, colorOffset };
}

function renderPlot({ box, plotHost, series, colorOffset }, xs, rebase) {
  const cols = [xs];
  const seriesOpts = [];

  /* Zu jeder Prognosereihe der Weg zurueck zu ihrem Bezugspunkt. Der Index
     entspricht dem Reihenindex in uPlot, damit sich von der Reihe unter dem
     Zeiger unmittelbar auf ihre Herkunft schliessen laesst. */
  const anchorMeta = [];

  series.forEach((s, i) => {
    const color = colorFor(colorOffset === undefined ? i : colorOffset);

    const actualCol = cols.length;
    cols.push(s.values);
    seriesOpts.push({
      label: s.symbol,
      stroke: color,
      width: 1.6,
      spanGaps: true,
      value: (u, v) => (v === null ? '–' : fmtNum(v, rebase ? 2 : 4))
    });

    /* Rückschau: was wurde für diesen Zeitpunkt vorhergesagt, als er noch in
       der Zukunft lag. Fein gepunktet, damit sie den Ist-Verlauf nicht
       verdeckt — der Vergleich der Linien ist der eigentliche Zweck.

       Je länger der Horizont, desto heller und gröber die Strichelung: eine
       Jahresprognose soll erkennbar weniger belastbar wirken als eine
       Tagesprognose. */
    /* Stichtags-Fächer: der Pfad, den das Modell an einem bestimmten Tag
       gezeichnet hat. Kräftige eigene Farbe mit Punkten an den Stützstellen,
       damit er sich klar von Ist-Verlauf und Rückschau abhebt. */
    (s.asOfForecasts || []).forEach((af, k) => {
      anchorMeta[cols.length] = {
        actualCol,
        fixed: af.anchorIdx,
        label: `Stichtag ${af.madeAtUtc.slice(0, 10)}`,
        color: ASOF_COLORS[k % ASOF_COLORS.length]
      };
      cols.push(af.values);
      seriesOpts.push({
        label: `${s.symbol} · Stichtag ${af.madeAtUtc.slice(0, 10)}`,
        stroke: ASOF_COLORS[k % ASOF_COLORS.length],
        width: 2.2,
        dash: [8, 4],
        spanGaps: true,
        points: { show: true, size: 7, stroke: ASOF_COLORS[k % ASOF_COLORS.length], fill: '#0f1216' },
        value: (u, v) => (v === null ? '–' : fmtNum(v, rebase ? 2 : 4))
      });
    });

    (s.pastTracks || []).forEach((tr, k) => {
      /* Bei der Rueckschau hat jeder Punkt seinen eigenen Bezugspunkt: er
         entstand an seinem eigenen Stichtag, einen Horizont frueher. */
      anchorMeta[cols.length] = {
        actualCol,
        anchors: tr.anchorIdx,
        label: `Rückschau ${tr.horizonLabel}`,
        color: lighten(color, 0.35 + Math.min(0.45, k * 0.12))
      };
      cols.push(tr.values);
      seriesOpts.push({
        label: `${s.symbol} · ${tr.horizonLabel}`,
        stroke: lighten(color, 0.35 + Math.min(0.45, k * 0.12)),
        width: 1.3,
        dash: [2 + k * 2, 3 + k],
        spanGaps: true,
        value: (u, v) => (v === null ? '–' : fmtNum(v, rebase ? 2 : 4))
      });
    });

    /* Prognose als eigene Reihe: gestrichelt, dicker und aufgehellt. Bewusst
       deutlich anders als der Ist-Verlauf — eine Prognose darf im Chart nicht
       wie gemessene Wirklichkeit aussehen. */
    if (s.forecast) {
      anchorMeta[cols.length] = {
        actualCol,
        fixed: s.forecastAnchorIdx,
        label: 'Prognose',
        color: lighten(color, 0.45)
      };
      cols.push(s.forecast);
      seriesOpts.push({
        label: s.symbol + ' (Prognose)',
        stroke: lighten(color, 0.45),
        width: 2.6,
        dash: [6, 4],
        spanGaps: true,
        points: { show: true, size: 6, stroke: lighten(color, 0.45), fill: '#0f1216' },
        value: (u, v) => (v === null ? '–' : fmtNum(v, rebase ? 2 : 4))
      });
    }
  });

  // clientWidth statt offsetWidth: der Innenabstand der Box ist bereits
  // abgezogen, nur der Rahmen kommt noch weg.
  const width = box.clientWidth - 22;
  const height = chartState.mode === 'grid' ? 240 : 430;

  /* Logarithmische Achse.

     Die Normalisierung auf 100 macht Größenordnungen vergleichbar, aber sie
     löst nicht das zweite Problem: Entwickeln sich die Verläufe stark
     auseinander, wird die lineare Achse unbrauchbar. Gemessen an einem echten
     Fall -- BTC-USD, ENA-USD und ZEC-USD über ein Jahr:

         ZEC-USD  +1.925 %   →  Index rund 2.000
         BTC-USD    −33 %    →  Index 67
         ENA-USD    −77 %    →  Index 23

     Auf einer Achse von 0 bis 2.400 liegen 67 und 23 beide praktisch auf der
     Nulllinie. Sie sehen flach aus und sind es nicht -- der eine hat ein
     Drittel verloren, der andere drei Viertel. Das liest sich als „die
     Normalisierung funktioniert nicht" und wurde auch so gemeldet.

     Logarithmisch belegt jede Verdopplung denselben Abstand, gleich auf
     welchem Niveau. `distr: 3` ist uPlots Zehnerlogarithmus.

     Nicht positive Werte lassen sich nicht logarithmieren; uPlot lässt sie
     aus. Bei normalisierten Reihen kommt das nicht vor (Start 100, Kurse
     positiv), bei Originalwerten ebenso wenig. */
  const logarithmisch = $('#logscale')?.checked;

  const plot = new uPlot({
    width: Math.max(280, width),
    height,
    series: [{ value: '{YYYY}-{MM}-{DD} {HH}:{mm}' }, ...seriesOpts],
    // Dieselbe Achsendefinition wie die übrigen Ansichten — insbesondere die
    // mitwachsende Breite, damit große Zahlen nicht abgeschnitten werden.
    axes: darkAxes(),
    scales: logarithmisch ? { y: { distr: 3 } } : undefined,
    legend: { show: series.length > 1 },
    cursor: { drag: { x: false, y: false } },
    plugins: [zoomPanPlugin(), hoverPlugin(), anchorPlugin(anchorMeta, rebase)]
  }, cols, plotHost);

  chartState.plots.push({ plot, box });
}


/* Zeigt zu einem Prognosepunkt, von welchem Kurs aus er gerechnet wurde.

   Eine Prognose ohne ihren Bezugspunkt ist schwer zu beurteilen: Die Linie
   sagt "hier staende der Kurs", verschweigt aber, was das Modell zu diesem
   Zeitpunkt ueberhaupt wusste. Bei der Rueckschau faellt das besonders ins
   Gewicht, denn dort hat jeder einzelne Punkt einen anderen Stichtag -- er
   entstand einen Horizont frueher, auf einem anderen Kursniveau. Erst die
   Verbindung zwischen beiden zeigt, welche Bewegung das Modell tatsaechlich
   behauptet hat.

   Gezeichnet wird auf einer eigenen Ebene ueber dem Diagramm. uPlot zeichnet
   seine Leinwand nicht neu, wenn sich nur der Zeiger bewegt -- die Verbindung
   in dessen Zeichenschritt zu haengen hiesse, bei jeder Mausbewegung das
   gesamte Diagramm neu aufzubauen. */
/* ============================================ Werte am Zeiger ==============

   Ein kleiner Kasten, der dem Zeiger folgt und die Werte der Reihen an genau
   dieser Stelle zeigt.

   WARUM ZUSAETZLICH ZUR LEGENDE. uPlots Legende steht unter dem Diagramm und
   fuehrt ALLE Reihen auf -- bei fuenf Werten mit Prognose und Rueckschau sind
   das fuenfzehn Eintraege in drei Zeilen. Wer wissen will, was die Linie unter
   dem Zeiger gerade wert ist, muss sie dort erst suchen; der Blick geht vom
   Kurs weg, und beim Zurueckschauen ist der Zeiger schon woanders. Die Zahl
   gehoert dorthin, wo die Maus steht.

   DIE NAECHSTE REIHE STEHT OBEN UND FETT. Bei sechzehn Reihen ist die Liste
   sonst eine Wand aus Zahlen, in der die eine gesuchte nicht auffaellt --
   sortiert wird deshalb nach dem senkrechten Abstand zum Zeiger.             */
function hoverPlugin(hoechstens = 8) {
  let u = null;
  let kasten = null;

  function verstecke() {
    if (kasten) kasten.style.display = 'none';
  }

  return {
    hooks: {
      ready: uu => {
        u = uu;
        kasten = document.createElement('div');
        kasten.className = 'u-tip';

        /*  Ohne das faengt der Kasten selbst die Maus ab, sobald der Zeiger
            ihn beruehrt -- uPlot bekaeme kein mousemove mehr, der Kasten
            verschwaende, der Zeiger waere wieder darueber, und das Ganze
            flackerte im Kreis.                                              */
        kasten.style.pointerEvents = 'none';

        u.over.appendChild(kasten);
      },

      setCursor: uu => {
        const idx = uu.cursor.idx;
        const links = uu.cursor.left;
        const oben = uu.cursor.top;

        if (idx == null || links == null || links < 0 || oben == null || oben < 0) {
          verstecke();
          return;
        }

        const treffer = [];

        for (let si = 1; si < uu.series.length; si++) {
          const reihe = uu.series[si];
          if (reihe.show === false) continue;

          const v = uu.data[si][idx];
          if (v == null) continue;

          treffer.push({
            name: reihe.label || ('Reihe ' + si),
            farbe: reihe.stroke ? (typeof reihe.stroke === 'function'
                     ? reihe.stroke(uu, si) : reihe.stroke) : '#8b97a6',
            wert: v,
            abstand: Math.abs(uu.valToPos(v, reihe.scale || 'y') - oben)
          });
        }

        if (!treffer.length) { verstecke(); return; }

        treffer.sort((a, b) => a.abstand - b.abstand);

        const zeit = uu.data[0][idx];

        kasten.innerHTML =
          /*  NICHT fmtDate: Das erwartet eine Zeichenkette und ruft darauf
              `endsWith` -- ein Date-Objekt liesse es werfen, und zwar bei
              jeder Mausbewegung.                                             */
          '<div class="u-tip-kopf">'
          + esc(new Date(zeit * 1000).toLocaleString('de-DE')) + '</div>'
          + treffer.slice(0, hoechstens).map((t, i) =>
              '<div class="u-tip-zeile' + (i === 0 ? ' nah' : '') + '">'
              + '<span class="u-tip-punkt" style="background:' + esc(t.farbe) + '"></span>'
              + '<span class="u-tip-name">' + esc(t.name) + '</span>'
              + '<span class="u-tip-wert">' + fmtNum(t.wert, 2) + '</span></div>').join('')
          + (treffer.length > hoechstens
              ? '<div class="u-tip-mehr">und ' + (treffer.length - hoechstens)
                + ' weitere</div>' : '');

        kasten.style.display = 'block';

        /*  An der rechten und unteren Kante umklappen, sonst schiebt der
            Kasten sich aus dem Diagramm und ist genau dort unlesbar, wo man
            am haeufigsten hinzeigt -- am aktuellen Rand.                     */
        const bw = uu.over.clientWidth;
        const bh = uu.over.clientHeight;
        const kw = kasten.offsetWidth;
        const kh = kasten.offsetHeight;

        const x = links + 14 + kw > bw ? links - 14 - kw : links + 14;
        const y = Math.min(Math.max(0, oben + 14), Math.max(0, bh - kh));

        kasten.style.transform = 'translate(' + Math.max(0, x) + 'px,' + y + 'px)';
      },

      destroy: () => { if (kasten) kasten.remove(); }
    }
  };
}

function anchorPlugin(meta, rebase) {
  let u = null;
  let layer = null;
  let ctx = null;

  // Angeheftet bleibt sichtbar, bis woanders geklickt wird.
  let pinned = null;
  let hover = null;

  // Letzte Zeigerposition, um echte Mausbewegung von Skalenwechseln zu trennen.
  let lastLeft = null;
  let lastTop = null;

  const LINE = '#ffcf70';

  function enabled() {
    const box = document.getElementById('show-anchor');
    return box ? box.checked : false;
  }

  function resize() {
    if (!u || !layer) return;

    const dpr = devicePixelRatio || 1;
    const w = u.over.clientWidth;
    const h = u.over.clientHeight;

    layer.width = Math.round(w * dpr);
    layer.height = Math.round(h * dpr);
    layer.style.width = w + 'px';
    layer.style.height = h + 'px';

    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    draw();
  }

  /** Welche Prognosereihe liegt unter dem Zeiger -- und woher stammt sie? */
  function pick() {
    const idx = u.cursor.idx;
    if (idx == null || u.cursor.top == null || u.cursor.top < 0) return null;

    let best = null;
    let bestDist = 26;          // Pixel; darueber gilt nichts als getroffen

    for (let si = 1; si < u.data.length; si++) {
      const m = meta[si];
      if (!m) continue;

      const v = u.data[si][idx];
      if (v == null) continue;

      const py = u.valToPos(v, 'y');
      const d = Math.abs(py - u.cursor.top);
      if (d >= bestDist) continue;

      bestDist = d;
      best = { si, m, idx, value: v, py };
    }

    if (!best) return null;

    /* Bei der Rueckschau steht der Bezugspunkt je Punkt in der Tabelle, bei
       Stichtags- und laufender Prognose gilt einer fuer die ganze Reihe. */
    const a = best.m.anchors ? best.m.anchors[best.idx] : best.m.fixed;
    if (a == null || a < 0) return null;

    const av = u.data[best.m.actualCol][a];
    if (av == null) return null;

    return Object.assign({}, best, { anchorIdx: a, anchorValue: av });
  }

  function draw() {
    if (!ctx) return;

    ctx.clearRect(0, 0, u.over.clientWidth, u.over.clientHeight);

    const hit = enabled() ? (pinned || hover) : null;
    if (!hit) return;

    const w = u.over.clientWidth;
    const h = u.over.clientHeight;

    const ax = u.valToPos(u.data[0][hit.anchorIdx], 'x');
    const ay = u.valToPos(hit.anchorValue, 'y');
    const px = u.valToPos(u.data[0][hit.idx], 'x');
    const py = u.valToPos(hit.value, 'y');

    /* Beim Hineinzoomen liegt der Bezugspunkt fast immer ausserhalb des
       Ausschnitts -- gerade dann, wenn man genau hinschaut. Frueher wurde in
       diesem Fall gar nichts gezeichnet, was wie ein Ausfall der Anzeige wirkte.

       Stattdessen wird die Verbindung am Rand gekappt: Sie zeigt weiterhin in
       die Richtung, in der der Bezugspunkt tatsaechlich liegt, und endet in
       einer Spitze am Rand. Datum und Kurs stehen ohnehin in der Beschriftung,
       die Lage bleibt damit ablesbar, ohne einen Punkt an eine Stelle zu malen,
       an der er nicht ist. */
    const edge = clipToBox(px, py, ax, ay, w, h);

    ctx.save();

    // ------------------------------------------------------- Verbindung
    ctx.strokeStyle = LINE;
    ctx.lineWidth = 1.4;
    ctx.setLineDash([5, 4]);
    ctx.beginPath();
    ctx.moveTo(edge.x, edge.y);
    ctx.lineTo(px, py);
    ctx.stroke();

    if (!edge.clipped) {
      // Senkrechte am Bezugspunkt -- hebt den Zeitpunkt hervor, nicht nur den Kurs.
      ctx.globalAlpha = 0.4;
      ctx.beginPath();
      ctx.moveTo(ax, 0);
      ctx.lineTo(ax, h);
      ctx.stroke();
      ctx.globalAlpha = 1;
    }

    ctx.setLineDash([]);

    // ------------------------------------------------------------ Punkte
    if (edge.clipped) arrow(edge.x, edge.y, ax - px, ay - py);
    else ring(ax, ay, 6, LINE);

    ring(px, py, 5, hit.m.color || LINE);

    // ------------------------------------------------------- Beschriftung
    const dec = rebase ? 2 : 4;
    const pct = hit.anchorValue !== 0
      ? (hit.value - hit.anchorValue) / Math.abs(hit.anchorValue) * 100
      : 0;

    const lines = [
      `Bezug: ${stamp(u.data[0][hit.anchorIdx])}`,
      `${fmtNum(hit.anchorValue, dec)} → ${fmtNum(hit.value, dec)}`,
      `${pct >= 0 ? '+' : ''}${pct.toFixed(2)} % · ${hit.m.label}`
    ];

    if (edge.clipped) lines.push('außerhalb des Ausschnitts');

    /* Liegt der Bezugspunkt im Bild, steht die Beschriftung bei ihm; sonst am
       Prognosepunkt, denn nur der ist noch zu sehen. */
    if (edge.clipped) label(lines, px, py);
    else label(lines, ax, ay);

    ctx.restore();
  }

  /* Kappt die Strecke vom Prognosepunkt zum Bezugspunkt am Rand der
     Zeichenflaeche. Der Prognosepunkt liegt immer innen -- er steht ja unter
     dem Zeiger -- - deshalb genuegt es, den Anteil des Weges zu suchen, bei dem
     die Strecke den Rand erreicht. */
  function clipToBox(px, py, ax, ay, w, h) {
    const dx = ax - px;
    const dy = ay - py;

    const m = 3;                       // Rand, damit die Spitze sichtbar bleibt
    let t = 1;

    const limit = (num, den) => {
      if (den === 0) return;
      const tt = num / den;
      if (tt >= 0 && tt < t) t = tt;
    };

    if (ax < m)     limit(m - px, dx);
    if (ax > w - m) limit(w - m - px, dx);
    if (ay < m)     limit(m - py, dy);
    if (ay > h - m) limit(h - m - py, dy);

    return { x: px + dx * t, y: py + dy * t, clipped: t < 1 };
  }

  /** Spitze am Rand, die dorthin zeigt, wo der Bezugspunkt liegt. */
  function arrow(x, y, dx, dy) {
    const len = Math.hypot(dx, dy) || 1;
    const ux = dx / len;
    const uy = dy / len;

    // Senkrechte zur Richtung, fuer die beiden hinteren Ecken.
    const nx = -uy;
    const ny = ux;

    const size = 8;

    ctx.beginPath();
    ctx.moveTo(x + ux * size, y + uy * size);
    ctx.lineTo(x - ux * size * 0.6 + nx * size * 0.7, y - uy * size * 0.6 + ny * size * 0.7);
    ctx.lineTo(x - ux * size * 0.6 - nx * size * 0.7, y - uy * size * 0.6 - ny * size * 0.7);
    ctx.closePath();

    ctx.fillStyle = LINE;
    ctx.fill();
  }

  function ring(x, y, r, color) {
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fillStyle = '#0f1216';
    ctx.fill();
    ctx.lineWidth = 2;
    ctx.strokeStyle = color;
    ctx.stroke();
  }

  function stamp(sec) {
    // uPlot rechnet die Zeitachse in Sekunden, Date erwartet Millisekunden.
    const d = new Date(sec * 1000);
    const p = n => String(n).padStart(2, '0');
    const day = `${d.getUTCFullYear()}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())}`;

    // Uhrzeit nur, wenn sie etwas beitraegt -- bei Tagesbars ist sie stets 00:00.
    return d.getUTCHours() || d.getUTCMinutes()
      ? `${day} ${p(d.getUTCHours())}:${p(d.getUTCMinutes())}`
      : day;
  }

  function label(lines, x, y) {
    ctx.font = '11px system-ui, sans-serif';

    const pad = 6;
    const lh = 14;
    const w = Math.max.apply(null, lines.map(t => ctx.measureText(t).width)) + pad * 2;
    const h = lines.length * lh + pad * 2 - 3;

    // In den sichtbaren Bereich hineinziehen, statt am Rand abzuschneiden.
    const bx = Math.min(Math.max(2, x + 10), u.over.clientWidth - w - 2);
    const by = Math.min(Math.max(2, y - h - 10), u.over.clientHeight - h - 2);

    ctx.fillStyle = 'rgba(15, 18, 22, 0.92)';
    ctx.strokeStyle = LINE;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.rect(bx, by, w, h);
    ctx.fill();
    ctx.stroke();

    ctx.fillStyle = '#e8e8e8';
    ctx.textBaseline = 'top';
    lines.forEach((t, i) => ctx.fillText(t, bx + pad, by + pad + i * lh));
  }

  return {
    hooks: {
      init(up) {
        u = up;

        layer = document.createElement('canvas');
        layer.className = 'anchor-layer';
        ctx = layer.getContext('2d');
        u.over.appendChild(layer);

        let downX = 0, downY = 0;

        u.over.addEventListener('mousedown', e => {
          downX = e.clientX;
          downY = e.clientY;
        });

        /* Nur ein echter Klick heftet an. Ohne diese Pruefung wuerde jedes
           Verschieben des Ausschnitts am Ende als Klick gewertet. */
        u.over.addEventListener('click', e => {
          if (Math.abs(e.clientX - downX) > 4 || Math.abs(e.clientY - downY) > 4) return;
          if (!enabled()) return;

          pinned = pinned ? null : pick();
          draw();
        });

        u.over.addEventListener('mouseleave', () => {
          hover = null;
          draw();
        });
      },

      setSize: resize,
      setScale: draw,

      setCursor() {
        if (!enabled()) {
          if (hover) { hover = null; draw(); }
          return;
        }

        /* uPlot meldet den Zeiger auch dann neu, wenn sich nur der Ausschnitt
           geaendert hat. Beim Zoomen bleibt die Maus stehen, waehrend die
           Prognoselinie unter ihr wegrutscht -- die Auswahl wuerde dann
           grundlos verschwinden, obwohl der Nutzer gerade genauer hinsehen
           will. Nur eine echte Bewegung waehlt neu; sonst wird lediglich an
           den neuen Stellen nachgezeichnet. */
        const moved = u.cursor.left !== lastLeft || u.cursor.top !== lastTop;
        lastLeft = u.cursor.left;
        lastTop = u.cursor.top;

        if (moved) hover = pick();
        draw();
      }
    }
  };
}

/* Hellere Variante einer Hexfarbe — die Prognose soll erkennbar zum selben
   Wert gehören, sich aber klar vom Ist-Verlauf abheben. */
function lighten(hex, amount) {
  const n = parseInt(hex.slice(1), 16);
  const r = (n >> 16) & 255, g = (n >> 8) & 255, b = n & 255;
  const mix = c => Math.round(c + (255 - c) * amount);
  return `rgb(${mix(r)}, ${mix(g)}, ${mix(b)})`;
}

function renderLegend(series, rebase) {
  const el = $('#legend');
  el.innerHTML = '';

  series.forEach((s, i) => {
    const up = (s.changePct ?? 0) >= 0;
    const div = document.createElement('div');
    div.className = 'item';
    div.innerHTML =
      `<span class="swatch" style="background:${colorFor(i)}"></span>` +
      `<b>${s.symbol}</b>` +
      `<span class="dim">${CLASS_LABEL[s.assetClass] || s.assetClass}</span>` +
      `<span class="chg ${up ? 'up' : 'down'}">${up ? '+' : ''}${fmtNum(s.changePct, 2)} %</span>` +
      `<span class="dim">${fmtNum(s.last, 2)} ${s.currency || ''}</span>` +
      ((s.asOfForecasts || [])
        .map(af => {
          const withActual = af.points.filter(p => p.actualChangePct !== null);
          if (!withActual.length) return '';
          const hit = withActual.filter(p =>
            Math.sign(p.predictedChangePct) === Math.sign(p.actualChangePct)).length;
          return `<span class="dim" title="Prognosepfad vom ${af.madeAtUtc.slice(0,10)}">` +
            `| Stichtag ${af.madeAtUtc.slice(0,10)}: ${hit}/${withActual.length} Richtung</span>`;
        }).join('')) +
      ((s.pastTracks || [])
        .filter(t => t.accuracy.scored > 0)
        .map(t => `<span class="dim" title="${t.accuracy.scored} bewertete Prognosen im Zeitraum">` +
          `| ${t.horizonLabel}: ${fmtNum(t.accuracy.hitRatePct, 0)} % Treffer, ` +
          `Ø ${fmtNum(t.accuracy.meanAbsPctError, 1)} % Fehler</span>`)
        .join(''));
    el.appendChild(div);
  });
}

$('#chart-class').onchange = loadAvailable;
$('#chart-sector').onchange = loadAvailable;
$('#chart-country').onchange = loadAvailable;

/* Branche und Land betreffen nur Aktien. Bei Krypto stünde dort nichts
   Sinnvolles, und ein wirkungsloser Filter ist schlimmer als keiner --
   er sieht aus, als täte er etwas. */
function applySectorVisibility() {
  const cls = $('#chart-class').value;
  const show = cls === '' || cls === 'Stock';

  $('#chart-sector').parentElement.style.display = show ? '' : 'none';
  $('#chart-country').parentElement.style.display = show ? '' : 'none';

  if (!show) {
    $('#chart-sector').value = '';
    $('#chart-country').value = '';
  }
}

$('#chart-class').addEventListener('change', applySectorVisibility);
$('#chart-search').oninput = debounce(loadAvailable, 300);
/* Die Achse ist reine Darstellung: Beim Umschalten wird neu gezeichnet, aber
   nicht neu geladen. `rebase` dagegen ändert die Zahlen und muss beim Server
   angefragt werden -- deshalb hängt es weiterhin an „Anzeigen". */
$('#logscale').onchange = () => redrawSoon();

$('#chart-reload').onclick = drawCharts;

/* Der Schalter für die Säulengewichte zeichnet neu.

   Ohne das müsste man ihn setzen und danach von Hand „Anzeigen" drücken —
   und würde denken, er täte nichts. */
$('#apply-weights').onchange = () => {
  if (chartState.selected.length) drawCharts();
  saveUiState();
};

$('#show-forecast').onchange = () => {
  if (chartState.selected.length) drawCharts();
};

function renderAsOfList() {
  const el = $('#asof-list');
  el.innerHTML = '';

  asOfDates.forEach((d, i) => {
    const chip = document.createElement('span');
    chip.className = 'chip';
    chip.innerHTML = `<span class="dot" style="background:${ASOF_COLORS[i % ASOF_COLORS.length]}"></span>` +
      `${d}<button title="Entfernen">×</button>`;
    chip.querySelector('button').onclick = () => {
      asOfDates.splice(i, 1);
      renderAsOfList();
      if (chartState.selected.length) drawCharts();
    };
    el.appendChild(chip);
  });
}

/* Eigene Farben für die Stichtags-Fächer — sie gehören nicht zu einem Wert,
   sondern zu einem Zeitpunkt, und müssen sich von den Kurvenfarben abheben. */
const ASOF_COLORS = ['#ffffff', '#ffd166', '#06d6a0', '#ef476f'];

$('#asof-add').onclick = () => {
  const v = $('#asof-input').value;
  if (!v) { setStatus('Erst ein Datum wählen', 'err'); return; }

  if (asOfDates.includes(v)) return;

  if (asOfDates.length >= MAX_ASOF) {
    setStatus(`Höchstens ${MAX_ASOF} Stichtage gleichzeitig`, 'err');
    return;
  }

  asOfDates.push(v);
  asOfDates.sort();
  renderAsOfList();

  if (chartState.selected.length) drawCharts();
};

$('#show-forecast-past').onchange = () => {
  applyViewControls();
  if (chartState.selected.length) drawCharts();
};

/* Mehrfachauswahl: jeder Horizont lässt sich einzeln zu- und abschalten.
   Mindestens einer bleibt aktiv, sonst wäre die Rückschau leer. */
$('#fc-horizons').addEventListener('click', e => {
  const b = e.target.closest('button[data-h]');
  if (!b) return;

  const active = $$('#fc-horizons button.active');
  if (b.classList.contains('active') && active.length === 1) return;

  b.classList.toggle('active');
  if (chartState.selected.length) drawCharts();
});
$('#chart-clear').onclick = () => {
  chartState.selected = [];
  renderAvailable();
  renderSelected();
  destroyPlots();
  $('#charts-empty').style.display = 'block';
  $('#legend').innerHTML = '';
};

$('#layout-mode').addEventListener('click', e => {
  const b = e.target.closest('button[data-mode]');
  if (!b) return;
  $$('#layout-mode button').forEach(x => x.classList.toggle('active', x === b));
  chartState.mode = b.dataset.mode;
  if (chartState.plots.length) drawCharts();
});

/* Schnellwahl des Zeitraums. Setzt die Monatszahl und leert die Datumsfelder,
   damit es nur eine wirksame Quelle gibt statt zweier widersprüchlicher. */
$('#range-presets').addEventListener('click', e => {
  const b = e.target.closest('button[data-months]');
  if (!b) return;

  $$('#range-presets button').forEach(x => x.classList.toggle('active', x === b));
  $('#months').value = b.dataset.months;
  $('#from-date').value = '';
  $('#to-date').value = '';

  if (chartState.selected.length) drawCharts();
});

/* Ein ausdrücklich gesetztes Datum hat Vorrang — die Schnellwahl wird dann
   abgewählt, damit nicht zwei Angaben gleichzeitig aktiv erscheinen. */
['#from-date', '#to-date'].forEach(sel => {
  $(sel).addEventListener('change', () => {
    if ($('#from-date').value || $('#to-date').value)
      $$('#range-presets button').forEach(x => x.classList.remove('active'));

    if (chartState.selected.length) drawCharts();
  });
});

// Ansichtsumschalter in der Kopfzeile.
$('#flow-scope').addEventListener('click', e => {
  const b = e.target.closest('button[data-scope]');
  if (!b) return;
  $$('#flow-scope button').forEach(x => x.classList.toggle('active', x === b));
  drawCharts();
});

$('#view-switch').addEventListener('click', e => {
  const b = e.target.closest('button[data-cview]');
  if (!b) return;

  $$('#view-switch button').forEach(x => x.classList.toggle('active', x === b));
  chartState.view = b.dataset.cview;
  applyViewControls();

  if (chartState.selected.length) drawCharts();
});

/* Nicht jede Einstellung ergibt in jeder Ansicht Sinn: Kerzen lassen sich
   nicht überlagern, und eine Korrelationsmatrix hat keine Anordnung. Statt
   wirkungslose Schalter stehen zu lassen, werden sie ausgeblendet. */
function applyViewControls() {
  const v = chartState.view;
  const isLine = v === 'line';
  const isMatrix = v === 'matrix';

  $('#grp-layout').style.display = isLine ? '' : 'none';
  $('#grp-rebase').style.display = isLine ? '' : 'none';

  const isFlow = v === 'flow';
  const isInvest = v === 'invest';

  /* Im Depot wirkt keine der Diagrammeinstellungen: Es zeigt Betraege, keine
     Kurse. Ein Regler, der nichts tut, laesst den Nutzer suchen, warum nichts
     passiert. */
  ['#grp-range', '#grp-presets', '#grp-logscale'].forEach(sel => {
    const el = $(sel);
    if (el) el.style.display = isInvest ? 'none' : '';
  });

  // Die Aufloesung hat keine eigene Kennung, wohl aber ihr Schalter.
  const aufl = $('#interval-mode')?.closest('.group');
  if (aufl) aufl.style.display = isInvest ? 'none' : '';

  /* Die Legende gehoert zu den Kursdiagrammen. Sie stehen zu lassen heisst,
     ueber einer Tabelle mit Betraegen eine Zeile mit Trefferquoten zu zeigen —
     zwei Themen, die nichts miteinander zu tun haben. */
  const leg = $('#legend');
  if (leg) leg.style.display = isInvest ? 'none' : '';

  // Die Prognose wird als Kurve gezeichnet — das ergibt nur in der Linienansicht Sinn.
  $('#grp-forecast').style.display = isLine ? '' : 'none';
  $('#grp-asof').style.display = isLine ? '' : 'none';
  $('#grp-flow-scope').style.display = isFlow ? '' : 'none';
  $('#grp-fc-horizon').style.display =
    isLine && $('#show-forecast-past').checked ? '' : 'none';
  /* Der Zeitraum wirkt inzwischen auf ALLE Ansichten, auch auf die
     Korrelation — sie wird dafür live gerechnet statt aus der vorberechneten
     Paartabelle gelesen. Deshalb bleibt die Auswahl überall sichtbar. */

  // Die Auto-Suche liefert Paare — sie gehört zur Korrelationsansicht.
  $('#autofind').style.display = isMatrix ? '' : 'none';

  if (isInvest) {
    $('#charts-empty').textContent =
      'Links Werte auswählen — sie erscheinen dann hier als Zeilen zum Eintragen.';
    return;
  }

  $('#charts-empty').textContent = isMatrix
    ? 'Mindestens zwei Werte auswählen — oder die Auto-Suche starten.'
    : 'Links Werte auswählen und auf Anzeigen klicken.';
}

/* Auto-Suche: durchsucht den gesamten verfolgten Bestand nach auffälligen
   Paaren und übernimmt die beteiligten Werte in die Auswahl. Der Sinn ist,
   Zusammenhänge zu finden, die man von sich aus nicht gesucht hätte. */
$('#autofind-go').onclick = async () => {
  const mode = $('#autofind-mode').value;
  setStatus('durchsucht alle verfolgten Werte …', 'busy');

  const d = await guard(() => api(
    `/api/analysis/extremes?interval=${chartState.interval}&mode=${mode}&limit=12`));
  if (!d) return;

  if (!d.pairs.length) {
    setStatus('Nichts gefunden — ist die Analyse für diese Auflösung gerechnet?', 'err');
    return;
  }

  // Beteiligte Werte laden und als Auswahl setzen.
  const list = await guard(() => api('/api/assets/?limit=2000&tracked=true'));
  if (!list) return;

  const byId = new Map(list.map(a => [a.assetId, a]));
  const picked = d.assetIds.map(id => byId.get(id)).filter(Boolean).slice(0, MAX_SELECTED);

  chartState.selected = picked;
  renderAvailable();
  renderSelected();

  await drawCharts();
  renderPairFindings(d);

  setStatus(`${d.pairs.length} Paare gefunden, ${picked.length} Werte übernommen`);
};

/* Die gefundenen Paare als Liste unter der Matrix — die Heatmap zeigt das
   Geflecht, diese Liste benennt die konkreten Treffer. */
function renderPairFindings(d) {
  const wrap = $('#matrix');

  const box = document.createElement('div');
  box.className = 'card';

  const label = {
    negative: 'Gegenläufigste Paare',
    positive: 'Gleichlaufendste Paare',
    lead: 'Stärkster Vorlauf',
    crossings: 'Meiste Kreuzungen'
  }[d.mode] || 'Treffer';

  const rows = d.pairs.map(p => {
    const lag = p.bestLagBars
      ? `${Math.abs(p.bestLagBars)} Bars — ${p.bestLagBars > 0 ? p.a.symbol : p.b.symbol} voraus`
      : 'gleichzeitig';

    return `<tr>
      <td><b>${p.a.symbol}</b></td>
      <td><b>${p.b.symbol}</b></td>
      <td class="num ${p.corr >= 0 ? 'up' : 'down'}">${fmtNum(p.corr, 3)}</td>
      <td class="num">${p.crossings}</td>
      <td>${lag}</td>
      <td class="num dim">${p.nObs}</td>
    </tr>`;
  }).join('');

  box.innerHTML = `<h3>${label} — im gesamten Bestand</h3>
    <table class="grid">
      <thead><tr><th>A</th><th>B</th><th class="num">Korrelation</th>
        <th class="num">Kreuzungen</th><th>Vorlauf</th><th class="num">Punkte</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>`;

  wrap.appendChild(box);
}

$('#interval-mode').addEventListener('click', e => {
  const b = e.target.closest('button[data-interval]');
  if (!b) return;
  $$('#interval-mode button').forEach(x => x.classList.toggle('active', x === b));
  chartState.interval = b.dataset.interval;

  // Stundenbars reichen weniger weit zurück; Standardzeitraum anpassen.
  if (chartState.interval === '1h' && parseInt($('#months').value, 10) > 12) {
    $('#months').value = 6;
  }
  if (chartState.plots.length) drawCharts();
});

function debounce(fn, ms) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

/* Beim Verkleinern des Fensters nur die Größe nachziehen. Ein kompletter
   Neuaufbau würde die Daten erneut vom Server holen — bei jedem Ziehen am
   Fensterrahmen. */
window.addEventListener('resize', debounce(() => {
  resizePlots();
}, 150));

function resizePlots() {
  const height = chartState.mode === 'grid' ? 240 : 430;
  chartState.plots.forEach(({ plot, box }) => {
    const width = Math.max(280, box.clientWidth - 22);
    if (width !== plot.width || height !== plot.height) plot.setSize({ width, height });
  });
}

/* Auch dann nachziehen, wenn sich nicht das Fenster ändert, sondern der Platz.

   uPlot bekommt seine Breite als feste Pixelzahl, gemessen beim Zeichnen. Wird
   in einer verborgenen Ansicht gezeichnet, ist dort nichts zu messen — das
   Diagramm bleibt schmal und wird es auch, wenn die Ansicht später aufgeht.
   Genau das passiert seit der Sitzungswiederherstellung: Beim Seitenaufbau
   werden die zuletzt gewählten Werte sofort gezeichnet, die Ansicht liegt
   dabei aber noch hinter `display: none`.

   Am Fenster-Ereignis hängt das nicht, denn das Fenster ändert sich nicht.
   Beobachtet wird deshalb der Behälter selbst. */
const chartsResizeObserver = new ResizeObserver(debounce(() => resizePlots(), 60));

chartsResizeObserver.observe($('#charts'));

// ============================================================= Auswahl ===

async function loadSelectTable() {
  const params = new URLSearchParams({ limit: '1000' });
  const cls = $('#sel-class').value;
  const tracked = $('#sel-tracked').value;
  const search = $('#sel-search').value.trim();
  if (cls) params.set('cls', cls);
  if (tracked) params.set('tracked', tracked);
  if (search) params.set('search', search);

  const list = await guard(() => api('/api/assets/?' + params));
  if (!list) return;

  const tb = clearTable('#sel-table');
  if (!list.length) { emptyRow(tb, 7, 'Nichts gefunden.'); return; }

  for (const a of list) {
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = a.isTracked;
    cb.onchange = async () => {
      cb.disabled = true;
      const ok = await guard(() => api('/api/assets/track', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ assetIds: [a.assetId], tracked: cb.checked })
      }), cb.checked ? `${a.symbol} wird verfolgt` : `${a.symbol} nicht mehr verfolgt`);
      if (!ok) cb.checked = !cb.checked;
      cb.disabled = false;
    };

    row(tb, [
      { text: a.marketCapRank ?? '–', cls: 'num dim' },
      { html: `<b>${a.symbol}</b>` },
      a.name || '–',
      CLASS_LABEL[a.assetClass] || a.assetClass,
      { text: fmtCap(a.marketCap), cls: 'num' },
      a.currency || '–',
      cb
    ]);
  }
}

$('#sel-reload').onclick = loadSelectTable;
$('#sel-class').onchange = loadSelectTable;
$('#sel-tracked').onchange = loadSelectTable;
$('#sel-search').oninput = debounce(loadSelectTable, 300);

$('#universe-refresh').onclick = async () => {
  setStatus('Universum wird neu geladen, das dauert etwas …', 'busy');
  await guard(() => api('/api/assets/universe/refresh', { method: 'POST' }), 'Universum aktualisiert');
  await loadSelectTable();
};

// ============================================================= Analyse ===

async function fillAssetSelect(selectEl) {
  const list = await guard(() => api('/api/assets/tracked'));
  if (!list) return [];

  const current = selectEl.value;
  selectEl.innerHTML = '';
  for (const a of list) {
    const o = document.createElement('option');
    o.value = a.assetId;
    o.textContent = `${a.symbol} — ${a.name || ''}`;
    selectEl.appendChild(o);
  }
  if (current) selectEl.value = current;
  return list;
}

async function loadAnalysis() {
  const id = $('#ana-asset').value;
  const iv = $('#ana-interval').value;
  if (!id) return;

  const [leaders, corr, cross] = await Promise.all([
    guard(() => api(`/api/analysis/leaders/${id}?interval=${iv}&limit=15`)),
    guard(() => api(`/api/analysis/correlations/${id}?interval=${iv}&limit=25`)),
    guard(() => api(`/api/analysis/crossings?interval=${iv}&days=90&limit=100`))
  ]);

  const tl = clearTable('#ana-leaders');
  if (!leaders || !leaders.length) {
    emptyRow(tl, 4, 'Keine Vorlauf-Beziehungen gefunden. Analyse schon gerechnet?');
  } else {
    for (const l of leaders) {
      row(tl, [
        { html: `<b>${l.symbol || l.leaderAssetId}</b>` },
        l.name || '–',
        { text: l.leadBars + ' Bars', cls: 'num' },
        { text: fmtNum(l.corr, 3), cls: 'num ' + (l.corr >= 0 ? 'up' : 'down') }
      ]);
    }
  }

  const tc = clearTable('#ana-corr');
  if (!corr || !corr.length) {
    emptyRow(tc, 4, 'Noch keine Korrelationen berechnet.');
  } else {
    for (const c of corr) {
      row(tc, [
        { html: `<b>${c.symbol || c.assetId}</b>` },
        { text: fmtNum(c.corr, 3), cls: 'num ' + (c.corr >= 0 ? 'up' : 'down') },
        { text: c.bestLagBars, cls: 'num' },
        c.bestLagBars === 0 ? 'gleichzeitig'
          : c.leads ? `${c.symbol} läuft voraus`
            : `läuft ${c.symbol} voraus`
      ]);
    }
  }

  const tx = clearTable('#ana-cross');
  if (!cross || !cross.length) {
    emptyRow(tx, 6, 'Keine Kreuzungen im Zeitraum.');
  } else {
    for (const x of cross.slice(0, 100)) {
      row(tx, [
        fmtDate(x.tsUtc, false),
        { html: `<b>${x.a.symbol || x.a.id}</b>` },
        { html: `<b>${x.b.symbol || x.b.id}</b>` },
        x.direction,
        { text: fmtNum(x.spreadBefore, 2), cls: 'num' },
        { text: fmtNum(x.spreadAfter, 2), cls: 'num' }
      ]);
    }
  }
}

$('#ana-reload').onclick = loadAnalysis;
$('#ana-asset').onchange = loadAnalysis;
$('#ana-interval').onchange = loadAnalysis;

/* ------------------------------------------------ Kreuzungs-Rangliste ---
   Die Rangliste steht nach Paargewinn — also nach dem, was BEREITS gelaufen
   ist. Ohne die Bewährungsspalten daneben läse sich das wie eine Empfehlung,
   und genau das darf es nicht. Deshalb steht der Hinweis über der Tabelle und
   nicht darunter.                                                          */

function pct(v, stellen = 1) {
  if (v === null || v === undefined) return '–';
  return (v * 100).toFixed(stellen).replace('.', ',') + ' %';
}

function kxAbfrage() {
  const q = new URLSearchParams({
    interval: $('#kx-interval').value,
    tage: $('#kx-tage').value,
    haltedauer: $('#kx-halte').value,
    maxJeSymbol: $('#kx-max').value,
    nurKlassenwechsel: $('#kx-wechsel').value,
    limit: '60'
  });
  return `/api/analysis/crossings/chancen?${q}`;
}

async function loadKreuzungen() {
  const t = clearTable('#kx-grid');
  $('#kx-hinweis').textContent = 'wird geladen …';
  $('#kx-aus').textContent = '';

  const d = await guard(() => api(kxAbfrage()));
  if (!d) { $('#kx-hinweis').textContent = ''; emptyRow(t, 11, 'Abfrage fehlgeschlagen.'); return; }

  // Fett-Auszeichnung aus dem Hinweis in echtes Markup übersetzen.
  $('#kx-hinweis').innerHTML = (d.hinweis || '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');

  let zeilen = d.zeilen || [];
  if ($('#kx-nurbew').value === 'true') zeilen = zeilen.filter(z => z.bewaehrt);

  if (!zeilen.length) {
    emptyRow(t, 11, $('#kx-nurbew').value === 'true'
      ? 'Kein Paar im Fenster hat sich bewährt. Das ist eine Aussage, kein Fehler.'
      : 'Keine Kreuzung im Zeitraum.');
  } else {
    for (const z of zeilen) {
      const h = z.historie || {};
      row(t, [
        { html: `<b class="up">${z.kaufen.symbol}</b><span class="dim"> ${z.kaufen.klasse}</span>` },
        { html: `<b class="down">${z.verkaufen.symbol}</b><span class="dim"> ${z.verkaufen.klasse}</span>` },
        fmtDate(z.kreuzung, false),
        { text: z.tageSeither, cls: 'num' },
        { text: pct(z.renditeKaufen), cls: 'num ' + (z.renditeKaufen >= 0 ? 'up' : 'down') },
        { text: pct(z.renditeVerkaufen), cls: 'num ' + (z.renditeVerkaufen >= 0 ? 'up' : 'down') },
        { html: `<b>${pct(z.paargewinn)}</b>`, cls: 'num ' + (z.paargewinn >= 0 ? 'up' : 'down') },
        { text: h.kreuzungen ?? 0, cls: 'num dim' },
        { text: h.trefferquote == null ? '–' : pct(h.trefferquote, 0), cls: 'num' },
        { text: h.mittelgewinn == null ? '–' : pct(h.mittelgewinn), cls: 'num' },
        { html: z.bewaehrt
            ? '<span class="pill ok">bewährt</span>'
            : '<span class="pill off">ohne Rückhalt</span>' }
      ]);
    }
  }

  /* Ausgelassene Paare gehören sichtbar unter die Tabelle. Ein Kurssprung
     durch einen Split erzeugt einen Scheingewinn von fünfzig Prozent; wer ihn
     still herausfiltert, verbirgt zugleich, dass die Kursreihe kaputt ist. */
  const aus = d.ausgelassen || [];
  $('#kx-aus').textContent = aus.length
    ? `Wegen eines Kurssprungs ausgelassen (${aus.length}): `
      + aus.slice(0, 8).map(a => `${a.symbolA}/${a.symbolB}`).join(', ')
      + (aus.length > 8 ? ' …' : '') + ' — ' + aus[0].grund
    : '';
}

$('#kx-load').onclick = loadKreuzungen;
for (const id of ['#kx-interval', '#kx-tage', '#kx-halte', '#kx-max', '#kx-wechsel', '#kx-nurbew'])
  $(id).onchange = loadKreuzungen;

$('#ana-recompute').onclick = async () => {
  const iv = $('#ana-interval').value;
  setStatus('Analyse läuft, bei vielen Werten dauert das …', 'busy');
  await guard(
    () => api(`/api/analysis/recompute?interval=${iv}&windowBars=${iv === '1h' ? 720 : 365}`,
      { method: 'POST' }),
    'Analyse neu berechnet');
  await loadAnalysis();
};

// ============================================================ Prognose ===

async function loadForecast() {
  const id = $('#fc-asset').value;
  if (!id) return;

  const [data, acc] = await Promise.all([
    guard(() => api(`/api/forecast/${id}`)),
    guard(() => api('/api/forecast/accuracy'))
  ]);

  const tb = clearTable('#fc-table');
  if (!data || !data.forecasts.length) {
    emptyRow(tb, 8, 'Noch keine Prognosen. Auf „Prognose neu rechnen" klicken.');
  } else {
    for (const f of data.forecasts) {
      const up = f.changePct >= 0;
      const t = f.track;
      row(tb, [
        f.horizonLabel,
        fmtDate(f.targetTsUtc),
        { text: fmtNum(f.baseClose, 2), cls: 'num' },
        { text: fmtNum(f.predictedClose, 2), cls: 'num' },
        { text: (up ? '+' : '') + fmtNum(f.changePct, 2) + ' %', cls: 'num ' + (up ? 'up' : 'down') },
        { text: fmtPct(f.confidence * 100, 0), cls: 'num' },
        { text: t ? `${fmtNum(t.hitRate * 100, 0)} % (n=${t.n})` : 'ungeprüft', cls: 'num ' + (t ? '' : 'dim') },
        { text: t ? fmtPct(t.mape * 100, 2) : '–', cls: 'num ' + (t ? '' : 'dim') }
      ]);
    }
  }

  const ta = clearTable('#fc-accuracy');
  if (!acc || !acc.length) {
    emptyRow(ta, 4, 'Noch nichts ausgewertet — Prognosen müssen erst fällig werden.');
  } else {
    for (const a of acc) {
      row(ta, [
        a.horizonLabel,
        { text: a.scored, cls: 'num' },
        { text: fmtPct(a.meanAbsPctError, 3), cls: 'num' },
        { text: fmtPct(a.hitRatePct, 1), cls: 'num ' + (a.hitRatePct >= 50 ? 'up' : 'down') }
      ]);
    }
  }

  // Horizont-Auswahl für die Gewichte aus den vorhandenen Prognosen füllen.
  const hSel = $('#fc-horizon');
  if (data && data.forecasts.length && !hSel.options.length) {
    for (const f of data.forecasts) {
      const o = document.createElement('option');
      o.value = f.horizonHours;
      o.textContent = f.horizonLabel;
      hSel.appendChild(o);
    }
  }
  await loadWeights();
}

async function loadWeights() {
  const id = $('#fc-asset').value;
  const h = $('#fc-horizon').value;
  const tb = clearTable('#fc-weights');
  if (!id || !h) { emptyRow(tb, 4, 'Kein Horizont gewählt.'); return; }

  const w = await guard(() => api(`/api/forecast/weights/${id}/${h}`));
  if (!w || !w.length) {
    emptyRow(tb, 4, 'Noch keine gelernten Gewichte — alle Teilmodelle zählen gleich.');
    return;
  }

  for (const x of w) {
    row(tb, [
      x.modelName,
      { text: fmtNum(x.weight * 100, 1) + ' %', cls: 'num' },
      { text: x.observations, cls: 'num' },
      { text: fmtPct(x.hitRatePct, 1), cls: 'num' }
    ]);
  }
}

$('#fc-reload').onclick = loadForecast;
$('#fc-asset').onchange = loadForecast;
$('#fc-horizon').onchange = loadWeights;

$('#fc-run').onclick = async () => {
  const id = $('#fc-asset').value;
  await guard(() => api(`/api/forecast/run/${id}`, { method: 'POST' }), 'Prognose erstellt');
  await loadForecast();
};

$('#fc-score').onclick = async () => {
  const r = await guard(() => api('/api/forecast/score', { method: 'POST' }));
  if (r) setStatus(`${r.scored} Prognosen ausgewertet, ${r.weightsUpdated} Gewichte angepasst`);
  await loadForecast();
};

// ============================================================== System ===

async function loadSystem() {
  const [stats, providers, runs] = await Promise.all([
    guard(() => api('/api/health/stats')),
    guard(() => api('/api/health/providers')),
    guard(() => api('/api/health/runs?last=40'))
  ]);

  if (stats) {
    const parts = ['<dl class="kv">'];
    for (const a of stats.assets) {
      parts.push(`<dt>${CLASS_LABEL[a.assetClass] || a.assetClass}</dt>` +
        `<dd>${a.tracked} von ${a.total} verfolgt</dd>`);
    }
    for (const b of stats.bars) {
      parts.push(`<dt>Bars ${b.interval}</dt>` +
        `<dd>${Number(b.bars).toLocaleString('de-DE')} über ${b.assets} Werte<br>` +
        `<span class="dim">${fmtDate(b.oldest, false)} – ${fmtDate(b.newest)}</span></dd>`);
    }
    parts.push(`<dt>Prognosen</dt><dd>${stats.forecasts.total} gesamt, ` +
      `${stats.forecasts.scored} ausgewertet, ${stats.forecasts.pending} offen</dd>`);
    parts.push(`<dt>Analyse</dt><dd>${stats.analysis.pairs} Paare, ` +
      `${stats.analysis.crossings} Kreuzungen</dd>`);
    parts.push(`<dt>Horizonte</dt><dd>${stats.config.horizons.join(', ')} Stunden</dd>`);
    parts.push('</dl>');
    $('#sys-stats').innerHTML = parts.join('');
  }

  if (providers) {
    const parts = ['<dl class="kv">'];
    for (const p of providers.marketData) {
      parts.push(`<dt>${p.provider}</dt><dd>` +
        `<span class="pill ${p.configured ? 'ok' : 'off'}">` +
        `${p.configured ? 'bereit' : 'kein Schlüssel'}</span> ` +
        `<span class="dim">${p.intervals.join(', ')}</span></dd>`);
    }
    for (const p of providers.universe) {
      parts.push(`<dt>${p.provider} (Universum)</dt><dd>` +
        `<span class="pill ${p.configured ? 'ok' : 'off'}">` +
        `${p.configured ? 'bereit' : 'kein Schlüssel'}</span></dd>`);
    }
    parts.push('</dl>');
    $('#sys-providers').innerHTML = parts.join('');
  }

  await loadScheduler();

  const tb = clearTable('#sys-runs');
  if (!runs || !runs.length) {
    emptyRow(tb, 7, 'Noch keine Läufe.');
  } else {
    for (const r of runs) {
      const dur = r.finishedUtc
        ? ((new Date(r.finishedUtc + 'Z') - new Date(r.startedUtc + 'Z')) / 1000).toFixed(1) + ' s'
        : 'läuft …';
      row(tb, [
        r.jobName,
        fmtDate(r.startedUtc),
        dur,
        { text: r.okCount ?? '–', cls: 'num' },
        { text: r.errCount ?? '–', cls: 'num ' + (r.errCount ? 'down' : '') },
        { text: r.rowsWritten ?? '–', cls: 'num' },
        { text: r.note || '', cls: 'dim' }
      ]);
    }
  }
}

async function loadScheduler() {
  const d = await guard(() => api('/api/scheduler/'));
  if (!d) return;

  $('#sched-enabled').checked = d.enabled;
  $('#sched-label').textContent = d.enabled ? 'aktiv' : 'angehalten';

  const parts = ['<dl class="kv">'];
  parts.push(`<dt>Zustand</dt><dd><span class="pill ${d.enabled ? 'ok' : 'off'}">` +
    `${d.enabled ? 'läuft' : 'angehalten'}</span>` +
    (d.busy ? ' <span class="dim">— Job läuft gerade</span>' : '') + '</dd>');
  parts.push(`<dt>Nächster Stundenlauf</dt><dd>${fmtDate(d.nextHourlyUtc)} ` +
    `<span class="dim">(${d.hourlyCronUtc})</span></dd>`);
  parts.push(`<dt>Nächster Tageslauf</dt><dd>${fmtDate(d.nextDailyUtc)} ` +
    `<span class="dim">(${d.dailyCronUtc})</span></dd>`);
  parts.push(`<dt>Zuletzt</dt><dd>${d.lastJob || '–'}` +
    (d.lastHourlyUtc || d.lastDailyUtc
      ? ` <span class="dim">${fmtDate(d.lastDailyUtc || d.lastHourlyUtc)}</span>` : '') +
    `<br><span class="dim">${d.lastResult || 'noch kein Lauf'}</span></dd>`);

  if (d.skippedWhileDisabled > 0)
    parts.push(`<dt>Übersprungen</dt><dd>${d.skippedWhileDisabled} Termine, ` +
      `während der Scheduler angehalten war</dd>`);

  parts.push('</dl>');
  $('#sched-status').innerHTML = parts.join('');
}

$('#sched-enabled').onchange = async e => {
  const on = e.target.checked;
  await guard(() => api(`/api/scheduler/enabled?value=${on}`, { method: 'POST' }),
    on ? 'Scheduler läuft' : 'Scheduler angehalten');
  await loadScheduler();
};

async function runSchedulerJob(job) {
  setStatus(`${job === 'daily' ? 'Tageslauf' : 'Stundenlauf'} läuft — das dauert etwas …`, 'busy');
  await guard(() => api(`/api/scheduler/run?job=${job}`, { method: 'POST' }), 'Lauf beendet');
  await loadScheduler();
  await loadSystem();
}

$('#sched-run-hourly').onclick = () => runSchedulerJob('hourly');
$('#sched-run-daily').onclick = () => runSchedulerJob('daily');

$('#sys-reload').onclick = loadSystem;
$('#sys-update-1d').onclick = async () => {
  await guard(() => api('/api/ingest/update?interval=1d', { method: 'POST' }), 'Tagesdaten aktualisiert');
  await loadSystem();
};
$('#sys-update-1h').onclick = async () => {
  await guard(() => api('/api/ingest/update?interval=1h', { method: 'POST' }), 'Stundendaten aktualisiert');
  await loadSystem();
};


// ============================================================== Säulen ===

/* Die Prognose ruht auf mehreren Säulen. Jede beantwortet eine andere Frage an
   dieselben Daten, jede lässt sich einzeln zuschalten und gewichten.

   Die Gewichte werden gegeneinander normiert. Das ist wichtig für das
   Verständnis: Eine Säule auf 30 zu stellen heißt nicht "30 Prozent", sondern
   "30 im Verhältnis zu dem, was die anderen haben". Deshalb steht neben jedem
   Regler der tatsächliche Anteil -- ohne ihn wäre die Zahl irreführend. */

const PILLAR_KEYS = ['learning', 'math', 'flow', 'deep', 'knowledge', 'reasoning', 'semantic'];

function pillarConfig() {
  const out = {
    // Auch der offene Unterreiter gehoert zum Zustand der Seite.
    open: $('#pillar-tabs button.active')?.dataset.pillar || 'learning',
    pillars: {},
    strategies: {},

    /* Die Gewichte der drei Baender gehoeren zum Zustand wie jeder Regler.
       Sie stehen bewusst neben `pillars` und nicht darin: `pillars` haelt je
       Saeule ein Gewicht fuers Gesamturteil, hier steht, wie sich EINE Saeule
       intern zusammensetzt. Zwei verschiedene Fragen, zwei Felder. */
    deepWeights: { ...deepWeights }
  };

  PILLAR_KEYS.forEach(k => {
    const on = $(`.pillar-on[data-key="${k}"]`);
    const w = $(`.pillar-weight[data-key="${k}"]`);
    if (!on || !w) return;

    out.pillars[k] = { enabled: on.checked, weight: parseInt(w.value, 10) || 0 };
  });

  $$('.strat-on').forEach(cb => {
    const key = cb.dataset.key;
    const params = {};

    $$(`.strat-param[data-key="${key}"]`).forEach(inp => {
      params[inp.dataset.param] = parseFloat(inp.value);
    });

    out.strategies[key] = { enabled: cb.checked, params };
  });

  return out;
}

function applyPillarConfig(cfg) {
  if (!cfg) return;

  Object.entries(cfg.pillars || {}).forEach(([k, v]) => {
    const on = $(`.pillar-on[data-key="${k}"]`);
    const w = $(`.pillar-weight[data-key="${k}"]`);
    if (!on || !w || on.disabled) return;

    on.checked = !!v.enabled;
    w.value = v.weight ?? w.value;
  });

  Object.entries(cfg.strategies || {}).forEach(([k, v]) => {
    const cb = $(`.strat-on[data-key="${k}"]`);
    if (cb) cb.checked = !!v.enabled;

    Object.entries(v.params || {}).forEach(([name, val]) => {
      const inp = $(`.strat-param[data-key="${k}"][data-param="${name}"]`);
      if (inp && val != null) inp.value = val;
    });
  });

  if (cfg.deepWeights) Object.assign(deepWeights, cfg.deepWeights);

  if (cfg.open === 'deep') loadDeep();
  if (cfg.open === 'math') loadCurve();
  if (cfg.open === 'flow') loadFlow();
  if (cfg.open === 'knowledge') loadKnowledge();
  if (cfg.open === 'reasoning') loadReasoning();
  if (cfg.open === 'semantic') loadSemantic();

  if (cfg.open) {
    $$('#pillar-tabs button').forEach(b => b.classList.toggle('active', b.dataset.pillar === cfg.open));
    $$('.pillar-panel').forEach(x => x.classList.toggle('active', x.dataset.panel === cfg.open));
  }

  renderPillarWeights();
}

/* Zeigt neben jedem Regler den normierten Anteil. Abgeschaltete Säulen zählen
   dabei nicht mit -- sonst sähe es so aus, als nähmen sie den anderen etwas
   weg. */
/* Die Gewichte gehen zusaetzlich dauerhaft auf den Server.

   Sie lagen bisher nur im Sitzungszustand. Wer den Browser wechselte, bekam
   eine Anwendung, in der jede Saeule auf null steht -- und die Tagesuebersicht
   meldete das als Zustand des Systems statt als fehlende Eingabe. Ein Regler,
   der die Zahlen im Diagramm veraendert, ist Konfiguration und keine
   Ansichtseinstellung.

   Verzoegert geschrieben: Am Regler zu ziehen loest Dutzende Ereignisse aus. */
let gewichtSchreibZeit = null;

function schreibeGewichte() {
  /* Nur Verwalter duerfen die Gewichte dauerhaft ablegen.

     Ohne diese Zeile schickt die Oberflaeche den Schreibvorgang auch fuer
     einen Nur-Lese-Zugang -- der Server weist ihn korrekt mit 403 ab, und der
     Nutzer sieht eine Fehlermeldung fuer etwas, das er nie ausgeloest hat. Die
     Regler selbst bleiben bedienbar; sie wirken dann nur auf die eigene
     Sitzung. */
  if (wer && !wer.istAdmin) return;

  clearTimeout(gewichtSchreibZeit);
  gewichtSchreibZeit = setTimeout(async () => {
    const g = {};
    for (const k of PILLAR_KEYS) {
      // `reasoning` erzeugt keine Prognose und hat serverseitig kein Gewicht.
      if (k === 'reasoning') continue;
      const w = $(`.pillar-weight[data-key="${k}"]`);
      const on = $(`.pillar-on[data-key="${k}"]`);
      if (!w) continue;
      g[k] = (on && !on.checked) ? 0 : (parseInt(w.value, 10) || 0);
    }
    try {
      await api('/api/saeulen/gewichte', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(g)
      });
    } catch { /* Ohne Server bleibt der Sitzungszustand -- kein Grund zu stoeren. */ }
  }, 800);
}

/* Beim ersten Aufbau die abgelegten Gewichte uebernehmen, sofern die Sitzung
   noch keine hat. So beginnt ein neuer Browser nicht bei null. */
async function ladeGewichteVomServer() {
  try {
    const d = await api('/api/saeulen/gewichte');
    for (const g of d.gewichte || []) {
      const w = $(`.pillar-weight[data-key="${g.saeule}"]`);
      if (w) w.value = g.gewicht;
      const on = $(`.pillar-on[data-key="${g.saeule}"]`);
      if (on && g.gewicht > 0) on.checked = true;
    }
    renderPillarWeights();
  } catch { /* still */ }
}

function renderPillarWeights() {
  const active = PILLAR_KEYS
    .map(k => ({ k, on: $(`.pillar-on[data-key="${k}"]`), w: $(`.pillar-weight[data-key="${k}"]`) }))
    .filter(x => x.on && x.w && x.on.checked);

  const sum = active.reduce((a, x) => a + (parseInt(x.w.value, 10) || 0), 0);

  PILLAR_KEYS.forEach(k => {
    const w = $(`.pillar-weight[data-key="${k}"]`);
    const out = $(`.pillar-weight-out[data-key="${k}"]`);
    const share = $(`[data-share="${k}"]`);
    const on = $(`.pillar-on[data-key="${k}"]`);
    if (!w || !out || !share || !on) return;

    out.textContent = w.value;

    if (!on.checked) { share.textContent = 'nicht einbezogen'; return; }
    if (sum <= 0) { share.textContent = 'alle auf null'; return; }

    share.textContent = `entspricht ${((parseInt(w.value, 10) || 0) / sum * 100).toFixed(1)} %`;
  });

  $$('.strategy').forEach(el => {
    const cb = $('.strat-on', el);
    el.classList.toggle('off', cb && !cb.checked);
  });

  schreibeGewichte();
}

$('#pillar-tabs').addEventListener('click', e => {
  const b = e.target.closest('button[data-pillar]');
  if (!b) return;

  $$('#pillar-tabs button').forEach(x => x.classList.toggle('active', x === b));
  $$('.pillar-panel').forEach(x => x.classList.toggle('active', x.dataset.panel === b.dataset.pillar));

  // Beide Säulen holen ihren Zustand erst, wenn sie gebraucht werden.
  if (b.dataset.pillar === 'deep') loadDeep();
  if (b.dataset.pillar === 'math') loadCurve();
  if (b.dataset.pillar === 'flow') loadFlow();
  if (b.dataset.pillar === 'knowledge') loadKnowledge();
  if (b.dataset.pillar === 'reasoning') loadReasoning();
  if (b.dataset.pillar === 'semantic') loadSemantic();
});

$('#view-pillars').addEventListener('input', renderPillarWeights);
$('#view-pillars').addEventListener('change', renderPillarWeights);

// ---------------------------------------------------- Kapitalfluss -------

/* Die Säule zeigt, was bisher nur unter „Kurse → Fluss" stand.

   Der Unterschied ist nicht nur der Ort. Unter „Kurse" beantwortet die
   Flussansicht die Frage „was ist gerade los"; hier geht es um die Frage, ob
   aus dieser Bewegung ein Prognosebeitrag wird. Deshalb steht hier der
   Frühindikator-Teil oben und die Beschreibung darunter — und deshalb sagt die
   Säule ausdrücklich, dass sie noch kein Gewicht bekommt. */

function kfParams(extra) {
  const p = new URLSearchParams({
    interval: $('#kf-interval').value,
    months: $('#kf-months').value,
    ...extra
  });

  if ($('#kf-selected').checked && chartState.selected.length)
    p.set('ids', chartState.selected.map(a => a.assetId).join(','));

  return p;
}

const kfMrd = v => {
  const a = Math.abs(v);
  if (a >= 1e12) return (v / 1e12).toFixed(2) + ' Bio $';
  if (a >= 1e9) return (v / 1e9).toFixed(1) + ' Mrd $';
  if (a >= 1e6) return (v / 1e6).toFixed(0) + ' Mio $';
  return fmtNum(v, 0) + ' $';
};

async function kfAttribution() {
  const d = await guard(() => api('/api/flow/attribution?' + kfParams({ limit: 12 })));
  const box = $('#kf-attribution');
  if (!d || !box) return;

  if (d.error) { box.innerHTML = `<p class="hint">${d.error}</p>`; return; }

  /* Der Anteil der Umverteilung ist die Kernzahl: Liegt er bei 80 %, bewegt
     sich das meiste Kapital INNERHALB des verfolgten Marktes und nur ein
     Fünftel kommt von außen. */
  box.innerHTML =
    `<dl class="kv">` +
    `<dt>Zeitraum</dt><dd>${d.von.slice(0, 10)} bis ${d.bis.slice(0, 10)} · ` +
    `${d.assets} Werte · ${d.schritte} Schritte · ` +
    `im Mittel ${fmtNum(d.werteJeSchritt, 1)} Werte je Schritt gemeinsam gehandelt</dd>` +
    `<dt>Umverteilt</dt><dd><b>${kfMrd(d.umverteilt)}</b> — ` +
    `<b>${fmtNum(d.anteilUmverteiltPct, 1)} %</b> der gesamten Bewegung blieb im System</dd>` +
    `<dt>Von außen</dt><dd>Zufluss ${kfMrd(d.zufluss)} · Abfluss ${kfMrd(d.abfluss)} · ` +
    `netto <b>${kfMrd(d.nettoExtern)}</b></dd>` +
    `</dl><p class="hint block">${d.hinweis || ''}</p>`;
}

async function kfCoincidence() {
  const d = await guard(() => api('/api/flow/coincidence?' + kfParams({ limit: 25 })));
  const box = $('#kf-coincidence');
  if (!d || !box) return;

  if (d.error) { box.innerHTML = `<p class="hint">${d.error}</p>`; return; }

  const l = d.fruehindikatoren || [];

  if (!l.length) {
    box.innerHTML = `<p class="hint">Kein Frühindikator über der Schwelle. ` +
      `${d.kapitalereignisse} Kapitalereignisse, ${d.kursereignisse} Kursereignisse ` +
      `im Zeitraum.</p>`;
    return;
  }

  const zeile = r => {
    /* Nur ein NEGATIVER Versatz taugt als Frühindikator. Ein positiver heißt,
       der Kurs reagierte auf die Kapitalbewegung — richtig herum gelesen ist
       das keine Vorhersage, sondern eine Nachricht. */
    const frueh = r.avgLagBars < -0.5 && r.lift > 1.3;

    return `<tr${frueh ? '' : ' class="dim"'}>
      <td><b>${r.symbol}</b></td>
      <td class="num">${r.hits}</td>
      <td class="num"><b>${fmtNum(r.lift, 2)}×</b></td>
      <td class="num">${r.avgLagBars >= 0 ? '+' : ''}${fmtNum(r.avgLagBars, 2)}</td>
      <td class="num">${fmtNum(r.avgSeverity, 0)}</td>
      <td>${frueh
        ? '<span class="verdict good">läuft vor</span>'
        : '<span class="verdict mid">folgt oder zufällig</span>'}</td>
    </tr>`;
  };

  box.innerHTML =
    `<p class="hint">${d.kapitalereignisse} Kapitalereignisse gegen ` +
    `${d.kursereignisse} Kursereignisse, Fenster ±${d.windowBars} Bars.</p>` +
    `<table class="grid"><thead><tr>
      <th>Wert</th><th class="num">Treffer</th><th class="num">Faktor</th>
      <th class="num">Versatz</th><th class="num">Ø Stufe</th><th></th>
    </tr></thead><tbody>${l.map(zeile).join('')}</tbody></table>` +
    `<p class="hint block">${d.hinweis || ''}</p>`;
}

async function kfEvents() {
  const d = await guard(() => api('/api/flow/events?' +
    kfParams({ minSeverity: $('#kf-minsev').value, limit: 120 })));

  const box = $('#kf-events');
  if (!d || !box) return;

  if (d.error) { box.innerHTML = `<p class="hint">${d.error}</p>`; return; }

  let l = d.ereignisse || [];
  const gesamt = l.length;

  if ($('#kf-suspect').checked) l = l.filter(e => !e.suspect);

  if (!l.length) {
    box.innerHTML = `<p class="hint">Nichts über Stufe ${$('#kf-minsev').value}` +
      (gesamt ? ` (${gesamt} davon als verdächtig ausgeblendet)` : '') + `.</p>`;
    return;
  }

  const zeile = e => `<tr${e.suspect ? ' class="dim"' : ''}>
    <td><b>${e.symbol}</b></td>
    <td>${e.tsUtc.slice(0, 10)}</td>
    <td>${e.kind}</td>
    <td class="num">${fmtNum(e.severity, 0)}
      <span class="bar" style="width:${Math.round(e.severity / 100 * 40)}px"></span></td>
    <td class="num">${e.returnPct >= 0 ? '+' : ''}${fmtNum(e.returnPct, 2)} %</td>
    <td class="num dim">${fmtNum(e.returnZ, 1)} σ</td>
    <td>${e.suspect ? '<span class="verdict bad">verdächtig</span>' : ''}</td>
  </tr>`;

  box.innerHTML =
    `<p class="hint">${d.gefunden.toLocaleString('de')} über Stufe ${d.minSeverity}, ` +
    `${l.length} gezeigt. ${d.skala}</p>` +
    `<table class="grid"><thead><tr>
      <th>Wert</th><th>Datum</th><th>Art</th><th class="num">Stufe</th>
      <th class="num">Änderung</th><th class="num">z</th><th></th>
    </tr></thead><tbody>${l.slice(0, 60).map(zeile).join('')}</tbody></table>`;
}

async function kfRotation() {
  const d = await guard(() => api('/api/flow/rotation-pairs?' +
    kfParams({ recentDays: 30, limit: 20 })));

  const box = $('#kf-rotation');
  if (!d || !box) return;

  if (d.error) { box.innerHTML = `<p class="hint">${d.error}</p>`; return; }

  const l = d.pairs || d.paare || [];

  if (!l.length) { box.innerHTML = '<p class="hint">Nichts Auffälliges.</p>'; return; }

  box.innerHTML =
    `<table class="grid"><thead><tr>
      <th>A</th><th>B</th><th class="num">Score</th>
      <th class="num">Anteil A Δ</th><th class="num">Anteil B Δ</th>
    </tr></thead><tbody>${l.map(p => `<tr>
      <td><b>${p.symbolA}</b></td><td><b>${p.symbolB}</b></td>
      <td class="num down">${fmtNum(p.score, 3)}</td>
      <td class="num">${p.shareChangeA >= 0 ? '+' : ''}${fmtNum(p.shareChangeA, 3)} %</td>
      <td class="num">${p.shareChangeB >= 0 ? '+' : ''}${fmtNum(p.shareChangeB, 3)} %</td>
    </tr>`).join('')}</tbody></table>` +
    (d.hinweis ? `<p class="hint block">${d.hinweis}</p>` : '');
}

/* Ein Zeitraum als „von bis", nicht als [object Object]. */
const zeitraum = z => z && z.von
  ? `${z.von.slice(0, 10)} bis ${z.bis.slice(0, 10)}`
  : '–';

async function kfGroups() {
  const box = $('#kf-groups');
  if (!box) return;

  /* Die Anlageklasse wird zu einer Liste von Kennungen aufgelöst.

     Der Endpunkt kennt keinen Klassenfilter, sondern nur `ids` — und das ist
     richtig so: Er soll nicht wissen müssen, was eine Anlageklasse ist. Die
     Auflösung gehört hierher, wo die Frage gestellt wird. */
  const cls = $('#kf-gclass').value;

  const liste = await guard(() => api(
    `/api/assets/?cls=${cls}&tracked=true&limit=200`));

  if (!liste) return;

  const items = Array.isArray(liste) ? liste : (liste.items || liste.assets || []);

  if (items.length < 6) {
    box.innerHTML = `<p class="hint">Nur ${items.length} verfolgte Werte in ` +
      `dieser Anlageklasse — zu wenige für eine Gruppensuche.</p>`;
    return;
  }

  const q = new URLSearchParams({
    interval: $('#kf-interval').value,
    months: $('#kf-gmonths').value,
    groups: $('#kf-gcount').value,
    maxSize: 10,
    ids: items.map(a => a.assetId).join(',')
  });

  setStatus(`Gruppensuche über ${items.length} Werte …`);

  const d = await guard(() => api('/api/flow/groups?' + q));

  setStatus('');

  if (!d) return;

  if (d.error) {
    box.innerHTML = `<p class="hint">${d.error}` +
      (d.hinweis ? `<br><span class="dim">${d.hinweis}</span>` : '') + `</p>`;
    return;
  }

  const l = d.gruppen || [];

  if (!l.length) {
    box.innerHTML =
      `<p class="hint">Keine Gruppe gefunden — ${d.werte} Werte hatten über ` +
      `${$('#kf-gmonths').value} Monate durchgehenden Handel, aber keine ` +
      `Teilmenge davon hielt ihre Summe besser als eine zufällige.<br>` +
      `<span class="dim">Das ist ein Ergebnis, kein Fehler. Zu versuchen wären ` +
      `ein kürzerer Zeitraum (mehr Werte bleiben übrig) oder eine andere ` +
      `Anlageklasse.</span></p>`;
    return;
  }

  box.innerHTML =
    // Beide Zeiträume kommen als Objekt mit von/bis, nicht als Zeichenkette.
    `<p class="hint">${d.werte} Werte, ${d.punkte} Zeitpunkte · ` +
    `gesucht auf ${zeitraum(d.suchzeitraum)}, ` +
    `geprüft auf <b>${zeitraum(d.pruefzeitraum)}</b></p>` +
    l.map(g => {
      /* Drei Zahlen, drei verschiedene Fragen — und nur zusammen eine Aussage.

         `geschlossenheitInnen` sagt, wie gut die Gruppe dort hält, wo sie
         gesucht wurde; das ist immer gut, sonst wäre sie nicht gefunden worden.
         `geschlossenheitAussen` sagt es für einen Zeitraum, den die Suche nie
         gesehen hat. Und `zufall` ist der Wert, den zufällig zusammengestellte
         Gruppen derselben Größe dort erreichen. Erst der Abstand zwischen den
         letzten beiden ist der Befund. */
      const besserAlsZufall = g.geschlossenheitAussen < g.zufall;

      return `<div class="band${g.haelt && besserAlsZufall ? '' : ' off'}">
        <div class="band-head">
          <div class="band-name">
            <b>Gruppe ${g.rank}</b>
            <span class="dim">${g.size} Werte</span>
          </div>
          <span class="share">${g.haelt && besserAlsZufall
            ? '<span class="verdict good">hält außerhalb</span>'
            : '<span class="verdict bad">nicht besser als Zufall</span>'}</span>
        </div>

        <p class="wippe">
          <span class="seite-a">${(g.seiteA || []).join(' · ')}</span>
          <span class="wippe-mitte">⇄</span>
          <span class="seite-b">${(g.seiteB || []).join(' · ')}</span>
        </p>

        <dl class="kv">
          <dt>Geschlossenheit</dt><dd>
            im Suchzeitraum ${fmtNum(g.geschlossenheitInnen, 3)} ·
            <b>außerhalb ${fmtNum(g.geschlossenheitAussen, 3)}</b> ·
            Zufallsgruppen ${fmtNum(g.zufall, 3)}</dd>
          <dt>Wippe</dt><dd>${fmtNum(g.wippenAnteil * 100, 1)} % der Schritte
            bewegen sich die beiden Seiten gegenläufig</dd>
        </dl>
      </div>`;
    }).join('') +
    (d.hinweis ? `<p class="hint block">${d.hinweis}</p>` : '');
}

$('#kf-run').onclick = async () => {
  setStatus('Kapitalfluss wird ausgewertet …');

  // Nacheinander, nicht gleichzeitig: Jede dieser Auswertungen liest dieselben
  // Bars über Monate. Parallel gestartet holen sie sie fünffach.
  await kfAttribution();
  await kfCoincidence();
  await kfEvents();
  await kfRotation();

  /* Die Gruppensuche laeuft NICHT mit.

     Sie braucht eine einzelne Anlageklasse, waehrend die uebrigen Auswertungen
     ueber den gesamten Markt gehen. Sie hier mitzustarten hiesse, sie mit
     Einstellungen zu fahren, unter denen sie nichts finden kann -- und das
     Ergebnis „keine Gruppe" saehe dann aus wie eine Aussage ueber den Markt
     statt ueber die Einstellung. */
  setStatus('');
};

['#kf-minsev', '#kf-suspect'].forEach(sel =>
  $(sel)?.addEventListener('change', () => kfEvents()));

$('#kf-groups-run')?.addEventListener('click', () => kfGroups());

let flowGeladen = false;

async function loadFlow() {
  if (flowGeladen) return;
  flowGeladen = true;

  await kfAttribution();
  await kfCoincidence();
}

// ------------------------------------------------------- Reasoning -------

/* Der Gesprächsagent.

   Der Verlauf lebt im Browser und nicht auf dem Server: Ein Gespräch ist an
   dieses Fenster gebunden, nicht an den Benutzer, und ein Neustart der API soll
   es nicht wegräumen. Mitgegeben werden nur die letzten Züge — der Server
   schneidet ohnehin nochmals zu. */

const rsVerlauf = [];

async function loadReasoning() {
  // Übersicht und Journal sind der Grund, warum man diese Seite öffnet — sie
  // sollen dastehen, nicht auf einen Klick warten.
  $('#rs-heute')?.click();
  $('#rs-journal')?.click();

  await loadReasoningHealth();
}

async function loadReasoningHealth() {
  const d = await guard(() => api('/api/reasoning/health'));
  const box = $('#rs-health');
  if (!d || !box) return;

  box.innerHTML =
    `<dl class="kv">` +
    `<dt>Modell</dt><dd><span class="verdict ${d.bereit ? 'good' : 'bad'}">` +
    `${d.bereit ? 'geladen' : 'nicht geladen'}</span> ${d.modell}` +
    `${d.denken ? ' · denkt vor der Antwort' : ' · ohne Denken (schnell, ungenau)'}</dd>` +
    `<dt>Werkzeuge</dt><dd class="dim">${d.werkzeuge.join(' · ')}</dd>` +
    `</dl><p class="hint block">${d.hinweis}</p>`;

  /* Die Modelliste kommt von Ollama, nicht aus einer festen Aufzählung.

     Welche davon Werkzeugaufrufe beherrschen, sagt Ollama nicht — das zeigt
     sich erst beim Fragen. Deshalb steht die Messung daneben statt einer
     Vorauswahl. */
  const sel = $('#rs-modell');

  if (sel && d.verfuegbar?.length) {
    sel.innerHTML = d.verfuegbar
      .map(m => `<option value="${m}"${m === d.modell ? ' selected' : ''}>${m}</option>`)
      .join('');
  }

  const dk = $('#rs-denken');
  if (dk) dk.checked = !!d.denken;

  await ladeEndpunkte();
}

/* ======================================================= GPU-Endpunkte =====

   Der Endpunkt entscheidet nur, WO Ollama läuft -- an keiner Zahl dieser
   Anwendung ändert er etwas. Deshalb steht die Verwaltung in der
   Reasoning-Ansicht und nicht unter System: Sie ist für genau eine Säule da. */

async function ladeEndpunkte() {
  const t = clearTable('#rs-ep-liste');
  if (!t) return;

  const d = await guard(() => api('/api/ollama/endpunkte'));
  if (!d) return;

  for (const e of d.endpunkte || []) {
    row(t, [
      { html: (e.istAktiv ? '<b>' + e.name + '</b>' : e.name)
            + (e.istAktiv ? ' <span class="pill ok">aktiv</span>' : '') },
      { html: '<code class="dim">' + e.baseUrl + '</code>' },
      { html: e.nutztTunnel
          ? '<span class="pill ok">SSH-Tunnel</span>'
          : '<span class="pill off">direkt</span>' },
      { html: e.nutztTunnel
          ? '<span class="dim">entfällt</span>'
          : (e.hatAnmeldung ? '<span class="pill ok">hinterlegt</span>'
                            : '<span class="dim">keine</span>') },
      { html: (e.istAktiv ? '' : '<button class="ep-waehlen" data-id="' + e.id + '">wählen</button> ')
            + '<button class="ep-pruefen" data-id="' + e.id + '">prüfen</button>'
            + (e.id === 'lokal' ? ''
               : ' <button class="ep-weg" data-id="' + e.id + '">entfernen</button>') }
    ]);
  }

  t.onclick = async ev => {
    const b = ev.target.closest('button');
    if (!b) return;

    const id = b.dataset.id;

    if (b.classList.contains('ep-waehlen')) {
      await guard(() => api('/api/ollama/waehlen?id=' + encodeURIComponent(id),
                            { method: 'POST' }), 'Endpunkt gewählt');
      await ladeEndpunkte();
      return;
    }

    if (b.classList.contains('ep-weg')) {
      await guard(() => api('/api/ollama/endpunkte/' + encodeURIComponent(id),
                            { method: 'DELETE' }), 'Endpunkt entfernt');
      await ladeEndpunkte();
      return;
    }

    /* Prüfen dauert -- Tunnelaufbau plus Abfrage. Ohne Rückmeldung wirkt die
       Seite eingefroren und man klickt ein zweites Mal. */
    const kasten = $('#rs-ep-pruefung');
    if (kasten) kasten.innerHTML = '<p class="hint block">Wird geprüft …</p>';

    const d2 = await guard(() => api('/api/ollama/pruefen?id=' + encodeURIComponent(id),
                                     { method: 'POST' }));
    if (!kasten) return;
    if (!d2) { kasten.innerHTML = ''; return; }

    kasten.innerHTML =
      '<p class="hint block"><span class="verdict ' + (d2.erreichbar ? 'good' : 'bad') + '">'
      + (d2.erreichbar ? 'erreichbar' : 'nicht erreichbar') + '</span> ' + esc(d2.meldung)
      + (d2.modelle?.length
          ? '<br><span class="dim">Modelle: ' + d2.modelle.map(esc).join(' · ') + '</span>'
          : '')
      + (d2.hinweis ? '<br>' + esc(d2.hinweis) : '')
      + '</p>';
  };
}

$('#rs-ep-anlegen')?.addEventListener('click', async () => {
  const ssh = $('#rs-ep-ssh').value.trim();
  const name = $('#rs-ep-name').value.trim();
  const url = $('#rs-ep-url').value.trim();

  if (!name || (!url && !ssh)) {
    setStatus('Name und Adresse werden gebraucht — oder ein SSH-Host für den Tunnel.', 'err');
    return;
  }

  const ok = await guard(() => api('/api/ollama/endpunkte', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      id: '',
      name,
      baseUrl: url || ('ssh://' + ssh + ' → 127.0.0.1:11434'),
      nutztTunnel: !!ssh,
      sshHost: ssh || null,
      sshPort: parseInt($('#rs-ep-sshport').value, 10) || 22,
      sshUser: 'root',
      remotePort: 11434,
      // Über den Tunnel wird keine Anmeldung gebraucht -- und was nicht
      // gebraucht wird, soll auch nicht gespeichert werden.
      schluessel: ssh ? null : ($('#rs-ep-key').value || null)
    })
  }), 'Endpunkt eingetragen');

  if (ok) {
    for (const id of ['#rs-ep-name', '#rs-ep-url', '#rs-ep-ssh', '#rs-ep-key']) $(id).value = '';
    await ladeEndpunkte();
  }
});

// ------------------------------------------------------------- vast.ai -----

async function ladeVast() {
  const t = clearTable('#rs-vast-liste');
  const hin = $('#rs-vast-hinweis');
  if (!t) return;

  const d = await guard(() => api('/api/vast/instanzen'));
  if (!d) return;

  if (hin) hin.innerHTML = d.hinweis ? '<p class="hint block">' + esc(d.hinweis) + '</p>' : '';

  for (const i of d.instanzen || []) {
    row(t, [
      { html: '<code>' + i.id + '</code>'
            + (i.uebernommen ? ' <span class="pill ok">übernommen</span>' : '') },
      { html: i.gpuCount + '× ' + esc(i.gpuName)
            + (i.gpuRamMb ? ' <span class="dim">' + Math.round(i.gpuRamMb / 1024) + ' GB</span>' : '')
            + (i.warnung ? '<br><span class="dim">' + esc(i.warnung) + '</span>' : '') },
      { html: i.laeuft ? '<span class="pill ok">läuft</span>'
                       : '<span class="pill off">' + esc(i.status) + '</span>' },
      { html: (i.kostenJetztProStunde || i.pricePerHour).toFixed(4) + ' $'
            + (i.laeuft ? '' : ' <span class="dim">(angehalten)</span>') },
      { html: '<button class="vast-zustand" data-id="' + i.id + '" data-laufen="'
            + (i.laeuft ? 'false' : 'true') + '">'
            + (i.laeuft ? 'anhalten' : 'fortsetzen') + '</button>'
            + (i.laeuft
               ? ' <button class="vast-uebernehmen" data-id="' + i.id + '">übernehmen</button>'
               : '') }
    ]);
  }

  t.onclick = async ev => {
    const b = ev.target.closest('button');
    if (!b) return;

    const id = b.dataset.id;

    if (b.classList.contains('vast-zustand')) {
      const d2 = await guard(() => api('/api/vast/zustand?id=' + id + '&laufen=' + b.dataset.laufen,
                                       { method: 'POST' }));
      if (d2?.hinweis) setStatus(d2.hinweis, 'ok');
    } else {
      const d2 = await guard(() => api('/api/vast/uebernehmen?id=' + id, { method: 'POST' }));
      if (d2) {
        setStatus(d2.meldung + ' ' + (d2.hinweis || ''), 'ok');
        await ladeEndpunkte();
      }
    }

    await ladeVast();
  };
}

$('#rs-vast-laden')?.addEventListener('click', ladeVast);

/* =============================================================== Nachlese ===

   Die abgelegten Gespräche. Bis hierher lebte eine Antwort nur im Browser-Tab
   -- ein Neuladen löschte sie, und eine Antwort, für die das Modell vier
   Minuten gerechnet hat, war damit weg.                                     */

let logVersatz = 0;

async function ladeLog(anhaengen) {
  const t = anhaengen ? $('#rs-log-liste')?.tBodies[0] : clearTable('#rs-log-liste');
  if (!t) return;

  if (!anhaengen) { logVersatz = 0; $('#rs-log-detail').innerHTML = ''; }

  const q = new URLSearchParams({ limit: 25, versatz: logVersatz });
  const suche = $('#rs-log-suche')?.value.trim();
  if (suche) q.set('suche', suche);
  if ($('#rs-log-gemerkt')?.checked) q.set('nurGemerkte', 'true');

  const d = await guard(() => api('/api/reasoning/log/?' + q));
  if (!d) return;

  const eintraege = d.eintraege || [];
  logVersatz += eintraege.length;

  if (!eintraege.length && !anhaengen) {
    row(t, [{ html: '<span class="dim">Noch nichts abgelegt.</span>' }, '', '', '', '', '']);
    return;
  }

  for (const e of eintraege) {
    row(t, [
      fmtDate(e.askedUtc, true),
      { html: (e.gemerkt ? '<span class="pill ok">gemerkt</span> ' : '')
            + '<b>' + esc(e.frage.length > 90 ? e.frage.slice(0, 90) + ' …' : e.frage) + '</b>'
            + '<br><span class="dim">' + esc(e.anriss || '') + '</span>'
            + (e.notiz ? '<br><span class="dim">Notiz: ' + esc(e.notiz) + '</span>' : '') },
      { html: '<span class="dim">' + esc(e.modell || '–') + '</span>'
            + (e.endpunkt ? '<br><span class="dim">' + esc(e.endpunkt) + '</span>' : '') },
      fmtNum(e.sekunden, 1) + ' s',
      /* Null Werkzeugaufrufe sind kein Schönheitsfehler, sondern ein Befund:
         Dann hat das Modell frei geantwortet, und keine Zahl darin ist belegt. */
      { html: e.werkzeugzahl > 0
          ? String(e.werkzeugzahl)
          : '<span class="pill off" title="Ohne Werkzeugaufruf ist keine Zahl der Antwort belegt.">keine</span>' },
      { html: '<button class="log-auf" data-id="' + e.logId + '">öffnen</button> '
            + '<button class="log-merken" data-id="' + e.logId + '" data-wert="'
            + (e.gemerkt ? 'false' : 'true') + '">'
            + (e.gemerkt ? 'vergessen' : 'merken') + '</button> '
            + '<button class="log-weg" data-id="' + e.logId + '">löschen</button>' }
    ]);
  }

  t.onclick = async ev => {
    const b = ev.target.closest('button');
    if (!b) return;
    const id = b.dataset.id;

    if (b.classList.contains('log-merken')) {
      await guard(() => api('/api/reasoning/log/' + id + '/merken?gemerkt=' + b.dataset.wert,
                            { method: 'POST' }), 'gespeichert');
      await ladeLog(false);
      return;
    }

    if (b.classList.contains('log-weg')) {
      await guard(() => api('/api/reasoning/log/' + id, { method: 'DELETE' }), 'Eintrag gelöscht');
      await ladeLog(false);
      return;
    }

    const d2 = await guard(() => api('/api/reasoning/log/' + id));
    if (!d2) return;

    const kasten = $('#rs-log-detail');
    kasten.innerHTML =
      '<div class="card"><h4>' + esc(d2.frage) + '</h4>'
      + '<p class="hint block">' + fmtDate(d2.askedUtc, true) + ' · ' + esc(d2.modell || '–')
      + ' · ' + esc(d2.endpunkt || '–') + ' · ' + fmtNum(d2.sekunden, 1) + ' s · '
      + d2.runden + ' Runden' + (d2.wer ? ' · ' + esc(d2.wer) : '') + '</p>'
      + '<pre class="antwort">' + esc(d2.antwort) + '</pre>'
      + '<h5>Werkzeugaufrufe</h5>'
      + (d2.werkzeuge?.length
          ? '<pre class="antwort dim">' + esc(JSON.stringify(d2.werkzeuge, null, 2)) + '</pre>'
          : '<p class="hint block"><b>Keiner.</b> Das Modell hat frei geantwortet — '
            + 'keine Zahl darin ist durch eine Messung gedeckt.</p>')
      + '<div class="toolbar">'
      + '<button id="log-md">Markdown kopieren</button>'
      + '<input id="log-notiz" size="40" placeholder="Notiz" value="'
      + esc(d2.notiz || '') + '">'
      + '<button id="log-notiz-setzen" data-id="' + d2.logId + '">Notiz speichern</button>'
      + '</div></div>';

    $('#log-md').onclick = async () => {
      await navigator.clipboard.writeText(d2.markdown || '');
      setStatus('Markdown kopiert');
    };

    $('#log-notiz-setzen').onclick = async () => {
      const n = encodeURIComponent($('#log-notiz').value);
      await guard(() => api('/api/reasoning/log/' + d2.logId + '/merken?gemerkt=true&notiz=' + n,
                            { method: 'POST' }), 'Notiz gespeichert');
      await ladeLog(false);
    };
  };
}

$('#rs-log-laden')?.addEventListener('click', () => ladeLog(false));
$('#rs-log-mehr')?.addEventListener('click', () => ladeLog(true));
$('#rs-log-suche')?.addEventListener('keydown', e => { if (e.key === 'Enter') ladeLog(false); });
$('#rs-log-gemerkt')?.addEventListener('change', () => ladeLog(false));

$('#rs-log-aufraeumen')?.addEventListener('click', async () => {
  const d = await guard(() => api('/api/reasoning/log/aufraeumen?tage=30', { method: 'POST' }));
  if (d) { setStatus(d.hinweis); await ladeLog(false); }
});

$('#rs-modell-setzen')?.addEventListener('click', async () => {
  const name = $('#rs-modell').value;
  const denken = $('#rs-denken').checked;

  const d = await guard(() => api(
    `/api/reasoning/modell?name=${encodeURIComponent(name)}&denken=${denken}`,
    { method: 'POST' }));

  if (d) setStatus(`Modell ${d.modell}, Denken ${d.denken ? 'an' : 'aus'}`);

  await loadReasoningHealth();
});

function rsAnhaengen(rolle, text, werkzeuge, meta) {
  const el = document.createElement('div');
  el.className = 'msg ' + rolle;

  const kopf = rolle === 'user' ? 'Frage' : 'Antwort';

  /* Die Werkzeugspur wird eingeklappt gezeigt. Aufgeklappt wäre sie bei sechs
     Aufrufen länger als die Antwort; ganz weggelassen wäre die Antwort nicht
     mehr überprüfbar. */
  const spur = (werkzeuge && werkzeuge.length)
    ? `<details class="tools">
         <summary>${werkzeuge.length} Werkzeugaufruf${werkzeuge.length === 1 ? '' : 'e'}
           — worauf die Antwort beruht</summary>
         ${werkzeuge.map(w => `
           <div class="tool">
             <code>${w.name}(${(w.argumente || '').replace(/</g, '&lt;')})</code>
             <pre>${(w.ergebnis || '').replace(/</g, '&lt;')}</pre>
           </div>`).join('')}
       </details>`
    : '';

  el.innerHTML =
    `<div class="msg-head">${kopf}` +
    (meta ? `<span class="dim">${meta}</span>` : '') + `</div>` +
    `<div class="msg-body">${text.replace(/</g, '&lt;').replace(/\n/g, '<br>')}</div>` +
    spur;

  $('#rs-chat').appendChild(el);
  el.scrollIntoView({ block: 'nearest' });
}

/* Die Tagesübersicht.

   Bewusst ohne Sprachmodell: Was hier steht, sind Messwerte aus den Säulen.
   Ein Modell dazwischenzuschalten hieße, Zahlen durch eine Formulierung zu
   ersetzen, die man nicht mehr nachrechnen kann — und genau davon hat dieses
   Projekt genug gesehen. */

const HEUTE_ART = {
  warnung: { farbe: 'bad', wort: 'Warnung' },
  befund: { farbe: 'good', wort: 'Befund' },
  wartung: { farbe: 'mid', wort: 'Pflege' },
  lage: { farbe: 'mid', wort: 'Lage' }
};

/* Das Tagesjournal.

   Der Markdown-Text wird mitgeliefert und hier nur leicht dargestellt: Wer ihn
   in ein Blog stellen will, kopiert ihn unverändert. Ihn im Browser in HTML zu
   verwandeln und dann wieder zurück wäre ein Umweg, bei dem Formatierung
   verlorengeht. */

let journalMarkdown = '';

$('#rs-journal').onclick = async () => {
  const q = new URLSearchParams();

  q.set('gewichte', PILLAR_KEYS
    .map(k => {
      const on = $(`.pillar-on[data-key="${k}"]`);
      const w = $(`.pillar-weight[data-key="${k}"]`);
      return `${k}:${on && !on.checked ? 0 : (w?.value || 0)}`;
    })
    .join(','));

  if ($('#rs-journal-sel').checked && chartState.selected.length)
    q.set('ids', chartState.selected.map(a => a.assetId).join(','));

  const knopf = $('#rs-journal');
  knopf.disabled = true;
  knopf.textContent = 'schreibt …';

  setStatus('Journal wird zusammengestellt …');

  const d = await guard(() => api('/api/reasoning/journal?' + q));

  knopf.disabled = false;
  knopf.textContent = 'Journal schreiben';
  setStatus('');

  if (!d) return;

  journalMarkdown = d.markdown || '';

  const dl = $('#rs-journal-dl');
  dl.href = 'data:text/markdown;charset=utf-8,' + encodeURIComponent(journalMarkdown);
  dl.download = `marktjournal-${d.tag.slice(0, 10)}.md`;
  dl.style.display = '';

  /* Fett und kursiv aus dem Markdown übernehmen, sonst nichts. Ein
     vollständiger Renderer wäre für fünf Abschnitte eine Bibliothek zu viel —
     und der Text soll ohnehin so aussehen, wie er kopiert wird. */
  const leicht = t => t
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>')
    .replace(/\*(.+?)\*/g, '<i>$1</i>')
    .replace(/^\| (.+) \|$/gm, m => m)
    .replace(/^- /gm, '• ')
    .replace(/\n/g, '<br>');

  $('#rs-journal-out').innerHTML =
    `<div class="journal">` +
    `<h4>${d.Title || d.title}</h4>` +
    `<p class="journal-lead">${leicht(d.Lead || d.lead)}</p>` +
    (d.abschnitte || []).map(a => `
      <div class="journal-abschnitt">
        <h5>${a.ueberschrift}</h5>
        <p>${leicht(a.text)}</p>
        ${a.notiz ? `<p class="journal-notiz">${leicht(a.notiz)}</p>` : ''}
      </div>`).join('') +
    (d.quellen?.length
      ? `<details class="tools"><summary>${d.quellen.length} Quellen</summary><pre>` +
        d.quellen.join('\n').replace(/</g, '&lt;') + `</pre></details>`
      : '') +
    `</div>`;
};

$('#rs-journal-copy').onclick = async () => {
  if (!journalMarkdown) { setStatus('Erst ein Journal schreiben', 'err'); return; }

  try {
    await navigator.clipboard.writeText(journalMarkdown);
    setStatus('Markdown in der Zwischenablage');
  } catch {
    setStatus('Kopieren nicht erlaubt — der Knopf „herunterladen" daneben geht immer', 'err');
  }
};

$('#rs-heute').onclick = async () => {
  const q = new URLSearchParams();

  /* Die Gewichte kommen aus den Reglern, nicht aus der Datenbank. Sie werden
     dort eingestellt; eine zweite Quelle dafür wäre eine, die irgendwann
     abweicht. */
  q.set('gewichte', PILLAR_KEYS
    .map(k => `${k}:${$(`.pillar-weight[data-key="${k}"]`)?.value || 0}`)
    .join(','));

  if ($('#rs-heute-sel').checked && chartState.selected.length)
    q.set('ids', chartState.selected.map(a => a.assetId).join(','));

  setStatus('Tagesübersicht wird zusammengestellt …');

  const d = await guard(() => api('/api/reasoning/heute?' + q));

  setStatus('');
  if (!d) return;

  const punkt = p => {
    const a = HEUTE_ART[p.art] || HEUTE_ART.lage;

    /* Punkte mit Gewicht null bleiben sichtbar, aber abgeblendet. Sie
       wegzulassen hieße zu entscheiden, was der Nutzer nicht sehen soll;
       sie gleich darzustellen hieße zu behaupten, sie wögen gleich viel. */
    return `<div class="heute-punkt${p.gewicht < 1 ? ' dim' : ''}">
      <div class="heute-kopf">
        <span class="verdict ${a.farbe}">${a.wort}</span>
        <b>${p.title}</b>
        <span class="heute-saeule">${p.saeule}</span>
        <span class="heute-gewicht" title="Auffälligkeit × Gewicht der Säule">
          ${fmtNum(p.gewicht, 0)}
          <span class="bar" style="width:${Math.round(p.gewicht / 100 * 46)}px"></span>
        </span>
      </div>
      <p>${p.detail.replace(/</g, '&lt;')}</p>
    </div>`;
  };

  const gew = Object.entries(d.gewichte || {})
    .map(([k, v]) => `${k} ${v}`).join(' · ');

  $('#rs-heute-out').innerHTML =
    `<dl class="kv">` +
    `<dt>Stand</dt><dd>${d.stand.slice(0, 16).replace('T', ' ')} UTC</dd>` +
    `<dt>Kurz</dt><dd>${d.zusammenfassung}</dd>` +
    `<dt>Gewichte</dt><dd class="dim">${gew}</dd>` +
    `</dl>` +
    d.punkte.map(punkt).join('') +
    `<div class="heute-caveats">` +
    d.einschraenkungen.map(c => `<p class="hint">${c}</p>`).join('') +
    `</div>`;
};

$('#rs-send').onclick = async () => {
  const feld = $('#rs-input');
  const frage = feld.value.trim();
  if (!frage) return;

  rsAnhaengen('user', frage);
  feld.value = '';

  const knopf = $('#rs-send');
  knopf.disabled = true;
  knopf.textContent = 'denkt …';

  /* Ein großes Modell auf der CPU braucht Minuten je Runde, und der Agent darf
     mehrere Runden Werkzeuge aufrufen. Ohne diesen Hinweis sieht die Oberfläche
     aus, als hinge sie. */
  setStatus('Der Agent arbeitet — bei einem großen Modell dauert das Minuten …');

  const d = await guard(() => api('/api/reasoning/ask', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    /* Die eingestellte Sprache geht als VORSCHLAG mit: Stellt jemand auf
       englischer Oberfläche eine deutsche Frage, sticht die Frage. Ist loc.js
       nicht geladen, fehlt das Feld und es bleibt bei Deutsch. */
    body: JSON.stringify({
      frage,
      verlauf: rsVerlauf.slice(-8),
      sprache: (typeof Loc !== 'undefined' ? Loc.code : null)
    })
  }));

  knopf.disabled = false;
  knopf.textContent = 'Fragen';
  setStatus('');

  if (!d) return;

  rsVerlauf.push({ role: 'user', content: frage });
  rsVerlauf.push({ role: 'assistant', content: d.antwort });

  rsAnhaengen('assistant', d.antwort, d.werkzeuge,
              `${d.modell} · ${d.runden} Runde${d.runden === 1 ? '' : 'n'} · ${fmtNum(d.sekunden, 0)} s`);
};

$('#rs-clear').onclick = () => {
  rsVerlauf.length = 0;
  $('#rs-chat').innerHTML = '';
};

$('#rs-input').addEventListener('keydown', e => {
  // Eingabe schickt ab, Umschalt+Eingabe macht einen Zeilenumbruch.
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); $('#rs-send').click(); }
});

// -------------------------------------------- Wissen und Semantik --------

/* Beide Säulen teilen sich Code, weil sie dieselbe Aufgabe lösen: Text
   hereinholen, einbetten, wiederfinden. Nur die Quelle ist eine andere —
   hochgeladene Bücher hier, beobachtete Adressen dort. Zwei Umsetzungen hießen
   zwei Rasteransichten, die auseinanderlaufen, sobald eine geändert wird. */

async function ladeHealth() {
  const d = await guard(() => api('/api/knowledge/health'));
  const box = $('#kn-health');
  if (!d || !box) return;

  const zeile = (ok, was, dazu) =>
    `<dt>${was}</dt><dd><span class="verdict ${ok ? 'good' : 'bad'}">` +
    `${ok ? 'erreichbar' : 'nicht erreichbar'}</span> ${dazu}</dd>`;

  box.innerHTML =
    `<dl class="kv">` +
    zeile(d.ollama, 'Ollama', `Modell ${d.modell}, ${d.dimensionen} Dimensionen`) +
    zeile(d.qdrant, 'Qdrant',
      `${d.vektorenWissen.toLocaleString('de')} Vektoren Wissen · ` +
      `${d.vektorenSemantik.toLocaleString('de')} Vektoren Semantik`) +
    `<dt>Ablage</dt><dd class="dim">${d.ablage}</dd></dl>` +
    (d.ollama && d.qdrant ? ''
      : `<p class="hint block warn">Ohne beide Dienste lässt sich nichts einbetten. ` +
        `Ollama startet mit <code>ollama serve</code>, Qdrant mit seiner ` +
        `<code>qdrant.exe</code>.</p>`);
}

/* Geholt UND eingebettet -- zwei verschiedene Tatsachen in einer Spalte.

   Vorher stand hier nur `eingebettet`, waehrend die Ueberschrift bei der Semantik
   „zuletzt geholt" versprach. Fuer einen Feed ist das systematisch leer: Ein Feed
   wird nie eingebettet, er erzeugt Artikel. Von 35 Feeds hatten 28 kein
   `indexed_utc` und zeigten deshalb „–", obwohl jeder einzelne stuendlich geholt
   wird und `last_checked_utc` bei ALLEN gesetzt war. Die Spalte behauptete also
   „nie geholt", wo „nie eingebettet, aber laufend geholt" richtig gewesen waere.

   Beides nebeneinander, weil beides zaehlt: Wann zuletzt nachgesehen wurde, und
   ob daraus etwas Durchsuchbares geworden ist.                               */
function geholtZelle(q) {
  const kurz = t => t ? t.slice(0, 16).replace('T', ' ') : null;

  const geholt = kurz(q.zuletztGeprueft);
  const ein = kurz(q.eingebettet);

  return `<td>${geholt || '<span class="dim">nie geholt</span>'}` +
         `<br><span class="dim">${
            ein ? 'eingebettet ' + ein
                : q.art === 'feed' ? 'Feeds werden nicht eingebettet'
                                   : 'nicht eingebettet'
         }</span></td>`;
}

/* Eine Rasterzeile je Quelle. */
function quellZeile(q, saeule) {
  /* Bei einem Feed zählt nicht, wie viele Abschnitte ER hat — er hat keine.
     Es zählt, wie viele Artikel aus ihm entstanden sind. */
  const zustand = q.art === 'feed'
    ? (q.artikel > 0
        ? `<span class="verdict good">${q.artikel} Artikel</span>`
        : `<span class="verdict mid">noch nichts geholt</span>`)
    : q.abschnitte > 0
      ? `<span class="verdict good">${q.abschnitte} Abschnitte</span>`
      : `<span class="verdict mid">noch nicht eingebettet</span>`;

  return `<tr class="${q.aktiv ? '' : 'dim'}">
    <td><b>${q.title}</b>${q.region ? ` <span class="tag">${q.region}</span>` : ''}
      <br><span class="dim">${q.herkunft}</span></td>
    <td class="num">${q.groesseMb > 0 ? fmtNum(q.groesseMb, 2) + ' MB' : '–'}</td>
    <td>${(q.aufgenommen || '').slice(0, 10)}</td>
    ${geholtZelle(q)}
    <td>${zustand}</td>
    <td class="dim">${q.status || ''}</td>
    <td class="row-actions">
      <button class="link kn-index" data-id="${q.sourceId}" data-saeule="${saeule}">einbetten</button>
      <button class="link kn-reindex" data-id="${q.sourceId}" data-saeule="${saeule}">neu</button>
      <button class="link kn-toggle" data-id="${q.sourceId}" data-aktiv="${!q.aktiv}" data-saeule="${saeule}">
        ${q.aktiv ? 'stilllegen' : 'aktivieren'}</button>
      <button class="link kn-del" data-id="${q.sourceId}" data-saeule="${saeule}">löschen</button>
    </td>
  </tr>`;
}

async function ladeQuellen(saeule) {
  const box = $(saeule === 'semantic' ? '#sm-grid' : '#kn-grid');
  const d = await guard(() => api(`/api/knowledge/sources?saeule=${saeule}`));
  if (!d || !box) return;

  if (!d.length) {
    box.innerHTML = `<p class="hint">Noch nichts ${saeule === 'semantic'
      ? 'eingetragen' : 'hochgeladen'}.</p>`;
    return;
  }

  /* Dieselbe Zelle unter zwei verschiedenen Ueberschriften war der Fehler --
     jetzt sagt die Ueberschrift beide Tatsachen an, die darunter stehen. */
  const kopf = saeule === 'semantic'
    ? '<th>Adresse</th><th class="num">Größe</th><th>eingetragen</th>'
      + '<th>geholt / eingebettet</th>'
    : '<th>Quelle</th><th class="num">Größe</th><th>hochgeladen</th>'
      + '<th>geholt / eingebettet</th>';

  box.innerHTML =
    `<table class="grid"><thead><tr>${kopf}` +
    `<th>Zustand</th><th>Meldung</th><th></th></tr></thead>` +
    `<tbody>${d.map(q => quellZeile(q, saeule)).join('')}</tbody></table>`;
}

/* Ein Zuhörer für beide Raster. Sie werden bei jedem Laden neu gebaut —
   einzeln gebundene Zuhörer wären danach weg. */
document.addEventListener('click', async e => {
  const b = e.target.closest('.kn-index, .kn-reindex, .kn-toggle, .kn-del');
  if (!b) return;

  const id = b.dataset.id;
  const saeule = b.dataset.saeule;

  if (b.classList.contains('kn-del')) {
    if (!confirm('Quelle mitsamt allen Abschnitten und Vektoren löschen?')) return;
    await guard(() => api(`/api/knowledge/${id}`, { method: 'DELETE' }), 'gelöscht');
  } else if (b.classList.contains('kn-toggle')) {
    await guard(() => api(`/api/knowledge/active/${id}?aktiv=${b.dataset.aktiv}`,
                          { method: 'POST' }));
  } else {
    const neu = b.classList.contains('kn-reindex');

    setStatus(`Einbetten läuft — bei einem Fachbuch dauert das Minuten …`);

    const r = await guard(() => api(`/api/knowledge/index/${id}?neu=${neu}`,
                                    { method: 'POST' }));
    if (r) setStatus(r.hinweis);
  }

  await ladeQuellen(saeule);
  await ladeHealth();
});

/* Hochladen. */
$('#kn-upload').onclick = async () => {
  const inp = $('#kn-file');
  if (!inp.files.length) { setStatus('Erst Dateien wählen', 'err'); return; }

  const fd = new FormData();
  for (const f of inp.files) fd.append('file', f);

  setStatus('Hochladen …');

  const res = await fetch('/api/knowledge/upload?saeule=knowledge',
                          { method: 'POST', body: fd });

  if (!res.ok) { setStatus('Hochladen fehlgeschlagen', 'err'); return; }

  const d = await res.json();

  setStatus(`${d.angelegt.length} aufgenommen` +
            (d.abgelehnt.length ? `, ${d.abgelehnt.length} abgelehnt` : ''));

  if (d.abgelehnt.length)
    alert('Abgelehnt:\n' + d.abgelehnt.map(x => `${x.fileName}: ${x.grund}`).join('\n'));

  inp.value = '';
  await ladeQuellen('knowledge');
};

$('#kn-refresh').onclick = () => { ladeQuellen('knowledge'); ladeHealth(); };

/* Adresse eintragen. */
$('#sm-add').onclick = async () => {
  const url = $('#sm-url').value.trim();
  if (!url) { setStatus('Adresse fehlt', 'err'); return; }

  const d = await guard(() => api('/api/knowledge/web?saeule=semantic', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      url,
      title: $('#sm-title').value.trim() || null,
      pollMinutes: parseInt($('#sm-poll').value, 10) || 240
    })
  }), 'eingetragen');

  if (!d) return;

  $('#sm-url').value = '';
  $('#sm-title').value = '';

  await ladeQuellen('semantic');
};

/* Startliste eintragen — für beide Säulen dieselbe Mechanik. */
async function kuratiertEintragen(saeule) {
  const d = await guard(() => api(`/api/knowledge/kuratiert?saeule=${saeule}`,
                                  { method: 'POST' }));
  if (!d) return;

  setStatus(`${d.angelegt} neu eingetragen` +
            (d.vorhanden ? `, ${d.vorhanden} waren schon da` : '') +
            (d.fehler.length ? `, ${d.fehler.length} Fehler` : ''));

  if (d.fehler.length) alert('Nicht eingetragen:\n' + d.fehler.join('\n'));

  await ladeQuellen(saeule);
}

$('#sm-curated').onclick = () => kuratiertEintragen('semantic');
$('#kn-curated').onclick = () => kuratiertEintragen('knowledge');

/* Feeds abholen. Läuft lange — die Meldung sagt es. */
$('#sm-feeds').onclick = async () => {
  const knopf = $('#sm-feeds');
  knopf.disabled = true;
  knopf.textContent = 'holt ab …';

  setStatus('Feeds werden abgeholt und neue Artikel eingebettet — das dauert bei '
          + 'vielen Quellen einige Minuten …');

  const proFeed = parseInt($('#sm-perfeed').value, 10) || 15;

  const d = await guard(() => api(
    `/api/knowledge/feeds?saeule=semantic&proFeed=${proFeed}`, { method: 'POST' }));

  knopf.disabled = false;
  knopf.textContent = 'Feeds abholen';
  setStatus('');

  if (!d) return;

  $('#sm-feedrun').innerHTML =
    `<dl class="kv">` +
    `<dt>Durchgang</dt><dd>${d.feeds} Feeds fällig · ` +
    `<b>${d.neueArtikel} neue Artikel</b> · ${d.eingebettet} eingebettet` +
    (d.fehler ? ` · <span class="verdict bad">${d.fehler} Fehler</span>` : '') +
    `</dd></dl>` +
    (d.meldungen?.length
      ? `<details class="tools"><summary>je Quelle</summary><pre>` +
        d.meldungen.join('\n').replace(/</g, '&lt;') + `</pre></details>`
      : '') +
    `<p class="hint block">${d.hinweis}</p>`;

  await ladeQuellen('semantic');
  await ladeHealth();
};

/* Alle noch nicht eingebetteten Quellen der Wissenssäule nacheinander
   durchgehen. Nacheinander, nicht gleichzeitig: Ollama bettet ohnehin
   seriell ein, und parallel gestartet konkurrieren die Anfragen nur um
   dieselbe Warteschlange. */
/* ======================================================= Alle einbetten ===

   Der Lauf sitzt im Server; hier wird nur gestartet und der Stand abgefragt.

   Der Vorgaenger lief im Browser: eine Anfrage je Quelle und dazwischen jedes
   Mal ein Neuladen der gesamten Quellenliste. Bei 504 offenen Quellen sind das
   ueber tausend Anfragen, der Fortschritt haengt am offenen Tab, und wer das
   Fenster schliesst, hat einen halb eingebetteten Bestand ohne Protokoll.    */

function einbettenAlle(saeule, pfx) {
  const start = $('#' + pfx + '-alle-start');
  const stop  = $('#' + pfx + '-alle-stop');
  const kasten = $('#' + pfx + '-alle-stand');
  if (!start) return;

  let uhr = null;

  function zeige(d) {
    if (!d || d.nieGelaufen) { kasten.innerHTML = ''; return; }

    const anteil = Math.round((d.anteil || 0) * 100);

    kasten.innerHTML =
      '<p class="hint block">'
      + (d.laeuft
          ? `<b>Läuft:</b> ${d.erledigt} von ${d.gesamt} (${anteil} %)`
            + (d.aktuell ? ' · <span class="dim">' + esc(d.aktuell) + '</span>' : '')
          : '<b>Fertig.</b> ' + esc(d.bilanz || ''))
      + (d.meldung ? ' <span class="dim">' + esc(d.meldung) + '</span>' : '')
      + '</p>'
      /* Ohne Text ist kein Fehler des Laufs, sondern ein Befund ueber die Quelle --
         getrennt gezeigt, sonst liest sich ein sauberer Lauf wie ein kaputter. */
      + (d.gesamt
          ? '<dl class="kv">'
            + `<dt>eingebettet</dt><dd>${d.eingebettet} Quellen, ${d.abschnitte} Abschnitte</dd>`
            + `<dt>ohne Text</dt><dd>${d.ohneText}</dd>`
            + `<dt>fehlgeschlagen</dt><dd>${d.fehler}</dd>`
            + '</dl>'
          : '');

    start.hidden = !!d.laeuft;
    stop.hidden = !d.laeuft;

    if (!d.laeuft && uhr) { clearInterval(uhr); uhr = null; ladeHealth?.(); }
  }

  async function stand() {
    const d = await guard(() => api('/api/knowledge/alle/stand?saeule=' + saeule));
    zeige(d);
  }

  start.onclick = async () => {
    const umfang = $('#' + pfx + '-alle-umfang').value;

    const d = await guard(() => api(
      `/api/knowledge/alle?saeule=${saeule}&umfang=${umfang}`, { method: 'POST' }));

    if (!d) return;
    setStatus(d.hinweis);

    await stand();
    /* Zwei Sekunden: Der Lauf braucht je Quelle Sekunden, haeufiger zu fragen
       zeigt dieselbe Zahl noch einmal. */
    if (!uhr) uhr = setInterval(stand, 2000);
  };

  stop.onclick = async () => {
    await guard(() => api('/api/knowledge/alle/abbrechen?saeule=' + saeule,
                          { method: 'POST' }), 'wird abgebrochen');
    await stand();
  };

  // Beim Aufbau einmal nachsehen -- ein Lauf kann aus einer frueheren Sitzung laufen.
  stand();
}

einbettenAlle('knowledge', 'kn');
einbettenAlle('semantic', 'sm');

$('#sm-refresh-all').onclick = async () => {
  setStatus('Adressen werden abgeholt …');

  const d = await guard(() => api('/api/knowledge/refresh?saeule=semantic',
                                  { method: 'POST' }));
  if (d) setStatus(`${d.aufgefrischt} Adressen neu eingebettet`);

  await ladeQuellen('semantic');
  await ladeHealth();
};

/* Suchen — für beide Säulen dasselbe. */
async function sucheWissen(saeule) {
  const feld = $(saeule === 'semantic' ? '#sm-query' : '#kn-query');
  const box = $(saeule === 'semantic' ? '#sm-results' : '#kn-results');

  const frage = feld.value.trim();
  if (!frage) { setStatus('Erst eine Frage eingeben', 'err'); return; }

  const d = await guard(() => api(
    `/api/knowledge/search?frage=${encodeURIComponent(frage)}&saeule=${saeule}&limit=8`));

  if (!d) return;

  if (!d.treffer.length) {
    box.innerHTML = '<p class="hint">Kein Treffer. Ist schon etwas eingebettet?</p>';
    return;
  }

  /* Jeder Treffer führt an seine Stelle zurück.

     Ein Fundstellenhinweis ohne Weg dorthin ist eine Behauptung: Wer ihn
     nachschlagen will, müsste das ganze Buch durchsehen — und genau das soll
     die Suche ersparen. Bei PDFs springt der Verweis auf die Seite, sonst über
     einen Textanker an die Textstelle. */
  box.innerHTML = d.treffer.map(t => {
    const f = t.fundstelle || {};

    const datum = t.occurredUtc
      ? `<span class="dim">${t.occurredUtc.slice(0, 10)}</span>` : '';

    const ziel = t.verweis
      ? `<a href="${t.verweis}" target="_blank" rel="noopener">${f.beschreibung || 'zur Stelle'} ↗</a>`
      : `<span class="dim">${f.beschreibung || ''}</span>`;

    return `
    <div class="hit">
      <div class="hit-head">
        <b>${t.title}</b>
        ${datum}
        <span class="stelle">${ziel}</span>
        <span class="score">Ähnlichkeit ${fmtNum(t.aehnlichkeit, 3)}</span>
      </div>
      <p>${t.text.replace(/</g, '&lt;')}</p>
      <p class="dim small">${t.quelle}</p>
    </div>`;
  }).join('') +
    `<p class="hint block">${d.hinweis}</p>`;
}

$('#kn-search').onclick = () => sucheWissen('knowledge');
$('#sm-search').onclick = () => sucheWissen('semantic');

$('#kn-query').addEventListener('keydown', e => { if (e.key === 'Enter') sucheWissen('knowledge'); });
$('#sm-query').addEventListener('keydown', e => { if (e.key === 'Enter') sucheWissen('semantic'); });

let knowledgeGeladen = false;
let semanticGeladen = false;

async function loadKnowledge() {
  if (knowledgeGeladen) return;
  knowledgeGeladen = true;

  await ladeHealth();
  await ladeQuellen('knowledge');
}

async function loadSemantic() {
  if (semanticGeladen) return;
  semanticGeladen = true;

  await ladeQuellen('semantic');
}

// ---------------------------------------------------- Kurvendiskussion ---

/* Ein Durchgang, ein Bestand, eine Nummer.

   Die Funde werden abgelegt statt bei jeder Abfrage neu gerechnet. Der Grund
   ist die Blätterfunktion: Über 300 Werte dauert die Rechnung Minuten, und wer
   von Seite drei auf Seite vier klickt, will nicht durch einen Bestand
   blättern, der sich zwischendurch neu bildet. */

const cvState = { seite: 1, lauf: null };

const CV_ARTEN = {
  hochpunkt: 'Hochpunkt',
  tiefpunkt: 'Tiefpunkt',
  wendepunkt: 'Wendepunkt',
  sattelpunkt: 'Sattelpunkt',
  steigungsausbruch: 'Steigungsausbruch',
  kruemmungsausbruch: 'Krümmungsausbruch',
  sprung: 'Sprung'
};

$('#cv-scan').onclick = async () => {
  const q = new URLSearchParams({
    interval: $('#ma-interval').value || '1d',
    halbfenster: $('#cv-half').value,
    kausal: $('#cv-causal').checked,
    minZ: $('#cv-minz').value,
    minStufe: $('#cv-minsev').value,
    verknuepfungsfenster: $('#cv-link').value
  });

  if ($('#cv-selected').checked) {
    if (!chartState.selected.length) {
      setStatus('Keine Werte ausgewählt — im Reiter „Kurse" wählen', 'err');
      return;
    }
    q.set('ids', chartState.selected.map(a => a.assetId).join(','));
  }

  setStatus('Kurvendiskussion läuft — das kann über den gesamten Bestand Minuten dauern …');

  const d = await guard(() => api('/api/curve/scan?' + q, { method: 'POST' }));
  if (!d) return;

  setStatus('');

  $('#cv-runinfo').innerHTML =
    `<dl class="kv">` +
    `<dt>Lauf</dt><dd>Nummer ${d.runId} — ${d.werte} Werte, ` +
    `<b>${d.ereignisse.toLocaleString('de')}</b> Stellen, ` +
    `${d.verknuepfungen.toLocaleString('de')} Verknüpfungen ` +
    `in ${fmtNum(d.dauerSekunden, 1)} s</dd>` +
    `<dt>Glättung</dt><dd class="${d.hinweis.includes('UNBRAUCHBAR') ? 'warn' : ''}">` +
    `${d.hinweis}</dd></dl>`;

  cvState.lauf = d.runId;
  cvState.seite = 1;

  await ladeLaeufe();
  await ladeCurveGrid();
  await ladeCurveLinks();
};

let curveGeladen = false;

async function loadCurve() {
  if (curveGeladen) return;
  curveGeladen = true;

  if (!$('#cv-asset').options.length || $('#cv-asset').options.length === 1) {
    await fillAssetSelect($('#cv-asset'));

    // "alle" wieder nach vorn — fillAssetSelect ersetzt den Inhalt.
    $('#cv-asset').insertAdjacentHTML('afterbegin', '<option value="">alle</option>');
    $('#cv-asset').value = '';
  }

  await ladeLaeufe();
  await ladeCurveGrid();
  await ladeCurveLinks();
}

async function ladeLaeufe() {
  const d = await guard(() => api('/api/curve/runs'));
  if (!d) return;

  const sel = $('#cv-run');

  sel.innerHTML = d.map(r =>
    `<option value="${r.run_id}">#${r.run_id} — ${(r.started_utc || '').slice(0, 16).replace('T', ' ')} · ` +
    `${r.events?.toLocaleString('de') || 0} Stellen · Fenster ${r.half_window} · ` +
    `${r.causal ? 'kausal' : 'zentriert'}</option>`).join('');

  if (cvState.lauf) sel.value = cvState.lauf;
  else if (sel.options.length) cvState.lauf = parseInt(sel.value, 10);

  // Artenliste einmal füllen.
  const t = $('#cv-type');
  if (t.options.length <= 1) {
    t.innerHTML = '<option value="">alle</option>' +
      Object.entries(CV_ARTEN).map(([k, v]) => `<option value="${k}">${v}</option>`).join('');
  }
}

async function ladeCurveGrid() {
  const q = new URLSearchParams({
    seite: cvState.seite,
    groesse: $('#cv-size').value,
    sortierung: $('#cv-sort').value,
    minStufe: $('#cv-fsev').value
  });

  if (cvState.lauf) q.set('lauf', cvState.lauf);
  if ($('#cv-type').value) q.set('art', $('#cv-type').value);
  if ($('#cv-asset').value) q.set('assetId', $('#cv-asset').value);

  const d = await guard(() => api('/api/curve/events?' + q));
  if (!d) return;

  if (!d.gesamt) {
    $('#cv-grid').innerHTML = '<p class="hint">Noch kein Durchgang gerechnet.</p>';
    $('#cv-pager').innerHTML = '';
    $('#cv-stats').innerHTML = '';
    return;
  }

  const zeile = r => `<tr>
    <td><b>${r.symbol}</b> <span class="dim">${r.klasse}</span></td>
    <td>${r.ts.slice(0, 10)}</td>
    <td>${r.bezeichnung}</td>
    <td class="num">${fmtNum(r.stufe, 1)}
      <span class="bar" style="width:${Math.round(r.stufe / 100 * 46)}px"></span></td>
    <td class="num">${fmtNum(r.kurs, 4)}</td>
    <td class="num dim">${fmtNum(r.geglaettet, 4)}</td>
    <td class="num">${r.steigungPct >= 0 ? '+' : ''}${fmtNum(r.steigungPct, 3)} %</td>
    <td class="num dim">${fmtNum(r.kruemmung, 2)}</td>
  </tr>`;

  $('#cv-grid').innerHTML =
    `<table class="grid"><thead><tr>
      <th>Wert</th><th>Datum</th><th>Stelle</th><th class="num">Stufe</th>
      <th class="num">Kurs</th><th class="num">geglättet</th>
      <th class="num">Steigung/Bar</th><th class="num">Krümmung ‱</th>
    </tr></thead><tbody>${d.zeilen.map(zeile).join('')}</tbody></table>`;

  renderPager(d);
  ladeCurveStats();
}

/* Blättern ohne alle Seitenzahlen: Bei 185 Seiten wäre eine vollständige Leiste
   breiter als das Fenster. Gezeigt werden die Nachbarn der aktuellen Seite,
   Anfang und Ende — der Rest ist über das Eingabefeld erreichbar. */
function renderPager(d) {
  const p = d.seite, n = d.seiten;
  if (n <= 1) { $('#cv-pager').innerHTML = ''; return; }

  const knopf = (s, txt, aktiv) =>
    `<button class="cv-page${aktiv ? ' active' : ''}" data-p="${s}">${txt}</button>`;

  const seiten = new Set([1, n, p, p - 1, p + 1, p - 2, p + 2]);

  const liste = [...seiten].filter(x => x >= 1 && x <= n).sort((a, b) => a - b);

  let html = knopf(Math.max(1, p - 1), '‹ zurück', false);
  let vorher = 0;

  for (const x of liste) {
    if (vorher && x > vorher + 1) html += '<span class="dim"> … </span>';
    html += knopf(x, x, x === p);
    vorher = x;
  }

  html += knopf(Math.min(n, p + 1), 'weiter ›', false);
  html += `<span class="dim">Seite <input id="cv-goto" type="number" min="1" max="${n}" `
        + `value="${p}" style="width:70px"> von ${n} · `
        + `${d.gesamt.toLocaleString('de')} Stellen</span>`;

  $('#cv-pager').innerHTML = html;
}

$('#cv-pager').addEventListener('click', e => {
  const b = e.target.closest('.cv-page');
  if (!b) return;
  cvState.seite = parseInt(b.dataset.p, 10);
  ladeCurveGrid();
});

$('#cv-pager').addEventListener('change', e => {
  if (e.target.id !== 'cv-goto') return;
  cvState.seite = Math.max(1, parseInt(e.target.value, 10) || 1);
  ladeCurveGrid();
});

['#cv-type', '#cv-asset', '#cv-sort', '#cv-size', '#cv-fsev'].forEach(sel => {
  $(sel)?.addEventListener('change', () => { cvState.seite = 1; ladeCurveGrid(); });
});

$('#cv-run')?.addEventListener('change', () => {
  cvState.lauf = parseInt($('#cv-run').value, 10);
  cvState.seite = 1;
  ladeCurveGrid();
  ladeCurveLinks();
});

async function ladeCurveStats() {
  const q = cvState.lauf ? `?lauf=${cvState.lauf}` : '';
  const d = await guard(() => api('/api/curve/stats' + q));
  if (!d || !d.arten?.length) { $('#cv-stats').innerHTML = ''; return; }

  const gesamt = d.arten.reduce((a, x) => a + x.anzahl, 0);

  $('#cv-stats').innerHTML =
    `<p class="hint block"><b>Verteilung:</b> ` +
    d.arten.map(a =>
      `${CV_ARTEN[a.event_type] || a.event_type} ` +
      `<b>${a.anzahl.toLocaleString('de')}</b> (${(a.anzahl / gesamt * 100).toFixed(0)} %, ` +
      `Ø Stufe ${fmtNum(a.mittlere_stufe, 0)})`).join(' · ') +
    `<br><span class="dim">${d.hinweis}</span></p>`;
}

async function ladeCurveLinks() {
  const q = new URLSearchParams({ limit: 60 });
  if (cvState.lauf) q.set('lauf', cvState.lauf);

  const d = await guard(() => api('/api/curve/links?' + q));
  if (!d) return;

  const l = d.verknuepfungen || [];

  if (!l.length) {
    $('#cv-links').innerHTML = '<p class="hint">Keine Verknüpfung über der Schwelle.</p>';
    return;
  }

  const zeile = r => {
    /* Der Vorlauf bekommt nur dann Farbe, wenn er einer ist: Abstand von null
       verschieden UND der Anteil klar über der Hälfte. Alles andere ist
       Gleichzeitigkeit und darf nicht wie ein Befund aussehen. */
    const vorlauf = Math.abs(r.median_lag_bars) >= 1
                 && (r.lead_share > 0.65 || r.lead_share < 0.35);

    return `<tr>
      <td><b>${r.symbol_a}</b> → <b>${r.symbol_b}</b></td>
      <td class="dim">${CV_ARTEN[r.type_a] || r.type_a} / ${CV_ARTEN[r.type_b] || r.type_b}</td>
      <td class="num"><b>${fmtNum(r.lift, 2)}×</b></td>
      <td class="num">${r.pairs}</td>
      <td class="num dim">${fmtNum(r.expected, 1)}</td>
      <td class="num">${r.median_lag_bars >= 0 ? '+' : ''}${fmtNum(r.median_lag_bars, 1)}</td>
      <td class="num">${fmtNum(r.lead_share * 100, 0)} %</td>
      <td>${vorlauf
        ? '<span class="verdict good">Vorlauf</span>'
        : '<span class="verdict mid">gleichzeitig</span>'}</td>
    </tr>`;
  };

  $('#cv-links').innerHTML =
    `<table class="grid"><thead><tr>
      <th>Paar</th><th>Arten</th><th class="num">Faktor</th>
      <th class="num">Treffer</th><th class="num">erwartet</th>
      <th class="num">Abstand</th><th class="num">danach</th><th></th>
    </tr></thead><tbody>${l.map(zeile).join('')}</tbody></table>`;
}

// ------------------------------------------------ Mathematische Analyse ---

let maPlot = null;

async function loadPillars() {
  await ladeGewichteVomServer();
  renderPillarWeights();

  if (!$('#ma-asset').options.length) await fillAssetSelect($('#ma-asset'));

  // Stand der ersten Säule aus den vorhandenen Auswertungen.
  const acc = await guard(() => api('/api/forecast/accuracy'));
  const dl = $('#pillar-learning-state');
  dl.innerHTML = '';

  if (!acc || !acc.length) {
    dl.innerHTML = '<dt>—</dt><dd>Noch keine ausgewerteten Prognosen.</dd>';
    return;
  }

  acc.forEach(a => {
    dl.insertAdjacentHTML('beforeend',
      `<dt>${a.horizonLabel}</dt>` +
      `<dd>${fmtNum(a.scored, 0)} ausgewertet · ${fmtNum(a.hitRatePct, 1)} % Richtung getroffen · ` +
      `${fmtNum(a.meanAbsPctError, 2)} % Betragsfehler</dd>`);
  });
}

$('#ma-run').onclick = runMathAnalysis;

async function runMathAnalysis() {
  const id = parseInt($('#ma-asset').value, 10);
  if (!id) { setStatus('Erst einen Wert wählen', 'err'); return; }

  const cfg = pillarConfig();
  const sp = cfg.strategies.spectrum?.params || {};

  const q = new URLSearchParams({
    interval: $('#ma-interval').value,
    months: $('#ma-months').value,
    segment: sp.segment ?? 256,
    minPeriod: sp.minPeriod ?? 5,
    maxPeriod: sp.maxPeriod ?? 120,
    ssaWindow: cfg.strategies.ssa?.params.ssaWindow ?? 0,
    ssaComponents: cfg.strategies.ssa?.params.ssaComponents ?? 4,
    horizon: 30
  });

  const d = await guard(() => api(`/api/spectral/${id}?${q}`));
  if (!d) return;

  renderSpectrumResult(d);
  renderSpectrumChart(d);
  await runMathForecast(id, cfg, sp);
}

function renderSpectrumResult(d) {
  const badge = (text, kind) => `<span class="verdict ${kind}">${text}</span>`;

  // ------------------------------------------------------------- Spektrum
  const r = d.spektrum.aufRenditen;
  const b = d.spektrum.aufBandpass;

  /* Beide Grundlagen nebeneinander, weil sie verschiedene Fragen beantworten
     und häufig verschiedene Antworten geben. Nur eine zu zeigen hieße, sich
     die passendere auszusuchen. */
  $('#ma-res-spectrum').innerHTML =
    `<div>Auf <b>Renditen</b>: stärkste Periode <span class="num">${fmtNum(r.dominantPeriod, 1)}</span> Bars, ` +
    `${fmtNum(r.prominence, 1)}× über dem Untergrund ${verdictFor(r.prominence, 4, 8)}</div>` +
    `<div>Auf <b>Bandpass</b>: stärkste Periode <span class="num">${fmtNum(b.dominantPeriod, 1)}</span> Bars, ` +
    `${fmtNum(b.prominence, 1)}× über dem Untergrund ${verdictFor(b.prominence, 4, 8)}</div>` +
    `<div class="dim">Spektrale Entropie ${fmtNum(b.spectralEntropy, 3)} — ` +
    `nahe 1 heißt strukturlos. Anteil langer Perioden ${fmtNum(b.lowBandShare * 100, 1)} %.</div>`;

  // ---------------------------------------------------------- Stabilität
  const st = d.stabilitaet;
  const kind = st.stability >= 0.7 ? 'good' : st.stability >= 0.4 ? 'mid' : 'bad';

  $('#ma-res-stability').innerHTML =
    `<div>In <span class="num">${fmtNum(st.stability * 100, 0)} %</span> von ${st.windows} Fenstern ` +
    `dieselbe Periode um <span class="num">${fmtNum(st.medianPeriod, 1)}</span> Bars ` +
    `(± ${fmtNum(st.spread, 1)}) ${badge(st.urteil, kind)}</div>`;

  // -------------------------------------------------------- Momentanzyklus
  const h = d.momentanzyklus;

  $('#ma-res-hilbert').innerHTML = h.valid
    ? `<div>Zykluslänge <span class="num">${fmtNum(h.cyclePeriod, 1)}</span> Bars, ` +
      `Phase <span class="num">${fmtNum(h.phase, 0)}°</span> — <b>${h.lage}</b>, ` +
      `Güte ${fmtNum(h.phaseQuality * 100, 0)} % ${verdictFor(h.phaseQuality, 0.3, 0.6)}</div>`
    : `<div>Kein brauchbarer Zyklus erkennbar ${badge('unbestimmt', 'bad')}</div>`;

  // ------------------------------------------------------------- Zerlegung
  const z = d.zerlegung;

  $('#ma-res-ssa').innerHTML =
    `<div>Fensterlänge ${z.windowLength}, ${z.usedComponents} Komponenten, ` +
    `<span class="num">${fmtNum(z.explainedShare * 100, 1)} %</span> der Streuung erklärt</div>` +
    `<div>${z.komponenten.map(c =>
      // Ein Trend hat keine Periode -- eine Null dort zu zeigen waere falsch.
      `${c.kind} <span class="dim">(${c.dominantPeriod > 0 ? 'T≈' + fmtNum(c.dominantPeriod, 0) + ', ' : ''}` +
      `${fmtNum(c.varianceShare * 100, 1)} %)</span>`
    ).join(' · ')}</div>` +
    (z.forecastValid ? '' : `<div class="dim">Fortschreibung nicht möglich: ${z.note}</div>`);

  // --------------------------------------------------------- Marktverhalten
  const m = d.marktverhalten;
  const mk = m.label === 'richtungslos' ? 'mid' : 'good';

  $('#ma-res-regime').innerHTML =
    `<div>Hurst-Exponent <span class="num">${fmtNum(m.hurst, 3)}</span> ${badge(m.label, mk)} ` +
    `<span class="dim">Vertrauen ${fmtNum(m.confidence * 100, 0)} %, Anpassungsgüte ${fmtNum(m.fitQuality, 2)}</span></div>` +
    `<div class="dim">Vorabgewichte: ${Object.entries(m.vorabgewichte)
      .map(([k, v]) => `${k} ${fmtNum(v * 100, 1)} %`).join(' · ')}</div>`;
}

function verdictFor(v, mid, good) {
  if (v >= good) return `<span class="verdict good">deutlich</span>`;
  if (v >= mid) return `<span class="verdict mid">schwach</span>`;
  return `<span class="verdict bad">nicht unterscheidbar</span>`;
}

function renderSpectrumChart(d) {
  const host = $('#ma-chart');
  host.innerHTML = '';

  if (maPlot) { maPlot.destroy(); maPlot = null; }

  const kurve = d.spektrum.kurve || [];
  if (kurve.length < 4) { host.innerHTML = '<p class="hint">Zu wenige Daten.</p>'; return; }

  const xs = kurve.map(p => p.periode);
  const ys = kurve.map(p => p.verhaeltnis);

  maPlot = new uPlot({
    width: Math.max(320, host.clientWidth - 8),
    height: 240,
    scales: { x: { time: false } },
    axes: darkAxes(),
    legend: { show: false },
    cursor: { drag: { x: false, y: false } },
    series: [
      { label: 'Periode (Bars)' },
      {
        label: 'Verhältnis zum Untergrund',
        stroke: '#6aa9ff',
        width: 1.6,
        fill: 'rgba(106,169,255,0.12)'
      }
    ]
  }, [xs, ys], host);
}

async function runMathForecast(id, cfg, sp) {
  const on = Object.entries(cfg.strategies)
    .filter(([, v]) => v.enabled)
    .map(([k]) => k);

  const box = $('#ma-forecast');

  if (!on.length) {
    box.innerHTML = '<p class="hint">Kein Verfahren aktiviert.</p>';
    return;
  }

  const q = new URLSearchParams({
    interval: $('#ma-interval').value,
    months: $('#ma-months').value,
    horizon: 30,
    strategies: on.join(','),
    minPeriod: sp.minPeriod ?? 5,
    maxPeriod: sp.maxPeriod ?? 120,
    ssaWindow: cfg.strategies.ssa?.params.ssaWindow ?? 0,
    ssaComponents: cfg.strategies.ssa?.params.ssaComponents ?? 4,
    halfLife: cfg.strategies.hilbert?.params.halfLife ?? 20
  });

  const f = await guard(() => api(`/api/spectral/forecast/${id}?${q}`));
  if (!f) return;

  const rows = f.verfahren.map(v =>
    `<tr><td>${v.label}</td>` +
    `<td class="num">${fmtNum(v.confidence * 100, 0)} %</td>` +
    `<td>${v.liefertPfad ? 'Prognose' : 'nur Beurteilung'}</td>` +
    `<td class="dim">${v.note}</td></tr>`).join('');

  const last = f.punkte[f.punkte.length - 1];

  box.innerHTML =
    `<table class="grid"><thead><tr><th>Verfahren</th><th class="num">Vertrauen</th>` +
    `<th>Art</th><th>Befund</th></tr></thead><tbody>${rows}</tbody></table>` +
    (f.tragfaehig
      ? `<p class="hint block">Über ${f.horizon} Bars: von ${fmtNum(f.letzterKurs, 4)} auf ` +
        `<b>${fmtNum(last.close, 4)}</b> (${last.changePct >= 0 ? '+' : ''}${fmtNum(last.changePct, 2)} %). ` +
        `${f.hinweis}</p>`
      : `<p class="hint block">${f.hinweis}</p>`);
}


// --------------------------------------- Mathematische Analyse: alle Werte ---

/* Die zweite Hälfte der Säule. Sie fragt nicht, was in einem Kurs steckt,
   sondern welche Zeitskalen viele Kurse gleichzeitig treiben — und wer dabei
   vorausläuft.

   Getrennt nach Anlageklasse, und zwar zwingend: Aktien und Krypto haben
   verschiedene Handelskalender. Gemischt fände die Kreuzspektralmatrix eine
   Wochenperiodik, die alle Aktien teilen und kein Krypto — den Kalender also
   statt den Markt. */

let mmPlot = null;

$('#mm-scope').addEventListener('click', e => {
  const b = e.target.closest('button[data-scope]');
  if (!b) return;
  $$('#mm-scope button').forEach(x => x.classList.toggle('active', x === b));
});

/** Die Werte des gewählten Betrachtungsraums, als Id-Liste. */
async function marketScopeIds() {
  const scope = $('#mm-scope button.active')?.dataset.scope || 'stocks';

  if (scope === 'sel') {
    if (chartState.selected.length < 8) {
      setStatus('Mindestens acht Werte auswählen — darunter trägt die Matrix nicht', 'err');
      return null;
    }
    return chartState.selected.map(a => a.assetId);
  }

  const list = await guard(() => api('/api/assets/?tracked=true&limit=400'));
  if (!list) return null;

  const wanted = scope === 'crypto'
    ? ['Crypto']
    : ['Stock', 'Etf'];

  return list.filter(a => wanted.includes(a.assetClass)).map(a => a.assetId);
}

$('#mm-run').onclick = runMarketModes;

async function runMarketModes() {
  const ids = await marketScopeIds();
  if (!ids || ids.length < 8) {
    setStatus('Zu wenige Werte im Betrachtungsraum', 'err');
    return;
  }

  const panel = $('[data-panel="math"]');
  panel.classList.add('mm-busy');

  /* Eine mitlaufende Uhr statt einer Schätzung.

     Zuerst stand hier „dauert rund zwanzig Sekunden" — eine Zahl aus einem Lauf
     über dreißig Werte. Der Aufwand wächst aber mit dem Quadrat der Wertezahl
     und linear mit den Ersatzläufen; bei zweihundert Werten liegt sie um
     Größenordnungen daneben. Eine falsche Zusage ist schlechter als keine: Wer
     nach zwanzig Sekunden nichts sieht, hält es für abgestürzt.

     Die Uhr sagt nichts zu, sondern zeigt, dass gearbeitet wird. */
  const started = Date.now();

  const ticker = setInterval(() => {
    const s = Math.round((Date.now() - started) / 1000);
    setStatus(`rechnet über ${ids.length} Werte … ${s} s`, 'busy');
  }, 1000);

  setStatus(`rechnet über ${ids.length} Werte …`, 'busy');

  try {
    const common = new URLSearchParams({
      ids: ids.join(','),
      interval: '1d',
      months: '300',
      segment: $('#mm-segment').value,
      minPeriod: '5',
      maxPeriod: $('#mm-max').value,
      overlap: '0.85'
    });

    const modesQ = new URLSearchParams(common);
    modesQ.set('surrogates', $('#mm-surrogates').value);
    modesQ.set('modes', $('#mm-modes').value);
    modesQ.set('limit', '60');
    modesQ.set('members', '25');

    const m = await guard(() => api('/api/spectral/modes?' + modesQ));
    if (m) { renderMarketModes(m); renderModeProfile(m); }

    const leadQ = new URLSearchParams(common);
    leadQ.set('modes', $('#mm-modes').value);
    leadQ.set('skipModes', '1');
    leadQ.set('limit', '12');

    const l = await guard(() => api('/api/spectral/leaders?' + leadQ));
    if (l) renderLeaders(l);
  } finally {
    clearInterval(ticker);
    panel.classList.remove('mm-busy');

    const took = Math.round((Date.now() - started) / 1000);
    setStatus(took > 3 ? `fertig nach ${took} s` : '');
  }
}

function renderMarketModes(d) {
  const box = $('#mm-modes-out');

  if (!d.moden || !d.moden.length) {
    box.innerHTML = `<p class="hint">${d.note || 'Kein Ergebnis.'}</p>`;
    return;
  }

  /* Nach Rang zusammengefasst statt Frequenz für Frequenz. Zweihundert Zeilen
     mit je einer Frequenz sind keine Auskunft — die Frage lautet, wie stark
     jede Mode insgesamt ist und wie weit ihre Werte zeitlich auseinanderliegen. */
  const byRank = {};

  for (const x of d.moden) {
    const r = x.rank ?? 1;
    (byRank[r] ??= []).push(x);
  }

  const mean = (a, f) => a.reduce((s, x) => s + f(x), 0) / a.length;

  const rows = Object.entries(byRank).map(([rank, list]) => {
    const share = mean(list, x => x.explainedShare);
    const spread = mean(list, x => x.phaseSpreadBars);
    const sig = list.filter(x => x.significant).length;

    return `<tr>
      <td class="num">${rank}</td>
      <td class="num">${fmtNum(share * 100, 1)} %</td>
      <td class="num">${fmtNum(spread, 2)}</td>
      <td class="num">${sig} / ${list.length}</td>
      <td>${rank === '1'
        ? 'Marktfaktor — alle laufen gleichzeitig, prognostisch wertlos'
        : spread > 10 ? 'Umschichtung mit zeitlichem Versatz' : 'schwach ausgeprägt'}</td>
    </tr>`;
  }).join('');

  box.innerHTML =
    `<p class="hint">${d.assets} Werte, ${d.points} gemeinsame Punkte, ` +
    `${d.segments} Abschnitte, ${d.degreesOfFreedom} Freiheitsgrade · ` +
    `${d.bedeutsam} von ${d.geprueft} geprüften Frequenz-Moden-Paaren bedeutsam ` +
    `bei Schwelle z&gt;${fmtNum(d.threshold, 2)}</p>` +
    `<table class="grid"><thead><tr>` +
    `<th class="num">Mode</th><th class="num">erklärter Anteil</th>` +
    `<th class="num">Phasenspreizung</th><th class="num">bedeutsam</th><th>Lesart</th>` +
    `</tr></thead><tbody>${rows}</tbody></table>` +
    `<p class="hint block">${d.note}</p>`;
}

function renderModeProfile(d) {
  const host = $('#mm-chart');
  host.innerHTML = '';

  if (mmPlot) { mmPlot.destroy(); mmPlot = null; }

  const first = (d.moden || []).filter(x => (x.rank ?? 1) === 1)
                               .sort((a, b) => a.periodBars - b.periodBars);

  if (first.length < 4) { host.innerHTML = '<p class="hint">Zu wenige Punkte.</p>'; return; }

  const xs = first.map(x => x.periodBars);
  const real = first.map(x => x.explainedShare);
  const surr = first.map(x => x.surrogateShare);

  mmPlot = new uPlot({
    width: Math.max(320, host.clientWidth - 8),
    height: 240,
    scales: { x: { time: false } },
    axes: darkAxes(),
    cursor: { drag: { x: false, y: false } },
    series: [
      { label: 'Periode (Bars)' },
      { label: 'gemessen', stroke: '#6aa9ff', width: 1.8, fill: 'rgba(106,169,255,0.12)' },
      /* Die Ersatzkurve gehört zwingend daneben. Ohne sie liest man einen
         Anteil von 0,35 als hoch oder niedrig, je nach Erwartung — mit ihr
         sieht man den Abstand, und nur der ist die Aussage. */
      { label: 'Ersatzreihen', stroke: '#e28c7e', width: 1.4, dash: [5, 4] }
    ]
  }, [xs, real, surr], host);
}

function renderLeaders(d) {
  const box = $('#mm-leaders');

  if (!d.baender || !d.baender.length) {
    box.innerHTML = '<p class="hint">Kein Ergebnis.</p>';
    return;
  }

  const row = r => {
    const width = Math.round(Math.min(1, Math.abs(r.vorlaufAnteil) / 0.35) * 60);
    return `<tr>
      <td>${r.symbol}</td>
      <td class="dim">${r.sektor || r.klasse || ''}</td>
      <td class="num">${r.vorlaufBars >= 0 ? '+' : ''}${fmtNum(r.vorlaufBars, 2)}</td>
      <td class="num">${fmtNum(r.bestaendigkeit, 2)}</td>
      <td><span class="bar" style="width:${width}px"></span></td>
    </tr>`;
  };

  box.innerHTML = d.baender.map(b => `
    <h4>${b.band} <span class="dim">— ${b.moden} Moden, ${b.werte} Werte</span></h4>
    <div class="two-col">
      <div>
        <p class="hint">läuft voraus</p>
        <table class="grid"><thead><tr>
          <th>Symbol</th><th>Sektor</th><th class="num">Vorlauf</th>
          <th class="num">Beständigkeit</th><th></th>
        </tr></thead><tbody>${b.vorlaeufer.slice(0, 8).map(row).join('')}</tbody></table>
      </div>
      <div>
        <p class="hint">läuft nach</p>
        <table class="grid"><thead><tr>
          <th>Symbol</th><th>Sektor</th><th class="num">Vorlauf</th>
          <th class="num">Beständigkeit</th><th></th>
        </tr></thead><tbody>${b.nachzuegler.slice(0, 8).map(row).join('')}</tbody></table>
      </div>
    </div>`).join('') +
    `<p class="hint block">${d.hinweis}</p>`;
}


// ------------------------------------------------ Säule Deep Learning ---

/* Die einzige Säule, die sich nicht erklären kann.

   Alle anderen Verfahren lassen sich nachlesen: Warum eine Mode bedeutsam ist,
   warum ein Vorlauf gemessen wurde, warum ein Merkmal nichts beiträgt. Ein
   gelerntes Netz kann das nicht — was es weiß, steht in Gewichten.

   Deshalb wird hier grundsätzlich die Messung neben die Vorhersage gestellt,
   nicht darunter und nicht auf Nachfrage. Wer eine Zahl sieht, sieht daneben,
   was sie im Sperrbereich wert war. */

/* Die drei Bänder mit ihren Gewichten.

   Die Gewichte stehen bewusst NEBEN den Messwerten und nicht getrennt davon.
   Ein Regler ohne die Zahl, gegen die er sich rechtfertigen muss, lädt dazu
   ein, nach Erwartung zu gewichten statt nach Ergebnis — und genau das ist
   der Fehler, den diese Säule strukturell verhindern soll. */

let deepBands = [];

const BAND_ORDER = ['kurz', 'mittel', 'lang'];

const BAND_TITEL = {
  kurz: 'Kurzfristig',
  mittel: 'Mittelfristig',
  lang: 'Langfristig'
};

/* Gewichte je Band. Ein fehlendes Modell behält seinen Wert — wird es später
   trainiert, ist die Einstellung noch da. */
const deepWeights = { kurz: 100, mittel: 100, lang: 100 };

async function loadDeepStatus() {
  const box = $('#dl-bands');
  const d = await guard(() => api('/api/deep/status'));
  if (!d) return;

  if (!d.geladen) {
    deepBands = [];
    box.innerHTML =
      `<p class="hint">${d.hinweis}</p>` +
      `<pre class="dim">POST /api/model/export?interval=1d&horizons=1,2,3,5,10,20,60,120,250
` +
      `python ml/train_deep.py --csv D:/_data/stockcrawler/features_1d.csv --name deep_short --horizons 1,2,3,5   --seq 48
` +
      `python ml/train_deep.py --csv D:/_data/stockcrawler/features_1d.csv --name deep_mid   --horizons 10,20,60  --seq 96
` +
      `python ml/train_deep.py --csv D:/_data/stockcrawler/features_1d.csv --name deep_long  --horizons 120,250   --seq 128 --channels 32</pre>`;
    return;
  }

  deepBands = d.modelle || [];

  const karten = BAND_ORDER.map(band => {
    const m = deepBands.find(x => x.band === band);
    return m ? bandKarte(m) : bandFehlt(band, d.fehlend);
  }).join('');

  box.innerHTML = karten + `<div id="dl-band-sum"></div>` +
    `<p class="hint block">${d.hinweis}</p>`;

  renderDeepWeights();
  fillBacktestBands();
}

function bandKarte(m) {
  const g = m.gemessen;

  /* Die Farbe muss dem Urteil folgen, nicht der ersten Latte.

     Das lange Modell hat Fehlerverhältnis 0,9832 und 59,9 % Richtung und
     bekam damit ein grünes Urteil — obwohl es die blosse Drift in keinem
     einzigen Horizont schlägt. Grün für „hat gelernt, dass Kurse steigen"
     ist genau die Art Anzeige, gegen die dieses Projekt gebaut ist. */
  const traegt = (g.jeHorizont || []).some(h => h.traegt);

  const kind = traegt ? 'good'
             : g.fehlerverhaeltnisSperrbereich >= 1 ? 'bad' : 'mid';

  /* Zwei Latten je Zeile, nicht eine.

     Ein Fehlerverhältnis von 0,96 sieht nach Erfolg aus. Daneben steht, was
     die blosse Trainingsdrift erreicht — eine einzige Zahl —, und beim langen
     Modell sind das 0,91. Erst dieser Vergleich ist die Aussage. */
  const proHorizont = (g.jeHorizont || []).map(h => `<tr${h.traegt ? '' : ' class="dim"'}>
       <td class="num">${h.horizontBars}</td>
       <td class="num">${h.fehlerverhaeltnis === null ? '–' : fmtNum(h.fehlerverhaeltnis, 4)}</td>
       <td class="num">${h.trefferquote === null ? '–' : fmtNum(h.trefferquote, 1) + ' %'}</td>
       <td class="num dim">${h.driftFehlerverhaeltnis == null ? '–' : fmtNum(h.driftFehlerverhaeltnis, 4)}</td>
       <td class="num dim">${h.driftTrefferquote == null ? '–' : fmtNum(h.driftTrefferquote, 1) + ' %'}</td>
       <td>${h.traegt
             ? '<span class="verdict good">trägt</span>'
             : '<span class="verdict bad">Drift besser</span>'}</td>
     </tr>`).join('');

  return `
  <div class="band" data-band="${m.band}">
    <div class="band-head">
      <div class="band-name">
        <b>${BAND_TITEL[m.band]}</b>
        <span class="dim">${m.bandLabel}</span>
      </div>
      <div class="weight">
        <label>Gewicht</label>
        <input type="range" class="band-weight" data-band="${m.band}"
               min="0" max="100" value="${deepWeights[m.band]}">
        <output class="band-weight-out" data-band="${m.band}">${deepWeights[m.band]}</output>
        <span class="share" data-band-share="${m.band}"></span>
      </div>
    </div>

    <dl class="kv">
      <dt>Fassung</dt><dd>${m.version} · Fenster ${m.seqLen} Bars ·
        ${m.werte} Werte · Horizonte ${m.horizonte.join(', ')}</dd>
      <dt>Im Sperrbereich</dt><dd>Fehlerverhältnis
        <b>${fmtNum(g.fehlerverhaeltnisSperrbereich, 4)}</b>,
        Richtung ${fmtNum(g.trefferquoteSperrbereich, 2)} %
        <span class="verdict ${kind}">${m.urteil}</span></dd>
    </dl>

    ${proHorizont ? `<table class="grid compact"><thead>
      <tr><th></th><th colspan="2">Modell</th><th colspan="2">blosse Drift</th><th></th></tr>
      <tr><th class="num">Bars</th>
          <th class="num">Fehlerverh.</th><th class="num">Richtung</th>
          <th class="num">Fehlerverh.</th><th class="num">Richtung</th>
          <th></th></tr></thead>
      <tbody>${proHorizont}</tbody></table>
      <p class="hint">Die <b>blosse Drift</b> ist die mittlere Rendite des
      Trainingszeitraums — eine einzige Zahl, ohne Netz. Über lange Horizonte
      steigen Kurse im Mittel; wer nur „aufwärts" sagt, schlägt den Stillstand
      zwangsläufig. Erst wer die Drift schlägt, hat etwas gelernt.</p>` : ''}

    <p class="hint">
      Vorschlag der Messung: <b>${m.gewichtsvorschlag}</b>
      <button class="link band-apply" data-band="${m.band}"
              data-w="${m.gewichtsvorschlag}">übernehmen</button>
      <span class="dim">— null, sobald ein Band den Stillstand nicht schlägt.
      Ein Verfahren, das schlechter ist als gar keines, wird durch ein kleines
      Gewicht nicht besser, nur unauffälliger.</span>
    </p>
  </div>`;
}

function bandFehlt(band, fehlend) {
  const f = (fehlend || []).find(x => x === band);

  return `
  <div class="band off" data-band="${band}">
    <div class="band-head">
      <div class="band-name">
        <b>${BAND_TITEL[band]}</b>
        <span class="dim">noch nicht trainiert</span>
      </div>
    </div>
    <p class="hint">Ohne Modell kein Gewicht. Der Platz bleibt stehen, damit
      sichtbar ist, was fehlt.</p>
  </div>`;
}

/* Anteile je Band und die Zahl, die der Nutzer sonst im Kopf bilden müsste:
   wie viel der Gewichtung auf Bänder entfällt, die sich bewährt haben. */
function renderDeepWeights() {
  const vorhanden = deepBands.map(m => m.band);
  const summe = vorhanden.reduce((a, b) => a + deepWeights[b], 0);

  vorhanden.forEach(b => {
    const out = $(`.band-weight-out[data-band="${b}"]`);
    const share = $(`[data-band-share="${b}"]`);
    if (out) out.textContent = deepWeights[b];
    if (!share) return;

    share.textContent = summe > 0
      ? `${(deepWeights[b] / summe * 100).toFixed(1)} % der Säule`
      : 'alle auf null';
  });

  /* „Trägt" heißt: schlägt den Stillstand UND die blosse Drift.

     Die erste Fassung prüfte nur die erste Latte und zählte das lange Modell
     mit — Fehlerverhältnis 0,9832, Richtung 59,9 %, sieht gut aus. Je Horizont
     schlägt es die Drift aber in keinem einzigen Fall. Entschieden wird das
     deshalb am Horizont und auf dem Server, wo beide Zahlenreihen liegen. */
  const tragend = deepBands
    .filter(m => (m.gemessen.jeHorizont || []).some(h => h.traegt))
    .reduce((a, m) => a + deepWeights[m.band], 0);

  const el = $('#dl-band-sum');
  if (!el) return;

  const anteil = summe > 0 ? tragend / summe * 100 : 0;

  el.innerHTML =
    `<p class="hint block"><b>Zusammensetzung der Säule:</b> ` +
    vorhanden.map(b =>
      `${BAND_TITEL[b]} ${summe > 0 ? (deepWeights[b] / summe * 100).toFixed(0) : 0} %`
    ).join(' · ') +
    `<br><b>Davon auf Bänder, die sich im Sperrbereich bewährt haben: ` +
    `<span class="verdict ${anteil > 0 ? 'good' : 'bad'}">${anteil.toFixed(0)} %</span></b> — ` +
    (anteil > 0
      ? 'der Rest verteilt Anteile an einem Beitrag, den die Messung nicht bestätigt.'
      : 'kein geladenes Modell schlägt bislang die Annahme, es ändere sich nichts. ' +
        'Die Regler lassen sich stellen, ändern aber nichts an dieser Zahl.') +
    `</p>`;
}

/* Ein Zuhörer für den ganzen Bereich statt einer je Regler — die Karten werden
   bei jedem Zustandsabruf neu gebaut, einzeln gebundene Zuhörer wären dann
   weg. */
$('#dl-bands')?.addEventListener('input', e => {
  const r = e.target.closest('.band-weight');
  if (!r) return;

  deepWeights[r.dataset.band] = parseInt(r.value, 10) || 0;
  renderDeepWeights();
  saveUiState();
});

$('#dl-bands')?.addEventListener('click', e => {
  const b = e.target.closest('.band-apply');
  if (!b) return;

  const band = b.dataset.band;
  deepWeights[band] = parseInt(b.dataset.w, 10) || 0;

  const r = $(`.band-weight[data-band="${band}"]`);
  if (r) r.value = deepWeights[band];

  renderDeepWeights();
  saveUiState();
});

async function loadDeep() {
  renderPillarWeights();
  await loadDeepStatus();

  if (!$('#dl-asset').options.length) await fillAssetSelect($('#dl-asset'));
}

$('#dl-run').onclick = async () => {
  const id = parseInt($('#dl-asset').value, 10);
  if (!id) { setStatus('Erst einen Wert wählen', 'err'); return; }

  const q = new URLSearchParams({
    wKurz: deepWeights.kurz,
    wMittel: deepWeights.mittel,
    wLang: deepWeights.lang
  });

  const d = await guard(() => api(`/api/deep/forecast/${id}?${q}`));
  const box = $('#dl-forecast');
  if (!d) { box.innerHTML = ''; return; }

  const bandBlock = b => {
    const rows = b.prognosen.map(p => {
      /* Aussagekraft unter eins heißt: Das Modell hält sein eigenes Vorzeichen
         für nicht tragfähig. Das gehört sichtbar in die Zeile, nicht in eine
         Fußnote — sonst liest man die Richtung als Aussage. */
      const weak = p.aussagekraft < 1;

      return `<tr${weak ? ' class="dim"' : ''}>
        <td class="num">${p.horizontBars}</td>
        <td>${p.ziel.slice(0, 10)}</td>
        <td class="num">${fmtNum(p.kurs, 4)}</td>
        <td class="num">${p.changePct >= 0 ? '+' : ''}${fmtNum(p.changePct, 2)} %</td>
        <td class="num">± ${fmtNum(p.streuungPct, 2)} %</td>
        <td class="num">${fmtNum(p.bandUntenPct, 2)} … ${fmtNum(p.bandObenPct, 2)}</td>
        <td class="num">${fmtNum(p.aussagekraft, 2)}${weak
          ? ' <span class="verdict bad">trägt nicht</span>' : ''}</td>
      </tr>`;
    }).join('');

    const anteil = d.gewichtung.anteile[b.band];

    return `<div class="band-fc${b.traegt ? '' : ' off'}">
      <h4>${BAND_TITEL[b.band]} <span class="dim">${b.bandLabel}</span>
        <span class="share">Gewicht ${b.gewicht} · ${fmtNum(anteil, 1)} % der Säule</span></h4>
      <p class="hint">Im Sperrbereich: Fehlerverhältnis
        <b>${fmtNum(b.gemessen.fehlerverhaeltnisSperrbereich, 4)}</b>,
        Richtung ${fmtNum(b.gemessen.trefferquoteSperrbereich, 2)} %
        <span class="verdict ${b.traegt ? 'good' : 'bad'}">${b.traegt
          ? 'trägt' : 'trägt nicht'}</span></p>
      <table class="grid"><thead><tr>
        <th class="num">Bars</th><th>Ziel</th><th class="num">Kurs</th>
        <th class="num">Änderung</th><th class="num">Streuung</th>
        <th class="num">Band</th><th class="num">Aussagekraft</th>
      </tr></thead><tbody>${rows}</tbody></table>
    </div>`;
  };

  const fehlt = (d.fehlend || []).map(f =>
    `<li>${BAND_TITEL[f.band] || f.band}: ${f.grund}` +
    `${f.benoetigt ? ` (${f.benoetigt} Bars nötig)` : ''}</li>`).join('');

  box.innerHTML =
    `<p class="hint">${d.symbol} — letzter Kurs ${fmtNum(d.letzterKurs, 4)} ` +
    `(${d.stand.slice(0, 10)})</p>` +
    d.baender.map(bandBlock).join('') +
    (fehlt ? `<p class="hint block"><b>Ohne Antwort:</b><ul>${fehlt}</ul></p>` : '') +
    `<p class="hint block">${d.hinweis}</p>` +
    `<p class="hint block">Aussagekraft ist die Vorhersage geteilt durch ihre eigene ` +
    `Unsicherheit. Unter eins sagt das Modell selbst, dass sein Vorzeichen nicht trägt — ` +
    `solche Zeilen sind abgeblendet.</p>`;
};

/* ----------------------------------------------------- Rueckblick --------
   Prognose gegen Wirklichkeit.

   Zwei Diagramme statt einem, und das ist kein Schmuck. Uebereinander gelegt
   sehen Prognose- und Kurskurve fast immer gut aus: Beide folgen dem Kurs, und
   bei taeglichen Bewegungen von zwei Prozent liegt die Vorhersage optisch immer
   nah dran -- auch dann, wenn sie nichts weiter tut, als den letzten Wert zu
   wiederholen. Das zweite Diagramm zeigt deshalb die Abweichung selbst, und
   daneben die Abweichung dessen, der gar nichts vorhersagt. Erst dieser
   Vergleich ist eine Aussage. */

let btPlot = null;
let btErrPlot = null;

/* Bandwahl und Horizontliste hängen zusammen: Jedes Band kennt nur seine
   eigenen Horizonte. Eine gemeinsame Liste böte Kombinationen an, die kein
   Modell beantworten kann. */
function fillBacktestBands() {
  const sel = $('#dl-bt-band');
  if (!sel) return;

  const vorher = sel.value;

  sel.innerHTML = deepBands
    .map(m => `<option value="${m.band}">${BAND_TITEL[m.band]} — ${m.horizonte.join(', ')} Bars</option>`)
    .join('');

  if (vorher && deepBands.some(m => m.band === vorher)) sel.value = vorher;

  fillBacktestHorizons();
}

function fillBacktestHorizons() {
  const sel = $('#dl-bt-horizon');
  const band = $('#dl-bt-band')?.value;
  const m = deepBands.find(x => x.band === band);

  if (!sel || !m) { if (sel) sel.innerHTML = ''; return; }

  sel.innerHTML = m.horizonte
    .map(h => `<option value="${h}">${h} Bar${h === 1 ? '' : 's'}</option>`).join('');
}

$('#dl-bt-band')?.addEventListener('change', fillBacktestHorizons);

$('#dl-backtest').onclick = async () => {
  const id = parseInt($('#dl-asset').value, 10);
  if (!id) { setStatus('Erst einen Wert wählen', 'err'); return; }

  const h = parseInt($('#dl-bt-horizon').value, 10) || 0;
  const band = $('#dl-bt-band').value || 'kurz';

  setStatus('Rückblick wird gerechnet …');
  const d = await guard(() => api(`/api/deep/backtest/${id}?band=${band}&horizon=${h}&maxPunkte=800`));

  if (!d) return;
  setStatus('');

  renderBacktestSummary(d);
  renderBacktestChart(d);
};

function renderBacktestSummary(d) {
  const k = d.kennzahlen;

  const kind = k.fehlerverhaeltnis >= 1 ? 'bad'
             : k.trefferquotePct <= 52.3 ? 'mid' : 'good';

  $('#dl-bt-summary').innerHTML =
    `<dl class="kv">` +
    `<dt>Band</dt><dd>${BAND_TITEL[d.band] || d.band} — ${d.bandLabel}</dd>` +
    `<dt>Bereich</dt><dd>${d.bereich}, ab ${d.abUtc.slice(0, 10)} — ` +
    `${k.anzahl} Vorhersagen bei ${d.horizontBars} Bars Vorlauf</dd>` +
    `<dt>Mittlere Abweichung</dt><dd>` +
    `mit Modell <b>${fmtNum(k.mittlererFehlerPct, 3)} %</b> · ` +
    `ohne Modell ${fmtNum(k.fehlerOhneModellPct, 3)} % · ` +
    `Verhältnis <b>${fmtNum(k.fehlerverhaeltnis, 4)}</b></dd>` +
    `<dt>Richtung</dt><dd>${fmtNum(k.trefferquotePct, 1)} % ` +
    `<span class="verdict ${kind}">${d.urteil}</span></dd>` +
    /* Die Verzerrung steht dabei, weil sie eine andere Krankheit anzeigt als
       der Fehler: Ein Modell kann im Betrag gut sein und trotzdem systematisch
       zu hoch oder zu tief liegen. */
    `<dt>Verzerrung</dt><dd>${k.verzerrungPct >= 0 ? '+' : ''}` +
    `${fmtNum(k.verzerrungPct, 4)} % im Mittel — ` +
    `${Math.abs(k.verzerrungPct) < 0.05 ? 'kein nennenswerter Hang nach oben oder unten'
      : k.verzerrungPct > 0 ? 'sagt systematisch zu hoch voraus'
      : 'sagt systematisch zu tief voraus'}</dd>` +
    `</dl>`;
}

function renderBacktestChart(d) {
  const host = $('#dl-bt-chart');
  const errHost = $('#dl-bt-err');

  host.innerHTML = '';
  errHost.innerHTML = '';

  // Der Hinweis unter dem Diagramm wird nachtraeglich eingehaengt und muss
  // beim naechsten Lauf verschwinden, sonst stapeln sich alte Zahlen.
  errHost.nextElementSibling?.classList.contains('hint')
    && errHost.nextElementSibling.remove();

  if (btPlot) { btPlot.destroy(); btPlot = null; }
  if (btErrPlot) { btErrPlot.destroy(); btErrPlot = null; }

  const pts = d.punkte || [];
  if (pts.length < 3) { host.innerHTML = '<p class="hint">Zu wenige Punkte.</p>'; return; }

  // uPlot rechnet in Sekunden, nicht in Millisekunden.
  const xs = pts.map(p => Date.parse(p.ts) / 1000);

  const width = () => Math.max(320, host.clientWidth - 8);

  btPlot = new uPlot({
    width: width(),
    height: 260,
    axes: darkAxes(),
    cursor: { drag: { x: true, y: false } },
    series: [
      {},
      { label: 'tatsächlich', stroke: '#9aa7b8', width: 1.6 },
      { label: 'Prognose', stroke: '#6aa9ff', width: 1.4 }
    ]
  }, [xs, pts.map(p => p.tatsaechlich), pts.map(p => p.prognose)], host);

  /* Zweites Diagramm: der aufsummierte Vorsprung.

     Der naheliegende Entwurf -- beide Fehlerkurven uebereinander -- ist
     unbrauchbar, und das laesst sich am ersten Versuch ablesen: 2,173 % gegen
     2,170 %. Die Kurven liegen so dicht beieinander, dass die obere die untere
     verdeckt, und man sieht bei jedem Modell dasselbe Bild.

     Aufsummiert wird der Unterschied dagegen sichtbar. Je Vorhersage wird
     eingetragen, um wie viel der Fehler kleiner war als der desjenigen, der gar
     nichts vorhersagt. Die Summe steigt, solange das Modell beitraegt, und
     faellt, solange es schadet. Ein Modell ohne Beitrag laeuft als Abwaertslinie
     durchs Bild -- unmissverstaendlich, wo zwei fast deckungsgleiche Kurven
     nichts sagen. */
  let run = 0;
  const advantage = pts.map(p => {
    run += Math.abs(p.tatsaechlichPct) - Math.abs(p.abweichungPct);
    return Math.round(run * 1000) / 1000;
  });

  btErrPlot = new uPlot({
    width: width(),
    height: 200,
    axes: darkAxes(),
    cursor: { drag: { x: true, y: false } },
    series: [
      {},
      { label: 'aufsummierter Vorsprung (Prozentpunkte)',
        stroke: run >= 0 ? '#5fbf8f' : '#e28c7e', width: 1.6,
        fill: run >= 0 ? 'rgba(95,191,143,0.12)' : 'rgba(226,140,126,0.12)' }
    ]
  }, [xs, advantage], errHost);

  const days = pts.length;
  errHost.insertAdjacentHTML('afterend',
    `<p class="hint block">Nach ${days} Vorhersagen steht der Vorsprung bei ` +
    `<b>${run >= 0 ? '+' : ''}${fmtNum(run, 2)}</b> Prozentpunkten. ` +
    (run >= 0
      ? 'Die Linie steigt — das Modell liegt in der Summe näher an der Wahrheit '
        + 'als die Annahme, es ändere sich nichts.'
      : 'Die Linie fällt — das Modell liegt in der Summe weiter von der Wahrheit '
        + 'entfernt als die Annahme, es ändere sich nichts. Genau dafür ist dieses '
        + 'Diagramm da: Übereinandergelegte Kursverläufe hätten das verdeckt.') +
    `</p>`);
}

// ================================================================ Start ===

/* ==================================================================== Tausch ===

   Die Seite beantwortet eine einzige Frage: „Ich stecke mit soundsoviel
   Kapital in diesem Wert — wogegen sollte ich tauschen?"

   Zwei Dinge müssen dabei sichtbar bleiben, sonst wird aus einer Beobachtung
   eine Empfehlung:

   1. Der Paargewinn ist VERGANGENHEIT. Er sagt, was der Tausch seit dem
      Kippen eingebracht hätte, nicht was er einbringen wird.
   2. Ohne Rückhalt aus früheren Kreuzungen ist die Zeile eine Anekdote. Das
      Abzeichen steht deshalb in derselben Zeile und nicht in einer Fußnote.  */

async function loadBestand() {
  const t = clearTable('#sw-bestand');
  const d = await guard(() => api('/api/bestand'));
  if (!d) { emptyRow(t, 9, 'Bestand konnte nicht geladen werden.'); return; }

  const pos = d.positionen || [];
  if (!pos.length) {
    emptyRow(t, 9, 'Noch nichts eingetragen. Ohne Bestand gibt es nichts zu tauschen.');
  } else {
    for (const h of pos) {
      row(t, [
        { html: '<b>' + h.symbol + '</b>' },
        h.name || '–',
        { text: h.klasse, cls: 'dim' },
        { text: fmtNum(h.kapital, 2) + ' ' + h.waehrung, cls: 'num' },
        { text: h.kurs == null ? '–' : fmtNum(h.kurs, 4), cls: 'num' },
        { text: h.einstand == null ? '–' : fmtNum(h.einstand, 4), cls: 'num' },
        { text: h.buchstand == null ? '–' : fmtNum(h.buchstand, 2),
          cls: 'num ' + (h.buchstand == null ? 'dim'
                         : h.buchstand >= h.kapital ? 'up' : 'down') },
        { text: h.notiz || '', cls: 'dim' },
        { html: '<button class="sw-del" data-id="' + h.assetId + '">entfernen</button>' }
      ]);
    }
  }

  /* Ereignis am Tabellenkörper statt an jeder Schaltfläche: Die Zeilen werden
     bei jedem Laden neu gebaut, und einzeln gesetzte Handler wären dann weg. */
  t.onclick = async e => {
    const b = e.target.closest('button.sw-del');
    if (!b) return;
    await guard(() => api('/api/bestand/' + b.dataset.id, { method: 'DELETE' }), 'Position entfernt');
    await loadBestand();
    /* Die Vorschlaege haengen am Bestand. Sie stehenzulassen hiesse, Ziele
       fuer eine Position anzuzeigen, die es nicht mehr gibt. */
    await loadTausch();
  };

  $('#sw-summe').textContent = pos.length
    ? pos.length + ' Positionen, zusammen ' + fmtNum(d.kapitalGesamt, 2) + '.'
    : '';
}

$('#sw-add').onclick = async () => {
  const symbol = $('#sw-symbol').value.trim();
  const kapital = parseFloat($('#sw-kapital').value);

  if (!symbol) { setStatus('Symbol fehlt.', 'err'); return; }
  if (!(kapital > 0)) { setStatus('Kapital muss größer als null sein.', 'err'); return; }

  const einstandRoh = parseFloat($('#sw-einstand').value);

  const ok = await guard(() => api('/api/bestand', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      symbol: symbol,
      kapital: kapital,
      waehrung: $('#sw-waehrung').value.trim() || 'USD',
      einstand: Number.isFinite(einstandRoh) ? einstandRoh : null,
      notiz: $('#sw-notiz').value.trim() || null
    })
  }), symbol + ' eingetragen');

  if (ok) {
    $('#sw-symbol').value = '';
    $('#sw-kapital').value = '';
    $('#sw-einstand').value = '';
    $('#sw-notiz').value = '';
    await loadBestand();
    await loadTausch();
  }
};

async function loadTausch() {
  const q = new URLSearchParams({
    interval: $('#sw-interval').value,
    tage: $('#sw-tage').value,
    haltedauer: $('#sw-halte').value,
    jeBestand: $('#sw-je').value,
    nurBewaehrt: $('#sw-nurbew').value
  });

  $('#sw-hinweis').textContent = 'wird gerechnet …';
  $('#sw-ziele').innerHTML = '';

  const d = await guard(() => api('/api/bestand/tausch?' + q));
  if (!d) { $('#sw-hinweis').textContent = 'Abfrage fehlgeschlagen.'; return; }

  $('#sw-hinweis').innerHTML = (d.hinweis || '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');

  const ziel = $('#sw-ziele');

  for (const pos of d.positionen || []) {
    const box = document.createElement('div');
    box.className = 'card';

    const kopf = document.createElement('h3');
    kopf.innerHTML = pos.symbol + ' <span class="dim">' + pos.klasse + '</span>'
      + ' <span class="dim">— ' + fmtNum(pos.kapital, 2) + ' ' + pos.waehrung + '</span>';
    box.appendChild(kopf);

    const lage = document.createElement('p');
    lage.className = 'hint';
    lage.textContent = pos.lage;
    box.appendChild(lage);

    if (pos.vorschlaege && pos.vorschlaege.length) {
      const tab = document.createElement('table');
      tab.className = 'grid';
      tab.innerHTML = '<thead><tr>'
        + '<th>Tauschen in</th><th>Kreuzung</th><th class="num">Tage</th>'
        + '<th class="num">Bestand seither</th><th class="num">Ziel seither</th>'
        + '<th class="num">Unterschied</th><th class="num">wäre geworden</th>'
        + '<th class="num">frühere</th><th class="num">Trefferquote</th>'
        + '<th class="num">Ø Gewinn</th><th>Rückhalt</th></tr></thead><tbody></tbody>';
      box.appendChild(tab);

      const tb = tab.querySelector('tbody');
      for (const v of pos.vorschlaege) {
        const h = v.historie || {};
        row(tb, [
          { html: '<b class="up">' + v.ziel.symbol + '</b><span class="dim"> ' + v.ziel.klasse + '</span>' },
          fmtDate(v.kreuzung, false),
          { text: v.tageSeither, cls: 'num' },
          { text: pct(v.renditeBestand), cls: 'num ' + (v.renditeBestand >= 0 ? 'up' : 'down') },
          { text: pct(v.renditeZiel), cls: 'num ' + (v.renditeZiel >= 0 ? 'up' : 'down') },
          { html: '<b>' + pct(v.paargewinnSeither) + '</b>',
            cls: 'num ' + (v.paargewinnSeither >= 0 ? 'up' : 'down') },
          { text: fmtNum(v.waereGeworden, 2),
            cls: 'num ' + (v.waereGeworden >= v.kapital ? 'up' : 'down') },
          { text: h.kreuzungen == null ? 0 : h.kreuzungen, cls: 'num dim' },
          { text: h.trefferquote == null ? '–' : pct(h.trefferquote, 0), cls: 'num' },
          { text: h.mittelgewinn == null ? '–' : pct(h.mittelgewinn), cls: 'num' },
          { html: v.bewaehrt
              ? '<span class="pill ok">Rückhalt</span>'
              : '<span class="pill off">ohne Rückhalt</span>' }
        ]);
      }
    }

    ziel.appendChild(box);
  }
}

$('#sw-load').onclick = loadTausch;
for (const id of ['#sw-interval', '#sw-tage', '#sw-halte', '#sw-je', '#sw-nurbew'])
  $(id).onchange = loadTausch;

/* Die Symbolliste als Vorschlag im Eingabefeld. Ohne sie tippt man „BTC"
   statt „BTC-USD" und bekommt eine Fehlermeldung statt einer Position. */
async function fillSymbolListe() {
  const list = await guard(() => api('/api/assets'));
  if (!list) return;
  const dl = $('#sw-symbole');
  dl.innerHTML = '';
  for (const a of list) {
    const o = document.createElement('option');
    o.value = a.symbol;
    o.label = a.name || '';
    dl.appendChild(o);
  }
}

/* =============================================================== Day Trading ===

   Die Seite ist bewusst so gebaut, dass sie zuerst die Hürde zeigt und dann
   erst die Bewegung. Umgekehrt gelesen wäre sie eine Einladung: „Sieh, wie viel
   sich bewegt!" -- und die Kosten stünden im Kleingedruckten.                 */

async function loadDayTrading() {
  const stunden = $('#dt-stunden').value;
  $('#dt-kern').textContent = 'wird gerechnet …';

  const d = await guard(() => api('/api/daytrading/heute?stunden=' + stunden));
  if (!d) { $('#dt-kern').textContent = 'Abfrage fehlgeschlagen.'; return; }

  $('#dt-kern').innerHTML = (d.kernaussage || '')
    .replace(/&/g, '&amp;').replace(/</g, '&lt;')
    .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');

  // ---------------------------------------------------------- Fenster ----
  const tf = clearTable('#dt-fenster');
  for (const f of d.fenster || []) {
    row(tf, [
      { html: '<b>' + f.klasse + '</b>' },
      f.juengsteBar ? fmtDate(f.juengsteBar, true) : '–',
      { text: f.stundenAlt + ' h', cls: 'num' },
      { html: (f.vermutlichOffen
          ? '<span class="pill ok">handelt</span> '
          : '<span class="pill off">ruht</span> ') + f.bemerkung }
    ]);
  }

  // ----------------------------------------------------- Beweglichkeit ---
  const tb = clearTable('#dt-beweg');
  for (const b of d.beweglichkeit || []) {
    row(tb, [
      { html: '<b>' + b.klasse + '</b>' },
      { text: fmtNum(b.bars, 0), cls: 'num dim' },
      { text: b.werte, cls: 'num dim' },
      { text: pct(b.mittlereBewegung, 2), cls: 'num' },
      { text: pct(b.medianBewegung, 2), cls: 'num' },
      /* Ab der Hälfte grün, darunter rot: Unter 50 Prozent ist die Mehrheit der
         Stunden von vornherein nicht handelbar. */
      { html: '<b>' + pct(b.anteilUeberKosten, 0) + '</b>',
        cls: 'num ' + (b.anteilUeberKosten >= 0.5 ? 'up' : 'down') },
      { text: pct(b.anteilUeberDoppeltenKosten, 0), cls: 'num dim' }
    ]);
  }

  // ------------------------------------------------------------- Güte ----
  const tg = clearTable('#dt-guete');
  for (const g of d.guete || []) {
    row(tg, [
      g.quelle,
      { text: g.horizontStunden ? g.horizontStunden + ' h' : '–', cls: 'num' },
      { text: g.trefferquote == null ? '–' : pct(g.trefferquote, 1), cls: 'num' },
      { html: (g.urteil || '').replace(/\*\*(.+?)\*\*/g, '<b>$1</b>') },
      { html: g.traegt
          ? '<span class="pill ok">trägt</span>'
          : '<span class="pill off">trägt nicht</span>' }
    ]);
  }

  // ------------------------------------------------------ Anweisungen ----
  const ol = $('#dt-anweisungen');
  ol.innerHTML = '';
  for (const a of d.anweisungen || []) {
    const li = document.createElement('li');
    li.innerHTML = a.replace(/&/g, '&amp;').replace(/</g, '&lt;')
                    .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>')
                    .replace(/`(.+?)`/g, '<code>$1</code>');
    li.style.marginBottom = '8px';
    ol.appendChild(li);
  }

  // ------------------------------------------------------- Bewegungen ----
  const tm = clearTable('#dt-bewegungen');
  if (!(d.bewegungen || []).length) {
    emptyRow(tm, 8, 'Keine Stundenbars mit sechs Vorgängern in den letzten 48 Stunden. '
      + 'Das heißt „keine Daten", nicht „keine Bewegung" — siehe die Tabelle „Was jetzt '
      + 'gehandelt wird" darüber.');
  } else {
    for (const b of d.bewegungen) {
      row(tm, [
        { html: '<b>' + b.symbol + '</b>' },
        b.name || '–',
        { text: b.klasse, cls: 'dim' },
        { text: fmtNum(b.kurs, 4), cls: 'num' },
        { text: pct(b.veraenderungStunde, 2),
          cls: 'num ' + (b.veraenderungStunde >= 0 ? 'up' : 'down') },
        { text: pct(b.veraenderungSechsStunden, 2),
          cls: 'num ' + (b.veraenderungSechsStunden >= 0 ? 'up' : 'down') },
        { text: pct(b.spannweite, 2), cls: 'num dim' },
        fmtDate(b.tsUtc, true)
      ]);
    }
  }

  // -------------------------------------------------------- Literatur ----
  const lit = $('#dt-literatur');
  lit.innerHTML = '';
  if (!(d.literatur || []).length) {
    const p = document.createElement('p');
    p.className = 'hint';
    p.textContent = 'Keine Fundstelle. Ist die Wissenssäule eingebettet und Qdrant erreichbar?';
    lit.appendChild(p);
  }
  for (const l of d.literatur || []) {
    const box = document.createElement('div');
    box.style.borderLeft = '2px solid var(--line)';
    box.style.padding = '2px 0 2px 12px';
    box.style.margin = '10px 0';

    const kopf = document.createElement('div');
    kopf.innerHTML = '<b>' + l.titel + '</b>'
      + '<span class="dim"> — Ähnlichkeit ' + fmtNum(l.score, 3)
      + (l.fundstelle ? ', ' + l.fundstelle : '') + '</span>';
    box.appendChild(kopf);

    const txt = document.createElement('p');
    txt.className = 'hint';
    txt.textContent = l.auszug;
    box.appendChild(txt);

    lit.appendChild(box);
  }
}

$('#dt-load').onclick = loadDayTrading;
$('#dt-stunden').onchange = loadDayTrading;

/* ================================================================= Langfrist ===

   Die einzige Ansicht, deren Grundgröße gemessen trägt. Trotzdem gilt hier
   dieselbe Reihenfolge wie überall: erst die Einschränkung, dann die Zahl.
   Konkret heißt das drei Dinge:

   1. Der Verzerrungshinweis steht OBEN, nicht unten. Das Universum ist die
      heutige Rangliste nach Marktkapitalisierung -- eine Rückrechnung darauf
      misst Gewinner.
   2. Der tiefste Einbruch steht neben der Rendite und nicht dahinter.
   3. Körbe ohne den Vermerk „Sperrbereich" sind Rückschau auf die eigene
      Auswahl.                                                                */

async function loadLangfrist() {
  const q = new URLSearchParams({
    jahre: $('#lf-jahre').value,
    minTage: $('#lf-mintage').value,
    limit: $('#lf-limit').value
  });

  $('#lf-kern').textContent = 'wird gerechnet …';

  const d = await guard(() => api('/api/langfrist/uebersicht?' + q));
  if (!d) { $('#lf-kern').textContent = 'Abfrage fehlgeschlagen.'; return; }

  const fett = t => (t || '').replace(/&/g, '&amp;').replace(/</g, '&lt;')
                             .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');

  $('#lf-kern').innerHTML = fett(d.kernaussage)
    + '<br><span class="dim">Zeitraum ' + fmtDate(d.vonUtc, false)
    + ' bis ' + fmtDate(d.bisUtc, false)
    + ', Sperrbereich ab ' + fmtDate(d.sperrbereichAbUtc, false) + '.</span>';

  $('#lf-verzerrung').innerHTML = fett(d.verzerrungshinweis);

  // -------------------------------------------------------------- Körbe ---
  const tk = clearTable('#lf-koerbe');
  const texte = $('#lf-korbtexte');
  texte.innerHTML = '';

  for (const k of d.koerbe || []) {
    /* Die Drift der engsten Bindung wird eingefärbt: Wächst sie, war die
       Streuung, auf die sich die Auswahl stützte, zum Messzeitpunkt weg. */
    const driftKlasse = k.korrelationsdrift > 0.05 ? 'down'
                      : k.korrelationsdrift < -0.05 ? 'up' : 'dim';

    row(tk, [
      { html: '<b>' + k.name + '</b>'
          + (k.imSperrbereich ? ' <span class="pill ok">Sperrbereich</span>' : '') },
      { html: '<b>' + pct(k.renditeProJahr, 1) + '</b>',
        cls: 'num ' + (k.renditeProJahr >= 0 ? 'up' : 'down') },
      { text: pct(k.schwankungProJahr, 1), cls: 'num dim' },
      { html: '<b>' + pct(k.groessterRueckgang, 1) + '</b>', cls: 'num down' },
      { text: fmtNum(k.renditeJeRueckgang, 2), cls: 'num' },
      { text: pct(k.anteilPositiverJahre, 0), cls: 'num dim' },
      { text: fmtNum(k.hoechsteKorrelation, 2),
        cls: 'num ' + (k.hoechsteKorrelation > 0.9 ? 'down' : '') },
      { text: fmtNum(k.hoechsteKorrelationBeiAuswahl, 2), cls: 'num ' + driftKlasse },
      { text: k.mitglieder.join(', '), cls: 'dim' }
    ]);

    const p = document.createElement('p');
    p.className = 'hint';
    p.innerHTML = '<b>' + k.name + '</b> — ' + k.begruendung;
    texte.appendChild(p);
  }

  // -------------------------------------------------------- Anweisungen ---
  const ol = $('#lf-anweisungen');
  ol.innerHTML = '';
  for (const a of d.anweisungen || []) {
    const li = document.createElement('li');
    li.innerHTML = fett(a);
    li.style.marginBottom = '8px';
    ol.appendChild(li);
  }

  // --------------------------------------------------------- Einzelwerte --
  langfristWerte = d.einzelwerte || [];
  zeichneLangfristWerte();
}

let langfristWerte = [];

function zeichneLangfristWerte() {
  const t = clearTable('#lf-werte');
  const wie = $('#lf-sort').value;

  const schluessel = {
    rendite: w => w.renditeProJahr,
    jeSchwankung: w => w.renditeJeSchwankung,
    jeRueckgang: w => w.renditeJeRueckgang,
    positiv: w => w.anteilPositiverJahre
  }[wie] || (w => w.renditeProJahr);

  const sortiert = [...langfristWerte].sort((a, b) => schluessel(b) - schluessel(a));

  if (!sortiert.length) { emptyRow(t, 10, 'Keine Reihe mit genug Historie.'); return; }

  for (const w of sortiert) {
    row(t, [
      { html: '<b>' + w.symbol + '</b>' },
      w.name || '–',
      { text: w.klasse, cls: 'dim' },
      { text: fmtNum(w.jahre, 1), cls: 'num dim' },
      { html: '<b>' + pct(w.renditeProJahr, 1) + '</b>',
        cls: 'num ' + (w.renditeProJahr >= 0 ? 'up' : 'down') },
      { text: pct(w.schwankungProJahr, 0), cls: 'num dim' },
      { text: pct(w.groessterRueckgang, 0), cls: 'num down' },
      { text: fmtNum(w.renditeJeRueckgang, 2), cls: 'num' },
      { text: pct(w.anteilPositiverJahre, 0),
        cls: 'num ' + (w.anteilPositiverJahre >= 0.8 ? 'up' : '') },
      { text: pct(w.schlechtestesJahr, 0),
        cls: 'num ' + (w.schlechtestesJahr >= 0 ? 'up' : 'down') }
    ]);
  }
}

$('#lf-load').onclick = loadLangfrist;
$('#lf-jahre').onchange = loadLangfrist;
$('#lf-mintage').onchange = loadLangfrist;
$('#lf-limit').onchange = loadLangfrist;
$('#lf-sort').onchange = zeichneLangfristWerte;

/* ================================================================= Bot-Herde ===

   Die Seite prüft eine Vermutung statt sie zu bestätigen. Zwei Dinge müssen
   deshalb sichtbar bleiben:

   1. Die Vergleichslatte. Ein Auslöser, nach dem der Kurs steigt, sagt nichts,
      solange er in einem Jahr auftritt, in dem alles steigt.
   2. Die Kostenschwelle. Von sieben Auslösern bei Aktien schlagen drei den
      Rundlauf. Die übrigen sind messbar und nicht handelbar -- und genau das
      ist der Unterschied, den eine solche Seite sonst verwischt.            */

async function loadHerde() {
  $('#he-kern').textContent = 'wird geladen …';

  const fett = t => (t || '').replace(/&/g, '&amp;').replace(/</g, '&lt;')
                             .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');

  const [a, u, z] = await Promise.all([
    guard(() => api('/api/herde/ausloeser')),
    guard(() => api('/api/herde/umkehr?limit=20')),
    guard(() => api('/api/herde/zeitzonen'))
  ]);

  /* ------------------------------------------------ Zeitzonen-Vorlauf --- */
  const tz = clearTable('#he-zeitzonen');

  if (!z || !z.vorhanden) {
    $('#he-zz-antwort').textContent = (z && z.hinweis) || 'Noch kein Lauf vorhanden.';
    emptyRow(tz, 11, 'Noch keine Auswertung — über „Zeitzonen neu rechnen" anstoßen.');
  } else {
    $('#he-zz-antwort').innerHTML = fett(z.antwort);

    for (const r of z.zeilen || []) {
      /* Die handelbare Spalte wird gegen ihre eigene Schwelle eingefärbt und
         nicht gegen null: 0,041 sieht nach etwas aus und liegt bei 2.566
         Beobachtungen genau auf der Grenze. */
      const ueber = Math.abs(r.handelbar) > r.schwelle;

      row(tz, [
        { html: '<b>' + r.frueher + '</b>' },
        { html: '<b>' + r.spaeter + '</b>' },
        { text: r.vorsprung + ' h', cls: 'num' },
        { text: r.ueberlappung + ' h',
          cls: 'num ' + (r.ueberlappung > 0 ? 'down' : 'dim') },
        { text: fmtNum(r.tage, 0), cls: 'num dim' },
        { text: fmtNum(r.naiv, 3), cls: 'num dim' },
        { html: '<b>' + fmtNum(r.sprung, 3) + '</b>', cls: 'num up' },
        { html: '<b>' + fmtNum(r.handelbar, 3) + '</b>',
          cls: 'num ' + (r.alsVorlaufLesbar && ueber ? 'up' : 'down') },
        { text: fmtNum(r.schwelle, 3), cls: 'num dim' },
        { text: fmtNum(r.gegenprobe, 3), cls: 'num' },
        { html: r.alsVorlaufLesbar
            ? '<span class="pill ok">ja</span>'
            : '<span class="pill off">nein — gemeinsame Handelszeit</span>' }
      ]);
    }
  }

  // ------------------------------------------------------- Auslöser -----
  const t = clearTable('#he-ausloeser');

  if (!a || !a.vorhanden) {
    $('#he-kern').textContent = (a && a.hinweis) || 'Noch kein Lauf vorhanden.';
    emptyRow(t, 10, 'Noch keine Auswertung — über „Auslöser neu rechnen" anstoßen.');
  } else {
    $('#he-kern').innerHTML = fett(a.kernaussage)
      + '<br><span class="dim">Stand ' + fmtDate(a.stand, true)
      + ', Zeitraum ab ' + fmtDate(a.seit, false) + '. ' + (a.kostenhinweis || '') + '</span>';

    for (const z of a.zeilen || []) {
      const urteil = z.ueberKosten
        ? (z.gegenlaeufig
            ? '<span class="pill ok">gegenläufig, über Kosten</span>'
            : '<span class="pill ok">über Kosten</span>')
        : (z.gegenlaeufig
            ? '<span class="pill off">gegenläufig, unter Kosten</span>'
            : '<span class="pill off">unter Kosten</span>');

      row(t, [
        { html: '<b>' + z.ausloeser + '</b>' },
        { text: z.klasse, cls: 'dim' },
        { text: fmtNum(z.ereignisse, 0), cls: 'num dim' },
        { text: z.werte, cls: 'num dim' },
        { text: pct(z.ueberschuss1, 3), cls: 'num ' + (z.ueberschuss1 >= 0 ? 'up' : 'down') },
        { text: pct(z.ueberschuss5, 3), cls: 'num ' + (z.ueberschuss5 >= 0 ? 'up' : 'down') },
        { html: '<b>' + pct(z.ueberschuss20, 3) + '</b>',
          cls: 'num ' + (z.ueberschuss20 >= 0 ? 'up' : 'down') },
        /* Unter 0,5 heißt: Das Signal sagt das Gegenteil dessen, was eintritt. */
        { text: fmtNum(z.richtung20, 2),
          cls: 'num ' + (z.richtung20 < 0.5 ? 'down' : 'up') },
        { text: fmtNum(z.volumenFaktor, 2) + '×',
          cls: 'num ' + (z.volumenFaktor > 1.2 ? 'up' : 'dim') },
        { html: urteil }
      ]);
    }
  }

  // --------------------------------------------------- Umkehrschluss ----
  const tu = clearTable('#he-umkehr');

  if (!u || !u.vorhanden) {
    $('#he-umkehr-antwort').textContent = (u && u.hinweis) || 'Noch kein Lauf vorhanden.';
    emptyRow(tu, 7, 'Noch keine Auswertung.');
    return;
  }

  $('#he-umkehr-antwort').innerHTML = fett(u.antwort);

  $('#he-umkehr-zahlen').textContent =
    `${u.pairsTested.toLocaleString('de-DE')} Paare mit mindestens ${u.minCrossings} Kreuzungen, `
    + `Horizont ${u.horizonDays} Kalendertage. Auffällig nach unten: ${u.sigNegative}, `
    + `nach oben: ${u.sigPositive}, allein durch Zufall erwartet: ${u.expectedByChance} je Seite.`;

  for (const e of u.extreme || []) {
    row(tu, [
      { html: '<b>' + e.symbolA + '</b> / <b>' + e.symbolB + '</b>'
          + '<span class="dim"> ' + e.klasseA + ' / ' + e.klasseB + '</span>' },
      { text: e.n, cls: 'num dim' },
      { text: fmtNum(e.hitRate, 3), cls: 'num down' },
      { text: fmtNum(e.z, 2), cls: 'num' },
      { text: pct(e.meanGain, 2), cls: 'num ' + (e.meanGain >= 0 ? 'up' : 'down') },
      { text: pct(e.invertiertNachKosten, 2),
        cls: 'num ' + (e.invertiertNachKosten > 0 ? 'up' : 'down') },
      { html: e.naheVerwandt
          ? '<span class="pill off">nahe verwandt — Paarhandel</span>'
          : '<span class="pill ok">eigenständig</span>' }
    ]);
  }
}

$('#he-load').onclick = loadHerde;

$('#he-rechnen').onclick = async () => {
  setStatus('Auslöser werden gerechnet — das dauert einige Minuten …', 'busy');
  await guard(() => api('/api/herde/ausloeser/rechnen?jahre=5', { method: 'POST' }),
              'Auslöser neu gerechnet');
  await loadHerde();
};

$('#he-zz-rechnen').onclick = async () => {
  setStatus('Zeitzonen werden gerechnet …', 'busy');
  await guard(() => api('/api/herde/zeitzonen/rechnen?jahre=10', { method: 'POST' }),
              'Zeitzonen neu gerechnet');
  await loadHerde();
};

$('#he-umkehr-rechnen').onclick = async () => {
  setStatus('Umkehrtest wird gerechnet — das dauert einige Minuten …', 'busy');
  await guard(() => api('/api/herde/umkehr/rechnen?tage=28&minKreuzungen=8', { method: 'POST' }),
              'Umkehrtest neu gerechnet');
  await loadHerde();
};

/* Rechnet die Schaetzungen nach, wenn seit der letzten ein Kurs dazugekommen ist.

   Nur dann: Ein Lauf ueber alle verfolgten Werte und sieben Horizonte kostet
   Sekunden, und ihn bei jedem Ansichtswechsel zu starten waere Verschwendung --
   die Antwort waere ja dieselbe.                                              */
async function frischeSchaetzung() {
  const st = await guard(() => api('/api/forecast/stand'));
  if (!st) return;

  if (!st.veraltet) return;

  setStatus('Neue Kurse eingetroffen — Schätzungen werden neu gerechnet …', 'busy');

  const r = await guard(() => api('/api/forecast/auffrischen', { method: 'POST' }));

  if (r?.gerechnet) {
    setStatus('Schätzungen neu gerechnet. Die bisherigen bleiben zum Vergleich stehen.');
    if (typeof loadForecast === 'function') loadForecast();
  }
}


/* ==================================================== Prognose gegen Ist ===

   Die Auswertung laeuft je Horizont getrennt, und das ist keine Formsache:
   Eine Tagesprognose reift nach einem Tag, eine Jahresprognose nach einem Jahr.
   In eine Liste geworfen vergliche man Fallzahlen von drei mit Fallzahlen von
   null -- und die leere Zeile saehe nach Datenverlust aus statt nach einer
   Prognose, die schlicht noch laeuft.                                        */

async function ladeGuete() {
  const von = $('#gu-von')?.value;
  const min = parseInt($('#gu-min')?.value, 10) || 2;
  const top = parseInt($('#gu-top')?.value, 10) || 10;

  const q = new URLSearchParams({ mindestens: min, proHorizont: top });
  if (von) q.set('von', von + 'T00:00:00Z');

  const d = await guard(() => api('/api/guete/uebersicht?' + q));
  if (!d) return;

  $('#gu-bilanz').innerHTML =
    '<p class="hint block"><b>' + esc(d.bilanz) + '</b></p>'
    + '<p class="hint block">' + esc(d.hinweis) + '</p>';

  await ladeLernkurve(von);

  const raus = $('#gu-out');
  raus.innerHTML = '';

  for (const h of d.horizonte) {
    const kopf = document.createElement('h4');

    kopf.innerHTML = esc(h.label)
      + (h.werte
          ? ` <span class="dim">${h.traegt} von ${h.werte} über dem Stillstand `
            + `(${h.anteilTraegt} %) · Median ${fmtNum(h.medianVerhaeltnis, 3)}</span>`
          : ' <span class="pill off">noch nichts abgelaufen</span>');

    raus.appendChild(kopf);

    if (h.hinweis) {
      const p = document.createElement('p');
      p.className = 'hint block';
      p.textContent = h.hinweis;
      raus.appendChild(p);
    }

    if (!h.beste.length) continue;

    const t = document.createElement('table');
    t.className = 'grid';

    t.innerHTML =
      '<thead><tr><th>Wert</th><th class="num">n</th><th class="num">Ø Fehler</th>'
      + '<th class="num">Stillstand</th><th class="num">Verhältnis</th>'
      + '<th class="num">Richtung</th><th></th></tr></thead><tbody>'
      + h.beste.map(x =>
          '<tr><td><b>' + esc(x.symbol) + '</b> <span class="dim">'
          + esc((x.name || '').slice(0, 34)) + '</span></td>'
          + '<td class="num">' + x.bewertet + '</td>'
          + '<td class="num">' + fmtNum(x.mittlererFehlerPct, 3) + ' %</td>'
          + '<td class="num dim">' + fmtNum(x.stillstandFehlerPct, 3) + ' %</td>'
          + '<td class="num"><b>' + fmtNum(x.fehlerverhaeltnis, 3) + '</b></td>'
          + '<td class="num">' + fmtNum(x.trefferquotePct, 0) + ' %</td>'
          + '<td>' + (x.traegt
              ? '<span class="pill ok">schlägt Stillstand</span>'
              : '<span class="pill off">darunter</span>') + '</td></tr>').join('')
      + '</tbody>';

    raus.appendChild(t);
  }
}

async function ladeLernkurve(von) {
  const q = new URLSearchParams({ horizont: 24 });
  if (von) q.set('von', von + 'T00:00:00Z');

  const d = await guard(() => api('/api/guete/lernkurve?' + q));
  const kasten = $('#gu-kurve');
  if (!d || !kasten) return;

  kasten.innerHTML =
    (d.tage.length
      ? '<table class="grid"><thead><tr><th>Zieltag</th><th class="num">Werte</th>'
        + '<th class="num">Median Verhältnis</th><th class="num">darunter</th>'
        + '<th class="num">Richtung</th></tr></thead><tbody>'
        + d.tage.map(t =>
            '<tr><td>' + fmtDate(t.tag, false) + '</td>'
            + '<td class="num">' + t.werte + '</td>'
            + '<td class="num"><b>' + fmtNum(t.medianVerhaeltnis, 3) + '</b></td>'
            + '<td class="num">' + t.traegt + ' <span class="dim">('
            + fmtNum(t.anteilTraegt, 1) + ' %)</span></td>'
            + '<td class="num">' + fmtNum(t.mittlereTrefferquotePct, 1) + ' %</td></tr>').join('')
        + '</tbody></table>'
      : '')
    + '<p class="hint block">' + esc(d.hinweis) + '</p>'
    /* Der Trend wird erst ab sieben Tagen ueberhaupt gerechnet -- eine Steigung
       aus drei Punkten waere Kaffeesatz, und sie stuende hier wie ein Befund. */
    + (d.trend
        ? '<p class="hint block">Erste Hälfte im Mittel <b>' + fmtNum(d.trend.erste, 3)
          + '</b>, zweite <b>' + fmtNum(d.trend.zweite, 3) + '</b> — '
          + (d.trend.zweite < d.trend.erste ? 'es wird besser.' : 'keine Verbesserung.')
          + '</p>'
        : '');
}

$('#gu-start')?.addEventListener('click', ladeGuete);


/* ================================================== Woraus sie entsteht ===

   Die Mischung sichtbar machen. Ohne diese Ansicht ist die kombinierte Zahl
   eine Behauptung: Man sieht ein Ergebnis und weiss nicht, ob es von einer
   Saeule mit Rueckhalt kommt oder von vieren ohne.                          */

async function zeigeMischung() {
  const sym = $('#sm-symbol').value.trim();
  const h = $('#sm-h').value;
  const raus = $('#sm-out');

  const d = await guard(() => api(
    `/api/saeulen/mischung/${encodeURIComponent(sym)}?horizont=${h}`));

  if (!d) { raus.innerHTML = ''; return; }

  const p = d.prognose;
  const m = JSON.parse(p.pillar_mix || '{}');
  const b = m.beitraege || [];

  const pct = v => fmtNum(v * 100, 3) + ' %';

  raus.innerHTML =
    '<dl class="kv">'
    + `<dt>Basiskurs</dt><dd>${fmtNum(p.base_close, 2)}</dd>`
    + `<dt>Säule 1 allein</dt><dd>${fmtNum(p.predicted_close, 2)} `
    + `<span class="dim">(${pct(p.predicted_return)})</span></dd>`
    + `<dt>alle Säulen</dt><dd><b>${fmtNum(p.combined_close, 2)}</b> `
    + `<span class="dim">(${pct(p.combined_return)})</span></dd>`
    + `<dt>gestellt</dt><dd class="dim">${fmtDate(p.made_at_utc, true)} für `
    + `${fmtDate(p.target_ts_utc, true)}</dd>`
    + '</dl>'

    + (m.basis !== undefined
        ? `<p class="hint block">Grundlage ${pct(m.basis)}, Aufschlag ${pct(m.aufschlag)}`
          + (m.gedeckelt ? ' <b>(gedeckelt)</b>' : '') + '.</p>'
        : '')

    + '<table class="grid"><thead><tr><th>Säule</th><th>Art</th>'
    + '<th class="num">sagt</th><th class="num">Anteil</th><th>warum</th></tr></thead><tbody>'
    + b.map(x =>
        '<tr><td><b>' + esc(x.saeule) + '</b></td>'
        + '<td>' + (x.art === 'Aufschlag'
            ? '<span class="pill off">Aufschlag</span>'
            : '<span class="pill ok">Grundlage</span>') + '</td>'
        + '<td class="num">' + pct(x.rendite) + '</td>'
        + '<td class="num">' + (x.anteil > 0
            ? '<b>' + fmtNum(x.anteil * 100, 1) + ' %</b>'
            : '<span class="dim">0</span>') + '</td>'
        + '<td class="dim">' + esc(x.warum) + '</td></tr>').join('')
    + '</tbody></table>';
}

async function zeigeSkill() {
  const d = await guard(() => api('/api/saeulen/skill'));
  if (!d) return;

  const z = d.gemessen || [];

  $('#sm-out').innerHTML =
    '<p class="hint block">' + esc(d.hinweis) + '</p>'
    + (z.length
        ? '<table class="grid"><thead><tr><th>Säule</th><th class="num">Horizont</th>'
          + '<th class="num">Rückhalt</th><th class="num">Fälle</th><th>Befund</th>'
          + '</tr></thead><tbody>'
          + z.map(x =>
              '<tr><td><b>' + esc(x.pillar) + '</b></td>'
              + '<td class="num">' + x.horizon_hours + ' h</td>'
              + '<td class="num">' + (x.skill > 0
                  ? '<b>' + fmtNum(x.skill, 3) + '</b>'
                  : '<span class="pill off">0</span>') + '</td>'
              + '<td class="num">' + x.n_obs + '</td>'
              + '<td class="dim">' + esc((x.detail || '').slice(0, 140)) + '</td></tr>').join('')
          + '</tbody></table>'
        : '<p class="hint block">Noch nichts gemessen. Die Semantik-Säule wird über '
          + '<code>POST /api/saeulen/kalibrieren</code> geprüft.</p>');
}

async function zeigeMischvergleich() {
  const d = await guard(() => api('/api/guete/mischvergleich'));
  if (!d) return;

  const h = d.horizonte || [];

  $('#sm-out').innerHTML =
    '<p class="hint block">' + esc(d.hinweis) + '</p>'
    + (h.length
        ? '<table class="grid"><thead><tr><th class="num">Horizont</th>'
          + '<th class="num">verglichen</th><th class="num">Fehler Säule 1</th>'
          + '<th class="num">Fehler Mischung</th><th class="num">Mischung besser</th>'
          + '<th>Urteil</th></tr></thead><tbody>'
          + h.map(x =>
              '<tr><td class="num">' + x.horizonHours + ' h</td>'
              + '<td class="num">' + x.verglichen + '</td>'
              + '<td class="num">' + fmtNum(x.fehlerSaeule1Pct, 3) + ' %</td>'
              + '<td class="num">' + fmtNum(x.fehlerMischungPct, 3) + ' %</td>'
              + '<td class="num">' + x.mischungBesser + ' <span class="dim">('
              + fmtNum(x.anteilBesser, 1) + ' %)</span></td>'
              + '<td>' + esc(x.urteil) + '</td></tr>').join('')
          + '</tbody></table>'
        : '');
}

$('#sm-zeigen')?.addEventListener('click', zeigeMischung);
$('#sm-skill')?.addEventListener('click', zeigeSkill);
$('#sm-vergleich')?.addEventListener('click', zeigeMischvergleich);


/* ============================================================== Taktleiste ===

   Zeigt durchgehend, was gerade laeuft und was als naechstes kommt.

   Warum eine eigene Abfrage und nicht `api()`: Diese Leiste fragt alle fuenf
   Sekunden. Ueber `api()` setzte jede Abfrage „laedt …" in die Statuszeile und
   loeschte sie danach -- die Statuszeile flackerte dann dauerhaft, und eine
   echte Meldung waere darin untergegangen. Ausserdem darf ein Aussetzer hier
   nichts melden: Dass der Takt fuer fuenf Sekunden nicht erreichbar war, ist
   keine Nachricht.                                                           */

function taktDauer(sekunden) {
  if (sekunden == null) return '';
  if (sekunden < 60) return sekunden + ' s';
  if (sekunden < 3600) return Math.floor(sekunden / 60) + ' min ' + (sekunden % 60) + ' s';

  const h = Math.floor(sekunden / 3600);
  return h + ' h ' + Math.floor((sekunden % 3600) / 60) + ' min';
}

async function taktAktualisieren() {
  const leiste = $('#taktleiste');
  if (!leiste || !wer) return;

  let d;
  try {
    const r = await fetch('/api/scheduler');
    if (!r.ok) return;
    d = await r.json();
  } catch { return; }   // Aussetzer sind keine Nachricht.

  leiste.hidden = false;

  const punkt = $('#takt-punkt');
  const was = $('#takt-was');
  const dauer = $('#takt-dauer');
  const naechster = $('#takt-naechster');

  if (d.laufend) {
    punkt.className = 'takt-punkt laeuft';

    /* Der Einzelschritt ist die nuetzlichere Auskunft: „Stundenlauf" steht zehn
       Minuten lang da, „update:1h" wechselt und zeigt damit, dass es vorangeht. */
    was.innerHTML = '<b>' + esc(d.laufend.job || 'Lauf') + '</b>'
      + (d.laufend.schritt ? ' <span class="dim">→ ' + esc(d.laufend.schritt) + '</span>' : '');

    dauer.textContent = 'seit ' + taktDauer(d.laufend.sekunden ?? d.laufend.schrittSekunden);
  } else if (!d.enabled) {
    punkt.className = 'takt-punkt aus';
    was.innerHTML = '<b>Zeitplan angehalten</b>';
    dauer.textContent = d.skippedWhileDisabled
      ? d.skippedWhileDisabled + ' Termine übersprungen' : '';
  } else {
    punkt.className = 'takt-punkt';
    was.textContent = 'bereit';
    dauer.textContent = d.lastJob
      ? 'zuletzt ' + d.lastJob + (d.lastResult ? ' — ' + d.lastResult : '')
      : '';
  }

  naechster.textContent = d.naechster
    ? 'nächster: ' + d.naechster.name + ' in ' + taktDauer(d.naechster.inSekunden)
      + ' (' + fmtDate(d.naechster.wannUtc, true) + ')'
    : 'kein Termin geplant';
}

/* Die Kopfhoehe messen, statt sie zu kennen.

   Die Kopfzeile darf umbrechen; ihre Hoehe haengt damit an der Fensterbreite und
   daran, welche Bedienelemente die aktuelle Ansicht zeigt. Ein fester Wert im CSS
   war schon einmal falsch und versteckte die Leiste vollstaendig hinter der
   Kopfzeile -- sichtbar, mit Text, und trotzdem unsichtbar.                    */
function kopfhoeheMessen() {
  const kopf = document.querySelector('header.topbar');
  if (!kopf) return;

  document.documentElement.style.setProperty(
    '--kopfhoehe', Math.round(kopf.getBoundingClientRect().height) + 'px');
}

kopfhoeheMessen();
window.addEventListener('resize', kopfhoeheMessen);

/* Auch ein Ansichtswechsel aendert sie: Der Ansichtsumschalter und die
   Auto-Suche erscheinen nur bei den Kursen und lassen die Zeile umbrechen. */
new ResizeObserver(kopfhoeheMessen).observe(document.querySelector('header.topbar'));

/* Fuenf Sekunden: Ein Lauf dauert Minuten, haeufiger zu fragen zeigt dieselbe
   Zahl noch einmal. Seltener fuehlt sich die Leiste tot an, waehrend gearbeitet
   wird -- und genau dagegen ist sie da. */
setInterval(taktAktualisieren, 5000);
taktAktualisieren();


/* ============================================================== Investings ===

   Ein virtuelles Depot: Man traegt ein, mit wieviel man heute in einen Wert
   ginge, und sieht fortan, was daraus geworden waere.

   DAS FELD TRAEGT DEN SOLL-STAND, NICHT DIE VERAENDERUNG. Wer 1.000 stehen hat
   und 1.500 eintraegt, legt 500 nach; wer 0 eintraegt, loest auf. Ein Feld
   „wieviel dazu" verlangte vom Nutzer die Subtraktion, die die Anwendung selbst
   machen kann -- und jeder versehentliche zweite Klick waere eine echte zweite
   Buchung. Deshalb ist das Feld mit dem heutigen Stand vorbelegt: Bestaetigen
   ohne Aenderung bucht nichts.

   WAS DIE SEITE NICHT RECHNET: Gebuehren, Schlupf, Steuer und die Bewegung des
   Wechselkurses. Die ersten drei gehoeren zu den Handelsseiten, wo sie
   vollstaendig gerechnet werden; die vierte fehlt, weil dieses System keine
   Devisenreihen fuehrt. Beides steht auf der Seite und nicht im
   Kleingedruckten.                                                          */

/* `zielWaehrung` ist die Zaehleinheit des Gesamtbandes und steht auf USD:
   Von 594 verfolgten Werten notieren 331 in USD, die Kryptoseite ohnehin, und
   der Autopilot rechnet darin. Eine EUR-Vorgabe zeigte fuer ein reines
   USD-Depot eine umgerechnete Zahl, wo eine ungerechnete moeglich ist -- und
   jede Umrechnung ist eine Annahme mehr. `waehrung` bleibt EUR: Das ist die
   Vorbelegung fuer NEUE Positionen im manuellen Depot, eine andere Frage. */
const investState = { waehrung: 'EUR', zielWaehrung: 'USD', offen: new Set() };

async function drawInvest(token) {
  const ids = chartState.selected.map(a => a.assetId).join(',');
  const d = await guard(() => api('/api/invest/?werte=' + encodeURIComponent(ids)));

  if (!d || isStale(token)) return;

  $('#charts-empty').style.display = 'none';

  const host = $('#invest');

  host.innerHTML =
      '<div id="gesamt-band"></div>'
    + '<h3 class="invest-h">Mein Depot</h3>'
    + investSummen(d) + investTabelle(d)
    + '<div id="invest-chart"></div>'
    + '<h3 class="invest-h">Autopilot</h3>'
    + '<div id="auto-out"></div>';

  investVerdrahten();

  /* Das Band zuerst: Es beantwortet die Frage, mit der man auf die Seite
     kommt („wieviel habe ich?"), und soll nicht auf drei Nachladungen warten.
     Die drei Abfragen laufen nebeneinander -- nacheinander addierten sich
     ihre Wartezeiten, obwohl keine von der anderen abhaengt. */
  await Promise.all([
    gesamtZeichnen(token),
    investVerlauf(token),
    autopilotZeichnen(token)
  ]);
}

/* ========================================================= Gesamtvermoegen ===

   Alles zusammen -- mein Depot UND die Strategien des Autopiloten, EUR und USD
   ueber EURUSD=X in eine Zaehleinheit gebracht.

   Warum ganz oben: Wer auf diese Seite kommt, will als Erstes wissen, wieviel
   er hat. Diese Zahl aus vier Tabellen zusammenzusuchen ist genau die Arbeit,
   die eine Uebersicht abnehmen soll.                                        */
async function gesamtZeichnen(token) {
  const w = investState.zielWaehrung || 'EUR';
  const d = await guard(() => api('/api/invest/gesamt?waehrung=' + w));

  if (!d || isStale(token)) return;

  const host = $('#gesamt-band');
  if (!host) return;

  const vz = d.gewinn >= 0 ? 'plus' : 'minus';

  const waehl = ['EUR', 'USD'].map(x =>
    '<option value="' + x + '"' + (x === w ? ' selected' : '') + '>' + x + '</option>').join('');

  host.innerHTML =
    '<div class="gesamt">'
    + '<div class="gesamt-zahl">'
    + '<div class="w">Gesamtvermögen · alle Depots'
    + ' <select id="gesamt-waehrung">' + waehl + '</select></div>'
    + '<div class="zahl">' + fmtNum(d.vermoegen, 2) + '</div>'
    + '<div class="zeile invest-gewinn ' + vz + '">'
    + (d.gewinn >= 0 ? '+' : '') + fmtNum(d.gewinn, 2)
    + (d.renditePct == null ? ''
       : ' (' + (d.renditePct >= 0 ? '+' : '') + fmtNum(d.renditePct, 2) + ' %)')
    + '<span class="dim"> · eingezahlt ' + fmtNum(d.eingezahlt, 2) + '</span></div>'
    + '</div>'
    + '<div class="gesamt-depots">'
    + (d.depots || []).map(x =>
        '<div class="gd' + (x.zaehlt ? '' : ' nichtgezaehlt') + '"'
        + (x.zaehlt ? '' : ' title="Vergleichslauf — steht nicht in der Summe. Die drei '
            + 'Strategien sind Gegenrechnungen auf dasselbe Geld."') + '>'
        + '<b>' + esc(depotName(x.depot)) + (x.zaehlt ? '' : ' <i>(Vergleich)</i>') + '</b>'
        + '<span>' + fmtNum(x.vermoegen, 2) + '</span>'
        + '<span class="dim invest-gewinn ' + (x.gewinn >= 0 ? 'plus' : 'minus') + '">'
        + (x.gewinn >= 0 ? '+' : '') + fmtNum(x.gewinn, 2) + '</span></div>').join('')
    + '</div></div>'
    + '<div id="gesamt-chart"></div>'
    + '<div id="depot-chart"></div>'
    + '<p class="hint block">' + esc(d.hinweis)
    + (d.kurs ? ' Kurs ' + fmtNum(d.kurs, 4) + ' vom ' + fmtDate(d.kursUtc) + '.' : '')
    + '</p>';

  $('#gesamt-waehrung')?.addEventListener('change', async e => {
    investState.zielWaehrung = e.target.value;
    // Zeichnet beide Kurven neu -- der Depotvergleich haengt an derselben Einheit.
    await gesamtZeichnen(++drawToken);
  });

  gesamtKurve(d);
  depotKurve();
}

/* ============================================== Entwicklung je Depot ========

   Eine Linie je Depot statt einer Summe. Genau deshalb duerfen die
   Vergleichslaeufe hier mit: Was in einer Summe eine Doppelzaehlung waere --
   dreimal dasselbe Geld --, ist als eigene Linie die eigentliche Aussage.

   ZWEI ANSICHTEN, WEIL ES ZWEI FRAGEN SIND. „Betraege" beantwortet „wieviel
   habe ich wo"; „indexiert" beantwortet „welche Entscheidung war die bessere".
   In Betraegen sieht ein Depot mit 20.000 immer besser aus als eines mit
   1.000, auch wenn es schlechter laeuft -- solange beide gleich gross sind,
   fuehrt das nicht in die Irre, aber verlassen darf man sich darauf nicht.  */
async function depotKurve() {
  const host = $('#depot-chart');
  if (!host) return;

  const w = investState.zielWaehrung || 'USD';
  const d = await guard(() => api('/api/invest/depotverlauf?waehrung=' + w));

  if (!d) return;

  const reihen = (d.reihen || []).filter(r => r.zeiten.length > 0);

  if (!reihen.length) { host.innerHTML = ''; return; }

  /* Eine gemeinsame Zeitachse. Die Depots starten zu verschiedenen Tagen; wer
     jede Reihe auf ihrer eigenen Achse zeichnete, bekaeme Linien, die
     nebeneinander statt uebereinander liegen. */
  const alle = [...new Set(reihen.flatMap(r => r.zeiten))].sort();
  const xs = alle.map(t => new Date(t).getTime() / 1000);

  /* Ein einzelner Punkt ist kein Diagramm.

     uPlot kann aus einem x-Wert keinen Bereich ableiten und spannt die Achse
     dann ueber Jahre -- gemessen: Oct 2026 bis Apr 2029 fuer einen Punkt von
     heute. Das sieht nach kaputten Daten aus und ist bloss ein fehlender
     zweiter Tag. */
  if (alle.length < 2) {
    host.innerHTML = '<p class="hint block">Für eine Linie braucht es einen zweiten '
      + 'Zeitpunkt — der kommt mit dem nächsten Stundenlauf.</p>';
    return;
  }

  const indexiert = investState.depotIndex !== false;

  const spalten = reihen.map(r => {
    const nach = new Map(r.zeiten.map((t, i) =>
      [t, indexiert ? r.indexiert[i] : r.werte[i]]));

    /* Vor dem ersten Tag eines Depots steht `null`, nicht null-Komma-null:
       Eine Linie, die bei 0 beginnt und dann springt, behauptet einen Verlust,
       den es nie gab. `spanGaps` bleibt deshalb aus. */
    return alle.map(t => (nach.has(t) ? nach.get(t) : null));
  });

  host.innerHTML = '';

  const box = document.createElement('div');
  box.className = 'chart-box';

  const kopf = document.createElement('div');
  kopf.className = 'chart-title';

  kopf.innerHTML = 'Entwicklung je Depot'
    + '<span class="sub">' + (indexiert ? 'indexiert (Start = 100)' : 'Beträge in ' + esc(w))
    + '</span>';

  const schalter = document.createElement('div');
  schalter.className = 'depot-schalter';

  schalter.innerHTML =
    '<button data-idx="1"' + (indexiert ? ' class="active"' : '') + '>indexiert</button>'
    + '<button data-idx="0"' + (indexiert ? '' : ' class="active"') + '>Beträge</button>';

  kopf.appendChild(schalter);
  box.appendChild(kopf);

  const plotHost = document.createElement('div');
  box.appendChild(plotHost);
  host.appendChild(box);

  schalter.addEventListener('click', e => {
    const b = e.target.closest('button[data-idx]');
    if (!b) return;
    investState.depotIndex = b.dataset.idx === '1';
    depotKurve();
  });

  /* Wer zaehlt, bekommt eine kraeftige Linie; die Vergleichslaeufe eine
     duenne. Der Blick soll zuerst auf das fallen, was wirklich im Vermoegen
     steht. */
  const farben = { manuell: '#4c9aff', aktiv: '#35c46b',
                   streng: '#c8ad5f', halten: '#8b97a6', invers: '#d0736a' };

  const reihenOpt = reihen.map(r => ({
    label: depotName(r.depot) + (r.zaehlt ? '' : ' (Vgl.)'),
    stroke: farben[r.depot] || '#8b97a6',
    width: r.zaehlt ? 2.2 : 1.2,
    dash: r.zaehlt ? undefined : [5, 3],
    spanGaps: false,
    value: (u, v) => (v == null ? '–' : fmtNum(v, 2) + (indexiert ? '' : ' ' + w))
  }));

  const plot = new uPlot({
    width: plotWidth(box),
    height: 280,
    series: [{ label: 'Zeit', value: '{YYYY}-{MM}-{DD} {HH}:{mm}' }, ...reihenOpt],
    axes: [
      { stroke: '#8b97a6', grid: { stroke: '#2a323d', width: 1 },
        ticks: { stroke: '#2a323d' }, font: AXIS_FONT },
      { stroke: '#8b97a6', grid: { stroke: '#2a323d', width: 1 },
        ticks: { stroke: '#2a323d' }, font: AXIS_FONT,
        values: (u, t) => t.map(v => fmtNum(v, indexiert ? 1 : 0)), size: 76 }
    ],
    legend: { show: true },
    cursor: { drag: { x: false, y: false } },
    plugins: [zoomPanPlugin()]
  }, [xs, ...spalten], plotHost);

  chartState.plots.push({ plot, box });

}

function depotName(d) {
  return { manuell: 'Mein Depot', streng: 'Streng', aktiv: 'Aktiv',
           halten: 'Grundlinie', invers: 'Invers' }[d] || d;
}

function gesamtKurve(d) {
  const host = $('#gesamt-chart');
  if (!host) return;

  const punkte = d.punkte || [];

  if (punkte.length < 2) {
    host.innerHTML = '<p class="hint block">Für eine Linie braucht es einen zweiten '
      + 'Zeitpunkt — der kommt mit dem nächsten Stundenlauf.</p>';
    return;
  }

  host.innerHTML = '';

  const box = document.createElement('div');
  box.className = 'chart-box';

  const kopf = document.createElement('div');
  kopf.className = 'chart-title';
  kopf.innerHTML = 'Verlauf des Gesamtvermögens'
    + '<span class="sub">alle Depots, in ' + esc(d.waehrung) + '</span>';
  box.appendChild(kopf);

  const plotHost = document.createElement('div');
  box.appendChild(plotHost);
  host.appendChild(box);

  const xs = punkte.map(x => new Date(x.zeit).getTime() / 1000);

  const plot = new uPlot({
    width: plotWidth(box),
    height: 260,
    series: [
      /* Mit Uhrzeit: Die Reihe ist in den juengsten Tagen stuendlich
         aufgeloest, und ein Zeiger, der nur das Datum nennt, zeigt dann
         zwoelfmal denselben Wert an. */
      { label: 'Zeit', value: '{YYYY}-{MM}-{DD} {HH}:{mm}' },
      { label: 'Vermögen', stroke: '#4c9aff', width: 2, spanGaps: true,
        value: (u, v) => (v == null ? '–' : fmtNum(v, 2)) },
      { label: 'eingezahlt', stroke: '#c8ad5f', width: 1.2, dash: [4, 4], spanGaps: true,
        value: (u, v) => (v == null ? '–' : fmtNum(v, 2)) }
    ],
    axes: [
      { stroke: '#8b97a6', grid: { stroke: '#2a323d', width: 1 },
        ticks: { stroke: '#2a323d' }, font: AXIS_FONT },
      { stroke: '#8b97a6', grid: { stroke: '#2a323d', width: 1 },
        ticks: { stroke: '#2a323d' }, font: AXIS_FONT,
        values: (u, t) => t.map(v => fmtNum(v, 0)), size: 76 }
    ],
    legend: { show: true },
    cursor: { drag: { x: false, y: false } },
    plugins: [zoomPanPlugin()]
  }, [xs, punkte.map(x => x.vermoegen), punkte.map(x => x.eingezahlt)], plotHost);

  chartState.plots.push({ plot, box });
}

/* ============================================================== Autopilot ===

   Drei Strategien nebeneinander, dazu das Protokoll ihrer Beschluesse.

   DIE ZAHL, DIE OBEN STEHEN MUSS: Die gemessene Richtungstrefferquote der
   Live-Prognosen liegt bei 0,474. Noetig waeren bei 1,55 % Tagesbewegung und
   0,3 % Rundlauf 59,7 %. Wer das nicht liest, bevor er ein Startkapital
   eintraegt, haelt spaeter einen erwarteten Verlust fuer Pech.               */
async function autopilotZeichnen(token) {
  const d = await guard(() => api('/api/autopilot/'));
  if (!d || isStale(token)) return;

  const host = $('#auto-out');
  if (!host) return;

  const takte = t => (d.takte || []).map(x =>
    '<option value="' + x.code + '"' + (x.code === t ? ' selected' : '') + '>'
    + esc(x.name) + '</option>').join('');

  /* Streng, aktiv, Grundlinie -- in dieser Reihenfolge liest sich die Leiste
     als Steigerung vom strengsten zum lockersten Massstab. Alphabetisch
     stuende die Grundlinie in der Mitte, und der Vergleich, um den es geht,
     waere auseinandergerissen. */
  const ordnung = { streng: 0, aktiv: 1, halten: 2 };

  /* Ein Bedienelement, das nichts bewirkt, gehoert abgeschaltet und nicht
     bloss unbeachtet gelassen. Die Grundlinie kauft einmal und ruehrt sich nie
     wieder -- Umschichtungstakt, Hysterese und Veto haben fuer sie keine
     Bedeutung, und wer daran dreht, wartet sonst vergeblich auf eine Wirkung. */
  const tot = (depot, feld) =>
    (depot === 'halten' && ['takt', 'hysterese', 'nemotron'].includes(feld))

    /*  `invers` bekommt kein Nemotron-Veto: Es pruefte, ob ein Kauf plausibel
        ist, und lehnte damit genau das ab, was dieses Depot absichtlich tut.
        Das Kaestchen stand trotzdem da und liess sich anhaken -- ein
        Bedienelement, das nichts bewirkt, ist schlimmer als keines.         */
    || (depot === 'invers' && feld === 'nemotron');

  const feld = (depot, name, inhalt, titel) =>
    '<td class="num">' + (tot(depot, name)
      ? '<span class="dim" title="' + esc(titel) + '">–</span>'
      : inhalt) + '</td>';

  const zeilen = (d.einstellungen || [])
    .slice()
    .sort((a, b) => (ordnung[a.depot] ?? 9) - (ordnung[b.depot] ?? 9))
    .map(e =>
      '<tr class="auto-zeile" data-depot="' + esc(e.depot) + '">'

      + '<td><label class="an"><input type="checkbox" class="auto-an"'
      + (e.aktiv ? ' checked' : '') + '> <b>' + esc(depotName(e.depot)) + '</b></label>'
      + '<br><span class="dim">' + esc(autoWas(e.depot)) + '</span></td>'

      + '<td class="num"><input type="radio" name="auto-zaehlt" class="auto-zaehlt"'
      + (e.zaehlt ? ' checked' : '') + '></td>'

      + '<td class="num"><input class="auto-budget" type="number" min="0" step="100" '
      + 'value="' + Number(e.startkapital).toFixed(0) + '"> '
      + '<select class="auto-waehrung">'
      + ['USD', 'EUR'].map(w => '<option value="' + w + '"'
          + (w === e.waehrung ? ' selected' : '') + '>' + w + '</option>').join('')
      + '</select></td>'

      + '<td class="num"><input class="auto-werte" type="number" min="1" max="30" value="'
      + e.werte + '"></td>'

      + feld(e.depot, 'takt',
          '<select class="auto-takt">' + takte(e.takt) + '</select>',
          'Die Grundlinie schichtet nie um.')

      + '<td class="num"><input class="auto-max" type="number" min="2" max="100" step="1" '
      + 'value="' + Math.round(e.maxAnteil * 100) + '"> %</td>'

      + feld(e.depot, 'hysterese',
          '<input class="auto-hyst" type="number" min="0" max="20" step="0.05" value="'
          + (e.hysterese * 100).toFixed(2) + '"> %',
          'Die Grundlinie tauscht nie, also gibt es nichts zu bremsen.')

      + feld(e.depot, 'nemotron',
          '<input type="checkbox" class="auto-nemo"' + (e.nemotron ? ' checked' : '') + '>',
          'Die Grundlinie bekommt kein Veto — sie soll die Auswahl nicht verändern.')

      + '<td class="auto-tasten"><button class="auto-speichern">Übernehmen</button>'
      + '<button class="auto-lauf" title="Übergeht den eingestellten Takt">Jetzt laufen</button>'
      + '<button class="auto-reset" title="Buchungen, Kasse, Läufe und Beschlüsse '
      + 'löschen und das Startbudget aufs Konto legen">Zurücksetzen</button>'
      + '</td></tr>').join('');

  const einstellTabelle =
    '<table class="grid auto-einst"><thead><tr>'
    + '<th>Strategie</th>'
    + '<th class="num" title="Welche Strategie ins Gesamtvermögen eingeht. Genau eine — '
    + 'die drei sind Gegenrechnungen auf dasselbe Geld, keine drei Geldtöpfe. Alle zu '
    + 'summieren zählte dasselbe Geld dreimal.">zählt</th>'
    + '<th class="num" title="Der Betrag, auf den ein Zurücksetzen zurückführt. '
    + 'Er steht in den Einstellungen und nicht auf dem Konto: Der Kontostand ändert '
    + 'sich mit jedem Kauf, das Budget bleibt die Zahl, gegen die das Ergebnis zu '
    + 'halten ist.">Budget</th>'
    + '<th class="num" title="Wieviele Werte das Depot gleichzeitig hält">Werte</th>'
    + '<th class="num" title="Wie oft umgeschichtet werden darf. Bewertet und '
    + 'protokolliert wird trotzdem jeden Tag.">Umschichtung</th>'
    + '<th class="num" title="Höchstanteil eines einzelnen Wertes am Depotvermögen">'
    + 'Höchstanteil</th>'
    + '<th class="num" title="Wieviel Vorsprung ein Anwärter braucht, um einen '
    + 'gehaltenen Wert zu verdrängen. Ohne diese Sperre tauscht ein Rangwechsel um '
    + 'einen Platz täglich hin und her und zahlt jedes Mal den Rundlauf von 0,3 %.">'
    + 'Hysterese</th>'
    + '<th class="num" title="Nemotron darf ein Geschäft ablehnen und begründet es. '
    + 'Die Zahlen bestimmen Auswahl und Grösse — nicht das Sprachmodell.">Veto</th>'
    + '<th></th></tr></thead><tbody>' + zeilen + '</tbody></table>';

  const v = d.vergleich || {};

  host.innerHTML =
    '<p class="hint block"><b>Bevor Sie Kapital eintragen.</b> Die gemessene '
    + 'Richtungstrefferquote der Live-Prognosen liegt bei <b>0,474</b>. Damit ein '
    + 'Geschäft die Gebühren deckt, wären bei 1,55 % Tagesbewegung und 0,3 % Rundlauf '
    + '<b>0,597</b> nötig. Der Autopilot ist deshalb gegen eine Grundlinie gebaut: '
    + '„Streng" handelt nur mit Nachweis und wird meist nichts tun, „Aktiv" folgt der '
    + 'Erwartung des Modells auch ohne Nachweis, „Grundlinie" kauft einmal und rührt '
    + 'sich nie wieder. Erst der Abstand zwischen ihnen ist eine Aussage.</p>'
    + einstellTabelle
    + (v.zeilen && v.zeilen.length ? vergleichTabelle(v) : '')
    + '<div class="toolbar"><button class="journal-auf" data-depot="autopilot">'
    + 'Journal — alle Vorgänge</button>'
    + '<button id="auto-rang">Rangfolge ansehen (ohne Handel)</button>'
    + '<button id="auto-reset-alle">Alle zurücksetzen</button>'
    + '<span class="hint">Ein Vergleich ist nur eine Aussage, wenn alle vom selben '
    + 'Punkt starten.</span></div>'
    + '<div id="auto-depots"></div>'
    + laeufeTabelle(d.laeufe || [])
    + '<div id="auto-detail"></div>';

  autopilotVerdrahten();
  journalVerdrahten();
  await autoDepotsZeichnen(token);
}

/* ================================================= Was der Autopilot haelt ===

   Ein eigenes Raster je Strategie, getrennt vom manuellen Depot.

   WARUM GETRENNT UND NICHT IN EINER TABELLE MIT SPALTE „Depot": Die beiden
   Listen beantworten verschiedene Fragen. Oben steht, was ICH eingetragen habe
   -- dort gehoert ein Eingabefeld hin. Hier steht, was das System selbst
   ausgesucht hat; ein Eingabefeld waere dort falsch, denn beim naechsten Lauf
   ueberschreibt der Autopilot jede Eingabe wieder. Zusammengelegt saehe man
   Zeilen, von denen die einen bearbeitbar waeren und die anderen nicht, ohne
   dass die Tabelle sagt, warum.

   Die Zeilen sind deshalb NUR LESBAR -- bis auf das Journal, das zeigt, wann
   und zu welchem Kurs der Autopilot gekauft hat.                             */
async function autoDepotsZeichnen(token) {
  const host = $('#auto-depots');
  if (!host) return;

  const depots = ['streng', 'aktiv', 'halten'];

  /* Welche Strategie zaehlt -- fuer die Kennzeichnung im Kopf jeder Tabelle.
     Aus den Einstellungen, nicht geraten. */
  const einst = await guard(() => api('/api/autopilot/')).catch(() => null);

  investState.leit = (einst?.einstellungen || []).find(e => e.zaehlt)?.depot || 'aktiv';

  const daten = await Promise.all(depots.map(x =>
    guard(() => api('/api/invest/?depot=' + x)).catch(() => null)));

  if (isStale(token)) return;

  const stuecke = [];

  depots.forEach((depot, i) => {
    const d = daten[i];
    if (!d) return;

    const pos = (d.positionen || []).filter(x => x.buchungen > 0);
    const block = (d.waehrungen || [])[0];

    /* Ein Depot ohne Kapital UND ohne Position hat nichts zu zeigen. Eine
       leere Tabelle mit Ueberschrift sieht aus wie ein Fehler. */
    if (!pos.length && !block) return;

    stuecke.push(autoDepotBlock(depot, pos, block));
  });

  host.innerHTML = stuecke.length
    ? stuecke.join('')
    : '<p class="hint block">Noch kein Kapital in den Strategien. Sobald ein '
      + 'Startbetrag gesetzt und eine Strategie eingeschaltet ist, stehen hier die '
      + 'Werte, die das System selbst ausgesucht hat.</p>';

  /* Journal auch hier -- und mit dem richtigen Depot, sonst laendet die
     Abfrage im manuellen und meldet „keine Buchungen". */
  journalVerdrahten();

  $$('#auto-depots .inv-journal').forEach(b => b.addEventListener('click', e => {
    const tr = e.target.closest('tr[data-asset]');
    if (tr) investJournal(tr, Number(tr.dataset.asset), tr.dataset.depot);
  }));
}

function autoDepotBlock(depot, pos, block) {
  const vz = (block?.gewinn ?? 0) >= 0 ? 'plus' : 'minus';

  const kopf =
    '<div class="auto-depot-kopf"><b>' + esc(depotName(depot)) + '</b>'
    + (investState.leit === depot
        ? '<span class="pill ok" title="Diese Strategie geht ins Gesamtvermögen ein">'
          + 'zählt zum Vermögen</span>'
        : '<span class="pill off" title="Vergleichslauf — steht nicht in der Summe">'
          + 'Vergleich</span>')
    + '<span class="dim">' + esc(autoWas(depot)) + '</span>'
    + (block
        ? '<span class="auto-kennzahl">Vermögen <b>' + fmtNum(block.vermoegen, 2) + '</b> '
          + esc(block.waehrung) + '</span>'
          + '<span class="auto-kennzahl">Konto ' + fmtNum(block.kontostand, 2) + '</span>'
          + '<span class="auto-kennzahl invest-gewinn ' + vz + '">'
          + (block.gewinn >= 0 ? '+' : '') + fmtNum(block.gewinn, 2)
          + (block.renditePct == null ? ''
             : ' (' + (block.renditePct >= 0 ? '+' : '')
               + fmtNum(block.renditePct, 3) + ' %)') + '</span>'
          + (block.gebuehren > 0
              ? '<span class="auto-kennzahl minus">Gebühren '
                + fmtNum(block.gebuehren, 2) + '</span>' : '')
        : '')
    + '</div>';

  if (!pos.length) {
    return kopf + '<p class="hint block">Hält nichts. '
      + (depot === 'streng'
          ? 'Dieses Depot handelt nur mit nachgewiesenem Vorsprung — dass es leer '
            + 'bleibt, ist derzeit das Ergebnis und kein Fehler.'
          : 'Noch kein Lauf, oder nichts erfüllte die Bedingungen.')
      + '</p>';
  }

  return kopf
    + '<table class="grid invest-tab auto-tab"><thead><tr>'
    + '<th>Wert</th><th class="num">Kurs</th>'
    + '<th class="num" title="Der noch investierte Betrag je Anteil — der Kurs, bei dem '
    + 'die Position auf null stünde.">Nulllinie</th>'
    + '<th class="num">Einsatz</th><th class="num">Stand heute</th>'
    + '<th class="num">Gewinn</th><th class="num">Gebühr</th>'
    + '<th class="num">seit</th><th></th></tr></thead><tbody>'
    + pos.map(x => autoDepotZeile(x, depot)).join('')
    + '</tbody></table>';
}

function autoDepotZeile(x, depot) {
  const vz = (x.gewinn ?? 0) >= 0 ? 'plus' : 'minus';

  return '<tr data-asset="' + x.assetId + '" data-depot="' + esc(depot) + '">'
    + '<td><b>' + esc(x.symbol) + '</b><br><span class="dim">'
    + esc((x.name || '').slice(0, 34)) + '</span></td>'

    + '<td class="num">' + (x.kurs == null ? '<span class="dim">–</span>'
        : fmtNum(x.kurs, 4))
    + kursStand(x, x.kursUtc && (Date.now() - new Date(x.kursUtc)) > 36 * 3600e3) + '</td>'

    + '<td class="num">' + (x.einsatzJeAnteil == null ? '<span class="dim">–</span>'
        : fmtNum(x.einsatzJeAnteil, 4)) + '</td>'

    + '<td class="num">' + fmtNum(x.einsatz, 2) + ' <span class="dim">'
    + esc(x.waehrung) + '</span></td>'

    + '<td class="num">' + (x.stand == null ? '<span class="dim">–</span>'
        : '<b>' + fmtNum(x.stand, 2) + '</b>') + '</td>'

    + '<td class="num">' + (x.gewinn == null ? '<span class="dim">–</span>'
        : '<span class="invest-gewinn ' + vz + '">'
          + (x.gewinn >= 0 ? '+' : '') + fmtNum(x.gewinn, 2)
          + (x.renditePct == null ? ''
             : '<br><span class="dim">' + (x.renditePct >= 0 ? '+' : '')
               + fmtNum(x.renditePct, 2) + ' %</span>') + '</span>') + '</td>'

    + '<td class="num">' + (x.gebuehren > 0
        ? '<span class="minus">−' + fmtNum(x.gebuehren, 2) + '</span>'
        : '<span class="dim">–</span>') + '</td>'

    + '<td class="num dim">' + (x.seitUtc ? fmtDate(x.seitUtc) : '–') + '</td>'

    + '<td><button class="inv-journal" title="alle Buchungen dieses Wertes">'
    + x.buchungen + '×</button></td></tr>';
}

function autoWas(depot) {
  return {
    streng: 'handelt nur bei nachgewiesenem Vorsprung',
    aktiv:  'folgt der Erwartung des Modells',
    halten: 'Grundlinie: einmal gekauft, nie wieder'
  }[depot] || '';
}

function vergleichTabelle(v) {
  return '<table class="grid"><thead><tr><th>Depot</th><th>was es tut</th>'
    + '<th class="num">eingezahlt</th><th class="num">Vermögen</th>'
    + '<th class="num">Gewinn</th><th class="num">Gebühren</th>'
    + '<th class="num">Geschäfte</th><th class="num">gegen Grundlinie</th></tr></thead><tbody>'
    + v.zeilen.slice()
        .sort((a, b) => ({ manuell: 0, streng: 1, aktiv: 2, halten: 3 }[a.depot] ?? 9)
                      - ({ manuell: 0, streng: 1, aktiv: 2, halten: 3 }[b.depot] ?? 9))
        .map(z =>
        '<tr><td><b>' + esc(depotName(z.depot)) + '</b></td>'
        + '<td class="dim">' + esc(z.was) + '</td>'
        + '<td class="num">' + fmtNum(z.eingezahlt, 2) + '</td>'
        + '<td class="num"><b>' + fmtNum(z.vermoegen, 2) + '</b></td>'
        + '<td class="num invest-gewinn ' + (z.gewinn >= 0 ? 'plus' : 'minus') + '">'
        + (z.gewinn >= 0 ? '+' : '') + fmtNum(z.gewinn, 2)
        + (z.renditePct == null ? ''
           : '<br><span class="dim">' + fmtNum(z.renditePct, 3) + ' %</span>') + '</td>'
        + '<td class="num minus">−' + fmtNum(z.gebuehren, 2) + '</td>'
        + '<td class="num">' + z.geschaefte + '</td>'
        + '<td class="num">' + (z.vorsprungPct == null
            ? '<span class="dim">–</span>'
            : '<b class="invest-gewinn ' + (z.vorsprungPct >= 0 ? 'plus' : 'minus') + '">'
              + (z.vorsprungPct >= 0 ? '+' : '') + fmtNum(z.vorsprungPct, 3) + ' %</b>')
        + '</td></tr>').join('')
    + '</tbody></table>'
    + '<p class="hint block">' + esc(v.hinweis) + '</p>';
}

function laeufeTabelle(laeufe) {
  if (!laeufe.length) return '<p class="hint block">Noch kein Lauf.</p>';

  return '<table class="grid"><thead><tr><th>Lauf</th><th>Depot</th><th>wann</th>'
    + '<th class="num">geprüft</th><th class="num">Geschäfte</th>'
    + '<th class="num">Gebühren</th><th class="num">Vermögen</th><th>Notiz</th>'
    + '</tr></thead><tbody>'
    + laeufe.map(l =>
        '<tr><td><button class="auto-beschluesse" data-lauf="' + l.laufId + '">#'
        + l.laufId + '</button></td>'
        + '<td>' + esc(depotName(l.depot)) + '</td>'
        + '<td class="dim">' + fmtDate(l.gestartetUtc, true) + '</td>'
        + '<td class="num">' + l.geprueft + '</td>'
        + '<td class="num">' + (l.geschaefte > 0 ? '<b>' + l.geschaefte + '</b>' : '0') + '</td>'
        + '<td class="num">' + fmtNum(l.gebuehren, 2) + '</td>'
        + '<td class="num">' + (l.vermoegen == null ? '–' : fmtNum(l.vermoegen, 2)) + '</td>'
        + '<td class="dim">' + esc(l.notiz || (l.handelstag ? '' : 'kein Handelstag')) + '</td>'
        + '</tr>').join('')
    + '</tbody></table>';
}

function autopilotVerdrahten() {
  $$('.auto-zeile').forEach(z => {
    z.querySelector('.auto-speichern')?.addEventListener('click', async () => {
      const d = await guard(() => api('/api/autopilot/einstellung', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        /* Was fuer diese Strategie keine Bedeutung hat, steht als „–" da und
           wird auch nicht mitgeschickt -- der Server liesse den bisherigen Wert
           dann unveraendert stehen, statt ihn auf null zu setzen. */
        body: JSON.stringify({
          depot: z.dataset.depot,
          aktiv: z.querySelector('.auto-an').checked,
          werte: Number(z.querySelector('.auto-werte').value),
          startkapital: Number(z.querySelector('.auto-budget').value),
          waehrung: z.querySelector('.auto-waehrung').value,
          takt: z.querySelector('.auto-takt')?.value,
          maxAnteil: Number(z.querySelector('.auto-max').value) / 100,
          hysterese: z.querySelector('.auto-hyst')
            ? Number(z.querySelector('.auto-hyst').value) / 100 : undefined,
          nemotron: z.querySelector('.auto-nemo')?.checked
        })
      }));

      if (!d) return;
      setStatus(depotName(d.depot) + ': Budget ' + fmtNum(d.startkapital, 0) + ' '
        + d.waehrung + ', ' + d.werte + ' Werte, höchstens '
        + fmtNum(d.maxAnteil * 100, 0) + ' % je Wert, Umschichtung ' + d.taktName
        + (d.aktiv ? ', eingeschaltet' : ', ausgeschaltet') + '.'
        + (d.startkapital !== undefined
           ? ' Ein neues Budget wirkt erst beim Zurücksetzen.' : ''));
    });

    z.querySelector('.auto-reset')?.addEventListener('click',
      () => autoZuruecksetzen(z.dataset.depot));

    /* Sofort wirksam, nicht erst auf „Übernehmen": Ein Auswahlknopf, der eine
       Auswahl zeigt, die noch nicht gilt, ist eine Falschanzeige -- und diese
       hier entscheidet, welche Zahl ganz oben steht. */
    z.querySelector('.auto-zaehlt')?.addEventListener('change', async () => {
      const d = await guard(() => api('/api/autopilot/einstellung', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ depot: z.dataset.depot, zaehlt: true })
      }));

      if (!d) return;

      setStatus('Im Gesamtvermögen zählt jetzt: ' + depotName(d.depot)
        + '. Die anderen beiden bleiben Vergleichsläufe.');

      await drawInvest(++drawToken);
    });

    z.querySelector('.auto-lauf')?.addEventListener('click', async () => {
      setStatus('Autopilot läuft …', 'busy');

      const d = await guard(() => api('/api/autopilot/lauf?depot='
        + encodeURIComponent(z.dataset.depot) + '&erzwingen=true', { method: 'POST' }));

      if (!d) return;

      setStatus(depotName(d.depot) + ': ' + d.geschaefte + ' Geschäfte, '
        + fmtNum(d.gebuehren, 2) + ' Gebühren'
        + (d.notiz ? ' — ' + d.notiz : '') + '.');

      await drawInvest(++drawToken);
    });
  });

  $('#auto-rang')?.addEventListener('click', rangfolgeZeigen);
  $('#auto-reset-alle')?.addEventListener('click', () => autoZuruecksetzen(null));

  $$('.auto-beschluesse').forEach(b => b.addEventListener('click',
    () => beschluesseZeigen(Number(b.dataset.lauf))));
}

/* ================================================== Journal eines Depots ===

   Jeder Vorgang, der Geld bewegt hat -- Ein- und Auszahlungen wie Kaeufe und
   Verkaeufe, in EINER Liste.

   Es gibt bereits ein Journal je Wert und eines je Kassenwaehrung. Beide
   beantworten Teilfragen. Wer pruefen will, ob der Kontostand stimmt, muss den
   ganzen Weg sehen -- eingezahlt, gekauft, Gebuehr, verkauft -- in der
   Reihenfolge, in der es passiert ist. Deshalb steht in jeder Zeile der
   Kontostand NACH dem Vorgang: Ohne ihn ist ein Journal eine Liste von
   Ereignissen, mit ihm eine Rechnung, die man nachrechnen kann.             */

function journalVerdrahten() {
  $$('.journal-auf').forEach(b => {
    if (b.dataset.verdrahtet) return;      // sonst haengen nach jedem Neuzeichnen mehrere
    b.dataset.verdrahtet = '1';
    b.addEventListener('click', () => journalOeffnen(b.dataset.depot));
  });
}

async function journalOeffnen(depot) {
  const dlg = $('#journal-dialog');
  if (!dlg) return;

  $('#journal-titel').textContent = depot === 'autopilot'
    ? 'Journal · Autopilot'
    : 'Journal · ' + depotName(depot);
  $('#journal-inhalt').innerHTML = '<p class="hint block">lädt …</p>';

  if (!dlg.open) dlg.showModal();

  const d = await guard(() => api('/api/invest/journal?depot='
    + encodeURIComponent(depot) + '&grenze=500'));

  if (!d) { dlg.close(); return; }

  const wort = {
    einzahlung: 'eingezahlt', auszahlung: 'abgehoben',
    kauf: 'gekauft', verkauf: 'verkauft'
  };

  /* Nur bei mehreren Strategien eine Spalte dafuer -- beim manuellen Depot
     stuende in jeder Zeile dasselbe Wort. */
  const mehrere = depot === 'autopilot';

  /* Eine Summenzeile JE STRATEGIE. Sie zu einer Zahl zu addieren war der
     erste Entwurf und meldete „Konto 1.000,08" -- die Summe dreier
     Gegenrechnungen auf dasselbe Geld, also genau die Doppelzaehlung, vor der
     der Text darunter warnt. */
  const kopf = !d.zeilen.length
    ? ''
    : (d.summen || []).map(x =>
        '<div class="journal-summe">'
        + (mehrere ? '<span class="j-depot"><b>' + esc(depotName(x.depot))
            + '</b></span>' : '')
        + '<span><b>' + x.geschaefte + '</b> Geschäfte</span>'
        + '<span>eingezahlt <b>' + fmtNum(x.eingezahlt, 2) + '</b> '
        + esc(x.waehrung) + '</span>'
        + (x.ausgezahlt > 0
            ? '<span>abgehoben <b>' + fmtNum(x.ausgezahlt, 2) + '</b></span>' : '')
        + '<span class="minus">Gebühren <b>' + fmtNum(x.gebuehren, 2) + '</b></span>'
        + '<span>Konto <b>' + fmtNum(x.kontostand, 2) + '</b></span>'
        + '</div>').join('');

  $('#journal-inhalt').innerHTML =
    kopf
    + '<p class="hint block">' + esc(d.hinweis) + '</p>'
    + (!d.zeilen.length ? '' :
      '<table class="grid journal-tab"><thead><tr>'
      + '<th>wann</th>'
      + (mehrere ? '<th>Strategie</th>' : '')
      + '<th>was</th><th>Wert</th>'
      + '<th class="num">Kurs</th><th class="num">Anteile</th>'
      + '<th class="num" title="Was in den Kurs ging">Betrag</th>'
      + '<th class="num">Gebühr</th>'
      + '<th class="num" title="Was das Konto gekostet hat — Betrag plus Gebühr">Kasse</th>'
      + '<th class="num" title="Kontostand NACH diesem Vorgang'
      + (mehrere ? ', je Strategie gerechnet' : '') + '">Konto danach</th>'
      + '</tr></thead><tbody>'
      + d.zeilen.map(z =>
          '<tr class="j-' + esc(z.art) + '">'
          + '<td class="dim">' + fmtDate(z.amUtc, true) + '</td>'
          + (mehrere ? '<td>' + esc(depotName(z.depot)) + '</td>' : '')
          + '<td>' + esc(wort[z.art] || z.art) + '</td>'
          + '<td>' + (z.symbol
              ? '<b>' + esc(z.symbol) + '</b><br><span class="dim">'
                + esc((z.name || '').slice(0, 26)) + '</span>'
              : '<span class="dim">–</span>') + '</td>'
          + '<td class="num">' + (z.kurs == null ? '<span class="dim">–</span>'
              : fmtNum(z.kurs, 4)
                + (z.kursUtc ? '<br><span class="dim">' + fmtDate(z.kursUtc, true)
                   + '</span>' : '')) + '</td>'
          + '<td class="num">' + (z.anteile == null ? '<span class="dim">–</span>'
              : fmtNum(z.anteile, 6)) + '</td>'
          + '<td class="num invest-gewinn ' + (z.betrag >= 0 ? 'plus' : 'minus') + '">'
          + (z.betrag >= 0 ? '+' : '') + fmtNum(z.betrag, 2) + '</td>'
          + '<td class="num">' + (z.gebuehr > 0
              ? '<span class="minus">−' + fmtNum(z.gebuehr, 2) + '</span>'
              : '<span class="dim">–</span>') + '</td>'
          + '<td class="num invest-gewinn ' + (z.kasse >= 0 ? 'plus' : 'minus') + '">'
          + (z.kasse >= 0 ? '+' : '') + fmtNum(z.kasse, 2) + '</td>'
          + '<td class="num"><b>' + fmtNum(z.kontostandDanach, 2) + '</b> '
          + '<span class="dim">' + esc(z.waehrung) + '</span></td></tr>').join('')
      + '</tbody></table>');
}

$('#journal-zu')?.addEventListener('click', () => $('#journal-dialog')?.close());

/* Klick auf den Hintergrund schliesst. Das <dialog> selbst fuellt nur den
   Kasten; getroffen wird der Rand, wenn das Ereignisziel der Dialog ist. */
$('#journal-dialog')?.addEventListener('click', e => {
  if (e.target.id === 'journal-dialog') e.target.close();
});

/* Zuruecksetzen.

   OHNE Rueckfrage-Dialog: Das Depot ist virtuell, es geht kein echtes Geld
   verloren, und ein Bestaetigungsfenster fuer eine folgenlose Handlung
   erzieht dazu, Bestaetigungen wegzuklicken -- auch die, die zaehlen. Was
   verschwindet, steht im Titel des Knopfes und danach in der Statuszeile. */
async function autoZuruecksetzen(depot) {
  const d = await guard(() => api('/api/autopilot/reset'
    + (depot ? '?depot=' + encodeURIComponent(depot) : ''), { method: 'POST' }));

  if (!d) return;

  setStatus(d.zurueckgesetzt
    .map(x => depotName(x.depot) + ' → ' + fmtNum(x.startkapital, 0) + ' ' + x.waehrung)
    .join(', ') + ' zurückgesetzt.');

  await drawInvest(++drawToken);
}

/* Die Rangfolge, ohne dass ein Cent bewegt wird. Wer eine Handelsstrategie nur
   im Nachhinein an ihren Buchungen pruefen kann, prueft sie nicht. */
async function rangfolgeZeigen() {
  const d = await guard(() => api('/api/autopilot/rangfolge?depot=aktiv&grenze=25'));
  if (!d) return;

  $('#auto-detail').innerHTML =
    '<p class="hint block">' + esc(d.hinweis).replace(/\n\n/g, '</p><p class="hint block">')
    + '</p>'
    + '<table class="grid"><thead><tr><th>Wert</th><th class="num">Erwartung</th>'
    + '<th class="num">Treffer roh</th><th class="num">geschrumpft</th>'
    + '<th class="num">Fälle</th><th class="num">Verdienst</th>'
    + '<th class="num">Erwartungswert</th><th class="num">nötig</th>'
    + '<th>Lage</th></tr></thead><tbody>'
    + d.anwaerter.map(a =>
        '<tr><td><b>' + esc(a.symbol) + '</b> <span class="dim">'
        + esc((a.name || '').slice(0, 22)) + '</span></td>'
        + '<td class="num">' + fmtNum(a.punktzahl * 100, 3) + ' %</td>'
        + '<td class="num dim">' + (a.trefferquoteRoh == null ? '–'
            : fmtNum(a.trefferquoteRoh, 3)) + '</td>'
        + '<td class="num">' + (a.trefferquote == null ? '–'
            : fmtNum(a.trefferquote, 3)) + '</td>'
        + '<td class="num">' + a.bewertet + '</td>'
        + '<td class="num">' + (a.verdienst > 0
            ? '<b>' + fmtNum(a.verdienst, 2) + '</b>'
            : '<span class="pill off">0</span>') + '</td>'
        + '<td class="num invest-gewinn ' + ((a.erwartungswert ?? -1) > 0 ? 'plus' : 'minus')
        + '">' + (a.erwartungswert == null ? '–'
            : fmtNum(a.erwartungswert * 100, 3) + ' %') + '</td>'
        + '<td class="num dim">' + fmtNum(a.noetigeTrefferquote, 3) + '</td>'
        + '<td class="dim">' + esc(a.lage) + '</td></tr>').join('')
    + '</tbody></table>';
}

async function beschluesseZeigen(laufId) {
  const d = await guard(() => api('/api/autopilot/beschluesse/' + laufId + '?grenze=60'));
  if (!d) return;

  const marke = b => b.ausgefuehrt
    ? '<span class="pill ok">' + esc(b.beschluss) + '</span>'
    : '<span class="pill off">' + esc(b.beschluss) + '</span>';

  $('#auto-detail').innerHTML =
    '<p class="hint block">Alle Beschlüsse aus Lauf #' + laufId + ' — auch die '
    + '<b>abgelehnten</b>. Ein Autopilot, der nur seine Geschäfte protokolliert, lässt '
    + 'sich nicht prüfen: Man sähe, was er getan hat, und nie, was er erwogen und '
    + 'verworfen hat.</p>'
    + '<table class="grid"><thead><tr><th class="num">#</th><th>Wert</th>'
    + '<th>Beschluss</th><th class="num">Erwartung</th><th class="num">Treffer</th>'
    + '<th class="num">Betrag</th><th>Nemotron</th><th>Grund</th></tr></thead><tbody>'
    + d.beschluesse.map(b =>
        '<tr><td class="num dim">' + b.rang + '</td>'
        + '<td><b>' + esc(b.symbol) + '</b></td>'
        + '<td>' + marke(b) + '</td>'
        + '<td class="num">' + fmtNum(b.punktzahl * 100, 3) + ' %</td>'
        + '<td class="num">' + (b.trefferquote == null ? '–'
            : fmtNum(b.trefferquote, 3)) + '</td>'
        + '<td class="num">' + (b.betrag == null ? '–' : fmtNum(b.betrag, 2)) + '</td>'
        + '<td>' + (b.urteil == null
            ? '<span class="dim" title="nicht gefragt oder nicht erreichbar">kein Urteil</span>'
            : b.urteil ? '<span class="pill ok">zugestimmt</span>'
                       : '<span class="pill off">abgelehnt</span>') + '</td>'
        + '<td class="dim">' + esc((b.grund || '').slice(0, 110))
        + (b.urteilText ? '<br><i>' + esc(b.urteilText.slice(0, 110)) + '</i>' : '')
        + '</td></tr>').join('')
    + '</tbody></table>';
}


/* Die Karten je Waehrung: Konto, Positionen, Vermoegen.

   VERMOEGEN IST KONTO PLUS KURSWERT, und die grosse Zahl auf der Karte ist
   genau das. Nur den Kurswert zu zeigen waere irrefuehrend: Wer alles verkauft,
   saehe eine Null und haette doch sein Geld -- es liegt nur auf dem Konto.

   Getrennt je Waehrung, weil ein Gesamtwert ueber EUR- und USD-Positionen einen
   Wechselkurs voraussetzte, den dieses System nicht hat.                     */
function investSummen(d) {
  const bloecke = d.waehrungen || [];

  const kopf = '<p class="hint block">' + esc(d.hinweis) + '</p>';

  const karten = bloecke.map(b => {
    const vz = b.gewinn >= 0 ? 'plus' : 'minus';
    const pct = b.renditePct == null ? '' :
      ' <span class="' + vz + '">(' + (b.renditePct >= 0 ? '+' : '')
      + fmtNum(b.renditePct, 2) + ' %)</span>';

    return '<div class="invest-block" data-w="' + esc(b.waehrung) + '">'
      + '<div class="w">' + esc(b.waehrung) + ' · Vermögen</div>'
      + '<div class="stand">' + fmtNum(b.vermoegen, 2) + '</div>'
      + '<div class="zeile invest-gewinn ' + vz + '">'
      + (b.gewinn >= 0 ? '+' : '') + fmtNum(b.gewinn, 2) + pct + '</div>'
      + '<div class="zeile">Konto <b>' + fmtNum(b.kontostand, 2) + '</b>'
      + ' · in Kursen ' + fmtNum(b.stand, 2)
      + ' <span class="dim">(' + b.positionen + ')</span></div>'
      + '<div class="zeile">eingezahlt ' + fmtNum(b.eingezahlt, 2)
      + (b.ausgezahlt > 0 ? ' · abgehoben ' + fmtNum(b.ausgezahlt, 2) : '')
      + (b.gebuehren > 0
          ? ' · <span class="minus">Gebühren ' + fmtNum(b.gebuehren, 2) + '</span>' : '')
      + '</div></div>';
  }).join('');

  return kopf
    + (karten ? '<div class="invest-summen">' + karten + '</div>' : '')
    + investKontoLeiste(d);
}

/* Die Kontoleiste. Der Stand ist ein SOLL-Feld wie das der Position: eintragen,
   was dastehen soll, gebucht wird die Differenz. Dieselbe Regel an beiden
   Stellen -- zwei verschiedene Bedienlogiken auf einer Seite waeren eine
   Fehlerquelle ohne Gegenwert. */
function investKontoLeiste(d) {
  const konten = d.konten || [];

  const zeilen = konten.map(k =>
    '<div class="konto-zeile" data-w="' + esc(k.waehrung) + '">'
    + '<b>' + esc(k.waehrung) + '</b>'
    + '<label>Kontostand</label>'
    + '<input class="konto-stand" type="number" step="0.01" min="0" value="'
    + k.stand.toFixed(2) + '">'
    + '<label title="Reibungsverlust je Vorgang. Kaufen und Verkaufen sind zwei '
    + 'Vorgänge — wer eine Runde dreht, zahlt ihn zweimal.">Gebühr je Vorgang %</label>'
    + '<input class="konto-gebuehr" type="number" step="0.01" min="0" max="10" value="'
    + Number(k.gebuehrPct).toFixed(2) + '">'
    + '<button class="konto-setzen primary">Übernehmen</button>'
    + '<button class="konto-journal" title="alle Bewegungen dieser Währung">Kasse</button>'
    + '</div>').join('');

  return '<div class="konto-leiste">' + zeilen
    + '<div class="toolbar"><button class="journal-auf" data-depot="manuell">'
    + 'Journal — alle Vorgänge</button>'
    + '<span class="hint">Jeder Kauf, Verkauf und jede Einzahlung, mit dem '
    + 'Kontostand nach jedem Schritt.</span></div>'
    + '<p class="hint">Investitionen gehen vom Konto ab, Verkäufe buchen darauf '
    + 'zurück. Die Gebühr fällt bei <b>jedem</b> Vorgang an und verlässt das Konto '
    + 'zusätzlich zum Kaufbetrag. Zum Vergleich: Die Handelsseiten rechnen mit '
    + '0,3 % je Rundlauf, also 0,15 % je Bein.</p>'
    + '<div id="konto-out"></div></div>';
}

function investTabelle(d) {
  const p = d.positionen || [];

  if (!p.length) {
    return '<p class="hint block">Keine Werte gewählt und nichts gebucht — '
      + 'links Werte auswählen, dann erscheinen sie hier.</p>';
  }

  const waehl = ['EUR', 'USD'].map(w =>
    '<option value="' + w + '"' + (w === investState.waehrung ? ' selected' : '')
    + '>' + w + '</option>').join('');

  const kopf =
    '<div class="toolbar"><div class="group">'
    + '<label for="inv-waehrung">Währung neuer Positionen</label>'
    + '<select id="inv-waehrung">' + waehl + '</select>'
    + '</div><span class="hint">Bestehende Positionen behalten ihre eigene — '
    + 'ein Wechsel mitten im Lauf würde Beträge in zwei Zähleinheiten addieren.</span>'
    + '</div>';

  const zeilen = p.map(x => investZeile(x)).join('');

  return kopf
    + '<table class="grid invest-tab"><thead><tr>'
    + '<th>Wert</th>'
    + '<th class="num">Kurs</th>'
    + '<th class="num" title="Der noch investierte Betrag je Anteil — der Kurs, bei dem die Position auf null stünde. Nach einer Entnahme liegt er unter dem tatsächlichen Kaufkurs, weil weniger Geld drinsteckt.">Nulllinie</th>'
    + '<th class="num">Einsatz</th>'
    + '<th class="num">Stand heute</th>'
    + '<th class="num">Gewinn</th>'
    + '<th class="num" title="Was die Vorgänge dieses Wertes an Gebühr gekostet haben">Gebühr</th>'
    + '<th>investieren / anpassen auf</th>'
    + '<th></th></tr></thead><tbody>' + zeilen + '</tbody></table>';
}

/* Woher der Kurs stammt und von wann.

   `1h` heisst: aus einer Stundenbar, also so frisch, wie dieses System werden
   kann. `1d` heisst: fuer diesen Wert gibt es keine Stundendaten -- 34 der
   verfolgten Werte haben keine --, der Kurs haengt also am letzten
   Tagesschluss. Das ist ein Unterschied, den man sehen koennen muss, statt ihn
   zu vermuten. */
function kursStand(x, alt36) {
  if (!x.kursUtc) return '';

  const quelle = x.kursQuelle === '1h' ? 'Stundenkurs'
               : x.kursQuelle === '1d' ? 'Tagesschluss' : 'Kurs';

  return '<br><span class="dim' + (alt36 ? ' minus' : '') + '" title="' + quelle
    + (x.kursQuelle === '1d'
        ? ' — für diesen Wert liegen keine Stundendaten vor" '
        : '" ')
    + '>' + fmtDate(x.kursUtc, true) + '</span>';
}

function investZeile(x) {
  const hat = x.buchungen > 0;
  const vz = (x.gewinn ?? 0) >= 0 ? 'plus' : 'minus';

  /* Vorbelegt mit dem heutigen Stand: Das Feld sagt „so soll es sein", und
     unveraendert bestaetigen bucht deshalb nichts.

     NICHT ueber fmtNum: Das liefert die deutsche Schreibweise („5.000,00"), und
     ein <input type="number"> verwirft einen Wert, den es nicht als Zahl liest
     -- das Feld stand danach leer da, obwohl eine Position existierte. Wer dann
     bestaetigt, ohne hinzusehen, bucht nichts; wer eine Zahl eintippt, meint
     eine Aenderung und bekommt eine. Beides harmlos, aber die Vorbelegung war
     der ganze Zweck. */
  const soll = hat && x.stand != null ? x.stand.toFixed(2) : '';

  /* Der Kurszeitpunkt steht IMMER da, nicht nur wenn er alt ist.

     Vorher erschien er erst ab 36 Stunden -- und genau dadurch war nicht zu
     sehen, dass das Depot auf Tagesschluessen von vorgestern hing, waehrend
     stuendlich frische Kurse hereinkamen. Eine Zahl ohne ihren Zeitpunkt laesst
     sich nicht daraufhin pruefen, ob sie aktuell ist. */
  const alt36 = x.kursUtc && (Date.now() - new Date(x.kursUtc)) > 36 * 3600e3;

  return '<tr class="' + (hat ? '' : 'leer') + '" data-asset="' + x.assetId
    + '" data-symbol="' + esc(x.symbol) + '">'

    + '<td><b>' + esc(x.symbol) + '</b>'
    + (x.gewaehlt ? '' : ' <span class="pill off" title="nicht in der Auswahl">n. gewählt</span>')
    + '<br><span class="dim">' + esc((x.name || '').slice(0, 34)) + '</span></td>'

    + '<td class="num">' + (x.kurs == null ? '<span class="dim">–</span>' : fmtNum(x.kurs, 4))
    + kursStand(x, alt36) + '</td>'

    + '<td class="num">' + (x.einsatzJeAnteil == null ? '<span class="dim">–</span>'
        : fmtNum(x.einsatzJeAnteil, 4)) + '</td>'

    + '<td class="num">' + (hat ? fmtNum(x.einsatz, 2) + ' <span class="dim">'
        + esc(x.waehrung) + '</span>' : '<span class="dim">–</span>') + '</td>'

    + '<td class="num">' + (x.stand == null ? '<span class="dim">–</span>'
        : '<b>' + fmtNum(x.stand, 2) + '</b>') + '</td>'

    + '<td class="num">' + (x.gewinn == null ? '<span class="dim">–</span>'
        : '<span class="invest-gewinn ' + vz + '">'
          + (x.gewinn >= 0 ? '+' : '') + fmtNum(x.gewinn, 2)
          + (x.renditePct == null ? ''
             : '<br><span class="dim">' + (x.renditePct >= 0 ? '+' : '')
               + fmtNum(x.renditePct, 2) + ' %</span>')
          + '</span>') + '</td>'

    + '<td class="num">' + (hat && x.gebuehren > 0
        ? '<span class="minus">−' + fmtNum(x.gebuehren, 2) + '</span>'
        : '<span class="dim">–</span>') + '</td>'

    + '<td><input class="soll" type="number" step="0.01" min="0" value="' + soll
    + '" placeholder="Betrag" ' + (x.kurs == null ? 'disabled title="kein Kurs"' : '')
    + '><button class="inv-setzen" ' + (x.kurs == null ? 'disabled' : '')
    + '>Buchen</button></td>'

    + '<td>' + (hat
        ? '<button class="inv-journal" title="alle Buchungen dieses Wertes">'
          + x.buchungen + '×</button> '
          + '<button class="inv-weg" title="Simulation für diesen Wert verwerfen">×</button>'
        : '') + '</td></tr>';
}

function investVerdrahten() {
  const tab = $('.invest-tab');
  if (!tab) return;

  $('#inv-waehrung')?.addEventListener('change', e => {
    investState.waehrung = e.target.value;
  });

  $$('.konto-zeile').forEach(z => {
    z.querySelector('.konto-setzen')?.addEventListener('click', () => kontoSetzen(z));
    z.querySelector('.konto-journal')?.addEventListener('click', () => kontoJournal(z));

    /* Enter in einem der beiden Felder uebernimmt. Wer eine Zahl tippt, will
       sie abschicken -- nicht die Maus zum Knopf fuehren. */
    z.querySelectorAll('input').forEach(f => f.addEventListener('keydown', e => {
      if (e.key !== 'Enter') return;
      e.preventDefault();
      kontoSetzen(z);
    }));
  });

  tab.addEventListener('click', async e => {
    const tr = e.target.closest('tr[data-asset]');
    if (!tr) return;

    const symbol = tr.dataset.symbol;
    const assetId = Number(tr.dataset.asset);

    if (e.target.closest('.inv-setzen')) return investSetzen(tr, symbol);
    if (e.target.closest('.inv-journal')) return investJournal(tr, assetId, 'manuell');
    if (e.target.closest('.inv-weg')) return investLoeschen(symbol, assetId);
  });

  /* Enter im Feld bucht. Wer eine Zahl tippt, will sie abschicken -- nicht die
     Maus zum Knopf fuehren. */
  tab.addEventListener('keydown', e => {
    if (e.key !== 'Enter' || !e.target.classList.contains('soll')) return;
    e.preventDefault();

    const tr = e.target.closest('tr[data-asset]');
    if (tr) investSetzen(tr, tr.dataset.symbol);
  });
}

async function kontoSetzen(zeile) {
  const w = zeile.dataset.w;
  const stand = Number((zeile.querySelector('.konto-stand').value || '').replace(',', '.'));
  const geb = Number((zeile.querySelector('.konto-gebuehr').value || '').replace(',', '.'));

  if (!isFinite(stand) || stand < 0) {
    setStatus('Der Kontostand muss eine Zahl ab null sein.', 'err');
    return;
  }

  if (!isFinite(geb) || geb < 0 || geb > 10) {
    setStatus('Der Gebührensatz muss zwischen 0 und 10 % liegen.', 'err');
    return;
  }

  const d = await guard(() => api('/api/invest/konto', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ waehrung: w, stand, gebuehrPct: geb })
  }));

  if (!d) return;

  setStatus(w + '-Konto: ' + fmtNum(d.stand, 2) + ', Gebühr '
    + fmtNum(d.gebuehrPct, 2) + ' % je Vorgang.');

  await drawInvest(++drawToken);
}

/* Das Kassenjournal. Es steht hier, weil die Karte nur Summen zeigt: Wer
   nachlegt, verkauft und Gebuehren zahlt, sieht spaeter einen Kontostand und
   weiss nicht mehr, woraus er entstanden ist. */
async function kontoJournal(zeile) {
  const w = zeile.dataset.w;
  const raus = $('#konto-out');

  if (raus.dataset.w === w) {          // nochmal geklickt: zuklappen
    raus.innerHTML = '';
    raus.dataset.w = '';
    return;
  }

  const rows = await guard(() => api('/api/invest/kontobewegungen?waehrung='
    + encodeURIComponent(w) + '&grenze=200'));

  if (!rows) return;

  raus.dataset.w = w;

  const wort = {
    einzahlung: 'eingezahlt', auszahlung: 'abgehoben',
    kauf: 'gekauft', verkauf: 'verkauft', gebuehr: 'Gebühr'
  };

  raus.innerHTML = !rows.length
    ? '<p class="hint block">Noch keine Bewegung auf dem ' + esc(w) + '-Konto.</p>'
    : '<table class="grid"><thead><tr><th>wann</th><th>was</th><th>Wert</th>'
      + '<th class="num">Betrag</th></tr></thead><tbody>'
      + rows.map(m =>
          '<tr><td>' + fmtDate(m.amUtc, true) + '</td>'
          + '<td>' + esc(wort[m.grund] || m.grund) + '</td>'
          + '<td class="dim">' + esc(m.symbol || '–') + '</td>'
          + '<td class="num invest-gewinn ' + (m.betrag >= 0 ? 'plus' : 'minus') + '">'
          + (m.betrag >= 0 ? '+' : '') + fmtNum(m.betrag, 2) + '</td></tr>').join('')
      + '</tbody></table>';
}

async function investSetzen(tr, symbol) {
  const feld = tr.querySelector('input.soll');
  const roh = (feld.value || '').trim();

  if (roh === '') {
    setStatus('Kein Betrag eingetragen.', 'err');
    return;
  }

  const betrag = Number(roh.replace(',', '.'));

  if (!isFinite(betrag) || betrag < 0) {
    setStatus('Betrag muss eine Zahl ab null sein.', 'err');
    return;
  }

  /* Bestehende Positionen behalten ihre Waehrung; der Server weist einen
     Wechsel ab. Fuer neue gilt die Wahl in der Leiste. */
  const d = await guard(() => api('/api/invest/', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ symbol, betrag, waehrung: investState.waehrung })
  }));

  if (!d) return;

  setStatus(d.meldung);
  await drawInvest(++drawToken);
}

async function investLoeschen(symbol, assetId) {
  const d = await guard(() => api('/api/invest/' + assetId, { method: 'DELETE' }));
  if (!d) return;

  setStatus(symbol + ': Simulation verworfen (' + d.geloescht
    + (d.geloescht === 1 ? ' Buchung).' : ' Buchungen).'));
  await drawInvest(++drawToken);
}

/* Das Journal eines Wertes, aufklappbar unter seiner Zeile.

   Es steht hier, weil die Uebersicht nur Summen zeigt: Wer nachlegt und
   entnimmt, sieht spaeter einen Einsatz von 300 und weiss nicht mehr, ob das
   einmal 300 waren oder dreimal 500 minus zweimal 600. */
async function investJournal(tr, assetId, depot) {
  const naechste = tr.nextElementSibling;

  if (naechste && naechste.classList.contains('invest-journal')) {
    naechste.remove();
    investState.offen.delete(assetId);
    return;
  }

  /* Ohne das Depot laendet die Abfrage im manuellen -- und zeigte fuer eine
     Position des Autopiloten „keine Buchungen", obwohl daneben ein Bestand
     steht. */
  const rows = await guard(() => api('/api/invest/buchungen/' + assetId
    + '?depot=' + encodeURIComponent(depot || 'manuell')));

  if (!rows) return;

  investState.offen.add(assetId);

  const zeile = document.createElement('tr');
  zeile.className = 'invest-journal';

  zeile.innerHTML = '<td colspan="' + (tr.children.length || 9)
    + '"><table class="grid"><thead><tr>'
    + '<th>gebucht</th><th class="num">Kurs</th><th class="num">Betrag</th>'
    + '<th class="num">Gebühr</th><th class="num">Anteile</th>'
    + '<th>Kursstand</th></tr></thead><tbody>'
    + rows.map(b =>
        '<tr><td>' + fmtDate(b.amUtc, true) + '</td>'
        + '<td class="num">' + fmtNum(b.kurs, 4) + '</td>'
        + '<td class="num invest-gewinn ' + (b.betrag >= 0 ? 'plus' : 'minus') + '">'
        + (b.betrag >= 0 ? '+' : '') + fmtNum(b.betrag, 2) + ' ' + esc(b.waehrung) + '</td>'
        + '<td class="num">' + (b.gebuehr > 0
            ? '<span class="minus">−' + fmtNum(b.gebuehr, 2) + '</span>'
            : '<span class="dim">–</span>') + '</td>'
        + '<td class="num">' + fmtNum(b.anteile, 6) + '</td>'
        + '<td class="dim">' + (b.kursUtc ? fmtDate(b.kursUtc, true) : '–') + '</td></tr>'
      ).join('')
    + '</tbody></table></td>';

  tr.after(zeile);
}

/* Der Verlauf: was das Depot Tag fuer Tag wert war, gegen das, was bis dahin
   eingesetzt war. Der Abstand der beiden Linien IST der Gewinn -- eine einzelne
   Vermoegenslinie sagt nichts, solange man nicht weiss, wieviel hineingegangen
   ist. Dieselbe Lehre wie beim aufsummierten Vorsprung in der Rueckschau: Zwei
   Kurven, deren Abstand die Aussage traegt, statt einer, die man deuten muss. */
async function investVerlauf(token) {
  const d = await guard(() => api('/api/invest/verlauf'));
  if (!d || isStale(token)) return;

  const host = $('#invest-chart');
  if (!host) return;

  const reihen = (d.reihen || []).filter(r => r.punkte.length > 1);

  if (!reihen.length) {
    /* Zwei verschiedene Lagen, und ein gemeinsamer Satz waere fuer eine davon
       falsch: gar nichts gebucht, oder gebucht und erst ein Tag alt. */
    host.innerHTML = (d.reihen || []).length
      ? '<p class="hint block">Der Verlauf entsteht ab dem zweiten Tag — heute gibt es '
        + 'genau einen Punkt, und der ist der Einsatz selbst.</p>'
      : '';
    return;
  }

  host.innerHTML = '';

  reihen.forEach(r => {
    const box = document.createElement('div');
    box.className = 'chart-box';

    const kopf = document.createElement('div');
    kopf.className = 'chart-title';
    kopf.innerHTML = 'Vermögensverlauf ' + esc(r.waehrung)
      + '<span class="sub">Konto + Kurswert'
      + (r.werte.length ? ' · ' + esc(r.werte.join(', ')) : '') + '</span>';
    box.appendChild(kopf);

    const plotHost = document.createElement('div');
    box.appendChild(plotHost);
    host.appendChild(box);

    const xs = r.punkte.map(x => new Date(x.zeit).getTime() / 1000);
    const wert = r.punkte.map(x => x.vermoegen);
    const konto = r.punkte.map(x => x.konto);
    const einsatz = r.punkte.map(x => x.eingezahlt);

    const plot = new uPlot({
      width: plotWidth(box),
      height: 300,
      series: [
        { label: 'Zeit', value: '{YYYY}-{MM}-{DD} {HH}:{mm}' },
        {
          label: 'Vermögen', stroke: '#4c9aff', width: 2, spanGaps: true,
          value: (u, v) => (v == null ? '–' : fmtNum(v, 2))
        },
        {
          /* Duenn und ruhig: Wieviel gerade NICHT im Kurs steckt. Die Linie
             erklaert die Spruenge der Vermoegenskurve -- ein Verkauf hebt sie,
             ein Kauf senkt sie, und die Summe bleibt bis auf die Gebuehr
             gleich. */
          label: 'davon Konto', stroke: '#6f7f8f', width: 1.2, spanGaps: true,
          value: (u, v) => (v == null ? '–' : fmtNum(v, 2))
        },
        {
          /* Gestrichelt und blass: Das Eingezahlte ist die Latte, nicht das
             Ergebnis. Es soll erkennbar sein, ohne mit der Vermoegenslinie um
             Aufmerksamkeit zu ringen. Der Abstand der beiden IST der Gewinn --
             Gebuehren eingeschlossen, denn die haben das Konto bereits
             verlassen. */
          label: 'eingezahlt', stroke: '#c8ad5f', width: 1.2, dash: [4, 4],
          spanGaps: true, value: (u, v) => (v == null ? '–' : fmtNum(v, 2))
        }
      ],
      axes: [
        { stroke: '#8b97a6', grid: { stroke: '#2a323d', width: 1 },
          ticks: { stroke: '#2a323d' }, font: AXIS_FONT },
        /* NICHT fmtCompact: Das kuerzt auf eine Stelle und macht aus 9.266
           und 9.480 zweimal „9 Tsd" -- die Achse trug untereinander vier Paare
           gleicher Beschriftungen. Bei Kursen ueber Groessenordnungen hinweg ist
           die Kuerzung richtig, bei Betraegen eines Depots nicht: Sie liegen
           immer in derselben Groessenordnung, und genau die Stellen, die
           fmtCompact wegwirft, sind die interessanten. */
        { stroke: '#8b97a6', grid: { stroke: '#2a323d', width: 1 },
          ticks: { stroke: '#2a323d' }, font: AXIS_FONT,
          values: (u, t) => t.map(v => fmtNum(v, 0)), size: 76 }
      ],
      legend: { show: true },
      cursor: { drag: { x: false, y: false } },
      plugins: [zoomPanPlugin()]
    }, [xs, wert, konto, einsatz], plotHost);

    chartState.plots.push({ plot, box });
  });

  const h = document.createElement('p');
  h.className = 'hint block';
  h.textContent = reihen[0].hinweis;
  host.appendChild(h);
}


/* ============================================================ Neuzugänge ===

   Was neu an den Markt kommt -- Erstnotizen und Börsenlistings, mit dem
   erwarteten Datum, soweit die Quelle eines nennt.

   DIE WICHTIGSTE SPALTE IST „VORLAUF". Sie sagt, wieviele Tage zwischen
   unserer Entdeckung und dem erwarteten Start lagen. Genau daran entscheidet
   sich, ob man auf so etwas ueberhaupt setzen kann: Eine Ankuendigung, die
   am selben Tag kommt, an dem gehandelt wird, laesst keine Entscheidung mehr
   zu -- egal wie gut sie sonst waere.                                        */

async function ladeNeuzugaenge() {
  const d = await guard(() => api('/api/neuzugang/'));
  if (!d) return;

  const raus = $('#nz-out');

  const kennzahl = (was, wert, titel) =>
    '<div class="nz-kennzahl"' + (titel ? ' title="' + esc(titel) + '"' : '') + '>'
    + '<div class="w">' + esc(was) + '</div><div class="zahl">' + wert + '</div></div>';

  raus.innerHTML =
    '<div class="nz-kennzahlen">'
    + kennzahl('angekündigt', d.angekuendigt, 'noch nicht gehandelt')
    + kennzahl('gehandelt', d.gehandelt, 'im Bestand angekommen, Erstkurs bekannt')
    + kennzahl('ausgefallen', d.ausgefallen, 'angekündigt, aber nie daraus geworden')
    + kennzahl('Kohorte läuft', d.kohorteSeitTagen + ' T')
    + kennzahl('Vorlauf ⌀', d.mittlererVorlaufTage == null
        ? '–' : fmtNum(d.mittlererVorlaufTage, 1) + ' T',
        'Tage zwischen Ankündigung und erwartetem Start')
    + '</div>'

    + '<p class="hint block">' + esc(d.hinweis) + '</p>'

    + nzTabelle('Anstehend', d.anstehend, true)
    + nzTabelle('Gestartet oder ausgefallen', d.gestartet, false);
}

function nzTabelle(titel, zeilen, anstehend) {
  if (!zeilen.length) {
    return '<h4 class="nz-h">' + esc(titel) + '</h4>'
      + '<p class="hint block">' + (anstehend
          ? 'Nichts angekündigt. Beim nächsten Abholen kann sich das ändern.'
          : 'Noch nichts gestartet. Sobald ein angekündigter Wert im Bestand '
            + 'auftaucht, wird sein erster Kurs hier festgehalten.') + '</p>';
  }

  /* Der Rollbereich gehoert um die Tabelle, nicht um die Seite: Breite
     Inhalte muessen in sich rollen, sonst schiebt der Rumpf waagrecht und die
     Kopfzeile wandert aus dem Bild. */
  return '<h4 class="nz-h">' + esc(titel) + ' <span class="dim">('
    + zeilen.length + ')</span></h4>'
    + '<div class="nz-roll"><table class="grid nz-tab"><thead><tr>'
    + '<th>Wert</th><th>Markt</th><th>Quelle</th>'
    + (anstehend ? '<th class="num">erwartet</th><th class="num">in</th>'
                 : '<th class="num">Start</th><th class="num">Erstkurs</th>')
    + '<th class="num" title="Tage zwischen unserer Entdeckung und dem erwarteten '
    + 'Start — daran entscheidet sich, ob man überhaupt reagieren kann">Vorlauf</th>'
    + '<th>Stand</th></tr></thead><tbody>'
    + zeilen.map(z => nzZeile(z, anstehend)).join('')
    + '</tbody></table></div>';
}

function nzZeile(z, anstehend) {
  const marke = { angekuendigt: 'pill', gehandelt: 'pill ok', ausgefallen: 'pill off' };

  /* Der Stand steht als ASCII-Schluessel in der Datenbank -- dort gehoert er auch
     hin, weil ein Schluessel mit Umlaut in jeder Abfrage zur Stolperfalle wird.
     Angezeigt wird deutsch. */
  const stand = { angekuendigt: 'angekündigt', gehandelt: 'gehandelt',
                  ausgefallen: 'ausgefallen' };

  const preis = z.preisVon == null ? '' :
    ' <span class="dim">' + fmtNum(z.preisVon, 2)
    + (z.preisBis != null && z.preisBis !== z.preisVon
        ? '–' + fmtNum(z.preisBis, 2) : '') + '</span>';

  /* Nicht jede Ankuendigung nennt ein Symbol.

     Binance schreibt den Ticker meist in Klammern („… (DJTB) …"), aber
     laengst nicht immer -- „Binance Futures Will Launch USDⓈ-Margined …"
     enthaelt keines. Der Sammler faellt dann auf den Titel zurueck, damit die
     Zeile ueberhaupt eine Kennung hat. Sie als fette Ueberschrift zu zeigen
     ergaebe eine abgeschnittene Zeile Fliesstext, die wie ein kaputtes Symbol
     aussieht -- also entscheidet die FORM: kurz und ohne Leerzeichen ist ein
     Symbol, alles andere ist ein Titel. */
  const wieSymbol = z.symbol && z.symbol.length <= 14 && !/\s/.test(z.symbol);
  const kopf = wieSymbol ? z.symbol : (z.name || z.symbol || '');
  const unter = wieSymbol ? (z.name || '') : '';

  return '<tr>'
    + '<td>' + (z.url
        ? '<a href="' + esc(z.url) + '" target="_blank" rel="noopener">'
          + (wieSymbol ? '<b>' + esc(kopf) + '</b>' : esc(kopf.slice(0, 70))) + '</a>'
        : (wieSymbol ? '<b>' + esc(kopf) + '</b>' : esc(kopf.slice(0, 70))))
    + preis
    + (unter ? '<br><span class="dim">' + esc(unter.slice(0, 46)) + '</span>' : '')
    + '</td>'

    + '<td>' + esc(z.markt || '–') + '</td>'
    + '<td class="dim">' + esc(z.quelle) + ' · ' + esc(z.art) + '</td>'

    + (anstehend
        ? '<td class="num">' + (z.erwartetAm ? fmtDate(z.erwartetAm, false)
              : '<span class="dim">–</span>')
          + '</td>'
          + '<td class="num">' + (z.tageBis == null ? '<span class="dim">–</span>'
              : z.tageBis < 0 ? '<span class="minus">überfällig</span>'
              : '<b>' + z.tageBis + ' T</b>') + '</td>'
        : '<td class="num dim">' + (z.ersterKursUtc ? fmtDate(z.ersterKursUtc) : '–') + '</td>'
          + '<td class="num">' + (z.ersterKurs == null ? '<span class="dim">–</span>'
              : fmtNum(z.ersterKurs, 4)) + '</td>')

    + '<td class="num">' + (z.vorlauf == null ? '<span class="dim">–</span>'
        : z.vorlauf + ' T') + '</td>'

    + '<td><span class="' + (marke[z.status] || 'pill') + '">' + esc(stand[z.status] || z.status) + '</span>'
    + '<br><span class="dim">entdeckt ' + fmtDate(z.entdecktUtc) + '</span></td>'
    + '</tr>';
}

/* Das Quellen-Protokoll.

   Eine Quelle, die stillschweigend nichts mehr liefert, sieht aus wie ein
   Markt ohne Neuzugaenge. Deshalb steht je Lauf und Quelle da, ob sie
   geantwortet hat -- und mit welcher Meldung, wenn nicht. */
async function ladeNzLaeufe() {
  const d = await guard(() => api('/api/neuzugang/'));
  if (!d) return;

  $('#nz-out').innerHTML =
    '<h4 class="nz-h">Quellen-Protokoll</h4>'
    + (!d.laeufe.length ? '<p class="hint block">Noch kein Lauf.</p>'
      : '<table class="grid"><thead><tr><th>wann</th><th>Quelle</th>'
        + '<th class="num">gefunden</th><th class="num">neu</th><th>Ergebnis</th>'
        + '</tr></thead><tbody>'
        + d.laeufe.map(l =>
            '<tr><td class="dim">' + fmtDate(l.gestartetUtc, true) + '</td>'
            + '<td><b>' + esc(l.quelle) + '</b></td>'
            + '<td class="num">' + l.gefunden + '</td>'
            + '<td class="num">' + (l.neu > 0 ? '<b>' + l.neu + '</b>' : '0') + '</td>'
            + '<td>' + (l.erfolg
                ? '<span class="pill ok">geantwortet</span>'
                : '<span class="pill off">Ausfall</span> <span class="dim">'
                  + esc((l.meldung || '').slice(0, 90)) + '</span>') + '</td></tr>').join('')
        + '</tbody></table>');
}

$('#nz-sammeln')?.addEventListener('click', async () => {
  setStatus('holt Vorankündigungen …', 'busy');

  const d = await guard(() => api('/api/neuzugang/sammeln', { method: 'POST' }));
  if (!d) return;

  const neu = d.laeufe.reduce((a, l) => a + l.neu, 0);
  const aus = d.laeufe.filter(l => !l.erfolg).map(l => l.quelle);

  setStatus(neu + ' neu aus ' + d.laeufe.length + ' Quellen'
    + (aus.length ? ' — Ausfall bei ' + aus.join(', ') : '') + '.');

  await ladeNeuzugaenge();
});

$('#nz-laeufe')?.addEventListener('click', ladeNzLaeufe);

const VIEW_INIT = {
  neuzugang: () => { if (!$('#nz-out').children.length) ladeNeuzugaenge(); },
  charts: () => { if (!chartState.available.length) loadAvailable(); },
  select: () => { if (!$('#sel-table tbody').children.length) loadSelectTable(); },
  analysis: async () => {
    if (!$('#ana-asset').options.length) {
      await fillAssetSelect($('#ana-asset'));
      loadAnalysis();
    }
    // Die Rangliste hängt nicht am gewählten Wert und wird deshalb einmal
    // geladen, sobald die Ansicht das erste Mal aufgeht.
    if (!$('#kx-grid tbody').children.length) loadKreuzungen();
  },
  forecast: async () => {
    /* Beim Oeffnen nachrechnen, falls neue Kurse hereingekommen sind.

       Prognostiziert wird immer vom jetzigen Kurs aus ueber alle Horizonte. Kommt
       ein neuer Bar herein, ist die angezeigte Schaetzung von gestern -- sie steht
       dann neben einem Kurs, den sie nicht kannte, und das sieht man ihr nicht an.

       Die alten Zeilen bleiben stehen. Genau sie sind spaeter der Beleg: Was gestern
       fuer heute geschaetzt wurde, muss heute noch dastehen, sonst laesst sich nichts
       nachpruefen. Es wird ergaenzt, nie ersetzt.                                   */
    await frischeSchaetzung();

    if (!$('#fc-asset').options.length) {
      await fillAssetSelect($('#fc-asset'));
      loadForecast();
    }
  },
  herde: () => {
    if (!$('#he-ausloeser tbody').children.length) loadHerde();
  },
  langfrist: () => {
    if (!$('#lf-koerbe tbody').children.length) loadLangfrist();
  },
  daytrading: () => {
    if (!$('#dt-beweg tbody').children.length) loadDayTrading();
  },
  swap: async () => {
    if (!$('#sw-bestand tbody').children.length) {
      await fillSymbolListe();
      await loadBestand();
      loadTausch();
    }
  },
  pillars: loadPillars,
  system: async () => {
    await loadSystem();
    // Nur fuer Verwalter -- fuer alle anderen ist die Karte ohnehin verborgen.
    await ladeBenutzer();
  }
};

// ========================================================== Sitzung ===

/* Merkt sich, was eingestellt war, damit ein Neuladen der Seite nicht bei null
   anfaengt.

   Der Zustand liegt in der Datenbank, nicht im Browser. Das ist mehr Aufwand
   als localStorage und der Grund dafuer ist Absicht: Sobald es Benutzer gibt,
   sollen dieselben Einstellungen an einem anderen Rechner wieder da sein. Die
   Ablage kennt deshalb schon heute zwei Ebenen -- Sitzung und Benutzer --,
   auch wenn bislang nur die erste benutzt wird. Der Browser haelt nur einen
   Sitzungsschluessel in einem Cookie; alles Weitere kommt vom Server.

   Gespeichert wird nach Bereichen getrennt, damit der Kursfilter beim
   Schreiben nicht ueberbuegelt, was ein anderer Reiter gerade abgelegt hat. */

let stateReady = false;
let lastSaved = {};

/** Setzt in einer Schaltergruppe den passenden Knopf aktiv. */
function setActiveButton(container, key, value) {
  let hit = null;

  $$(container + ' button').forEach(b => {
    const on = b.dataset[key] === String(value);
    b.classList.toggle('active', on);
    if (on) hit = b;
  });

  return hit;
}

function collectUiState() {
  return {
    nav: {
      tab: $('#tabs button.active')?.dataset.view || 'charts'
    },
    pillars: pillarConfig(),
    charts: {
      view: chartState.view,
      mode: chartState.mode,
      interval: chartState.interval,

      /* Die Auswahl wird abgelegt, nicht nur als Id-Liste: sonst muesste beim
         Laden erst der gesamte Bestand geholt werden, bevor die Liste
         ueberhaupt anzeigbar waere -- und eine Auswahl, die gerade nicht in
         den gefilterten Bestand faellt, bliebe leer.

         Abgelegt wird aber nur, was die Anzeige braucht. Das ganze Asset mit
         Marktkapitalisierung, Boerse und Zeitstempeln waere ein Vielfaches an
         Daten, und zwar veraltete -- die richtige Quelle dafuer ist die
         Bestandsliste, nicht ein gemerkter Filter. */
      selected: chartState.selected.map(a => ({
        assetId: a.assetId, symbol: a.symbol, name: a.name, assetClass: a.assetClass
      })),

      cls: $('#chart-class').value,
      sector: $('#chart-sector').value,
      country: $('#chart-country').value,
      search: $('#chart-search').value,

      months: $('#months').value,
      from: $('#from-date').value,
      to: $('#to-date').value,

      rebase: $('#rebase').checked,
      logscale: $('#logscale')?.checked || false,
      forecast: $('#show-forecast').checked,
      forecastPast: $('#show-forecast-past').checked,
      anchor: $('#show-anchor').checked,

      horizons: $$('#fc-horizons button.active').map(b => b.dataset.h),
      asOf: asOfDates.slice(),
      flowScope: $('#flow-scope button.active')?.dataset.scope || 'all'
    }
  };
}

/** Traegt einen geladenen Zustand in die Oberflaeche ein. */
function applyUiState(areas) {
  applyPillarConfig(areas.pillars);

  const c = areas.charts;
  if (!c) return;

  if (Array.isArray(c.selected))
    chartState.selected = c.selected.slice(0, MAX_SELECTED);

  if (c.view) { chartState.view = c.view; setActiveButton('#view-switch', 'cview', c.view); }
  if (c.mode) { chartState.mode = c.mode; setActiveButton('#layout-mode', 'mode', c.mode); }
  if (c.interval) { chartState.interval = c.interval; setActiveButton('#interval-mode', 'interval', c.interval); }

  if (c.cls != null) $('#chart-class').value = c.cls;
  if (c.sector != null) $('#chart-sector').dataset.wanted = c.sector;
  if (c.country != null) $('#chart-country').dataset.wanted = c.country;
  if (c.search != null) $('#chart-search').value = c.search;

  if (c.months != null) $('#months').value = c.months;
  if (c.from != null) $('#from-date').value = c.from;
  if (c.to != null) $('#to-date').value = c.to;

  /* Datum und Schnellwahl duerfen nicht gleichzeitig gesetzt aussehen -- die
     Handhabung im laufenden Betrieb schliesst das aus, also darf das
     Wiederherstellen es nicht einschleppen. */
  if (c.from || c.to) $$('#range-presets button').forEach(x => x.classList.remove('active'));
  else setActiveButton('#range-presets', 'months', c.months ?? 12);

  if (c.rebase != null) $('#rebase').checked = !!c.rebase;
  if (c.logscale != null && $('#logscale')) $('#logscale').checked = !!c.logscale;
  if (c.forecast != null) $('#show-forecast').checked = !!c.forecast;
  if (c.forecastPast != null) $('#show-forecast-past').checked = !!c.forecastPast;
  if (c.anchor != null) $('#show-anchor').checked = !!c.anchor;

  if (Array.isArray(c.horizons)) {
    const wanted = new Set(c.horizons);
    $$('#fc-horizons button').forEach(b => b.classList.toggle('active', wanted.has(b.dataset.h)));
  }

  if (Array.isArray(c.asOf)) {
    asOfDates.length = 0;
    c.asOf.slice(0, MAX_ASOF).forEach(d => asOfDates.push(d));
    renderAsOfList();
  }

  if (c.flowScope) setActiveButton('#flow-scope', 'scope', c.flowScope);

  renderSelected();
}

/* Ein Schreibvorgang je Bereich, und nur wenn sich dort etwas geaendert hat.
   Ohne diesen Vergleich schriebe jeder Klick irgendwo in der Anwendung beide
   Bereiche erneut in die Datenbank. */
const saveUiState = debounce(() => {
  if (!stateReady) return;

  const now = collectUiState();

  for (const [area, value] of Object.entries(now)) {
    const json = JSON.stringify(value);
    if (json === lastSaved[area]) continue;

    lastSaved[area] = json;

    // Bewusst ohne die api()-Hilfe: das Speichern laeuft im Hintergrund und
    // soll die Statuszeile nicht dauernd auf "laedt" setzen.
    fetch('/api/state/' + area, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: json
    }).catch(() => { /* Ein verlorener Zustand ist kein Grund zu stoeren. */ });
  }
}, 700);

async function startUp() {
  let areas = null;

  try {
    const res = await fetch('/api/state/');
    if (res.ok) areas = (await res.json()).areas;
  } catch {
    /* Ohne gemerkten Zustand startet die Anwendung wie bisher. Ein Ausfall
       der Ablage darf die Oberflaeche nicht blockieren. */
  }

  if (areas) {
    applyUiState(areas);
    lastSaved = Object.fromEntries(
      Object.entries(collectUiState()).map(([k, v]) => [k, JSON.stringify(v)]));
  }

  applyViewControls();

  /* Reihenfolge: erst die Auswahllisten füllen, dann die gemerkte Wahl setzen,
     dann laden. Andersherum stünde der gemerkte Wert in einer Liste, die ihn
     noch nicht enthält, und ginge verloren. */
  await loadFacets();

  ['#chart-sector', '#chart-country'].forEach(sel => {
    const el = $(sel);
    const wanted = el.dataset.wanted;
    if (wanted && [...el.options].some(o => o.value === wanted)) el.value = wanted;
    delete el.dataset.wanted;
  });

  applySectorVisibility();
  await loadAvailable();

  // Erst jetzt zeichnen: vorher stuende die wiederhergestellte Auswahl noch nicht.
  if (chartState.selected.length || chartState.view === 'flow') drawCharts();

  const tab = areas?.nav?.tab;
  if (tab && tab !== 'charts') $(`#tabs button[data-view="${tab}"]`)?.click();

  stateReady = true;

  /* Ein einziger Zuhoerer statt eines Aufrufs in jeder Bedienhandlung. In der
     Erfassungsphase, damit er auch dann laeuft, wenn ein Handler die
     Weitergabe stoppt -- und mit Verzoegerung, sodass die eigentlichen
     Handler laengst durch sind, wenn der Zustand eingesammelt wird. */
  ['click', 'change', 'input'].forEach(t =>
    document.addEventListener(t, () => saveUiState(), true));
}

/* Erst die Anmeldung, dann die Daten.

   Ohne diese Reihenfolge holt der Seitenaufbau zwanzig Abfragen, die alle mit
   401 antworten -- der Nutzer saehe ein Feld voller Fehlermeldungen statt
   eines Anmeldefeldes. Die Startsequenz laeuft deshalb nur, wenn jemand
   angemeldet ist; sonst uebernimmt der Schleier, und nach der Anmeldung laedt
   die Seite ohnehin neu. */
pruefeAnmeldung().then(angemeldet => { if (angemeldet) startUp(); });


/* ==================================================== Anmeldung verdrahten ===

   Ganz am Ende, damit alle Hilfsfunktionen stehen. Die Pruefung laeuft VOR
   dem ersten Laden von Daten -- sonst holt die Oberflaeche zwanzig Abfragen,
   die alle mit 401 antworten, und der Nutzer sieht ein Feld voller
   Fehlermeldungen statt eines Anmeldefeldes.                                */


$('#wer-abmelden').onclick = async () => {
  await fetch('/api/auth/logout', { method: 'POST' });
  location.reload();
};

// ----------------------------------------------------- Benutzerverwaltung ---

async function ladeBenutzer() {
  if (!wer?.istAdmin) return;

  const t = clearTable('#bv-liste');
  const d = await guard(() => api('/api/auth/benutzer'));
  if (!d) return;

  for (const b of d.benutzer || []) {
    row(t, [
      { html: '<b>' + b.login + '</b>' },
      b.anzeigename || '–',
      { html: b.rolle === 'admin'
          ? '<span class="pill ok">Verwalter</span>'
          : '<span class="pill off">Nutzer</span>' },
      { html: !b.aktiv ? '<span class="pill off">abgeschaltet</span>'
             : b.gesperrt ? '<span class="pill off">gesperrt</span>'
             : '<span class="pill ok">aktiv</span>' },
      b.letzteAnmeldungUtc ? fmtDate(b.letzteAnmeldungUtc, true) : 'nie',
      /* Ein Feld vom Typ `password`, damit auch der Verwalter beim Tippen
         nicht auf den Bildschirm schreibt -- und `autocomplete="new-password"`,
         damit der Browser nicht das eigene Kennwort hineinfüllt. */
      { html: '<input type="password" class="bv-pw" data-id="' + b.userId
            + '" size="16" autocomplete="new-password" placeholder="mind. 12 Zeichen">' },
      { html: '<button class="bv-pwsetzen" data-id="' + b.userId + '">setzen</button> '
            + '<button class="bv-rolle" data-id="' + b.userId + '" data-rolle="'
            + (b.rolle === 'admin' ? 'user' : 'admin') + '">'
            + (b.rolle === 'admin' ? 'zu Nutzer' : 'zu Verwalter') + '</button> '
            + '<button class="bv-aktiv" data-id="' + b.userId + '" data-aktiv="'
            + (b.aktiv ? 'false' : 'true') + '">'
            + (b.aktiv ? 'abschalten' : 'einschalten') + '</button> '
            + '<button class="bv-weg" data-id="' + b.userId + '">löschen</button>' }
    ]);
  }

  /* Ein Zuhoerer am Tabellenkoerper statt an jeder Schaltflaeche: Die Zeilen
     werden bei jedem Laden neu gebaut. */
  t.onclick = async e => {
    const b = e.target.closest('button');
    if (!b) return;

    const id = b.dataset.id;

    if (b.classList.contains('bv-pwsetzen')) {
      const feld = t.querySelector('input.bv-pw[data-id="' + id + '"]');
      const neu = feld ? feld.value : '';

      if (!neu) { setStatus('Kein Kennwort eingegeben.', 'err'); return; }

      const ok = await guard(() => api('/api/auth/benutzer/' + id, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ kennwort: neu })
      }), 'Kennwort gesetzt — alle Sitzungen dieses Benutzers beendet');

      // Das Feld in jedem Fall leeren, auch wenn es schiefging.
      if (feld) feld.value = '';
      if (!ok) return;
    } else if (b.classList.contains('bv-weg')) {
      await guard(() => api('/api/auth/benutzer/' + id, { method: 'DELETE' }), 'Benutzer gelöscht');
    } else if (b.classList.contains('bv-rolle')) {
      await guard(() => api('/api/auth/benutzer/' + id, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ rolle: b.dataset.rolle })
      }), 'Rolle geändert');
    } else if (b.classList.contains('bv-aktiv')) {
      await guard(() => api('/api/auth/benutzer/' + id, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ aktiv: b.dataset.aktiv === 'true' })
      }), 'Zustand geändert');
    }

    await ladeBenutzer();
  };
}

$('#bv-anlegen').onclick = async () => {
  const ok = await guard(() => api('/api/auth/benutzer', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      login: $('#bv-login').value.trim(),
      kennwort: $('#bv-kennwort').value,
      rolle: $('#bv-rolle').value,
      anzeigename: $('#bv-name').value.trim() || null
    })
  }), 'Benutzer angelegt');

  if (ok) {
    $('#bv-login').value = '';
    $('#bv-name').value = '';
    $('#bv-kennwort').value = '';
    await ladeBenutzer();
  }
};

$('#pw-aendern').onclick = async () => {
  const ok = await guard(() => api('/api/auth/passwort', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ alt: $('#pw-alt').value, neu: $('#pw-neu').value })
  }), 'Kennwort geändert');

  if (ok) { $('#pw-alt').value = ''; $('#pw-neu').value = ''; }
};
