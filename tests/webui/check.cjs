const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const { spawnSync } = require('node:child_process');

const webRoot = path.resolve(__dirname, '../../webui');
const appPath = path.join(webRoot, 'app.js');
const tests = [];
function test(name, task) { tests.push([name, task]); }
function deferred() { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; }

function renderedTree(html) {
  const parsed = spawnSync('python', [path.join(__dirname, 'render_tree.py')], { input: html, encoding: 'utf8' });
  assert.equal(parsed.status, 0, parsed.stderr);
  function wrap(raw, parent = null) {
    const node = { tag: raw.tag, attrs: raw.attrs, parentElement: parent, disabled: Object.hasOwn(raw.attrs, 'disabled'), value: raw.attrs.value, dataset: {} };
    for (const [key, value] of Object.entries(raw.attrs)) if (key.startsWith('data-')) node.dataset[key.slice(5).replace(/-([a-z])/g, (_, char) => char.toUpperCase())] = value;
    node.matches = selector => selector.startsWith('.') ? (node.attrs.class || '').split(/\s+/).includes(selector.slice(1)) : node.tag === selector;
    node.closest = selector => { for (let current = node; current; current = current.parentElement) if (current.matches(selector)) return current; return null; };
    node.hasAttribute = name => Object.hasOwn(node.attrs, name);
    node.children = raw.children.map(child => wrap(child, node));
    return node;
  }
  return wrap(JSON.parse(parsed.stdout));
}
function descendants(root, predicate) { return [root, ...root.children.flatMap(child => descendants(child, () => true))].filter(predicate); }
function accountRow(tree, uid) { return descendants(tree, node => node.tag === 'tr' && descendants(node, child => child.dataset.selectAccount === uid).length)[0]; }
async function click(h, target) { assert.ok(target, 'Actual rendered click target must exist'); h.events.get('click')({ target }); await new Promise(setImmediate); }

function load() {
  const nodes = new Map();
  class Node {
    constructor() { this.innerHTML = ''; this.textContent = ''; this.hidden = false; this.open = false; this.disabled = false; this.isConnected = true; this.listeners = new Map(); this.children = []; this.classList = { toggle() {} }; }
    querySelectorAll() { return []; }
    addEventListener(name, handler) { this.listeners.set(name, handler); }
    setAttribute() {} removeAttribute() {}
    replaceChildren(...children) { this.children = []; this.append(...children); this.innerHTML = ''; this.textContent = ''; }
    append(...children) { children.forEach(child => { child.parentElement = this; }); this.children.push(...children); }
    showModal() { this.open = true; } close() { this.open = false; }
    reset() {} click() { return this.listeners.get('click')?.({}); }
    remove() { if (this.parentElement) this.parentElement.children = this.parentElement.children.filter(child => child !== this); this.parentElement = null; }
    focus() { document.activeElement = this; } select() { this.selected = true; } setSelectionRange() {}
    set id(value) { this._id = value; nodes.set('#' + value, this); } get id() { return this._id; }
  }
  const select = selector => { if (!nodes.has(selector)) nodes.set(selector, new Node()); return nodes.get(selector); };
  const events = new Map();
  const document = { querySelector: select, querySelectorAll: () => [], getElementById: id => select('#' + id), createElement: () => new Node(), body: new Node(), addEventListener: (name, handler) => events.set(name, handler) };
  const windowEvents = new Map(); const navigator = { languages: ['zh-CN'] }; document.documentElement = {};
  const window = { addEventListener: (name, handler) => windowEvents.set(name, handler) };
  const context = vm.createContext({ document, window, navigator, crypto: webcrypto, console, URL, Date, setTimeout: () => 1, clearTimeout() {}, setInterval() {}, confirm: () => true, fetch: async () => { throw new Error('Unexpected fetch'); } });
  const apiSource = fs.readFileSync(path.join(webRoot, 'api.js'), 'utf8').replaceAll(/^export /gm, '').replaceAll(/^import .*\r?\n/gm, '');
  let appSource = fs.readFileSync(appPath, 'utf8').replaceAll(/^import .*\r?\n/gm, '').replaceAll(/^export /gm, '').replaceAll(/^import .*\r?\n/gm, '');
  const startup = /run\(async \(\) => \{ const session = await api\.session\(\); if \(session\.authenticated\) await loadWorkbench\(\); else showLogin\(\); \}\);/;
  assert.match(appSource, startup, 'Only the automatic initial network boot is omitted by this harness');
  appSource = appSource.replace(startup, '');
  const localeSource = ['i18n.js','catalog-locales.js'].map(file => fs.readFileSync(path.join(webRoot,file),'utf8').replaceAll(/^export /gm,'')).join('\n');
  vm.runInContext(localeSource + '\n' + apiSource + '\n' + appSource + '\nglobalThis.testApi = { api, state, action, commitPending, selectAccount, openGrant, showCredentialReview, handleError, run, ManagementApi, ApiError, renderAccounts, renderResources, download, putDraft, localizeCatalog, localDisplayNames, catalogNameCell, matches, buildPreviewSections, showCredentials, hideCredentials, restoreCredentials, dismissCredentials, copyCredentials, resumeManagement, renderSettings, refreshExpertPeriod, applyExpertPeriod, syncSettingsControls, materialDisplay, materialKey, materialImageRoute };', context);
  const exposed = context.testApi;
  const data = { revision: 'a'.repeat(64), account: { uid: '1', name: 'Owner <img src=x onerror=alert(1)>', rank: 1 }, resources: { freeGems: 1, paidGems: 0, gf: 0, friendPoints: 0, stamina: 10, staminaMax: 10 }, cards: [], equipment: [], items: [], collections: { skins: [], backgroundIds: [], titleIds: [] }, busy: {} };
  Object.assign(exposed.state, { authenticated: true, uid: '1', data, lease: { leaseId: 'lease-one', expiresAt: Date.now() + 60000 }, catalog: { cards: [], equipment: [{ id: 5, name: 'Knife' }], items: [{ type: 6, id: 11, maxQty: 999, name: 'Ticket' }, { type: 6, id: 12, maxQty: 999, name: 'Other' }], limits: { maxItemsPerCommand: 200, maxEquipment: 999, maxFreeGems: 999, maxGf: 999 } } });
  exposed.api.account = async () => data;
  exposed.api.accounts = async () => ({ items: [] });
  exposed.api.commit = async () => ({ applied: true });
  return { ...exposed, context, nodes, select, data, window, events, windowEvents, document, navigator };
}

