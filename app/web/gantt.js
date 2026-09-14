/* Calendar/layout calculations are independent of rendering and persistence. */
(() => {
  'use strict';
  const key = d => `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`;
  const parse = s => /^\d{4}-\d{2}-\d{2}$/.test(s||'') ? new Date(`${s}T00:00:00`) : new Date(NaN);
  const ordinal = s => { const d=parse(s); return Date.UTC(d.getFullYear(),d.getMonth(),d.getDate())/86400000; };
  const cache = new WeakMap();

  function packTasks(tasks, range) {
    const first=ordinal(range.start),last=first+range.days-1;
    const entries=tasks.map(task=>{
      let start=ordinal(task.start||task.due),end=ordinal(task.due||task.start);
      if(!Number.isFinite(start)||!Number.isFinite(end))return null;
      if(end<start)[start,end]=[end,start];
      if(end<first||start>last)return null;
      const offset=Math.max(first,start)-first,span=Math.min(last,end)-Math.max(first,start)+1;
      return {task,offset,span};
    }).filter(Boolean).sort((a,b)=>a.offset-b.offset||b.span-a.span||String(a.task.id).localeCompare(String(b.task.id)));
    const lanes=[],ends=[];
    for(const entry of entries){
      let lane=ends.findIndex(end=>end<=entry.offset);
      if(lane<0){lane=lanes.length;lanes.push([]);}
      lanes[lane].push(entry);ends[lane]=entry.offset+entry.span;
    }
    return {lanes,laneCount:lanes.length};
  }

  function occurrences(alarm,range) {
    const stamp=JSON.stringify([range.start,range.days,alarm.enabled,alarm.when,alarm.repeat,alarm.intervalDays,alarm.monthDay,alarm.monthTime,alarm.monthlyResumeWhen,alarm.lastTriggeredWhen]);
    const previous=cache.get(alarm);if(previous?.stamp===stamp)return previous.items;
    const first=parse(range.start),end=new Date(first);end.setDate(end.getDate()+range.days);
    const items=[],seen=new Set();
    const add=(d,fired=false)=>{
      if(!Number.isFinite(+d)||d<first||d>=end||seen.has(+d))return;
      seen.add(+d);items.push({when:d.toISOString(),fired});
    };
    if(alarm.lastTriggeredWhen)add(new Date(alarm.lastTriggeredWhen),true);
    const due=new Date(alarm.when);
    if(alarm.enabled&&Number.isFinite(+due)){
      add(due);
      if(alarm.repeat==='monthly'){
        const anchor=Math.max(1,Math.min(31,Number(alarm.monthDay)||due.getDate()));
        const time=String(alarm.monthTime||'').match(/^(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?$/);
        const clock=time?[Number(time[1]),Number(time[2]),Number(time[3]||0),Number((time[4]||'').padEnd(3,'0').slice(0,3))]:[due.getHours(),due.getMinutes(),due.getSeconds(),due.getMilliseconds()];
        const resume=new Date(alarm.monthlyResumeWhen||alarm.when),regular=Number.isFinite(+resume)?resume:due;
        if(regular>due)add(regular);
        let month=new Date(regular.getFullYear(),regular.getMonth()+1,1);
        if(month<new Date(first.getFullYear(),first.getMonth(),1))month=new Date(first.getFullYear(),first.getMonth(),1);
        for(let i=0;i<10000&&month<end;i++,month.setMonth(month.getMonth()+1)){
          const day=Math.min(anchor,new Date(month.getFullYear(),month.getMonth()+1,0).getDate());
          const next=new Date(month.getFullYear(),month.getMonth(),day,...clock);
          if(next>due)add(next);
        }
      }else if(['daily','weekly','interval'].includes(alarm.repeat)){
        const step=alarm.repeat==='daily'?1:alarm.repeat==='weekly'?7:Math.max(1,Math.min(3650,Number(alarm.intervalDays)||1));
        const skip=Math.max(1,Math.floor((ordinal(range.start)-ordinal(key(due)))/step));
        const next=new Date(due);next.setDate(next.getDate()+skip*step);
        for(let i=0;i<10000&&next<end;i++,next.setDate(next.getDate()+step))add(next);
      }
    }
    items.sort((a,b)=>new Date(a.when)-new Date(b.when));cache.set(alarm,{stamp,items});return items;
  }

  function reminderDays(alarms,projects,studentId,range) {
    const memberProjects=new Set(projects.filter(p=>(p.studentIds||[]).includes(studentId)).map(p=>p.id));
    const days=new Map();
    for(const alarm of alarms){
      const type=alarm.scopeType||'global';
      if(type!=='global'&&!(type==='student'&&alarm.studentId===studentId)&&!(type==='project'&&memberProjects.has(alarm.projectId)))continue;
      for(const item of occurrences(alarm,range)){
        const date=key(new Date(item.when));
        if(!days.has(date))days.set(date,{date,offset:ordinal(date)-ordinal(range.start),alarms:[]});
        days.get(date).alarms.push({alarm,when:item.when,scopeType:type,fired:item.fired});
      }
    }
    for(const day of days.values())day.alarms.sort((a,b)=>new Date(a.when)-new Date(b.when)||String(a.alarm.id).localeCompare(String(b.alarm.id)));
    return [...days.values()].sort((a,b)=>a.offset-b.offset);
  }
  globalThis.GanttLayout=Object.freeze({packTasks,reminderDays});
})();
