const {useState,useRef,useEffect} = React;
const html = htm.bind(React.createElement);

const initialCats = [
  {id:'current',name:'当前项目',color:'#126FF7',open:true,files:[
    {id:'f1',name:'品牌视觉.psd',icon:'🎨'},{id:'f2',name:'需求清单.docx',icon:'📄'},{id:'f3',name:'DeskZone',icon:'📁'},{id:'f4',name:'原型草图.png',icon:'🖼️'}]},
  {id:'todo',name:'待处理',color:'#31CDEE',open:true,files:[
    {id:'f5',name:'截图 0916.png',icon:'🖼️'},{id:'f6',name:'会议资料.pdf',icon:'📕'},{id:'f7',name:'素材包.zip',icon:'🗜️'}]},
  {id:'common',name:'常用',color:'#76A7FF',open:true,files:[
    {id:'f8',name:'项目资料',icon:'📁'},{id:'f9',name:'Chrome',icon:'🌐'},{id:'f10',name:'Figma',icon:'◆'}]},
  {id:'done',name:'已完成',color:'#7DC7B0',open:false,files:[
    {id:'f11',name:'旧版方案.pdf',icon:'📕'},{id:'f12',name:'归档素材',icon:'📁'}]}
];

function BrandMark(){return html`<span class="brand-mark"><i></i><i></i><i></i></span>`}

function Toggle({on,setOn}){return html`<div class=${'switch '+(on?'on':'')} onClick=${()=>setOn(!on)}><i></i></div>`}

function Settings({theme,setTheme,opacity,setOpacity,onClose,snap,setSnap}){
  const [section,setSection]=useState('外观');
  const sections=['常规','面板','分类与文件','拖放行为','快捷键','外观','备份','关于'];
  return html`<div class="settings-backdrop" onMouseDown=${e=>e.target===e.currentTarget&&onClose()}>
    <div class="settings">
      <aside class="settings-nav">
        <div class="settings-logo"><${BrandMark}/> 桌序 DeskZone</div>
        ${sections.map(s=>html`<div class=${'nav-item '+(section===s?'active':'')} onClick=${()=>setSection(s)}>${s}</div>`)}
      </aside>
      <main class="settings-main">
        <div class="settings-head"><h2>${section}</h2><button class="icon-btn" onClick=${onClose}>✕</button></div>
        ${section==='外观' && html`
          <div class="setting-row"><div class="meta"><strong>深色模式</strong><span>切换 DeskZone 的浅色 / 深色主题</span></div><${Toggle} on=${theme==='dark'} setOn=${v=>setTheme(v?'dark':'light')}/></div>
          <div class="setting-row"><div class="meta"><strong>面板透明度</strong><span>调整桌面面板的视觉透明程度</span></div><input class="range" type="range" min="72" max="100" value=${opacity} onChange=${e=>setOpacity(+e.target.value)}/></div>
          <div class="setting-row"><div class="meta"><strong>品牌色</strong><span>主蓝 #126FF7 · 亮蓝 #1DA4F4 · 青色 #31CDEE</span></div><div style=${{display:'flex',gap:'6px'}}><i style=${{width:'22px',height:'22px',borderRadius:'50%',background:'#126FF7'}}></i><i style=${{width:'22px',height:'22px',borderRadius:'50%',background:'#1DA4F4'}}></i><i style=${{width:'22px',height:'22px',borderRadius:'50%',background:'#31CDEE'}}></i></div></div>
        `}
        ${section==='面板' && html`
          <div class="setting-row"><div class="meta"><strong>靠边吸附</strong><span>距离屏幕边缘较近时轻微吸附</span></div><${Toggle} on=${snap} setOn=${setSnap}/></div>
          <div class="setting-row"><div class="meta"><strong>默认尺寸</strong><span>380 × 650 px 原型尺寸，对应约 360 × 520 DIP 设计</span></div><span style=${{fontSize:'12px',color:'var(--text-2)'}}>标准</span></div>
        `}
        ${!['外观','面板'].includes(section) && html`
          <div style=${{padding:'28px',border:'1px dashed var(--border)',borderRadius:'12px',color:'var(--text-2)',fontSize:'13px',textAlign:'center'}}>
            ${section}设置将在正式客户端中实现；原型重点展示桌面面板的核心交互。
          </div>`}
      </main>
    </div>
  </div>`
}

