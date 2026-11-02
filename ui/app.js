// RuleHealth UI: one Alpine component. Talks to the serve API only:
//   GET /api/tenants, GET /api/tenants/{alias}/{doc}, PUT /api/tenants/{alias}/selections,
//   GET|POST /api/tenants/{alias}/jobs, GET /api/jobs/{id}, POST /api/jobs/{id}/cancel,
//   GET /api/tenants/{alias}/validation-plan
'use strict';

const DOCS = ['tenant', 'rules', 'incidents', 'rule-stats', 'candidates', 'selections', 'validation'];
const MIN_N = 5; // rates on fewer classified incidents are flagged "low n"
const MAX_TREND = 8; // series cap for the per-rule line chart: the categorical palette has 8 slots

// Event-type columns where "must always be present" usually makes sense. Highlight only.
const SUGGESTED_COLUMNS = new Set([
  'ActionType', 'OperationName', 'Operation', 'EventID', 'EventName', 'Category', 'ActivityType',
  'LogonType', 'Application', 'AppDisplayName', 'RecordType', 'Workload', 'ResultType',
]);

const OPERATOR_LABELS = {
  eq: '==', eq_ci: '=~', has: 'has', has_cs: 'has_cs', contains: 'contains', contains_cs: 'contains_cs',
  startswith: 'startswith', startswith_cs: 'startswith_cs', endswith: 'endswith', endswith_cs: 'endswith_cs',
  hasprefix: 'hasprefix', hasprefix_cs: 'hasprefix_cs', hassuffix: 'hassuffix', hassuffix_cs: 'hassuffix_cs',
  regex: 'matches regex', like: 'like', like_cs: 'like_cs',
};

const VERDICTS = {
  present: { label: 'Present' },
  absent: { label: 'Absent' },
  'table-empty': { label: 'Table empty' },
  error: { label: 'Query error' },
  skipped: { label: 'Skipped' },
};

const VERDICT_ORDER = ['absent', 'table-empty', 'error', 'skipped', 'present'];

// Brand look per verdict / rule health state: rh-chip modifier + Lucide icon. Text always goes with it.
const VERDICT_LOOK = {
  present: { cls: 'is-present', icon: 'check' },
  absent: { cls: 'is-absent', icon: 'octagon-alert' },
  'table-empty': { cls: 'is-empty', icon: 'database' },
  error: { cls: 'is-error', icon: 'octagon-x' },
  skipped: { cls: 'is-skipped', icon: 'minus' },
};
const HEALTH_LOOK = {
  absent: VERDICT_LOOK.absent,
  'table-empty': VERDICT_LOOK['table-empty'],
  error: VERDICT_LOOK.error,
  // Still fires, but some exclusions do nothing: a warning, not a critical state (red = absent).
  'dead-exclusion': { cls: 'tone-warn is-hollow', icon: 'circle-dashed' },
  'manual-ok': { cls: 'is-present is-hollow', icon: 'badge-check' },        // fine by decision, not by measurement
  ok: VERDICT_LOOK.present,
  pending: { cls: 'is-skipped is-hollow', icon: 'clock' },
  none: { cls: 'is-skipped is-hollow', icon: 'minus' },
  disabled: { cls: 'is-skipped is-hollow', icon: 'circle-off' },
};
// Rules per table: health of each rule on that table, grouped for the stacked bar (worst first,
// then the fine states, then not validated and disabled). `stripes` = same hue, hatched: a state that
// is fine or firing by decision/exception rather than by measurement (manual OK, dead exclusions).
const TABLE_HEALTH_GROUPS = [
  { key: 'absent', label: 'Absent values', short: 'Absent', states: ['absent'], title: 'A value the rule needs on this table did not occur in the window' },
  { key: 'empty', label: 'Table empty', short: 'Empty', states: ['table-empty'], title: 'No rows in this table during the window: connector or retention issue' },
  { key: 'error', label: 'Validation error', short: 'Error', states: ['error'], title: 'The validation query for its values on this table failed' },
  { key: 'dead', label: 'Dead exclusions only', short: 'Dead excl.', states: ['dead-exclusion'], stripes: true, title: 'Only excluded values are absent: the rule still fires' },
  { key: 'manual', label: 'Marked OK manually', short: 'Manual OK', states: ['manual-ok'], stripes: true, title: 'Its absent or empty values on this table were accepted with Mark as OK' },
  { key: 'ok', label: 'All values present', short: 'Present', states: ['ok'], title: 'Every value marked Validate on this table occurred in the window' },
  { key: 'unchecked', label: 'Not validated', short: 'Not validated', states: ['pending', 'none'], title: 'No value of the rule on this table is validated yet, or none is marked Validate (e.g. a table it only joins)' },
  { key: 'disabled', label: 'Disabled rules', short: 'Disabled', states: ['disabled'], title: 'The rule does not run' },
];
const tableHealthGroup = state => (TABLE_HEALTH_GROUPS.find(g => g.states.includes(state)) ?? TABLE_HEALTH_GROUPS[6]).key;

// Needs attention reasons, by rank: absent, noisy, bind errors, dead exclusions, silent.
const REASON_LOOK = [
  { cls: 'is-absent', icon: 'octagon-alert' },
  { cls: 'tone-warn', icon: 'volume-2' },
  { cls: 'is-error', icon: 'code-xml' },
  { cls: 'tone-warn is-hollow', icon: 'circle-dashed' },
  { cls: 'tone-neutral', icon: 'moon' },
];

// ---------------------------------------------------------------- small helpers
const remember = (key, value) => { try { localStorage.setItem(`rulehealth.${key}`, value); } catch { /* storage blocked */ } };
/** Issue categories offered on every rule; the user can add more (stored as plain names). */
const DEFAULT_CATEGORIES = ['Incorrect filtering', 'Missing events', 'Validation needed'];
const NO_CATEGORY = '(none)'; // Rules filter value for rules without a category

/** Sets or (with null) removes body[field][ruleId]; drops the field when it ends up empty. */
function setRuleEntry(body, field, ruleId, value) {
  const all = { ...(body[field] ?? {}) };
  if (value == null) delete all[ruleId]; else all[ruleId] = value;
  if (Object.keys(all).length) body[field] = all; else delete body[field];
}

/** CSV cell: quoted, and text starting with = + - @ is prefixed with ' so Excel does not run it as a formula. */
function csvCell(value) {
  let text = String(value ?? '');
  if (/^[=+\-@\t\r]/.test(text)) text = `'${text}`;
  return `"${text.replace(/"/g, '""')}"`;
}

/** Now as ISO-8601 UTC without milliseconds, like every timestamp the engine writes. */
const isoNow = () => new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
const recall = key => { try { return localStorage.getItem(`rulehealth.${key}`); } catch { return null; } };
const clone = value => JSON.parse(JSON.stringify(value));
const escapeHtml = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const escapeRegex = s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

async function getJson(url) {
  const response = await fetch(url, { cache: 'no-store' });
  if (response.status === 404) return null;
  if (!response.ok) throw new Error(`${url}: HTTP ${response.status}`);
  return response.json();
}

/**
 * The CSRF token of this browser session (GET /api/session also sets the session cookie). Every
 * write sends it in X-DRH-CSRF; the browser adds the Origin header. A page on another site can
 * read neither, so the server refuses its writes.
 */
let sessionRequest = null;
/**
 * GET /api/session: { csrf, signedIn: [{alias, upn, tenantId, signedInAt}], signIn } (never a token).
 * signIn, the result of the last sign-in or consent, is handed out once by the server: the first
 * call after a sign-in gets it, so init() reads it from here.
 */
async function loadSession(renew = false) {
  if (renew || !sessionRequest)
    sessionRequest = getJson('/api/session').catch(error => { sessionRequest = null; throw error; });
  return sessionRequest;
}
async function csrf(renew = false) {
  return (await loadSession(renew))?.csrf ?? '';
}

/**
 * PUT/POST/DELETE to the serve API with the session's CSRF token. A restarted server has forgotten
 * the session: then get a new one and retry once.
 * Throws with the server's `error` text; returns the parsed body (or null for 204).
 */
