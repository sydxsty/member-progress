(() => {
  'use strict';
  const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const symbols={spark:'<path d="m12 3 2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4ZM20 2v4M18 4h4"/>',send:'<path d="m21 3-7 18-4-7-7-4Z M21 3 10 14"/>',check:'<path d="m5 12 4 4L19 6"/>',archive:'<rect x="3" y="3" width="18" height="5" rx="1.5"/><path d="M5 8v11a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8M9 12h6"/>',close:'<path d="m6 6 12 12M6 18 18 6"/>',bell:'<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"/>',refresh:'<path d="M20 7v5h-5M4 17v-5h5M6.1 6.1a8 8 0 0 1 13.3 3M4.6 14.9a8 8 0 0 0 13.3 3"/>'};
  const icon = name => `<svg class="assistant-icon" viewBox="0 0 24 24" aria-hidden="true">${symbols[name]||symbols.spark}</svg>`;
  let state={messages:[],busy:false,error:'',canUndoClear:false,runtime:{available:false,version:'',latestVersion:'',autoUpdate:false,updateStatus:''}};
  let config={},host=null,settingsHost=null,loaded=false,loading=false,draft='',includeReminders=true,includeRecent=true,refs=[],sending=false,clearing=false,operation=0,scrollTop=0,pinned=true,clearedComposer=null;
  const bound=new WeakSet();
  const scopeKey=r=>`${r.scopeType}:${r.scopeId}`;
  const date=s=>{const d=new Date(s);return Number.isFinite(d.getTime())?d.toLocaleString('zh-CN',{month:'numeric',day:'numeric',hour:'2-digit',minute:'2-digit'}):'';};
  const runningUpdate=()=>/正在|下载|准备|校验/.test(state.runtime?.updateStatus||'');
  function notify(text) {const n=document.createElement('div');n.className='assistant-toast';n.textContent=text;document.body.appendChild(n);setTimeout(()=>n.remove(),5000);}
  async function request(action,payload={},requestOperation=operation) {
    if(typeof config.rpc!=='function')throw new Error('GPT 聊天尚未连接，请重新打开页面。');
    const result=await config.rpc(action,payload);
    if(requestOperation===operation&&result&&Array.isArray(result.messages)&&result.runtime)onState(result);
    return result;
  }
  function ensureLoaded() {
    if(loaded||loading||!config.rpc)return;
    loading=true;
    request('assistant.get').then(()=>{loaded=true;}).catch(e=>{state.error=e.message;render();}).finally(()=>{loading=false;});
  }
  function capture() {
    if(clearing||!host?.isConnected)return;
    const area=host.querySelector('[data-assistant-draft]');if(area)draft=area.value;
    const scroll=host.querySelector('.assistant-scroll');if(scroll){scrollTop=scroll.scrollTop;pinned=scroll.scrollHeight-scroll.scrollTop-scroll.clientHeight<60;}
  }
  function onState(next) {capture();state={...state,...next,runtime:{...state.runtime,...next.runtime}};loaded=true;render();renderSettings();}
  function mount(container) {capture();host=container;bind(container);render(true);ensureLoaded();}
  function render(force=false) {
    if(!host?.isConnected||(!force&&!host.querySelector('.assistant-page')))return;
    const area=host.querySelector('[data-assistant-draft]'),focused=document.activeElement===area,selection=area?.selectionStart;
    const busy=state.busy||sending||clearing;
    const messages=state.messages||[];
    host.innerHTML=`<section class="assistant-page">
      <header class="assistant-heading"><div><h1>GPT 聊天</h1></div><div class="assistant-heading-actions">
        ${state.canUndoClear?`<button type="button" class="assistant-undo-clear" data-assistant-action="undo-clear" ${busy?'disabled':''}>撤销清空</button>`:''}
        <button type="button" class="assistant-clear" data-assistant-action="clear" ${clearing||(!messages.length&&!state.busy&&!sending)?'disabled':''}>${clearing?'正在整理…':'清空会话'}</button>
        <div class="assistant-heading-mark">${icon('spark')}</div>
      </div></header>
      <div class="assistant-scroll"><div class="assistant-thread">${messages.length?messages.map(message).join(''):`<div class="assistant-welcome"><div class="assistant-welcome-mark">${icon('spark')}</div><h2>想先梳理哪件事？</h2><p>带上提醒和最近归档，直接问。<br>也可以说明时间、对象和要提醒的事，创建提醒。</p><div class="assistant-quick-questions">${['今天应该优先跟进谁？','明天9点提醒我核对进展','帮我梳理接下来的安排'].map(q=>`<button type="button" data-assistant-action="quick" data-assistant-question="${escape(q)}" ${busy?'disabled':''}>${escape(q)}</button>`).join('')}</div></div>`}
      ${clearing?'':busy?`<div class="assistant-working"><span class="assistant-spinner"></span>正在整理资料并回答…<button type="button" class="assistant-cancel-inline" data-assistant-action="cancel">停止</button></div>`:state.error?`<div class="assistant-error"><span>${escape(state.error)}</span>${messages.some(m=>m.role==='user')?'<button type="button" data-assistant-action="retry">重试</button>':''}</div>`:''}</div></div>
      <div class="assistant-bottom"><div class="assistant-composer-wrap"><div class="assistant-contexts"><span class="assistant-context-label">本次参考</span><button type="button" class="assistant-context-chip${includeReminders?' assistant-selected':''}" data-assistant-action="toggle-reminders" aria-pressed="${includeReminders}" ${clearing?'disabled':''}>${icon(includeReminders?'check':'bell')}全部提醒</button><button type="button" class="assistant-context-chip${includeRecent?' assistant-selected':''}" data-assistant-action="toggle-recent" aria-pressed="${includeRecent}" ${clearing?'disabled':''}>${icon(includeRecent?'check':'archive')}最近归档</button>${refs.map(r=>`<div class="assistant-reference-chip"><button type="button" data-assistant-action="open-ref" data-assistant-scope="${escape(r.scopeType)}" data-assistant-id="${escape(r.scopeId)}" title="查看整份归档" ${clearing?'disabled':''}>${icon('archive')}<span>${escape(r.title||'已引用归档')}</span></button><button type="button" data-assistant-action="remove-ref" data-assistant-key="${escape(scopeKey(r))}" aria-label="移除${escape(r.title)}引用" ${clearing?'disabled':''}>${icon('close')}</button></div>`).join('')}</div>
      <div class="assistant-compose-box"><textarea data-assistant-draft ${sending||clearing?'disabled':''} placeholder="问问进展，或说“明天9点提醒我核对进展”…" rows="2" aria-label="GPT 聊天问题">${escape(draft)}</textarea><div class="assistant-compose-footer"><span class="assistant-compose-hint">Enter 发送 · Shift + Enter 换行</span>${state.busy&&!clearing?'<button type="button" class="assistant-stop" data-assistant-action="cancel">停止回答</button>':`<button type="button" class="assistant-send" data-assistant-action="send" ${sending||clearing||!draft.trim()?'disabled':''}>${icon('send')}${sending?'正在发送':'发送'}</button>`}</div></div><p class="assistant-disclaimer">可直接创建全局、成员或项目提醒</p></div></div></section>`;
    const scroll=host.querySelector('.assistant-scroll');scroll.scrollTop=pinned?scroll.scrollHeight:scrollTop;
    scroll.addEventListener('scroll',()=>{scrollTop=scroll.scrollTop;pinned=scroll.scrollHeight-scroll.scrollTop-scroll.clientHeight<60;},{passive:true});
    const input=host.querySelector('[data-assistant-draft]');if(focused){input.focus({preventScroll:true});if(selection!=null)input.setSelectionRange(selection,selection);}autoHeight(input);
  }
  function reminderTime(reminder) {
    const d=new Date(reminder.when);if(!Number.isFinite(d.getTime()))return '';
    const clock=d.toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit',hour12:false});
    if(reminder.repeat==='daily')return `每天 ${clock}`;
    if(reminder.repeat==='weekly')return `每周${['日','一','二','三','四','五','六'][d.getDay()]} ${clock}`;
    if(reminder.repeat==='monthly')return `每月${Number(reminder.monthDay)||d.getDate()}号 ${clock}`;
    return d.toLocaleDateString('zh-CN',{month:'numeric',day:'numeric',...(d.getFullYear()!==new Date().getFullYear()?{year:'numeric'}:{})})+' '+clock;
  }
  function reminderCard(reminder) {
    const scope=reminder.scopeLabel||(reminder.scopeType==='global'?'全局':reminder.scopeType==='project'?'项目':'成员');
    return `<div class="assistant-reminder-card"><div class="assistant-reminder-icon">${icon('bell')}</div><div class="assistant-reminder-body"><div class="assistant-reminder-status">已创建提醒</div><strong>${escape(reminder.title)}</strong><div class="assistant-reminder-detail">${escape(reminderTime(reminder))}<span>·</span>${escape(scope)}</div></div>${reminder.id?`<button type="button" data-assistant-action="open-reminder" data-assistant-id="${escape(reminder.id)}">查看</button>`:''}</div>`;
  }
  function message(m) {
    const user=m.role==='user',results=!user&&Array.isArray(m.reminderResults)?m.reminderResults:[];
    return `<article class="assistant-message${user?' assistant-user':''}"><div class="assistant-message-avatar">${user?'我':icon('spark')}</div><div class="assistant-message-body"><div class="assistant-message-meta"><span>${user?'我':'GPT'}</span><time>${date(m.createdAt)}</time></div><div class="assistant-message-text">${escape(m.text)}${(m.references||[]).length?`<div class="assistant-message-references">${m.references.map(r=>`<button type="button" data-assistant-action="open-ref" data-assistant-scope="${escape(r.scopeType)}" data-assistant-id="${escape(r.scopeId)}">${icon('archive')}${escape(r.title||'引用归档')}</button>`).join('')}</div>`:''}</div>${results.length?`<div class="assistant-reminder-results">${results.map(reminderCard).join('')}</div>`:''}</div></article>`;
  }
  function autoHeight(input){if(input){input.style.height='auto';input.style.height=`${Math.min(input.scrollHeight,160)}px`;}}
  async function send(text=draft,retryMessageId=null) {
    text=String(text||'').trim();if(!text||sending||state.busy||clearing)return;
    const payload=retryMessageId?{retryMessageId}:{text,includeReminders,includeRecent,references:refs.map(r=>({...r}))};
    const before=draft,requestOperation=operation;sending=true;pinned=true;render();
    try {await request('assistant.send',payload,requestOperation);if(operation===requestOperation&&!retryMessageId)draft='';}
    catch(e){if(operation===requestOperation){draft=before||(!retryMessageId?text:'');notify(e.message);}}
    finally{if(operation===requestOperation){sending=false;render();host?.querySelector('[data-assistant-draft]')?.focus();}}
  }
  async function clearConversation(undo=false) {
    if(clearing||(undo&&(state.busy||sending)))return;
    capture();const before={draft,references:refs.map(r=>({...r}))};
    clearing=true;const requestOperation=++operation;render();
    try {
      await request(undo?'assistant.undo-clear':'assistant.clear',{},requestOperation);
      if(undo){draft=clearedComposer?.draft||'';refs=clearedComposer?.references||[];clearedComposer=null;}
      else{clearedComposer=before;draft='';refs=[];}
      state.error='';scrollTop=0;pinned=true;
    }catch(e){notify(e.message);}
    finally{clearing=false;sending=false;render();host?.querySelector('[data-assistant-draft]')?.focus();}
  }
  function bind(container) {
    if(bound.has(container))return;bound.add(container);
    container.addEventListener('input',e=>{
      if(e.target.hasAttribute('data-assistant-draft')){draft=e.target.value;autoHeight(e.target);const button=container.querySelector('[data-assistant-action=send]');if(button)button.disabled=sending||clearing||state.busy||!draft.trim();}
    });
    container.addEventListener('keydown',e=>{if(e.target.hasAttribute('data-assistant-draft')&&e.key==='Enter'&&!e.shiftKey&&!e.isComposing&&e.keyCode!==229){e.preventDefault();send();}});
    container.addEventListener('change',e=>{if(e.target.hasAttribute('data-assistant-auto-update'))request('assistant.settings',{autoUpdate:e.target.checked}).catch(e=>notify(e.message));});
    container.addEventListener('click',e=>{
      const el=e.target.closest('[data-assistant-action]');if(!el||el.disabled)return;
      const a=el.dataset.assistantAction;
      if(a==='send'){send();return;}
      if(a==='quick'){if(clearing||sending||state.busy)return;capture();draft=el.dataset.assistantQuestion||'';render();host?.querySelector('[data-assistant-draft]')?.focus();return;}
      if(a==='clear'){clearConversation();return;}
      if(a==='undo-clear'){clearConversation(true);return;}
      if(a==='open-reminder'){config.openReminder?.(el.dataset.assistantId);return;}
      if(a==='toggle-reminders'){capture();includeReminders=!includeReminders;render();return;}
      if(a==='toggle-recent'){capture();includeRecent=!includeRecent;render();return;}
      if(a==='remove-ref'){capture();refs=refs.filter(r=>scopeKey(r)!==el.dataset.assistantKey);render();return;}
      if(a==='open-ref'){config.openArchive?.(el.dataset.assistantScope,el.dataset.assistantId);return;}
      if(a==='retry'){const last=[...(state.messages||[])].reverse().find(m=>m.role==='user');if(last)send(last.text,last.id);return;}
      const action={cancel:'assistant.cancel',check:'assistant.check-update',update:'assistant.update'}[a];
      if(action)request(action).catch(e=>notify(e.message));
    });
  }
  function reference(value) {
    if(clearing||!value||!['student','project'].includes(value.scopeType)||!value.scopeId)return;
    capture();const r={scopeType:value.scopeType,scopeId:String(value.scopeId),title:String(value.title||'引用归档')};
    const index=refs.findIndex(x=>scopeKey(x)===scopeKey(r));if(index<0)refs.push(r);else refs[index]=r;
    render();
  }
  function mountSettings(container){if(!container)return;settingsHost=container;bind(container);renderSettings();ensureLoaded();}
  function renderSettings(){
    if(!settingsHost?.isConnected)return;
    const r=state.runtime||{},updating=runningUpdate();
    const newer=r.latestVersion&&r.latestVersion!==r.version;
    settingsHost.innerHTML=`<section class="assistant-settings"><h3 class="assistant-settings-heading">GPT 聊天内核 · Codex CLI</h3><div class="assistant-settings-version"><span>${r.available?`当前版本 ${escape(r.version)}`:'内置组件暂不可用'}</span>${newer?`<span>可更新至 ${escape(r.latestVersion)}</span>`:''}</div><div class="assistant-settings-actions"><button type="button" data-assistant-action="check" ${updating||state.busy?'disabled':''}>${icon('refresh')}检查更新</button><button type="button" data-assistant-action="update" ${updating||state.busy||(!newer&&r.available)?'disabled':''}>更新内核</button></div><label class="assistant-auto-update"><input type="checkbox" data-assistant-auto-update ${r.autoUpdate?'checked':''}>自动检查并更新内核</label><p class="assistant-settings-note">默认关闭。开启后每天空闲时检查一次，更新失败保留原版本。聊天使用上方配置的 Responses 接口。</p>${r.updateStatus?`<p class="assistant-runtime-status">${escape(r.updateStatus)}</p>`:''}</section>`;
  }
  let previewUndo=null;
  let preview={messages:[],busy:false,error:'',canUndoClear:false,runtime:{available:false,version:'',latestVersion:'',autoUpdate:false,updateStatus:'浏览器预览不运行 GPT 聊天组件。'}};
  async function previewHandle(action,p={}) {
    if(action==='assistant.get')return structuredClone(preview);
    if(action==='assistant.send'){
      if(p.retryMessageId){if(!preview.messages.some(m=>m.role==='user'&&m.id===p.retryMessageId))throw new Error('这条问题已不在当前会话中。');}
      else{
        if(!String(p.text||'').trim())throw new Error('先写下想问的问题。');
        preview.messages.push({id:crypto.randomUUID(),role:'user',text:String(p.text).trim(),createdAt:new Date().toISOString(),references:structuredClone(p.references||[])});
      }
      preview.error='浏览器预览仅保存本页临时对话，不调用 Codex。请在桌面应用中提问。';
    } else if(action==='assistant.clear'){
      if(preview.messages.length)previewUndo={messages:structuredClone(preview.messages),error:preview.error};
      preview.messages=[];preview.busy=false;preview.error='';preview.canUndoClear=!!previewUndo;
    } else if(action==='assistant.undo-clear'){
      if(!previewUndo)throw new Error('没有可恢复的会话。');
      preview.messages=previewUndo.messages;preview.error='';preview.busy=false;previewUndo=null;preview.canUndoClear=false;
    } else if(action==='assistant.cancel'){preview.busy=false;}
    else if(action==='assistant.settings'){preview.runtime.autoUpdate=!!p.autoUpdate;}
    else if(action==='assistant.check-update'||action==='assistant.update')throw new Error('请在桌面应用中检查或更新 GPT 聊天组件。');
    else throw new Error('不支持的 GPT 聊天操作。');
    return structuredClone(preview);
  }
  window.AssistantChat={configure(value){config={...config,...value};},mount,onState,reference,mountSettings,previewHandle};
})();
