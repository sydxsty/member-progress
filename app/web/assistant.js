(() => {
  'use strict';
  const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const symbols={spark:'<path d="m12 3 2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4ZM20 2v4M18 4h4"/>',send:'<path d="m21 3-7 18-4-7-7-4Z M21 3 10 14"/>',check:'<path d="m5 12 4 4L19 6"/>',archive:'<rect x="3" y="3" width="18" height="5" rx="1.5"/><path d="M5 8v11a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8M9 12h6"/>',close:'<path d="m6 6 12 12M6 18 18 6"/>',calendar:'<rect x="3" y="5" width="18" height="16" rx="2"/><path d="M7 3v4M17 3v4M3 11h18"/>',bell:'<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"/>',clip:'<path d="m8 13 7-7a3 3 0 0 1 4 4L9 20a5 5 0 0 1-7-7L12 3"/><path d="m5 15 9-9"/>',file:'<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z M14 2v6h6 M8 13h8 M8 17h5"/>',refresh:'<path d="M20 7v5h-5M4 17v-5h5M6.1 6.1a8 8 0 0 1 13.3 3M4.6 14.9a8 8 0 0 0 13.3 3"/>'};
  const icon = name => `<svg class="assistant-icon" viewBox="0 0 24 24" aria-hidden="true">${symbols[name]||symbols.spark}</svg>`;
  let state={messages:[],busy:false,error:'',canUndoClear:false,runtime:{available:false,version:'',latestVersion:'',autoUpdate:false,updateStatus:''}};
  let config={},host=null,settingsHost=null,loaded=false,loading=false,draft='',includeReminders=true,includeRecent=true,refs=[],sending=false,clearing=false,operation=0,scrollTop=0,pinned=true,clearedComposer=null,files=[],uploading=false,composing=false,renderPending=false,dropDepth=0;
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
  function onProgress(progress) {state.activity={...(state.activity||{}),...(progress||{})};patchProgress();}
  function mount(container) {capture();host=container;bind(container);render(true);ensureLoaded();}
  function render(force=false) {
    if(composing){renderPending=true;return;}renderPending=false;
    if(!host?.isConnected||(!force&&!host.querySelector('.assistant-page')))return;
    const area=host.querySelector('[data-assistant-draft]'),focused=document.activeElement===area,selection=area?.selectionStart;
    const busy=state.busy||sending||clearing;
    const messages=state.messages||[],retryable=retryableMessage();
    host.innerHTML=`<section class="assistant-page">
      <header class="assistant-heading"><div><h1>GPT 聊天</h1></div><div class="assistant-heading-actions">
        ${state.canUndoClear?`<button type="button" class="assistant-undo-clear" data-assistant-action="undo-clear" ${busy?'disabled':''}>撤销清空</button>`:''}
        <button type="button" class="assistant-clear" data-assistant-action="clear" ${clearing||(!messages.length&&!state.busy&&!sending)?'disabled':''}>${clearing?'正在整理…':'清空会话'}</button>
        <div class="assistant-heading-mark">${icon('spark')}</div>
      </div></header>
      <div class="assistant-scroll"><div class="assistant-thread">${messages.length?messages.map(message).join(''):`<div class="assistant-welcome"><div class="assistant-welcome-mark">${icon('spark')}</div><h2>想先梳理哪件事？</h2><p>直接问进展、安排或微信里的事情。<br>GPT 会按需查找资料，也可以创建任务或提醒。</p><div class="assistant-quick-questions">${['今天应该优先跟进谁？','明天9点提醒我核对进展','帮我梳理接下来的安排'].map(q=>`<button type="button" data-assistant-action="quick" data-assistant-question="${escape(q)}" ${busy?'disabled':''}>${escape(q)}</button>`).join('')}</div></div>`}
      <div data-assistant-progress-root>${activityView()}</div>${!clearing&&!busy&&state.error?`<div class="assistant-error"><span>${escape(state.error)}</span>${retryable?`<button type="button" data-assistant-action="retry" data-assistant-id="${escape(retryable.id)}">重试</button>`:''}</div>`:''}</div></div>
      <div class="assistant-bottom"><div class="assistant-composer-wrap"><div class="assistant-contexts"><span class="assistant-context-label">本次重点</span><button type="button" class="assistant-context-chip${includeReminders?' assistant-selected':''}" data-assistant-action="toggle-reminders" aria-pressed="${includeReminders}" ${clearing?'disabled':''}>${icon(includeReminders?'check':'bell')}全部提醒</button><button type="button" class="assistant-context-chip${includeRecent?' assistant-selected':''}" data-assistant-action="toggle-recent" aria-pressed="${includeRecent}" ${clearing?'disabled':''}>${icon(includeRecent?'check':'archive')}最近归档</button>${refs.map(r=>`<div class="assistant-reference-chip"><button type="button" data-assistant-action="open-ref" data-assistant-scope="${escape(r.scopeType)}" data-assistant-id="${escape(r.scopeId)}" title="查看重点资料；GPT 仍可按需查找全库" ${clearing?'disabled':''}>${icon('archive')}<span>${escape(r.title||'已引用归档')}</span></button><button type="button" data-assistant-action="remove-ref" data-assistant-key="${escape(scopeKey(r))}" aria-label="移除${escape(r.title)}引用" ${clearing?'disabled':''}>${icon('close')}</button></div>`).join('')}</div>
      <div class="assistant-compose-box"><div class="assistant-draft-files">${draftFiles()}</div><input type="file" class="assistant-file-input" data-assistant-files multiple hidden><textarea data-assistant-draft ${sending||clearing?'disabled':''} placeholder="问问进展，或安排任务、提醒…" rows="2" aria-label="GPT 聊天问题">${escape(draft)}</textarea><div class="assistant-compose-footer"><div class="assistant-compose-tools"><button type="button" class="assistant-attach" data-assistant-action="pick-files" title="添加文件，也可拖入或粘贴图片" ${sending||clearing||uploading?'disabled':''}>${icon('clip')}${uploading?'读取中…':'附件'}</button><span class="assistant-compose-hint">Enter 发送 · Shift + Enter 换行</span></div>${state.busy&&!clearing?'<button type="button" class="assistant-stop" data-assistant-action="cancel">停止回答</button>':`<button type="button" class="assistant-send" data-assistant-action="send" ${sending||clearing||uploading||(!draft.trim()&&!files.length)?'disabled':''}>${icon('send')}${sending?'正在发送':'发送'}</button>`}</div></div><p class="assistant-disclaimer">${!config.isConfigured?.()?'请先在设置中连接 GPT。':config.currentUser?.()?`“我”指 ${escape(config.currentUser().name)} · 可安排任务和提醒`:'先在设置中选择“当前用户”，再用“我”安排任务。'}</p></div></div></section>`;
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
  function taskCard(task) {
    const dates=task.start&&task.due&&task.start!==task.due?`${task.start} — ${task.due}`:task.start||task.due||'';
    return `<div class="assistant-reminder-card assistant-task-card"><div class="assistant-reminder-icon">${icon('calendar')}</div><div class="assistant-reminder-body"><div class="assistant-reminder-status">已创建任务</div><strong>${escape(task.title)}</strong><div class="assistant-reminder-detail">${escape(dates)}${dates&&task.scopeLabel?'<span>·</span>':''}${escape(task.scopeLabel||'')}</div></div>${task.id?`<button type="button" data-assistant-action="open-task" data-assistant-id="${escape(task.id)}">查看</button>`:''}</div>`;
  }
  function message(m) {
    const user=m.role==='user',results=!user&&Array.isArray(m.reminderResults)?m.reminderResults:[],tasks=!user&&Array.isArray(m.taskResults)?m.taskResults:[];
    return `<article class="assistant-message${user?' assistant-user':''}"><div class="assistant-message-avatar">${user?'我':icon('spark')}</div><div class="assistant-message-body"><div class="assistant-message-meta"><span>${user?'我':'GPT'}</span><time>${date(m.createdAt)}</time></div><div class="assistant-message-text">${escape(m.text)}${attachmentCards(m.attachments)}${(m.references||[]).length?`<div class="assistant-message-references">${m.references.map(r=>`<button type="button" data-assistant-action="open-ref" data-assistant-scope="${escape(r.scopeType)}" data-assistant-id="${escape(r.scopeId)}">${icon('archive')}${escape(r.title||'引用归档')}</button>`).join('')}</div>`:''}</div>${results.length||tasks.length?`<div class="assistant-reminder-results">${tasks.map(taskCard).join('')}${results.map(reminderCard).join('')}</div>`:''}${(m.archiveResults||[]).map(r=>`<button type="button" class="assistant-file-card" data-assistant-action="open-ref" data-assistant-scope="${escape(r.scopeType)}" data-assistant-id="${escape(r.scopeId)}">${icon('archive')}<span><strong>${r.replayed?'已在归档中':'已归档'}</strong><small>查看归档</small></span></button>`).join('')}</div></article>`;
  }
  const fileSize=value=>Number(value)>=1048576?`${(Number(value)/1048576).toFixed(1)} MB`:`${Math.max(0,Math.ceil(Number(value||0)/1024))} KB`;
  function draftFiles(){return files.map((f,i)=>`<span class="assistant-draft-file">${icon('file')}<span title="${escape(f.name)}">${escape(f.name)}</span><small>${fileSize(f.size)}</small><button type="button" data-assistant-action="remove-file" data-assistant-index="${i}" aria-label="移除 ${escape(f.name)}" ${sending||clearing||uploading?'disabled':''}>${icon('close')}</button></span>`).join('');}
  function attachmentCards(values){
    return (Array.isArray(values)?values:[]).map(file=>{
      let preview='';try{const url=new URL(file.previewUrl);if(url.origin==='https://student-progress-media.local'&&url.pathname.startsWith('/assistant-image/'))preview=url.href;}catch{}
      return `<button type="button" class="assistant-file-card${preview?' assistant-image-card':''}" data-assistant-action="open-file" data-assistant-id="${escape(file.id)}" title="打开原文件">${preview?`<img loading="lazy" src="${escape(preview)}" alt="${escape(file.name)}">`:icon('file')}<span><strong>${escape(file.name||'附件')}</strong><small>${fileSize(file.size)}</small></span></button>`;
    }).join('');
  }
  async function addFiles(fileList){
    const incoming=Array.from(fileList||[]);if(!incoming.length||uploading||sending||clearing)return;
    if(files.length+incoming.length>20){notify('每条消息最多添加 20 个附件。');return;}
    if(incoming.some(f=>f.size>20*1024*1024)){notify('单个附件不能超过 20 MB。');return;}
    if([...files,...incoming].reduce((n,f)=>n+Number(f.size||0),0)>40*1024*1024){notify('每条消息的附件合计最多 40 MB。');return;}
    capture();const requestOperation=operation;uploading=true;render();
    try{
      const added=await Promise.all(incoming.map(f=>new Promise((resolve,reject)=>{const reader=new FileReader();reader.onerror=()=>reject(new Error(`无法读取 ${f.name}`));reader.onload=()=>resolve({name:f.name||'粘贴图片.png',mime:f.type||'application/octet-stream',size:f.size,dataBase64:String(reader.result).split(',')[1]||''});reader.readAsDataURL(f);})));
      if(operation===requestOperation)files.push(...added);
    }catch(error){if(operation===requestOperation)notify(error.message);}
    finally{uploading=false;render();}
  }
  function activityView(){
    if(clearing)return '';
    const activity=state.activity||{},items=Array.isArray(activity.items)?activity.items:[],active=state.busy||sending||['starting','running'].includes(activity.phase);
    const phase={starting:'准备中',running:'正在处理',completed:'已完成',cancelled:'已停止',error:'未完成'}[activity.phase]||(active?'正在处理':'');
    const usage=activity.usage||{},finite=value=>typeof value==='number'&&Number.isFinite(value);
    const hasContext=finite(usage.contextTokens)&&finite(usage.contextWindow)&&usage.contextWindow>0;
    const percent=hasContext?Math.min(100,Math.max(0,finite(usage.percent)?usage.percent:100*usage.contextTokens/usage.contextWindow)):null;
    const meter=hasContext?`<span class="assistant-context-usage" title="当前上下文 ${usage.contextTokens.toLocaleString()} / ${usage.contextWindow.toLocaleString()} tokens">上下文 ${percent.toFixed(0)}%</span>`:finite(usage.contextTokens)?`<span class="assistant-context-usage" title="内核已提供当前占用，尚未提供上下文总容量">上下文 ${usage.contextTokens.toLocaleString()} tokens</span>`:active?'<span class="assistant-context-usage" title="内核尚未提供当前上下文占用">上下文 —</span>':'';
    if(!active&&!items.length)return '';
    return `<div class="assistant-live-status"><span class="assistant-working">${active?'<span class="assistant-spinner"></span>':''}${escape(phase)}</span>${meter}${active?'<button type="button" class="assistant-cancel-inline" data-assistant-action="cancel">停止</button>':''}</div>${items.length?`<details class="assistant-activity"><summary>处理记录 <span>${items.length}</span></summary><div class="assistant-activity-items">${items.map(item=>`<div class="assistant-activity-item" data-activity-id="${escape(item.id)}"><div><span class="assistant-activity-kind">${escape(({reasoning:'推理摘要',summary:'推理摘要',tool:'工具',search:'检索',plan:'计划',command:'命令'}[item.kind])||'进展')}</span><strong>${escape(item.title||'正在处理')}</strong><span class="assistant-activity-status">${escape(({running:'进行中',inProgress:'进行中',completed:'完成',failed:'失败',error:'失败',cancelled:'停止'}[item.status])||'')}</span></div>${item.text?`<p>${escape(item.text)}</p>`:''}</div>`).join('')}</div></details>`:''}${active&&activity.text?`<div class="assistant-stream-text" aria-live="polite">${escape(activity.text)}</div>`:''}`;
  }
  function patchProgress(){
    const target=host?.querySelector('[data-assistant-progress-root]');if(!target)return;
    const scroll=host.querySelector('.assistant-scroll'),stick=scroll&&scroll.scrollHeight-scroll.scrollTop-scroll.clientHeight<60;
    const opened=target.querySelector('details')?.open;target.innerHTML=activityView();
    if(opened&&target.querySelector('details'))target.querySelector('details').open=true;
    if(stick&&scroll)scroll.scrollTop=scroll.scrollHeight;
  }
  function autoHeight(input){if(input){input.style.height='auto';input.style.height=`${Math.min(input.scrollHeight,160)}px`;}}
  function retryableMessage(){
    if(!state.error)return null;
    const messages=state.messages||[],index=messages.findLastIndex(m=>m.role==='user');
    if(index<0)return null;const user=messages[index];
    return messages.slice(index+1).every(m=>m.role==='assistant'&&(!m.replyToMessageId||m.replyToMessageId===user.id))?user:null;
  }
  async function send(text=draft,retryMessageId=null) {
    text=String(text||'').trim();if((!text&&!files.length&&!retryMessageId)||sending||state.busy||clearing||uploading)return;
    const payload=retryMessageId?{retryMessageId}:{text,includeReminders,includeRecent,references:refs.map(r=>({...r})),files:files.map(({name,mime,dataBase64})=>({name,mime,dataBase64}))};
    const before=draft,requestOperation=operation;sending=true;pinned=true;render();
    try {await request('assistant.send',payload,requestOperation);if(operation===requestOperation&&!retryMessageId){draft='';files=[];}}
    catch(e){if(operation===requestOperation){draft=before||(!retryMessageId?text:'');notify(e.message);}}
    finally{if(operation===requestOperation){sending=false;render();host?.querySelector('[data-assistant-draft]')?.focus();}}
  }
  async function clearConversation(undo=false) {
    if(clearing||(undo&&(state.busy||sending)))return;
    capture();const before={draft,references:refs.map(r=>({...r})),files:files.slice()};
    clearing=true;const requestOperation=++operation;render();
    try {
      await request(undo?'assistant.undo-clear':'assistant.clear',{},requestOperation);
      if(undo){draft=clearedComposer?.draft||'';refs=clearedComposer?.references||[];files=clearedComposer?.files||[];clearedComposer=null;}
      else{clearedComposer=before;draft='';refs=[];files=[];}
      state.error='';scrollTop=0;pinned=true;
    }catch(e){notify(e.message);}
    finally{clearing=false;sending=false;render();host?.querySelector('[data-assistant-draft]')?.focus();}
  }
  function bind(container) {
    if(bound.has(container))return;bound.add(container);
    container.addEventListener('input',e=>{
      if(e.target.hasAttribute('data-assistant-draft')){draft=e.target.value;autoHeight(e.target);const button=container.querySelector('[data-assistant-action=send]');if(button)button.disabled=sending||clearing||state.busy||uploading||(!draft.trim()&&!files.length);}
    });
    container.addEventListener('compositionstart',e=>{if(e.target.hasAttribute('data-assistant-draft'))composing=true;});
    container.addEventListener('compositionend',e=>{if(e.target.hasAttribute('data-assistant-draft')){draft=e.target.value;composing=false;requestAnimationFrame(()=>{if(renderPending)render();});}});
    container.addEventListener('paste',e=>{if(!e.target.closest('.assistant-compose-box'))return;const picked=Array.from(e.clipboardData?.items||[]).filter(i=>i.kind==='file').map(i=>i.getAsFile()).filter(Boolean);if(picked.length){e.preventDefault();addFiles(picked);}});
    container.addEventListener('dragenter',e=>{if(!e.target.closest('.assistant-page')||!Array.from(e.dataTransfer?.types||[]).includes('Files'))return;e.preventDefault();dropDepth++;host?.querySelector('.assistant-compose-box')?.classList.add('assistant-drop-active');});
    container.addEventListener('dragover',e=>{if(e.target.closest('.assistant-page')&&Array.from(e.dataTransfer?.types||[]).includes('Files'))e.preventDefault();});
    container.addEventListener('dragleave',()=>{if(dropDepth>0)dropDepth--;if(!dropDepth)host?.querySelector('.assistant-compose-box')?.classList.remove('assistant-drop-active');});
    container.addEventListener('drop',e=>{if(!e.target.closest('.assistant-page'))return;e.preventDefault();e.stopPropagation();dropDepth=0;host?.querySelector('.assistant-compose-box')?.classList.remove('assistant-drop-active');addFiles(e.dataTransfer?.files);});
    container.addEventListener('keydown',e=>{if(e.target.hasAttribute('data-assistant-draft')&&e.key==='Enter'&&!e.shiftKey&&!e.isComposing&&e.keyCode!==229){e.preventDefault();send();}});
    container.addEventListener('change',e=>{if(e.target.hasAttribute('data-assistant-files')){addFiles(e.target.files);return;}if(e.target.hasAttribute('data-assistant-auto-update'))request('assistant.settings',{autoUpdate:e.target.checked}).catch(e=>notify(e.message));});
    container.addEventListener('click',e=>{
      const el=e.target.closest('[data-assistant-action]');if(!el||el.disabled)return;
      const a=el.dataset.assistantAction;
      if(a==='send'){send();return;}
      if(a==='pick-files'){host?.querySelector('[data-assistant-files]')?.click();return;}
      if(a==='remove-file'){files.splice(Number(el.dataset.assistantIndex),1);render();return;}
      if(a==='open-file'){request('assistant.file.open',{attachmentId:el.dataset.assistantId}).catch(e=>notify(e.message));return;}
      if(a==='quick'){if(clearing||sending||state.busy)return;capture();draft=el.dataset.assistantQuestion||'';render();host?.querySelector('[data-assistant-draft]')?.focus();return;}
      if(a==='clear'){clearConversation();return;}
      if(a==='undo-clear'){clearConversation(true);return;}
      if(a==='open-task'){config.openTask?.(el.dataset.assistantId);return;}
      if(a==='open-reminder'){config.openReminder?.(el.dataset.assistantId);return;}
      if(a==='toggle-reminders'){capture();includeReminders=!includeReminders;render();return;}
      if(a==='toggle-recent'){capture();includeRecent=!includeRecent;render();return;}
      if(a==='remove-ref'){capture();refs=refs.filter(r=>scopeKey(r)!==el.dataset.assistantKey);render();return;}
      if(a==='open-ref'){config.openArchive?.(el.dataset.assistantScope,el.dataset.assistantId);return;}
      if(a==='retry'){const last=retryableMessage();if(last&&last.id===el.dataset.assistantId)send(last.text,last.id);return;}
      const action={cancel:'assistant.cancel',check:'assistant.check-update',update:'assistant.update'}[a];
      if(action)request(action).catch(e=>notify(e.message));
    });
  }
  function reference(value) {
    if(clearing||!value||!['student','project','wechat'].includes(value.scopeType)||!value.scopeId)return;
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
        if(!String(p.text||'').trim()&&!(p.files||[]).length)throw new Error('先写下问题，或添加附件。');
        preview.messages.push({id:crypto.randomUUID(),role:'user',text:String(p.text).trim(),createdAt:new Date().toISOString(),references:structuredClone(p.references||[]),attachments:(p.files||[]).map(f=>({id:crypto.randomUUID(),name:f.name,mime:f.mime,size:Math.floor((f.dataBase64||'').length*3/4)}))});
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
  window.AssistantChat={configure(value){config={...config,...value};},mount,onState,onProgress,reference,mountSettings,previewHandle};
})();