async function apiSend(method, url, body, retried = false) {
  const response = await fetch(url, {
    method,
    cache: 'no-store',
    headers: { 'Content-Type': 'application/json', 'X-DRH-CSRF': await csrf() },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { /* not JSON */ }
  if (response.status === 403 && !retried && /X-DRH-CSRF/.test(data?.error ?? '')) {
    await csrf(true);
    return apiSend(method, url, body, true);
  }
  if (!response.ok) throw new Error(data?.error ?? `HTTP ${response.status}`);
  return data;
}

/** GET that returns the server's error text instead of throwing on 4xx (for states the UI explains). */
async function getJsonOrProblem(url) {
  const response = await fetch(url, { cache: 'no-store' });
  const data = await response.json().catch(() => null);
  return response.ok ? { data, problem: null } : { data: null, problem: data?.error ?? `HTTP ${response.status}` };
}

/**
 * Rule name for "same rule, other copy" detection: without leading [tags] (deployment prefixes like
 * "[TCCD-Medium-3302]"), whitespace collapsed, case-insensitive. Live 2026-10-19: a disabled Sentinel
 * rule and the enabled custom detection that replaced it differed only by such a prefix.
 */
const normalizedRuleName = name => (name ?? '').replace(/^\s*(\[[^\]]*\]\s*)+/, '').replace(/\s+/g, ' ').trim().toLowerCase();

/** Every rule uses the value only to exclude events (!=, !in, !has...): absent means a dead exclusion, not a broken rule. */
const isExclusionOnly = check => check.occurrences.length > 0 && check.occurrences.every(o => o.negated);

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * A Log Analytics workspace ARM id inside a pasted text: the bare id, the workspace page URL, or a
 * Sentinel blade URL with the id URL-encoded. Same rule as WorkspaceClient.ParseResourceId (server).
 */
function parseWorkspaceId(text) {
  let decoded = (text ?? '').trim();
  try { decoded = decodeURIComponent(decoded); } catch { /* keep as typed */ }
  const m = decoded.match(/\/?subscriptions\/([0-9a-f-]{36})\/resourceGroups\/([^/?#]+)\/providers\/Microsoft\.OperationalInsights\/workspaces\/([^/?#&\s]+)/i);
  return m ? { sub: m[1], rg: m[2], name: m[3] } : null;
}

// Plain-language reasons for comparisons the extraction could not turn into a value check.
const UNRESOLVED_REASONS = {
  RuntimeValue: 'the value is only known when the query runs (a subquery, toscalar or watchlist), so there is no fixed value to check',
  UnboundExpression: 'the compared column could not be traced back to a table',
  ComputedColumn: 'the column is computed by the rule or comes from a function, not read from a table',
};

// The mark the query menu was opened from, to give focus back on Escape. Kept outside Alpine's
// reactive state: a proxied DOM element cannot be focused.
let markMenuOrigin = null;

const JOB_LABELS = {
  refresh: 'Update from tenant', collect: 'Collect from tenant', extract: 'Extract value checks', validate: 'Validate',
};

// Public pages the UI links to (navigation only: nothing is loaded from these hosts).
const DOCS_URL = 'https://cerberus-cyber-consulting.github.io/DetectionRuleHealth';
const REPO_URL = 'https://github.com/Cerberus-Cyber-Consulting/DetectionRuleHealth';

// Setup wizard (#/setup/<step>, skill rulehealth-onboarding). Which step a tenant is at comes from the
// server (setupStep in /api/tenants: derived from its files and the sign-in), never from a state file.
const SETUP_STEPS = [
  { key: 'welcome', label: 'Welcome' },
  { key: 'app', label: 'How to sign in' },
  { key: 'signin', label: 'Sign in' },
  { key: 'workspaces', label: 'Workspaces' },
  { key: 'collect', label: 'Collect' },
  { key: 'done', label: 'Done' },
];

// What the sign-in asks for and why (same table as docs/setup.md → Permissions).
const PERMISSIONS = [
  { api: 'Microsoft Graph', name: 'CustomDetection.Read.All', why: 'read Defender XDR custom detection rules' },
  { api: 'Microsoft Graph', name: 'ThreatHunting.Read.All', why: 'read the advanced hunting schema; validate values in Defender XDR tables' },
  { api: 'Microsoft Graph', name: 'SecurityIncident.Read.All', why: 'read incidents and their classification' },
  { api: 'Microsoft Graph', name: 'SecurityAlert.Read.All', why: 'link the alerts in incidents to custom detection rules' },
  { api: 'Azure Service Management', name: 'user_impersonation', why: 'list subscriptions and Sentinel workspaces; read analytics rules and tables' },
  { api: 'Log Analytics API', name: 'Data.Read', why: 'read SecurityIncident; validate values in workspace tables' },
];

// Job stage rows: icon per state (the state is also spelled out for screen readers).
const STAGE_LOOK = {
  pending: { icon: 'circle-dashed', text: 'Waiting' },
  running: { icon: 'refresh-cw', text: 'Running' },
  succeeded: { icon: 'circle-check', text: 'Succeeded' },
  failed: { icon: 'octagon-x', text: 'Failed' },
  skipped: { icon: 'circle-minus', text: 'Skipped' },
  cancelled: { icon: 'circle-slash', text: 'Cancelled' },
};

const SENTINEL_LOOK = {
  enabled: { cls: 'tone-info', icon: 'shield-check', text: 'Microsoft Sentinel' },
  unknown: { cls: 'tone-neutral', icon: 'shield-question', text: 'Sentinel state unknown' },
  notEnabled: { cls: 'tone-neutral', icon: 'minus', text: 'No Sentinel' },
};

// The two sign-in options (skill rulehealth-auth). tenant.json auth.mode: pkce / microsoft-apps.
const SIGN_IN_LABELS = {
  'own-app': 'Your own app registration',
  'microsoft-apps': "Microsoft's apps (quick start)",
};
/** The wizard's option for a tenant.json: Microsoft's apps, else the own app registration. */
const signInOf = tenant => (tenant?.auth?.mode === 'microsoft-apps' ? 'microsoft-apps' : 'own-app');
/** Which Microsoft app signs in for a permission in the quick start. */
const quickStartApp = api => (api === 'Microsoft Graph' ? 'Microsoft Graph Command Line Tools' : 'Microsoft Azure PowerShell');

const armWorkspaceId = w =>
  `/subscriptions/${w.subscriptionId}/resourceGroups/${w.resourceGroup}/providers/Microsoft.OperationalInsights/workspaces/${w.workspaceName}`;

/** Alias suggestion from a display name: lowercase letters, digits and dashes (the folder name). */
const slug = text => (text ?? '').toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '')
  .replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40);

/** Monday 00:00 UTC of the week containing the date. */
function weekStart(date) {
  const d = new Date(Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()));
  const day = (d.getUTCDay() + 6) % 7;
  d.setUTCDate(d.getUTCDate() - day);
  return d;
}

const classOf = incident =>
  incident.status === 'open' ? 'open'
    : incident.classification === 'TP' ? 'tp'
      : incident.classification === 'BP' ? 'bp'
        : incident.classification === 'FP' ? 'fp'
          : 'undetermined';

document.addEventListener('alpine:init', () => {
  // Lucide icons as a directive: <i x-icon="'shield-check'"></i>. lucide.createIcons() swaps each <i>
  // for an <svg> once, which loses the element inside Alpine templates that re-render; the directive
  // draws the SVG itself and redraws it when the expression changes (e.g. a verdict's icon).
  const pascal = name => String(name).replace(/(^|-)([a-z0-9])/g, (_, __, c) => c.toUpperCase());
  Alpine.directive('icon', (el, { expression }, { effect, evaluateLater }) => {
    const getName = evaluateLater(expression);
    effect(() => getName(name => {
      el.replaceChildren();
      const node = name && lucide.icons[pascal(name)];
      if (!node) {
        if (name) console.warn(`x-icon: no Lucide icon named "${name}"`);
        return;
      }
      const svg = lucide.createElement(node);
      svg.setAttribute('class', 'rh-icon');
      svg.setAttribute('aria-hidden', 'true');
      el.append(svg);
    }));
  });

  Alpine.data('app', () => ({
    // ------------------------------------------------------------ state
    loading: true,
    error: null,
    tenants: [],
    tenant: null,
    docs: {},
    route: { view: 'overview', param: null },

    // derived maps (rebuilt once per tenant load)
    rulesById: {},
    sameNameById: {}, // ruleId → the other rules with the same normalized name (old copies, migrations)
    disabledOnly: new Set(), // checkIds whose every rule is disabled
    statsById: {},
    checksById: {},
    checksByRule: {},
    unresolvedByRule: {},
    summaryByRule: {},
    verdictByCheck: {},

    // working copy of selections.decisions
    decisions: {},
    // ruleId → { state: 'ok', note, decidedAt, coveredCheckIds }: "manual OK" (working copy + saved copy)
    ruleStatuses: {},
    savedRuleStatuses: {},
    okNote: '', // note typed in the rule detail before marking it OK
    okFormOpen: false,
    // Investigation notes live in docs.selections.ruleNotes and are saved right away (not via Save):
    // a note records something that was done, it is not a draft decision.
    noteDrafts: {}, // ruleId → text typed but not added yet
    noteAuthor: recall('noteAuthor') ?? '',
    patching: false, // a notes or categories write (patchSelections) is running
    categoryError: null,
    categoryFormOpen: false,
    noCategory: NO_CATEGORY,
    tableChartLimit: 20, // Overview "Rules per table": bars shown (Show all lifts it)
    newCategory: '',
    noteError: null,
    savedDecisions: {},
    touched: {}, // checks decided in this session stay visible under "unreviewed only"
    saving: false,
    saveMessage: null,
    saveError: null,

    // Noise = (BP + FP) / classified (owner's decision 2026-10-31: benign positives are noise too).
    // One threshold for the noise map, the Noisy reason in Needs attention and the rule detail tile.
    noiseThreshold: 0.5,
    perRulePercent: false,
    showSilent: false,
    showDerived: false,

    // Incidents-per-rule-over-time chart. slots: ruleId → colour slot 1..8, kept while the rule stays
    // selected so adding/removing other rules never repaints it (colour follows the rule, not its rank).
    trend: { selected: [], slots: {}, unit: 'week', filter: '' },
    showTable: {},
    expanded: {},
    markMenu: null, // right-click menu on a highlighted comparison in the rule query



    // Data view: background jobs (collect / extract / validate) and the validation dry run.
    job: null, // latest job of this tenant as returned by the server (state, log, summary, error)
    jobTimer: null,
    jobError: null,
    jobNote: null,
    plan: null,
    planError: null,
    planLoading: false,
    opts: { incidentDays: 90, saveRaw: false, validationDays: 30, includeBasic: false },

    // Sign-in: who is signed in (from /api/session, no tokens) and the result of the last sign-in,
    // handed out once by the server and shown until dismissed.
    session: { signedIn: [] },
    signInNotice: null,
    appInfo: null, // /api/app: version, update check, data folder

    // Setup wizard (#/setup/<step>). alias = the tenant being set up (after the app step).
    setup: {
      alias: null,
      form: { signIn: 'own-app', tenant: '', clientId: '', displayName: '', alias: '', aliasTouched: false },
      submitted: false,
      saving: false,
      error: null,
      lookup: { busy: false, input: '', tenantId: null, error: null },
      discover: { busy: false, loaded: false, alias: null, error: null, status: null, result: null, selected: {}, none: false, manual: '', manualIds: [] },
      collect: { incidentDays: 90, validationDays: 30, extract: true },
      copied: null,
    },

    // Settings page (#/settings) for the current tenant.
    settingsForm: { displayName: '', incidentDays: 90, validationDays: 30 },
    settingsMessage: null,
    settingsError: null,
    removeConfirm: '',
    removeOpen: false,

    // Default: worst health first, so rules that need attention are at the top.
    rulesFilter: { text: '', source: '', enabled: '', health: '', category: '', table: '', errors: false, sort: 'status', dir: 1 },
    checksFilter: { unreviewed: true, validatable: true, table: '', column: '', text: '', absent: false, exclusion: '', enabledRules: true, suggested: false },

    // ------------------------------------------------------------ lifecycle
    async init() {
      const threshold = Number(recall('noiseThreshold'));
      if (threshold > 0 && threshold < 1) this.noiseThreshold = threshold;
      // The exclusion-only filter sticks across reloads (hiding them is a standing preference).
      const exclusion = recall('exclusionFilter');
      if (exclusion === 'hide' || exclusion === 'only') this.checksFilter.exclusion = exclusion;
      this.$watch('checksFilter.exclusion', value => remember('exclusionFilter', value));
      if (recall('enabledRulesFilter') === 'false') this.checksFilter.enabledRules = false;
      this.$watch('checksFilter.enabledRules', value => remember('enabledRulesFilter', String(value)));

      window.addEventListener('hashchange', () => this.applyHash());
      // ECharts measures axis labels when it draws; drawn before Montserrat/JetBrains Mono loaded,
      // long labels get clipped. Draw again once the brand fonts are in.
      document.fonts?.ready.then(() => this.renderCharts());
      // The menu is positioned in the viewport; scrolling the page would detach it from its mark.
      window.addEventListener('scroll', () => this.closeMarkMenu(), { passive: true });
      window.addEventListener('beforeunload', e => {
        if (this.dirty) { e.preventDefault(); e.returnValue = ''; }
      });

      try {
        const [tenants, session] = await Promise.all([getJson('/api/tenants'), loadSession()]);
        this.tenants = tenants ?? [];
        this.session = session ?? { signedIn: [] };
        this.signInNotice = session?.signIn ?? null;
      } catch (err) {
        this.error = `Could not reach the Detection Rule Health server: ${err.message}`;
        this.loading = false;
        return;
      }
      getJson('/api/app').then(info => { this.appInfo = info; }).catch(() => { /* footer only */ });

      // The tenant the wizard was working on (the sign-in round trip reloads the page).
      const setupAlias = this.signInNotice?.alias ?? recall('setupTenant');
      if (this.tenants.some(t => t.alias === setupAlias && !t.demo)) this.setup.alias = setupAlias;

      if (location.hash === '#/add-tenant') history.replaceState(null, '', '#/setup/app'); // pre-M8 bookmark
      if (!location.hash) history.replaceState(null, '', this.startRoute());
      this.parseHash();

      const remembered = this.route.view === 'setup' && this.setup.alias ? this.setup.alias : recall('tenant');
      const alias = (this.tenants.find(t => t.alias === remembered) ?? this.tenants.find(t => !t.demo) ?? this.tenants[0])?.alias;
      if (alias) await this.loadTenant(alias);
      this.loading = false;
      await this.enterView();
      this.renderCharts();
    },

    /**
     * Where a page load without a hash starts: the wizard while no real tenant is set up (unless the
     * user chose the demo data), the wizard step of a tenant that is halfway, else the last route.
     */
    startRoute() {
      const real = this.tenants.filter(t => !t.demo);
      if (!real.length) return recall('explored') === '1' && this.tenants.length ? '#/overview' : '#/setup/welcome';
      const halfway = real.find(t => t.alias === (this.setup.alias ?? recall('tenant')) && t.setupStep !== 'done')
        ?? (real.length === 1 && real[0].setupStep !== 'done' ? real[0] : null);
      if (halfway) {
        this.setup.alias = halfway.alias;
        return `#/setup/${halfway.setupStep}`;
      }
      const lastRoute = recall('route');
      return lastRoute && lastRoute.startsWith('#/') && !lastRoute.startsWith('#/setup') ? lastRoute : '#/overview';
    },

    async loadTenant(alias) {
      if (this.dirty && !confirm('You have unsaved decisions. Switch tenant and discard them?')) {
        this.$nextTick(() => { this.$refs.tenantSelect.value = this.tenant; });
        return;
      }
      this.error = null;
      this.saveMessage = null;
      this.saveError = null;
      try {
        // Only the documents the tenant has (a fresh tenant has just tenant.json): no 404s in the console.
        const files = this.tenants.find(t => t.alias === alias)?.files;
        const loaded = await Promise.all(DOCS.map(doc => !files || files.includes(doc)
          ? getJson(`/api/tenants/${encodeURIComponent(alias)}/${doc}`)
          : Promise.resolve(null)));
        const docs = {};
        DOCS.forEach((doc, i) => { docs[doc] = loaded[i]; });
        const switched = this.tenant !== alias;
        this.tenant = alias;
        this.docs = docs;
        this.buildMaps();
        remember('tenant', alias);
        if (switched) await this.resetDataView();
      } catch (err) {
        this.error = `Could not load tenant '${alias}': ${err.message}`;
      }
      this.renderCharts();
    },

    buildMaps() {
      const rules = this.docs.rules?.rules ?? [];
      const candidates = this.docs.candidates;
      this.rulesById = Object.fromEntries(rules.map(r => [r.ruleId, r]));

      const byName = {};
      for (const rule of rules) (byName[normalizedRuleName(rule.displayName)] ??= []).push(rule);
      this.sameNameById = Object.fromEntries(rules
        .map(r => [r.ruleId, byName[normalizedRuleName(r.displayName)].filter(o => o.ruleId !== r.ruleId)])
        .filter(([, others]) => others.length));

      // Rules missing from rules.json do not count as disabled (unknown is not "off"), like the engine.
      this.disabledOnly = new Set((candidates?.checks ?? [])
        .filter(c => { const ids = [...new Set(c.occurrences.map(o => o.ruleId))]; return ids.length && ids.every(id => this.rulesById[id] && !this.rulesById[id].enabled); })
        .map(c => c.checkId));
      this.statsById = Object.fromEntries((this.docs['rule-stats']?.rules ?? []).map(s => [s.ruleId, s]));
      this.checksById = Object.fromEntries((candidates?.checks ?? []).map(c => [c.checkId, c]));

      const byRule = {};
      for (const check of candidates?.checks ?? []) {
        for (const occurrence of check.occurrences) {
          (byRule[occurrence.ruleId] ??= new Set()).add(check.checkId);
        }
      }
      this.checksByRule = Object.fromEntries(Object.entries(byRule).map(([k, v]) => [k, [...v]]));

      const unresolved = {};
      for (const u of candidates?.unresolved ?? []) (unresolved[u.ruleId] ??= []).push(u);
      this.unresolvedByRule = unresolved;
      this.summaryByRule = Object.fromEntries((candidates?.ruleSummaries ?? []).map(s => [s.ruleId, s]));
      this.verdictByCheck = Object.fromEntries((this.docs.validation?.results ?? []).map(r => [r.checkId, r]));

      this.decisions = clone(this.docs.selections?.decisions ?? {});
      this.savedDecisions = clone(this.decisions);
      this.ruleStatuses = clone(this.docs.selections?.ruleStatuses ?? {});
      this.savedRuleStatuses = clone(this.ruleStatuses);
      this.touched = {};
      this.noteDrafts = {};
      this.expanded = {};
      this.initTrend();
    },

    // ------------------------------------------------------------ routing
    parseHash() {
      const parts = location.hash.replace(/^#\/?/, '').split('/');
      const view = ['overview', 'rules', 'checks', 'validation', 'data', 'setup', 'settings'].includes(parts[0]) ? parts[0] : 'overview';
      const param = parts.length > 1 ? decodeURIComponent(parts.slice(1).join('/')) : null;
      this.route = { view, param };
      remember('route', location.hash);
    },

    async applyHash() {
      this.markMenu = null;
      this.okFormOpen = false;
      this.noteError = null;
      this.categoryError = null;
      this.categoryFormOpen = false;
      this.parseHash();
      window.scrollTo(0, 0);
      await this.enterView();
      this.renderCharts();
    },

    /** What a view needs when it opens: the wizard step's tenant and data, the settings form. */
    async enterView() {
      if (this.route.view === 'settings') {
        this.fillSettingsForm();
        return;
      }
      if (this.route.view !== 'setup') return;
      const step = this.setupStep;
      // Steps after the app registration work on one tenant: the one being set up.
      if (!['welcome', 'app'].includes(step)) {
        if (!this.setup.alias || !this.tenants.some(t => t.alias === this.setup.alias)) {
          history.replaceState(null, '', '#/setup/app');
          this.parseHash();
          return;
        }
        if (this.tenant !== this.setup.alias) await this.loadTenant(this.setup.alias);
      }
      if (step === 'app') this.fillAppForm();
      if (step === 'workspaces' && (!this.setup.discover.loaded || this.setup.discover.alias !== this.setup.alias)) await this.discoverWorkspaces();
      if (step === 'collect') {
        const d = this.docs.tenant?.defaults ?? {};
        this.setup.collect.incidentDays = [30, 90, 180].includes(d.incidentDays) ? d.incidentDays : 90;
        this.setup.collect.validationDays = Math.min(d.validationDays ?? 30, 30);
      }
    },

    ruleHref: ruleId => `#/rules/${encodeURIComponent(ruleId)}`,
    go(hash) { location.hash = hash; },

    // ------------------------------------------------------------ charts
    renderCharts() {
      this.$nextTick(() => {
        RHCharts.disposeDetached();
        if (!this.docs.rules) return;
        if (this.route.view === 'overview') {
          if (this.$refs.healthPie && this.ruleHealthBreakdown.total) {
            const breakdown = this.ruleHealthBreakdown;
            RHCharts.renderChart(this.$refs.healthPie, RHCharts.healthPieOption(breakdown.groups, breakdown.total),
              p => this.showHealthGroupInRules(breakdown.groups[p.dataIndex]));
          }
          if (this.$refs.tableChart && this.tableRows.length) {
            const rows = this.tableRows.slice(0, this.tableChartLimit);
            RHCharts.renderChart(this.$refs.tableChart, RHCharts.tableOption(rows, this.tableHealthGroups),
              p => this.showTableInRules(rows[rows.length - 1 - p.dataIndex].table));
          }
          if (this.categorizedCount) {
            const groups = this.categoryReport;
            RHCharts.renderChart(this.$refs.categoryChart, RHCharts.categoryOption(groups),
              p => this.showCategoryInRules(groups[groups.length - 1 - p.dataIndex].name));
          }
          if (this.perRuleRows.length) {
            RHCharts.renderChart(this.$refs.perRuleChart, RHCharts.perRuleOption(this.perRuleRows, this.perRulePercent),
              p => this.go(this.ruleHref(this.perRuleRows[this.perRuleRows.length - 1 - p.dataIndex].ruleId)));
          }
          if (this.noisePoints.length) {
            RHCharts.renderChart(this.$refs.noiseChart, RHCharts.noiseOption(this.noisePoints, this.noiseThreshold),
              p => this.go(this.ruleHref(p.data.row.ruleId)));
          }
          if (this.weeklyRows.length) RHCharts.renderChart(this.$refs.weeklyChart, RHCharts.weeklyOption(this.weeklyRows));
          if (this.$refs.trendChart && this.trendSeries.length) {
            const series = this.trendSeries;
            RHCharts.renderChart(this.$refs.trendChart,
              RHCharts.ruleTrendOption(this.trendBuckets.map(b => b.label), series, this.trend.unit === 'week' ? 'Week of' : 'Day'),
              p => this.go(this.ruleHref(series[p.seriesIndex].ruleId)));
          }
        }
        if (this.route.view === 'rules' && this.route.param && this.ruleWeeklyRows.length) {
          RHCharts.renderChart(this.$refs.ruleWeeklyChart, RHCharts.weeklyOption(this.ruleWeeklyRows));
        }
      });
    },

    setThreshold(value) {
      const v = Math.min(95, Math.max(5, Number(value) || 50)) / 100;
      this.noiseThreshold = v;
      remember('noiseThreshold', String(v));
      this.renderCharts();
    },

    toggleTable(key) {
      this.showTable[key] = !this.showTable[key];
      this.renderCharts();
    },

    // ------------------------------------------------------------ formatting
    fmtInt: n => (n == null ? '—' : Number(n).toLocaleString('en-US')),
    fmtPct: (num, den) => (den ? `${Math.round((num / den) * 100)} %` : '—'),
    fmtDate: iso => (iso ? iso.slice(0, 10) : '—'),
    fmtDateTime: iso => (iso ? `${iso.slice(0, 10)} ${iso.slice(11, 16)} UTC` : '—'),
    opLabel: cls => OPERATOR_LABELS[cls] ?? cls,
    verdictInfo: v => VERDICTS[v] ?? { label: v },
    verdictLook: v => VERDICT_LOOK[v] ?? VERDICT_LOOK.skipped,
    unresolvedReason: status => UNRESOLVED_REASONS[status] ?? status,
    healthLook: key => HEALTH_LOOK[key] ?? HEALTH_LOOK.none,
    reasonLook: rank => REASON_LOOK[rank] ?? REASON_LOOK[4],
    /** Verdict chip text: the label plus the evidence (count, last seen). */
    verdictText(result) {
      if (result.verdict === 'present') return `Present · ${this.fmtInt(result.matchCount)} · last ${this.fmtDate(result.lastSeenAt)}`;
      if (result.verdict === 'absent' && this.docs.validation) return `Absent · ${this.docs.validation.windowDays} days`;
      if (result.verdict === 'table-empty') return 'Table empty · connector?';
      if (result.verdict === 'error') return 'Query error';
      return this.verdictInfo(result.verdict).label;
    },
    sourceLabel: s => ({ sentinel: 'Microsoft Sentinel', defender: 'Defender XDR', markdown: 'Markdown' }[s] ?? s),
    /** Short source for tags in dense tables (the specimen's tags read SENTINEL / DEFENDER). */
    sourceTag: s => ({ sentinel: 'Sentinel', defender: 'Defender', markdown: 'Markdown' }[s] ?? s),
    /** Rule kind as people say it (the APIs return identifiers such as customDetection). */
    kindLabel: k => ({ customDetection: 'Custom detection', Scheduled: 'Scheduled', NRT: 'Near real-time', Fusion: 'Fusion', MicrosoftSecurityIncidentCreation: 'Incident creation', MLBehaviorAnalytics: 'ML behavior analytics', ThreatIntelligence: 'Threat intelligence' }[k] ?? k),
    /** Display name; disabled rules say so wherever they are listed, so an old copy is never mistaken for the live rule. */
    ruleName(ruleId) {
      const rule = this.rulesById[ruleId];
      if (!rule) return ruleId;
      return rule.enabled ? rule.displayName : `${rule.displayName} (disabled)`;
    },

    /** "FP 62 % (31/50)": never a rate without its count. */
    fpText(stats) {
      if (!stats) return '—';
      const n = stats.tp + stats.bp + stats.fp;
      return n ? `FP ${this.fmtPct(stats.fp, n)} (${stats.fp}/${n})` : '—';
    },

    /** "BP + FP 62 % (31/50: BP 10, FP 21)": the noise rate with its parts. */
    noiseText(stats) {
      if (!stats) return '—';
      const n = stats.tp + stats.bp + stats.fp;
      return n ? `BP + FP ${this.fmtPct(stats.bp + stats.fp, n)} (${stats.bp + stats.fp}/${n}: BP ${stats.bp}, FP ${stats.fp})` : '—';
    },

    // ------------------------------------------------------------ banner / header
    get isDemo() {
      return Object.values(this.docs).some(doc => doc && doc.demo === true);
    },
    get tenantName() {
      return this.docs.tenant?.displayName ?? this.tenant;
    },
    /** Topbar title for the current route. */
    get viewTitle() {
      if (this.route.view === 'rules') return this.route.param ? 'Rule detail' : 'Rules';
      return { overview: 'Overview', checks: 'Value checks', validation: 'Validation', data: 'Data', setup: 'Connect a tenant', settings: 'Settings' }[this.route.view] ?? 'Detection Rule Health';
    },
    /** Topbar status chip: how old the collected rules are. */
    get dataAge() {
      const at = this.docs.rules?.generatedAt;
      return at && this.tenant && this.route.view !== 'setup' ? this.fmtDate(at) : '';
    },
    /** The current tenant's entry in /api/tenants (demo, readOnly, signedIn, setupStep). */
    get tenantInfo() {
      return this.tenants.find(t => t.alias === this.tenant) ?? null;
    },
    get isReadOnly() { return !!this.tenantInfo?.readOnly; },
    /** A real tenant (not demo, not offline markdown) without a sign-in in this server process. */
    get signedOut() {
      const t = this.tenantInfo;
      return !!t && !t.demo && !t.offline && !t.signedIn && !this.needsReconnect;
    },
    /**
     * Added before M8 with the Azure CLI (modes cli, cli-graph) or without a client id: the server
     * cannot sign it in (TenantAuth). It has to be connected again with the user's own app
     * registration; the alias, data and decisions stay.
     */
    get needsReconnect() {
      const t = this.tenantInfo, auth = this.docs.tenant?.auth;
      if (!t || t.demo || t.offline || !auth) return false;
      return !(auth.mode === 'pkce' || auth.mode === 'microsoft-apps' || (auth.mode === 'browser' && auth.clientId));
    },
    signedInAs(alias) {
      return this.session.signedIn?.find(s => s.alias === alias) ?? null;
    },
    /** Top-level navigation to Microsoft's sign-in page; the server sends the browser back to `next`. */
    signIn(alias, next = 'data', consent = false) {
      if (this.dirty) { this.saveError = 'Save your value-check decisions first: signing in leaves this page.'; return; }
      location.href = `/auth/login?tenant=${encodeURIComponent(alias)}&next=${encodeURIComponent(next)}${consent ? '&consent=1' : ''}`;
    },
    signInLabel: option => SIGN_IN_LABELS[option] ?? option,
    signInOf: tenant => signInOf(tenant),
    quickStartApp: api => quickStartApp(api),
    /** The current tenant signs in through Microsoft's apps (quick start). */
    get isQuickStart() { return signInOf(this.docs.tenant) === 'microsoft-apps'; },
    /**
     * Entra admin consent for every permission on the tenant's own app registration; returns to the
     * sign-in step. Null for Microsoft's apps: /adminconsent does not work for Microsoft's own apps, an
     * administrator consents by signing in with the consent prompt instead (signIn(…, true)).
     */
    adminConsentUrl(tenantDoc = this.docs.tenant, next = 'setup') {
      if (!tenantDoc?.tenantId || !tenantDoc?.auth?.clientId || signInOf(tenantDoc) !== 'own-app') return null;
      return `https://login.microsoftonline.com/${encodeURIComponent(tenantDoc.tenantId)}/adminconsent`
        + `?client_id=${encodeURIComponent(tenantDoc.auth.clientId)}`
        + `&redirect_uri=${encodeURIComponent(`${location.origin}/auth/callback`)}`
        + `&state=${encodeURIComponent(next)}`;
    },
    async copyText(text, key) {
      try {
        await navigator.clipboard.writeText(text);
        this.setup.copied = key;
        setTimeout(() => { if (this.setup.copied === key) this.setup.copied = null; }, 2000);
      } catch {
        prompt('Copy this:', text);
      }
    },
    get redirectUri() { return `${location.origin}/auth/callback`; },
    /**
     * The upgrade for a container user: pull, remove, run again with the same command as the README
     * (the named volume keeps the data). Beta users stay on :beta; stable users on :latest.
     */
    get upgradeDocker() {
      const tag = (this.appInfo?.update?.latest ?? '').includes('-') ? 'beta' : 'latest';
      const image = `ghcr.io/cerberus-cyber-consulting/detectionrulehealth:${tag}`;
      return `docker pull ${image}\n`
        + 'docker rm -f detectionrulehealth\n'
        + 'docker run -d --name detectionrulehealth \\\n'
        + '  -p 127.0.0.1:5180:5180 -v drh-data:/data \\\n'
        + '  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \\\n'
        + `  --restart unless-stopped ${image}`;
    },
    docsUrl: page => `${DOCS_URL}/${page}`,
    repoUrl: page => `${REPO_URL}/${page}`,
    /** Sidebar footer, under the tenant switcher: short tenant id and workspace(s), in mono. */
    get tenantIdLine() {
      const t = this.docs.tenant;
      if (!t) return '';
      if (t.auth?.mode === 'offline') return 'offline · markdown rules';
      const id = t.tenantId ? `${t.tenantId.slice(0, 4)}…${t.tenantId.slice(-4)}` : '';
      const ws = t.workspaces ?? [];
      const wsText = ws.length ? `ws ${ws[0].workspaceName}${ws.length > 1 ? ` +${ws.length - 1}` : ''}` : 'Defender only';
      return [id, wsText].filter(Boolean).join(' · ');
    },
    /** Sidebar badge on Validation. */
    get absentCount() {
      return (this.docs.validation?.results ?? []).filter(r => r.verdict === 'absent').length;
    },

    // ------------------------------------------------------------ overview
    get kpis() {
      const rules = this.docs.rules?.rules ?? [];
      const incidents = this.docs.incidents?.incidents ?? [];
      const closed = incidents.filter(i => i.status === 'closed');
      const verdicted = closed.filter(i => ['TP', 'BP', 'FP'].includes(i.classification));
      const validating = Object.entries(this.decisions).filter(([id, d]) => d.state === 'validate' && this.checksById[id]).length;
      const results = this.docs.validation?.results ?? [];
      const absent = results.filter(r => r.verdict === 'absent').length;
      const bySource = s => rules.filter(r => r.enabled && r.source === s).length;
      const split = [['Microsoft Sentinel', bySource('sentinel')], ['Defender XDR', bySource('defender')], ['Markdown', bySource('markdown')]]
        .filter(([, n]) => n).map(([name, n]) => `${name} ${n}`).join(' · ');
      return [
        { label: 'Enabled rules', icon: 'list-checks', tone: 'tone-info', value: `${rules.filter(r => r.enabled).length} / ${rules.length}`, context: split || 'no rules collected' },
        {
          label: this.docs.incidents ? `Incidents, ${this.docs.incidents.window.days} days` : 'Incidents', icon: 'siren', tone: 'tone-neutral',
          value: this.docs.incidents ? this.fmtInt(incidents.length) : '—',
          context: this.docs.incidents ? `${this.fmtInt(closed.length)} closed` : 'Not collected yet',
        },
        {
          label: 'Closed with a classification', icon: 'circle-check-big', tone: 'tone-neutral',
          value: this.docs.incidents ? this.fmtPct(verdicted.length, closed.length) : '—',
          context: this.docs.incidents ? `${verdicted.length} of ${closed.length} closed as TP, BP or FP` : '',
        },
        { label: 'Checks marked validate', icon: 'shield-check', tone: 'tone-secure', value: this.fmtInt(validating), context: `of ${this.fmtInt(this.docs.candidates?.checks.length ?? 0)} extracted values` },
        {
          label: 'Absent values', icon: 'octagon-alert', tone: absent ? 'tone-crit' : 'tone-neutral',
          value: this.docs.validation ? this.fmtInt(absent) : '—',
          context: this.docs.validation ? `of ${results.length} validated · ${this.docs.validation.windowDays} days` : 'Not validated yet',
        },
      ];
    },

    get perRuleRows() {
      return (this.docs['rule-stats']?.rules ?? [])
        .filter(s => s.incidents > 0)
        .sort((a, b) => b.incidents - a.incidents)
        .slice(0, 20)
        .map(s => {
          const n = s.tp + s.bp + s.fp;
          const name = this.ruleName(s.ruleId);
          return {
            ruleId: s.ruleId, name, label: n < MIN_N ? `${name} · low n` : name,
            tp: s.tp, bp: s.bp, fp: s.fp, undetermined: s.undetermined, open: s.open, total: s.incidents, lowN: n < MIN_N,
          };
        });
    },

    get noisePoints() {
      return (this.docs['rule-stats']?.rules ?? [])
        .map(s => ({ ...s, n: s.tp + s.bp + s.fp }))
        .filter(s => s.n >= MIN_N)
        .map(s => ({ ruleId: s.ruleId, name: this.ruleName(s.ruleId), incidents: s.incidents, bp: s.bp, fp: s.fp, n: s.n,
          noise: s.bp + s.fp, noiseRate: (s.bp + s.fp) / s.n }));
    },

    weeklyFor(incidents) {
      const range = this.docs.incidents?.window;
      if (!range) return [];
      const weeks = new Map();
      for (let d = weekStart(new Date(range.from)); d <= new Date(range.to); d.setUTCDate(d.getUTCDate() + 7)) {
        const key = d.toISOString().slice(0, 10);
        weeks.set(key, { key, label: key.slice(5), tp: 0, bp: 0, fp: 0, undetermined: 0, open: 0, total: 0 });
      }
      for (const incident of incidents) {
        const week = weeks.get(weekStart(new Date(incident.createdAt)).toISOString().slice(0, 10));
        if (!week) continue;
        week[classOf(incident)]++;
        week.total++;
      }
      return [...weeks.values()];
    },

    // ------------------------------------------------------------ incidents per rule over time

    /** Default selection: the remembered one for this tenant, else the top 5 rules by incidents. */
    initTrend() {
      let remembered = [];
      try { remembered = JSON.parse(recall(`trend.${this.tenant}`) ?? '[]'); } catch { remembered = []; }
      const withIncidents = new Set(this.trendCandidatesAll.map(r => r.ruleId));
      const selected = Array.isArray(remembered) ? remembered.filter(id => withIncidents.has(id)).slice(0, MAX_TREND) : [];
      this.trend = { selected: [], slots: {}, unit: recall('trend.unit') === 'day' ? 'day' : 'week', filter: '' };
      if (selected.length) selected.forEach(id => this.addTrendRule(id));
      else this.trendTop(5);
    },

    /** Rules with at least one incident in the window, most incidents first. */
    get trendCandidatesAll() {
      return (this.docs['rule-stats']?.rules ?? [])
        .filter(s => s.incidents > 0)
        .sort((a, b) => b.incidents - a.incidents || this.ruleName(a.ruleId).localeCompare(this.ruleName(b.ruleId)))
        .map(s => ({ ruleId: s.ruleId, name: this.ruleName(s.ruleId), incidents: s.incidents }));
    },
    get trendCandidates() {
      const text = this.trend.filter.trim().toLowerCase();
      return this.trendCandidatesAll.filter(r => !text || r.name.toLowerCase().includes(text));
    },

    addTrendRule(ruleId) {
      if (this.trend.selected.includes(ruleId) || this.trend.selected.length >= MAX_TREND) return;
      const used = new Set(Object.values(this.trend.slots));
      const slot = [1, 2, 3, 4, 5, 6, 7, 8].find(s => !used.has(s));
      this.trend.slots[ruleId] = slot;
      this.trend.selected.push(ruleId);
    },
    toggleTrendRule(ruleId) {
      if (this.trend.selected.includes(ruleId)) {
        this.trend.selected = this.trend.selected.filter(id => id !== ruleId);
        delete this.trend.slots[ruleId];
      } else {
        this.addTrendRule(ruleId);
      }
      this.trendChanged();
    },
    trendTop(n) {
      this.trend.selected = [];
      this.trend.slots = {};
      this.trendCandidatesAll.slice(0, n).forEach(r => this.addTrendRule(r.ruleId));
      this.trendChanged();
    },
    trendClear() {
      this.trend.selected = [];
      this.trend.slots = {};
      this.trendChanged();
    },
    setTrendUnit(unit) {
      this.trend.unit = unit;
      remember('trend.unit', unit);
      this.renderCharts();
    },
    trendChanged() {
      if (this.tenant) remember(`trend.${this.tenant}`, JSON.stringify(this.trend.selected));
      this.renderCharts();
    },
    get maxTrend() { return MAX_TREND; },
    /** Line slot (1–8) of a rule in the per-rule chart; 0 when not selected. */
    trendSlot(ruleId) {
      return this.trend.slots[ruleId] ?? 0;
    },

    /** Time buckets over the incident window: Monday-start weeks or days, UTC. */
    get trendBuckets() {
      const range = this.docs.incidents?.window;
      if (!range) return [];
      const step = this.trend.unit === 'day' ? 1 : 7;
      const first = this.trend.unit === 'day' ? new Date(range.from.slice(0, 10) + 'T00:00:00Z') : weekStart(new Date(range.from));
      const buckets = [];
      for (let d = first; d <= new Date(range.to); d = new Date(d.getTime() + step * 86400000)) {
        const key = d.toISOString().slice(0, 10);
        buckets.push({ key, label: key.slice(5) });
      }
      return buckets;
    },

    /** One series per selected rule, in selection order: incidents created per bucket. */
    get trendSeries() {
      const buckets = this.trendBuckets;
      if (!buckets.length || !this.trend.selected.length) return [];
      const index = new Map(buckets.map((b, i) => [b.key, i]));
      const keyOf = date => (this.trend.unit === 'day' ? date : weekStart(date)).toISOString().slice(0, 10);
      const counts = Object.fromEntries(this.trend.selected.map(id => [id, new Array(buckets.length).fill(0)]));
      for (const incident of this.docs.incidents?.incidents ?? []) {
        const i = index.get(keyOf(new Date(incident.createdAt)));
        if (i === undefined) continue;
        for (const id of incident.ruleIds) if (counts[id]) counts[id][i]++;
      }
      return this.trend.selected.map(id => ({ ruleId: id, name: this.ruleName(id), slot: this.trend.slots[id], data: counts[id] }));
    },

    get weeklyRows() {
      return this.weeklyFor(this.docs.incidents?.incidents ?? []);
    },

    /** Per rule, everything that needs a look; ordered by how urgent it is. */
    get attentionRows() {
      const hasIncidents = !!this.docs.incidents;
      const rows = [];
      for (const rule of this.docs.rules?.rules ?? []) {
        if (!rule.enabled) continue; // a disabled rule does not run: nothing about it is urgent
        const info = this.ruleHealth(rule.ruleId);
        const stats = this.statsById[rule.ruleId];
        const reasons = [];
        // A manual OK accepts the value problems (absent, dead exclusions); noise, bind errors and silence still count.
        const manualOk = this.ruleStatus(rule, info).key === 'manual-ok';
        if (info.absent && !manualOk) reasons.push({ rank: 0, text: `${info.absent} absent value${info.absent > 1 ? 's' : ''}` });
        if (stats && info.n >= MIN_N && info.noiseRate >= this.noiseThreshold) reasons.push({ rank: 1, text: `Noisy · ${this.noiseText(stats)}` });
        if (info.bindErrors) reasons.push({ rank: 2, text: `${info.bindErrors} bind error${info.bindErrors > 1 ? 's' : ''}` });
        if (info.deadExclusions && !manualOk) reasons.push({ rank: 3, text: `${info.deadExclusions} dead exclusion${info.deadExclusions > 1 ? 's' : ''}` });
        const silent = hasIncidents && rule.enabled && (stats?.incidents ?? 0) === 0;
        if (silent) reasons.push({ rank: 4, text: 'Silent · no incidents in the window', silent: true });
        if (reasons.length) rows.push({ rule, reasons, rank: Math.min(...reasons.map(r => r.rank)), onlySilent: reasons.every(r => r.silent) });
      }
      return rows.sort((a, b) => a.rank - b.rank || a.rule.displayName.localeCompare(b.rule.displayName));
    },
    get attentionVisible() {
      return this.attentionRows.filter(r => this.showSilent || !r.onlySilent);
    },
    get silentCount() {
      return this.attentionRows.filter(r => r.onlySilent).length;
    },

    // ------------------------------------------------------------ per-rule health
    /** Health counts of a rule's values; with `table`, only its values on that table (Rules per table). */
    ruleHealth(ruleId, table = null) {
      const all = this.checksByRule[ruleId] ?? [];
      const checkIds = table ? all.filter(id => this.checksById[id]?.table === table) : all;
      let validate = 0, absent = 0, deadExclusions = 0, unreviewed = 0, tableEmpty = 0, errors = 0, present = 0;
      const emptyTables = new Set();
      const problemIds = []; // what a "manual OK" covers: absent, dead exclusion, table-empty, error
      for (const id of checkIds) {
        const check = this.checksById[id];
        const decision = this.decisions[id]?.state;
        if (decision === 'validate') validate++;
        if (!decision && check.validatable) unreviewed++;
        const verdict = this.verdictByCheck[id]?.verdict;
        if (verdict === 'absent' || verdict === 'table-empty' || verdict === 'error') problemIds.push(id);
        if (verdict === 'absent') {
          const own = check.occurrences.filter(o => o.ruleId === ruleId);
          if (own.every(o => o.negated)) deadExclusions++; else absent++;
        } else if (verdict === 'table-empty') { tableEmpty++; emptyTables.add(check.table); }
        else if (verdict === 'error') errors++;
        else if (verdict === 'present' && decision === 'validate') present++;
      }
      const stats = this.statsById[ruleId];
      const n = stats ? stats.tp + stats.bp + stats.fp : 0;
      return {
        checks: checkIds.length, validate, absent, deadExclusions, unreviewed, tableEmpty, errors, present,
        emptyTables: [...emptyTables], problemIds,
        bindErrors: this.summaryByRule[ruleId]?.bindErrors.length ?? 0,
        n, fpRate: n ? stats.fp / n : null, noiseRate: n ? (stats.bp + stats.fp) / n : null,
      };
    },

    /**
     * One health state per rule for the rules page, worst first (rank). Color is the verdict color of
     * the Validation view and always comes with a text label.
     * absent: a value the rule needs no longer occurs, so it can no longer fire.
     * table-empty: a table it reads had no rows in the window (connector or retention problem).
     * dead-exclusion: only exclusions are absent; the rule still fires, an exclusion does nothing.
     */
    ruleStatus(rule, info) {
      const plural = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
      if (!rule.enabled) return { key: 'disabled', rank: 8, label: 'Disabled', title: 'The rule does not run; validation skips values only it uses' };

      // Manual OK: the user accepted the problem values listed at the time. Only while every current
      // problem is one of them; a new absent value shows the real status again (with `okExpired`).
      const manual = this.ruleStatuses[rule.ruleId];
      if (manual && info.problemIds.length) {
        const covered = new Set(manual.coveredCheckIds);
        const fresh = info.problemIds.filter(id => !covered.has(id)).length;
        if (!fresh) {
          return {
            key: 'manual-ok', rank: 4, label: 'Manual OK', manual,
            title: `Marked OK manually${manual.decidedAt ? ` on ${this.fmtDate(manual.decidedAt)}` : ' (not saved yet)'}` +
              `${manual.note ? `: ${manual.note}` : ''}. Accepts ${plural(info.problemIds.length, 'value')} that were absent, empty or failing then.`,
          };
        }
        const real = this.ruleStatus(rule, { ...info, problemIds: [] }); // the status without the override
        return { ...real, okExpired: { manual, fresh }, title: `${real.title} · Marked OK earlier, but ${plural(fresh, 'new problem value')} since.` };
      }
      if (info.absent) return { key: 'absent', rank: 0, label: plural(info.absent, 'absent value'), title: 'A value the rule needs did not occur in the validation window: the rule can no longer fire on it' };
      if (info.tableEmpty) return { key: 'table-empty', rank: 1, label: 'Table empty', title: `No rows in ${info.emptyTables.join(', ')} during the window: connector or retention problem` };
      if (info.errors) return { key: 'error', rank: 2, label: 'Validation error', title: 'The validation query for some of its values failed; see the Validation view' };
      if (info.deadExclusions) return { key: 'dead-exclusion', rank: 3, label: plural(info.deadExclusions, 'dead exclusion'), title: 'Only excluded values are absent: the rule still fires, but those exclusions do nothing' };
      if (info.validate && info.present === info.validate) return { key: 'ok', rank: 5, label: 'All present', title: 'Every value marked Validate occurred in the window' };
      if (info.validate) return { key: 'pending', rank: 6, label: 'Not validated yet', title: 'Some values marked Validate have no result yet: run validation in the Data view' };
      return { key: 'none', rank: 7, label: 'No values selected', title: 'No value of this rule is marked Validate' };
    },

    /** Rules per health state, for the summary strip above the rules table (all rules, before filters). */
    /** Name of a group of rules by health (strip chips and the Health select): fixed text, so it never
     *  depends on which rule happened to be counted first ("absent value" vs "absent values"). */
    healthGroupLabel: key => ({
      absent: 'With absent values', 'table-empty': 'With an empty table', error: 'With validation errors',
      'dead-exclusion': 'With dead exclusions only', 'manual-ok': 'Marked OK manually', ok: 'With all values present',
      pending: 'Not validated yet', none: 'No values selected', disabled: 'Disabled',
    }[key] ?? key),
    get ruleStatusCounts() {
      const counts = {};
      for (const rule of this.docs.rules?.rules ?? []) {
        const status = this.ruleStatus(rule, this.ruleHealth(rule.ruleId));
        (counts[status.key] ??= { ...status, count: 0 }).count++;
      }
      return Object.values(counts).sort((a, b) => a.rank - b.rank);
    },
    /** Health of the rule shown in the rule detail (with its manual OK, if any). */
    get currentRuleStatus() {
      const rule = this.currentRule;
      return rule ? this.ruleStatus(rule, this.ruleHealth(rule.ruleId)) : null;
    },

    /** Manual OK: accept the rule's current problem values; Save keeps it. */
    markRuleOk(ruleId) {
      this.ruleStatuses[ruleId] = {
        state: 'ok', note: this.okNote.trim() || null, decidedAt: null,
        coveredCheckIds: [...this.ruleHealth(ruleId).problemIds],
      };
      this.okNote = '';
      this.okFormOpen = false;
      this.saveMessage = null;
    },
    removeRuleOk(ruleId) {
      delete this.ruleStatuses[ruleId];
      this.saveMessage = null;
    },

    // ------------------------------------------------------------ investigation notes
    get ruleNotes() {
      return this.docs.selections?.ruleNotes ?? {};
    },
    /** Notes of one rule, newest first. */
    notesFor(ruleId) {
      return [...(this.ruleNotes[ruleId] ?? [])].reverse();
    },
    /** Badge in the rule tables: count + the latest note as tooltip; null without notes. */
    notesBadge(ruleId) {
      const notes = this.ruleNotes[ruleId];
      if (!notes?.length) return null;
      const last = notes[notes.length - 1];
      const text = last.text.length > 200 ? `${last.text.slice(0, 200)}…` : last.text;
      return {
        label: `${notes.length} note${notes.length === 1 ? '' : 's'}`,
        title: `Latest note, ${this.fmtDateTime(last.createdAt)}${last.author ? ` by ${last.author}` : ''}:\n${text}`,
      };
    },
    async addRuleNote(ruleId) {
      const text = (this.noteDrafts[ruleId] ?? '').trim();
      if (!text || this.patching) return;
      const author = this.noteAuthor.trim();
      remember('noteAuthor', author);
      const note = {
        id: `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`,
        text, author: author || null, createdAt: isoNow(),
      };
      if (await this.saveRuleNotes(ruleId, [...(this.ruleNotes[ruleId] ?? []), note])) this.noteDrafts[ruleId] = '';
    },
    async deleteRuleNote(ruleId, noteId) {
      if (!confirm('Delete this note? It is removed from the track record.')) return;
      await this.saveRuleNotes(ruleId, (this.ruleNotes[ruleId] ?? []).filter(n => n.id !== noteId));
    },
    async saveRuleNotes(ruleId, notes) {
      this.noteError = null;
      const error = await this.patchSelections(body => setRuleEntry(body, 'ruleNotes', ruleId, notes.length ? notes : null));
      if (error) this.noteError = `Saving the note failed: ${error}`;
      return !error;
    },

    /**
     * Saves a change that does not wait for Save (notes, categories): the PUT body is the last
     * *saved* selections document with `mutate` applied, so unsaved value decisions and manual OKs
     * stay unsaved (and keep counting as unsaved changes). Returns an error message or null.
     */
    async patchSelections(mutate) {
      if (this.patching || this.saving) return 'another save is still running, try again';
      this.patching = true;
      const now = isoNow();
      const body = this.docs.selections
        ? clone(this.docs.selections)
        : { schemaVersion: 1, tenant: this.tenant, updatedAt: now, decisions: {} };
      body.updatedAt = now;
      mutate(body, now);
      try {
        await apiSend('PUT', `/api/tenants/${encodeURIComponent(this.tenant)}/selections`, body);
        this.docs.selections = body;
        return null;
      } catch (err) {
        return err.message;
      } finally {
        this.patching = false;
      }
    },

    // ------------------------------------------------------------ issue categories (reporting)
    get ruleCategories() {
      return this.docs.selections?.ruleCategories ?? {};
    },
    categoriesOf(ruleId) {
      return this.ruleCategories[ruleId]?.categories ?? [];
    },
    /** The default categories first, then custom ones in use (alphabetical). */
    get categoryNames() {
      const used = new Set(Object.values(this.ruleCategories).flatMap(a => a.categories));
      const custom = [...used].filter(c => !DEFAULT_CATEGORIES.includes(c)).sort((a, b) => a.localeCompare(b));
      return [...DEFAULT_CATEGORIES, ...custom];
    },
    async setRuleCategories(ruleId, categories) {
      this.categoryError = null;
      const error = await this.patchSelections((body, now) =>
        setRuleEntry(body, 'ruleCategories', ruleId, categories.length ? { categories, updatedAt: now } : null));
      if (error) this.categoryError = `Saving the category failed: ${error}`;
      return !error;
    },
    toggleCategory(ruleId, name) {
      const current = this.categoriesOf(ruleId);
      return this.setRuleCategories(ruleId, current.includes(name) ? current.filter(c => c !== name) : [...current, name]);
    },
    /** "+ New category": reuses an existing name that differs only in case. */
    async addCustomCategory(ruleId) {
      const typed = this.newCategory.trim().replace(/\s+/g, ' ').slice(0, 60);
      if (!typed) return;
      const name = this.categoryNames.find(c => c.toLowerCase() === typed.toLowerCase()) ?? typed;
      const current = this.categoriesOf(ruleId);
      if (current.includes(name) || await this.setRuleCategories(ruleId, [...current, name])) {
        this.newCategory = '';
        this.categoryFormOpen = false;
      }
    },

    /** Overview report: one group per category with its rules (defaults are shown even when empty). */
    get categoryReport() {
      const rules = this.docs.rules?.rules ?? [];
      return this.categoryNames.map(name => {
        const rows = rules.filter(r => this.categoriesOf(r.ruleId).includes(name)).map(rule => {
          const notes = this.ruleNotes[rule.ruleId] ?? [];
          return {
            rule, stats: this.statsById[rule.ruleId] ?? null,
            status: this.ruleStatus(rule, this.ruleHealth(rule.ruleId)),
            lastNote: notes.length ? notes[notes.length - 1] : null,
            since: this.ruleCategories[rule.ruleId].updatedAt,
          };
        }).sort((a, b) => a.status.rank - b.status.rank || a.rule.displayName.localeCompare(b.rule.displayName));
        return { name, rows, enabled: rows.filter(r => r.rule.enabled).length };
      }).filter(g => g.rows.length || DEFAULT_CATEGORIES.includes(g.name));
    },
    /** Distinct rules with at least one category (a rule in two categories counts once). */
    get categorizedCount() {
      const ids = new Set((this.docs.rules?.rules ?? []).map(r => r.ruleId));
      return Object.keys(this.ruleCategories).filter(id => ids.has(id)).length;
    },
    // ------------------------------------------------------------ rules per table (Overview)
    /**
     * Tables a rule reads: ruleSummaries[].tables from extraction. Files extracted before that field
     * existed fall back to the tables of the rule's value checks (incomplete: a table the rule only
     * joins or unions without a literal comparison is missed).
     */
    tablesOf(ruleId) {
      const tables = this.summaryByRule[ruleId]?.tables;
      if (tables) return tables;
      return [...new Set((this.checksByRule[ruleId] ?? []).map(id => this.checksById[id]?.table).filter(Boolean))].sort();
    },
    get tablesFromChecksOnly() {
      return !!this.docs.candidates && (this.docs.candidates.ruleSummaries ?? []).some(s => !s.tables);
    },
    /** One row per table: rules reading it, split by enabled and source; empty in the last validation. */
    get tableRows() {
      const byTable = {};
      const verdicts = {};
      for (const r of this.docs.validation?.results ?? []) (verdicts[r.table] ??= new Set()).add(r.verdict);
      for (const rule of this.docs.rules?.rules ?? []) {
        if (!rule.queryText) continue;
        for (const table of this.tablesOf(rule.ruleId)) {
          const row = byTable[table] ??= {
            table, enabled: 0, disabled: 0, sentinel: 0, defender: 0, other: 0, ruleIds: [],
            health: Object.fromEntries(TABLE_HEALTH_GROUPS.map(g => [g.key, 0])),
          };
          row[rule.enabled ? 'enabled' : 'disabled']++;
          if (rule.enabled) row[rule.source === 'sentinel' ? 'sentinel' : rule.source === 'defender' ? 'defender' : 'other']++;
          row.ruleIds.push(rule.ruleId);
          // Health of this rule judged on its values for THIS table only (an absent value in another
          // table it reads does not count here); a manual OK counts when it covers them.
          row.health[tableHealthGroup(this.ruleStatus(rule, this.ruleHealth(rule.ruleId, table)).key)]++;
        }
      }
      return Object.values(byTable).map(row => {
        const seen = verdicts[row.table];
        // A table is "empty" when validation found no rows at all; one present value proves rows exist.
        return { ...row, total: row.enabled + row.disabled, empty: !!seen?.has('table-empty') && !seen.has('present') };
      }).sort((a, b) => b.enabled - a.enabled || b.total - a.total || a.table.localeCompare(b.table));
    },
    /**
     * Overview donut: every rule once, by its rule-wide health (the Rules page status), in the same
     * groups and colours as Rules per table. Empty groups are left out.
     */
    get ruleHealthBreakdown() {
      const rules = this.docs.rules?.rules ?? [];
      const counts = Object.fromEntries(TABLE_HEALTH_GROUPS.map(g => [g.key, 0]));
      for (const rule of rules) counts[tableHealthGroup(this.ruleStatus(rule, this.ruleHealth(rule.ruleId)).key)]++;
      return {
        total: rules.length,
        groups: TABLE_HEALTH_GROUPS.filter(g => counts[g.key]).map(g => ({ ...g, count: counts[g.key] })),
      };
    },
    /** The single rule status a health group stands for right now (a group may hold two); null if several. */
    healthGroupState(group) {
      const used = group.states.filter(s => this.ruleStatusCounts.some(c => c.key === s));
      return used.length === 1 ? used[0] : null;
    },
    /** Donut / legend click: Rules filtered to that health state (when the group is one state). */
    showHealthGroupInRules(group) {
      const state = this.healthGroupState(group);
      if (!state) return;
      this.rulesFilter.health = state;
      this.go('#/rules');
    },
    /** Health groups that occur in Rules per table (legend, table columns); empty groups are left out. */
    get tableHealthGroups() {
      const rows = this.tableRows;
      return TABLE_HEALTH_GROUPS.filter(g => rows.some(r => r.health[g.key]));
    },
    /** Rules with a query that read no known table (bind errors, a DCR `source`, functions only). */
    get rulesWithoutTable() {
      return (this.docs.rules?.rules ?? []).filter(r => r.queryText && this.summaryByRule[r.ruleId] && !this.tablesOf(r.ruleId).length);
    },
    showTableInRules(table) {
      this.rulesFilter.table = table;
      this.go('#/rules');
    },

    showCategoryInRules(name) {
      this.rulesFilter.category = name;
      this.go('#/rules');
    },
    /** One line per rule × category, for reporting in Excel. */
    exportCategoryCsv() {
      const head = ['Category', 'Rule', 'Rule id', 'Source', 'Enabled', 'Health', 'Incidents', 'TP', 'BP', 'FP',
        'Categorised at (UTC)', 'Latest note', 'Latest note at (UTC)', 'Latest note by'];
      const lines = this.categoryReport.flatMap(g => g.rows.map(r => [
        g.name, r.rule.displayName, r.rule.ruleId, this.sourceLabel(r.rule.source), r.rule.enabled ? 'yes' : 'no',
        r.status.label, r.stats?.incidents ?? '', r.stats?.tp ?? '', r.stats?.bp ?? '', r.stats?.fp ?? '',
        this.fmtDateTime(r.since).replace(' UTC', ''), r.lastNote?.text ?? '',
        r.lastNote ? this.fmtDateTime(r.lastNote.createdAt).replace(' UTC', '') : '', r.lastNote?.author ?? '',
      ]));
      const csv = [head, ...lines].map(row => row.map(csvCell).join(',')).join('\r\n');
      // BOM so Excel reads UTF-8; one download, nothing leaves the machine.
      const url = URL.createObjectURL(new Blob(['﻿', csv], { type: 'text/csv;charset=utf-8' }));
      const a = Object.assign(document.createElement('a'), { href: url, download: `rulehealth-${this.tenant}-issue-categories-${isoNow().slice(0, 10)}.csv` });
      document.body.append(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    toggleHealthFilter(key) {
      this.rulesFilter.health = this.rulesFilter.health === key ? '' : key;
    },

    // ------------------------------------------------------------ rules list
    get ruleRows() {
      const f = this.rulesFilter;
      const text = f.text.trim().toLowerCase();
      const rows = (this.docs.rules?.rules ?? []).map(rule => {
        const stats = this.statsById[rule.ruleId];
        const health = this.ruleHealth(rule.ruleId);
        const status = this.ruleStatus(rule, health);
        return {
          rule, stats, health, status,
          sort: {
            // Worst state first; within a state, the count that state is about (absent values for
            // "absent", ...), then incidents. Mixing counts made a 1-absent rule outrank 4-absent ones.
            status: status.rank * 1e6 - Math.min(({ absent: health.absent, 'table-empty': health.tableEmpty, error: health.errors,
              'dead-exclusion': health.deadExclusions, ok: health.present })[status.key] ?? 0, 999) * 1e3 - Math.min(stats?.incidents ?? 0, 999),
            name: rule.displayName.toLowerCase(), source: rule.source, enabled: rule.enabled ? 1 : 0,
            incidents: stats?.incidents ?? -1, fp: health.n >= 1 ? health.fpRate : -1,
            checks: health.validate * 1000 + health.checks, absent: health.absent, errors: health.bindErrors,
          },
        };
      }).filter(r =>
        (!text || r.rule.displayName.toLowerCase().includes(text) || r.rule.ruleId.toLowerCase().includes(text)
          || (this.ruleNotes[r.rule.ruleId] ?? []).some(n => n.text.toLowerCase().includes(text)))
        && (!f.source || r.rule.source === f.source)
        && (f.enabled === '' || String(r.rule.enabled) === f.enabled)
        && (!f.health || r.status.key === f.health)
        && (!f.table || this.tablesOf(r.rule.ruleId).includes(f.table))
        && (!f.category || (f.category === NO_CATEGORY ? !this.categoriesOf(r.rule.ruleId).length : this.categoriesOf(r.rule.ruleId).includes(f.category)))
        && (!f.errors || r.health.bindErrors > 0));
      const key = f.sort;
      return rows.sort((a, b) => {
        const x = a.sort[key], y = b.sort[key];
        const cmp = typeof x === 'string' ? x.localeCompare(y) : x - y;
        return cmp * f.dir || a.sort.name.localeCompare(b.sort.name);
      });
    },
    get sources() {
      return [...new Set((this.docs.rules?.rules ?? []).map(r => r.source))];
    },
    sortBy(key) {
      if (this.rulesFilter.sort === key) this.rulesFilter.dir *= -1;
      else { this.rulesFilter.sort = key; this.rulesFilter.dir = key === 'name' || key === 'source' || key === 'status' ? 1 : -1; }
    },
    /** Lucide icon for a sortable column header ('' when the table is not sorted by it). */
    sortIcon(key) {
      return this.rulesFilter.sort === key ? (this.rulesFilter.dir > 0 ? 'chevron-up' : 'chevron-down') : 'chevrons-up-down';
    },
    /** aria-sort only on the sorted column (WAI-ARIA sortable table pattern). */
    ariaSort(key) {
      return this.rulesFilter.sort === key ? (this.rulesFilter.dir > 0 ? 'ascending' : 'descending') : null;
    },
    /** Rules list: Enabled column text. */
    enabledText: rule => (rule.status === 'autoDisabled' ? 'Auto-disabled' : (rule.enabled ? 'Enabled' : 'Disabled')),

    // ------------------------------------------------------------ rule detail
    get currentRule() {
      return this.route.view === 'rules' && this.route.param ? this.rulesById[this.route.param] ?? null : null;
    },
    get ruleWeeklyRows() {
      const id = this.route.param;
      return this.weeklyFor((this.docs.incidents?.incidents ?? []).filter(i => i.ruleIds.includes(id)));
    },
    get currentRuleChecks() {
      const rule = this.currentRule;
      if (!rule) return [];
      // Validatable values first: derived ones can be numerous (expression lineage over-approximates).
      return (this.checksByRule[rule.ruleId] ?? []).map(id => this.checkRow(this.checksById[id], rule.ruleId))
        .sort((a, b) => (b.check.validatable - a.check.validatable) || a.check.table.localeCompare(b.check.table)
          || a.check.column.localeCompare(b.check.column) || a.check.value.localeCompare(b.check.value));
    },
    get currentRuleDerivedCount() {
      return this.currentRuleChecks.filter(r => !r.check.validatable).length;
    },

    /** The query with every extracted comparison marked by decision/verdict, unresolved ones dotted. */
    get highlightedQuery() {
      const rule = this.currentRule;
      const text = rule?.queryText ?? '';
      if (!text) return '';
      const byExpression = new Map();
      for (const id of this.checksByRule[rule.ruleId] ?? []) {
        const check = this.checksById[id];
        for (const o of check.occurrences) {
          if (o.ruleId !== rule.ruleId) continue;
          (byExpression.get(o.expression) ?? byExpression.set(o.expression, new Map()).get(o.expression)).set(id, check);
        }
      }
      const marks = [];
      // attrs: data-checks (space-separated checkIds, hex only) or data-unresolved (the reason), used by the
      // right-click menu; marks are focusable so the menu also opens from the keyboard.
      const find = (expression, cls, title, attrs = '') => {
        const pattern = expression.split(' ').filter(Boolean).map(escapeRegex).join('\\s+');
        if (!pattern) return;
        for (const m of text.matchAll(new RegExp(pattern, 'g'))) marks.push({ start: m.index, end: m.index + m[0].length, cls, title, attrs });
      };
      for (const [expression, checks] of byExpression) {
        const states = [...checks.keys()].map(id => this.verdictByCheck[id]?.verdict === 'absent' ? 'absent'
          : this.decisions[id]?.state === 'validate' ? (this.verdictByCheck[id]?.verdict === 'present' ? 'present' : 'validate')
            : this.decisions[id]?.state === 'ignore' ? 'ignore' : 'unreviewed');
        // Partly absent: some selected values of this comparison occurred, others did not (owner's request
        // 2026-10-26: a has_any list with 4 present and 8 absent looked like it was entirely absent).
        const cls = states.includes('absent') && states.includes('present') ? 'partial'
          : ['absent', 'unreviewed', 'validate', 'present', 'ignore'].find(s => states.includes(s));
        const counts = Object.entries(states.reduce((acc, s) => ({ ...acc, [s]: (acc[s] ?? 0) + 1 }), {}))
          .map(([s, n]) => `${n} ${s}`).join(', ');
        const meaning = cls === 'partial' ? ' · partly absent: some values the rule needs no longer occur' : '';
        find(expression, `rh-hl is-${cls}`, `${checks.size} value${checks.size > 1 ? 's' : ''}: ${counts}${meaning} · right-click to decide`,
          ` data-checks="${escapeHtml([...checks.keys()].join(' '))}" tabindex="0"`);
      }
      for (const u of this.unresolvedByRule[rule.ruleId] ?? []) {
        const reason = `${UNRESOLVED_REASONS[u.status] ?? u.status}${u.detail ? ` (${u.detail})` : ''}`;
        find(u.expression, 'rh-hl is-unresolved', reason, ` data-unresolved="${escapeHtml(reason)}" tabindex="0"`);
      }

      marks.sort((a, b) => a.start - b.start || (b.end - b.start) - (a.end - a.start));
      let html = '', pos = 0;
      for (const m of marks) {
        if (m.start < pos) continue; // overlapping: keep the outer/earlier mark
        html += escapeHtml(text.slice(pos, m.start))
          + `<mark class="${m.cls}" title="${escapeHtml(m.title)}"${m.attrs}>${escapeHtml(text.slice(m.start, m.end))}</mark>`;
        pos = m.end;
      }
      return html + escapeHtml(text.slice(pos));
    },

    // ------------------------------------------------------------ query right-click menu
    /**
     * Right-click (or Menu key / Shift+F10 / Enter on a focused mark) on a highlighted comparison in the
     * rule query opens a menu with the decision control for every value in it. Anywhere else the
     * browser's own menu stays, so copying query text keeps working.
     */
    openMarkMenu(event) {
      const mark = event.target.closest?.('mark[data-checks], mark[data-unresolved]');
      if (!mark) return;
      if (event.type === 'keydown') {
        const opens = event.key === 'ContextMenu' || event.key === 'Enter' || (event.key === 'F10' && event.shiftKey);
        if (!opens) return;
      }
      event.preventDefault();
      const rect = mark.getBoundingClientRect();
      const fromKeyboard = event.type === 'keydown';
      this.markMenu = {
        ids: (mark.dataset.checks ?? '').split(' ').filter(id => this.checksById[id]),
        unresolved: mark.dataset.unresolved ?? null,
        expression: mark.textContent,
        left: fromKeyboard ? rect.left : event.clientX,
        top: fromKeyboard ? rect.bottom + 4 : event.clientY,
      };
      markMenuOrigin = mark;
      this.$nextTick(() => {
        // Keep the menu inside the viewport, then focus its first control.
        const menu = this.$refs.markMenu;
        if (!menu || !this.markMenu) return;
        const margin = 8;
        this.markMenu.left = Math.max(margin, Math.min(this.markMenu.left, window.innerWidth - menu.offsetWidth - margin));
        this.markMenu.top = Math.max(margin, Math.min(this.markMenu.top, window.innerHeight - menu.offsetHeight - margin));
        // First visible decision control (the "All values" row is hidden for a single value), else Close.
        const visible = [...menu.querySelectorAll('.ctx-all button, .ctx-row .decision button')].find(b => b.offsetParent);
        (visible ?? menu.querySelector('button'))?.focus();
      });
    },

    closeMarkMenu(restoreFocus = false) {
      if (!this.markMenu) return;
      this.markMenu = null;
      if (restoreFocus && markMenuOrigin) {
        // A decision re-renders the query, replacing the mark: find its successor by the same check ids.
        const key = markMenuOrigin.dataset.checks ?? markMenuOrigin.dataset.unresolved;
        const target = markMenuOrigin.isConnected ? markMenuOrigin
          : [...document.querySelectorAll('pre.rh-query mark')].find(m => (m.dataset.checks ?? m.dataset.unresolved) === key);
        target?.focus();
      }
      markMenuOrigin = null;
    },

    /** The menu's values as workbench rows (from this rule's point of view), validatable first. */
    get markMenuRows() {
      return (this.markMenu?.ids ?? [])
        .map(id => this.checkRow(this.checksById[id], this.currentRule?.ruleId))
        .sort((a, b) => (b.check.validatable - a.check.validatable) || a.check.value.localeCompare(b.check.value)
          || a.check.table.localeCompare(b.check.table));
    },
    /**
     * Distinct table.column targets of the menu's values. A comparison after a union (SigninLogs +
     * AADNonInteractiveUserSignInLogs) has one check per table and value, each with its own verdict,
     * so the rows must say which table they are about.
     */
    get markMenuTargets() {
      return [...new Set(this.markMenuRows.map(r => `${r.check.table}.${r.check.path ?? r.check.column}`))];
    },

    setMarkMenuAll(state) {
      for (const row of this.markMenuRows) if (row.check.validatable) this.setDecision(row.id, state);
    },

    // ------------------------------------------------------------ workbench
    /** View model for one check row, optionally from the point of view of one rule. */
    checkRow(check, forRuleId = null) {
      const ruleIds = [...new Set(check.occurrences.map(o => o.ruleId))];
      const own = forRuleId ? check.occurrences.filter(o => o.ruleId === forRuleId) : check.occurrences;
      return {
        check, id: check.checkId, ruleIds,
        state: this.decisions[check.checkId]?.state ?? null,
        verdict: this.verdictByCheck[check.checkId] ?? null,
        exclusionOnly: isExclusionOnly(check),
        disabledOnly: this.disabledOnly.has(check.checkId),
        ownExpressions: [...new Set(own.map(o => o.expression))],
      };
    },

    get tableOptions() {
      return [...new Set((this.docs.candidates?.checks ?? []).map(c => c.table))].sort();
    },
    get columnOptions() {
      const { table: t, suggested } = this.checksFilter;
      return [...new Set((this.docs.candidates?.checks ?? [])
        .filter(c => (!t || c.table === t) && (!suggested || SUGGESTED_COLUMNS.has(c.column))).map(c => c.column))].sort();
    },

    /** "Suggested columns only" on: a column picked before that is not suggested would show nothing. */
    toggleSuggested() {
      if (this.checksFilter.suggested && this.checksFilter.column && !SUGGESTED_COLUMNS.has(this.checksFilter.column)) this.checksFilter.column = '';
    },

    get filteredChecks() {
      const f = this.checksFilter;
      const text = f.text.trim().toLowerCase();
      return (this.docs.candidates?.checks ?? []).filter(c =>
        (!f.unreviewed || !this.decisions[c.checkId] || this.touched[c.checkId])
        && (!f.validatable || c.validatable)
        && (!f.table || c.table === f.table)
        && (!f.column || c.column === f.column)
        && (!f.suggested || SUGGESTED_COLUMNS.has(c.column))
        && (!f.absent || this.verdictByCheck[c.checkId]?.verdict === 'absent')
        && (f.exclusion !== 'only' || isExclusionOnly(c))
        && (f.exclusion !== 'hide' || !isExclusionOnly(c))
        && (!f.enabledRules || !this.disabledOnly.has(c.checkId))
        && (!text || c.value.toLowerCase().includes(text) || c.column.toLowerCase().includes(text) || c.table.toLowerCase().includes(text)));
    },

    /** Table → Column → values: "always present" is a property of event-type columns, not of rules. */
    get checkGroups() {
      const tables = new Map();
      for (const check of this.filteredChecks) {
        const table = tables.get(check.table) ?? tables.set(check.table, { table: check.table, columns: new Map(), count: 0 }).get(check.table);
        const column = table.columns.get(check.column)
          ?? table.columns.set(check.column, { key: `${check.table}.${check.column}`, column: check.column, rows: [] }).get(check.column);
        column.rows.push(this.checkRow(check));
        table.count++;
      }
      return [...tables.values()].sort((a, b) => a.table.localeCompare(b.table)).map(t => ({
        ...t,
        columns: [...t.columns.values()].sort((a, b) =>
          (SUGGESTED_COLUMNS.has(b.column) - SUGGESTED_COLUMNS.has(a.column)) || a.column.localeCompare(b.column))
          .map(c => ({ ...c, rows: c.rows.sort((a, b) => a.check.value.localeCompare(b.check.value) || a.check.operatorClass.localeCompare(b.check.operatorClass)) })),
      }));
    },

    isSuggested: column => SUGGESTED_COLUMNS.has(column),

    /**
     * Validatable values that every rule uses only to exclude events (!=, !in, !has...). Split into
     * unreviewed and already decided: the bulk "Ignore" only touches the unreviewed ones, because
     * validating an exclusion is a deliberate way to find dead exclusions.
     */
    get exclusionOnlyChecks() {
      const all = (this.docs.candidates?.checks ?? []).filter(c => c.validatable && isExclusionOnly(c));
      return {
        unreviewed: all.filter(c => !this.decisions[c.checkId]),
        decided: all.filter(c => this.decisions[c.checkId]),
      };
    },

    ignoreExclusionOnly() {
      for (const check of this.exclusionOnlyChecks.unreviewed) this.setDecision(check.checkId, 'ignore');
    },

    // Values only used by disabled rules need no review (validate skips them), so the counters leave them out.
    get unreviewedCount() {
      return (this.docs.candidates?.checks ?? [])
        .filter(c => c.validatable && !this.decisions[c.checkId] && !this.disabledOnly.has(c.checkId)).length;
    },
    get reviewProgress() {
      const all = (this.docs.candidates?.checks ?? []).filter(c => c.validatable && !this.disabledOnly.has(c.checkId));
      const done = all.filter(c => this.decisions[c.checkId]).length;
      const disabled = (this.docs.candidates?.checks ?? []).filter(c => c.validatable && this.disabledOnly.has(c.checkId)).length;
      return { done, total: all.length, disabled };
    },

    setDecision(checkId, state) {
      const check = this.checksById[checkId];
      if (check && !check.validatable) return; // derived values cannot be validated in v1, so no decision either
      this.touched[checkId] = true;
      this.saveMessage = null;
      if (!state) { delete this.decisions[checkId]; return; }
      const previous = this.decisions[checkId];
      this.decisions[checkId] = {
        state,
        note: previous?.note ?? null,
        decidedAt: previous?.decidedAt ?? null,
        check: previous?.check ?? (check ? { table: check.table, column: check.column, path: check.path, operatorClass: check.operatorClass, value: check.value } : null),
      };
    },

    /** Applies to the visible rows of one column only, so hidden decisions are never overwritten. */
    setColumn(column, state) {
      for (const row of column.rows) {
        if (!row.check.validatable) continue;
        this.setDecision(row.id, state);
      }
    },

    /** v / i / u on a focused row, arrows to move. */
    onRowKey(event, checkId) {
      const key = event.key.toLowerCase();
      const map = { v: 'validate', i: 'ignore', u: null };
      if (key in map) {
        event.preventDefault();
        this.setDecision(checkId, map[key]);
        this.focusSibling(event.currentTarget, 1);
      } else if (key === 'arrowdown' || key === 'j') {
        event.preventDefault(); this.focusSibling(event.currentTarget, 1);
      } else if (key === 'arrowup' || key === 'k') {
        event.preventDefault(); this.focusSibling(event.currentTarget, -1);
      }
    },
    focusSibling(row, step) {
      const rows = [...document.querySelectorAll('.check-row')];
      const next = rows[rows.indexOf(row) + step];
      if (next) next.focus();
    },

    resetFilters() {
      this.checksFilter = { unreviewed: true, validatable: true, table: '', column: '', text: '', absent: false, exclusion: '', enabledRules: true, suggested: false };
      this.touched = {};
      this.noteDrafts = {};
    },

    get orphanedDecisions() {
      return Object.entries(this.decisions)
        .filter(([id]) => !this.checksById[id])
        .map(([id, d]) => ({ id, ...d }));
    },
    deleteOrphan(id) {
      delete this.decisions[id];
      this.saveMessage = null;
    },

    // ------------------------------------------------------------ saving
    get dirty() {
      return this.changedCount > 0;
    },
    /** Changed value decisions plus changed "manual OK" rule statuses. */
    get changedCount() {
      const keys = new Set([...Object.keys(this.decisions), ...Object.keys(this.savedDecisions)]);
      const decisions = [...keys].filter(k => this.decisions[k]?.state !== this.savedDecisions[k]?.state).length;
      const ruleKeys = new Set([...Object.keys(this.ruleStatuses), ...Object.keys(this.savedRuleStatuses)]);
      const statuses = [...ruleKeys].filter(k => !this.sameRuleStatus(this.ruleStatuses[k], this.savedRuleStatuses[k])).length;
      return decisions + statuses;
    },
    sameRuleStatus(a, b) {
      if (!a || !b) return a === b;
      return a.note === b.note && [...a.coveredCheckIds].sort().join() === [...b.coveredCheckIds].sort().join();
    },

    async save() {
      if (!this.dirty || this.saving || this.patching) return;
      this.saving = true;
      this.saveError = null;
      const now = isoNow();
      const decisions = {};
      for (const [id, d] of Object.entries(this.decisions)) {
        const saved = this.savedDecisions[id];
        if (saved && saved.state === d.state) { decisions[id] = clone(saved); continue; }
        const candidate = this.checksById[id];
        decisions[id] = {
          state: d.state,
          note: d.note ?? null,
          decidedAt: now,
          check: candidate
            ? { table: candidate.table, column: candidate.column, path: candidate.path, operatorClass: candidate.operatorClass, value: candidate.value }
            : d.check,
        };
      }
      // Manual OK: unchanged entries keep their decidedAt, changed or new ones get now.
      const ruleStatuses = {};
      for (const [ruleId, s] of Object.entries(this.ruleStatuses)) {
        const saved = this.savedRuleStatuses[ruleId];
        ruleStatuses[ruleId] = this.sameRuleStatus(s, saved) ? clone(saved) : { ...clone(s), decidedAt: now };
      }
      const body = { schemaVersion: 1, tenant: this.tenant, updatedAt: now, decisions };
      if (Object.keys(ruleStatuses).length) body.ruleStatuses = ruleStatuses;
      // Notes and categories are saved when they change (patchSelections): carry the saved copies over.
      if (this.docs.selections?.ruleCategories) body.ruleCategories = clone(this.docs.selections.ruleCategories);
      if (this.docs.selections?.ruleNotes) body.ruleNotes = clone(this.docs.selections.ruleNotes);
      if (this.docs.selections?.demo) body.demo = true; // keep the sample's demo flag
      try {
        await apiSend('PUT', `/api/tenants/${encodeURIComponent(this.tenant)}/selections`, body);
        this.docs.selections = body;
        this.decisions = clone(decisions);
        this.savedDecisions = clone(decisions);
        this.ruleStatuses = clone(ruleStatuses);
        this.savedRuleStatuses = clone(ruleStatuses);
        this.plan = null; // the reviewed queries no longer match the selection
        this.saveMessage = `Decisions saved (${Object.keys(decisions).length}). Run validation in Data to check the selected values.`;
      } catch (err) {
        this.saveError = `Save failed: ${err.message}`;
      } finally {
        this.saving = false;
      }
    },

    // ------------------------------------------------------------ data view (jobs)
    get isOffline() { return this.docs.tenant?.auth?.mode === 'offline'; },
    get jobRunning() { return this.job?.state === 'running'; },
    jobLabel: kind => JOB_LABELS[kind] ?? kind,
    /** Job state chip: text and rh-chip modifier (failed is critical, cancelled is neutral). */
    jobStateText: state => ({ running: 'Running', succeeded: 'Succeeded', failed: 'Failed', cancelled: 'Cancelled', queued: 'Queued' }[state] ?? state),
    // Green stays reserved for "present / secure": a finished job is info, a failed one critical.
    jobStateClass: state => ({ running: 'tone-info', succeeded: 'tone-info', failed: 'tone-crit' }[state] ?? 'tone-neutral'),
    jobStateIcon: state => ({ running: 'refresh-cw', succeeded: 'check', failed: 'octagon-x', cancelled: 'circle-slash' }[state] ?? 'clock'),

    get validateCount() {
      return Object.values(this.savedDecisions).filter(d => d.state === 'validate').length;
    },
    get extractStale() {
      const rules = this.docs.rules, candidates = this.docs.candidates;
      return !!(rules && candidates && rules.generatedAt > candidates.generatedAt);
    },
    get validationCounts() {
      const counts = {};
      for (const r of this.docs.validation?.results ?? []) counts[r.verdict] = (counts[r.verdict] ?? 0) + 1;
      return VERDICT_ORDER.filter(v => counts[v]).map(v => ({ verdict: v, count: counts[v] }));
    },
    /** The plan's queries; a table with more than 50 values is split, labelled "part 2 of 3". */
    get planBatches() {
      const batches = this.plan?.batches ?? [];
      const totals = {}, seen = {};
      for (const b of batches) totals[`${b.table}|${b.target}`] = (totals[`${b.table}|${b.target}`] ?? 0) + 1;
      return batches.map(b => {
        const key = `${b.table}|${b.target}`;
        seen[key] = (seen[key] ?? 0) + 1;
        return { ...b, part: totals[key] > 1 ? `part ${seen[key]} of ${totals[key]}` : '' };
      });
    },

    /** Skipped / not-binding checks of the reviewed plan, grouped by reason. */
    get planReasons() {
      const groups = {};
      for (const d of this.plan?.decided ?? []) {
        const key = `${d.verdict}|${d.reason ?? ''}`;
        (groups[key] ??= { verdict: d.verdict, reason: d.reason ?? '', items: [] }).items.push(d);
      }
      return Object.values(groups).sort((a, b) => b.items.length - a.items.length);
    },

    /** On tenant switch: defaults from tenant.json, forget the old plan, pick up a job that may still run. */
    async resetDataView() {
      clearTimeout(this.jobTimer);
      this.job = null;
      this.jobError = null;
      this.jobNote = null;
      this.plan = null;
      this.planError = null;
      const defaults = this.docs.tenant?.defaults ?? {};
      this.opts = {
        incidentDays: defaults.incidentDays ?? 90, saveRaw: false,
        validationDays: Math.min(defaults.validationDays ?? 30, 30), includeBasic: false,
      };
      try {
        const jobs = await getJson(`/api/tenants/${encodeURIComponent(this.tenant)}/jobs`);
        this.job = jobs?.[0] ?? null;
        if (this.jobRunning) this.pollJob();
      } catch { /* an older server without jobs: the view still shows the status */ }
    },

    async startJob(kind, { fresh = false } = {}) {
      this.jobError = null;
      this.jobNote = null;
      if (this.dirty) {
        this.jobError = 'Save your value-check decisions first: the job reloads the data when it finishes.';
        return;
      }
      const days = kind === 'validate' ? this.opts.validationDays : (kind === 'extract' ? null : this.opts.incidentDays);
      try {
        this.job = await apiSend('POST', `/api/tenants/${encodeURIComponent(this.tenant)}/jobs`, {
          kind, days: days ? Number(days) : null, saveRaw: this.opts.saveRaw, includeBasic: this.opts.includeBasic, freshSignIn: fresh,
        });
        this.pollJob();
      } catch (err) {
        this.jobError = err.message;
      }
    },

    /**
     * What a failed job needs, from its error text (the server's `hint` is shown as well):
     * - signin: the sign-in ended (server restarted, refresh token expired) → Sign in.
     * - roles: Defender says the *account* has no permissions ("Missing user permissions … user
     *   permissions: ."): activate a PIM role and get new tokens, or ask for a role.
     * - consent: missing scopes / consent required → admin consent for the app registration.
     * - retry: anything else.
     */
    get jobFix() {
      const job = this.job;
      if (!job || job.state !== 'failed' || !job.error) return null;
      const text = job.error;
      if (/sign-in this version no longer supports/i.test(text)) return { kind: 'reconnect' };
      if (/Not signed in|Signed out of|changed since you signed in/i.test(text)) return { kind: 'signin' };
      // "Token used: <app>; permissions: <scp>; directory roles active in the token: <roles>." (server, 401/403)
      const token = text.match(/Token used: ([^;]+); permissions: ([^;]*); directory roles active in the token: ([^.\n]*)\./);
      const tokenInfo = token ? { app: token[1], roles: token[3] } : null;
      if (/Missing user permissions/i.test(text)) return { kind: 'roles', tokenInfo };
      if (/AADSTS65001|AADSTS65004|AADSTS90094|consent/i.test(text)) return { kind: 'consent', tokenInfo };
      return { kind: 'retry' };
    },

    /** Rerun the failed step; fresh = newly issued tokens (after activating a PIM role). */
    retryJob(fresh = false) {
      if (this.job) this.startJob(this.job.kind, { fresh });
    },

    /** Same tab: Entra's consent prompt; Entra sends the browser back to /auth/callback. */
    grantConsent(next = 'data') {
      if (this.dirty) { this.jobError = 'Save your value-check decisions first: the consent prompt leaves this page.'; return; }
      const url = this.adminConsentUrl(this.docs.tenant, next);
      if (url) location.href = url;
    },

    stageLook: state => STAGE_LOOK[state] ?? STAGE_LOOK.pending,
    sentinelLook: state => SENTINEL_LOOK[state] ?? SENTINEL_LOOK.unknown,
    /** Stages that did not work in a job that went on without them (the wizard's done step lists them). */
    get failedStages() {
      return (this.job?.stages ?? []).filter(s => s.state === 'failed');
    },

    async cancelJob() {
      if (!this.job) return;
      try {
        this.job = await apiSend('POST', `/api/jobs/${this.job.id}/cancel`);
      } catch (err) {
        this.jobError = err.message;
      }
    },

    /** Polls the running job once a second; reloads the tenant's files when it succeeds. */
    pollJob() {
      clearTimeout(this.jobTimer);
      const id = this.job?.id;
      const tenant = this.tenant;
      this.jobTimer = setTimeout(async () => {
        try {
          const job = await getJson(`/api/jobs/${id}`);
          if (this.tenant !== tenant || this.job?.id !== id) return; // switched tenant meanwhile
          if (!job) { this.jobError = 'The job is gone (was the server restarted?).'; this.job = null; return; }
          this.job = job;
          this.$nextTick(() => { const log = this.$refs.jobLog; if (log) log.scrollTop = log.scrollHeight; });
          if (job.state === 'running') { this.pollJob(); return; }
          if (job.state === 'succeeded') {
            await this.reloadAfterJob(job);
            // The wizard's collect step leaves when the job succeeds.
            if (this.route.view === 'setup' && this.setupStep === 'collect') this.go('#/setup/done');
          }
        } catch (err) {
          this.jobError = `Lost contact with the server: ${err.message}`;
        }
      }, 1000);
    },

    async reloadAfterJob(job) {
      this.plan = null;
      if (this.dirty) {
        this.jobNote = 'New data is ready, but you have unsaved decisions: save them, then reload the page to see it.';
        return;
      }
      try { this.tenants = (await getJson('/api/tenants')) ?? this.tenants; } catch { /* keep the list */ }
      await this.loadTenant(this.tenant);
      this.jobNote = job.kind === 'validate' ? 'Validation finished: see the results in the Validation view.' : 'The views now show the new data.';
    },

    /** Validation dry run: what would be queried where, and what is skipped and why. Sends nothing to the tenant. */
    async loadPlan() {
      this.planLoading = true;
      this.planError = null;
      const query = `days=${encodeURIComponent(this.opts.validationDays)}&includeBasic=${this.opts.includeBasic}`;
      try {
        const { data, problem } = await getJsonOrProblem(`/api/tenants/${encodeURIComponent(this.tenant)}/validation-plan?${query}`);
        this.plan = data;
        this.planError = problem;
      } catch (err) {
        this.planError = err.message;
      } finally {
        this.planLoading = false;
      }
    },

    // ------------------------------------------------------------ setup wizard
    parsedWorkspace: text => parseWorkspaceId(text),
    get setupSteps() { return SETUP_STEPS; },
    get permissions() { return PERMISSIONS; },
    /** The step in the URL; #/setup alone starts at the welcome step on a fresh install, else at the app step. */
    get setupStep() {
      const step = this.route.param;
      if (SETUP_STEPS.some(s => s.key === step)) return step;
      return this.tenants.some(t => !t.demo) ? 'app' : 'welcome';
    },
    get setupIndex() { return SETUP_STEPS.findIndex(s => s.key === this.setupStep); },
    get setupTenant() { return this.tenants.find(t => t.alias === this.setup.alias) ?? null; },
    get demoTenant() { return this.tenants.find(t => t.demo) ?? null; },
    /** Step indicator: done steps link back, the current one is marked, later ones are plain text. */
    stepState(index) {
      return index < this.setupIndex ? 'is-done' : index === this.setupIndex ? 'is-current' : '';
    },
    stepHref(index) {
      return index < this.setupIndex && (index < 2 || this.setup.alias) ? `#/setup/${SETUP_STEPS[index].key}` : null;
    },
    /** Sidebar "Add tenant": a new wizard run, with an empty form. */
    startNewTenant() {
      this.setup.alias = null;
      remember('setupTenant', '');
      this.setup.form = { signIn: 'own-app', tenant: '', clientId: '', displayName: '', alias: '', aliasTouched: false };
      this.setup.submitted = false;
      this.setup.error = null;
      this.setup.lookup = { busy: false, input: '', tenantId: null, error: null };
      this.go(this.tenants.some(t => !t.demo) ? '#/setup/app' : '#/setup/welcome');
    },
    /** "Explore with demo data": the read-only sample tenant, remembered so the wizard does not come back on every load. */
    async exploreDemo() {
      if (!this.demoTenant) return;
      remember('explored', '1');
      await this.loadTenant(this.demoTenant.alias);
      this.go('#/overview');
    },

    // ---- step 2: app registration
    /** Back from a later step: show what was saved for the tenant being set up. */
    fillAppForm() {
      const t = this.setup.alias && this.tenant === this.setup.alias ? this.docs.tenant : null;
      if (!t || this.setup.form.tenant) return;
      this.setup.form = { signIn: signInOf(t), tenant: t.tenantId ?? '', clientId: t.auth?.clientId ?? '', displayName: t.displayName ?? t.alias, alias: t.alias, aliasTouched: true };
    },
    syncSetupAlias() {
      if (!this.setup.form.aliasTouched) this.setup.form.alias = slug(this.setup.form.displayName);
    },
    /** Tenant field: a GUID as is; a domain (contoso.onmicrosoft.com) is looked up anonymously at Entra ID. */
    async lookupTenant() {
      const input = this.setup.form.tenant.trim();
      const lookup = this.setup.lookup;
      if (GUID.test(input) || !input.includes('.')) { this.setup.lookup = { busy: false, input, tenantId: null, error: null }; return; }
      if (lookup.input === input && (lookup.tenantId || lookup.error)) return;
      this.setup.lookup = { busy: true, input, tenantId: null, error: null };
      try {
        const { data, problem } = await getJsonOrProblem(`/api/lookup-tenant?domain=${encodeURIComponent(input)}`);
        if (this.setup.form.tenant.trim() !== input) return; // typed on meanwhile
        this.setup.lookup = { busy: false, input, tenantId: data?.tenantId ?? null, error: problem };
      } catch (err) {
        this.setup.lookup = { busy: false, input, tenantId: null, error: err.message };
      }
    },
    /** The tenant id to save: typed as a GUID, or resolved from the domain. */
    get setupTenantId() {
      const input = this.setup.form.tenant.trim();
      if (GUID.test(input)) return input.toLowerCase();
      return this.setup.lookup.input === input ? this.setup.lookup.tenantId : null;
    },
    /** Same checks as the server's TenantOperation.Prepare, shown after the first Continue. */
    get setupErrors() {
      const f = this.setup.form;
      const errors = {};
      if (!this.setup.submitted) return errors;
      const input = f.tenant.trim();
      if (!input) errors.tenant = 'Enter the tenant ID, or a domain of the tenant such as contoso.onmicrosoft.com.';
      else if (!GUID.test(input) && !input.includes('.')) errors.tenant = 'The tenant ID is a GUID (Entra admin center → Overview → Tenant ID), or enter a domain such as contoso.onmicrosoft.com.';
      else if (!GUID.test(input) && this.setup.lookup.input === input && this.setup.lookup.error) errors.tenant = this.setup.lookup.error;
      if (f.signIn === 'own-app' && !GUID.test(f.clientId.trim())) errors.clientId = 'The client ID is the Application (client) ID of your app registration, a GUID.';
      if (!/^[a-z0-9-]+$/.test(f.alias)) errors.alias = 'Use lowercase letters, digits and dashes.';
      // Another real tenant's alias is for the server to judge: the same tenant again (reconnect) is fine.
      else if (this.tenants.some(t => t.alias === f.alias && t.demo)) errors.alias = 'This alias is used by the demo data. Pick another one.';
      return errors;
    },
    async submitApp() {
      this.setup.submitted = true;
      this.setup.error = null;
      if (this.setup.saving) return;
      if (!GUID.test(this.setup.form.tenant.trim())) await this.lookupTenant();
      if (Object.keys(this.setupErrors).length || !this.setupTenantId) return;
      const f = this.setup.form;
      this.setup.saving = true;
      try {
        await apiSend('POST', '/api/tenants', {
          alias: f.alias, tenantId: this.setupTenantId, signIn: f.signIn, displayName: f.displayName.trim() || null,
          clientId: f.signIn === 'own-app' ? f.clientId.trim() : null,
        });
        this.setup.alias = f.alias;
        remember('setupTenant', f.alias);
        this.tenants = (await getJson('/api/tenants')) ?? this.tenants;
        await this.loadTenant(f.alias);
        this.go('#/setup/signin');
      } catch (err) {
        this.setup.error = err.message;
      } finally {
        this.setup.saving = false;
      }
    },

    // ---- step 3: sign in
    /** The last sign-in result, when it is about the tenant being set up (or an admin consent, which has no tenant). */
    get setupNotice() {
      const n = this.signInNotice;
      return n && (n.alias === this.setup.alias || n.alias == null) ? n : null;
    },

    // ---- step 4: workspaces
    async discoverWorkspaces() {
      const d = this.setup.discover;
      Object.assign(d, { busy: true, loaded: false, alias: this.setup.alias, error: null, status: null, result: null, selected: {}, none: false });
      try {
        const response = await fetch(`/api/tenants/${encodeURIComponent(this.setup.alias)}/discover`, { cache: 'no-store' });
        const data = await response.json().catch(() => null);
        if (!response.ok) { d.error = data?.error ?? `HTTP ${response.status}`; d.status = response.status; return; }
        d.result = data;
        // Preselect what tenant.json has; on a first run every workspace with Microsoft Sentinel.
        const saved = this.docs.tenant?.workspaces;
        for (const w of data.workspaces)
          d.selected[w.workspaceId] = saved ? saved.some(s => s.workspaceId === w.workspaceId) : w.sentinel === 'enabled';
        d.none = Array.isArray(saved) && saved.length === 0;
        // Saved workspaces that discovery did not list (role on the workspace only) stay, as manual entries.
        d.manualIds = (saved ?? []).filter(s => !data.workspaces.some(w => w.workspaceId === s.workspaceId)).map(armWorkspaceId);
      } catch (err) {
        d.error = err.message;
      } finally {
        d.busy = false;
        d.loaded = true;
      }
    },
    get discoveredSentinel() { return (this.setup.discover.result?.workspaces ?? []).filter(w => w.sentinel !== 'notEnabled'); },
    get discoveredOther() { return (this.setup.discover.result?.workspaces ?? []).filter(w => w.sentinel === 'notEnabled'); },
    get selectedWorkspaceIds() {
      const d = this.setup.discover;
      const picked = (d.result?.workspaces ?? []).filter(w => d.selected[w.workspaceId]).map(armWorkspaceId);
      return [...picked, ...d.manualIds];
    },
    toggleNoWorkspace() {
      if (this.setup.discover.none) { this.setup.discover.selected = {}; this.setup.discover.manualIds = []; }
    },
    addManualWorkspace() {
      const d = this.setup.discover;
      const parsed = parseWorkspaceId(d.manual);
      if (!parsed) return;
      const id = `/subscriptions/${parsed.sub}/resourceGroups/${parsed.rg}/providers/Microsoft.OperationalInsights/workspaces/${parsed.name}`;
      if (!d.manualIds.includes(id)) d.manualIds.push(id);
      d.manual = '';
      d.none = false;
    },
    async saveWorkspaces() {
      const d = this.setup.discover;
      d.error = null;
      const ids = d.none ? [] : this.selectedWorkspaceIds;
      if (!d.none && !ids.length) { d.error = 'Select at least one workspace, or choose No Sentinel workspace.'; return; }
      this.setup.saving = true;
      try {
        await apiSend('PUT', `/api/tenants/${encodeURIComponent(this.setup.alias)}/workspaces`, { workspaces: ids });
        this.tenants = (await getJson('/api/tenants')) ?? this.tenants;
        await this.loadTenant(this.setup.alias);
        this.go('#/setup/collect');
      } catch (err) {
        d.error = err.message;
      } finally {
        this.setup.saving = false;
      }
    },
    /** Display: "law-sentinel in rg-sec" for a manual entry. */
    manualLabel(id) {
      const p = parseWorkspaceId(id);
      return p ? `${p.name} in ${p.rg}` : id;
    },

    // ---- step 5: collect
    async startCollection() {
      this.jobError = null;
      const c = this.setup.collect;
      try {
        await apiSend('PUT', `/api/tenants/${encodeURIComponent(this.setup.alias)}/settings`, {
          defaults: { incidentDays: Number(c.incidentDays), validationDays: Number(c.validationDays) },
        });
      } catch (err) {
        this.jobError = err.message;
        return;
      }
      this.opts.incidentDays = Number(c.incidentDays);
      this.opts.validationDays = Number(c.validationDays);
      await this.startJob(c.extract ? 'refresh' : 'collect');
    },
    /** The collect step shows the job only while it belongs to this tenant and is a collection. */
    get setupJob() {
      const job = this.job;
      return job && job.tenant === this.setup.alias && ['refresh', 'collect'].includes(job.kind) ? job : null;
    },

    // ---- step 6: done
    get doneTiles() {
      const c = this.docs.candidates;
      // Rules whose query names a table or column the tenant schema does not have ("bind errors").
      const bindErrorRules = c ? (c.ruleSummaries ?? []).filter(s => s.bindErrors?.length).length : undefined;
      return [
        { label: 'Rules', value: this.docs.rules?.rules.length, icon: 'list-checks', tone: 'tone-info' },
        { label: `Incidents · ${this.docs.incidents?.window.days ?? '—'} days`, value: this.docs.incidents?.incidents.length, icon: 'siren', tone: 'tone-neutral' },
        { label: 'Value checks found', value: c?.checks.length, icon: 'scan-line', tone: 'tone-info' },
        { label: 'Rules with unknown tables or columns', value: bindErrorRules, icon: 'triangle-alert', tone: bindErrorRules ? 'tone-warn' : 'tone-neutral' },
      ];
    },
    finishSetup(hash) {
      remember('setupTenant', '');
      this.go(hash);
    },

    // ------------------------------------------------------------ settings
    fillSettingsForm() {
      const t = this.docs.tenant;
      this.settingsMessage = null;
      this.settingsError = null;
      this.removeConfirm = '';
      this.removeOpen = false;
      this.settingsForm = {
        displayName: t?.displayName ?? this.tenant ?? '',
        incidentDays: t?.defaults?.incidentDays ?? 90,
        validationDays: Math.min(t?.defaults?.validationDays ?? 30, 30),
      };
    },
    async saveSettings() {
      this.settingsMessage = null;
      this.settingsError = null;
      const f = this.settingsForm;
      try {
        await apiSend('PUT', `/api/tenants/${encodeURIComponent(this.tenant)}/settings`, {
          displayName: f.displayName.trim() || null,
          defaults: { incidentDays: Number(f.incidentDays), validationDays: Number(f.validationDays) },
        });
        this.tenants = (await getJson('/api/tenants')) ?? this.tenants;
        await this.loadTenant(this.tenant);
        this.fillSettingsForm();
        this.settingsMessage = 'Settings saved.';
      } catch (err) {
        this.settingsError = err.message;
      }
    },
    async signOut() {
      this.settingsError = null;
      try {
        await apiSend('POST', `/auth/logout?tenant=${encodeURIComponent(this.tenant)}`);
        const [tenants, session] = await Promise.all([getJson('/api/tenants'), loadSession(true)]);
        this.tenants = tenants ?? this.tenants;
        this.session = session ?? this.session;
        this.settingsMessage = 'Signed out. Collected data stays available; sign in again to collect or validate.';
      } catch (err) {
        this.settingsError = err.message;
      }
    },
    async collectAgain() {
      this.go('#/data');
      await this.startJob('refresh');
    },
    /** A pre-M8 tenant: the wizard's app step for the same alias and tenant ID; the rest follows from there. */
    reconnectTenant() {
      const t = this.docs.tenant;
      this.setup.alias = this.tenant;
      remember('setupTenant', this.tenant);
      this.setup.form = { signIn: signInOf(t), tenant: t?.tenantId ?? '', clientId: t?.auth?.clientId ?? '', displayName: t?.displayName ?? this.tenant, alias: this.tenant, aliasTouched: true };
      this.setup.submitted = false;
      this.setup.error = null;
      this.go('#/setup/app');
    },
    /** Edit the workspaces with the wizard's workspace step. */
    editWorkspaces() {
      this.setup.alias = this.tenant;
      remember('setupTenant', this.tenant);
      if (this.signedOut) { this.go('#/setup/signin'); return; }
      this.setup.discover.loaded = false;
      this.go('#/setup/workspaces');
    },
    async removeTenant() {
      const alias = this.tenant;
      if (this.removeConfirm !== alias) return;
      this.settingsError = null;
      try {
        await apiSend('DELETE', `/api/tenants/${encodeURIComponent(alias)}`);
        this.tenants = (await getJson('/api/tenants')) ?? [];
        if (this.setup.alias === alias) this.setup.alias = null;
        remember('tenant', '');
        const next = this.tenants.find(t => !t.demo) ?? this.tenants[0];
        this.tenant = null;
        this.docs = {};
        if (next) {
          await this.loadTenant(next.alias);
          this.go('#/overview');
        } else {
          this.go('#/setup/welcome');
        }
      } catch (err) {
        this.settingsError = err.message;
      }
    },
    /** Value checks and Overview empty states: collect from the tenant now (Data shows the progress). */
    async collectNow() {
      if (this.signedOut) { this.signIn(this.tenant, 'data'); return; }
      this.go('#/data');
      await this.startJob('refresh');
    },

    // ------------------------------------------------------------ validation view
    get validationGroups() {
      const results = this.docs.validation?.results ?? [];
      return VERDICT_ORDER
        .map(verdict => ({
          verdict,
          rows: results.filter(r => r.verdict === verdict).map(r => {
            const check = this.checksById[r.checkId];
            const ruleIds = check ? [...new Set(check.occurrences.map(o => o.ruleId))] : [];
            return { ...r, check, ruleIds, deadExclusion: !!check && check.occurrences.every(o => o.negated) };
          }).sort((a, b) => a.table.localeCompare(b.table) || a.column.localeCompare(b.column) || a.value.localeCompare(b.value)),
        }))
        .filter(g => g.rows.length);
    },
    get validationStale() {
      const v = this.docs.validation, c = this.docs.candidates;
      return !!(v && c && c.generatedAt > v.runAt);
    },
  }));
});