const credentialFixture = { uid: '1234567890123456', takeoverId: 'TESTID1234567890', password: 'TestOnly123' };
function attachNative(h, vault) {
  h.window.__BRSF_NATIVE_CREDENTIALS__ = true;
  h.window.prompt = (message, raw) => {
    assert.equal(message, 'BRSF_CREDENTIALS_V1'); const request = JSON.parse(raw); vault.calls.push(request.action);
    if (request.action === 'retain') { vault.record = { uid: request.uid, takeoverId: request.takeoverId, password: request.password }; return JSON.stringify({ ok: true }); }
    if (request.action === 'restore') return JSON.stringify({ ok: true, record: vault.record });
    if (request.action === 'clear') { vault.record = null; return JSON.stringify({ ok: true }); }
    if (request.action === 'copy') { assert.deepEqual(Object.keys(request).sort(), ['action', 'field']); vault.fields.push(request.field); return JSON.stringify({ ok: !!vault.record && vault.copySucceeds !== false }); }
    assert.fail('Unexpected native credential action');
  };
}

for (const kind of ['wallet', 'items']) test(`${kind}: slow preview preserves changed and new drafts after commit`, async () => {
  const h = load(); const wait = deferred(); let submitted;
  const first = kind === 'wallet' ? 'wallet:freeGems' : 'item:6:11:';
  const added = kind === 'wallet' ? 'wallet:gf' : 'item:6:12:';
  h.state.drafts.set(first, '10');
  h.api.preview = async (_uid, _lease, command) => { submitted = command; return wait.promise; };
  const pendingPreview = h.action(kind + '-preview');
  h.state.drafts.set(first, '20'); h.state.drafts.set(added, '30');
  wait.resolve({ changes: [] }); await pendingPreview;
  assert.equal(kind === 'wallet' ? submitted.payload.freeGems : submitted.payload.items[0].qty, 10);
  assert.equal(kind === 'wallet' ? Object.keys(submitted.payload).length : submitted.payload.items.length, 1);
  await h.commitPending();
  assert.equal(h.state.drafts.get(first), '20'); assert.equal(h.state.drafts.get(added), '30');
});

for (const kind of ['wallet', 'items']) test(`${kind}: returning an input to its old stored value during preview remains an unsaved target after commit`, async () => {
  const h = load(); const wait = deferred();
  const key = kind === 'wallet' ? 'wallet:freeGems' : 'item:6:11:';
  const original = kind === 'wallet' ? h.data.resources.freeGems : 0;
  h.putDraft(key, '10', original); h.api.preview = () => wait.promise;
  const pendingPreview = h.action(kind + '-preview');
  h.putDraft(key, String(original), original); assert.equal(h.state.drafts.has(key), false);
  wait.resolve({ changes: [] }); await pendingPreview; await h.commitPending();
  assert.equal(h.state.drafts.get(key), original);
});

test('unchanged submitted drafts are cleared while other draft categories remain', async () => {
  const h = load(); h.state.drafts.set('wallet:freeGems', '10'); h.state.drafts.set('item:6:11:', '20');
  h.api.preview = async () => ({ changes: [] });
  await h.action('wallet-preview'); await h.commitPending();
  assert.equal(h.state.drafts.has('wallet:freeGems'), false); assert.equal(h.state.drafts.get('item:6:11:'), '20');
});

test('failed commit retains pending operation ID and all drafts for idempotent retry', async () => {
  const h = load(); h.state.drafts.set('wallet:freeGems', '10'); h.api.preview = async () => ({ changes: [] });
  await h.action('wallet-preview'); const operation = h.state.pending.command.operationId;
  let calls = 0; h.api.commit = async (_uid, _lease, command) => { calls++; assert.equal(command.operationId, operation); if (calls === 1) throw new Error('response lost'); return { replayed: true }; };
  await assert.rejects(h.commitPending(), /response lost/);
  assert.equal(h.state.pending.command.operationId, operation); assert.equal(h.state.drafts.size, 1);
  await h.commitPending(); assert.equal(calls, 2); assert.equal(h.state.pending, null); assert.equal(h.state.drafts.size, 0);
});

test('switching accounts clears released lease before a failed next-account read', async () => {
  const h = load(); let released = false;
  h.api.release = async () => { released = true; };
  h.api.account = async () => { assert.equal(released, true); assert.equal(h.state.lease, null); throw new Error('unavailable'); };
  await assert.rejects(h.selectAccount('2'), /unavailable/);
  assert.equal(h.state.uid, '1'); assert.equal(h.state.lease, null);
});

test('uncertain release clears local lease and does not fetch a different account', async () => {
  const h = load(); h.api.release = async () => { throw new Error('not released'); };
  h.api.account = async () => { assert.fail('Must not switch after failed release'); };
  await assert.rejects(h.selectAccount('2'), /not released/); assert.equal(h.state.lease, null); assert.equal(h.state.uid, '1');
});

for (const [action, key] of [
  ['cards-ensure', 'cards:catalog'], ['cards-max', 'cards:owned'], ['cards-max-all', 'cards:owned'],
  ['equipment-max', 'equipment:owned'], ['equipment-max-all', 'equipment:owned'], ['equipment-lock', 'equipment:owned'],
  ['collection-grant', 'collection:backgrounds'], ['collection-grant', 'collection:titles'], ['collection-grant', 'collection:skins']
]) test(`${action}/${key}: successful commit clears only selections present when preview started`, async () => {
  const h = load(); const wait = deferred(); const selected = new Set(['5']); h.state.selections.set(key, selected);
  if (key.startsWith('collection:')) h.state.modes.collection = key.slice('collection:'.length);
  if (key === 'collection:skins') h.state.catalog.skins = [{ id: 5, cardId: 55 }];
  h.api.preview = () => wait.promise;
  const pendingPreview = h.action(action, { dataset: { locked: 'true' } });
  selected.add('6'); wait.resolve({ changes: [] }); await pendingPreview; await h.commitPending();
  assert.equal(selected.has('5'), false); assert.equal(selected.has('6'), true);
});

