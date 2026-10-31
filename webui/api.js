import { t, apiErrorMessage } from './i18n.js';
const PREFIX = '/management/v1';

export class ApiError extends Error {
  constructor(code, message, status = 0) {
    super(message);
    this.name = 'ApiError';
    this.code = code;
    this.status = status;
  }
}

export function operationId() {
  return crypto.randomUUID();
}

export function randomPassword() {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789';
  let result = '';
  while (result.length < 12) {
    const bytes = crypto.getRandomValues(new Uint8Array(24));
    for (const byte of bytes) {
      if (byte < Math.floor(256 / alphabet.length) * alphabet.length) result += alphabet[byte % alphabet.length];
      if (result.length === 12) break;
    }
  }
  return result;
}

export function expiryMillis(value) {
  if (typeof value === 'number') return value < 1e12 ? value * 1000 : value;
  return Date.parse(value);
}

export class ManagementApi {
  constructor() { this.csrfToken = ''; }
  async request(path, { method = 'GET', body, leaseId, download = false } = {}) {
    const headers = { Accept: download ? 'application/octet-stream, application/json' : 'application/json' };
    if (method !== 'GET') {
      headers['Content-Type'] = 'application/json';
      if (this.csrfToken) headers['X-BRSF-CSRF'] = this.csrfToken;
    }
    if (leaseId) headers['X-BRSF-Lease'] = leaseId;
    let response;
    try {
      response = await fetch(PREFIX + path, { method, headers, credentials: 'same-origin', cache: 'no-store', ...(method !== 'GET' ? { body: JSON.stringify(body ?? {}) } : {}) });
    } catch {
      throw new ApiError('network_error', t("无法连接本地管理服务。请检查服务是否仍在运行。"));
    }
    if (!response.ok) {
      let error;
      try { error = (await response.json()).error; } catch { /* Safe fallback only. */ }
      throw new ApiError(error?.code || 'request_failed', apiErrorMessage(error?.code || 'request_failed', response.status), response.status);
    }
    if (download) return { blob: await response.blob(), disposition: response.headers.get('Content-Disposition') };
    if (response.status === 204) return null;
    return response.json();
  }
  async session() { const data = await this.request('/session'); this.csrfToken = data.csrfToken || ''; return data; }
  async login(key) { const data = await this.request('/session', { method: 'POST', body: { key } }); this.csrfToken = data.csrfToken || ''; return data; }
  async logout() { try { return await this.request('/session', { method: 'DELETE' }); } finally { this.csrfToken = ''; } }
  status() { return this.request('/status'); }
  expertPeriod() { return this.request('/expert-period'); }
  setExpertPeriod(body) { return this.request('/expert-period', { method: 'POST', body }); }
  catalog() { return this.request('/catalog'); }
  accounts() { return this.request('/accounts'); }
  account(uid) { return this.request(`/accounts/${encodeURIComponent(uid)}`); }
  begin(uid) { return this.request(`/accounts/${encodeURIComponent(uid)}/maintenance`, { method: 'POST' }); }
  renew(uid, leaseId) { return this.request(`/accounts/${encodeURIComponent(uid)}/maintenance/renew`, { method: 'POST', leaseId }); }
  release(uid, leaseId) { return this.request(`/accounts/${encodeURIComponent(uid)}/maintenance`, { method: 'DELETE', leaseId }); }
  preview(uid, leaseId, command) { return this.request(`/accounts/${encodeURIComponent(uid)}/preview`, { method: 'POST', leaseId, body: command }); }
  commit(uid, leaseId, command) { return this.request(`/accounts/${encodeURIComponent(uid)}/commands`, { method: 'POST', leaseId, body: command }); }
  create(command) { return this.request('/accounts', { method: 'POST', body: command }); }
  credentials(uid, leaseId, command) { return this.request(`/accounts/${encodeURIComponent(uid)}/credentials`, { method: 'POST', leaseId, body: command }); }
  export(uid) { return this.request(`/accounts/${encodeURIComponent(uid)}/export`, { download: true }); }
  backup() { return this.request('/backup', { download: true }); }
}

// All service DTO adaptation stays in this module; no browser-local gameplay state.
export function catalogRows(catalog, category) { return catalog?.[category] || []; }
export function definitionId(row, category) {
  const key = { cards: 'cardId', equipment: 'equipId', items: 'id', skins: 'skinId', backgrounds: 'id', titles: 'id' }[category];
  return row?.[key] ?? row?.id;
}
export function definitionName(row) { return row?.name || row?.label || ''; }
export function definitionLimits(row) { return row?.limits || {}; }

export function growthLimits(row, growth, category) {
  const steps = row?.growth || [];
  const step = category === 'cards' ? steps.find(x => x.awakening === Number(growth.awakening)) : steps.find(x => x.breakthrough === Number(growth.breakthrough));
  return category === 'cards' ? {
    level: [1, step?.maxLevel], awakening: [0, row?.maxAwakening],
    mainSkillLevel: [1, step?.maxMainSkillLevel], killAbilityLevel: [1, step?.maxKillAbilityLevel],
    killZoneSkillLevel: [1, step?.maxKillZoneSkillLevel], masteryLevel: [1, row?.maxMasteryLevel]
  } : { level: [1, step?.maxLevel], breakthrough: [0, row?.maxBreakthrough] };
}
