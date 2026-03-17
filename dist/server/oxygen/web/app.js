const statusEl = document.getElementById('status');
const pluginSelect = document.getElementById('pluginSelect');
const editor = document.getElementById('editor');
const pluginName = document.getElementById('pluginName');
const logsEl = document.getElementById('logs');

document.getElementById('refreshLogs').onclick = loadLogs;
document.getElementById('reloadBtn').onclick = reloadPlugin;
document.getElementById('deleteBtn').onclick = deletePlugin;
document.getElementById('saveBtn').onclick = savePlugin;
pluginSelect.onchange = loadSelected;

async function api(path, opts){
  const res = await fetch(path, opts);
  if(!res.ok) throw new Error(await res.text());
  return res.json();
}

async function loadStatus(){
  try{const s = await api('/api/status'); statusEl.textContent = s.status;}
  catch{statusEl.textContent = 'offline';}
}

async function loadPlugins(){
  const data = await api('/api/plugins');
  pluginSelect.innerHTML = '';
  data.plugins.forEach(p=>{
    const o = document.createElement('option');
    o.value = p; o.textContent = p; pluginSelect.appendChild(o);
  });
  if(data.plugins.length){ await loadSelected(); }
}

async function loadSelected(){
  const name = pluginSelect.value; if(!name) return;
  const data = await api('/api/plugin?name='+encodeURIComponent(name));
  editor.value = data.code;
  pluginName.value = name.replace('.cs','');
}

async function savePlugin(){
  const name = pluginName.value.trim();
  if(!name) return;
  await api('/api/plugin', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({name, code: editor.value})});
  await loadPlugins();
}

async function deletePlugin(){
  const name = pluginSelect.value; if(!name) return;
  await api('/api/plugin?name='+encodeURIComponent(name), {method:'DELETE'});
  await loadPlugins();
}

async function reloadPlugin(){
  const name = pluginSelect.value; if(!name) return;
  await api('/api/reload?name='+encodeURIComponent(name));
}

async function loadLogs(){
  const data = await api('/api/logs');
  logsEl.textContent = data.text || '';
}

(async function init(){
  await loadStatus();
  await loadPlugins();
  await loadLogs();
})();
