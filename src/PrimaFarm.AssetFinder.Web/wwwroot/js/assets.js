/**
 * Three Acre Farm Fixed Assets - lookup page logic.
 * Plain JavaScript (no build step). Reads from the REST API (/api/v1) and posts photo uploads and
 * verifications to the MVC actions with the antiforgery token. All server text is HTML-escaped.
 */
(function () {
  "use strict";

  const lookupPanel = document.getElementById("lookup");
  const canEdit = lookupPanel.dataset.canEdit === "true";
  const csrfToken = document.querySelector('input[name="__RequestVerificationToken"]').value;

  function escapeHtml(s) {
    return String(s ?? "").replace(/[&<>"']/g, (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
  }

  class AuthRequired extends Error {}

  async function apiFetch(url, options = {}) {
    const headers = { Accept: "application/json", ...(options.headers || {}) };
    if (options.method && options.method !== "GET") headers.RequestVerificationToken = csrfToken;
    const res = await fetch(url, { credentials: "same-origin", ...options, headers });
    if (res.status === 401) {
      window.location.href = "/Account/Login?returnUrl=" + encodeURIComponent(location.pathname + location.search);
      throw new AuthRequired();
    }
    return res;
  }

  function formatDate(iso) {
    if (!iso) return null;
    return new Date(iso.endsWith("Z") ? iso : iso + "Z").toLocaleString();
  }

  // ---------------- Rendering ----------------

  function photoHtml(a) {
    const upload = canEdit
      ? `<div class="photo-actions">
           <label for="photoInput">${a.photoUrl ? "Replace photo" : "Add photo"}</label>
           <input id="photoInput" type="file" accept="image/jpeg,image/png,.jpg,.jpeg,.png" capture="environment">
           <div id="photoStatus" class="status-line" role="status"></div>
         </div>`
      : "";
    if (a.photoUrl) {
      return `<img src="${escapeHtml(a.photoUrl)}" alt="${escapeHtml(a.assetName || "Asset photo")}">${upload}`;
    }
    return `<div class="no-photo">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true">
          <rect x="3" y="5" width="18" height="14" rx="2"/><circle cx="9" cy="11" r="2"/><path d="M21 16l-5-4-4 3-3-2-6 5"/>
        </svg>
        <div>No photo on file<br>for this asset</div>
      </div>${upload}`;
  }

  function verifyHtml(a) {
    const last = a.lastVerifiedUtc
      ? `Last verified ${escapeHtml(formatDate(a.lastVerifiedUtc))} - condition <strong>${escapeHtml(a.lastCondition)}</strong>.`
      : "Not physically verified yet.";
    if (!canEdit) return `<div class="verify-box"><p class="hint">${last}</p></div>`;
    return `<div class="verify-box">
        <p class="panel-title">Physical verification</p>
        <p class="hint">${last} Seen this asset? Record it - it is sent to the finance asset register.</p>
        <div class="verify-row">
          <div class="field">
            <label for="condition">Condition</label>
            <select id="condition">
              <option>Good</option><option>Fair</option><option>Poor</option><option>Missing</option>
            </select>
          </div>
          <div class="field grow">
            <label for="notes">Notes (optional)</label>
            <input id="notes" type="text" maxlength="500" placeholder="e.g. found in House 2">
          </div>
          <button id="verifyBtn" type="button" class="btn btn-primary">Record verification</button>
        </div>
        <div id="verifyStatus" class="status-line" role="status"></div>
      </div>`;
  }

  function renderAssetCard(a) {
    const field = (k, v, wide) =>
      `<div class="field-item${wide ? " wide" : ""}"><div class="k">${k}</div><div class="v">${escapeHtml(v ?? "—")}</div></div>`;
    return `
      <article class="card" aria-label="Asset details">
        <div class="photo-box">${photoHtml(a)}</div>
        <div class="details">
          <h2 class="asset-name">${escapeHtml(a.assetName || "—")}</h2>
          <p class="asset-code-line">#${escapeHtml(a.assetCode)} · ${escapeHtml(a.farm)}</p>
          <div class="field-grid">
            ${field("Farm", a.farm)}
            ${field("Department", a.department)}
            ${field("Detail name", a.detailName, true)}
            ${field("Quantity", a.quantity)}
            ${field("Remarks", a.remarks, true)}
          </div>
        </div>
        ${verifyHtml(a)}
      </article>`;
  }

  // ---------------- Actions ----------------

  async function lookup(code) {
    const input = document.getElementById("codeInput");
    const result = document.getElementById("result");
    code = String(code).trim();
    input.value = code;
    hideSuggestions();

    if (!/^\d{1,18}$/.test(code)) {
      result.innerHTML = `<div class="not-found"><strong>Pick an asset.</strong> Type at least two characters and choose a match, or enter a numeric asset code.</div>`;
      return;
    }

    result.innerHTML = `<div class="loading" role="status">Looking up…</div>`;
    history.replaceState(null, "", "?code=" + encodeURIComponent(code));

    try {
      const res = await apiFetch(`/api/v1/assets/${encodeURIComponent(code)}`);
      if (res.status === 404) {
        result.innerHTML = `<div class="not-found"><strong>No match.</strong> Asset code "${escapeHtml(code)}" was not found. Double-check the code, or pick a suggestion while typing.</div>`;
        return;
      }
      if (!res.ok) throw new Error("HTTP " + res.status);
      const asset = await res.json();
      result.innerHTML = renderAssetCard(asset);
      wireCard(asset);
    } catch (err) {
      if (err instanceof AuthRequired) return;
      result.innerHTML = `<div class="error-box"><strong>Could not reach the server.</strong> Check the connection and try again.</div>`;
    }
  }

  function wireCard(asset) {
    const photoInput = document.getElementById("photoInput");
    if (photoInput) photoInput.addEventListener("change", () => uploadPhoto(asset.assetCode, photoInput));
    const verifyBtn = document.getElementById("verifyBtn");
    if (verifyBtn) verifyBtn.addEventListener("click", () => verify(asset.assetCode, verifyBtn));
  }

  async function uploadPhoto(code, input) {
    const status = document.getElementById("photoStatus");
    const file = input.files && input.files[0];
    if (!file) return;

    const setError = (msg) => { status.textContent = msg; status.className = "status-line err"; input.value = ""; };
    if (!/\.(jpe?g|png)$/i.test(file.name)) return setError("Only JPG or PNG photos are allowed.");
    if (file.size > 5 * 1024 * 1024) return setError("Photo is too large (max 5 MB).");

    status.textContent = "Uploading…";
    status.className = "status-line";
    const form = new FormData();
    form.append("photo", file);

    try {
      const res = await apiFetch(`/Assets/Photo/${encodeURIComponent(code)}`, { method: "POST", body: form });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) return setError(data.error || "Upload failed.");
      await lookup(code);
    } catch (err) {
      if (!(err instanceof AuthRequired)) setError("Upload failed. Check the connection.");
    }
  }

  async function verify(code, btn) {
    const status = document.getElementById("verifyStatus");
    btn.disabled = true;
    status.textContent = "Recording…";
    status.className = "status-line";

    try {
      const res = await apiFetch(`/Assets/Verify/${encodeURIComponent(code)}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          condition: document.getElementById("condition").value,
          notes: document.getElementById("notes").value,
        }),
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) {
        status.textContent = data.error || "Could not record the verification.";
        status.className = "status-line err";
        return;
      }
      status.textContent = data.message;
      status.className = data.financeStatus === "Posted" ? "status-line ok" : "status-line err";
      document.getElementById("notes").value = "";
    } catch (err) {
      if (err instanceof AuthRequired) return;
      status.textContent = "Could not record the verification. Check the connection.";
      status.className = "status-line err";
    } finally {
      btn.disabled = false;
    }
  }

  // ---------------- Autocomplete ----------------

  function hideSuggestions() {
    const box = document.getElementById("suggestions");
    box.classList.remove("show");
    box.innerHTML = "";
  }

  async function suggest(q) {
    const box = document.getElementById("suggestions");
    const farm = document.getElementById("farmFilter").value;
    try {
      const res = await apiFetch(`/api/v1/assets/search?q=${encodeURIComponent(q)}&farm=${encodeURIComponent(farm)}`);
      const matches = res.ok ? await res.json() : [];
      if (!Array.isArray(matches) || matches.length === 0) return hideSuggestions();

      box.innerHTML = matches.map((r) =>
        `<div class="sugg-item" role="option" tabindex="0" data-code="${escapeHtml(r.assetCode)}">
           <span>${escapeHtml(r.assetName || "—")} <span class="farm-tag">· ${escapeHtml(r.farm)}</span></span>
           <span class="sugg-code">#${escapeHtml(r.assetCode)}</span>
         </div>`).join("");
      box.classList.add("show");

      box.querySelectorAll(".sugg-item").forEach((el) => {
        const go = () => lookup(el.dataset.code);
        el.addEventListener("click", go);
        el.addEventListener("keydown", (ev) => {
          if (ev.key === "Enter" || ev.key === " ") { ev.preventDefault(); go(); }
        });
      });
    } catch {
      hideSuggestions();
    }
  }

  // ---------------- Init ----------------

  function init() {
    const input = document.getElementById("codeInput");
    const result = document.getElementById("result");

    result.innerHTML = `<div class="welcome-state"><strong>Ready to look up an asset</strong>
      <p>Type an asset code above (or start typing a name). Select a suggestion, or press Find / Enter.</p></div>`;

    // A name typed without picking a suggestion opens the best match.
    const submit = () => {
      const v = input.value.trim();
      if (!v) return;
      const first = document.querySelector(".sugg-item");
      lookup(!/^\d+$/.test(v) && first ? first.dataset.code : v);
    };

    document.getElementById("searchBtn").addEventListener("click", submit);
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") submit();
      if (e.key === "ArrowDown") {
        const first = document.querySelector(".sugg-item");
        if (first) { e.preventDefault(); first.focus(); }
      }
    });

    let timer;
    input.addEventListener("input", () => {
      clearTimeout(timer);
      const v = input.value.trim();
      if (v.length < 2) return hideSuggestions();
      timer = setTimeout(() => suggest(v), 200);
    });

    document.addEventListener("click", (e) => { if (!e.target.closest(".search-panel")) hideSuggestions(); });

    const deepLink = new URLSearchParams(location.search).get("code");
    if (deepLink) lookup(deepLink); else input.focus();
  }

  init();
})();
