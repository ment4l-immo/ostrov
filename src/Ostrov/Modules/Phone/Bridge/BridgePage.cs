namespace Ostrov.Modules.Phone.Bridge;

/// <summary>Страница моста для Safari на телефоне: «На компьютер» (файлы, фото, текст) и «С компьютера» (файлы и тексты). Тёмная, в стиле острова, без внешних файлов и библиотек.</summary>
internal static class BridgePage
{
    public const string Html = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="color-scheme" content="dark">
<title>Остров</title>
<style>
:root{--bg:#000;--card:#1c1c1e;--card2:#2c2c2e;--text:#fff;--soft:#9a9a9f;--ok:#fff}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
body{margin:0;background:var(--bg);color:var(--text);font:16px/1.4 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif;padding:max(16px,env(safe-area-inset-top)) 16px max(24px,env(safe-area-inset-bottom))}
h1{font-size:28px;margin:8px 0 18px;font-weight:700}
h2{font-size:13px;margin:26px 0 10px;color:var(--soft);font-weight:600;text-transform:uppercase;letter-spacing:.06em}
.card{background:var(--card);border-radius:20px;padding:14px}
.btn{display:block;width:100%;text-align:center;background:#fff;color:#000;border:0;border-radius:14px;padding:15px;font-size:17px;font-weight:600}
.btn:active{opacity:.7}
textarea{width:100%;min-height:84px;background:var(--card2);color:var(--text);border:0;border-radius:14px;padding:12px;font:inherit;resize:vertical;margin-top:12px}
.btn2{display:block;width:100%;margin-top:10px;background:var(--card2);color:var(--text);border:0;border-radius:14px;padding:13px;font-size:16px;font-weight:600}
.btn2:active{opacity:.7}
.row{display:flex;align-items:center;gap:10px;background:var(--card2);border-radius:14px;padding:11px 13px;margin-top:8px;position:relative;overflow:hidden}
.row .n,.row a{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--text);text-decoration:none}
.row .s{color:var(--soft);font-size:14px;white-space:nowrap}
.row button{background:#fff;color:#000;border:0;border-radius:10px;padding:7px 12px;font-weight:600;font-size:14px}
.bar{position:absolute;left:0;right:0;bottom:0;height:3px}
.bar b{display:block;height:100%;width:0;background:#fff;transition:width .15s}
.row.ok .s{color:#fff}.row.err .s{color:#ff8a80}
.empty{color:var(--soft);padding:6px 2px}
.toast{position:fixed;left:50%;bottom:calc(24px + env(safe-area-inset-bottom));transform:translateX(-50%);background:#fff;color:#000;padding:10px 18px;border-radius:20px;font-weight:600;opacity:0;transition:opacity .2s;pointer-events:none}
.toast.on{opacity:1}
</style>
</head>
<body>
<h1>Остров ⇄ телефон</h1>

<h2>На компьютер</h2>
<div class="card">
  <label class="btn">Выбрать файлы или фото<input id="pick" type="file" multiple hidden></label>
  <div id="up"></div>
  <textarea id="txt" placeholder="Текст или ссылка"></textarea>
  <button class="btn2" id="sendtxt">Отправить текст</button>
</div>

<h2>С компьютера</h2>
<div class="card"><div id="list" class="empty">Загрузка…</div></div>

<div class="toast" id="toast"></div>
<script>
const base = location.pathname.replace(/\/+$/, '');
const $ = id => document.getElementById(id);
let last = '';
function fmt(n){ if(n<1024) return n+' Б'; if(n<1048576) return (n/1024).toFixed(0)+' КБ'; if(n<1073741824) return (n/1048576).toFixed(1)+' МБ'; return (n/1073741824).toFixed(2)+' ГБ'; }
function toast(m){ const t=$('toast'); t.textContent=m; t.classList.add('on'); setTimeout(()=>t.classList.remove('on'),1800); }
function addRow(name){
  const row=document.createElement('div'); row.className='row';
  const n=document.createElement('span'); n.className='n'; n.textContent=name;
  const s=document.createElement('span'); s.className='s'; s.textContent='Жду…';
  const bar=document.createElement('i'); bar.className='bar'; const b=document.createElement('b'); bar.append(b);
  row.append(n,s,bar); $('up').prepend(row); return {row,s,b};
}
function upload(file){
  const r=addRow(file.name), x=new XMLHttpRequest();
  x.open('PUT', base+'/up?name='+encodeURIComponent(file.name));
  x.upload.onprogress=e=>{ if(e.lengthComputable){ const p=Math.round(e.loaded/e.total*100); r.b.style.width=p+'%'; r.s.textContent=p+'%'; } };
  x.onload=()=>{ if(x.status===200){ r.b.style.width='100%'; r.s.textContent='Готово'; r.row.classList.add('ok'); } else { r.s.textContent='Ошибка '+x.status; r.row.classList.add('err'); } };
  x.onerror=()=>{ r.s.textContent='Нет связи'; r.row.classList.add('err'); };
  x.send(file);
}
$('pick').addEventListener('change', e=>{ for(const f of e.target.files) upload(f); e.target.value=''; });
$('sendtxt').addEventListener('click', async ()=>{
  const t=$('txt').value.trim(); if(!t) return;
  try{ const r=await fetch(base+'/text',{method:'POST',body:t}); if(r.ok){ $('txt').value=''; toast('Текст отправлен'); } else toast('Не удалось отправить'); }
  catch(e){ toast('Нет связи'); }
});
async function copyText(id,btn){
  try{
    const r=await fetch(base+'/text/'+id); const t=await r.text();
    const ta=document.createElement('textarea'); ta.value=t; ta.style.cssText='position:fixed;opacity:0;left:0;top:0';
    document.body.append(ta); ta.focus(); ta.select(); ta.setSelectionRange(0,t.length);
    let ok=false; try{ ok=document.execCommand('copy'); }catch(e){}
    ta.remove(); btn.textContent=ok?'Скопировано':'Не вышло'; setTimeout(()=>btn.textContent='Копировать',1500);
  }catch(e){ btn.textContent='Нет связи'; }
}
function render(items){
  const box=$('list'); box.textContent='';
  if(!items.length){ box.className='empty'; box.textContent='Пока ничего. Брось файл в окно моста на компьютере.'; return; }
  box.className='';
  for(const it of items){
    const row=document.createElement('div'); row.className='row';
    if(it.kind==='file'){
      const a=document.createElement('a'); a.href=base+'/file/'+it.id; a.setAttribute('download',it.name); a.textContent=it.name;
      const s=document.createElement('span'); s.className='s'; s.textContent=fmt(it.size); row.append(a,s);
    } else {
      const n=document.createElement('span'); n.className='n'; n.textContent=it.name;
      const b=document.createElement('button'); b.textContent='Копировать'; b.onclick=()=>copyText(it.id,b); row.append(n,b);
    }
    box.append(row);
  }
}
async function refresh(){
  try{ const r=await fetch(base+'/list',{cache:'no-store'}); const d=await r.json(); const s=JSON.stringify(d.items); if(s!==last){ last=s; render(d.items); } }catch(e){}
}
refresh(); setInterval(refresh,3000);
</script>
</body>
</html>
""";
}
