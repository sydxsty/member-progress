(() => {
  'use strict';
  const $ = s => document.querySelector(s);
  const icons = {
    grid: '<rect x="3" y="3" width="7" height="7" rx="2"/><rect x="14" y="3" width="7" height="7" rx="2"/><rect x="3" y="14" width="7" height="7" rx="2"/><path d="M14 15h7M14 20h5"/>',
    users: '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75"/><circle cx="9" cy="7" r="4"/>',
    folder: '<path d="M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z"/>',
    archive: '<rect x="3" y="3" width="18" height="5" rx="1.5"/><path d="M5 8v11a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8M9 12h6"/>',
    bell: '<path d="M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4"/>',
    settings: '<path d="m9.2 3-.6 2.2-2 .9-2.1-.6-1.8 3.1 1.6 1.6-.2 2.2-1.4 1.7 1.8 3.1 2.2-.6 1.9 1.1.6 2.3h3.6l.6-2.3 1.9-1.1 2.2.6 1.8-3.1-1.5-1.7-.1-2.2 1.6-1.6-1.8-3.1-2.1.6-2-.9-.6-2.2Z"/><circle cx="11" cy="11.5" r="3"/>',
    plus: '<path d="M12 5v14M5 12h14"/>', search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 4 4"/>',
    close: '<path d="m6 6 12 12M6 18 18 6"/>', check: '<path d="m5 12 4 4L19 6"/>',
    send: '<path d="m21 3-7 18-4-7-7-4Z M21 3 10 14"/>', file: '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M8 13h8M8 17h5"/>',
    clip: '<path d="m21 11-9 9a6 6 0 0 1-8-8L14 2a4 4 0 0 1 6 6L10 18a2 2 0 0 1-3-3l9-9"/>',
    spark: '<path d="m12 3 2.4 6.6L21 12l-6.6 2.4L12 21l-2.4-6.6L3 12l6.6-2.4ZM20 2v4M18 4h4"/>',
    edit: '<path d="m15 4 5 5M4 20l4-1 13-13a2 2 0 0 0-5-5L3 14l-1 6Z"/>',
    grip: '<circle cx="8" cy="5" r="1"/><circle cx="16" cy="5" r="1"/><circle cx="8" cy="12" r="1"/><circle cx="16" cy="12" r="1"/><circle cx="8" cy="19" r="1"/><circle cx="16" cy="19" r="1"/>',
    dots: '<circle cx="5" cy="12" r="1"/><circle cx="12" cy="12" r="1"/><circle cx="19" cy="12" r="1"/>',
    arrow: '<path d="M5 12h14m-6-6 6 6-6 6"/>', refresh: '<path d="M20 7v5h-5M4 17v-5h5M6.1 6.1a8 8 0 0 1 13.3 3M4.6 14.9a8 8 0 0 0 13.3 3"/>',
    download: '<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>', upload: '<path d="M12 16V4m-5 5 5-5 5 5M4 16v5h16v-5"/>',
    trash: '<path d="M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7"/>', clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    chat: '<path d="M21 11.5a8.4 8.4 0 0 1-.9 3.8 8.5 8.5 0 0 1-7.6 4.7 8.4 8.4 0 0 1-3.8-.9L3 21l1.9-5.7a8.4 8.4 0 0 1-.9-3.8A8.5 8.5 0 0 1 8.7 4a8.4 8.4 0 0 1 3.8-.9h.5a8.5 8.5 0 0 1 8 8Z"/>'
  };
  const icon = (name) => `<svg class="icon" viewBox="0 0 24 24" aria-hidden="true">${icons[name] || icons.file}</svg>`;
  const esc = (x) => String(x ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const attr = esc;
  const native = !!window.chrome?.webview;
  let db = {students:[], archivedStudents:[], projects:[], tasks:[], messages:[], alarms:[], preferences:{recentStudentLabels:[],recentGlobalAlarmLabels:[]}, settings:{}, status:{}};
  let view = 'students', search = '', archiveFilter = 'all', selectedProject = null;
  let drawer = null, modal = null, inlineAdd = null, inlineEdit = null, uploading = false, dragDepth = 0, repainting = false;
  const drafts = new Map(), taskDrafts = new Map(), pending = new Map(), busy = new Set();
  let rpcId = 0, timelineGesture = null, studentGesture = null, suppressTimelineClickUntil = 0, suppressStudentClickUntil = 0, composing = false;
  let boardRefreshPending=false;
  const MIN_DAY_WIDTH = 24, MAX_DAY_WIDTH = 160;
  let dayWidth = 34;
  try {const saved=Number(localStorage.getItem('member-progress:gantt-day-width'));if(Number.isFinite(saved)&&saved>=MIN_DAY_WIDTH&&saved<=MAX_DAY_WIDTH)dayWidth=saved;} catch {}
  const todayKey = () => dateKey(new Date());
  function dateKey(d) { const a = new Date(d); return `${a.getFullYear()}-${String(a.getMonth()+1).padStart(2,'0')}-${String(a.getDate()).padStart(2,'0')}`; }
  function fromDate(s) { return new Date(`${s}T12:00:00`); }
  function addDays(s, n) { const d=fromDate(s); d.setDate(d.getDate()+n); return dateKey(d); }
  function daysBetween(a,b) { return Math.round((Date.UTC(...a.split('-').map((n,i)=>Number(n)-(i===1?1:0)))-Date.UTC(...b.split('-').map((n,i)=>Number(n)-(i===1?1:0))))/86400000); }
  function localInput(d) { const a = new Date(d); return `${dateKey(a)}T${String(a.getHours()).padStart(2,'0')}:${String(a.getMinutes()).padStart(2,'0')}`; }
  function humanTime(s, full=false) { if(!s) return ''; const d=new Date(s); if(!Number.isFinite(d.getTime())) return ''; return d.toLocaleString('zh-CN', full?{month:'numeric',day:'numeric',hour:'2-digit',minute:'2-digit'}:{hour:'2-digit',minute:'2-digit',hour12:false}); }
  const shortDate = s => s ? `${Number(s.slice(5,7))}/${Number(s.slice(8,10))}` : '';
  const listTags = s => String(s||'').split(/[;；,，、\n]/).map(s=>s.trim()).filter(Boolean);
  const unique = a => [...new Set(a)];
  const scopeKey = (t,id) => `${t}:${id}`;
  function context(t,id) { return (t==='project'?db.projects:[...db.students,...db.archivedStudents||[]]).find(x=>x.id===id); }
  function isArchived(type,id) {return type==='student'&&(db.archivedStudents||[]).some(s=>s.id===id);}
  function taskInScope(t,st,id) { return st==='project'?t.projectId===id:(t.studentIds||[]).includes(id); }
  function scopeTasks(st,id) { return db.tasks.filter(t=>taskInScope(t,st,id)); }
  function scopeMessages(st,id) { return db.messages.filter(m=>m.scopeType===st&&m.scopeId===id).sort((a,b)=>new Date(a.createdAt)-new Date(b.createdAt)); }
  const isOverdue = t => !t.done && t.due && t.due<todayKey();
  const isBlocked = t => !t.done && (t.tags||[]).some(t=>/受阻|阻塞|卡住/.test(t));
  const isSnapshot = r => r && Array.isArray(r.students) && Array.isArray(r.tasks);
  function update(r) { if(isSnapshot(r)) { db={...db,...r,settings:r.settings||{},status:r.status||{}}; if(window.BoardSplit?.dragging)boardRefreshPending=true;else if(!timelineGesture&&!studentGesture&&!composing)render(); } }
  function rpc(action,payload={}) {
    if(!native) return (action.startsWith('assistant.')?window.AssistantChat.previewHandle(action,payload):window.PreviewAdapter.handle(action,payload)).then(result=>{update(result);return result;});
    return new Promise((resolve,reject)=>{
      const id=String(++rpcId);
      const timeout=setTimeout(()=>{pending.delete(id);reject(new Error('操作暂时没有回应，请稍后重试。'));},90000);
      pending.set(id,{resolve:r=>{clearTimeout(timeout);update(r);resolve(r);},reject:e=>{clearTimeout(timeout);reject(e);}});
      window.chrome.webview.postMessage({id,action,payload});
    });
  }
  if(native) window.chrome.webview.addEventListener('message',e=>{
    const r=e.data;
    if(r?.type==='state') update(r.state);
    else if(r?.type==='assistant'||r?.type==='hostassistant') window.AssistantChat?.onState(r.state);
    else if(r?.type==='navigate') { if(r.scopeType==='student'||r.scopeType==='project') openArchive(r.scopeType,r.scopeId); else openAlarms(); }
    else if(r && r.id!=null) {const p=pending.get(String(r.id));if(!p)return;pending.delete(String(r.id));r.error?p.reject(new Error(r.error)):p.resolve(r.result);}
  });
  async function perform(key,fn) {
    if(busy.has(key))return;
    busy.add(key);
    try {await fn();} catch(e) {toast(e.message||'操作未完成，请重试。',true);} finally{busy.delete(key);}
  }
  function toast(message,error=false) {
    document.querySelectorAll('#toast-root .toast:not(.undo-toast)').forEach(node=>node.remove());
    const node=document.createElement('div');node.className=`toast${error?' error':''}`;node.textContent=message;$('#toast-root').append(node);setTimeout(()=>node.remove(),error?7000:3500);
  }
  function undoToast(id,kind='task') {
    document.querySelectorAll('#toast-root .toast').forEach(node=>node.remove());
    const node=document.createElement('div');node.className='toast undo-toast';node.append(document.createTextNode(kind==='message'?'对话已删除':'任务已删除'));
    const button=document.createElement('button');button.textContent='撤销';button.dataset.action=kind==='message'?'restore-message':'restore-task';button.dataset.id=id;node.append(button);$('#toast-root').append(node);setTimeout(()=>node.remove(),9000);
  }
  function empty(iconName,title,body,action,label,compact=false) {return `<div class="empty-state${compact?' compact':''}"><div class="empty-icon">${icon(iconName)}</div><h2>${esc(title)}</h2><p>${esc(body)}</p>${action?`<button class="primary" data-action="${action}">${icon('plus')}${esc(label)}</button>`:''}</div>`;}
  function preserveUI(fn) {
    const active=document.activeElement, focus=active?.dataset.focus, selection=active?.selectionStart, selectionEnd=active?.selectionEnd;
    const board=$('.gantt-scroll'), scrollLeft=board?.scrollLeft, scrollTop=board?.scrollTop, pageTop=$('.page-content')?.scrollTop;
    const chat=$('.chat-messages'), chatTop=chat?.scrollTop, chatBottom=chat?chat.scrollHeight-chat.scrollTop-chat.clientHeight<65:true;
    const modalTop=$('.modal-body')?.scrollTop,profileTop=$('.drawer-profile')?.scrollTop;
    repainting=true;try{fn();}finally{repainting=false;}
    fitBoard();
    const b=$('.gantt-scroll');if(b&&scrollLeft!=null){b.scrollLeft=scrollLeft;b.scrollTop=scrollTop;}else if(b){b.scrollLeft=Math.max(0,(timelineRange().today-2)*dayWidth);}syncBoardRows();
    if($('.page-content')&&pageTop!=null) $('.page-content').scrollTop=pageTop;
    if($('.modal-body')&&modalTop!=null)$('.modal-body').scrollTop=modalTop;
    if($('.drawer-profile')&&profileTop!=null)$('.drawer-profile').scrollTop=profileTop;
    const c=$('.chat-messages');if(c)c.scrollTop=chatBottom?c.scrollHeight:(chatTop||0);
    if(focus){const target=document.querySelector(`[data-focus="${CSS.escape(focus)}"]`);if(target){target.focus({preventScroll:true});if(typeof selection==='number'&&target.setSelectionRange)try{target.setSelectionRange(selection,selectionEnd??selection);}catch{}}}
  }
  function render() { preserveUI(()=>{renderSidebar();renderMain();renderDrawer();renderModal();}); }
  function fitBoard() {
    const board=$('.board'),gantt=$('.gantt-scroll');if(!board)return;
    window.BoardSplit.fit(board);
    board.style.setProperty('--day',`${dayWidth}px`);board.classList.toggle('wide-days',dayWidth>=80);
    if(gantt)board.style.setProperty('--gantt-scrollbar',`${Math.max(0,gantt.offsetHeight-gantt.clientHeight)}px`);
    alignMemberColumns(board);
  }
  function alignMemberColumns(board) {
    const heading=board.querySelector('.student-heading');if(!heading)return;
    const available=heading.clientWidth,measure=document.createElement('canvas').getContext('2d');
    function textWidth(node,text) {const style=getComputedStyle(node);measure.font=`${style.fontWeight} ${style.fontSize} ${style.fontFamily}`;return measure.measureText(text).width;}
    let name=0,grade=20,meta=18;
    board.querySelectorAll('.student-heading').forEach(row=>{
      const n=row.querySelector('.student-name'),g=row.querySelector('.inline-grade .field-display'),tags=row.querySelectorAll('.inline-tags .tag'),more=row.querySelector('.meta-more');
      name=Math.max(name,textWidth(n,n.textContent)+2);
      if(g)grade=Math.max(grade,textWidth(g,g.textContent)+6);
      meta=Math.max(meta,Array.from(tags).reduce((sum,t)=>sum+Math.min(65,textWidth(t,t.textContent)+8)+4,0)+(more?textWidth(more,more.textContent)+4:0)+21);
    });
    name=Math.ceil(Math.min(name,110));grade=Math.ceil(Math.min(grade,65));meta=Math.ceil(Math.min(meta,126));
    // The progress paragraph gives up space before identity columns do.
    let reserved=0;
    board.querySelectorAll('.student-heading').forEach(row=>{
      const extras=[...row.children].filter(n=>!n.matches('.student-name,.inline-grade,.inline-tags,.inline-currentSummary'));
      const gap=parseFloat(getComputedStyle(row).gap)||0;
      reserved=Math.max(reserved,extras.reduce((sum,n)=>sum+n.getBoundingClientRect().width,0)+gap*(row.children.length-1));
    });
    let deficit=Math.max(0,name+grade+meta+reserved-available);
    let cut=Math.min(deficit,Math.max(0,meta-22));meta-=cut;deficit-=cut;
    cut=Math.min(deficit,Math.max(0,name-42));name-=cut;deficit-=cut;
    grade-=Math.min(deficit,Math.max(0,grade-20));
    board.style.setProperty('--member-name',`${name}px`);board.style.setProperty('--member-grade',`${grade}px`);board.style.setProperty('--member-meta',`${meta}px`);
  }
  function zoomTimeline(e,gantt) {
    e.preventDefault();if(timelineGesture||studentGesture||window.BoardSplit.dragging)return;
    const delta=e.deltaY*(e.deltaMode===1?20:e.deltaMode===2?gantt.clientWidth:1);
    const next=Math.max(MIN_DAY_WIDTH,Math.min(MAX_DAY_WIDTH,Math.round(dayWidth*Math.exp(-Math.max(-240,Math.min(240,delta))*.0025)*10)/10));
    if(next===dayWidth)return;
    const x=Math.max(0,Math.min(gantt.clientWidth,e.clientX-gantt.getBoundingClientRect().left)),anchor=(gantt.scrollLeft+x)/dayWidth;
    dayWidth=next;const board=gantt.closest('.board');board.style.setProperty('--day',`${dayWidth}px`);board.classList.toggle('wide-days',dayWidth>=80);
    gantt.scrollLeft=anchor*dayWidth-x;
    try {localStorage.setItem('member-progress:gantt-day-width',String(dayWidth));} catch {}
  }
  function syncBoardRows() {const list=$('.student-list'),gantt=$('.gantt-scroll');if(list&&gantt)list.scrollTop=gantt.scrollTop;}
  document.addEventListener('scroll',e=>{if(e.target.matches?.('.gantt-scroll'))syncBoardRows();},true);
  document.addEventListener('wheel',e=>{if(e.ctrlKey){const gantt=e.target.closest('.gantt-scroll');if(gantt)zoomTimeline(e,gantt);return;}const strip=e.target.closest('.student-project-strip');if(strip&&strip.scrollWidth>strip.clientWidth){e.preventDefault();strip.scrollLeft+=(Math.abs(e.deltaX)>Math.abs(e.deltaY)?e.deltaX:e.deltaY)*(e.deltaMode===1?20:e.deltaMode===2?strip.clientWidth:1);return;}if(!e.target.closest('.student-fixed'))return;const gantt=$('.gantt-scroll');if(gantt&&gantt.scrollHeight>gantt.clientHeight){e.preventDefault();gantt.scrollTop+=e.deltaY*(e.deltaMode===1?20:e.deltaMode===2?gantt.clientHeight:1);syncBoardRows();}},{passive:false});
  document.addEventListener('boardratiochange',fitBoard);
  document.addEventListener('boardresized',()=>{if(boardRefreshPending){boardRefreshPending=false;render();}});
  new ResizeObserver(fitBoard).observe($('#main'));
  function renderSidebar() {
    $('#sidebar').innerHTML=`<div class="brand"><span class="brand-mark"><img src="logo.png" alt="" width="31" height="31"></span><span class="brand-label">成员进度</span></div><nav class="navigation">${[['students','users','成员'],['projects','folder','项目'],['archive','archive','归档'],['assistant','spark','GPT 聊天']].map(([v,i,l])=>`<button class="nav-button${view===v?' active':''}" data-action="view" data-view="${v}" ${view===v?'aria-current="page"':''} title="${l}">${icon(i)}<span class="nav-label">${l}</span></button>`).join('')}</nav><div class="sidebar-footer"><button class="nav-button" data-action="settings" title="设置">${icon('settings')}<span class="nav-label">设置</span></button><div class="local-status"><span class="status-dot"></span>资料保存在本机</div>${native?'':'<div class="preview-note">浏览器预览 · 刷新即重置</div>'}</div>`;
  }
  function header(title,subtitle,count,extra='') {
    return `<header class="page-header"><div><div class="heading-line"><h1>${esc(title)}</h1>${count==null?'':`<span class="count">${count}</span>`}</div></div><div class="header-actions"><div class="search-wrap">${icon('search')}<input type="search" data-focus="search" data-input="search" placeholder="${view==='archive'?'搜索记录与摘要':'搜索姓名、标签或任务'}" value="${attr(search)}" aria-label="搜索"></div><button data-action="alarms" title="全局、成员与项目提醒">${icon('bell')}提醒</button>${extra}</div></header>`;
  }
  function renderMain() {
    if(view==='students') renderStudents();
    else if(view==='projects') renderProjects();
    else if(view==='assistant') window.AssistantChat.mount($('#main'));
    else renderArchives();
  }
  function showView(next) {view=next;search='';inlineAdd=null;renderSidebar();renderMain();fitBoard();const board=$('.gantt-scroll');if(board)board.scrollLeft=Math.max(0,(timelineRange().today-2)*dayWidth);}
  function studentMatches(s) {const q=search.trim().toLowerCase();return !q||[s.name,s.grade,s.bio,s.currentSummary,...s.tags||[],...scopeTasks('student',s.id).map(t=>t.title)].join(' ').toLowerCase().includes(q);}
  function editable(s,field,location,options={}) {
    const value=field==='tags'?(s.tags||[]).join('; '):String(s[field]||''),display=options.display??value;
    const key=`${s.id}:${field}:${location}`,editing=inlineEdit?.key===key;
    const label={name:'姓名',grade:'年级',tags:'meta 标签',bio:'当前研究主题',currentSummary:'当前进展'}[field]||field,empty=!display;
    const hint=field==='grade'?`${icon('plus')}<span>年级</span>`:field==='tags'?'':icon('edit');
    const content=field==='tags'&&s.tags?.length?s.tags.slice(0,location==='main'?2:s.tags.length).map(t=>`<span class="tag">${esc(t)}</span>`).join('')+(location==='main'&&s.tags.length>2?`<span class="meta-more">+${s.tags.length-2}</span>`:''):empty?`<span class="empty-field-hint" aria-hidden="true">${hint}</span>`:esc(display);
    if(isArchived('student',s.id))return `<span class="${options.className||''}">${empty?'':content}</span>`;
    if(!editing)return `<span class="inline-field inline-${field}${empty?' field-empty':''}${empty&&location==='main'?' main-empty':''}" data-location="${location}"><button class="field-display ${options.className||''}${empty?' empty':''}" data-action="edit-inline" data-id="${attr(s.id)}" data-field="${field}" data-location="${location}" data-multiline="${!!options.multiline}" title="${attr(display||`填写${label}`)} · 点击编辑${field==='grade'?'；每年9月自动升一级':''}" aria-label="${empty?'填写':'编辑'}${attr(label)}" ${empty&&field==='tags'?'tabindex="-1"':''}>${content}</button>${field==='tags'?`<button class="meta-add" data-action="append-meta" data-id="${attr(s.id)}" data-location="${location}" title="添加一个 meta 标签" aria-label="为${attr(s.name)}添加标签">${icon('plus')}</button>`:''}</span>`;
    const e=inlineEdit,control=options.multiline?`<textarea data-inline-value data-focus="inline-${attr(key)}" rows="${location==='main'?2:3}" aria-label="编辑${attr(label)}">${esc(e.value)}</textarea>`:`<input data-inline-value data-focus="inline-${attr(key)}" value="${attr(e.value)}" aria-label="编辑${attr(label)}">`;
    return `<span class="inline-field inline-${field} is-editing" data-location="${location}"><span class="inline-edit${e.error?' has-error':''}" data-inline-key="${attr(key)}">${control}<span class="inline-edit-actions">${field==='tags'?`<button type="button" data-action="inline-separator" title="继续添加一个标签">${icon('plus')}</button>`:''}<button type="button" data-action="save-inline" title="保存（Enter）" ${e.saving?'disabled':''}>${icon(e.saving?'clock':'check')}</button><button type="button" data-action="cancel-inline" title="取消（Esc）" ${e.saving?'disabled':''}>${icon('close')}</button></span>${e.error?`<span class="inline-edit-error" role="alert">${esc(e.error)}</span>`:''}${field==='tags'?`<span class="inline-recent-labels">${recentLabels('recentStudentLabels').map(label=>`<button type="button" data-action="inline-label" data-label="${attr(label)}">${esc(label)}</button>`).join('')}</span>`:''}</span></span>`;
  }
  async function beginInlineEdit(id,field,location,multiline,append=false) {
    if(inlineEdit&&!await saveInlineEdit())return;
    const s=context('student',id);if(!s||isArchived('student',id))return;
    const value=field==='tags'?(s.tags||[]).join('; '):String(s[field]||'');
    inlineEdit={id,field,location,multiline,key:`${id}:${field}:${location}`,value:append&&value?`${value}; `:value,original:value,error:'',saving:false};
    render();const input=$('[data-inline-value]');if(input){input.focus({preventScroll:true});input.setSelectionRange(input.value.length,input.value.length);}
  }
  function saveInlineEdit() {
    const edit=inlineEdit;if(!edit)return Promise.resolve(true);if(edit.promise)return edit.promise;
    if(edit.field==='tags'?JSON.stringify(unique(listTags(edit.value)))===JSON.stringify(unique(listTags(edit.original))):edit.value.trim()===edit.original.trim()){inlineEdit=null;if(!timelineGesture&&!studentGesture)render();return Promise.resolve(true);}
    if(edit.field==='name'&&!edit.value.trim()){edit.error='姓名不能为空';if(!timelineGesture&&!studentGesture)render();return Promise.resolve(false);}
    edit.saving=true;edit.error='';
    edit.promise=(async()=>{
      try{await rpc('student.patch',{id:edit.id,field:edit.field,value:edit.field==='tags'?unique(listTags(edit.value)):edit.value.trim()});if(inlineEdit===edit)inlineEdit=null;return true;}
      catch(error){edit.error=error.message||'保存失败，请重试';return false;}
      finally{edit.saving=false;edit.promise=null;if(window.BoardSplit?.dragging)boardRefreshPending=true;else if(!timelineGesture&&!studentGesture&&!composing)render();}
    })();
    return edit.promise;
  }
  function timelineRange() {
    let start=addDays(todayKey(),-7),end=addDays(todayKey(),35);
    db.tasks.forEach(t=>{[t.start,t.due].filter(Boolean).forEach(d=>{if(d<start)start=d;if(d>end)end=d;});});
    db.alarms.forEach(a=>{[a.enabled?a.when:null,a.lastTriggeredWhen].filter(Boolean).forEach(when=>{if(!Number.isFinite(new Date(when).getTime()))return;const d=dateKey(when);if(d<start)start=d;if(d>end)end=d;});});
    const days=Math.max(1,Math.min(7300,daysBetween(end,start)+1));return {start,days,today:daysBetween(todayKey(),start)};
  }
  function studentLayout(s,range) {
    const tasks=scopeTasks('student',s.id),packed=window.GanttLayout.packTasks(tasks,range);
    const reminders=window.GanttLayout.reminderDays(db.alarms,db.projects,s.id,range);
    const reminderHeight=reminders.reduce((height,day)=>Math.max(height,day.alarms.length*15),0);
    return {...packed,reminders,reminderHeight,height:Math.max(60,packed.laneCount*25+reminderHeight+9)};
  }
  function alarmScopeLabel(a) {return a.scopeType==='global'?'全局':a.scopeType==='project'?`项目 · ${context('project',a.projectId)?.name||'项目'}`:context('student',a.studentId)?.name||'成员';}
  function reminderDescription(entries) {return entries.map(({alarm,when,fired})=>`${humanTime(when)} · ${alarmScopeLabel(alarm)} · ${alarm.title}${fired?' · 已提醒':''}`).join('\n');}
  function alarmsForStudent(id) {
    const projects=new Set(db.projects.filter(p=>(p.studentIds||[]).includes(id)).map(p=>p.id));
    return db.alarms.filter(a=>a.scopeType==='global'||(a.scopeType==='student'&&a.studentId===id)||(a.scopeType==='project'&&projects.has(a.projectId))).sort((a,b)=>(Number(!!b.enabled)-Number(!!a.enabled))||(new Date(a.when)-new Date(b.when))||String(a.id).localeCompare(String(b.id)));
  }
  function allReminderDescription(alarms) {return alarms.map(a=>`${humanTime(a.when,true)} · ${alarmScopeLabel(a)} · ${a.title}${a.enabled?'':a.lastTriggeredWhen?' · 已提醒':' · 已暂停'}`).join('\n');}
  function reminderChip(s,alarms) {
    alarms=window.ReminderDisplay.rowAlarms(alarms);
    if(!alarms.length)return '';
    const next=alarms.find(a=>a.enabled&&Number.isFinite(new Date(a.when).getTime()));
    const when=next?`${dateKey(next.when)===todayKey()?'':shortDate(dateKey(next.when))+' '}${humanTime(next.when)}`:'';
    return `<button class="member-reminders${next?'':' inactive'}${alarms.some(a=>window.ReminderDisplay.isTodayAlarm(a))?' reminder-today':''}" data-action="student-alarm" data-id="${attr(s.id)}" title="${attr(allReminderDescription(alarms))}" aria-label="${attr(s.name)}的全部 ${alarms.length} 条相关提醒"><span>提醒 ${alarms.length}</span>${when?`<time>${esc(when)}</time>`:''}</button>`;
  }
  function renderStudents() {
    const students=db.students.filter(studentMatches),range=timelineRange(),layouts=new Map(students.map(s=>[s.id,studentLayout(s,range)]));
    const dates=Array.from({length:range.days},(_,i)=>{const d=addDays(range.start,i),n=fromDate(d);return `<button class="date-cell${d===todayKey()?' today':''}${[0,6].includes(n.getDay())?' weekend':''}" data-action="date-alarm" data-date="${d}" title="${d} · 点击添加全局提醒"><strong>${n.getDate()===1?`${n.getMonth()+1}/1`:n.getDate()}</strong>${['日','一','二','三','四','五','六'][n.getDay()]}</button>`;}).join('');
    $('#main').innerHTML=header('成员','',db.students.length,`<button class="primary" data-action="new-student">${icon('plus')}成员</button>`)+`<div class="page-content students-page">${db.students.length===0?empty('users','从第一位成员开始','只需一个姓名。','new-student','添加成员'):students.length===0?'<div class="no-results">没有找到匹配的成员。</div>':`<div class="board-controls"><span>${fromDate(todayKey()).toLocaleDateString('zh-CN',{year:'numeric',month:'long',day:'numeric',weekday:'long'})}</span><div class="board-controls-right"><span class="today-dot">今天</span><button class="small ghost" data-action="today">回到今天</button></div></div><div class="board" style="--days:${range.days};--today:${range.today};--day:${dayWidth}px"><div class="student-fixed"><div class="board-header-label" title="拖动左侧圆点排序；点击姓名打开归档；其他文字点击编辑">成员 / 当前进展</div><div class="student-list">${students.map(s=>studentRow(s,layouts.get(s.id))).join('')}</div></div><div class="gantt-scroll" aria-label="任务日期时间线" title="Ctrl + 鼠标滚轮调整日期宽度"><div class="gantt-inner"><div class="date-header">${dates}</div>${students.map(s=>timelineRow(s,range,layouts.get(s.id))).join('')}</div></div></div>`}</div>`;
  }
  function studentRow(s,layout) {
    const tasks=scopeTasks('student',s.id),active=tasks.filter(t=>!t.done),undated=tasks.filter(t=>!t.start&&!t.due);
    const alarms=alarmsForStudent(s.id);
    const overdue=active.filter(isOverdue),blocked=active.filter(isBlocked);
    const warnings=[...overdue.slice(0,1).map(t=>`<button class="tag red" data-action="edit-task" data-id="${attr(t.id)}" title="${attr(overdue.map(x=>x.title).join('；'))}">逾期 ${daysBetween(todayKey(),t.due)} 天${overdue.length>1?` · ${overdue.length}项`:''}</button>`),...blocked.slice(0,1).map(t=>`<button class="tag amber" data-action="edit-task" data-id="${attr(t.id)}" title="${attr(blocked.map(x=>x.title).join('；'))}">受阻${blocked.length>1?` · ${blocked.length}项`:''}</button>`)];
    const projects=db.projects.filter(p=>(p.studentIds||[]).includes(s.id));
    const footer=projects.length||warnings.length||undated.length?`<div class="student-row-footer">${projects.length?`<div class="student-project-strip" title="所属项目 · 可横向滚动查看全部" aria-label="${attr(s.name)}参与的项目">${projects.map(p=>`<button class="student-project-chip" data-project-id="${attr(p.id)}" data-action="open-student-project" data-id="${attr(p.id)}" title="${attr(p.name)} · 打开项目">${icon('folder')}<span>${esc(p.name)}</span></button>`).join('')}</div>`:''}${warnings.length?`<div class="inline-warnings">${warnings.join('')}</div>`:''}${undated.length?`<button class="task-count" data-action="student-tasks" data-id="${attr(s.id)}" title="查看还未安排日期的任务">${undated.length} 项待排期</button>`:''}</div>`:'';
    const summary=s.currentSummary||(active.length?active.map(t=>t.title).join('；'):'');
    return `<section class="student-row${inlineEdit?.id===s.id&&inlineEdit.location==='main'?' editing':''}" data-student-id="${attr(s.id)}" style="--row-height:${layout.height}px"><button class="student-drag-handle" data-focus="drag-student-${attr(s.id)}" data-action="reorder-student" data-id="${attr(s.id)}" title="拖动排序；也可按上下方向键调整" aria-label="拖动调整${attr(s.name)}的顺序">${icon('grip')}</button><div class="student-info"><div class="student-heading"><button class="student-name" data-action="archive" data-scope-type="student" data-scope-id="${attr(s.id)}" title="归档、任务和成员信息">${esc(s.name)}</button>${editable(s,'grade','main',{className:'grade',placeholder:'+年级'})}${editable(s,'tags','main',{className:'student-meta',placeholder:'标签'})}${editable(s,'currentSummary','main',{className:'current-summary',display:summary,multiline:true,placeholder:'当前进展'})}${['pending','running'].includes(s.summaryStatus)?'<span class="summary-pill" title="GPT 正在整理摘要"><span class="spinner"></span></span>':s.summaryStatus==='error'?`<button class="summary-error" data-action="archive" data-scope-type="student" data-scope-id="${attr(s.id)}" title="摘要未生成，点击查看">摘要待重试</button>`:''}${reminderChip(s,alarms)}</div>${footer}</div></section>`;
  }
  function timelineRow(s,range,layout) {
    const byDate=new Map(layout.reminders.map(d=>[d.date,d]));
    const dates=Array.from({length:range.days},(_,i)=>{const date=addDays(range.start,i),day=byDate.get(date),description=day?reminderDescription(day.alarms):'';return `<button class="timeline-day${day?' has-reminder':''}${day?.alarms.every(x=>x.scopeType==='global')?' global-reminder':''}${day?.alarms.some(x=>x.scopeType==='project')?' project-reminder':''}" style="--offset:${i}" data-action="timeline-date" data-date="${date}" data-student-id="${attr(s.id)}" aria-label="${attr(s.name)} ${date}${day?' '+attr(description):' 添加提醒'}" title="${date}${day?'\n'+attr(description):''}\n点击添加提醒，拖动创建任务"></button>`;}).join('');
    const markers=layout.reminders.map(day=>day.alarms.map((entry,rowIndex)=>`<button class="timeline-reminder reminder-${attr(entry.scopeType)}${entry.fired?' reminder-fired':''}${window.ReminderDisplay.isToday(entry.when)?' reminder-today':''}" style="--offset:${day.offset};--reminder-row:${rowIndex}" data-action="edit-alarm" data-id="${attr(entry.alarm.id)}" data-alarm-id="${attr(entry.alarm.id)}" data-student-id="${attr(s.id)}" data-date="${day.date}" data-when="${attr(entry.when)}" data-row-index="${rowIndex}" title="${attr(reminderDescription([entry]))}\n点击修改提醒" aria-label="${day.date} ${attr(reminderDescription([entry]))}"><span>${humanTime(entry.when)}</span></button>`).join('')).join('');
    return `<div class="timeline-row" data-student-id="${attr(s.id)}" style="--row-height:${layout.height}px;--reminder-height:${layout.reminderHeight}px"><div class="timeline${layout.reminders.length?' with-reminders':''}" data-student-id="${attr(s.id)}" data-range-start="${range.start}" data-range-days="${range.days}">${dates}<span class="today-line"></span>${markers}${layout.lanes.map((lane,i)=>`<div class="task-lane" data-lane="${i}">${lane.map(item=>timelineTask(item.task,s.id,item)).join('')}</div>`).join('')}</div></div>`;
  }
  function timelineTask(t,studentId,{offset,span}) {
    const classes=[t.done?'done':'',isOverdue(t)?'overdue':'',isBlocked(t)?'blocked':'',span===1?'single-day':''].join(' ');
    return `<div class="task-bar ${classes}" data-task-id="${attr(t.id)}" style="--offset:${offset};--span:${span}" title="${attr(t.title)} · ${attr(t.start||t.due)}${t.start&&t.due?` — ${attr(t.due)}`:''} · 拖动改期，拖两端调整日期"><span class="task-resize start" data-resize="start" title="拖动调整开始日期"></span><button class="task-check${t.done?' checked':''}" data-action="toggle-task" data-id="${attr(t.id)}" aria-label="${t.done?'恢复任务':'完成任务'}：${attr(t.title)}">${t.done?icon('check'):''}</button><button class="task-title" data-action="edit-task" data-id="${attr(t.id)}">${esc(t.title)}</button>${!t.done?`<button class="task-archive" data-action="archive-task" data-id="${attr(t.id)}" data-scope-type="student" data-scope-id="${attr(studentId)}" title="提交归档并完成">${icon('archive')}</button>`:''}<span class="task-resize end" data-resize="due" title="拖动调整截止日期"></span></div>`;
  }
  function taskAdd(type,id,compact=false) {
    return `<form class="${compact?'inline-task-add':'wide-task-add'}" data-form="task-add" data-scope-type="${type}" data-scope-id="${attr(id)}"><input name="title" data-focus="task-add-${attr(scopeKey(type,id))}" placeholder="写下任务，Enter 添加" value="${attr(taskDrafts.get(scopeKey(type,id))||'')}" aria-label="新任务" maxlength="500" autocomplete="off" required><button${compact?'':' class="small"'} type="submit" title="添加任务">${icon('plus')}${compact?'':'添加'}</button></form>`;
  }
  function simpleTasks(type,id) {
    if(isArchived(type,id))return '<p class="quiet-note">已删除成员的任务不再显示。</p>';
    const tasks=scopeTasks(type,id),ordered=[...tasks.filter(t=>!t.done),...tasks.filter(t=>t.done)];
    return `<div class="simple-task-list">${ordered.length?ordered.map(t=>`<div class="simple-task${t.done?' done':''}"><button class="task-check${t.done?' checked':''}" data-action="toggle-task" data-id="${attr(t.id)}" aria-label="${t.done?'恢复':'完成'}：${attr(t.title)}">${t.done?icon('check'):''}</button><button class="task-title" data-action="edit-task" data-id="${attr(t.id)}">${esc(t.title)}</button><div class="task-meta">${isOverdue(t)?'<span class="tag red">逾期</span>':''}${(t.tags||[]).map(s=>`<span class="tag${!t.done&&/受阻/.test(s)?' amber':''}">${esc(s)}</span>`).join('')}${t.due?`<span class="task-date-label">${shortDate(t.due)}</span>`:''}${!t.done?`<button class="task-archive" data-action="archive-task" data-id="${attr(t.id)}" data-scope-type="${type}" data-scope-id="${attr(id)}" title="归档后完成任务">归档</button>`:''}<button class="icon-button task-delete" data-action="delete-task" data-id="${attr(t.id)}" title="删除任务，可撤销">${icon('trash')}</button></div></div>`).join(''):''}</div>${taskAdd(type,id)}`;
  }
  function renderProjects() {
    const q=search.trim().toLowerCase(),projects=db.projects.filter(p=>[p.name,p.currentSummary,...scopeTasks('project',p.id).map(t=>t.title),...(p.studentIds||[]).map(id=>context('student',id)?.name||'')].join(' ').toLowerCase().includes(q));
    if(!projects.some(p=>p.id===selectedProject))selectedProject=projects[0]?.id||null;
    const p=context('project',selectedProject);
    $('#main').innerHTML=header('项目','几位成员，一起推进一件事。',db.projects.length,`<button class="primary" data-action="new-project">${icon('plus')}项目</button>`)+`<div class="page-content">${db.projects.length===0?empty('folder','把相关成员放进一个项目','取个名字、选好成员，任务和归档就能共享。','new-project','新建项目'):!projects.length?'<div class="no-results">没有匹配的项目。</div>':`<div class="project-layout"><div class="project-list">${projects.map(p=>`<button class="project-item${selectedProject===p.id?' selected':''}" data-action="select-project" data-id="${attr(p.id)}"><span class="project-icon">${icon('folder')}</span><span class="project-item-text"><strong>${esc(p.name)}</strong><span class="project-member-count">${(p.studentIds||[]).length} 位成员</span></span></button>`).join('')}</div><section class="project-detail"><div class="project-title-row"><h2>${esc(p.name)}</h2><div class="header-actions"><button class="small" data-action="project-alarm" data-id="${attr(p.id)}" title="提醒项目成员">${icon('bell')}提醒</button><button class="icon-button" data-action="edit-project" data-id="${attr(p.id)}" title="修改项目">${icon('edit')}</button><button class="primary small" data-action="archive" data-scope-type="project" data-scope-id="${attr(p.id)}">${icon('archive')}归档</button></div></div><div class="project-members">${(p.studentIds||[]).map(id=>context('student',id)).filter(Boolean).map(s=>`<button class="tag" data-action="student-profile" data-id="${attr(s.id)}">${esc(s.name)}</button>`).join('')||'<span class="quiet-note">尚未选择成员</span>'}</div>${p.currentSummary?`<div class="project-summary">${esc(p.currentSummary)}</div>`:''}<div class="section-heading"><h3>共同任务</h3></div>${simpleTasks('project',p.id)}</section></div>`}</div>`;
  }
  function renderArchives() {
    const q=search.trim().toLowerCase();
    const scopes=[...[...db.students,...db.archivedStudents||[]].map(s=>({...s,type:'student',archived:isArchived('student',s.id)})),...db.projects.map(p=>({...p,type:'project'}))].map(s=>({...s,messages:scopeMessages(s.type,s.id)})).filter(s=>s.messages.length).filter(s=>archiveFilter==='all'||s.type===archiveFilter).filter(s=>!q||[s.name,s.currentSummary,...s.messages.map(m=>m.text),...s.messages.flatMap(m=>(m.attachments||[]).map(a=>a.name))].join(' ').toLowerCase().includes(q)).sort((a,b)=>new Date(b.messages.at(-1).createdAt)-new Date(a.messages.at(-1).createdAt));
    $('#main').innerHTML=header('归档','文字、文件和自动摘要，沿时间保留。',null)+`<div class="page-content"><div class="archive-filter">${[['all','全部'],['student','成员'],['project','项目']].map(([v,l])=>`<button class="${archiveFilter===v?'active':''}" data-action="archive-filter" data-filter="${v}">${l}</button>`).join('')}</div>${!scopes.length?empty('archive',q?'没有找到匹配的记录':'随手记录，自动归档',q?'试试成员姓名、项目名或记录中的关键词。':'点击成员姓名，发送文字或文件。'): `<div class="archive-list">${scopes.map(s=>{const m=s.messages.at(-1);return `<button class="archive-card" data-action="archive" data-scope-type="${s.type}" data-scope-id="${attr(s.id)}"><span class="avatar">${s.type==='project'?icon('folder'):esc(s.name.slice(-1))}</span><span class="archive-card-body"><span class="archive-card-heading"><strong>${esc(s.name)} ${s.archived?'<span class="tag">已删除</span>':''}<span class="grade">${s.type==='project'?'项目':esc(s.grade)}</span></strong><time>${humanTime(m.createdAt,true)}</time></span><p>${esc(s.currentSummary||m.text||(m.attachments||[]).map(a=>a.name).join('、'))}</p></span>${icon('arrow')}</button>`;}).join('')}</div>`}</div>`;
  }
  function draft() {if(!drawer)return {text:'',files:[]};const k=scopeKey(drawer.type,drawer.id);if(!drafts.has(k))drafts.set(k,{text:'',files:[]});return drafts.get(k);}
  function openArchive(type,id,taskId=null) {
    if(!context(type,id))return;
    drawer={type,id,tab:'archive',taskId};renderDrawer();setTimeout(()=>{const c=$('.chat-messages');if(c)c.scrollTop=c.scrollHeight;$('#chat-input')?.focus();},0);
  }
  function renderDrawer() {
    if(!drawer){$('#drawer-root').innerHTML='';return;}
    const s=context(drawer.type,drawer.id);if(!s){drawer=null;$('#drawer-root').innerHTML='';return;}
    const d=draft(),t=db.tasks.find(t=>t.id===drawer.taskId),sending=busy.has('chat-send'),archived=isArchived(drawer.type,drawer.id);
    if(archived&&drawer.tab==='tasks')drawer.tab='archive';
    const head=`<div class="drawer-header"><div class="drawer-title-row"><div class="drawer-person"><div class="avatar">${drawer.type==='project'?icon('folder'):esc(s.name.slice(-1))}</div><div><h2>${drawer.type==='student'?editable(s,'name','drawer-head',{className:'profile-name'}):esc(s.name)}${archived?' <span class="tag">已删除</span>':''}</h2>${drawer.type==='project'?'<span class="grade">项目归档</span>':editable(s,'grade','drawer-head',{className:'grade',placeholder:'+年级'})}</div></div><div class="drawer-header-actions">${drawer.tab==='archive'?`<button class="small reference-archive" data-action="reference-archive" title="将整个归档作为GPT聊天的引用；不会自动发送">${icon('spark')}引用整个归档</button>`:''}${!archived?`<button class="icon-button" data-action="${drawer.type==='project'?'project-alarm':'student-alarm'}" data-id="${attr(s.id)}" title="${drawer.type==='project'?'项目':'成员'}提醒">${icon('bell')}</button>`:''}<button class="icon-button" data-action="close-drawer" title="关闭">${icon('close')}</button></div></div><div class="drawer-tabs"><button class="${drawer.tab==='archive'?'active':''}" data-action="drawer-tab" data-tab="archive">归档</button>${archived?'':`<button class="${drawer.tab==='tasks'?'active':''}" data-action="drawer-tab" data-tab="tasks">任务</button>`}${drawer.type==='student'?`<button class="${drawer.tab==='profile'?'active':''}" data-action="drawer-tab" data-tab="profile">成员信息</button>`:''}</div></div>`;
    let content;
    if(drawer.tab==='profile')content=`<div class="drawer-profile"><div class="profile-field"><label>meta 标签</label>${editable(s,'tags','profile',{className:'student-meta',placeholder:'添加标签'})}</div><div class="profile-field"><label>bio · 当前研究主题</label>${editable(s,'bio','profile',{className:'profile-paragraph',placeholder:'填写当前研究主题',multiline:true})}</div><div class="profile-field"><label>当前进展</label>${editable(s,'currentSummary','profile',{className:'profile-paragraph',placeholder:'填写当前进展',multiline:true})}</div>${archived?'':`<div class="profile-buttons"><button class="small danger" data-action="delete-student" data-id="${attr(s.id)}">${icon('trash')}删除成员</button></div>`}</div>`;
    else if(drawer.tab==='tasks')content=`<div class="drawer-profile">${simpleTasks(drawer.type,drawer.id)}</div>`;
    else {
      const messages=scopeMessages(drawer.type,drawer.id);let lastDate='';
      const bubbles=messages.map(m=>{const date=dateKey(m.createdAt),sep=date!==lastDate?`<div class="chat-date">${fromDate(date).toLocaleDateString('zh-CN',{month:'long',day:'numeric',weekday:'long'})}</div>`:'';lastDate=date;return sep+messageBubble(m);}).join('');
      const status=s.summaryStatus;
      const statusHtml=archived?'':['pending','running'].includes(status)?`<div class="summary-state"><span class="spinner"></span>${status==='pending'?'稍后自动整理本次进展…':'GPT 正在整理摘要…'}</div>`:status==='error'?`<div class="summary-state error"><span>${esc(s.summaryError||'摘要未生成，原始记录已保存。')}</span><button class="text-button" data-action="retry-summary">重试</button></div>`:!db.settings.aiConfigured?'<div class="summary-state"><span>记录保存在本机。连接 GPT 后，发送内容会自动生成摘要。</span><button class="text-button" data-action="settings">连接 GPT</button></div>':'';
      content=`<div class="chat-messages">${messages.length?bubbles:`<div class="chat-intro"><div class="intro-icon">${icon('chat')}</div><strong>像发消息一样记录进展</strong>发一段文字，或拖入文件。<br>GPT 自动整理摘要，并提取下一步任务。</div>`}${statusHtml}</div>${archived?'<div class="archived-notice">成员已删除 · 历史对话仍可整理</div>':`<div class="composer">${t&&!t.done?`<div class="task-context">${icon('archive')}<strong>${esc(t.title)}</strong><span>发送后完成</span><button class="icon-button" data-action="clear-task-context" title="仅归档，不完成任务">${icon('close')}</button></div>`:''}<div class="composer-top"><button data-action="pick-files">${icon('clip')}添加文件</button><span>${uploading?'正在读取附件…':'支持拖入文件 / 粘贴图片'}</span></div><input class="file-input" id="chat-files" type="file" multiple><div class="composer-files">${d.files.map((f,i)=>`<div class="draft-file">${icon('file')}<span>${esc(f.name)}</span><button data-action="remove-file" data-index="${i}" title="移除附件">${icon('close')}</button></div>`).join('')}</div><textarea id="chat-input" data-focus="chat-input" data-input="chat" placeholder="记下进展、反馈或下一步…" rows="2" aria-label="归档消息" ${sending?'disabled':''}>${esc(d.text)}</textarea><div class="composer-footer"><span class="composer-hint">Enter 发送 · Shift + Enter 换行</span><button class="primary" data-action="send-chat" ${sending||uploading||(!d.text.trim()&&!d.files.length)?'disabled':''}>${icon('send')}${sending?'保存中…':t&&!t.done?'提交并完成':'发送归档'}</button></div></div>`}`;
    }
    $('#drawer-root').innerHTML=`<div class="drawer-backdrop" data-action="close-drawer"></div><aside class="drawer" role="dialog" aria-modal="true" aria-label="${attr(s.name)}归档">${head}${content}${dragDepth>0?'<div class="drop-overlay">松开鼠标，添加到这条记录</div>':''}</aside>`;
  }
  function fileStatus(a) {return '原文件已归档';}
  function fileSize(n) {return n>=1048576?`${(n/1048576).toFixed(1)} MB`:n>=1024?`${Math.ceil(n/1024)} KB`:`${n||0} B`;}
  function messageBubble(m) {
    const user=m.role==='user', suggestions=isArchived(drawer.type,drawer.id)?[]:m.suggestions||[];
    return `<div class="chat-message ${user?'user':'assistant'}" data-message-id="${attr(m.id)}"><div class="chat-avatar">${user?'我':icon('spark')}</div><div class="bubble-wrap"><div class="bubble-meta"><span>${user?'我':'GPT · 自动摘要'}</span><time>${humanTime(m.createdAt)}</time><button class="message-delete" data-action="delete-message" data-id="${attr(m.id)}" title="删除这条对话，可撤销" aria-label="删除${user?'我的归档':'GPT 摘要'}">${icon('trash')}</button></div><div class="chat-bubble">${esc(m.text)}${(m.attachments||[]).map(a=>`<button class="attachment-card" data-action="open-file" data-id="${attr(a.id)}"><span class="attachment-icon">${icon('file')}</span><span class="attachment-info"><strong>${esc(a.name)}</strong><small>${fileSize(a.size)} · ${esc(fileStatus(a))}</small></span></button>`).join('')}${suggestions.length?`<div class="suggestion-label">下一步 · 点击加入任务</div><div class="suggestions">${suggestions.map((s,i)=>{const exists=scopeTasks(drawer.type,drawer.id).some(t=>t.title===s.title);return `<button class="suggestion-chip" data-action="add-suggestion" data-message-id="${attr(m.id)}" data-index="${i}" ${exists?'disabled':''}>${icon(exists?'check':'plus')}${esc(s.title)}${exists?' · 已加入':''}</button>`;}).join('')}</div>`:''}</div></div></div>`;
  }
  function inputField(label,name,value,placeholder='',type='text',required=false) {return `<div class="form-field"><label for="field-${name}">${label}</label><input id="field-${name}" name="${name}" data-modal-field="${name}" data-focus="modal-${name}" type="${type}" value="${attr(value)}" placeholder="${attr(placeholder)}" ${required?'required':''}></div>`;}
  function openStudent(id) {if(id&&isArchived('student',id))return;const s=context('student',id);modal={type:'student',values:{id:s?.id,name:s?.name||'',grade:s?.grade||'',tags:(s?.tags||[]).join('; '),bio:s?.bio||'',detailsOpen:!!s}};renderModal();setTimeout(()=>$('#field-name')?.focus(),0);}
  function openProject(id) {const p=context('project',id);modal={type:'project',values:{id:p?.id,name:p?.name||'',studentIds:[...p?.studentIds||[]]}};renderModal();setTimeout(()=>$('#field-name')?.focus(),0);}
  function openTask(id) {const t=db.tasks.find(t=>t.id===id);if(!t)return;modal={type:'task',values:{id,title:t.title,start:t.start||'',due:t.due||'',tags:(t.tags||[]).join('; '),detailsOpen:!!(t.start||t.due||(t.tags||[]).length)}};renderModal();setTimeout(()=>$('#field-title')?.focus(),0);}
  function defaultAlarm(studentId=null,projectId=null) {const d=new Date();d.setHours(d.getHours()+1,0,0,0);return {id:null,scope:projectId?`project:${projectId}`:studentId?`student:${studentId}`:'global',title:projectId?`跟进${context('project',projectId)?.name||'项目'}的进度`:studentId?`跟进${context('student',studentId)?.name||'成员'}的进度`:'查看成员进度',when:localInput(d),repeat:'once',intervalDays:1,monthDay:d.getDate()};}
  function openAlarms(studentId=null,date=null,projectId=null) {if(studentId&&isArchived('student',studentId))return;const values=defaultAlarm(studentId,projectId);if(date){values.when=`${date}T${date===todayKey()?values.when.slice(11):'09:00'}`;values.monthDay=Number(date.slice(8,10));}modal={type:'alarms',filter:studentId||null,projectFilter:projectId||null,values};renderModal();}
  function editAlarm(id) {
    const alarm=db.alarms.find(a=>a.id===id);if(!alarm)return;
    if(modal?.type!=='alarms')openAlarms(alarm.studentId,null,alarm.projectId);
    modal.values={id:alarm.id,scope:alarm.scopeType==='global'?'global':alarm.scopeType==='project'?`project:${alarm.projectId}`:`student:${alarm.studentId}`,title:alarm.title,when:localInput(alarm.when),enabled:!!alarm.enabled,repeat:alarm.repeat,intervalDays:alarm.intervalDays||1,monthDay:alarm.monthDay||new Date(alarm.when).getDate()};renderModal();
  }
  function openReminderDay(studentId,date) {
    const day=window.GanttLayout.reminderDays(db.alarms,db.projects,studentId,{start:date,days:1}).find(d=>d.date===date);
    if(!day?.alarms.length){openAlarms(studentId,date);return;}
    openAlarms(studentId,date);
    if(day.alarms.length===1){editAlarm(day.alarms[0].alarm.id);return;}
    modal.occurrenceDate=date;modal.occurrences=day.alarms;renderModal();
  }
  function metaField(value) {return `<div class="form-field"><label for="field-tags">meta 标签 <span class="optional">可选</span></label><div class="meta-input-wrap"><input id="field-tags" name="tags" data-modal-field="tags" data-focus="modal-tags" value="${attr(value)}" placeholder="实习; 联培" autocomplete="off"><button type="button" class="meta-input-add" data-action="student-meta-plus" title="继续添加一个标签" aria-label="继续添加一个标签">${icon('plus')}</button></div></div>`;}
  function appendSeparator(value) {const tags=unique(listTags(value));return tags.length?`${tags.join('; ')}; `:'';}
  function recentLabels(kind) {const stored=db.preferences?.[kind]||[];return unique(stored).slice(0,kind==='recentStudentLabels'?8:6);}
  function labelChips(kind) {const labels=recentLabels(kind);return labels.length?`<div class="recent-labels" aria-label="最近使用的标签">${labels.map(label=>`<button type="button" class="label-chip" data-action="${kind==='recentStudentLabels'?'student-label':'alarm-label'}" data-label="${attr(label)}" title="点击填入">${esc(label)}</button>`).join('')}</div>`:'';}
  function openNewTask(studentId,start,due) {modal={type:'new-task',values:{scopeType:'student',scopeId:studentId,title:'',start,due}};renderModal();setTimeout(()=>$('#field-title')?.focus(),0);}

  function skipAlarmButton(a) {return a?.enabled?`<button type="button" class="alarm-skip" data-action="${a.repeat==='once'?'cancel-alarm':'skip-alarm'}" data-id="${attr(a.id)}" data-when="${attr(a.when)}" title="取消 ${attr(humanTime(a.when,true))} 这一次${a.repeat==='once'?'':'，后续照常提醒'}">取消本次</button>`:'';}
  function alarmMasterSwitch(a) {return a?`<div class="alarm-master"><span>整个提醒</span><button type="button" class="toggle${a.enabled?' on':''}" data-action="toggle-alarm" data-id="${attr(a.id)}" title="${a.enabled?'关闭':'启用'}整个提醒" aria-label="${a.enabled?'关闭':'启用'}整个提醒" role="switch" aria-checked="${!!a.enabled}"></button><small>${a.enabled?'已启用':'已关闭'}</small></div>`:'';}
  function openSettings() {modal={type:'settings',values:{baseUrl:db.settings.baseUrl||'',model:db.settings.model||'',apiMode:db.settings.apiMode||'responses',key:'',currentStudentId:db.settings.currentStudentId||''}};renderModal();}
  function renderModal() {
    if(!modal){$('#modal-root').innerHTML='';return;}
    const v=modal.values||{};let title='',body='',footer='',form='';
    if(modal.type==='student') {
      title=v.id?'编辑成员':'添加成员';form='student';
      body=`<div class="form-row">${inputField('姓名','name',v.name,'姓名','text',true)}${inputField('年级 <span class="optional">可选</span>','grade',v.grade,'如：研二')}</div>`+metaField(v.tags)+labelChips('recentStudentLabels')+`<details class="optional-details" data-modal-details ${v.detailsOpen?'open':''}><summary>bio（可选）</summary><div class="form-field"><textarea id="field-bio" name="bio" data-modal-field="bio" data-focus="modal-bio" rows="3" placeholder="当前研究主题">${esc(v.bio)}</textarea></div></details>`;
      footer=`${v.id?`<button class="danger modal-delete" data-action="delete-student" data-id="${attr(v.id)}" type="button">删除成员</button>`:''}<button data-action="close-modal" type="button">取消</button><button class="primary" type="submit">${v.id?'保存':'添加成员'}</button>`;
    } else if(modal.type==='delete-student') {
      title='删除成员';body=`<p class="delete-confirmation">删除“${esc(context('student',v.id)?.name||'成员')}”？个人任务和提醒将移出当前列表，归档记录保留。</p>`;footer=`<button data-action="close-modal" type="button">取消</button><button class="danger" data-action="confirm-delete-student" data-id="${attr(v.id)}" type="button">删除成员</button>`;
    } else if(modal.type==='new-task') {
      title='添加任务';form='new-task';body=`<div class="selected-date-range">${icon('clock')}${esc(context('student',v.scopeId)?.name)} · ${shortDate(v.start)}${v.start!==v.due?` — ${shortDate(v.due)}`:''}</div>`+inputField('任务','title',v.title,'写下要做什么，Enter 添加','text',true);footer='<button data-action="close-modal" type="button">取消</button><button class="primary" type="submit">添加任务</button>';
    } else if(modal.type==='project') {
      title=v.id?'编辑项目':'新建项目';form='project';body=inputField('项目名称','name',v.name,'要一起推进什么','text',true)+`<div class="form-field"><label>成员 <span class="optional">可随时调整</span></label><div class="member-options">${db.students.length?db.students.map(s=>`<label class="member-option"><input type="checkbox" name="member" value="${attr(s.id)}" data-modal-member ${v.studentIds.includes(s.id)?'checked':''}>${esc(s.name)}<small>${esc(s.grade)}</small></label>`).join(''):'<span class="quiet-note">先添加成员，之后可在这里选成员。</span>'}</div></div>`;footer='<button data-action="close-modal" type="button">取消</button><button class="primary" type="submit">保存项目</button>';
    } else if(modal.type==='task') {
      title='任务';form='task';body=inputField('任务名称','title',v.title,'任务名称','text',true)+`<details class="optional-details" data-modal-details ${v.detailsOpen?'open':''}><summary>日期与标签（可选）</summary><div class="form-row">${inputField('开始日期 <span class="optional">可选</span>','start',v.start,'','date')}${inputField('截止日期 <span class="optional">可选</span>','due',v.due,'','date')}</div>`+inputField('标签 <span class="optional">可选</span>','tags',v.tags,'如：受阻，等反馈')+'</details>';footer=`<button class="danger modal-delete" data-action="delete-task" data-id="${attr(v.id)}" type="button">删除任务</button><button data-action="close-modal" type="button">取消</button><button class="primary" type="submit">保存</button>`;
    } else if(modal.type==='alarms') {
      title=modal.projectFilter?`${context('project',modal.projectFilter)?.name||'项目'}的提醒`:modal.filter?`${context('student',modal.filter)?.name||'成员'}的提醒`:'提醒';
      const relevant=modal.filter?alarmsForStudent(modal.filter):db.alarms;
      const alarms=relevant.filter(a=>modal.occurrences?modal.occurrences.some(x=>x.alarm.id===a.id):!modal.projectFilter||(a.scopeType==='project'&&a.projectId===modal.projectFilter)).sort((a,b)=>(Number(!!b.enabled)-Number(!!a.enabled))||(new Date(a.when)-new Date(b.when)));
      body=`<form class="alarm-add" data-form="alarm"><div class="alarm-add-top"><select name="scope" data-modal-field="scope" data-focus="alarm-scope" aria-label="提醒对象"><option value="global" ${v.scope==='global'?'selected':''}>全局提醒</option>${db.students.length?`<optgroup label="成员">${db.students.map(s=>`<option value="student:${attr(s.id)}" ${v.scope===`student:${s.id}`?'selected':''}>${esc(s.name)}</option>`).join('')}</optgroup>`:''}${db.projects.length?`<optgroup label="项目">${db.projects.map(p=>`<option value="project:${attr(p.id)}" ${v.scope===`project:${p.id}`?'selected':''}>项目 · ${esc(p.name)}</option>`).join('')}</optgroup>`:''}</select><input name="title" data-modal-field="title" data-focus="alarm-title" placeholder="提醒内容" value="${attr(v.title)}" required aria-label="提醒内容"></div>${v.scope==='global'?labelChips('recentGlobalAlarmLabels'):''}<div class="alarm-selected-time"><strong>${esc(shortDate(v.when.slice(0,10)))} ${esc(v.when.slice(11))}</strong><div class="time-presets">${['09:00','14:00','17:00','20:00'].map(time=>`<button type="button" class="${v.when.slice(11)===time?'selected':''}" data-action="alarm-clock" data-time="${time}">${time}</button>`).join('')}</div></div><div class="alarm-add-time"><input type="datetime-local" name="when" data-modal-field="when" data-focus="alarm-when" value="${attr(v.when)}" required aria-label="提醒时间"><select name="repeat" data-modal-field="repeat" data-focus="alarm-repeat" aria-label="重复规则">${[['once','仅一次'],['daily','每天'],['weekly','每周'],['monthly','每月'],['interval','每隔几天']].map(([r,l])=>`<option value="${r}" ${v.repeat===r?'selected':''}>${l}</option>`).join('')}</select><button type="submit" class="primary">${v.id?'保存':'添加'}</button></div>${v.repeat==='interval'?`<div class="form-field" style="margin:10px 0 0"><label>间隔天数</label><input type="number" min="1" max="365" name="intervalDays" data-modal-field="intervalDays" value="${attr(v.intervalDays)}" required></div>`:''}${v.repeat==='monthly'?`<p class="monthly-anchor">每月 ${v.monthDay||Number(v.when.slice(8,10))} 日</p>`:''}${v.id?alarmMasterSwitch(db.alarms.find(a=>a.id===v.id)):""}<div class="quick-presets"><button type="button" data-action="alarm-preset" data-preset="10min">10 分钟后</button><button type="button" data-action="alarm-preset" data-preset="tomorrow">明天 9:00</button><button type="button" data-action="alarm-preset" data-preset="week">一周后 9:00</button>${v.id?skipAlarmButton(db.alarms.find(a=>a.id===v.id))+'<button type="button" data-action="alarm-reset">取消编辑</button>':''}</div></form><div class="alarm-list-heading"><h3>${modal.occurrenceDate?`${shortDate(modal.occurrenceDate)} 的提醒`:modal.projectFilter?'项目提醒':modal.filter?'相关提醒':'全部提醒'}</h3>${modal.filter||modal.projectFilter?'<button class="text-button small" data-action="alarms">查看全部</button>':''}</div>${alarms.length?alarms.map(a=>`<div class="alarm-item${a.enabled?'':' off'}${window.ReminderDisplay.isTodayAlarm(a)?' reminder-today':''}"><div class="alarm-main"><strong>${esc(a.title)} <span class="tag${a.scopeType==='global'?' blue':''}">${esc(alarmScopeLabel(a))}</span></strong><small>${humanTime(modal.occurrences?.find(x=>x.alarm.id===a.id)?.when||a.when,true)} · ${{once:'单次',daily:'每天',weekly:'每周',monthly:`每月${a.monthDay||new Date(a.when).getDate()}日`,interval:`每 ${a.intervalDays||1} 天`}[a.repeat]||'单次'}${a.enabled?'':' · 已暂停 / 已提醒'}</small></div><div class="alarm-actions">${a.enabled?skipAlarmButton(a):`<button type="button" class="alarm-skip" data-action="toggle-alarm" data-id="${attr(a.id)}">启用整个提醒</button>`}<button class="icon-button" data-action="edit-alarm" data-id="${attr(a.id)}" title="修改提醒">${icon('edit')}</button><button class="icon-button" data-action="delete-alarm" data-id="${attr(a.id)}" title="删除提醒">${icon('trash')}</button></div></div>`).join(''):'<p class="quiet-note">暂无提醒</p>'}`;
    } else if(modal.type==='settings') {
      title='设置';form='settings';body=`<section class="settings-section"><div class="form-field"><label for="field-currentStudentId">当前用户</label><select id="field-currentStudentId" name="currentStudentId" data-modal-field="currentStudentId" data-focus="modal-currentStudentId"><option value="">请选择本人</option>${db.students.map(s=>`<option value="${attr(s.id)}" ${v.currentStudentId===s.id?'selected':''}>${esc(s.name)}</option>`).join('')}</select><p class="field-hint">GPT 会把“我”的任务和提醒安排给这个成员。</p></div></section><section class="settings-section"><h3>GPT 自动摘要</h3><div class="settings-status"><span class="status-dot" style="background:${db.settings.aiConfigured?'#79a58d':'#b7bfcb'}"></span>${db.settings.aiConfigured?'已配置 · 发送归档后自动整理':'尚未连接 · 仍可正常保存记录和文件'}</div><button type="button" class="small" data-action="detect-settings">${icon('refresh')}导入本机现有 GPT 配置</button><details ${db.settings.aiConfigured?'':'open'}><summary>手动设置接口</summary>${inputField('API 地址','baseUrl',v.baseUrl,'https://api.openai.com/v1')}${inputField('模型','model',v.model,'使用接口中可用的模型名称')}${inputField('API Key <span class="optional">留空保留已有密钥</span>','key',v.key,'输入密钥','password')}<div class="form-field"><label for="field-apiMode">接口类型</label><select id="field-apiMode" name="apiMode" data-modal-field="apiMode"><option value="responses" ${v.apiMode==='responses'?'selected':''}>Responses</option><option value="chat" ${v.apiMode==='chat'?'selected':''}>Chat Completions</option></select></div></details><p class="field-hint">生成摘要会将当前归档文字及可读取的附件内容发送到所配置的 GPT 接口。</p></section><section class="settings-section" id="assistant-settings"></section><section class="settings-section" id="mobile-settings"></section><section class="settings-section"><h3>资料备份</h3><div class="settings-buttons"><button type="button" data-action="export-backup">${icon('download')}导出完整备份</button><button type="button" data-action="import-backup">${icon('upload')}恢复备份</button></div><p class="data-path">${esc(db.settings.dataRoot||'数据保存在应用旁的 Data 文件夹')}<br>包括成员、任务、记录和附件；不包含 API 密钥。</p></section><section class="settings-section"><h3>Windows 提醒</h3><p class="quiet-note">${esc(db.status.notificationStatus||'通过 Windows 通知栏静默提醒。')}</p><p class="field-hint">关闭窗口后保留托盘运行；从托盘退出应用后暂停提醒。</p></section>`;footer='<button data-action="close-modal" type="button">关闭</button><button type="submit" class="primary">保存设置</button>';
    }
    $('#modal-root').innerHTML=`<div class="modal-backdrop" data-action="modal-backdrop"><section class="modal${['alarms','settings'].includes(modal.type)?' wide':''}" role="dialog" aria-modal="true" aria-label="${attr(title)}">${form?`<form data-form="${form}" style="display:flex;flex-direction:column;min-height:0;max-height:90vh">`:''}<header class="modal-header"><h2>${esc(title)}</h2><button type="button" class="icon-button" data-action="close-modal" title="关闭">${icon('close')}</button></header><div class="modal-body">${body}</div>${footer?`<footer class="modal-footer">${footer}</footer>`:''}${form?'</form>':''}</section></div>`;
    if(modal.type==='settings'){window.AssistantChat.mountSettings($('#assistant-settings'));window.MobileSettings?.mount($('#mobile-settings'));}
  }
  function syncModalInput(el) {
    if(!modal)return;
    if(el.hasAttribute('data-modal-member')){const ids=new Set(modal.values.studentIds);el.checked?ids.add(el.value):ids.delete(el.value);modal.values.studentIds=[...ids];}
    else if(el.dataset.modalField){
      const field=el.dataset.modalField,previous=modal.values[field];modal.values[field]=el.value;
      if(modal.type==='alarms'&&((field==='when'&&String(previous).slice(0,10)!==el.value.slice(0,10))||(field==='repeat'&&el.value==='monthly')))modal.values.monthDay=Number(modal.values.when.slice(8,10));
    }
  }
  document.addEventListener('compositionstart',()=>{composing=true;});
  document.addEventListener('compositionend',()=>{composing=false;requestAnimationFrame(()=>{if(!composing&&!timelineGesture&&!studentGesture)render();});});
  document.addEventListener('input',e=>{
    const el=e.target;syncModalInput(el);
    if(el.hasAttribute('data-inline-value')&&inlineEdit){inlineEdit.value=el.value;inlineEdit.error='';return;}
    const taskForm=el.closest('form[data-form="task-add"]');
    if(taskForm&&el.name==='title')taskDrafts.set(scopeKey(taskForm.dataset.scopeType,taskForm.dataset.scopeId),el.value);
    if(el.dataset.input==='search'){const next=el.value;if(inlineEdit)saveInlineEdit().then(ok=>{if(ok){search=next;preserveUI(renderMain);}});else{search=next;preserveUI(renderMain);}}
    else if(el.dataset.input==='chat'){draft().text=el.value;el.style.height='auto';el.style.height=`${Math.min(el.scrollHeight,180)}px`;const send=$('[data-action="send-chat"]');if(send)send.disabled=uploading||busy.has('chat-send')||(!el.value.trim()&&!draft().files.length);}
  });
  document.addEventListener('focusout',e=>{
    if(!e.target.hasAttribute?.('data-inline-value')||repainting||!inlineEdit)return;
    if(e.relatedTarget?.closest('.inline-edit')===e.target.closest('.inline-edit'))return;
    const edit=inlineEdit,target=e.target;setTimeout(()=>{if(inlineEdit===edit&&!repainting&&target.isConnected&&!composing)saveInlineEdit();},0);
  });
  document.addEventListener('toggle',e=>{if(modal&&e.target.hasAttribute?.('data-modal-details'))modal.values.detailsOpen=e.target.open;},true);
  document.addEventListener('change',e=>{
    syncModalInput(e.target);
    if(e.target.id==='chat-files')addFiles(e.target.files);
    if(modal?.type==='alarms'&&['repeat','scope'].includes(e.target.dataset.modalField)){
      if(e.target.dataset.modalField==='scope') {const [type,id]=modal.values.scope.split(':');modal.values.title=id?`跟进${context(type,id)?.name||(type==='project'?'项目':'成员')}的进度`:'查看成员进度';}
      renderModal();
    }
  });
  document.addEventListener('submit',e=>{
    const form=e.target.closest('form[data-form]');if(!form)return;e.preventDefault();
    perform(`form-${form.dataset.form}`,async()=>{
      const type=form.dataset.form;
      if(type==='task-add'){
        const input=form.querySelector('[name="title"]'),title=input.value.trim();if(!title)return;
        await rpc('task.add',{title,scopeType:form.dataset.scopeType,scopeId:form.dataset.scopeId});
        taskDrafts.delete(scopeKey(form.dataset.scopeType,form.dataset.scopeId));
        const replacement=document.querySelector(`[data-focus="${CSS.escape(input.dataset.focus)}"]`);if(replacement){replacement.value='';replacement.focus();}toast('已添加任务');return;
      }
      const v={...modal.values};
      if(type==='student'){await rpc('student.save',{id:v.id,name:v.name.trim(),grade:v.grade.trim(),tags:unique(listTags(v.tags)),bio:v.bio.trim()});modal=null;renderModal();toast(v.id?'成员信息已保存':'已添加成员');}
      else if(type==='project'){const before=new Set(db.projects.map(p=>p.id));await rpc('project.save',{id:v.id,name:v.name.trim(),studentIds:v.studentIds});selectedProject=v.id||db.projects.find(p=>!before.has(p.id))?.id||selectedProject;modal=null;render();toast('项目已保存');}
      else if(type==='new-task'){await rpc('task.add',{title:v.title.trim(),scopeType:v.scopeType,scopeId:v.scopeId,start:v.start,due:v.due});modal=null;renderModal();toast('已添加任务');}
      else if(type==='task'){await rpc('task.update',{id:v.id,title:v.title.trim(),start:v.start||null,due:v.due||null,tags:unique(listTags(v.tags).filter(t=>t!=='逾期'))});modal=null;renderModal();toast('任务已更新');}
      else if(type==='alarm'){
        const [scopeType,scopeId]=v.scope.split(':'),studentId=scopeType==='student'?scopeId:null,projectId=scopeType==='project'?scopeId:null;
        await rpc('alarm.save',{id:v.id,scopeType,studentId,projectId,title:v.title.trim(),when:new Date(v.when).toISOString(),repeat:v.repeat,intervalDays:Number(v.intervalDays)||1,...(v.repeat==='monthly'?{monthDay:v.monthDay||Number(v.when.slice(8,10))}:{}),enabled:v.id?v.enabled!==false:true});
        modal.values=defaultAlarm(modal.filter,modal.projectFilter);delete modal.occurrences;delete modal.occurrenceDate;renderModal();toast('提醒已保存');
      } else if(type==='settings'){
        await window.MobileSettings?.save();await rpc('settings.save',{baseUrl:v.baseUrl.trim(),model:v.model.trim(),apiMode:v.apiMode,currentStudentId:v.currentStudentId||'',...(v.key.trim()?{key:v.key.trim()}:{})});modal=null;renderModal();toast('设置已保存');
      }
    });
  });
  function reorderStudents(movingId,targetId,after,visibleIds=db.students.filter(studentMatches).map(s=>s.id)) {
    if(movingId===targetId)return;
    const allIds=db.students.map(s=>s.id),visibleSet=new Set(visibleIds);
    if(visibleIds.some(id=>!allIds.includes(id))||!visibleSet.has(movingId)||!visibleSet.has(targetId)){toast('成员列表已变化，请重新拖动。',true);return;}
    const reordered=visibleIds.filter(id=>id!==movingId),index=reordered.indexOf(targetId)+(after?1:0);
    reordered.splice(index,0,movingId);
    let cursor=0;const ids=allIds.map(id=>visibleSet.has(id)?reordered[cursor++]:id);
    if(ids.every((id,i)=>id===allIds[i]))return;
    perform('student-reorder',async()=>{await rpc('student.reorder',{ids});});
  }
  function studentDropTarget(g) {
    const rows=Array.from(document.querySelectorAll('.student-list .student-row'));
    if(!rows.length)return;
    const found=rows.find(row=>g.clientY<row.getBoundingClientRect().bottom)||rows.at(-1),rect=found.getBoundingClientRect();
    g.targetId=found.dataset.studentId;g.after=g.clientY>rect.top+rect.height/2;
    document.querySelectorAll('.reorder-before,.reorder-after').forEach(node=>node.classList.remove('reorder-before','reorder-after'));
    if(g.targetId!==g.id)document.querySelectorAll(`[data-student-id="${CSS.escape(g.targetId)}"].student-row,[data-student-id="${CSS.escape(g.targetId)}"].timeline-row`).forEach(node=>node.classList.add(g.after?'reorder-after':'reorder-before'));
  }
  function animateStudentDrag() {
    const g=studentGesture;if(!g?.moved)return;
    const list=$('.student-list'),gantt=$('.gantt-scroll');
    if(list&&gantt){const rect=list.getBoundingClientRect(),distance=g.clientY<rect.top+35?g.clientY-(rect.top+35):g.clientY>rect.bottom-35?g.clientY-(rect.bottom-35):0;if(distance){gantt.scrollTop+=Math.max(-16,Math.min(16,distance*.3));syncBoardRows();}studentDropTarget(g);}
    if(g.ghost){g.ghost.style.left=`${g.clientX+12}px`;g.ghost.style.top=`${g.clientY+12}px`;}
    g.frame=requestAnimationFrame(animateStudentDrag);
  }
  function finishStudentGesture(cancel=false) {
    const g=studentGesture;if(!g)return;studentGesture=null;
    cancelAnimationFrame(g.frame);try{document.body.releasePointerCapture(g.pointerId);}catch{}
    g.ghost?.remove();document.body.classList.remove('student-dragging');
    document.querySelectorAll('.reorder-source,.reorder-before,.reorder-after').forEach(node=>node.classList.remove('reorder-source','reorder-before','reorder-after'));
    suppressStudentClickUntil=Date.now()+400;
    if(!cancel&&g.moved&&g.targetId)reorderStudents(g.id,g.targetId,g.after,g.visibleIds);
    render();
  }
  document.addEventListener('pointerdown',e=>{
    const handle=e.target.closest('.student-drag-handle');
    if(!handle||e.button!==0||modal||drawer||studentGesture||timelineGesture||busy.has('student-reorder'))return;
    const id=handle.dataset.id;
    studentGesture={id,pointerId:e.pointerId,clientX:e.clientX,clientY:e.clientY,startY:e.clientY,moved:false,visibleIds:db.students.filter(studentMatches).map(s=>s.id)};
    if(inlineEdit)saveInlineEdit().then(ok=>{if(!ok&&studentGesture?.id===id)finishStudentGesture(true);});
    document.body.setPointerCapture(e.pointerId);e.preventDefault();
  });
  document.addEventListener('pointermove',e=>{
    const g=studentGesture;if(!g||e.pointerId!==g.pointerId)return;
    g.clientX=e.clientX;g.clientY=e.clientY;
    if(!g.moved&&Math.abs(e.clientY-g.startY)<5)return;
    if(!g.moved){g.moved=true;document.body.classList.add('student-dragging');document.querySelectorAll(`[data-student-id="${CSS.escape(g.id)}"].student-row,[data-student-id="${CSS.escape(g.id)}"].timeline-row`).forEach(node=>node.classList.add('reorder-source'));g.ghost=document.createElement('div');g.ghost.className='student-drag-ghost';g.ghost.textContent=context('student',g.id)?.name||'成员';document.body.append(g.ghost);animateStudentDrag();}
    studentDropTarget(g);e.preventDefault();
  });
  document.addEventListener('pointerup',e=>{if(studentGesture?.pointerId===e.pointerId)finishStudentGesture();});
  document.addEventListener('pointercancel',e=>{if(studentGesture?.pointerId===e.pointerId)finishStudentGesture(true);});
  window.addEventListener('blur',()=>{if(studentGesture)finishStudentGesture(true);});
  function pointerDay(timeline,clientX) {
    return Math.max(0,Math.min(Number(timeline.dataset.rangeDays)-1,Math.floor((clientX-timeline.getBoundingClientRect().left)/dayWidth)));
  }
  function finishGesture(cancel=false) {
    const g=timelineGesture;if(!g)return;
    timelineGesture=null;suppressTimelineClickUntil=cancel?Infinity:Date.now()+350;
    try{g.timeline.releasePointerCapture(g.pointerId);}catch{}
    g.selection?.remove();g.bar?.classList.remove('dragging');document.body.classList.remove('timeline-dragging');
    if(cancel){render();return;}
    if(g.kind==='create') {
      const start=addDays(g.timeline.dataset.rangeStart,Math.min(g.startDay,g.endDay)),due=addDays(g.timeline.dataset.rangeStart,Math.max(g.startDay,g.endDay));
      if(g.moved)openNewTask(g.timeline.dataset.studentId,start,due);else openAlarms(g.timeline.dataset.studentId,start);
    } else if(!g.moved)openTask(g.task.id);
    else if(g.next&&(g.next.start!==g.task.start||g.next.due!==g.task.due))perform(`task-dates:${g.task.id}`,async()=>{await rpc('task.update',{id:g.task.id,...g.next});toast('任务日期已调整');});
    render();
  }
  document.addEventListener('pointerdown',e=>{
    if(e.button!==0||modal||drawer||timelineGesture||studentGesture)return;
    const timeline=e.target.closest('.timeline');if(!timeline)return;
    const bar=e.target.closest('.task-bar'),action=e.target.closest('[data-action]')?.dataset.action;
    if(bar&&action&&action!=='edit-task')return;
    if(!bar&&!e.target.closest('.timeline-day'))return;
    suppressTimelineClickUntil=0;
    const startDay=pointerDay(timeline,e.clientX),task=bar?db.tasks.find(t=>t.id===bar.dataset.taskId):null;
    if(bar&&!task)return;
    timelineGesture={timeline,bar,pointerId:e.pointerId,clientX:e.clientX,startDay,endDay:startDay,moved:false,kind:bar?(e.target.dataset.resize||'move'):'create',task:task?{...task}:null};
    if(inlineEdit)saveInlineEdit();
    timeline.setPointerCapture(e.pointerId);
    e.preventDefault();
  });
  document.addEventListener('pointermove',e=>{
    const g=timelineGesture;if(!g||g.pointerId!==e.pointerId)return;
    if(!g.moved&&Math.abs(e.clientX-g.clientX)<7)return;
    g.moved=true;document.body.classList.add('timeline-dragging');
    const board=$('.gantt-scroll'),rect=board.getBoundingClientRect();
    if(e.clientX>rect.right-18)board.scrollLeft+=12;
    else if(e.clientX<rect.left+18)board.scrollLeft=Math.max(0,board.scrollLeft-12);
    g.endDay=pointerDay(g.timeline,e.clientX);
    let offset,span;
    if(g.kind==='create') {
      offset=Math.min(g.startDay,g.endDay);span=Math.abs(g.endDay-g.startDay)+1;
      if(!g.selection){g.selection=document.createElement('div');g.selection.className='timeline-selection';g.timeline.append(g.selection);}
    } else {
      const task=g.task,start=task.start||task.due,due=task.due||task.start,delta=g.endDay-g.startDay;
      let nextStart=start,nextDue=due;
      if(g.kind==='move'){nextStart=addDays(start,delta);nextDue=addDays(due,delta);g.next={start:task.start?nextStart:null,due:task.due?nextDue:null};}
      else if(g.kind==='start'){nextStart=addDays(g.timeline.dataset.rangeStart,g.endDay);if(nextStart>due)nextStart=due;g.next={start:nextStart,due};}
      else{nextDue=addDays(g.timeline.dataset.rangeStart,g.endDay);if(nextDue<start)nextDue=start;g.next={start,due:nextDue};}
      offset=daysBetween(nextStart,g.timeline.dataset.rangeStart);span=daysBetween(nextDue,nextStart)+1;g.bar.classList.add('dragging');
    }
    const node=g.selection||g.bar;node.style.setProperty('--offset',offset);node.style.setProperty('--span',span);
    e.preventDefault();
  });
  document.addEventListener('pointerup',e=>{if(timelineGesture?.pointerId===e.pointerId)finishGesture();});
  document.addEventListener('pointercancel',e=>{if(timelineGesture?.pointerId===e.pointerId)finishGesture(true);});
  window.addEventListener('blur',()=>{if(timelineGesture)finishGesture(true);});
  document.addEventListener('click',async e=>{
    const el=e.target.closest('[data-action]');if(!el||el.disabled)return;
    if(e.detail!==0&&Date.now()<suppressStudentClickUntil&&el.closest('.student-row')){e.preventDefault();return;}
    if(e.detail!==0&&Date.now()<suppressTimelineClickUntil&&el.closest('.timeline')){e.preventDefault();return;}
    const a=el.dataset.action,id=el.dataset.id,st=el.dataset.scopeType,si=el.dataset.scopeId;
    if(a==='modal-backdrop'&&e.target!==el)return;
    if(inlineEdit&&['view','new-student','edit-student','student-profile','student-tasks','archive','archive-task','drawer-tab','close-drawer','settings','alarms','student-alarm','project-alarm','reminder-day','edit-alarm','date-alarm','timeline-date','edit-task','new-project','edit-project','delete-student','select-project','open-student-project','archive-filter','reference-archive'].includes(a)){if(!await saveInlineEdit())return;}
    if(a==='view'){const next=el.dataset.view;if(inlineEdit)saveInlineEdit().then(ok=>{if(ok)showView(next);});else showView(next);return;}
    if(a==='reorder-student')return;
    if(a==='append-meta'){beginInlineEdit(id,'tags',el.dataset.location,false,true);return;}
    if(a==='inline-separator'&&inlineEdit?.field==='tags'){inlineEdit.value=appendSeparator(inlineEdit.value);render();const input=$('[data-inline-value]');input?.focus({preventScroll:true});input?.setSelectionRange(input.value.length,input.value.length);return;}
    if(a==='student-meta-plus'&&modal?.type==='student'){modal.values.tags=appendSeparator(modal.values.tags);preserveUI(renderModal);const input=$('#field-tags');input?.focus({preventScroll:true});input?.setSelectionRange(input.value.length,input.value.length);return;}
    if(a==='edit-inline'){beginInlineEdit(id,el.dataset.field,el.dataset.location,el.dataset.multiline==='true');return;}
    if(a==='save-inline'){saveInlineEdit();return;}
    if(a==='cancel-inline'){if(inlineEdit&&!inlineEdit.saving){inlineEdit=null;render();}return;}
    if(a==='inline-label'&&inlineEdit?.field==='tags'){inlineEdit.value=unique([...listTags(inlineEdit.value),el.dataset.label]).join('; ');render();$('[data-inline-value]')?.focus({preventScroll:true});return;}
    if(a==='reference-archive'&&drawer){const reference={scopeType:drawer.type,scopeId:drawer.id,title:context(drawer.type,drawer.id)?.name||'归档'};perform('reference-archive',async()=>{if(!await saveInlineEdit())return;window.AssistantChat.reference(reference);drawer=null;dragDepth=0;renderDrawer();showView('assistant');});return;}
    if(a==='new-student'||a==='edit-student'){openStudent(id);return;}
    if(a==='new-project'||a==='edit-project'){openProject(id);return;}
    if(a==='select-project'){selectedProject=id;renderMain();return;}
    if(a==='open-student-project'){if(!context('project',id))return;selectedProject=id;showView('projects');return;}
    if(a==='archive-filter'){archiveFilter=el.dataset.filter;renderMain();return;}
    if(a==='student-profile'){drawer={type:'student',id,tab:'profile',taskId:null};renderDrawer();return;}
    if(a==='student-tasks'){drawer={type:'student',id,tab:'tasks',taskId:null};renderDrawer();return;}
    if(a==='delete-student'){modal={type:'delete-student',values:{id}};renderModal();return;}
    if(a==='student-label'&&modal?.type==='student'){modal.values.tags=unique([...listTags(modal.values.tags),el.dataset.label]).join('; ');preserveUI(renderModal);return;}
    if(a==='alarm-label'&&modal?.type==='alarms'){modal.values.title=el.dataset.label;preserveUI(renderModal);return;}
    if(a==='archive'){openArchive(st,si);return;}
    if(a==='archive-task'){openArchive(st,si,id);return;}
    if(a==='drawer-tab'){drawer.tab=el.dataset.tab;renderDrawer();const c=$('.chat-messages');if(c)c.scrollTop=c.scrollHeight;return;}
    if(a==='close-drawer'){const close=()=>{drawer=null;dragDepth=0;renderDrawer();};if(inlineEdit)saveInlineEdit().then(ok=>{if(ok)close();});else close();return;}
    if(a==='close-modal'||a==='modal-backdrop'){modal=null;renderModal();return;}
    if(a==='edit-task'){openTask(id);return;}
    if(a==='inline-task'){inlineAdd=scopeKey(st,si);preserveUI(renderMain);document.querySelector(`[data-focus="${CSS.escape(`task-add-${inlineAdd}`)}"]`)?.focus();return;}
    if(a==='today'){const b=$('.gantt-scroll');if(b)b.scrollTo({left:Math.max(0,(timelineRange().today-2)*dayWidth),behavior:'smooth'});return;}
    if(a==='date-alarm'){openAlarms(null,el.dataset.date);return;}
    if(a==='timeline-date'){openAlarms(el.dataset.studentId,el.dataset.date);return;}
    if(a==='student-alarm'){openAlarms(id);return;}
    if(a==='project-alarm'){openAlarms(null,null,id);return;}
    if(a==='reminder-day'){openReminderDay(el.dataset.studentId,el.dataset.date);return;}
    if(a==='alarms'){openAlarms();return;}
    if(a==='settings'){openSettings();return;}
    if(a==='clear-task-context'){drawer.taskId=null;preserveUI(renderDrawer);return;}
    if(a==='pick-files'){$('#chat-files').click();return;}
    if(a==='remove-file'){draft().files.splice(Number(el.dataset.index),1);preserveUI(renderDrawer);return;}
    if(a==='send-chat'){sendChat();return;}
    if(a==='edit-alarm'){editAlarm(id);return;}
    if(a==='alarm-reset'){modal.values=defaultAlarm(modal.filter,modal.projectFilter);renderModal();return;}
    if(a==='alarm-clock'){modal.values.when=`${modal.values.when.slice(0,10)}T${el.dataset.time}`;preserveUI(renderModal);return;}
    if(a==='alarm-preset'){
      const d=new Date();if(el.dataset.preset==='10min')d.setMinutes(d.getMinutes()+10,0,0);else{d.setDate(d.getDate()+(el.dataset.preset==='week'?7:1));d.setHours(9,0,0,0);}if(modal.values.when.slice(0,10)!==dateKey(d))modal.values.monthDay=d.getDate();modal.values.when=localInput(d);renderModal();return;
    }
    perform(`${a}:${id||''}`,async()=>{
      if(a==='toggle-task'){await rpc('task.toggle',{id});}
      else if(a==='delete-task'){await rpc('task.delete',{id});if(modal?.type==='task'&&modal.values.id===id){modal=null;renderModal();}undoToast(id);}
      else if(a==='restore-task'){await rpc('task.restore',{id});el.closest('.toast')?.remove();toast('任务已恢复');}
      else if(a==='delete-message'){await rpc('chat.delete',{id});undoToast(id,'message');}
      else if(a==='restore-message'){await rpc('chat.restore',{id});el.closest('.toast')?.remove();toast('对话已恢复');}
      else if(a==='confirm-delete-student'){await rpc('student.delete',{id});drafts.delete(scopeKey('student',id));taskDrafts.delete(scopeKey('student',id));modal=null;if(drawer?.id===id)drawer.tab='archive';render();toast('成员已删除，历史归档保留');}
      else if(a==='retry-summary'&&drawer){await rpc('summary.retry',{scopeType:drawer.type,scopeId:drawer.id});}
      else if(a==='add-suggestion'&&drawer){const m=db.messages.find(m=>m.id===el.dataset.messageId),s=m?.suggestions?.[Number(el.dataset.index)];if(s){await rpc('task.add',{title:s.title,scopeType:drawer.type,scopeId:drawer.id});toast('已加入任务');}}
      else if(a==='open-file'){await rpc('file.open',{id});}
      else if(a==='toggle-alarm'){await rpc('alarm.toggle',{id});if(modal?.type==='alarms'&&modal.values.id===id){modal.values.enabled=!!db.alarms.find(x=>x.id===id)?.enabled;renderModal();}}
      else if(a==='cancel-alarm'){const alarm=db.alarms.find(x=>x.id===id);if(!alarm?.enabled||alarm.when!==el.dataset.when){toast('提醒已有更新，请查看后重试');return;}await rpc('alarm.enabled',{id,enabled:false});if(modal?.type==='alarms'&&modal.values.id===id){modal.values.enabled=false;renderModal();}toast('本次提醒已取消');}
      else if(a==='skip-alarm'){await rpc('alarm.skip',{id,when:el.dataset.when});const alarm=db.alarms.find(x=>x.id===id);if(modal?.type==='alarms'&&modal.values.id===id&&alarm){modal.values.when=localInput(alarm.when);renderModal();}toast(`本次已取消${alarm?.enabled?'，下次 '+humanTime(alarm.when,true):''}`);}
      else if(a==='delete-alarm'){await rpc('alarm.delete',{id});toast('提醒已删除');}
      else if(a==='detect-settings'){await rpc('settings.detect');if(modal?.type==='settings')openSettings();toast(db.settings.aiConfigured?'已导入本机 GPT 配置':'未找到可用配置，可手动填写');}
      else if(a==='export-backup'){const r=await rpc('backup.export');if(r?.path)toast('完整备份已导出');}
      else if(a==='import-backup'){const r=await rpc('backup.import');if(isSnapshot(r))toast('备份已恢复');}
    });
  });
  async function addFiles(fileList) {
    if(!drawer||isArchived(drawer.type,drawer.id)||uploading)return;
    const target=draft(),files=Array.from(fileList||[]);if(!files.length)return;
    if(files.some(f=>f.size>20*1024*1024)){toast('单个文件不能超过 20 MB。',true);return;}
    if([...target.files,...files].reduce((n,f)=>n+f.size,0)>40*1024*1024){toast('每条记录的附件总大小不能超过 40 MB。',true);return;}
    uploading=true;preserveUI(renderDrawer);
    try {
      const loaded=await Promise.all(files.map(f=>new Promise((resolve,reject)=>{const reader=new FileReader();reader.onerror=()=>reject(new Error(`无法读取 ${f.name}`));reader.onload=()=>resolve({name:f.name,mime:f.type||'application/octet-stream',size:f.size,dataBase64:String(reader.result).split(',')[1]});reader.readAsDataURL(f);})));target.files.push(...loaded);
    } catch(e){toast(e.message,true);} finally{uploading=false;preserveUI(renderDrawer);}
  }
  async function sendChat() {
    if(!drawer||isArchived(drawer.type,drawer.id)||uploading||busy.has('chat-send'))return;
    const current={...drawer},d=draft(),text=d.text.trim(),files=d.files.map(({name,mime,dataBase64})=>({name,mime,dataBase64}));
    if(!text&&!files.length)return;
    await perform('chat-send',async()=>{
      preserveUI(renderDrawer);
      const task=db.tasks.find(t=>t.id===current.taskId);
      try {
        await rpc('chat.send',{scopeType:current.type,scopeId:current.id,text,files,...(task&&!task.done?{completeTaskId:task.id}:{})});
        d.text='';d.files=[];if(drawer?.id===current.id&&drawer?.type===current.type)drawer.taskId=null;
      } finally {busy.delete('chat-send');preserveUI(renderDrawer);const c=$('.chat-messages');if(c)c.scrollTop=c.scrollHeight;$('#chat-input')?.focus();}
    });
  }
  document.addEventListener('keydown',e=>{
    if(e.target.matches?.('.student-drag-handle')&&['ArrowUp','ArrowDown'].includes(e.key)){e.preventDefault();const id=e.target.dataset.id,ids=db.students.filter(studentMatches).map(s=>s.id),index=ids.indexOf(id),after=e.key==='ArrowDown',target=ids[index+(after?1:-1)];if(target)reorderStudents(id,target,after,ids);return;}
    if(e.target.hasAttribute?.('data-inline-value')&&inlineEdit){
      if(e.key==='Escape'){e.preventDefault();if(!inlineEdit.saving){inlineEdit=null;render();}return;}
      if(e.key==='Enter'&&!e.isComposing&&e.keyCode!==229&&!(inlineEdit.multiline&&e.shiftKey)){e.preventDefault();saveInlineEdit();return;}
    }
    if(e.key==='Escape'){if(studentGesture){e.preventDefault();finishStudentGesture(true);}else if(timelineGesture){e.preventDefault();finishGesture(true);}else if(inlineEdit&&!inlineEdit.saving){inlineEdit=null;render();}else if(modal){modal=null;renderModal();}else if(drawer){drawer=null;dragDepth=0;renderDrawer();}return;}
    if(e.target.id==='chat-input'&&e.key==='Enter'&&!e.shiftKey&&!e.isComposing&&e.keyCode!==229){e.preventDefault();sendChat();}
  });
  document.addEventListener('paste',e=>{
    if(!drawer||drawer.tab!=='archive'||modal||!e.target.closest('.drawer'))return;
    const files=Array.from(e.clipboardData?.items||[]).filter(i=>i.kind==='file').map(i=>i.getAsFile()).filter(Boolean);if(files.length){e.preventDefault();addFiles(files);}
  });
  document.addEventListener('dragenter',e=>{if(!Array.from(e.dataTransfer?.types||[]).includes('Files'))return;e.preventDefault();if(drawer&&drawer.tab==='archive'&&!modal){dragDepth++;if(dragDepth===1)preserveUI(renderDrawer);}});
  document.addEventListener('dragover',e=>{if(Array.from(e.dataTransfer?.types||[]).includes('Files'))e.preventDefault();});
  document.addEventListener('dragleave',e=>{if(dragDepth>0){dragDepth--;if(!dragDepth)preserveUI(renderDrawer);}});
  document.addEventListener('drop',e=>{e.preventDefault();dragDepth=0;if(drawer&&drawer.tab==='archive'&&!modal)addFiles(e.dataTransfer?.files);else renderDrawer();});
  window.addEventListener('error',()=>toast('界面遇到问题，请关闭窗口后重新打开。',true));
  window.AssistantChat?.configure({rpc,openArchive,openReminder:editAlarm,openTask,currentUser:()=>context('student',db.settings.currentStudentId),isConfigured:()=>!!db.settings.aiConfigured});
  window.MobileSettings?.configure({rpc,toast});
  rpc('app.get').catch(e=>{$('#main').innerHTML=empty('archive','暂时无法打开资料',e.message);});
})();
