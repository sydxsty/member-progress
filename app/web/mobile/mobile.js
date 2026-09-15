(() => {
  'use strict';
  const $ = (selector, host=document) => host.querySelector(selector);
  const esc = value => String(value ?? '').replace(/[&<>"']/g, c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const icons={members:'<circle cx="9" cy="7" r="3"/><path d="M3 21v-3a6 6 0 0 1 12 0v3M16 4a3 3 0 0 1 0 6m2 4a6 6 0 0 1 3 4v3"/>',bell:'<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"/>',spark:'<path d="m12 3 2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4ZM20 2v4M18 4h4"/>',plus:'<path d="M12 5v14M5 12h14"/>',back:'<path d="m15 5-7 7 7 7"/>',close:'<path d="m6 6 12 12M6 18 18 6"/>',more:'<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',check:'<rect x="3" y="3" width="18" height="18" rx="5"/><path d="m7 12 3 3 7-7"/>',square:'<rect x="3" y="3" width="18" height="18" rx="5"/>',archive:'<rect x="3" y="3" width="18" height="5" rx="2"/><path d="M5 8v11a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8M9 12h6"/>',edit:'<path d="m15 5 4 4M4 20l4-1L20 7a2.8 2.8 0 0 0-4-4L4 15Z"/>',refresh:'<path d="M20 7v5h-5M4 17v-5h5M6.1 6.1a8 8 0 0 1 13.3 3M4.6 14.9a8 8 0 0 0 13.3 3"/>',calendar:'<rect x="3" y="5" width="18" height="16" rx="3"/><path d="M7 3v4M17 3v4M3 11h18M8 15h2M14 15h2"/>',send:'<path d="m21 3-7 18-4-7-7-4Z M21 3 10 14"/>',link:'<path d="m10 14 4-4m-7 5-1 1a4 4 0 0 0 6 6l3-3a4 4 0 0 0 0-6m2-4 1-1a4 4 0 0 0-6-6L9 5a4 4 0 0 0 0 6"/>'};
  const icon = name=>`<svg viewBox="0 0 24 24" aria-hidden="true">${icons[name]||icons.more}</svg>`;
  const key = value=>{const d=value instanceof Date?value:new Date(value);return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;};
  const today=()=>key(new Date());
  const addDays=(value,n)=>{const d=new Date(`${value}T12:00:00`);d.setDate(d.getDate()+n);return key(d);};
  const shortDate=value=>value?`${Number(value.slice(5,7))}/${Number(value.slice(8,10))}`:'';
  const clock=value=>{const d=new Date(value);return Number.isFinite(+d)?d.toLocaleTimeString('zh-CN',{hour:'2-digit',minute:'2-digit',hour12:false}):'';};
  const stamp=value=>{const d=new Date(value);return Number.isFinite(+d)?`${shortDate(key(d))} ${clock(d)}`:'';};
  const localInput=value=>{const d=new Date(value);return `${key(d)}T${String(d.getHours()).padStart(2,'0')}:${String(d.getMinutes()).padStart(2,'0')}`;};
  const splitTags=value=>[...new Set(String(value||'').split(/[;；,，、\n]/).map(x=>x.trim()).filter(Boolean))];
  const uid=()=>globalThis.crypto?.randomUUID?.()||`${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}-${Math.random().toString(36).slice(2)}`;
  const native=!!window.MobileNative?.request;
  let db={students:[],projects:[],tasks:[],messages:[],alarms:[],preferences:{},recordVersions:{}}, assistant={messages:[],busy:false,error:'',canUndoClear:false,version:''};
  let loaded=false,connected=false,unauthorized=false,connectionError='',token='',loginError='',loginBusy=false;
  let tab='members',memberPage=0,search='',range={start:addDays(today(),-2),days:21},detail=null,sheet=null,working=false;
  let gptDraft='',refs=[],includeReminders=true,includeRecent=true,clearedComposer=null,sendGeneration=0,pollGeneration=0,changeVersion=0,refreshPromise=null,toastTimer,nativeReady=false;
  const archiveDrafts=new Map(),pendingNative=new Map(),requestIds=new Map();
  try{if(!native)token=sessionStorage.getItem('member-progress-token')||'';}catch{}
  const scope=(type,id)=>(type==='project'?db.projects:[...db.students,...db.archivedStudents||[]]).find(x=>x.id===id);
  const scopeTasks=(type,id)=>db.tasks.filter(t=>type==='project'?t.projectId===id:(t.studentIds||[]).includes(id));
  const scopeMessages=(type,id)=>db.messages.filter(m=>m.scopeType===type&&m.scopeId===id).sort((a,b)=>new Date(a.createdAt)-new Date(b.createdAt));
  const version=(type,id)=>db.recordVersions?.[`${type}:${id}`];
  const overdue=t=>!t.done&&!!t.due&&t.due<today();
  const blocked=t=>!t.done&&(t.tags||[]).some(x=>/受阻|阻塞|卡住/.test(x));
  const alarmLabel=a=>a.scopeType==='global'?'全局':scope(a.scopeType,a.scopeType==='project'?a.projectId:a.studentId)?.name||(a.scopeType==='project'?'项目':'成员');
  const repeatLabel=a=>({once:'单次',daily:'每天',weekly:'每周',monthly:`每月 ${a.monthDay||new Date(a.when).getDate()} 日`,interval:`每 ${a.intervalDays||1} 天`})[a.repeat]||'单次';
  const applicableAlarms=id=>{const projects=new Set(db.projects.filter(p=>(p.studentIds||[]).includes(id)).map(p=>p.id));return db.alarms.filter(a=>a.scopeType==='global'||a.scopeType==='student'&&a.studentId===id||a.scopeType==='project'&&projects.has(a.projectId)).sort((a,b)=>Number(!!b.enabled)-Number(!!a.enabled)||new Date(a.when)-new Date(b.when));};

  window.MobileNativeResult=(id,status,body)=>{
    const request=pendingNative.get(String(id));if(!request)return;
    clearTimeout(request.timeout);pendingNative.delete(String(id));request.resolve({status:Number(status),body});
  };
  async function api(method,path,body) {
    let status,data;
    if(native){
      const id=uid();
      const response=await new Promise((resolve,reject)=>{
        const timeout=setTimeout(()=>{pendingNative.delete(id);reject(new Error('电脑暂时没有回应，输入已保留。'));},45000);
        pendingNative.set(id,{resolve,reject,timeout});
        try{window.MobileNative.request(id,method,path,body===undefined?'':JSON.stringify(body));}catch(error){clearTimeout(timeout);pendingNative.delete(id);reject(error);}
      });
      status=response.status;try{data=JSON.parse(response.body||'{}');}catch{data={error:'电脑返回了无法读取的内容。'};}
    }else{
      const controller=new AbortController(),timeout=setTimeout(()=>controller.abort(),45000);
      try{const response=await fetch(path,{method,headers:{'Content-Type':'application/json',...(token?{Authorization:`Bearer ${token}`}:{})},body:body===undefined?undefined:JSON.stringify(body),cache:'no-store',signal:controller.signal});status=response.status;data=await response.json();}
      finally{clearTimeout(timeout);}
    }
    if(status<200||status>=300){const error=new Error(data?.error||(status===401?'请重新登录。':'连接电脑失败，输入已保留。'));error.status=status;error.code=data?.code;if(status===401){unauthorized=true;connected=false;if(!native){token='';try{sessionStorage.removeItem('member-progress-token');}catch{}}}throw error;}
    return data;
  }
  function applyState(value) {if(value&&Array.isArray(value.students)){db={...db,...value};loaded=true;}else if(value&&Array.isArray(value.messages)){assistant={...assistant,...value};}}
  async function refresh() {
    if(refreshPromise)return refreshPromise;
    refreshPromise=(async()=>{
      const results=await Promise.allSettled([api('GET','/api/mobile/state'),api('GET','/api/mobile/assistant')]);
      let failure=null;for(const result of results){if(result.status==='fulfilled')applyState(result.value);else failure=result.reason;}
      connected=!failure;connectionError=failure?.message||'';if(!failure)unauthorized=false;render();
      if(loaded&&!nativeReady){nativeReady=true;try{window.MobileNative?.nativeReady?.();}catch{}}
      return !failure;
    })().finally(()=>{refreshPromise=null;});return refreshPromise;
  }
  async function poll(generation) {
    while(generation===pollGeneration&&!document.hidden&&!unauthorized){
      try{
        const result=await api('GET',`/api/mobile/changes?version=${encodeURIComponent(changeVersion)}`);
        if(generation!==pollGeneration||document.hidden)return;
        changeVersion=result.version??changeVersion;
        if(result.changed||!connected)await refresh();
        if(!result.changed)await new Promise(resolve=>setTimeout(resolve,250));
      }catch(error){
        if(generation!==pollGeneration)return;
        connected=false;connectionError=error.message;render();
        if(unauthorized)return;
        await new Promise(resolve=>setTimeout(resolve,5000));
      }
    }
  }
  async function connect(){const generation=++pollGeneration;await refresh();if(generation===pollGeneration&&!document.hidden&&!unauthorized)void poll(generation);}
  async function action(name,payload={},expectedVersion) {
    const fingerprint=JSON.stringify([name,payload,expectedVersion]);
    if(!requestIds.has(fingerprint))requestIds.set(fingerprint,uid());
    const requestId=requestIds.get(fingerprint);
    const result=await api('POST','/api/mobile/action',{action:name,payload,requestId,...(expectedVersion?{expectedVersion}:{})});
    requestIds.delete(fingerprint);connected=true;connectionError='';applyState(result);
    if(name.startsWith('alarm.')||name==='reminder.snooze'||name==='assistant.send')try{window.MobileNative?.requestSync();}catch{}
    return result;
  }
  function toast(text,undo){clearTimeout(toastTimer);const el=$('#toast');el.innerHTML=esc(text)+(undo?`<button data-action="undo-task" data-id="${esc(undo)}">撤销</button>`:'');el.classList.add('show');toastTimer=setTimeout(()=>el.classList.remove('show'),undo?6500:3500);}
  function capture(){
    const active=document.activeElement,focus=active?.dataset.focus,selection=active&&'selectionStart'in active?[active.selectionStart,active.selectionEnd]:null;
    const scrolls=[...document.querySelectorAll('[data-scroll]')].map(el=>({key:el.dataset.scroll,left:el.scrollLeft,top:el.scrollTop,bottom:el.scrollHeight-el.scrollTop-el.clientHeight<45}));
    return {focus,selection,scrolls};
  }
  function restore(saved){
    saved.scrolls.forEach(p=>{const el=document.querySelector(`[data-scroll="${CSS.escape(p.key)}"]`);if(el){el.scrollLeft=p.left;el.scrollTop=p.bottom&&p.key==='gpt'?el.scrollHeight:p.top;}});
    const pager=$('.member-pages');if(pager)pager.scrollLeft=memberPage*pager.clientWidth;
    if(saved.focus){const el=document.querySelector(`[data-focus="${CSS.escape(saved.focus)}"]`);if(el){el.focus({preventScroll:true});try{if(saved.selection)el.setSelectionRange(...saved.selection);}catch{}}}
  }
  function render(){
    const saved=capture();
    if(!loaded&&(unauthorized||!native&&!token)){renderLogin();renderSheet();return;}
    if(!loaded){$('#app').innerHTML=`<div class="login"><img src="/logo.png" alt=""><h1>成员进度</h1><p>${esc(connectionError||'正在连接电脑…')}</p><button class="primary" data-action="reconnect">重新连接</button>${native?'<button class="plain" data-action="logout">修改连接</button>':''}</div>`;renderSheet();return;}
    const title={members:'成员',alarms:'提醒',gpt:'GPT 聊天'}[tab];
    $('#app').innerHTML=`<header class="app-header"><div class="row spread"><div class="brand"><img src="/logo.png" alt=""><h1>${title}</h1></div><div class="header-actions">${tab==='alarms'?`<button class="icon-button" data-action="new-alarm" aria-label="新建提醒">${icon('plus')}</button>`:''}<button class="icon-button" data-action="connection" aria-label="连接设置">${icon('more')}</button></div></div></header>${!connected?`<div class="connection-banner"><span>${esc(unauthorized?'登录已失效，输入已保留。':connectionError||'电脑未连接，输入已保留。')}</span><button data-action="${unauthorized?'logout':'reconnect'}">${unauthorized?'重新登录':'重连'}</button></div>`:''}<main class="main">${tab==='members'?renderMembers():tab==='alarms'?renderAlarms():renderGpt()}</main><nav class="nav-bottom" aria-label="主要页面">${[['members','members','成员'],['alarms','bell','提醒'],['gpt','spark','GPT']].map(([value,symbol,label])=>`<button data-action="nav" data-tab="${value}" class="${tab===value?'active':''}" ${tab===value?'aria-current="page"':''}>${icon(symbol)}<span>${label}</span></button>`).join('')}</nav>${renderDetail()}`;
    renderSheet();restore(saved);
    const pager=$('.member-pages');if(pager)pager.addEventListener('scroll',()=>{const next=Math.round(pager.scrollLeft/pager.clientWidth);if(next!==memberPage){memberPage=next;document.querySelectorAll('[data-action="member-page"]').forEach(el=>{el.classList.toggle('active',Number(el.dataset.page)===next);el.setAttribute('aria-selected',String(Number(el.dataset.page)===next));});}},{passive:true});
  }
  function renderLogin(){
    $('#app').innerHTML=`<div class="login"><img src="/logo.png" alt=""><h1>连接成员进度</h1><p>输入电脑端设置的连接密码。</p><form data-form="login"><input name="password" data-focus="login-password" type="password" placeholder="连接密码" autocomplete="current-password" required>${loginError?`<div class="sheet-error">${esc(loginError)}</div>`:''}<button class="primary" ${loginBusy?'disabled':''}>${loginBusy?'连接中…':'连接'}</button></form><small>${esc(location.host)}</small></div>`;
  }
  function memberLayout(student){const reminders=window.GanttLayout.reminderDays(db.alarms,db.projects,student.id,range),packed=window.GanttLayout.packTasks(scopeTasks('student',student.id),range),count=Math.max(0,...reminders.map(d=>d.alarms.length)),band=count?count*18+8:8;return {reminders,...packed,band,height:Math.max(84,band+packed.laneCount*27+9)};}
  function renderMembers(){
    const q=search.trim().toLowerCase(),members=db.students.filter(s=>!q||[s.name,s.grade,s.currentSummary,s.bio,...s.tags||[],...scopeTasks('student',s.id).map(t=>t.title)].join(' ').toLowerCase().includes(q));
    const layouts=new Map(members.map(s=>[s.id,memberLayout(s)]));
    return `<div class="page-toolbar"><input class="search" data-input="search" data-focus="member-search" value="${esc(search)}" placeholder="搜索成员、进展" aria-label="搜索成员"><div class="segmented" role="tablist" aria-label="成员视图"><button role="tab" aria-selected="${memberPage===0}" data-action="member-page" data-page="0" class="${memberPage===0?'active':''}">进展</button><button role="tab" aria-selected="${memberPage===1}" data-action="member-page" data-page="1" class="${memberPage===1?'active':''}">甘特图 →</button></div></div><div class="member-pages" data-scroll="member-pages"><section class="member-panel" aria-label="当前进展"><div class="list-caption" style="height:71px;align-items:flex-end;padding-bottom:11px"><span>${members.length} 位成员</span><span>点击进展即可修改</span></div>${members.map(s=>renderMember(s,layouts.get(s.id))).join('')||`<div class="empty">${search?'没有匹配的成员':'在电脑端添加成员后，这里会自动显示。'}</div>`}</section><section class="member-panel" aria-label="甘特图"><div class="gantt-toolbar"><button data-action="range" data-days="-14">‹ 较早</button><span>${shortDate(range.start)} — ${shortDate(addDays(range.start,range.days-1))}</span><div><button data-action="range" data-days="0">今天</button><button data-action="range" data-days="14">较晚 ›</button></div></div><div class="gantt-grid"><div class="gantt-names"><div class="gantt-name-header">成员</div>${members.map(s=>`<button class="gantt-name" style="--row-height:${layouts.get(s.id).height}px" data-action="detail" data-type="student" data-id="${esc(s.id)}">${esc(s.name)}</button>`).join('')}</div><div class="gantt-dates" data-scroll="gantt-dates" style="--timeline-width:${range.days*58}px"><div class="gantt-date-header">${Array.from({length:range.days},(_,i)=>{const date=addDays(range.start,i);return `<div class="gantt-date${date===today()?' today':''}"><b>${shortDate(date)}</b><span>${'日一二三四五六'[new Date(`${date}T12:00:00`).getDay()]}</span></div>`;}).join('')}</div>${members.map(s=>renderTrack(s,layouts.get(s.id))).join('')}</div></div></section></div>`;
  }
  function renderMember(s,layout){
    const tasks=scopeTasks('student',s.id),active=tasks.filter(t=>!t.done),summary=s.currentSummary||active.map(t=>t.title).join('；'),alarms=applicableAlarms(s.id),next=alarms.find(a=>a.enabled),projects=db.projects.filter(p=>(p.studentIds||[]).includes(s.id));
    return `<article class="member-item" style="--row-height:${layout.height}px" data-member-id="${esc(s.id)}"><div class="member-name-row"><button class="member-name" data-action="detail" data-type="student" data-id="${esc(s.id)}">${esc(s.name)}</button>${s.grade?`<span class="pill">${esc(s.grade)}</span>`:''}<div class="chips">${s.tags?.slice(0,2).map(t=>`<span class="pill">${esc(t)}</span>`).join('')||''}${tasks.some(overdue)?'<span class="pill overdue">逾期</span>':''}${tasks.some(blocked)?'<span class="pill blocked">受阻</span>':''}</div>${alarms.length?`<button class="member-reminder" data-action="member-alarms" data-id="${esc(s.id)}" aria-label="${esc(s.name)}的 ${alarms.length} 条相关提醒">${icon('bell')}${next?stamp(next.when):alarms.length+' 条'}</button>`:''}</div><button class="member-summary${summary?'':' blank'}" data-action="edit-summary" data-id="${esc(s.id)}" aria-label="编辑${esc(s.name)}的当前进展">${esc(summary)}</button>${projects.length?`<div class="member-projects">${projects.map(p=>`<button class="pill" data-action="detail" data-type="project" data-id="${esc(p.id)}">${esc(p.name)}</button>`).join('')}</div>`:''}</article>`;
  }
  function renderTrack(student,layout){
    const byDate=new Map(layout.reminders.map(d=>[d.date,d]));
    return `<div class="gantt-track" data-track-id="${esc(student.id)}" style="--row-height:${layout.height}px;--band:${layout.band}px">${Array.from({length:range.days},(_,i)=>{const date=addDays(range.start,i),day=byDate.get(date);return `<button class="gantt-cell${date===today()?' today':''}${day?' has-reminder':''}${day?.alarms.every(a=>a.scopeType==='global')?' global-only':''}" style="--offset:${i}" data-action="date" data-id="${esc(student.id)}" data-date="${date}" aria-label="${esc(student.name)} ${date} 添加任务或提醒"></button>`;}).join('')}${layout.reminders.map(day=>day.alarms.map((entry,row)=>`<button class="gantt-marker ${entry.scopeType}${entry.fired?' fired':''}" style="--offset:${day.offset};--reminder-row:${row}" data-action="edit-alarm" data-id="${esc(entry.alarm.id)}" aria-label="${esc(entry.alarm.title)} ${stamp(entry.when)}" title="${esc(entry.alarm.title)}">${clock(entry.when)}</button>`).join('')).join('')}${layout.lanes.map((lane,index)=>lane.map(({task,offset,span})=>`<button class="gantt-bar${task.done?' done':overdue(task)?' overdue':blocked(task)?' blocked':''}" style="--offset:${offset};--span:${span};--lane:${index}" data-action="edit-task" data-id="${esc(task.id)}" aria-label="${esc(task.title)}，${task.start||''}至${task.due||''}">${esc(task.title)}</button>`).join('')).join('')}</div>`;
  }
  function cancelAlarmButton(a){return a?.enabled?`<button type="button" class="alarm-cancel" data-action="${a.repeat==='once'?'cancel-alarm':'skip-alarm'}" data-id="${esc(a.id)}" data-when="${esc(a.when)}" title="取消 ${esc(stamp(a.when))} 这一次${a.repeat==='once'?'':'，后续照常提醒'}" ${working?'disabled':''}>取消本次</button>`:'';}
  function alarmRow(a){return `<article class="alarm-row${a.enabled?'':' off'}"><button class="alarm-clock" data-action="edit-alarm" data-id="${esc(a.id)}">${clock(a.when)}</button><button class="alarm-body" data-action="edit-alarm" data-id="${esc(a.id)}"><strong>${esc(a.title)}</strong><div class="alarm-meta"><span class="pill${a.scopeType==='global'?' blue':''}">${a.scopeType==='project'?'项目 · ':''}${esc(alarmLabel(a))}</span><span>${repeatLabel(a)}${a.enabled?'':' · 已关闭 / 已提醒'}</span></div></button>${a.enabled?cancelAlarmButton(a):`<button type="button" class="alarm-cancel" data-action="toggle-alarm" data-id="${esc(a.id)}" ${working?'disabled':''}>启用整个提醒</button>`}</article>`;}
  function renderAlarms(alarms=db.alarms){
    const sorted=[...alarms].sort((a,b)=>Number(!!b.enabled)-Number(!!a.enabled)||new Date(a.when)-new Date(b.when));let previous='';
    return `<div class="alarm-list">${sorted.map(a=>{const date=key(a.when),group=(!a.enabled?'暂停 / 已提醒':date===today()?'今天':date===addDays(today(),1)?'明天':shortDate(date)),heading=group!==previous?`<div class="date-group">${esc(group)}</div>`:'';previous=group;return heading+alarmRow(a);}).join('')||'<div class="empty">暂无提醒</div>'}</div>`;
  }
  function openDetail(type,id,taskId){if(!scope(type,id))return;detail={type,id,tab:taskId?'archive':'tasks',completeTaskId:taskId||null};render();}
  function renderDetail(){
    if(!detail)return '';const record=scope(detail.type,detail.id);if(!record){detail=null;return '';}
    const tasks=scopeTasks(detail.type,detail.id),draft=archiveDrafts.get(`${detail.type}:${detail.id}`)||'',messages=scopeMessages(detail.type,detail.id),complete=db.tasks.find(t=>t.id===detail.completeTaskId&&!t.done);
    return `<section class="detail" aria-label="${esc(record.name)}详情"><header class="detail-header"><div class="row"><button class="icon-button" data-action="close-detail" aria-label="返回">${icon('back')}</button><div class="detail-title grow"><h2>${esc(record.name)}</h2>${detail.type==='project'?'<small>项目</small>':record.grade||record.tags?.length?`<div class="chips">${[record.grade,...record.tags||[]].filter(Boolean).map(t=>`<span class="pill">${esc(t)}</span>`).join('')}</div>`:''}</div><button class="icon-button" data-action="scope-alarm" aria-label="添加相关提醒">${icon('bell')}</button><button class="icon-button" data-action="reference-current" aria-label="引用整个归档到 GPT">${icon('spark')}</button></div><div class="row" style="justify-content:center;margin-top:5px"><div class="segmented"><button data-action="detail-tab" data-tab="tasks" class="${detail.tab==='tasks'?'active':''}">任务</button><button data-action="detail-tab" data-tab="archive" class="${detail.tab==='archive'?'active':''}">归档</button></div></div></header><div class="detail-scroll" data-scroll="detail"><div class="detail-summary"><button class="member-summary${record.currentSummary?'':' blank'}" ${detail.type==='student'?`data-action="edit-summary" data-id="${esc(record.id)}" aria-label="编辑当前进展"`:'disabled'}>${esc(record.currentSummary)}</button></div>${['pending','running'].includes(record.summaryStatus)?'<div class="summary-status"><span class="spinner"></span> GPT 正在整理摘要…</div>':record.summaryStatus==='error'?`<div class="summary-status error">${esc(record.summaryError||'摘要未生成，记录已保存。')}<button data-action="retry-summary">重试</button></div>`:''}${detail.tab==='tasks'?`<div class="section-header"><h3>任务${tasks.length?' · '+tasks.length:''}</h3><button data-action="new-task">${icon('plus')} 添加</button></div><div class="task-list">${tasks.map(renderTask).join('')}</div><div class="section-header"><h3>相关提醒</h3><button data-action="scope-alarm">添加</button></div><div class="alarm-list">${(detail.type==='student'?applicableAlarms(detail.id):db.alarms.filter(a=>a.scopeType==='global'||a.scopeType==='project'&&a.projectId===detail.id)).map(alarmRow).join('')}</div>`:`<div class="archive-thread">${messages.map(m=>renderArchiveMessage(m,tasks)).join('')||'<div class="empty">发送文字，记录本次进展。</div>'}</div>`}</div>${detail.tab==='archive'?`<div class="detail-composer">${complete?`<div class="complete-context"><span>${esc(complete.title)} · 发送后完成</span><button data-action="clear-complete" aria-label="仅归档，不完成任务">${icon('close')}</button></div>`:''}<div class="composer-row"><textarea data-input="archive" data-focus="archive-draft" rows="1" placeholder="记下进展、反馈或下一步…" aria-label="归档消息" ${working?'disabled':''}>${esc(draft)}</textarea><button class="primary" data-action="send-archive" ${working||!draft.trim()?'disabled':''}>${complete?'提交并完成':'发送'}</button></div></div>`:''}</section>`;
  }
  function renderTask(t){return `<article class="task-row${t.done?' done':''}"><button class="task-check${t.done?' done':''}" data-action="toggle-task" data-id="${esc(t.id)}" aria-label="${t.done?'重新打开':'完成'}${esc(t.title)}" aria-pressed="${!!t.done}" ${working?'disabled':''}>${icon(t.done?'check':'square')}</button><button class="task-text" data-action="edit-task" data-id="${esc(t.id)}"><strong>${esc(t.title)}</strong><div class="task-meta">${t.start||t.due?`<span>${shortDate(t.start)}${t.start&&t.due?' — ':''}${shortDate(t.due)}</span>`:''}${t.projectId?`<span>${esc(scope('project',t.projectId)?.name||'项目')}</span>`:''}${overdue(t)?'<span class="pill overdue">逾期</span>':''}${(t.tags||[]).map(tag=>`<span class="pill${/受阻|阻塞|卡住/.test(tag)?' blocked':''}">${esc(tag)}</span>`).join('')}</div></button>${!t.done?`<button class="task-submit" data-action="submit-task" data-id="${esc(t.id)}">提交</button>`:''}</article>`;}
  function renderArchiveMessage(m,tasks){return `<article class="archive-message ${m.role==='user'?'user':'assistant'}"><div class="message-meta"><span>${m.role==='user'?'我':'GPT · 自动摘要'}</span><time>${stamp(m.createdAt)}</time></div><div class="message-bubble">${esc(m.text)}${(m.attachments||[]).map(a=>`<span class="attachment-name">附件 · ${esc(a.name)}</span>`).join('')}${(m.suggestions||[]).length?`<div class="suggestion-list">${m.suggestions.map((s,i)=>`<button data-action="suggestion" data-message-id="${esc(m.id)}" data-index="${i}" ${tasks.some(t=>t.title===s.title)||working?'disabled':''}>${tasks.some(t=>t.title===s.title)?'已加入':'＋'} ${esc(s.title)}</button>`).join('')}</div>`:''}</div></article>`;}

  function renderGpt(){
    const messages=assistant.messages||[];
    return `<section class="gpt-view"><div class="gpt-tools">${assistant.canUndoClear?`<button data-action="undo-clear" ${working||assistant.busy?'disabled':''}>撤销清空</button>`:''}<button data-action="clear-gpt" ${working||!messages.length&&!assistant.busy?'disabled':''}>清空会话</button></div><div class="gpt-thread" data-scroll="gpt">${messages.length?messages.map(renderGptMessage).join(''):`<div class="gpt-welcome">${icon('spark')}<h2>接下来，先跟进哪件事？</h2><p>参考全部提醒和最近归档，直接问。<br>也可以说出安排，创建任务或提醒。</p></div>`}${assistant.busy?'<div class="gpt-working"><span class="spinner"></span>正在整理资料并回答…<button data-action="cancel-gpt">停止</button></div>':assistant.error?`<div class="gpt-error">${esc(assistant.error)}${messages.some(m=>m.role==='user')?`<button data-action="retry-gpt" ${working?'disabled':''}>重试</button>`:''}</div>`:''}</div><div class="gpt-composer"><div class="chips"><button class="chip${includeReminders?' selected':''}" data-action="context-reminders" aria-pressed="${includeReminders}">${icon('bell')}全部提醒</button><button class="chip${includeRecent?' selected':''}" data-action="context-recent" aria-pressed="${includeRecent}">${icon('archive')}最近归档</button><button class="chip" data-action="reference-picker">＋ 引用归档</button>${refs.map((r,i)=>`<button class="chip selected" data-action="remove-reference" data-index="${i}" aria-label="移除${esc(r.title)}引用">${esc(r.title)} ×</button>`).join('')}</div><div class="composer-row"><textarea data-input="gpt" data-focus="gpt-draft" rows="1" placeholder="问问进展，或安排任务、提醒…" aria-label="GPT 聊天问题" ${working?'disabled':''}>${esc(gptDraft)}</textarea><button class="primary" data-action="send-gpt" ${working||assistant.busy||!gptDraft.trim()?'disabled':''}>发送</button></div></div></section>`;
  }
  function renderGptMessage(m){return `<article class="gpt-message ${m.role==='user'?'user':'assistant'}"><div class="message-meta"><span>${m.role==='user'?'我':'GPT'}</span><time>${stamp(m.createdAt)}</time></div><div class="message-bubble">${esc(m.text)}${m.references?.length?`<div class="gpt-references">${m.references.map(r=>`<button data-action="detail" data-type="${esc(r.scopeType)}" data-id="${esc(r.scopeId)}">${esc(r.title||scope(r.scopeType,r.scopeId)?.name||'引用归档')}</button>`).join('')}</div>`:''}</div>${(m.taskResults||[]).map(t=>`<button class="gpt-reminder" data-action="edit-task" data-id="${esc(t.id)}">${icon('calendar')}<span><small>已创建任务</small><strong>${esc(t.title)}</strong><small>${esc(t.start&&t.due&&t.start!==t.due?`${shortDate(t.start)} — ${shortDate(t.due)}`:shortDate(t.start||t.due))}${t.scopeLabel?' · '+esc(t.scopeLabel):''}</small></span></button>`).join('')}${(m.reminderResults||[]).map(a=>`<button class="gpt-reminder" data-action="edit-alarm" data-id="${esc(a.id)}">${icon('bell')}<span><strong>${esc(a.title)}</strong><small>${stamp(a.when)} · ${esc(a.scopeLabel||alarmLabel(a))} · ${repeatLabel(a)}</small></span></button>`).join('')}</article>`;}

  function nextTime(){const d=new Date();d.setHours(d.getHours()+1,0,0,0);return localInput(d);}
  function openAlarm(id,scopeValue='global',date){const a=db.alarms.find(x=>x.id===id);sheet={type:'alarm',error:'',expected:a?version('alarm',a.id):undefined,values:a?{id:a.id,scope:a.scopeType==='global'?'global':`${a.scopeType}:${a.scopeType==='project'?a.projectId:a.studentId}`,title:a.title,when:localInput(a.when),originalWhen:a.when,originalLocal:localInput(a.when),originalRepeat:a.repeat,repeat:a.repeat,intervalDays:a.intervalDays||1,enabled:!!a.enabled,monthDay:a.monthDay||new Date(a.when).getDate()}:{id:null,scope:scopeValue,title:'',when:date?`${date}T09:00`:nextTime(),repeat:'once',intervalDays:1,enabled:true}};renderSheet();}
  function openTask(id,type=detail?.type||'student',scopeId=detail?.id,start){const t=db.tasks.find(x=>x.id===id);sheet={type:'task',error:'',expected:t?version('task',t.id):undefined,values:t?{id:t.id,title:t.title,start:t.start||'',due:t.due||'',tags:(t.tags||[]).join('; ')}:{id:null,scopeType:type,scopeId,title:'',start:start||'',due:start||'',tags:''}};renderSheet();}
  function openSummary(id){const s=scope('student',id);if(!s)return;sheet={type:'summary',error:'',expected:version('student',id),values:{id,text:s.currentSummary||''}};renderSheet();setTimeout(()=>$('[data-focus="sheet-text"]')?.focus(),100);}
  const field=(label,name,value,type='text',required=false)=>`<label class="field"><span>${label}</span><input name="${name}" data-field="${name}" data-focus="sheet-${name}" type="${type}" value="${esc(value)}" ${required?'required':''} ${name==='title'?'maxlength="500"':''}></label>`;
  function scopeOptions(value){return `<option value="global" ${value==='global'?'selected':''}>全局提醒</option><optgroup label="成员">${db.students.map(s=>`<option value="student:${esc(s.id)}" ${value===`student:${s.id}`?'selected':''}>${esc(s.name)}</option>`).join('')}</optgroup><optgroup label="项目">${db.projects.map(p=>`<option value="project:${esc(p.id)}" ${value===`project:${p.id}`?'selected':''}>项目 · ${esc(p.name)}</option>`).join('')}</optgroup>`;}
  function renderSheet(){
    if(!sheet){$('#sheet-root').innerHTML='';return;}const saved=capture(),v=sheet.values||{};let title='',content='';
    const error=sheet.error?`<div class="sheet-error" role="alert">${esc(sheet.error)}${sheet.conflict?`<div class="conflict-latest">最新内容：\n${esc(sheet.latest||'此记录已删除。')}</div>${sheet.latest?'<button type="button" data-action="confirm-overwrite">使用我的内容保存</button>':''}`:''}</div>`:'';
    if(sheet.type==='summary'){title=`${scope('student',v.id)?.name||'成员'} · 当前进展`;content=`<form data-form="summary"><textarea name="text" data-field="text" data-focus="sheet-text" rows="5" aria-label="当前进展">${esc(v.text)}</textarea>${error}<div class="sheet-actions"><button type="button" class="plain" data-action="close-sheet">取消</button><button class="primary" ${working?'disabled':''}>${working?'保存中…':'保存'}</button></div></form>`;}
    else if(sheet.type==='task'){title=v.id?'编辑任务':'添加任务';content=`<form data-form="task">${field('任务','title',v.title,'text',true)}<div class="field-row">${field('开始日期','start',v.start,'date')}${field('截止日期','due',v.due,'date')}</div><div class="chips"><button type="button" class="chip" data-action="task-date" data-preset="today">今天</button><button type="button" class="chip" data-action="task-date" data-preset="tomorrow">明天</button><button type="button" class="chip" data-action="task-date" data-preset="week">7 天内</button><button type="button" class="chip" data-action="task-date" data-preset="none">不设日期</button></div>${field('标签（分号隔开）','tags',v.tags)}${error}<div class="sheet-actions">${v.id?'<button type="button" class="plain danger" data-action="delete-task">删除任务</button>':'<button type="button" class="plain" data-action="close-sheet">取消</button>'}<button class="primary" ${working?'disabled':''}>${working?'保存中…':'保存'}</button></div></form>`;}
    else if(sheet.type==='alarm'){title=v.id?'编辑提醒':'新建提醒';content=`<form data-form="alarm"><label class="field"><span>提醒对象</span><select data-field="scope" data-focus="sheet-scope" name="scope">${scopeOptions(v.scope)}</select></label>${field('提醒内容','title',v.title,'text',true)}${v.scope==='global'&&(db.preferences?.recentGlobalAlarmLabels||[]).length?`<div class="chips">${db.preferences.recentGlobalAlarmLabels.slice(0,6).map(t=>`<button type="button" class="chip" data-action="recent-label" data-value="${esc(t)}">${esc(t)}</button>`).join('')}</div>`:''}<div class="field-row">${field('日期','date',v.when.slice(0,10),'date',true)}${field('时间','time',v.when.slice(11,16),'time',true)}</div><div class="chips">${[['10min','10 分钟后'],['tomorrow','明天 9:00'],['week','一周后 9:00']].map(([p,label])=>`<button type="button" class="chip" data-action="alarm-preset" data-preset="${p}">${label}</button>`).join('')}</div><div class="chips">${['09:00','14:00','17:00','20:00'].map(t=>`<button type="button" class="chip${v.when.slice(11,16)===t?' selected':''}" data-action="alarm-time" data-time="${t}">${t}</button>`).join('')}</div><label class="field"><span>重复</span><select data-field="repeat" data-focus="sheet-repeat">${[['once','仅一次'],['daily','每天'],['weekly','每周'],['monthly','每月'],['interval','每隔几天']].map(([x,l])=>`<option value="${x}" ${v.repeat===x?'selected':''}>${l}</option>`).join('')}</select></label>${v.repeat==='interval'?field('间隔天数','intervalDays',v.intervalDays,'number',true):''}${v.repeat==='monthly'?`<p class="sheet-note">每月 ${v.monthDay||Number(v.when.slice(8,10))} 日 · 不足该日期的月份按电脑端规则处理</p>`:''}${v.id?`<div class="alarm-master"><span>整个提醒</span><button type="button" class="switch${v.enabled?' on':''}" data-action="toggle-alarm" data-id="${esc(v.id)}" aria-label="${v.enabled?'关闭':'启用'}整个提醒" role="switch" aria-checked="${!!v.enabled}" ${working?'disabled':''}></button><small>${v.enabled?'已启用':'已关闭'}</small>${cancelAlarmButton({...v,when:v.originalWhen,repeat:v.originalRepeat})}</div>`:''}${error}<div class="sheet-actions">${v.id?'<button type="button" class="plain danger" data-action="delete-alarm">删除</button><button type="button" class="plain" data-action="snooze-alarm">延后 10 分钟</button>':'<button type="button" class="plain" data-action="close-sheet">取消</button>'}<button class="primary" ${working?'disabled':''}>${working?'保存中…':'保存'}</button></div></form>`;}
    else if(sheet.type==='date'){title=`${scope('student',v.id)?.name||'成员'} · ${shortDate(v.date)}`;content=`<button class="sheet-option" data-action="date-task">${icon('calendar')}添加任务</button><button class="sheet-option" data-action="date-alarm">${icon('bell')}添加提醒</button>`;}
    else if(sheet.type==='member-alarms'){title=`${scope('student',v.id)?.name||'成员'} · 相关提醒`;content=`${renderAlarms(applicableAlarms(v.id))}<button class="primary" style="width:100%;margin-top:15px" data-action="new-member-alarm" data-id="${esc(v.id)}">添加提醒</button>`;}
    else if(sheet.type==='reference'){title='引用整个归档';content=`<div class="reference-options">${[...db.students.map(s=>({...s,type:'student'})),...db.projects.map(p=>({...p,type:'project'}))].map(s=>`<button class="reference-option" data-action="pick-reference" data-type="${s.type}" data-id="${esc(s.id)}">${icon('archive')}<span>${esc(s.name)}</span><small>${s.type==='project'?'项目':'成员'}</small></button>`).join('')}</div>`;}
    else if(sheet.type==='connection'){title='连接电脑';let connection={},notification={};try{connection=JSON.parse(window.MobileNative?.getConnection?.()||'{}');notification=JSON.parse(window.MobileNative?.getNotificationStatus?.()||'{}');}catch{}content=`<div class="connection-detail">${esc(connection.baseUrl||location.origin)}<br>${connected?'已连接':esc(connectionError||'未连接')}</div>${native?`<div class="native-hint" style="margin-top:12px">已缓存 ${Number(notification.cachedCount)||0} 条提醒${notification.lastSync?` · ${stamp(notification.lastSync)}`:''}${notification.lastError?`<br>${esc(notification.lastError)}`:''}${notification.notificationPermission===false?'<br>系统通知未开启，请在手机设置中允许通知。':''}${notification.exactAlarmPermission===false?'<br>系统准时提醒权限未开启，通知可能延后。':''}</div>`:''}<div class="reconnect-actions"><button class="primary" data-action="reconnect">重新同步</button><button class="plain" data-action="logout">退出登录</button></div>`;}
    $('#sheet-root').innerHTML=`<div class="sheet-overlay" data-action="sheet-backdrop"><section class="sheet" role="dialog" aria-modal="true" aria-label="${esc(title)}" data-scroll="sheet"><div class="sheet-handle"></div><header class="sheet-header"><h2>${esc(title)}</h2><button class="icon-button" data-action="close-sheet" aria-label="关闭">${icon('close')}</button></header>${content}</section></div>`;restore(saved);
  }

  async function perform(callback){
    if(working)return;working=true;render();
    try{await callback();}
    catch(error){
      if(error.status===409){await refresh();if(sheet&&['summary','task','alarm'].includes(sheet.type)){sheet.error='电脑端已有更新，输入已保留。请对照最新内容后保存。';sheet.conflict=true;const kind=sheet.type==='summary'?'student':sheet.type==='task'?'task':'alarm',record=kind==='student'?scope('student',sheet.values.id):(kind==='task'?db.tasks:db.alarms).find(x=>x.id===sheet.values.id);sheet.latest=record?(kind==='student'?record.currentSummary:kind==='task'?[record.title,record.start,record.due,(record.tags||[]).join('; ')].filter(Boolean).join('\n'):[record.title,stamp(record.when),repeatLabel(record)].join('\n')):'';}else toast('内容已在另一端更新，请查看最新状态后重试。');}
      else if(sheet){sheet.error=error.message||'保存失败，输入已保留。';}else toast(error.message||'未保存，请稍后重试。');
      if(!error.status||error.status===0){connected=false;connectionError='电脑未连接，输入已保留。';}
    }finally{working=false;render();}
  }
  async function saveSheet(){
    if(!sheet)return;const current=sheet,v=current.values;
    if(current.type==='summary')await action('student.patch',{id:v.id,field:'currentSummary',value:v.text},current.expected);
    else if(current.type==='task'){
      const payload={title:v.title.trim(),start:v.start||null,due:v.due||null,tags:splitTags(v.tags).filter(t=>t!=='逾期')};
      if(v.id)await action('task.update',{id:v.id,...payload},current.expected);
      else await action('task.add',{scopeType:v.scopeType,scopeId:v.scopeId,...payload});
    }else if(current.type==='alarm'){
      const [scopeType,scopeId]=v.scope.split(':'),when=new Date(v.when);if(!Number.isFinite(+when))throw new Error('请选择日期和时间。');
      await action('alarm.save',{id:v.id,scopeType,studentId:scopeType==='student'?scopeId:null,projectId:scopeType==='project'?scopeId:null,title:v.title.trim(),when:v.originalWhen&&v.when===v.originalLocal?v.originalWhen:when.toISOString(),repeat:v.repeat,intervalDays:Number(v.intervalDays)||1,enabled:v.id?v.enabled!==false:true},current.expected);
    }
    if(sheet===current)sheet=null;toast('已保存');
  }
  function addReference(type,id){const s=scope(type,id);if(!s)return;if(!refs.some(r=>r.scopeType===type&&r.scopeId===id))refs.push({scopeType:type,scopeId:id,title:s.name});detail=null;sheet=null;tab='gpt';render();}
  function updateComposerButtons(){const archive=$('[data-action="send-archive"]');if(archive)archive.disabled=working||!($('[data-input="archive"]')?.value.trim());const gpt=$('[data-action="send-gpt"]');if(gpt)gpt.disabled=working||assistant.busy||!gptDraft.trim();}
  document.addEventListener('input',event=>{
    const el=event.target;
    if(el.dataset.input==='search'){search=el.value;render();}
    else if(el.dataset.input==='archive'&&detail){archiveDrafts.set(`${detail.type}:${detail.id}`,el.value);updateComposerButtons();}
    else if(el.dataset.input==='gpt'){gptDraft=el.value;updateComposerButtons();}
    else if(el.dataset.field&&sheet?.values){const name=el.dataset.field;if(name==='date'){sheet.values.when=`${el.value}T${sheet.values.when.slice(11,16)}`;sheet.values.monthDay=Number(el.value.slice(8,10));}else if(name==='time')sheet.values.when=`${sheet.values.when.slice(0,10)}T${el.value}`;else sheet.values[name]=el.value;}
  });
  document.addEventListener('change',event=>{const name=event.target.dataset.field;if(sheet&&['scope','repeat','date','time'].includes(name))renderSheet();});
  document.addEventListener('submit',event=>{
    const form=event.target;if(!form.dataset.form)return;event.preventDefault();
    if(form.dataset.form==='login'){
      const password=new FormData(form).get('password');loginBusy=true;loginError='';render();
      api('POST','/api/mobile/login',{password,deviceName:'手机浏览器'}).then(result=>{token=result.token;unauthorized=false;try{sessionStorage.setItem('member-progress-token',token);}catch{}return connect();}).catch(error=>{loginError=error.message;}).finally(()=>{loginBusy=false;render();});
    }else void perform(saveSheet);
  });
  document.addEventListener('click',event=>{
    const el=event.target.closest('[data-action]');if(!el||el.disabled)return;const a=el.dataset.action,id=el.dataset.id;
    if(a==='sheet-backdrop'&&event.target!==el)return;
    if(a==='nav'){tab=el.dataset.tab;render();return;}
    if(a==='member-page'){memberPage=Number(el.dataset.page);const pager=$('.member-pages');pager?.scrollTo({left:memberPage*pager.clientWidth,behavior:'smooth'});document.querySelectorAll('[data-action="member-page"]').forEach(b=>b.classList.toggle('active',Number(b.dataset.page)===memberPage));return;}
    if(a==='range'){const days=Number(el.dataset.days);range.start=days?addDays(range.start,days):addDays(today(),-2);render();const dates=$('.gantt-dates');if(dates)dates.scrollLeft=0;return;}
    if(a==='detail'){openDetail(el.dataset.type,id);return;}
    if(a==='close-detail'){detail=null;render();return;}
    if(a==='detail-tab'){detail.tab=el.dataset.tab;render();return;}
    if(a==='edit-summary'){openSummary(id);return;}
    if(a==='edit-task'){if(!db.tasks.some(x=>x.id===id)){toast('这个任务已删除。');return;}openTask(id);return;}
    if(a==='new-task'){openTask();return;}
    if(a==='submit-task'&&detail){detail.tab='archive';detail.completeTaskId=id;render();$('[data-input="archive"]')?.focus();return;}
    if(a==='clear-complete'){detail.completeTaskId=null;render();return;}
    if(a==='date'){sheet={type:'date',values:{id,date:el.dataset.date}};renderSheet();return;}
    if(a==='date-task'){const v=sheet.values;openTask(null,'student',v.id,v.date);return;}
    if(a==='date-alarm'){const v=sheet.values;openAlarm(null,`student:${v.id}`,v.date);return;}
    if(a==='new-alarm'){openAlarm();return;}
    if(a==='edit-alarm'){if(!db.alarms.some(x=>x.id===id)){toast('这条提醒已删除。');return;}openAlarm(id);return;}
    if(a==='scope-alarm'&&detail){openAlarm(null,`${detail.type}:${detail.id}`);return;}
    if(a==='new-member-alarm'){openAlarm(null,`student:${id}`);return;}
    if(a==='member-alarms'){sheet={type:'member-alarms',values:{id}};renderSheet();return;}
    if(a==='close-sheet'||a==='sheet-backdrop'){if(!working){sheet=null;renderSheet();}return;}
    if(a==='connection'){sheet={type:'connection'};renderSheet();return;}
    if(a==='reconnect'){sheet=null;try{window.MobileNative?.requestSync();}catch{}void connect();return;}
    if(a==='logout'){++pollGeneration;sendGeneration++;sheet=null;detail=null;if(native){window.MobileNative.logout();return;}token='';try{sessionStorage.removeItem('member-progress-token');}catch{}db={students:[],projects:[],tasks:[],messages:[],alarms:[],preferences:{},recordVersions:{}};assistant={messages:[],busy:false,error:'',canUndoClear:false};archiveDrafts.clear();gptDraft='';refs=[];loaded=false;unauthorized=false;render();return;}
    if(a==='task-date'){const v=sheet.values,p=el.dataset.preset;v.start=p==='none'?'':p==='tomorrow'?addDays(today(),1):today();v.due=p==='week'?addDays(today(),7):v.start;renderSheet();return;}
    if(a==='recent-label'){sheet.values.title=el.dataset.value;renderSheet();return;}
    if(a==='alarm-time'){sheet.values.when=`${sheet.values.when.slice(0,10)}T${el.dataset.time}`;renderSheet();return;}
    if(a==='alarm-preset'){const d=new Date(),p=el.dataset.preset;if(p==='10min')d.setMinutes(d.getMinutes()+10);else{d.setDate(d.getDate()+(p==='tomorrow'?1:7));d.setHours(9,0,0,0);}sheet.values.when=localInput(d);sheet.values.monthDay=d.getDate();renderSheet();return;}
    if(a==='reference-current'&&detail){addReference(detail.type,detail.id);return;}
    if(a==='reference-picker'){sheet={type:'reference'};renderSheet();return;}
    if(a==='pick-reference'){addReference(el.dataset.type,id);return;}
    if(a==='remove-reference'){refs.splice(Number(el.dataset.index),1);render();return;}
    if(a==='context-reminders'){includeReminders=!includeReminders;render();return;}
    if(a==='context-recent'){includeRecent=!includeRecent;render();return;}
    void perform(async()=>{
      if(a==='toggle-task'){const t=db.tasks.find(x=>x.id===id);await action('task.update',{id,done:!t.done},version('task',id));}
      else if(a==='delete-task'&&sheet){const current=sheet;await action('task.delete',{id:current.values.id},current.expected);sheet=null;toast('任务已删除',current.values.id);}
      else if(a==='undo-task'){await action('task.restore',{id});toast('任务已恢复');}
      else if(a==='toggle-alarm'){const alarm=db.alarms.find(x=>x.id===id),editing=sheet?.type==='alarm'&&sheet.values.id===id;await action('alarm.enabled',{id,enabled:!alarm.enabled},editing?sheet.expected:version('alarm',id));if(editing){sheet.values.enabled=!!db.alarms.find(x=>x.id===id)?.enabled;sheet.expected=version('alarm',id);}}
      else if(a==='skip-alarm'||a==='cancel-alarm'){const editing=sheet?.type==='alarm'&&sheet.values.id===id;await action(a==='skip-alarm'?'alarm.skip':'alarm.enabled',a==='skip-alarm'?{id,when:el.dataset.when}:{id,enabled:false},editing?sheet.expected:version('alarm',id));const alarm=db.alarms.find(x=>x.id===id);if(editing)sheet=null;toast(`本次已取消${alarm?.enabled?'，下次 '+stamp(alarm.when):''}`);}
      else if(a==='delete-alarm'&&sheet){await action('alarm.delete',{id:sheet.values.id},sheet.expected);sheet=null;toast('提醒已删除');}
      else if(a==='snooze-alarm'&&sheet){await action('reminder.snooze',{id:sheet.values.id,minutes:10},sheet.expected);sheet=null;toast('已延后 10 分钟');}
      else if(a==='confirm-overwrite'&&sheet){const kind=sheet.type==='summary'?'student':sheet.type==='task'?'task':'alarm';sheet.expected=version(kind,sheet.values.id);sheet.conflict=false;sheet.error='';await saveSheet();}
      else if(a==='send-archive'&&detail){const current={...detail},draftKey=`${current.type}:${current.id}`,text=(archiveDrafts.get(draftKey)||'').trim();if(!text)return;const task=db.tasks.find(t=>t.id===current.completeTaskId&&!t.done);await action('chat.send',{scopeType:current.type,scopeId:current.id,text,...(task?{completeTaskId:task.id}:{})},task?version('task',task.id):undefined);archiveDrafts.delete(draftKey);if(detail?.id===current.id)detail.completeTaskId=null;toast(task?'已归档并完成':'已归档');}
      else if(a==='retry-summary'&&detail)await action('summary.retry',{scopeType:detail.type,scopeId:detail.id});
      else if(a==='suggestion'&&detail){const m=db.messages.find(m=>m.id===el.dataset.messageId),suggestion=m?.suggestions?.[Number(el.dataset.index)];if(suggestion)await action('task.add',{scopeType:detail.type,scopeId:detail.id,title:suggestion.title});}
      else if(a==='send-gpt'||a==='retry-gpt'){
        const generation=sendGeneration,payload=a==='retry-gpt'?{retryMessageId:[...assistant.messages].reverse().find(m=>m.role==='user')?.id}:{text:gptDraft.trim(),includeReminders,includeRecent,references:refs.map(r=>({...r}))};
        if(a==='send-gpt'&&!payload.text)return;await action('assistant.send',payload);if(a==='send-gpt'&&generation===sendGeneration){gptDraft='';clearedComposer=null;}
      }
      else if(a==='cancel-gpt')await action('assistant.cancel',{},assistant.version);
      else if(a==='clear-gpt'){const before={draft:gptDraft,refs:refs.map(r=>({...r}))};sendGeneration++;await action('assistant.clear',{},assistant.version);clearedComposer=before;gptDraft='';refs=[];}
      else if(a==='undo-clear'){await action('assistant.undo-clear',{},assistant.version);gptDraft=clearedComposer?.draft||'';refs=clearedComposer?.refs||[];clearedComposer=null;}
    });
  });
  window.MobileBack=()=>{if(working)return true;if(sheet){sheet=null;renderSheet();return true;}if(detail){detail=null;render();return true;}if(tab!=='members'){tab='members';render();return true;}if(memberPage){memberPage=0;render();return true;}return false;};
  window.MobileNotificationStatus=()=>{if(sheet?.type==='connection')renderSheet();};
  window.MobileOpenScope=(type,id)=>{if(!loaded){pendingScope={type,id};return;}if((type==='student'||type==='project')&&scope(type,id)){openDetail(type,id);detail.tab='tasks';}else{detail=null;tab='alarms';}render();};
  let pendingScope=null;
  document.addEventListener('visibilitychange',()=>{if(document.hidden)++pollGeneration;else void connect();});
  window.addEventListener('online',()=>void connect());
  window.addEventListener('resize',()=>{const pager=$('.member-pages');if(pager)pager.scrollLeft=memberPage*pager.clientWidth;});
  render();
  if(native||token)void connect().then(()=>{if(pendingScope){const value=pendingScope;pendingScope=null;window.MobileOpenScope(value.type,value.id);}});
})();
