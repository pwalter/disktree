// DiskTree-Landingpage. Kleines Vanilla-JS ohne Abhängigkeiten.
// Einzige Netzwerkanfrage: neueste Version von GitHub (nur beim Laden, Fehler werden still ignoriert).
// Ohne JS bleibt die Seite vollständig lesbar; alles hier ist reine Verbesserung.
(function () {
  'use strict';

  var root = document.documentElement;
  var mqReduce = window.matchMedia ? window.matchMedia('(prefers-reduced-motion: reduce)') : null;
  function reduced() { return !!(mqReduce && mqReduce.matches); }
  function $(sel, ctx) { return (ctx || document).querySelector(sel); }
  function $$(sel, ctx) { return Array.prototype.slice.call((ctx || document).querySelectorAll(sel)); }
  function safe(fn) { try { fn(); } catch (e) { /* eine kaputte Verbesserung darf den Rest nicht stoppen */ } }

  // ---------- Version und Dateigrößen von GitHub ----------
  safe(function () {
    function size(bytes) {
      if (!bytes) return '';
      return bytes >= 1048576 ? (bytes / 1048576).toFixed(1).replace('.', ',') + ' MB' : Math.round(bytes / 1024) + ' KB';
    }
    function setText(id, text) {
      var el = document.getElementById(id);
      if (el && text) { el.textContent = text; }
    }
    if (!window.fetch) return;
    fetch('https://api.github.com/repos/pwalter/disktree/releases/latest', { headers: { Accept: 'application/vnd.github+json' }, referrerPolicy: 'no-referrer', credentials: 'omit' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (rel) {
        if (!rel || !rel.tag_name) return;
        var version = String(rel.tag_name).replace(/^v/, '');
        var date = '';
        if (rel.published_at) {
          date = new Date(rel.published_at).toLocaleDateString('de-DE', { day: 'numeric', month: 'long', year: 'numeric' });
        }
        setText('rel-version', version);
        setText('rel-date', date ? 'vom ' + date : '');
        setText('hero-version', 'Aktuelle Version ' + version + (date ? ' vom ' + date : '') + ' · Windows x64');
        (rel.assets || []).forEach(function (a) {
          if (a.name === 'DiskTree.exe') { setText('size-full', size(a.size) + ' · Version ' + version); }
          if (a.name === 'DiskTree-slim.exe') { setText('size-slim', size(a.size) + ' · Version ' + version); }
        });
      })
      .catch(function () { /* ohne Netz bleibt der statische Text stehen */ });
  });

  // ---------- Farbschema: Automatisch / Hell / Dunkel ----------
  safe(function () {
    var btn = document.getElementById('theme-btn');
    if (!btn) return;
    var KEY = 'disktree-theme';
    var modes = ['auto', 'light', 'dark'];
    var names = { auto: 'Automatisch', light: 'Hell', dark: 'Dunkel' };
    var icons = { auto: '#i-contrast', light: '#i-sun', dark: '#i-moon' };
    var sources = $$('picture source[media*="prefers-color-scheme"]');
    function current() { var t = root.getAttribute('data-theme'); return t === 'light' || t === 'dark' ? t : 'auto'; }
    function apply(mode, store) {
      if (mode === 'auto') { root.removeAttribute('data-theme'); } else { root.setAttribute('data-theme', mode); }
      // <picture>-Quellen dem gewählten Schema folgen lassen
      var media = mode === 'dark' ? 'all' : mode === 'light' ? 'not all' : '(prefers-color-scheme: dark)';
      sources.forEach(function (s) { s.setAttribute('media', media); });
      var next = modes[(modes.indexOf(mode) + 1) % modes.length];
      btn.querySelector('.theme-label').textContent = 'Design: ' + names[mode];
      btn.querySelector('use').setAttribute('href', icons[mode]);
      btn.setAttribute('aria-label', 'Design: ' + names[mode] + '. Umschalten auf ' + names[next]);
      btn.title = 'Design: ' + names[mode] + ' (Klick: ' + names[next] + ')';
      if (store) {
        try { if (mode === 'auto') { localStorage.removeItem(KEY); } else { localStorage.setItem(KEY, mode); } } catch (e) { /* Speicher gesperrt */ }
      }
    }
    btn.hidden = false;
    apply(current(), false);
    btn.addEventListener('click', function () {
      apply(modes[(modes.indexOf(current()) + 1) % modes.length], true);
    });
  });
  function isDark() {
    var t = root.getAttribute('data-theme');
    if (t) return t === 'dark';
    return !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
  }

  // ---------- Scroll-Reveal ----------
  safe(function () {
    var items = $$('.reveal, .tilt');
    if (!items.length) return;
    // Gestaffelte Verzögerung innerhalb von Listen
    $$('[data-stagger]').forEach(function (list) {
      $$('.reveal', list).forEach(function (el, i) { el.style.setProperty('--sd', Math.min(i, 8) * 70 + 'ms'); });
    });
    function show(el) {
      el.classList.add('in');
      el.dispatchEvent(new CustomEvent('reveal'));
      // Nach dem Einblenden die Reveal-Klasse entfernen, damit eigene Übergänge (Hover) wieder greifen
      setTimeout(function () { el.classList.remove('reveal'); el.style.removeProperty('--sd'); }, 1300);
    }
    if (reduced() || !('IntersectionObserver' in window)) { items.forEach(function (el) { el.classList.add('in'); }); return; }
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) { io.unobserve(e.target); show(e.target); }
      });
    }, { rootMargin: '0px 0px -8% 0px', threshold: 0.12 });
    items.forEach(function (el) { io.observe(el); });
    root.classList.add('reveal-on');
  });

  // ---------- Hochzählende Kennzahlen ----------
  safe(function () {
    var nums = $$('.count[data-to]');
    if (!nums.length || reduced() || !('IntersectionObserver' in window)) return;
    function run(el) {
      var to = parseInt(el.getAttribute('data-to'), 10), t0 = null, dur = 1200 + Math.min(to, 300) * 2;
      function step(ts) {
        if (t0 === null) t0 = ts;
        var p = Math.min(1, (ts - t0) / dur), e = 1 - Math.pow(1 - p, 3);
        el.textContent = Math.round(to * e).toLocaleString('de-DE');
        if (p < 1) requestAnimationFrame(step);
      }
      el.textContent = '0';
      requestAnimationFrame(step);
    }
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) { if (e.isIntersecting) { io.unobserve(e.target); run(e.target); } });
    }, { threshold: 0.6 });
    nums.forEach(function (el) { io.observe(el); });
  });

  // ---------- Live-Baum im Hero ----------
  safe(function () {
    var lt = document.getElementById('live-tree');
    var replay = document.getElementById('lt-replay');
    if (!lt || reduced()) return;
    var state = $('.lt-state', lt);
    var finalState = state.textContent;
    var rows = $$('.row', lt).map(function (el) {
      return { el: el, gb: parseFloat(el.getAttribute('data-gb')), pct: parseFloat(el.getAttribute('data-pct')),
        sz: $('.sz', el), pc: $('.pc', el), bar: $('.bar i', el) };
    });
    function fmtSize(gb) {
      if (gb < 1) return Math.round(gb * 1000) + ' MB';
      if (gb >= 100) return Math.round(gb) + ' GB';
      return gb.toFixed(1).replace('.', ',') + ' GB';
    }
    function fmtPct(p) { return p.toFixed(1).replace('.', ',') + ' %'; }
    function ease(p) { return 1 - Math.pow(1 - p, 3); }
    // Zeitplan: Wurzel läuft über die ganze Dauer, Unterordner erscheinen nacheinander
    var STEP = 330, DUR = 1150, FIRST = 260;
    var total = FIRST + (rows.length - 2) * STEP + DUR + 200;
    rows.forEach(function (r, i) {
      r.start = i === 0 ? 0 : FIRST + (i - 1) * STEP;
      r.dur = i === 0 ? total : DUR + (i % 3) * 120;
    });
    var raf = 0, t0 = 0;
    function frame(ts) {
      if (!t0) t0 = ts;
      var t = ts - t0, done = true;
      rows.forEach(function (r) {
        if (t < r.start) { done = false; return; }
        if (!r.shown) { r.shown = true; r.el.classList.add('shown', 'busy'); }
        var p = Math.min(1, (t - r.start) / r.dur), e = ease(p);
        r.bar.style.setProperty('--p', (r.pct * e / 100).toFixed(4));
        r.pc.textContent = fmtPct(r.pct * e);
        if (p < 1) { done = false; r.sz.textContent = fmtSize(r.gb * e) + ' …'; }
        else if (r.el.classList.contains('busy')) { r.el.classList.remove('busy'); r.sz.textContent = fmtSize(r.gb); r.pc.textContent = fmtPct(r.pct); }
      });
      var gp = Math.min(1, t / total);
      if (!done) {
        state.textContent = 'Scanne … ' + Math.round(81 * gp) + ' Ordner · ' + Math.round(1107 * gp).toLocaleString('de-DE') + ' Dateien';
        raf = requestAnimationFrame(frame);
      } else {
        state.textContent = finalState;
        lt.classList.remove('playing');
        raf = 0;
      }
    }
    function play() {
      if (raf) cancelAnimationFrame(raf);
      t0 = 0;
      rows.forEach(function (r) {
        r.shown = false;
        r.el.classList.remove('shown', 'busy');
        r.bar.style.setProperty('--p', '0');
        r.pc.textContent = fmtPct(0);
        r.sz.textContent = '';
      });
      lt.classList.remove('playing');
      void lt.offsetWidth; // Animation des Scannen-Knopfs neu starten
      lt.classList.add('playing');
      state.textContent = 'Scanne …';
      raf = requestAnimationFrame(frame);
    }
    if (replay) { replay.hidden = false; replay.addEventListener('click', play); }
    if ('IntersectionObserver' in window) {
      var io = new IntersectionObserver(function (entries) {
        if (entries[0].isIntersecting) { io.disconnect(); setTimeout(play, 350); }
      }, { threshold: 0.3 });
      io.observe(lt);
    } else { play(); }
  });

  // ---------- Galerie als Tabs ----------
  safe(function () {
    var gal = document.getElementById('gallery');
    if (!gal) return;
    var list = $('.g-tabs', gal);
    var panels = $$('.g-panel', gal);
    if (!list || !panels.length) return;
    var tabs = panels.map(function (p, i) {
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'g-tab';
      b.id = 'tab-' + p.id;
      b.setAttribute('role', 'tab');
      b.setAttribute('aria-controls', p.id);
      b.textContent = p.getAttribute('data-label');
      p.setAttribute('role', 'tabpanel');
      p.setAttribute('aria-labelledby', b.id);
      p.setAttribute('tabindex', '0');
      b.addEventListener('click', function () { select(i, false); });
      list.appendChild(b);
      return b;
    });
    function select(i, focus) {
      tabs.forEach(function (t, k) {
        var on = k === i;
        t.setAttribute('aria-selected', on ? 'true' : 'false');
        t.tabIndex = on ? 0 : -1;
        panels[k].hidden = !on;
        panels[k].classList.toggle('enter', on);
      });
      if (focus) tabs[i].focus();
      // Aktiven Tab bei schmalem Bildschirm sichtbar halten (nur innerhalb der Tab-Leiste scrollen)
      var t = tabs[i], l = list;
      if (t.offsetLeft < l.scrollLeft || t.offsetLeft + t.offsetWidth > l.scrollLeft + l.clientWidth) {
        l.scrollLeft = t.offsetLeft - 8;
      }
    }
    list.addEventListener('keydown', function (e) {
      var i = tabs.indexOf(document.activeElement);
      if (i < 0) return;
      var n = tabs.length, k = null;
      if (e.key === 'ArrowRight') k = (i + 1) % n;
      else if (e.key === 'ArrowLeft') k = (i - 1 + n) % n;
      else if (e.key === 'Home') k = 0;
      else if (e.key === 'End') k = n - 1;
      if (k !== null) { e.preventDefault(); select(k, true); }
    });
    gal.classList.add('is-tabs');
    list.hidden = false;
    select(0, false);
  });

  // ---------- Lightbox ----------
  safe(function () {
    var dlg = document.getElementById('lightbox');
    if (!dlg || typeof dlg.showModal !== 'function') return; // ohne <dialog>: Link öffnet das Bild direkt
    var img = document.getElementById('lb-img'), cap = document.getElementById('lb-cap'), closeBtn = document.getElementById('lb-close');
    var opener = null;
    document.addEventListener('click', function (e) {
      var a = e.target.closest ? e.target.closest('a.zoom') : null;
      if (!a || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
      e.preventDefault();
      opener = a;
      var src = isDark() && a.getAttribute('data-dark') ? a.getAttribute('data-dark') : a.getAttribute('href');
      var inner = a.querySelector('img');
      img.src = src;
      img.alt = inner ? inner.alt : '';
      cap.textContent = a.getAttribute('data-caption') || '';
      dlg.showModal();
      closeBtn.focus();
    });
    closeBtn.addEventListener('click', function () { dlg.close(); });
    dlg.addEventListener('click', function (e) { if (e.target === dlg) dlg.close(); });
    dlg.addEventListener('close', function () {
      img.removeAttribute('src');
      if (opener) { opener.focus(); opener = null; }
    });
  });

  // ---------- Glanz auf Karten (folgt dem Mauszeiger) ----------
  safe(function () {
    if (reduced() || !(window.matchMedia && window.matchMedia('(hover: hover)').matches)) return;
    $$('.card').forEach(function (c) {
      c.addEventListener('pointermove', function (e) {
        var r = c.getBoundingClientRect();
        c.style.setProperty('--mx', (e.clientX - r.left) + 'px');
        c.style.setProperty('--my', (e.clientY - r.top) + 'px');
      });
    });
  });
})();
