const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { webcrypto } = require('node:crypto');
const { JSDOM } = require('jsdom');
const acorn = require('acorn');
const root = path.resolve(__dirname, '../../webui');
const tests=[];
function test(name,run){tests.push([name,run]);}
const clean = file => fs.readFileSync(path.join(root,file),'utf8').replaceAll(/^import .*\r?\n/gm,'').replaceAll(/^export /gm,'');
function load({languages=['zh-CN'],preference=null,host=null}={}) {
  const dom = new JSDOM(fs.readFileSync(path.join(root,'index.html'),'utf8'),{url:'http://127.0.0.1:18180/',runScripts:'outside-only'});
  const w=dom.window;
  Object.defineProperty(w.navigator,'languages',{value:languages,configurable:true});
  Object.defineProperty(w,'crypto',{value:webcrypto});
  if(preference)w.localStorage.setItem('brsf.language',preference);
  if(host)w.BrsHostLanguage=host;
  w.setInterval=()=>1;w.setTimeout=()=>1;w.clearTimeout=()=>{};w.confirm=()=>true;
  let fetches=0;w.fetch=async()=>{fetches++;throw Error('Unrequested network operation');};
  w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};
  let app=clean('app.js').replace(/run\(async \(\) => \{ const session = await api\.session\(\); if \(session\.authenticated\) await loadWorkbench\(\); else showLogin\(\); \}\);/,'');
  vm.runInContext(['i18n.js','catalog-locales.js','api.js'].map(clean).join('\n')+'\n'+app+`\nglobalThis.h={t,apiErrorMessage,getLanguage,getLocale,getPreference,setLanguagePreference,resolveLanguage,syncHostLanguage,refreshLanguageUi,localizeCatalog,catalogLocales,materialDisplay,localDisplayNames,messages,api,state,render,renderResources,renderCards,renderEquipment,renderSettings,openGrowth,openGrant,showCredentialReview,preview,handleError,numeric,showCredentials,copyCredentials,readablePreview,buildPreviewSections,showLogin};`,dom.getInternalVMContext());
  const h=w.h;h.window=w;h.document=w.document;h.dom=dom;h.fetches=()=>fetches;
  h.state.data={revision:'a'.repeat(64),account:{uid:'9007199254740993',name:'玩家 User <x>',rank:4,createdAt:1700000000},resources:{freeGems:1,paidGems:2,gf:3,friendPoints:4,stamina:5,staminaMax:10},cards:[{cardId:50010001,level:1,awakening:0}],equipment:[{id:'9007199254740995',equipId:110100103,level:1,breakthrough:0,locked:false}],items:[],collections:{skins:[],backgroundIds:[],titleIds:[]},busy:{}};
  h.state.uid=h.state.data.account.uid;h.state.authenticated=true;h.state.lease={leaseId:'lease-stable',expiresAt:Date.now()+120000};
  h.state.rawCatalog={cards:[{id:50010001,name:h.localDisplayNames.cards['50010001'].nameJa,rarity:5,maxAwakening:5,maxMasteryLevel:10,growth:[{awakening:0,maxLevel:50,maxMainSkillLevel:3,maxKillAbilityLevel:3,maxKillZoneSkillLevel:3}]}],equipment:[{id:110100103,name:h.localDisplayNames.equipment['110100103'].nameJa,rarity:3,maxBreakthrough:5,growth:[{breakthrough:0,maxLevel:20}]}],items:Object.values(h.materialDisplay.items).map(x=>({type:x.type,id:x.id,name:x.nameJa,maxQty:99999999,...(x.eventId==null?{}:{eventId:x.eventId})})),backgrounds:[],titles:[],skins:[],limits:{maxEquipment:100,maxItemsPerCommand:200,maxFreeGems:999,maxPaidGems:999,maxGf:999,maxFriendPoints:999,maxStamina:999}};
  h.state.catalog=h.localizeCatalog(h.state.rawCatalog);h.state.accounts=[{...h.state.data.account,cardCount:1,equipmentCount:1,busy:{}}];h.render();
  return h;
}
function assertStable(h,previous){assert.equal(h.state.uid,previous.uid);assert.equal(h.state.lease,previous.lease);assert.equal(h.state.pending,previous.pending);assert.equal(h.state.pending?.operationId,previous.operationId);assert.equal(h.state.pending?.command?.operationId,previous.commandId);assert.equal(h.state.drafts,previous.drafts);assert.equal(h.state.drafts.get('wallet:gf'),'77');assert.equal(h.state.selections,previous.selections);assert.equal(h.fetches(),0);}
function saveState(h){h.state.drafts.set('wallet:gf','77');h.state.selections.set('cards:owned',new Set(['50010001']));return {uid:h.state.uid,lease:h.state.lease,pending:h.state.pending,operationId:h.state.pending?.operationId,commandId:h.state.pending?.command?.operationId,drafts:h.state.drafts,selections:h.state.selections};}