function App(){
  const [theme,setTheme]=useState('light');
  const [cats,setCats]=useState(initialCats);
  const [locked,setLocked]=useState(false);
  const [collapsed,setCollapsed]=useState(false);
  // Standalone hide toggle removed from the prototype surface.
  const [settings,setSettings]=useState(false);
  const [view,setView]=useState('grid');
  const [selected,setSelected]=useState(null);
  const [editing,setEditing]=useState(null);
  const [dragging,setDragging]=useState(null);
  const [overCat,setOverCat]=useState(null);
  const [toast,setToast]=useState('');
  const [opacity,setOpacity]=useState(92);
  const [snap,setSnap]=useState(true);
  const [pos,setPos]=useState({x:Math.max(130,window.innerWidth-460),y:92});
  const [size,setSize]=useState({w:380,h:650});
  const panelRef=useRef(null);
  const moveRef=useRef(null);
  const resizeRef=useRef(null);

  useEffect(()=>{document.documentElement.dataset.theme=theme},[theme]);
  useEffect(()=>{if(!toast)return;const t=setTimeout(()=>setToast(''),1800);return()=>clearTimeout(t)},[toast]);
  useEffect(()=>{
    const onMove=e=>{
      if(moveRef.current&&!locked){
        let nx=e.clientX-moveRef.current.dx, ny=e.clientY-moveRef.current.dy;
        if(snap){if(nx<18)nx=10;if(ny<18)ny=10;if(window.innerWidth-(nx+size.w)<18)nx=window.innerWidth-size.w-10;if(window.innerHeight-(ny+48)<18)ny=window.innerHeight-58}
        setPos({x:Math.max(0,nx),y:Math.max(0,ny)});
      }
      if(resizeRef.current&&!locked){
        setSize({w:Math.max(300,Math.min(560,e.clientX-resizeRef.current.x)),h:Math.max(430,Math.min(window.innerHeight-pos.y-10,e.clientY-resizeRef.current.y))});
      }
    };
    const onUp=()=>{moveRef.current=null;resizeRef.current=null};
    window.addEventListener('mousemove',onMove);window.addEventListener('mouseup',onUp);
    return()=>{window.removeEventListener('mousemove',onMove);window.removeEventListener('mouseup',onUp)};
  },[locked,snap,size.w,pos.y]);

  const showToast=t=>setToast(t);
  const toggleCat=id=>setCats(c=>c.map(x=>x.id===id?{...x,open:!x.open}:x));
  const renameCat=(id,name)=>{if(!name.trim())return;setCats(c=>c.map(x=>x.id===id?{...x,name:name.trim()}:x));setEditing(null);showToast('分类名称已保存')};
  const addCategory=()=>{const id='cat'+Date.now();setCats(c=>[...c,{id,name:'新分类',color:'#89BCE8',open:true,files:[]}]);setEditing(id);showToast('已创建新分类')};
  const deleteCategory=id=>{setCats(c=>c.filter(x=>x.id!==id));showToast('分类已移除（原型不删除真实文件）')};
  const moveFile=(fileId,fromId,toId)=>{
    if(fromId===toId)return;
    let moving=null;
    const next=cats.map(c=>{if(c.id===fromId){moving=c.files.find(f=>f.id===fileId);return{...c,files:c.files.filter(f=>f.id!==fileId)}}return c});
    if(!moving)return;
    setCats(next.map(c=>c.id===toId?{...c,files:[...c.files,moving]}:c));
    showToast('已移动到 '+cats.find(c=>c.id===toId)?.name);
  };
  const onDrop=(e,catId)=>{e.preventDefault();setOverCat(null);if(dragging){moveFile(dragging.fileId,dragging.catId,catId);setDragging(null);return}if(e.dataTransfer.files?.length){showToast(`模拟添加 ${e.dataTransfer.files.length} 个文件到 ${cats.find(c=>c.id===catId)?.name}`)}};

  const panelStyle={left:pos.x+'px',top:pos.y+'px',width:size.w+'px',height:collapsed?'48px':size.h+'px',background:`color-mix(in srgb, var(--surface) ${opacity}%, transparent)`};

  return html`<div class="prototype" onClick=${()=>setSelected(null)}>
    <div class="desktop-icons">
      ${null}
      <!-- 项目资料桌面图标已按评审删除 -->
      <!-- 截图桌面图标已按评审删除 -->
    </div>
    ${null}

    <section ref=${panelRef} class=${'panel '+(locked?'locked ':'')+(collapsed?'collapsed ':'')} style=${panelStyle} onClick=${e=>e.stopPropagation()}>
      <header class="titlebar" onMouseDown=${e=>{if(e.button!==0||locked)return;moveRef.current={dx:e.clientX-pos.x,dy:e.clientY-pos.y}}}>
        <div class="titlebrand"><${BrandMark}/><span>桌序 <small>DeskZone</small></span></div>
        <div class="title-spacer"></div>
        <button title="锁定位置" class=${'icon-btn '+(locked?'active':'')} onMouseDown=${e=>e.stopPropagation()} onClick=${()=>{setLocked(!locked);showToast(!locked?'已锁定面板位置':'已解锁面板')}}>${locked?'🔒':'🔓'}</button>
        <button title="折叠" class="icon-btn" onMouseDown=${e=>e.stopPropagation()} onClick=${()=>setCollapsed(!collapsed)}>${collapsed?'▾':'—'}</button>
        <button title="设置" class="icon-btn" onMouseDown=${e=>e.stopPropagation()} onClick=${()=>setSettings(true)}>⚙</button>
      </header>

      ${!collapsed && html`<div class="panel-body">
        <div class="toolbar">
          <button class="add-btn" onClick=${addCategory}>＋ 新建分类</button>
          <div class="view-toggle"><button class=${view==='grid'?'active':''} onClick=${()=>setView('grid')}>▦</button><button class=${view==='list'?'active':''} onClick=${()=>setView('list')}>☷</button></div>
        </div>
        ${cats.map(cat=>html`<div key=${cat.id} class=${'category '+(overCat===cat.id?'drag-over':'')} onDragOver=${e=>{e.preventDefault();setOverCat(cat.id)}} onDragLeave=${()=>setOverCat(null)} onDrop=${e=>onDrop(e,cat.id)}>
          <div class="cat-head">
            <span class="chev" onClick=${()=>toggleCat(cat.id)}>${cat.open?'▾':'▸'}</span>
            <span class="dot" style=${{background:cat.color}}></span>
            <div class="cat-title" onDoubleClick=${()=>setEditing(cat.id)}>
              ${editing===cat.id?html`<input autoFocus defaultValue=${cat.name} onBlur=${e=>renameCat(cat.id,e.target.value)} onKeyDown=${e=>{if(e.key==='Enter')renameCat(cat.id,e.currentTarget.value);if(e.key==='Escape')setEditing(null)}}/>`:cat.name}
            </div>
            <span class="count">${cat.files.length}</span>
            <button class="icon-btn cat-menu" onClick=${()=>editing===cat.id?setEditing(null):setEditing(cat.id)}>⋯</button>
          </div>
          ${cat.open && html`
            <div class=${'files '+(view==='list'?'list':'')}>
              ${cat.files.map(file=>html`<div key=${file.id} draggable="true" class=${'file '+(selected===file.id?'selected':'')} title=${file.name}
                onClick=${e=>{e.stopPropagation();setSelected(file.id)}}
                onDoubleClick=${()=>showToast('原型：打开 '+file.name)}
                onDragStart=${e=>{setDragging({fileId:file.id,catId:cat.id});e.dataTransfer.effectAllowed='move'}}
                onDragEnd=${()=>{setDragging(null);setOverCat(null)}}>
                <div class="ficon">${file.icon}</div><div class="fname">${file.name}</div>
              </div>`)}
            </div>
            <div class="drop-hint">放入「${cat.name}」</div>
          `}
          ${editing===cat.id && html`<div class="context" style=${{right:'18px',marginTop:'-4px'}}>
            <button onClick=${()=>setEditing(cat.id)}>重命名</button>
            <button onClick=${()=>{setCats(c=>c.map(x=>x.id===cat.id?{...x,open:!x.open}:x));setEditing(null)}}>${cat.open?'折叠分类':'展开分类'}</button>
            <button class="danger" onClick=${()=>deleteCategory(cat.id)}>删除分类</button>
          </div>`}
        </div>`)}
      </div>`}
      ${!locked&&!collapsed&&html`<div class="resize-grip" onMouseDown=${e=>{e.stopPropagation();resizeRef.current={x:pos.x,y:pos.y}}}></div>`}
    </section>

    ${null}

    ${null}
    ${toast&&html`<div class="toast">${toast}</div>`}
    ${settings&&html`<${Settings} theme=${theme} setTheme=${setTheme} opacity=${opacity} setOpacity=${setOpacity} snap=${snap} setSnap=${setSnap} onClose=${()=>setSettings(false)}/>`}
  </div>`
}

ReactDOM.createRoot(document.getElementById('root')).render(html`<${App}/>`);