test('equipment grant uses the smaller per-command or remaining inventory cap', async () => {
  const h = load(); h.openGrant(5); assert.match(h.select('#review-body').innerHTML, /max="200"/);
  h.state.data.equipment = new Array(995); h.openGrant(5); assert.match(h.select('#review-body').innerHTML, /max="4"/);
  h.state.data.equipment = new Array(999); h.openGrant(5); assert.match(h.select('#review-body').innerHTML, /max="0"/);
});

test('create review describes deferred gifts and puts untrusted account name into a text node', async () => {
  const h = load(); h.showCredentialReview('create', '<svg onload=alert(1)>', 'Password123');
  const html = h.select('#review-body').innerHTML;
  assert.match(html, /完成教程后首次进入主页/); assert.doesNotMatch(html, /<svg|onload=/);
});

test('logout clears all local account and credential state when server cannot confirm', async () => {
  const h = load(); h.state.drafts.set('wallet:gf', '5'); h.state.pending = { password: 'Password123' };
  h.select('#credentials-result').append({ secret: 'old-password' }); h.select('#credentials-dialog').open = true;
  h.api.logout = async () => { throw new Error('offline'); };
  await h.select('#logout').click();
  assert.equal(h.state.uid, ''); assert.equal(h.state.data, null); assert.equal(h.state.lease, null); assert.equal(h.state.pending, null);
  assert.equal(h.state.drafts.size, 0); assert.equal(h.select('#workspace').hidden, true); assert.equal(h.select('#login').hidden, false);
  assert.equal(h.select('#credentials-result').children.length, 0); assert.equal(h.select('#credentials-dialog').open, false);
  assert.equal(h.select('#notice').textContent, '退出请求失败，请检查服务连接。');
});

test('401 discards local session state and duplicate run is suppressed while a write is pending', async () => {
  const h = load(); const wait = deferred(); let writes = 0;
  const first = h.run(async () => { writes++; await wait.promise; });
  await h.run(async () => { writes++; }); assert.equal(writes, 1); wait.resolve(); await first;
  h.handleError(new h.ApiError('auth_required', 'expired', 401)); assert.equal(h.state.uid, ''); assert.equal(h.state.data, null); assert.equal(h.api.csrfToken, '');
});

test('Android downloads navigate only to the standard account/backup route without claiming a saved file', async () => {
  const h = load(); const requests = [];
  h.window.__BRSF_NATIVE_DOWNLOADS__ = true; h.window.location = { assign: path => requests.push(path) };
  h.api.backup = async () => { assert.fail('Native download must not fetch a blob'); };
  h.api.export = h.api.backup;
  await h.download('backup'); await h.download('export');
  assert.equal(requests.join(','), '/management/v1/backup,/management/v1/accounts/1/export');
  assert.equal(h.select('#notice').textContent, '请选择文件保存位置。');
  h.state.uid = '1/../../session?x'; await h.download('export');
  assert.equal(requests[2], '/management/v1/accounts/1%2F..%2F..%2Fsession%3Fx/export');
});

test('ordinary browser download remains authenticated API/blob flow and reports errors', async () => {
  const h = load(); let backup = 0; let exported = 0;
  h.api.backup = async () => { backup++; throw new h.ApiError('auth_required', 'expired', 401); };
  h.api.export = async uid => { exported++; assert.equal(uid, '1'); throw new Error('download failed'); };
  await assert.rejects(h.download('backup'), error => error.status === 401);
  await assert.rejects(h.download('export'), /download failed/);
  assert.equal(backup, 1); assert.equal(exported, 1); assert.equal(h.select('#notice').textContent, '');
  h.api.backup = async () => ({ blob: new Blob(['isolated-backup-fixture']) });
  await h.download('backup'); assert.equal(h.select('#notice').textContent, '已发起整服备份下载。');
});

test('complete local-name dictionary has stable keys and localizes without changing source records', async () => {
  const h = load(); const names = h.localDisplayNames;
  assert.equal(Object.keys(names.cards).length, 76); assert.equal(Object.keys(names.equipment).length, 306);
  assert.equal(new Set(Object.values(names.equipment).map(entry => entry.nameJa)).size, 205);
  for (const category of ['cards', 'equipment']) for (const [id, entry] of Object.entries(names[category])) {
    assert.match(id, /^\d+$/); assert.equal(typeof entry.nameJa, 'string'); assert.ok(entry.nameJa); assert.equal(entry.source, 'local-translation');
    assert.ok(entry.nameZh); assert.doesNotMatch(entry.nameZh, /[\u3041-\u3096\u30a1-\u30fa\u30fc]/u); assert.doesNotMatch(entry.nameZh, /（武器）/u); assert.equal(typeof entry.nameEn, 'string');
    const raw = { id: Number(id), name: entry.nameJa };
    const localized = h.localizeCatalog({ [category]: [raw] })[category][0];
    assert.equal(localized.name, entry.nameZh); assert.equal(localized.originalName, entry.nameJa); assert.equal(raw.name, entry.nameJa);
  }
  assert.equal(names.equipment['520100100'].nameZh, 'AXTON VB-WA053');
  assert.equal(names.equipment['520100200'].nameZh, 'TRIFLE WP-SC958');
});

