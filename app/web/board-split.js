(() => {
  'use strict';
  const storageKey='member-progress:board-ratio',initial=.62;
  let ratio=initial,gesture=null;
  try{const saved=Number(localStorage.getItem(storageKey));if(saved>.05&&saved<.95)ratio=saved;}catch{}
  const bounds=board=>({width:board.clientWidth,min:Math.min(280,board.clientWidth*.58),max:board.clientWidth-Math.min(150,board.clientWidth*.32)});
  const notify=()=>document.dispatchEvent(new Event('boardratiochange'));
  function persist(){try{localStorage.setItem(storageKey,String(ratio));}catch{}}
  function fit(board){
    const b=bounds(board);if(!b.width)return;
    const left=Math.max(b.min,Math.min(b.max,b.width*ratio));
    board.style.setProperty('--info',`${Math.round(left)}px`);
    let handle=board.querySelector('.board-divider');
    if(!handle){
      handle=document.createElement('div');handle.className='board-divider';handle.tabIndex=0;
      handle.setAttribute('role','separator');handle.setAttribute('aria-orientation','vertical');
      handle.setAttribute('aria-label','调整成员进展与甘特图比例');
      handle.title='拖动调整比例；左右方向键微调，双击恢复默认';board.append(handle);
    }
    handle.setAttribute('aria-valuemin',String(Math.round(b.min/b.width*100)));
    handle.setAttribute('aria-valuemax',String(Math.round(b.max/b.width*100)));
    handle.setAttribute('aria-valuenow',String(Math.round(left/b.width*100)));
    handle.setAttribute('aria-valuetext',`成员进展 ${Math.round(left/b.width*100)}%`);
  }
  function setRatio(board,value){const b=bounds(board);ratio=Math.max(b.min/b.width,Math.min(b.max/b.width,value));notify();}
  function finish(cancel=false){
    if(!gesture)return;const old=gesture;gesture=null;
    if(cancel)ratio=old.start;
    document.body.classList.remove('board-resizing');
    try{old.handle.releasePointerCapture(old.pointerId);}catch{}
    persist();notify();document.dispatchEvent(new Event('boardresized'));
  }
  document.addEventListener('pointerdown',e=>{
    const handle=e.target.closest('.board-divider');if(!handle||e.button!==0)return;
    e.preventDefault();const board=handle.closest('.board');
    gesture={board,handle,pointerId:e.pointerId,start:ratio,offset:e.clientX-board.getBoundingClientRect().left-parseFloat(board.style.getPropertyValue('--info'))};
    handle.setPointerCapture(e.pointerId);document.body.classList.add('board-resizing');
  });
  document.addEventListener('pointermove',e=>{
    if(!gesture||e.pointerId!==gesture.pointerId)return;e.preventDefault();
    setRatio(gesture.board,(e.clientX-gesture.board.getBoundingClientRect().left-gesture.offset)/gesture.board.clientWidth);
  });
  document.addEventListener('pointerup',e=>{if(gesture&&e.pointerId===gesture.pointerId)finish();});
  document.addEventListener('pointercancel',e=>{if(gesture&&e.pointerId===gesture.pointerId)finish(true);});
  document.addEventListener('lostpointercapture',e=>{if(gesture&&e.pointerId===gesture.pointerId)finish(true);});
  document.addEventListener('keydown',e=>{
    if(gesture&&e.key==='Escape'){e.preventDefault();finish(true);return;}
    const handle=e.target.closest('.board-divider');if(!handle)return;
    const board=handle.closest('.board');
    if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;e.preventDefault();
    const current=parseFloat(board.style.getPropertyValue('--info'))/board.clientWidth;
    setRatio(board,e.key==='Home'?0:e.key==='End'?1:current+(e.key==='ArrowLeft'?-1:1)*(e.shiftKey ? .05 : .01));persist();
  });
  document.addEventListener('dblclick',e=>{if(e.target.closest('.board-divider')){ratio=initial;persist();notify();}});
  window.addEventListener('blur',()=>finish(true));
  window.BoardSplit=Object.freeze({fit,get dragging(){return !!gesture;}});
})();
