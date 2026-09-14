(() => {
  'use strict';
  let host=null, rpc=null, toast=()=>{}, state=null, saving=false;
  const esc=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const input=(name,label,value,type='text')=>`<label class="mobile-setting-field"><span>${label}</span><input data-mobile-field="${name}" type="${type}" value="${esc(value)}" autocomplete="off" ${type==='number'?'min="1" max="65535"':''}></label>`;
  function render() {
    if(!host?.isConnected||!state)return;
    host.innerHTML=`<h3>手机连接</h3><div class="mobile-connection-status"></div><label class="mobile-setting-check"><input data-mobile-field="enabled" type="checkbox" ${state.enabled?'checked':''}>允许手机访问</label>${input('password','连接密码',state.password)}<div class="mobile-addresses"></div><details class="mobile-forward-settings"><summary>外网转发与端口</summary><label class="mobile-setting-check"><input data-mobile-field="frpEnabled" type="checkbox" ${state.frpEnabled?'checked':''}>使用 frp 外网转发</label>${input('serverAddr','转发服务器',state.serverAddr)}<div class="mobile-setting-grid">${input('serverPort','连接端口',state.serverPort,'number')}${input('remotePort','手机访问端口',state.remotePort,'number')}</div>${input('frpToken',state.frpTokenConfigured?'转发口令（留空保留）':'转发口令','','password')}${input('listenPort','本机服务端口',state.listenPort,'number')}</details><div class="settings-buttons"><button type="button" data-mobile-act="save">保存连接</button><button type="button" data-mobile-act="restart">重新连接</button></div><div class="mobile-devices"></div>`;
    host.addEventListener('click',click,{once:false}); status();
  }
  function status() {
    if(!host?.isConnected||!state)return;
    const s=host.querySelector('.mobile-connection-status');
    if(s)s.innerHTML=`<p class="quiet-note">${esc(state.error||(state.running?(state.frpEnabled?state.frpStatus:'已开启，可在同一网络连接'):'尚未开启'))}</p>`;
    const addresses=host.querySelector('.mobile-addresses');
    const url=state.frpEnabled&&state.publicUrl?state.publicUrl:(state.lanUrls||[])[0];
    if(addresses)addresses.innerHTML=url?`<div class="mobile-address"><span>${esc(url)}</span><button type="button" data-mobile-act="copy" data-address="${esc(url)}">复制地址</button></div><button type="button" class="text-button" data-mobile-act="copy" data-address="${esc(url+'/mobile/member-progress.apk')}">复制 Android 安装包地址</button>`:'';
    const devices=host.querySelector('.mobile-devices');
    if(devices)devices.innerHTML=(state.devices||[]).length?`<h4>已登录设备</h4>${state.devices.map(d=>`<div class="mobile-device"><span>${esc(d.name)}</span><button type="button" class="text-button" data-mobile-act="revoke" data-id="${esc(d.id)}">移除</button></div>`).join('')}`:'';
  }
  function values() {
    if(!state||!host?.isConnected)return null;
    const value={};host.querySelectorAll('[data-mobile-field]').forEach(e=>value[e.dataset.mobileField]=e.type==='checkbox'?e.checked:e.type==='number'?Number(e.value):e.value);return value;
  }
  async function save() {
    if(saving)return;
    const value=values();if(!value||!state?.available)return;
    const changed=Object.entries(value).some(([k,v])=>k==='frpToken'?v.length>0:state[k]!==v);
    if(!changed)return;
    saving=true;
    try {state=await rpc('mobile.save',value);render();}
    finally {saving=false;}
  }
  async function click(e) {
    const el=e.target.closest('[data-mobile-act]');if(!el||!host?.contains(el))return;
    e.preventDefault();e.stopPropagation();
    if(el.disabled)return;el.disabled=true;
    try {
      const a=el.dataset.mobileAct;
      if(a==='save'){await save();toast('手机连接已保存');}
      if(a==='restart'){await save();state=await rpc('mobile.restart');status();}
      if(a==='copy'){await rpc('mobile.copy-address',{address:el.dataset.address});toast('地址已复制');}
      if(a==='revoke'){state=await rpc('mobile.revoke',{id:el.dataset.id});status();}
    } catch(err){toast(err.message,true);}finally{el.disabled=false;}
  }
  window.MobileSettings={
    configure(options){rpc=options.rpc;toast=options.toast||toast;},
    async mount(element){
      if(host)host.removeEventListener('click',click);host=element;state=null;
      if(!host)return;
      host.innerHTML='<h3>手机连接</h3><p class="quiet-note">正在读取连接设置…</p>';
      try {const next=await rpc('mobile.get');if(host!==element||!element.isConnected)return;state=next;render();}
      catch {if(element.isConnected)element.innerHTML='<h3>手机连接</h3><p class="quiet-note">请在 Windows 应用中配置手机连接。</p>';}
    },save
  };
  window.chrome?.webview?.addEventListener('message',e=>{const m=typeof e.data==='string'?JSON.parse(e.data):e.data;if(m?.type==='mobile'){state={...state,...m.state};status();}});
})();