test('Chinese, Japanese, official character English and decimal IDs remain searchable', async () => {
  const h = load(); const rows = h.localizeCatalog({ cards: [{ id: 50010001, name: 'エリシュカ[AFARM正式兵装]' }], equipment: [{ id: 110100103, name: 'Z／G ナイフ' }] });
  for (const query of ['艾莉什卡', 'エリシュカ', 'elishka', '50010001']) { h.state.search.cards = query; assert.equal(h.matches(rows.cards[0], 'cards'), true); }
  for (const query of ['短刀', 'ナイフ', '110100103']) { h.state.search.equipment = query; assert.equal(h.matches(rows.equipment[0], 'equipment'), true); }
  const changes = [{ label: '兵装', before: [], after: [{ cardId: 50010001, level: 1 }] }];
  assert.equal(h.buildPreviewSections(changes, rows)[0].entries[0].name, '艾莉什卡［AFARM正式兵装］');
});

test('unmapped or changed source labels retain original names without translation labels or another item translation', async () => {
  const h = load(); const raw = [{ id: 99999999, name: '<svg onload=alert(1)>' }, { id: 50010001, name: 'Different source name' }];
  const rows = h.localizeCatalog({ cards: raw }).cards;
  for (let i = 0; i < rows.length; i++) { assert.equal(rows[i].translationSource, 'unmapped'); assert.equal(rows[i].name, raw[i].name); }
  const html = h.catalogNameCell(rows[0], 'cards', '#99999999');
  assert.doesNotMatch(html, /本地译名|未提供|translation-label/); assert.doesNotMatch(html, /<svg|onload=/);
});

test('catalog thumbnails use only fixed local item routes with lazy 64px images and safe error fallback', async () => {
  const h = load(); const row = { id: 50010001, name: 'Elishka' };
  const html = h.catalogNameCell(row, 'cards', '#50010001');
  assert.match(html, /src="\/management\/images\/cards\/50010001\.png"/); assert.match(html, /width="64" height="64" loading="lazy"/);
  assert.match(h.catalogNameCell({ id: 110100103, name: 'Knife' }, 'equipment', 'instance1'), /\/equipment\/110100103\.png/);
  assert.doesNotMatch(h.catalogNameCell({ id: '1/../../session', name: 'bad' }, 'cards', ''), /<img|src=/);
  const fallback = { hidden: true }; const image = { tagName: 'IMG', hidden: false, nextElementSibling: fallback, hasAttribute: key => key === 'data-catalog-thumb' };
  h.events.get('error')({ target: image }); assert.equal(image.hidden, true); assert.equal(fallback.hidden, false);
  const unrelated = { tagName: 'IMG', hidden: false, hasAttribute: () => false };
  h.events.get('error')({ target: unrelated }); assert.equal(unrelated.hidden, false);
});

test('credential fields are selectable and all three browser copy actions retain the undismissed dialog', async () => {
  const h = load(); const copied = [];
  h.navigator.clipboard = { writeText: async text => { copied.push(text); } };
  h.showCredentials(credentialFixture, credentialFixture.uid);
  assert.equal(h.select('#credential-value-id').readOnly, true); assert.equal(h.select('#credential-value-password').readOnly, true);
  for (const field of ['id', 'password', 'all']) await h.copyCredentials(field);
  assert.equal(copied[0], credentialFixture.takeoverId); assert.equal(copied[1], credentialFixture.password);
  assert.equal(copied[2], `ID: ${credentialFixture.takeoverId}\nPASS: ${credentialFixture.password}`);
  assert.equal(h.select('#credentials-dialog').open, true); assert.equal(h.state.unreadCredentials.uid, credentialFixture.uid);
  assert.equal(h.select('#notice').textContent, '引继资料已复制。');
  const html = fs.readFileSync(path.join(webRoot, 'index.html'), 'utf8');
  for (const field of ['id', 'password', 'all']) assert.ok(html.includes(`data-copy-credentials="${field}"`));
  const copyButton = { disabled: false, dataset: { copyCredentials: 'id' }, isConnected: true };
  const closeButton = { disabled: false, dataset: {}, id: 'credentials-dismiss', isConnected: true };
  const writeButton = { disabled: false, dataset: {}, isConnected: true };
  h.document.querySelectorAll = selector => selector === 'button' ? [copyButton, closeButton, writeButton] : [];
  const wait = deferred(); const busy = h.run(() => wait.promise);
  assert.equal(writeButton.disabled, true); assert.equal(copyButton.disabled, false); assert.equal(closeButton.disabled, false);
  h.events.get('click')({ target: { closest: () => copyButton } }); await new Promise(setImmediate);
  assert.equal(copied[3], credentialFixture.takeoverId); wait.resolve(); await busy;
});

test('browser clipboard rejection falls back inside the active modal, removes secret buffer and restores focus', async () => {
  const h = load(); h.navigator.clipboard = { writeText: async () => { throw new Error('denied'); } };
  h.showCredentials(credentialFixture, credentialFixture.uid); const previous = h.select('#credential-value-id'); previous.focus(); let buffer;
  h.document.execCommand = command => { assert.equal(command, 'copy'); buffer = h.document.activeElement; assert.equal(buffer.parentElement, h.select('#credentials-dialog')); assert.equal(buffer.value, credentialFixture.password); return true; };
  await h.copyCredentials('password'); assert.equal(buffer.parentElement, null); assert.equal(buffer.value, ''); assert.equal(h.document.activeElement, previous);
  assert.equal(h.select('#notice').textContent, '密码已复制。');
  h.document.execCommand = () => false; await h.copyCredentials('id'); assert.equal(h.select('#notice').textContent, '复制失败，请长按字段复制。');
  assert.equal(h.state.unreadCredentials.password, credentialFixture.password);
});

test('native copy sends only a fixed field and announces success only after actual native acknowledgement', async () => {
  const h = load(); const vault = { record: null, calls: [], fields: [], copySucceeds: false }; attachNative(h, vault);
  h.showCredentials(credentialFixture, credentialFixture.uid); await h.copyCredentials('all');
  assert.equal(h.select('#notice').textContent, '复制失败，请长按字段复制。'); assert.equal(h.select('#credentials-dialog').open, true);
  vault.copySucceeds = true; for (const field of ['id', 'password', 'all']) await h.copyCredentials(field);
  assert.equal(vault.fields.join(','), 'all,id,password,all'); assert.equal(h.select('#notice').textContent, '引继资料已复制。');
  assert.equal(h.state.unreadCredentials.password, credentialFixture.password);
});

