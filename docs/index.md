---
layout: home
title: Detection Rule Health
description: Free, local overview of Microsoft Sentinel analytics rules and Defender XDR custom detections. See how each rule performs and whether the values it depends on still exist.
---

<section class="hero">
  <div>
    <div class="chips">
      <span class="chip">Public beta</span>
      <span class="chip secure">Runs on your machine</span>
      <span class="chip neutral">Free · MIT</span>
    </div>
    <h1>Know which detection rules still work.</h1>
    <p class="lede">One local overview of your Microsoft Sentinel analytics rules and Defender XDR custom detections:
    how each rule performs, and whether the values it depends on still exist in your data.</p>
    <div class="actions">
      <a class="btn primary" href="{{ '/quick-start/' | relative_url }}">Quick start</a>
      <a class="btn" href="{{ '/setup/' | relative_url }}">Setup guide</a>
      <a class="btn" href="https://github.com/{{ site.repository }}">View on GitHub</a>
    </div>
    <div class="hero-cmd">
<pre><code>docker run -d --name detectionrulehealth \
  -p 127.0.0.1:5180:5180 -v drh-data:/data \
  --read-only --tmpfs /tmp --cap-drop ALL \
  --security-opt no-new-privileges \
  {{ site.image }}:beta</code></pre>
    </div>
  </div>
  <img class="hero-mark" src="{{ '/assets/img/mark-dogs-bone.svg' | relative_url }}" alt="">
</section>

{% assign shot = site.static_files | where: "path", "/assets/img/screens/overview.png" | first %}
{% if shot %}
<section class="band">
  <div class="shot"><img src="{{ '/assets/img/screens/overview.png' | relative_url }}" alt="Detection Rule Health overview page with demo data: rule efficacy chart and value-check status" width="1440" height="900"></div>
</section>
{% endif %}

<section class="band">
  <p class="eyebrow">What it shows</p>
  <h2>Two questions per rule</h2>
  <div class="cards">
    <div class="card">
      <div class="icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2"/></svg></div>
      <p class="eyebrow">Efficacy</p>
      <h3>Is it worth its noise?</h3>
      <p>Incidents per rule and the true positive, benign positive, false positive, undetermined and open split. Counts sit next to every rate, and rules with too few classified incidents are marked as such.</p>
    </div>
    <div class="card">
      <div class="icon"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 7V5a2 2 0 0 1 2-2h2"/><path d="M17 3h2a2 2 0 0 1 2 2v2"/><path d="M21 17v2a2 2 0 0 1-2 2h-2"/><path d="M7 21H5a2 2 0 0 1-2-2v-2"/><circle cx="12" cy="12" r="3"/><path d="m16 16-1.9-1.9"/></svg></div>
      <p class="eyebrow">Value checks</p>
      <h3>Can it still fire?</h3>
      <p>Every literal a rule compares against, such as <code>ActionType == "LogonFailed"</code>, is extracted with Microsoft's KQL parser. You choose which values must always occur; the app checks your data and flags the ones that disappeared.</p>
    </div>
    <div class="card">
      <div class="icon secure"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="16" r="1"/><rect x="3" y="10" width="18" height="12" rx="2"/><path d="M7 10V7a5 5 0 0 1 10 0v3"/></svg></div>
      <p class="eyebrow">Stays local</p>
      <h3>Your tenant, your machine</h3>
      <p>A container on your computer, your own sign-in, results in your own volume. No backend, no telemetry, tokens in memory only. <a href="{{ '/privacy/' | relative_url }}">Privacy statement</a>.</p>
    </div>
  </div>
</section>

<section class="band">
  <p class="eyebrow">How it works</p>
  <h2>From container to overview in three steps</h2>
  <div class="flow">
    <div>
      <h3>Run the container</h3>
      <p>One <code>docker run</code> or <code>docker compose up</code>. Works with Docker Desktop, Docker Engine, Podman and Rancher Desktop, on x64 and Arm.</p>
    </div>
    <div>
      <h3>Choose your sign-in</h3>
      <p>Recommended: your own single-tenant app registration with read-only delegated permissions (script or portal, no secrets). Quick start: sign in through Microsoft's own apps, no registration needed.</p>
    </div>
    <div>
      <h3>Follow the wizard</h3>
      <p>Open <code>http://localhost:5180</code>, sign in with Microsoft, pick your Sentinel workspaces and collect. Then decide which values to validate.</p>
    </div>
  </div>
</section>

<section class="band split">
  <div>
    <p class="eyebrow">Built for detection engineers</p>
    <h2>What you get in the beta</h2>
    <ul>
      <li>Sentinel analytics rules (scheduled and NRT) and Defender XDR custom detections side by side</li>
      <li>Efficacy from <code>SecurityIncident</code> and Defender XDR incidents, per rule</li>
      <li>Value extraction with lineage through <code>let</code>, joins and unions, and a workbench to mark values as validate or ignore</li>
      <li>Validation against up to 30 days of your own data, through Log Analytics or advanced hunting</li>
      <li>Demo data to explore before you connect a tenant</li>
    </ul>
  </div>
  <div>
    <p class="eyebrow">Good to know</p>
    <h2>Beta limits</h2>
    <ul>
      <li>Read-only: the app never changes rules or incidents.</li>
      <li>Rates are only as good as how consistently incidents are classified.</li>
      <li>Values computed at runtime (watchlists, <code>toscalar()</code>) are listed but not validated.</li>
      <li>Data formats may still change between beta versions.</li>
    </ul>
    <p><a href="https://github.com/{{ site.repository }}/issues">Report an issue or suggest an idea</a>, without tenant data please.</p>
  </div>
</section>
