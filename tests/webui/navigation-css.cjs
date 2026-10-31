const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const base = path.resolve(__dirname, '../../webui');
const { JSDOM } = require('jsdom');
const fixed = fs.readFileSync(path.join(base, 'styles.css'), 'utf8');
// Synthetic mutation of the fixed selectors proves this suite detects broad-span hiding.
const previous = fixed.replaceAll('.sidebar nav button > span[aria-hidden="true"]', '.sidebar nav button span').replaceAll('.sidebar-bottom > #server-status', '.sidebar-bottom span');
const html = fs.readFileSync(path.join(base, 'index.html'), 'utf8');
const i18n = fs.readFileSync(path.join(base, 'i18n.js'), 'utf8').replaceAll(/^export /gm, '');
const app = fs.readFileSync(path.join(base, 'app.js'), 'utf8');
const staticLanguage = app.slice(app.indexOf('function applyStaticLanguage()'), app.indexOf('function snapshotFields()'));
const iconSelector = '.sidebar nav button > span[aria-hidden="true"]';

function load(css, width, language) {
  const dom = new JSDOM(html, { url: 'http://127.0.0.1:18180/', runScripts: 'outside-only' });
  const w = dom.window;
  w.document.querySelector('#workspace').hidden = false;
  const sheet = w.document.createElement('style'); sheet.textContent = css; w.document.head.append(sheet);
  // jsdom has no viewport/media renderer. Resolve the actual CSSOM media rules
  // for each tested viewport, preserving source order, then compute the cascade.
  const activeRules = [];
  function visit(rules) {
    for (const rule of rules) {
      if (rule.type === 4) {
        const media = rule.conditionText;
        const max = media.match(/max-width\s*:\s*(\d+)px/);
        const min = media.match(/min-width\s*:\s*(\d+)px/);
        assert.ok(max || min, 'Unmodeled media query: ' + media);
        if ((!max || width <= Number(max[1])) && (!min || width >= Number(min[1]))) visit(rule.cssRules);
      } else if (rule.type === 1) activeRules.push(rule);
    }
  }
  visit(sheet.sheet.cssRules);
  const activeCss = activeRules.map(rule => rule.cssText).join('\n');
  sheet.remove();
  const active = w.document.createElement('style'); active.textContent = activeCss; w.document.head.append(active);
  vm.runInContext(i18n + '\nconst $ = selector => document.querySelector(selector);\n' + staticLanguage + '\n' +
    `setLanguagePreference(${JSON.stringify(language)});applyStaticLanguage();`, dom.getInternalVMContext());
  return { dom, w, rules: activeRules };
}
function hiddenRules(h, element) {
  return h.rules.filter(rule => rule.style.getPropertyValue('display') === 'none' && element.matches(rule.selectorText));
}
function assertVisible(h, element, description) {
  for (let current = element; current; current = current.parentElement) {
    const style = h.w.getComputedStyle(current);
    assert.notEqual(style.display, 'none', description + ' display:none via ' + current.outerHTML.slice(0, 100));
    assert.notEqual(style.visibility, 'hidden', description + ' visibility:hidden');
    assert.notEqual(style.visibility, 'collapse', description + ' visibility:collapse');
    assert.notEqual(style.opacity, '0', description + ' opacity:0');
    assert.equal(current.hidden, false, description + ' hidden attribute on ancestor');
  }
  assert.equal(hiddenRules(h, element).length, 0, description + ' matches hidden CSS');
}
let checks = 0;
for (const language of ['zh', 'ja', 'en']) {
  for (const width of [360, 420, 760, 1024, 1180, 1280]) {
    const h = load(fixed, width, language);
    const labels = [...h.w.document.querySelectorAll('.sidebar nav button > span[data-i18n]')];
    const icons = [...h.w.document.querySelectorAll(iconSelector)];
    assert.equal(labels.length, 6); assert.equal(icons.length, 6);
    for (const label of labels) {
      assert.ok(label.textContent.trim(), language + ' has an empty navigation label');
      assert.equal(label.matches(iconSelector), false);
      assertVisible(h, label, `${language}/${width}/${label.textContent}`);
      const style = h.w.getComputedStyle(label);
      assert.notEqual(style.width, '22px', 'label gets icon width');
      assert.notEqual(style.fontSize, '19px', 'label gets icon font size');
      checks++;
    }
    for (const icon of icons) {
      assert.ok(icon.matches(iconSelector));
      assert.equal(h.rules.filter(rule => rule.selectorText === iconSelector && rule.style.width === '22px').length, 1);
      const phone = width <= 760;
      assert.equal(h.w.getComputedStyle(icon).display === 'none', phone, `icon responsive display at ${width}`);
      checks++;
    }
    const logout = h.w.document.querySelector('#logout > span[data-i18n]');
    assert.ok(logout.textContent.trim()); assertVisible(h, logout, `${language}/${width}/logout`);
    assert.equal(logout.matches('.sidebar-bottom > #server-status'), false);
    checks++;
    const status = h.w.document.querySelector('#server-status');
    assert.equal(h.w.getComputedStyle(status).display === 'none', width <= 760, 'status retains original responsive hiding');
    checks++;
    h.dom.window.close();
  }
}
// A synthetic selector mutation must hide labels and be detected.
const broken = load(previous, 760, 'en');
assert.equal([...broken.w.document.querySelectorAll('.sidebar nav button > span[data-i18n]')].filter(e => hiddenRules(broken, e).length).length, 6);
assert.ok(hiddenRules(broken, broken.w.document.querySelector('#logout > span[data-i18n]')).length);
broken.dom.window.close();
console.log(`Navigation CSS checks passed: ${checks} assertions across 3 languages and 6 viewport widths; broad-span selector mutation detected.`);
console.log('Boundary: actual CSSOM selectors/cascade checked with media rules resolved for each width; no browser layout or physical-device visibility claim.');