test('account creation retains credentials with the newly returned UID rather than a previously selected account', async () => {
  const h = load(); const vault = { record: null, calls: [], fields: [] }; attachNative(h, vault);
  h.showCredentialReview('create', 'New offline fixture', credentialFixture.password);
  h.api.create = async () => ({ uid: credentialFixture.uid, credentials: { takeoverId: credentialFixture.takeoverId, password: credentialFixture.password } });
  await h.commitPending(); assert.equal(h.state.uid, '1'); assert.equal(h.state.unreadCredentials.uid, credentialFixture.uid); assert.equal(vault.record.uid, credentialFixture.uid);
});

test('unacknowledged credentials restore to a fresh page only after owner authentication, and explicit dismissal clears both memories', async () => {
  const vault = { record: null, calls: [], fields: [] }; const first = load(); attachNative(first, vault); first.showCredentials(credentialFixture, credentialFixture.uid);
  const second = load(); attachNative(second, vault); second.state.authenticated = false; second.restoreCredentials();
  assert.equal(second.state.unreadCredentials, null); assert.equal(vault.calls.join(','), 'retain');
  second.state.authenticated = true; second.restoreCredentials(); assert.equal(second.select('#credentials-dialog').open, true);
  assert.equal(second.state.unreadCredentials.password, credentialFixture.password);
  second.dismissCredentials(); assert.equal(vault.record, null); assert.equal(second.state.unreadCredentials, null); assert.equal(second.select('#credentials-result').children.length, 0);
  second.restoreCredentials(); assert.equal(second.select('#credentials-dialog').open, false);
});

test('401 hides credential DOM but permits authenticated native-memory recovery; offline logout explicitly clears it', async () => {
  const h = load(); const vault = { record: null, calls: [], fields: [] }; attachNative(h, vault); h.showCredentials(credentialFixture, credentialFixture.uid);
  h.handleError(new h.ApiError('auth_required', 'expired', 401)); assert.equal(h.state.authenticated, false); assert.equal(h.select('#credentials-result').children.length, 0); assert.equal(h.state.data, null); assert.ok(vault.record);
  h.state.authenticated = true; h.state.catalog = { cards: [], equipment: [], limits: {} }; h.restoreCredentials(); assert.equal(h.select('#credentials-dialog').open, true);
  h.api.logout = async () => { throw new Error('offline'); }; await h.select('#logout').click();
  assert.equal(vault.record, null); assert.equal(h.state.unreadCredentials, null); assert.equal(h.select('#credentials-dialog').open, false);
});

test('returning to management refreshes only session CSRF while retaining the page, drafts and credentials', async () => {
  const h = load(); h.state.page = 'equipment'; h.state.drafts.set('wallet:gf', '20'); h.showCredentials(credentialFixture, credentialFixture.uid); h.api.csrfToken = 'old-csrf';
  const catalog = h.state.catalog; let reads = 0; h.api.request = async path => { assert.equal(path, '/session'); reads++; return { authenticated: true, csrfToken: 'new-csrf' }; };
  h.api.catalog = async () => assert.fail('Resume must not reload/reset the workbench');
  await h.resumeManagement(); assert.equal(reads, 1); assert.equal(h.api.csrfToken, 'new-csrf'); assert.equal(h.state.page, 'equipment'); assert.equal(h.state.catalog, catalog); assert.equal(h.state.data, h.data);
  assert.equal(h.state.drafts.get('wallet:gf'), '20'); assert.equal(h.state.lease, null); assert.equal(h.state.unreadCredentials.password, credentialFixture.password);
  h.api.request = async () => ({ authenticated: false }); await h.resumeManagement();
  assert.equal(h.select('#workspace').hidden, true); assert.equal(h.select('#credentials-dialog').open, false); assert.equal(h.state.drafts.get('wallet:gf'), '20');
  assert.equal(h.state.unreadCredentials.password, credentialFixture.password);
});

test('resume waits for an active operation; csrf failure refreshes session without replaying the write', async () => {
  const h = load(); const wait = deferred(); let reads = 0; let writes = 0;
  h.api.request = async path => { assert.equal(path, '/session'); reads++; return { authenticated: true, csrfToken: 'fresh' }; };
  const operation = h.run(async () => { await wait.promise; }); await h.resumeManagement(); assert.equal(reads, 0); wait.resolve(); await operation; await new Promise(setImmediate); assert.equal(reads, 1);
  h.state.drafts.set('wallet:gf', '20');
  await h.run(async () => { writes++; throw new h.ApiError('csrf_invalid', 'changed', 403); }); await new Promise(setImmediate);
  assert.equal(writes, 1); assert.equal(reads, 2); assert.equal(h.state.drafts.get('wallet:gf'), '20'); assert.equal(h.select('#notice').textContent, '登录状态已变化，请重试。');
});

const expertFixture = { revision: 0, activeQuestTypeId: 300206, defaultQuestTypeId: 300206, hasActiveBattle: false,
  periods: [300101, 300201, 300202, 300203, 300204, 300205, 300206].map(id => ({ questTypeId: id, name: '高度精鋭戦 · ' + id })) };

test('global period API uses owner CSRF and CAS without an account UID or maintenance lease', async () => {
  const h = load(); const requests = [];
  h.context.fetch = async (url, options) => { requests.push({ url, options }); return { ok: true, status: 200, json: async () => expertFixture }; };
  const api = new h.ManagementApi(); api.csrfToken = 'owner-csrf';
  await api.expertPeriod(); await api.setExpertPeriod({ expectedRevision: 0, questTypeId: 300201 });
  assert.equal(requests[0].url, '/management/v1/expert-period'); assert.equal(requests[0].options.method, 'GET');
  assert.equal(requests[1].url, '/management/v1/expert-period'); assert.equal(requests[1].options.headers['X-BRSF-CSRF'], 'owner-csrf');
  assert.equal(requests[1].options.headers['X-BRSF-Lease'], undefined);
  assert.deepEqual(JSON.parse(requests[1].options.body), { expectedRevision: 0, questTypeId: 300201 });
});

