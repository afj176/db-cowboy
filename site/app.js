// Optional: refresh the version and download sizes from the latest GitHub release.
// The page already shows static values, so any failure here is silently ignored.
(function () {
  if (!window.fetch) return;
  fetch("https://api.github.com/repos/afj176/db-cowboy/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
    .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
    .then(function (release) {
      var version = String(release.tag_name || "").replace(/^v/i, "");
      if (/^\d+\.\d+\.\d+/.test(version)) {
        document.querySelectorAll("[data-version]").forEach(function (el) { el.textContent = version; });
      }
      (release.assets || []).forEach(function (asset) {
        if (!asset.size) return;
        var mb = Math.round(asset.size / 1e6) + " MB";
        document.querySelectorAll('[data-size="' + asset.name + '"]').forEach(function (el) { el.textContent = mb; });
      });
    })
    .catch(function () {});
})();
