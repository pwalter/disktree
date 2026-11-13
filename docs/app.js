// Einzige Netzwerkanfrage der Seite: neueste Version von GitHub (nur beim Laden, Fehler werden still ignoriert).
(function () {
  'use strict';
  function size(bytes) {
    if (!bytes) return '';
    return bytes >= 1048576 ? (bytes / 1048576).toFixed(1).replace('.', ',') + ' MB' : Math.round(bytes / 1024) + ' KB';
  }
  function setText(id, text) {
    var el = document.getElementById(id);
    if (el && text) { el.textContent = text; }
  }
  try {
    if (!window.fetch) return;
    fetch('https://api.github.com/repos/pwalter/disktree/releases/latest', { headers: { Accept: 'application/vnd.github+json' }, referrerPolicy: 'no-referrer' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (rel) {
        if (!rel || !rel.tag_name) return;
        var version = String(rel.tag_name).replace(/^v/, '');
        var date = '';
        if (rel.published_at) {
          date = new Date(rel.published_at).toLocaleDateString('de-DE', { day: 'numeric', month: 'long', year: 'numeric' });
        }
        setText('rel-version', version);
        setText('rel-date', date);
        setText('hero-version', 'Aktuelle Version ' + version + (date ? ' vom ' + date : ''));
        (rel.assets || []).forEach(function (a) {
          if (a.name === 'DiskTree.exe') { setText('size-full', '(' + size(a.size) + ')'); }
          if (a.name === 'DiskTree-slim.exe') { setText('size-slim', '(' + size(a.size) + ')'); }
        });
      })
      .catch(function () { /* ohne JS oder Netz bleibt der statische Text stehen */ });
  } catch (e) { /* ignorieren */ }
})();