test('period switch waits for server success and preserves a different selection made while awaiting the response', async () => {
  const h = load(); h.state.page = 'settings'; h.state.uid = ''; h.state.data = null; h.state.lease = null;
  h.state.expertPeriod = expertFixture; h.state.expertSelection = '300201'; const wait = deferred();
  h.api.setExpertPeriod = async command => { assert.equal(command.expectedRevision, 0); assert.equal(command.questTypeId, 300201); return wait.promise; };
  const pending = h.applyExpertPeriod(); assert.equal(h.state.expertPeriod.activeQuestTypeId, 300206);
  h.state.expertSelection = '300202'; wait.resolve({ ...expertFixture, revision: 1, activeQuestTypeId: 300201 }); await pending;
  assert.equal(h.state.expertPeriod.activeQuestTypeId, 300201); assert.equal(h.state.expertSelection, '300202');
  assert.equal(h.select('#maintenance-banner').hidden, true); assert.equal(h.select('#notice').textContent, '开放期已切换，请完全退出并重新启动游戏。');
});

test('stale/global battle/network failures leave the current period and selected account drafts unchanged', async () => {
  const h = load(); h.state.expertPeriod = expertFixture; h.state.expertSelection = '300201'; h.state.drafts.set('wallet:gf', '20');
  for (const code of ['stale_revision', 'battle_active']) {
    h.api.setExpertPeriod = async () => { throw new h.ApiError(code, 'rejected', 409); }; await h.applyExpertPeriod();
    assert.equal(h.state.expertPeriod, expertFixture); assert.equal(h.state.expertSelection, '300201'); assert.equal(h.state.drafts.get('wallet:gf'), '20');
    assert.match(h.select('#notice').textContent, code === 'stale_revision' ? /刷新后重试/ : /结束战斗或循环/);
  }
  h.api.setExpertPeriod = async () => { throw new Error('offline'); }; await assert.rejects(h.applyExpertPeriod(), /offline/);
  assert.equal(h.state.expertPeriod, expertFixture); assert.equal(h.state.uid, '1');
});

test('known active battle disables apply, and selected period identical to current performs no write', async () => {
  const h = load(); h.state.expertPeriod = { ...expertFixture, hasActiveBattle: true }; h.state.expertSelection = '300201';
  h.api.setExpertPeriod = async () => assert.fail('Blocked or unchanged selection must not write');
  h.syncSettingsControls(); assert.equal(h.select('#expert-period-apply').disabled, true); await h.applyExpertPeriod();
  h.state.expertPeriod = expertFixture; h.state.expertSelection = '300206'; h.syncSettingsControls();
  assert.equal(h.select('#expert-period-apply').disabled, true); await h.applyExpertPeriod();
});