test('system locale uses the first preferred language and falls back to English',()=>{
  for(const [languages,expected] of [[['zh-Hant-HK'],'zh'],[['ja-JP'],'ja'],[['en-GB'],'en'],[['fr-FR','ja-JP'],'en'],[[],'en']]){const h=load({languages});assert.equal(h.getLanguage(),expected);assert.equal(h.getPreference(),'system');h.dom.window.close();}
});
test('explicit preference persists across reloads and automatic remains automatic',()=>{
  const h=load({languages:['ja-JP']});h.setLanguagePreference('en');assert.equal(h.window.localStorage.getItem('brsf.language'),'en');const h2=load({languages:['ja-JP'],preference:h.window.localStorage.getItem('brsf.language')});assert.equal(h2.getLanguage(),'en');h.setLanguagePreference('system');assert.equal(h.getLanguage(),'ja');assert.equal(h.window.localStorage.getItem('brsf.language'),'system');
});
test('late native bridge overrides browser storage and native events never overwrite system preference with resolved language',()=>{
  const h=load({languages:['zh-CN'],preference:'en'});h.window.BrsHostLanguage={preference:'system',language:'ja',systemLanguages:['ja-JP']};h.window.dispatchEvent(new h.window.CustomEvent('brs-language-change',{detail:h.window.BrsHostLanguage}));assert.equal(h.getPreference(),'system');assert.equal(h.getLanguage(),'ja');assert.equal(h.document.documentElement.lang,'ja-JP');
  const sent=[];h.window.BrsHost={setLanguage:p=>sent.push(p)};h.setLanguagePreference('zh');assert.deepEqual(sent,['zh']);assert.equal(h.window.BrsHostLanguage.systemLanguages[0],'ja-JP');
});
test('native preference is authoritative at initial document load',()=>{const h=load({preference:'zh',host:{preference:'en',language:'en'}});assert.equal(h.getPreference(),'en');assert.equal(h.document.querySelector('#language-select').value,'en');});
test('every stable source-message key has complete locales and identical placeholders',()=>{
  const h=load();for(const [key,row] of Object.entries(h.messages)){const expected=[...key.matchAll(/\{p\d+\}/g)].map(m=>m[0]).sort();for(const language of ['zh','ja','en']){assert.ok(row[language],key);assert.deepEqual([...row[language].matchAll(/\{p\d+\}/g)].map(m=>m[0]).sort(),expected,key);}}
  const visit=n=>{if(!n||typeof n!=='object')return;if(n.type==='CallExpression'&&n.callee.name==='t'&&n.arguments[0]?.type==='Literal')assert.ok(h.messages[n.arguments[0].value],n.arguments[0].value);for(const x of Object.values(n))if(Array.isArray(x))x.forEach(visit);else if(x&&typeof x==='object')visit(x);};visit(acorn.parse(fs.readFileSync(path.join(root,'app.js'),'utf8'),{ecmaVersion:'latest',sourceType:'module'}));
});
test('language selector changes all static navigation, login, dialogs and document locale',()=>{
  const h=load();for(const [language,account,label] of [['ja','アカウント','言語'],['en','Accounts','Language'],['zh','账号','语言']]){h.setLanguagePreference(language);assert.equal(h.document.querySelector('[data-page="accounts"]').textContent.replace('▤',''),account);assert.equal(h.document.querySelector('[for="language-select"]').textContent,label);assert.equal(h.document.querySelector('#review-dialog [data-close-dialog]').getAttribute('aria-label'),h.t('关闭'));assert.equal(h.document.querySelector('#owner-key').value,'');}
});
test('switching language preserves selected account, lease, drafts and new account input before preview without API calls',()=>{
  const h=load();const previous=saveState(h);h.document.querySelector('#create-name').value='Name 甲';h.document.querySelector('#create-password').value='AbCd123456';h.document.querySelector('#reset-password').value='Reset123';h.document.querySelector('#owner-key').value='private-key';
  for(const language of ['ja','en','zh']){h.setLanguagePreference(language);assertStable(h,previous);assert.equal(h.document.querySelector('#create-name').value,'Name 甲');assert.equal(h.document.querySelector('#create-password').value,'AbCd123456');assert.equal(h.document.querySelector('#reset-password').value,'Reset123');assert.equal(h.document.querySelector('#owner-key').value,'private-key');}
});
test('pending account creation retains operation ID and password through language changes',()=>{
  const h=load();h.showCredentialReview('create','New 甲','Secret123');const previous=saveState(h);h.setLanguagePreference('en');assertStable(h,previous);assert.equal(h.state.pending.password,'Secret123');assert.equal(h.document.querySelector('#review-title').textContent,'Confirm account creation');assert.equal(h.document.querySelector('#review-dialog').open,true);
});
test('pending credential reissue retains operation ID and exact account UID',()=>{
  const h=load();h.showCredentialReview('credentials','','Secret123');const previous=saveState(h);h.setLanguagePreference('ja');assertStable(h,previous);assert.equal(h.state.pending.password,'Secret123');assert.match(h.document.querySelector('#review-body').textContent,/9007199254740993/);
});
test('pending command preview re-renders names and growth fields while retaining exact operation and cleanup',async()=>{
  const h=load();let previews=0;h.api.preview=async()=>{previews++;return {changes:[{label:'兵装',before:[{cardId:50010001,level:1,awakening:0}],after:[{cardId:50010001,level:5,awakening:1}]}]};};const cleanup=()=>{};await h.preview('cards.growth',{cards:[{cardId:50010001,level:5}]},cleanup);const previous=saveState(h);h.setLanguagePreference('en');assertStable(h,previous);assert.equal(h.state.pending.cleanup,cleanup);assert.equal(previews,1);assert.match(h.document.querySelector('#review-body').textContent,/Elishka \[AFARM Standard Armament\]/);assert.match(h.document.querySelector('#review-body').textContent,/Awakening/);assert.doesNotMatch(h.document.querySelector('#review-body').textContent,/觉醒|兵装|修改前/);
});
test('growth dialog unsaved numeric and lock inputs survive language changes',()=>{
  const h=load();h.openGrowth('equipment','9007199254740995');h.document.querySelector('#growth-level').value='17';h.document.querySelector('#growth-breakthrough').value='2';h.document.querySelector('#growth-form [name="locked"]').checked=true;const previous=saveState(h);h.setLanguagePreference('en');assertStable(h,previous);assert.equal(h.document.querySelector('#growth-level').value,'17');assert.equal(h.document.querySelector('#growth-breakthrough').value,'2');assert.equal(h.document.querySelector('#growth-form [name="locked"]').checked,true);assert.match(h.document.querySelector('#review-body').textContent,/Limit break/);
});
test('equipment grant quantity and lock input survive language changes',()=>{
  const h=load();h.openGrant(110100103);h.document.querySelector('#grant-quantity').value='7';h.document.querySelector('#grant-form [name="locked"]').checked=true;const previous=saveState(h);h.setLanguagePreference('ja');assertStable(h,previous);assert.equal(h.document.querySelector('#grant-quantity').value,'7');assert.equal(h.document.querySelector('#grant-form [name="locked"]').checked,true);assert.match(h.document.querySelector('#review-title').textContent,/Z／G ナイフ/);
});
test('unacknowledged credentials and copy actions survive language changes without native retain or writes',async()=>{
  const h=load();h.showCredentials({takeoverId:'AbCd1234EfGh5678',password:'Secret123'},h.state.uid);const record=h.state.unreadCredentials;let prompts=0;h.window.__BRSF_NATIVE_CREDENTIALS__=true;h.window.prompt=()=>{prompts++;return '{}';};h.setLanguagePreference('en');assert.equal(h.state.unreadCredentials,record);assert.equal(prompts,0);assert.equal(h.document.querySelector('#credential-value-password').value,'Secret123');assert.equal(h.document.querySelector('#credential-value-password').getAttribute('aria-label'),'Password');assert.equal(h.document.querySelector('#credentials-dialog').open,true);
  h.window.__BRSF_NATIVE_CREDENTIALS__=false;let copied='';h.window.navigator.clipboard={writeText:async x=>{copied=x;}};await h.copyCredentials('all');assert.equal(copied,'ID: AbCd1234EfGh5678\nPASS: Secret123');assert.equal(h.document.querySelector('#notice').textContent,'Transfer credentials copied.');
});
test('all 230 material names and uses are localized, keep composite IDs and return exactly to accepted Chinese',()=>{
  const h=load();for(const language of ['ja','en','zh']){h.setLanguagePreference(language);assert.equal(h.state.catalog.items.length,230);for(const row of h.state.catalog.items){const key=`${row.type}:${row.id}:${row.eventId??''}`,original=h.materialDisplay.items[key],extra=h.catalogLocales.items[key];assert.equal(row.name,language==='zh'?original.nameZh:language==='ja'?original.nameJa:extra.nameEn);assert.equal(row.usage,language==='zh'?original.usage:language==='ja'?extra.usageJa:extra.usageEn);assert.ok(row.usage);if(language==='en')assert.doesNotMatch(row.name,/\p{Script=Han}/u);}}
});
test('all 76 unit variants and 306 equipment definitions have English names and exact original Japanese',()=>{
  const h=load();for(const category of ['cards','equipment']){const rows=Object.entries(h.localDisplayNames[category]).map(([id,row])=>({id:Number(id),name:row.nameJa}));for(const language of ['en','ja','zh']){h.setLanguagePreference(language);for(const row of h.localizeCatalog({[category]:rows})[category]){const original=h.localDisplayNames[category][row.id];assert.equal(row.name,language==='en'?h.catalogLocales[category][row.id]:language==='ja'?original.nameJa:original.nameZh);if(language==='en'){assert.doesNotMatch(row.name,/\p{Script=Han}/u);if(category==='cards')assert.match(row.name,/\[.+\]/);}}}}
});
test('resource rows, category controls and dynamic aria labels use the selected language with quantity drafts retained',()=>{
  const h=load();h.state.page='resources';h.state.drafts.set('item:6:10001:','42');h.setLanguagePreference('en');const page=h.document.querySelector('#page-content');assert.match(page.textContent,/Wallet and stamina/);assert.match(page.textContent,/Summon items/);assert.doesNotMatch(page.textContent,/目标数量|道具与材料|免费石|抽卡道具|只提交/);assert.equal(page.querySelector('[data-draft="item:6:10001:"]').value,'42');assert.equal(page.querySelector('[data-draft="item:6:10001:"]').getAttribute('aria-label'),'Item 10001 target quantity');
});
test('unknown server messages cannot leak Chinese while localized numeric validation retains specific values',async()=>{
  const h=load({preference:'en'});h.window.fetch=async()=>({ok:false,status:422,json:async()=>({error:{code:'future_unknown_code',message:'不可泄漏的后端中文'}})});await assert.rejects(h.api.status(),e=>{assert.equal(e.message,'Request failed (HTTP 422).');return true;});let error;try{h.numeric('999',1,8,h.t('等级'));}catch(e){error=e;}h.handleError(error);assert.equal(h.document.querySelector('#notice').textContent,'Level must be an integer from 1 to 8.');
});
test('visible localized notice can change language while preserving its numeric placeholders',()=>{const h=load();let e;try{h.numeric(999,1,8,h.t('等级'));}catch(error){e=error;}h.handleError(e);h.setLanguagePreference('en');assert.equal(h.document.querySelector('#notice').textContent,'Level must be an integer from 1 to 8.');});
test('no global mutation observer or translation of server protocol keys is introduced',()=>{const h=load();assert.doesNotMatch(fs.readFileSync(path.join(root,'app.js'),'utf8'),/MutationObserver/);const result=h.buildPreviewSections([{label:'资源',before:{gf:1},after:{gf:2}}],h.state.catalog);assert.equal(result[0].entries[0].name,'GF');});
(async()=>{let failures=0;for(const [name,run]of tests){try{await run();console.log('PASS '+name);}catch(error){failures++;console.error('FAIL '+name+'\n'+error.stack);}}console.log(`${tests.length-failures}/${tests.length} locale checks passed`);process.exitCode=failures?1:0;})();
