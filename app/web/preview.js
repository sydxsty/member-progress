/* Empty, transient browser preview. The desktop bridge owns real storage and GPT. */
(() => {
  'use strict';
  if (window.chrome?.webview) return;
  let state = {
    students: [], archivedStudents: [], projects: [], tasks: [], messages: [], alarms: [],
    preferences: {recentStudentLabels: [], recentGlobalAlarmLabels: []},
    settings: {baseUrl:'', model:'', apiMode:'responses', aiConfigured:false, dataRoot:'浏览器预览：仅保存在本页内存中，刷新后清空'},
    status: {notificationStatus:'浏览器预览不发送 Windows 通知；请在桌面应用中使用提醒。'}
  };
  const deletedTasks = new Map();
  function remember(key,labels,max) {state.preferences[key]=[...new Set([...labels.map(s=>String(s).trim()).filter(Boolean),...state.preferences[key]])].slice(0,max);}
  const clone = value => structuredClone(value);
  const id = () => window.crypto?.randomUUID?.() || `preview-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const required = (value,label) => {if(!String(value||'').trim()) throw new Error(`请填写${label}。`);return String(value).trim();};
  const scope = p => {
    const record=(p.scopeType==='project'?state.projects:state.students).find(x=>x.id===p.scopeId);
    if(!record)throw new Error('找不到关联的成员或项目。');return record;
  };
  const find = (array,key,label) => {const item=array.find(x=>x.id===key);if(!item)throw new Error(`找不到${label}。`);return item;};
  const validateDates = p => {if(p.start&&p.due&&p.start>p.due)throw new Error('截止日期不能早于开始日期。');};
  const dataSnapshot = () => clone(state);
  window.PreviewAdapter = {
    async handle(action,p={}) {
      switch(action) {
        case 'app.get': return dataSnapshot();
        case 'student.save': {
          const name=required(p.name,'姓名');
          if(p.id)Object.assign(find(state.students,p.id,'成员'),{name,grade:p.grade||'',tags:p.tags||[],bio:p.bio||''});
          else state.students.push({id:id(),name,grade:p.grade||'',tags:p.tags||[],bio:p.bio||'',currentSummary:'',summaryStatus:'idle',summaryError:''});
          remember('recentStudentLabels',p.tags||[],8);
          break;
        }
        case 'student.patch': {
          const student=find(state.students,p.id,'成员');
          if(!['name','grade','tags','bio','currentSummary'].includes(p.field))throw new Error('不支持修改此属性。');
          if(p.field==='tags') {if(!Array.isArray(p.value))throw new Error('标签格式无效。');student.tags=[...new Set(p.value.map(t=>String(t).trim()).filter(Boolean))];remember('recentStudentLabels',student.tags,8);}
          else {student[p.field]=p.field==='name'?required(p.value,'姓名'):String(p.value||'').trim();if(p.field==='currentSummary'){student.summaryStatus='idle';student.summaryError='';}}
          break;
        }
        case 'student.reorder': {
          if(!Array.isArray(p.ids)||p.ids.length!==state.students.length||new Set(p.ids).size!==p.ids.length||p.ids.some(i=>!state.students.some(s=>s.id===i)))throw new Error('成员列表已变化，请重试。');
          state.students=p.ids.map(i=>state.students.find(s=>s.id===i));break;
        }
        case 'student.delete': {
          const student=find(state.students,p.id,'成员');state.students=state.students.filter(s=>s.id!==p.id);state.archivedStudents.push({...student,deletedAt:new Date().toISOString(),summaryStatus:'idle'});
          state.projects.forEach(project=>project.studentIds=project.studentIds.filter(i=>i!==p.id));
          state.tasks=state.tasks.filter(t=>{const belonged=t.studentIds.includes(p.id);t.studentIds=t.studentIds.filter(i=>i!==p.id);return !belonged||t.studentIds.length||t.projectId;});
          state.alarms=state.alarms.filter(a=>a.studentId!==p.id);break;
        }
        case 'project.save': {
          const name=required(p.name,'项目名称');
          if((p.studentIds||[]).some(i=>!state.students.some(s=>s.id===i)))throw new Error('有成员不存在。');
          if(p.id)Object.assign(find(state.projects,p.id,'项目'),{name,studentIds:p.studentIds||[]});
          else state.projects.push({id:id(),name,studentIds:p.studentIds||[],currentSummary:'',summaryStatus:'idle',summaryError:''});
          break;
        }
        case 'task.add': {
          const title=required(p.title,'任务名称'),s=scope(p);validateDates(p);
          state.tasks.push({id:id(),title,studentIds:p.scopeType==='student'?[p.scopeId]:(p.studentIds||[...s.studentIds]),projectId:p.scopeType==='project'?p.scopeId:null,start:p.start||null,due:p.due||null,done:false,tags:p.tags||[],createdAt:new Date().toISOString()});break;
        }
        case 'task.update': {
          const t=find(state.tasks,p.id,'任务'),next={...t,...p};required(next.title,'任务名称');validateDates(next);Object.assign(t,next);break;
        }
        case 'task.toggle': {const t=find(state.tasks,p.id,'任务');t.done=!t.done;break;}
        case 'task.delete': {const t=find(state.tasks,p.id,'任务');deletedTasks.set(p.id,clone(t));state.tasks=state.tasks.filter(t=>t.id!==p.id);break;}
        case 'task.restore': {
          const t=deletedTasks.get(p.id);if(!t)throw new Error('找不到已删除的任务。');
          const studentIds=(t.studentIds||[]).filter(i=>state.students.some(s=>s.id===i)),projectId=state.projects.some(x=>x.id===t.projectId)?t.projectId:null;
          if(!studentIds.length&&!projectId)throw new Error('成员已删除，无法恢复该任务。');
          state.tasks.push({...t,studentIds,projectId});deletedTasks.delete(p.id);break;
        }
        case 'chat.send': {
          scope(p);if(!p.text?.trim()&&!p.files?.length)throw new Error('请输入文字或添加文件。');
          const files=p.files||[],sizes=files.map(f=>Math.floor((f.dataBase64?.length||0)*3/4));
          if(sizes.some(n=>n>20*1024*1024)||sizes.reduce((a,b)=>a+b,0)>40*1024*1024)throw new Error('附件超出大小限制。');
          let task=null;
          if(p.completeTaskId){task=find(state.tasks,p.completeTaskId,'任务');if(!(p.scopeType==='project'?task.projectId===p.scopeId:task.studentIds.includes(p.scopeId)))throw new Error('任务不属于当前归档。');}
          state.messages.push({id:id(),scopeType:p.scopeType,scopeId:p.scopeId,role:'user',text:p.text||'',createdAt:new Date().toISOString(),attachments:files.map((f,i)=>({id:id(),name:f.name,mime:f.mime,size:sizes[i],textStatus:'浏览器预览 · 未读取文字'})),suggestions:[]});
          if(task)task.done=true;
          break;
        }
        case 'summary.retry': {const s=scope(p);s.summaryStatus='error';s.summaryError='浏览器预览不调用 GPT；请在桌面应用中生成摘要。';break;}
        case 'alarm.save': {
          required(p.title,'提醒内容');if(!Number.isFinite(new Date(p.when).getTime()))throw new Error('请选择有效的提醒时间。');
          if(!['global','student','project'].includes(p.scopeType))throw new Error('提醒对象无效。');
          if(p.scopeType==='student')find(state.students,p.studentId,'成员');
          if(p.scopeType==='project')find(state.projects,p.projectId,'项目');
          const alarm={...p,id:p.id||id(),repeat:p.repeat||'once',intervalDays:p.intervalDays||1,enabled:p.enabled!==false,...(p.repeat==='monthly'?{monthDay:Math.max(1,Math.min(31,Number(p.monthDay)||new Date(p.when).getDate())),monthTime:new Date(p.when).toLocaleTimeString('sv-SE',{hour:'2-digit',minute:'2-digit',hour12:false})}:{})};
          if(p.id)Object.assign(find(state.alarms,p.id,'提醒'),alarm);else state.alarms.push(alarm);
          if(p.scopeType==='global')remember('recentGlobalAlarmLabels',[p.title],6);break;
        }
        case 'alarm.enabled': {find(state.alarms,p.id,'提醒').enabled=!!p.enabled;break;}
        case 'alarm.skip': {
          const a=find(state.alarms,p.id,'提醒');if(!a.enabled||a.repeat==='once'||a.when!==p.when)throw new Error('提醒已有更新，请查看后重试。');
          const d=new Date(a.when);
          if(a.repeat==='monthly'){const day=a.monthDay||d.getDate();d.setDate(1);d.setMonth(d.getMonth()+1);d.setDate(Math.min(day,new Date(d.getFullYear(),d.getMonth()+1,0).getDate()));}
          else d.setDate(d.getDate()+(a.repeat==='daily'?1:a.repeat==='weekly'?7:a.intervalDays||1));
          a.when=d.toISOString();break;
        }
        case 'alarm.toggle': {const a=find(state.alarms,p.id,'提醒');a.enabled=!a.enabled;break;}
        case 'alarm.delete': state.alarms=state.alarms.filter(a=>a.id!==p.id);break;
        case 'reminder.snooze': {const a=find(state.alarms,p.id,'提醒');a.when=new Date(Date.now()+(p.minutes||10)*60000).toISOString();a.enabled=true;break;}
        case 'reminder.poll': return [];
        case 'settings.save': Object.assign(state.settings,{baseUrl:p.baseUrl||'',model:p.model||'',apiMode:p.apiMode||'responses',currentStudentId:p.currentStudentId||'',aiConfigured:false});break;
        case 'settings.detect': throw new Error('导入本机 GPT 配置需在桌面应用中操作。');
        case 'backup.export': case 'backup.import': throw new Error('完整备份与恢复需在桌面应用中操作。');
        case 'file.open': throw new Error('浏览器预览不保存附件原件；请在桌面应用中打开。');
        default: throw new Error(`浏览器预览不支持此操作：${action}`);
      }
      return dataSnapshot();
    }
  };
})();