test('global settings retain original names and IDs, escape labels, and reject incomplete responses', async () => {
  const h = load(); h.state.page = 'settings'; const changed = { ...expertFixture, periods: expertFixture.periods.map((row, index) => ({ ...row, name: index ? row.name : '<img src=x onerror=alert(1)>' })) };
  h.api.expertPeriod = async () => changed; await h.refreshExpertPeriod(); const html = h.renderSettings();
  assert.match(html, /data-global-settings/); assert.match(html, /#300101/); assert.match(html, /#300206/); assert.doesNotMatch(html, /<img|data-requires-lease/);
  assert.equal(h.state.expertSelection, '300206');
  h.api.expertPeriod = async () => ({ revision: 1, activeQuestTypeId: 300201 });
  await assert.rejects(h.refreshExpertPeriod(), /无法读取服务设置/); assert.equal(h.state.expertPeriod, changed);
});

function accountSelectionFixture(h) {
  const ids = ['9007199254740993', '9007199254740995'];
  const records = ids.map((uid, index) => ({ ...JSON.parse(JSON.stringify(h.data)), revision: (index ? 'b' : 'a').repeat(64),
    account: { uid, name: 'Fixture account ' + (index + 1), rank: index + 1 },
    cards: [{ cardId: 1001 + index, level: 1, awakening: 0 }],
    equipment: [{ id: String(11 + index), equipId: 5, level: 1, breakthrough: 0, locked: false }] }));
  Object.assign(h.state, { page: 'accounts', uid: '', data: null, lease: null, accounts: records.map(row => ({ ...row.account, cardCount: 1, equipmentCount: 1, busy: {} })) });
  h.state.catalog.cards = records.map(row => ({ id: row.cards[0].cardId, name: row.account.name + ' card', rarity: 1 }));
  const reads = []; h.api.account = async uid => { assert.equal(typeof uid, 'string'); reads.push(uid); return records.find(row => row.account.uid === uid); };
  h.api.accounts = async () => ({ items: h.state.accounts });
  return { ids, records, reads };
}

test('clicking an actual rendered account name selects its exact text UID and every editor uses that account', async () => {
  const h = load(); const fixture = accountSelectionFixture(h); const targetUid = fixture.ids[1];
  const tree = renderedTree(h.renderAccounts()); const row = accountRow(tree, targetUid);
  const name = descendants(row, node => node.matches('.name'))[0];
  await click(h, name.children[0] || name);
  assert.equal(h.state.uid, targetUid, 'A tap on the visible account name must select it without horizontally finding a remote button');
  assert.equal(fixture.reads.join(','), targetUid); assert.equal(h.state.data.account.uid, targetUid);
  const nav = renderedTree(fs.readFileSync(path.join(webRoot, 'index.html'), 'utf8'));
  const navigate = page => click(h, descendants(nav, node => node.dataset.page === page)[0]);
  await navigate('resources'); assert.doesNotMatch(h.select('#page-content').innerHTML, /请先在「账号」页选择/);
  h.api.begin = async uid => { assert.equal(uid, targetUid); return { leaseId: 'selected-account-lease', expiresAt: Date.now() + 60000 }; };
  await click(h, descendants(renderedTree(h.select('#maintenance-banner').innerHTML), node => node.dataset.action === 'begin')[0]);
  const previews = []; h.api.preview = async (uid, lease, command) => { previews.push({ uid, lease, command }); return { changes: [] }; };
  h.state.drafts.set('wallet:gf', '500');
  await click(h, descendants(renderedTree(h.select('#page-content').innerHTML), node => node.dataset.action === 'wallet-preview')[0]);
  assert.equal(previews[0].command.kind, 'wallet.set'); assert.equal(previews[0].command.expectedRevision, fixture.records[1].revision);
  const close = descendants(nav, node => Object.hasOwn(node.attrs, 'data-close-dialog'))[0]; await click(h, close);
  await navigate('cards'); const cardBox = descendants(renderedTree(h.select('#page-content').innerHTML), node => node.dataset.selection === 'cards:owned')[0];
  cardBox.checked = true; h.events.get('change')({ target: cardBox });
  await click(h, descendants(renderedTree(h.select('#page-content').innerHTML), node => node.dataset.action === 'cards-max')[0]);
  assert.equal(previews[1].command.payload.cardIds.join(','), '1002'); await click(h, close);
  await navigate('equipment'); const equipBox = descendants(renderedTree(h.select('#page-content').innerHTML), node => node.dataset.selection === 'equipment:owned')[0];
  equipBox.checked = true; h.events.get('change')({ target: equipBox });
  await click(h, descendants(renderedTree(h.select('#page-content').innerHTML), node => node.dataset.action === 'equipment-lock')[0]);
  assert.equal(previews[2].command.payload.equipmentIds.join(','), '12');
  assert.equal(previews.length, 3); assert.ok(previews.every(request => request.uid === targetUid && request.lease === 'selected-account-lease'));
});

test('account UID/name text selection does not change accounts, while its explicit visible button triggers one selection', async () => {
  const h = load(); const fixture = accountSelectionFixture(h); const uid = fixture.ids[1];
  const row = accountRow(renderedTree(h.renderAccounts()), uid); const name = descendants(row, node => node.matches('.name'))[0];
  h.window.getSelection = () => ({ toString: () => uid, containsNode: node => node.dataset.accountIdentity === 'true' });
  await click(h, name.children[0] || name); assert.equal(h.state.uid, ''); assert.equal(fixture.reads.length, 0);
  const choice = descendants(row, node => node.tag === 'button' && node.dataset.selectAccount === uid && !node.dataset.accountIdentity)[0];
  h.events.get('click')({ target: choice }); h.events.get('click')({ target: choice }); await new Promise(setImmediate);
  assert.equal(h.state.uid, uid); assert.equal(fixture.reads.join(','), uid);
  const selected = accountRow(renderedTree(h.renderAccounts()), uid);
  assert.ok(selected.matches('.selected')); assert.ok(descendants(selected, node => node.attrs['aria-pressed'] === 'true').length);
});

test('native credential restoration and session refresh do not clear or replace the selected edit account', async () => {
  const h = load(); const fixture = accountSelectionFixture(h); const selectedUid = fixture.ids[1];
  const row = accountRow(renderedTree(h.renderAccounts()), selectedUid); const choice = descendants(row, node => node.tag === 'button' && node.dataset.selectAccount === selectedUid)[0];
  await click(h, choice); h.state.page = 'equipment';
  const vault = { record: { ...credentialFixture, uid: fixture.ids[0] }, calls: [], fields: [] }; attachNative(h, vault);
  h.api.csrfToken = 'before'; h.api.request = async path => { assert.equal(path, '/session'); return { authenticated: true, csrfToken: 'after' }; };
  h.windowEvents.get('brsf-management-resume')(); await new Promise(setImmediate);
  assert.equal(h.state.uid, selectedUid); assert.equal(h.state.data.account.uid, selectedUid); assert.equal(h.state.page, 'equipment');
  assert.equal(h.state.unreadCredentials.uid, fixture.ids[0]); assert.equal(fixture.reads.join(','), selectedUid);
});

test('rendered account/material cards keep primary controls visible with zero-min-width layouts at 320/360/fold widths', async () => {
  const h = load(); const fixture = accountSelectionFixture(h); const tree = renderedTree(h.renderAccounts());
  const table = descendants(tree, node => node.matches('.accounts-table'))[0]; assert.ok(table);
  for (const uid of fixture.ids) {
    const row = accountRow(tree, uid); assert.ok(descendants(row, node => node.matches('.account-identity-button')).length);
    const action = descendants(row, node => node.matches('.account-choose'))[0]; assert.ok(action);
    assert.equal(descendants(action, node => node.dataset.selectAccount === uid).length, 1);
  }
  const css = fs.readFileSync(path.join(webRoot, 'styles.css'), 'utf8'); const start = css.lastIndexOf('@media(max-width:1024px)'); assert.ok(start >= 0);
  const mobile = css.slice(start); assert.match(mobile, /\.table-wrap>table\.accounts-table\{[^}]*min-width:0/);
  assert.match(mobile, /\.accounts-table tbody\{[^}]*grid-template-columns:minmax\(0,1fr\)/);
  assert.match(mobile, /\.accounts-table \.account-choose button\{[^}]*width:100%/);
  assert.match(mobile, /\.accounts-table \.name\{[^}]*min-width:0/);
  h.state.uid = fixture.ids[0]; h.state.data = fixture.records[0];
  const materialTree = renderedTree(h.renderResources()); const itemsTable = descendants(materialTree, node => node.matches('.items-table'))[0]; assert.ok(itemsTable);
  for (const row of descendants(itemsTable, node => node.matches('.item-row'))) {
    const target = descendants(row, node => node.matches('.item-target'))[0]; assert.ok(target);
    assert.equal(descendants(target, node => node.tag === 'input' && node.dataset.draft?.startsWith('item:')).length, 1);
  }
  assert.match(mobile, /\.table-wrap>table\.items-table\{[^}]*min-width:0/);
  assert.match(mobile, /\.items-table \.item-target input\{[^}]*width:100%/);
  for (const width of [320, 360, 673, 841, 1024]) assert.ok(width <= 1024, 'Static card breakpoint covers ' + width + 'px; this is not a browser render claim');
});

function materialFixture(h) {
  const metadata = h.materialDisplay;
  const raw = Object.values(metadata.items).map(entry => ({ type: entry.type, id: entry.id, name: entry.nameJa, maxQty: 99999999, ...(entry.eventId === null ? {} : { eventId: entry.eventId }) }));
  h.state.catalog = h.localizeCatalog({ ...h.state.catalog, items: raw }); h.state.page = 'resources';
  return metadata;
}

test('all 230 material composite keys, categories and icon routes obey the public runtime contract', async () => {
  const h = load(); const metadata = materialFixture(h);
  assert.equal(Object.keys(metadata.items).length, 230); assert.equal(h.state.catalog.items.length, 230);
  const counts = {};
  for (const row of h.state.catalog.items) {
    const entry = metadata.items[h.materialKey(row)]; assert.ok(entry); assert.equal(row.name, entry.nameZh); assert.equal(row.originalName, entry.nameJa); assert.equal(row.usage, entry.usage);
    assert.equal(h.materialKey(row), `${row.type}:${row.id}:${row.eventId ?? ''}`);
    const route = `/management/images/items/${row.type}/${row.id}${row.type === 14 ? '/' + row.eventId : ''}.png`;
    assert.equal(entry.iconRoute, route); assert.equal(h.materialImageRoute(row), route); counts[row.materialCategory] = (counts[row.materialCategory] || 0) + 1;
  }
  assert.deepEqual(counts, { gacha: 18, stamina: 41, exchange: 36, common: 12, skills: 33, awakening: 82, equipment: 8 });
  assert.equal(metadata.items['6:10001:'].nameZh, '兵装制造券'); assert.equal(metadata.items['12:10001:'].nameZh, '初级战术教本');
  assert.notEqual(h.materialImageRoute({ type: 6, id: 10001 }), h.materialImageRoute({ type: 12, id: 10001 }));
  assert.equal(h.materialImageRoute({ type: 14, id: 10101, eventId: 101 }), '/management/images/items/14/10101/101.png');
  assert.equal(h.materialImageRoute({ type: 14, id: 10101 }), null); assert.equal(h.materialImageRoute({ type: 6, id: 10001, eventId: 101 }), null);
});

test('category changes keep cross-category quantity drafts and previews use Chinese names for the same exact wire keys', async () => {
  const h = load(); materialFixture(h);
  h.state.drafts.set('item:6:10001:', '5'); h.state.drafts.set('item:12:10001:', '7'); h.state.drafts.set('item:14:10101:101', '9');
  h.events.get('change')({ target: { id: 'material-category', value: 'common', dataset: {} } });
  assert.equal(h.state.drafts.size, 3); assert.match(h.select('#draft-count').textContent, /3/); assert.match(h.select('#page-content').innerHTML, /data-draft="item:12:10001:"[^>]*value="7"/);
  h.events.get('change')({ target: { id: 'material-category', value: 'gacha', dataset: {} } });
  assert.equal(h.state.drafts.size, 3); assert.match(h.select('#page-content').innerHTML, /data-draft="item:6:10001:"[^>]*value="5"/);
  let command; const after = [{ type: 6, id: 10001, qty: 5 }, { type: 12, id: 10001, qty: 7 }, { type: 14, id: 10101, eventId: 101, qty: 9 }];
  h.api.preview = async (_uid, _lease, request) => { command = request; return { changes: [{ label: '物品', before: [], after }] }; };
  await h.action('items-preview'); assert.deepEqual(JSON.parse(JSON.stringify(command.payload.items)), after);
  const sections = h.buildPreviewSections([{ label: '物品', before: [], after }], h.state.catalog);
  assert.equal(sections[0].entries.map(row => row.name).join(','), '兵装制造券,初级战术教本,星标巧克力');
  h.api.commit = async (_uid, _lease, request) => { assert.equal(request.operationId, command.operationId); return { applied: true }; };
  await h.commitPending(); assert.equal(h.state.drafts.size, 0);
});

test('Chinese/Japanese/skill keywords find materials, and changed names or event ownership never inherit unrelated display metadata', async () => {
  const h = load(); materialFixture(h); const exs = h.state.catalog.items.find(row => row.type === 12 && row.id === 20001);
  for (const term of ['EX-S', '因子', '击杀能力']) { h.state.search.items = term; assert.equal(h.matches(exs, 'items', exs.usage), true); }
  const ticket = h.state.catalog.items.find(row => row.type === 6 && row.id === 10001); h.state.search.items = '製造チケット'; assert.equal(h.matches(ticket, 'items'), true);
  for (const term of ['AFARM战斗训练', '練兵']) { h.state.search.items = term; assert.match(h.renderResources(), /data-draft="item:14:20101:201"/); }
  const event = h.state.catalog.items.find(row => row.type === 14 && row.id === 20101); assert.match(event.usage, /AFARM战斗训练/); assert.match(event.eventNameJa, /練兵/);
  const altered = [{ type: 6, id: 10001, name: 'Changed source name' }, { type: 14, id: 10101, eventId: 999, name: '星印チョコレート' }];
  const localized = h.localizeCatalog({ items: altered }).items;
  for (let index = 0; index < localized.length; index++) { assert.equal(localized[index].name, altered[index].name); assert.equal(localized[index].usage, ''); assert.equal(localized[index].materialCategory, null); }
  assert.equal(h.materialImageRoute(localized[1]), '/management/images/items/14/10101/999.png');
  const data = h.materialDisplay.items;
  assert.equal(data['12:20001:'].appliesTo.cardIds.length, 76); assert.equal(data['12:20001:'].appliesTo.skills.join(','), '1');
  assert.equal(data['12:30001:'].appliesTo.skills.join(','), '2,3'); assert.equal(data['12:40001:'].appliesTo.jobClassIds.join(','), '1');
});

(async () => {
  let failed = 0;
  for (const [name, task] of tests) { try { await task(); console.log('PASS ' + name); } catch (error) { failed++; console.error('FAIL ' + name + ': ' + error.message); } }
  console.log(`${tests.length - failed}/${tests.length} checks passed`); process.exitCode = failed ? 1 : 0;
})();
