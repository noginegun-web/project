const state = {
  servers: [],
  currentServerId: '',
  players: [],
  chat: [],
  squads: [],
  kills: [],
  economy: { topMoney: [], topFame: [] },
  mapData: null,
  mapSelection: null,
  mapView: {
    scale: 1,
    offsetX: 0,
    offsetY: 0,
    dragging: false,
    pointerId: null,
    lastX: 0,
    lastY: 0
  },
  items: [],
  categories: [],
  selectedItem: null,
  selectedPlayer: null,
  activePage: 'servers',
  settings: {
    host: '',
    apiKey: ''
  }
};

const els = {
  serverHost: document.getElementById('serverHost'),
  apiKey: document.getElementById('apiKey'),
  connectBtn: document.getElementById('connectBtn'),
  serverSelect: document.getElementById('serverSelect'),
  serverStatusDot: document.getElementById('serverStatusDot'),
  serverStatusText: document.getElementById('serverStatusText'),
  rconStatus: document.getElementById('rconStatus'),
  playersCount: document.getElementById('playersCount'),
  breadcrumbs: document.getElementById('breadcrumbs'),
  refreshCurrent: document.getElementById('refreshCurrent'),
  serverList: document.getElementById('serverList'),
  createServerBtn: document.getElementById('createServerBtn'),
  playersSummary: document.getElementById('playersSummary'),
  playersList: document.getElementById('playersList'),
  playersSearch: document.getElementById('playersSearch'),
  playersSort: document.getElementById('playersSort'),
  refreshPlayers: document.getElementById('refreshPlayers'),
  refreshMap: document.getElementById('refreshMap'),
  mapZoomIn: document.getElementById('mapZoomIn'),
  mapZoomOut: document.getElementById('mapZoomOut'),
  mapReset: document.getElementById('mapReset'),
  mapPlayersToggle: document.getElementById('mapPlayersToggle'),
  mapVehiclesToggle: document.getElementById('mapVehiclesToggle'),
  mapChestsToggle: document.getElementById('mapChestsToggle'),
  mapFlagsToggle: document.getElementById('mapFlagsToggle'),
  mapMeta: document.getElementById('mapMeta'),
  mapViewport: document.getElementById('mapViewport'),
  mapScene: document.getElementById('mapScene'),
  mapImage: document.getElementById('mapImage'),
  mapMarkers: document.getElementById('mapMarkers'),
  mapLayerStats: document.getElementById('mapLayerStats'),
  mapSelection: document.getElementById('mapSelection'),
  squadsList: document.getElementById('squadsList'),
  refreshSquads: document.getElementById('refreshSquads'),
  chatList: document.getElementById('chatList'),
  refreshChat: document.getElementById('refreshChat'),
  killsList: document.getElementById('killsList'),
  refreshKills: document.getElementById('refreshKills'),
  refreshEconomy: document.getElementById('refreshEconomy'),
  economyMoney: document.getElementById('economyMoney'),
  economyFame: document.getElementById('economyFame'),
  chatChannel: document.getElementById('chatChannel'),
  chatSearch: document.getElementById('chatSearch'),
  chatSendChannel: document.getElementById('chatSendChannel'),
  chatMessage: document.getElementById('chatMessage'),
  sendChat: document.getElementById('sendChat'),
  pluginSelect: document.getElementById('pluginSelect'),
  pluginName: document.getElementById('pluginName'),
  editor: document.getElementById('editor'),
  reloadBtn: document.getElementById('reloadBtn'),
  deleteBtn: document.getElementById('deleteBtn'),
  saveBtn: document.getElementById('saveBtn'),
  downloadPluginBtn: document.getElementById('downloadPluginBtn'),
  logs: document.getElementById('logs'),
  refreshLogs: document.getElementById('refreshLogs'),
  commandModal: document.getElementById('commandModal'),
  commandInput: document.getElementById('commandInput'),
  commandTitle: document.getElementById('commandTitle'),
  sendCommand: document.getElementById('sendCommand'),
  spawnModal: document.getElementById('spawnModal'),
  itemSearch: document.getElementById('itemSearch'),
  itemCategories: document.getElementById('itemCategories'),
  itemGrid: document.getElementById('itemGrid'),
  itemSelected: document.getElementById('itemSelected'),
  spawnItem: document.getElementById('spawnItem'),
  spawnQty: document.getElementById('spawnQty'),
  spawnItemBtn: document.getElementById('spawnItemBtn'),
  toast: document.getElementById('toast')
};

const navLinks = Array.from(document.querySelectorAll('.nav-link'));
const pages = Array.from(document.querySelectorAll('.page'));
const storageKeys = {
  host: 'scum.panel.host',
  apiKey: 'scum.panel.key'
};

function notify(text) {
  els.toast.textContent = text;
  els.toast.classList.add('show');
  setTimeout(() => els.toast.classList.remove('show'), 2400);
}

async function api(path, opts) {
  try {
    const base = apiBase();
    if (!base) {
      notify('Укажи IP:PORT сервера и нажми "Подключиться"');
      throw new Error('server not configured');
    }
    const headers = Object.assign({}, opts && opts.headers ? opts.headers : {});
    if (state.settings.apiKey) headers['X-API-KEY'] = state.settings.apiKey;
    if (opts && opts.body && !headers['Content-Type']) headers['Content-Type'] = 'application/json';
    const res = await fetch(base + path, Object.assign({}, opts, { headers, mode: 'cors' }));
    if (!res.ok) throw new Error(await res.text());
    return await res.json();
  } catch (err) {
    notify(err.message || 'Ошибка API');
    throw err;
  }
}

function apiBase() {
  let host = (state.settings.host || '').trim();
  if (!host) return '';
  if (!/^https?:\/\//i.test(host)) host = 'http://' + host;
  return host.replace(/\/+$/, '');
}

function loadSettings() {
  state.settings.host = localStorage.getItem(storageKeys.host) || '';
  state.settings.apiKey = localStorage.getItem(storageKeys.apiKey) || '';
  if (!state.settings.host && typeof location !== 'undefined' && location.origin && location.origin !== 'null') {
    state.settings.host = location.origin;
  }
  if (els.serverHost) els.serverHost.value = state.settings.host;
  if (els.apiKey) els.apiKey.value = state.settings.apiKey;
}

function saveSettings() {
  state.settings.host = (els.serverHost ? els.serverHost.value : '').trim();
  state.settings.apiKey = (els.apiKey ? els.apiKey.value : '').trim();
  localStorage.setItem(storageKeys.host, state.settings.host);
  localStorage.setItem(storageKeys.apiKey, state.settings.apiKey);
}

function currentServer() {
  return els.serverSelect.value || state.currentServerId;
}

function setActivePage(page) {
  state.activePage = page;
  pages.forEach(p => p.classList.toggle('active', p.dataset.page === page));
  navLinks.forEach(n => n.classList.toggle('active', n.dataset.page === page));
  const titleMap = {
    servers: 'Серверы',
    players: 'Игроки',
    map: 'Карта',
    squads: 'Отряды',
    chat: 'Чат',
    kills: 'Убийства',
    economy: 'Экономика',
    plugins: 'Плагины',
    downloads: 'Загрузки',
    logs: 'Логи'
  };
  els.breadcrumbs.textContent = `Платформа / ${titleMap[page] || 'Серверы'}`;
}

function updateStatus(online) {
  els.serverStatusText.textContent = online ? 'в сети' : 'не в сети';
  els.serverStatusDot.classList.toggle('online', online);
}

function humanizePlayerSource(source) {
  switch (String(source || '').toLowerCase()) {
    case 'rcon':
      return 'RCON';
    case 'db':
      return 'База';
    case 'native':
      return 'Хуки';
    default:
      return source ? String(source) : '—';
  }
}

function humanizeChannel(channel) {
  switch (String(channel || '').toLowerCase()) {
    case 'global':
      return 'Глобальный';
    case 'local':
      return 'Локальный';
    case 'squad':
      return 'Отряд';
    case 'admin':
      return 'Админ';
    default:
      return channel || 'Глобальный';
  }
}

function humanizeMapType(type) {
  switch (String(type || '').toLowerCase()) {
    case 'player':
    case 'players':
      return 'Игрок';
    case 'vehicle':
    case 'vehicles':
      return 'Транспорт';
    case 'chest':
    case 'chests':
      return 'Сундук';
    case 'flag':
    case 'flags':
      return 'Флаг';
    default:
      return type ? String(type) : 'Объект';
  }
}

function normalizeServers(raw) {
  if (!Array.isArray(raw)) return [];
  return raw.map(s => (typeof s === 'string' ? { id: s } : s));
}

async function loadServers() {
  if (!apiBase()) {
    updateStatus(false);
    renderServers();
    return;
  }
  try {
    const data = await api('/api/servers');
    state.servers = normalizeServers(data.servers || []);
    els.serverSelect.innerHTML = '';
    state.servers.forEach(s => {
      const opt = document.createElement('option');
      opt.value = s.id;
      opt.textContent = s.name ? `${s.name} (${s.id})` : s.id;
      els.serverSelect.appendChild(opt);
    });
    if (!state.servers.length) {
      updateStatus(false);
      renderServers();
      return;
    }
    if (!state.currentServerId || !state.servers.find(s => s.id === state.currentServerId)) {
      state.currentServerId = state.servers[0].id;
    }
    els.serverSelect.value = state.currentServerId;
    updateStatus(true);
    renderServers();
    await loadStatus();
    await loadPlayers();
    await loadPlugins();
    await loadChat();
    await loadSquads();
    await loadKills();
    await loadEconomy();
    await loadMap();
  } catch {
    updateStatus(false);
    renderServers();
  }
}

async function loadStatus() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/status?serverId=${encodeURIComponent(serverId)}`);
  els.rconStatus.textContent = `RCON: ${data.rcon ? 'включен' : 'выключен'}`;
  els.playersCount.textContent = `Игроки: ${data.players ?? 0}${data.playerSource ? ` / ${humanizePlayerSource(data.playerSource)}` : ''}`;
}

async function loadEconomy() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/economy?serverId=${encodeURIComponent(serverId)}`);
  state.economy = {
    topMoney: data.topMoney || [],
    topFame: data.topFame || []
  };
  renderEconomy();
}

function renderEconomy() {
  const renderTable = (rows) => {
    if (!rows.length) return '<div class="muted">Нет данных.</div>';
    const header = `<div class="table-row header">
      <div>#</div><div>Игрок</div><div>SteamID</div><div>Слава</div><div>Деньги</div>
    </div>`;
    const body = rows.slice(0, 50).map((r, i) => `
      <div class="table-row">
        <div>${i + 1}</div>
        <div>${r.name || 'Неизвестно'}</div>
        <div class="muted">${r.steamId || ''}</div>
        <div>${r.famePoints ?? 0}</div>
        <div>${r.moneyBalance ?? 0}</div>
      </div>
    `).join('');
    return header + body;
  };
  if (els.economyMoney) els.economyMoney.innerHTML = renderTable(state.economy.topMoney || []);
  if (els.economyFame) els.economyFame.innerHTML = renderTable(state.economy.topFame || []);
}

function renderServers() {
  els.serverList.innerHTML = '';
  if (!state.servers.length) {
    els.serverList.innerHTML = '<div class="card">Серверы не подключены.</div>';
    return;
  }
  state.servers.forEach(server => {
    const card = document.createElement('div');
    card.className = 'card server-card';
    const badgeClass = 'badge' + (server.id === currentServer() ? '' : '');
    const token = server.token || 'нет токена';
    card.innerHTML = `
      <div class="server-meta">
        <div>
          <div class="server-name">${server.id}</div>
          <div class="muted">${server.version ? `v${server.version}` : 'версия неизвестна'}</div>
        </div>
        <span class="${badgeClass}">${server.id === currentServer() ? 'подключен' : 'доступен'}</span>
      </div>
      <div class="token-row">
        <div class="token" data-token="${token}">${maskToken(token)}</div>
        <button class="btn ghost" data-action="toggle-token">Показать</button>
        <button class="btn ghost" data-action="copy-token">Скопировать</button>
      </div>
      <div class="row">
        <button class="btn ghost" data-action="players">Игроки</button>
        <button class="btn ghost" data-action="command">Команда</button>
        <button class="btn ghost" data-action="plugins">Плагины</button>
      </div>
    `;
    card.querySelector('[data-action="players"]').onclick = () => {
      setActivePage('players');
      loadPlayers();
    };
    card.querySelector('[data-action="plugins"]').onclick = () => {
      setActivePage('plugins');
      loadPlugins();
    };
    card.querySelector('[data-action="command"]').onclick = () => openCommandModal('');
    card.querySelector('[data-action="toggle-token"]').onclick = (e) => {
      const tokenEl = card.querySelector('.token');
      const shown = tokenEl.textContent === token;
      tokenEl.textContent = shown ? maskToken(token) : token;
      e.target.textContent = shown ? 'Показать' : 'Скрыть';
    };
    card.querySelector('[data-action="copy-token"]').onclick = () => {
      navigator.clipboard.writeText(token).then(() => notify('Токен скопирован'));
    };
    els.serverList.appendChild(card);
  });
}

function maskToken(token) {
  if (!token || token.length < 6) return 'нет токена';
  return token.slice(0, 6) + '•'.repeat(Math.max(4, token.length - 6));
}

function escapeHtml(text) {
  return String(text ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function formatNumber(value) {
  const num = Number(value || 0);
  return Number.isFinite(num) ? num.toLocaleString('ru-RU') : '0';
}

function formatHours(seconds) {
  const num = Number(seconds || 0);
  if (!Number.isFinite(num) || num <= 0) return '0 ч';
  return `${Math.round(num / 3600).toLocaleString('ru-RU')} ч`;
}

function defaultMapData() {
  return {
    background: {
      url: 'https://scum-map.com/images/interactive_map/scum/island.jpg',
      sourceUrl: 'https://scum-map.com/en/map/',
      name: 'Остров'
    },
    bounds: {
      minX: -905369.6875,
      maxX: 619646.5625,
      minY: -904357.625,
      maxY: 619659.75,
      invertX: true,
      invertY: true
    },
    updatedAt: '',
    counts: {
      players: 0,
      vehicles: 0,
      chests: 0,
      flags: 0
    },
    layers: {
      players: [],
      vehicles: [],
      chests: [],
      flags: []
    }
  };
}

function formatStamp(value) {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return escapeHtml(value);
  return date.toLocaleString('ru-RU');
}

function hasKnownLocation(player) {
  const loc = player && player.location;
  if (!loc) return false;
  return [loc.x, loc.y, loc.z].some(v => Number(v || 0) !== 0);
}

function formatLocation(player) {
  if (!hasKnownLocation(player)) return 'Нет live-координат';
  return [player.location.x, player.location.y, player.location.z]
    .map(v => Math.round(Number(v || 0)).toLocaleString('ru-RU'))
    .join(', ');
}

function getPlayerInitials(player) {
  const parts = String(player?.name || 'Player')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2);
  return (parts.map(p => p[0]).join('') || 'P').toUpperCase();
}

function getPlayerSearchBlob(player) {
  return [
    player.name,
    player.steamId,
    player.fakeName,
    player.ipAddress,
    player.authorityIp,
    player.authorityName,
    player.prisonerId,
    player.itemInHands,
    ...(Array.isArray(player.quickAccessItems) ? player.quickAccessItems : [])
  ].join(' ').toLowerCase();
}

function getPlayerSortValue(player, sortBy) {
  switch (sortBy) {
    case 'money':
      return Number(player.money || 0);
    case 'fame':
      return Number(player.famePoints || 0);
    case 'playtime':
      return Number(player.playTime || 0);
    case 'login':
      return Date.parse(player.lastLogin || '') || 0;
    case 'name':
    default:
      return String(player.name || '').toLowerCase();
  }
}

function renderPlayerSummary() {
  if (!els.playersSummary) return;
  if (!state.players.length) {
    els.playersSummary.innerHTML = '';
    return;
  }

  const totalMoney = state.players.reduce((sum, p) => sum + Number(p.money || 0), 0);
  const totalFame = state.players.reduce((sum, p) => sum + Number(p.famePoints || 0), 0);
  const withCoords = state.players.filter(hasKnownLocation).length;
  const protectedPlayers = state.players.filter(p => !p.hasUsedNewPlayerProtection).length;
  const source = humanizePlayerSource(state.players[0]?.source || 'db');

  els.playersSummary.innerHTML = `
    <div class="summary-card">
      <div class="summary-label">Онлайн</div>
      <div class="summary-value">${formatNumber(state.players.length)}</div>
      <div class="summary-sub">Игроков сейчас на сервере</div>
    </div>
    <div class="summary-card">
      <div class="summary-label">Баланс</div>
      <div class="summary-value">${formatNumber(totalMoney)}</div>
      <div class="summary-sub">Сумма денег у онлайна</div>
    </div>
    <div class="summary-card">
      <div class="summary-label">Слава</div>
      <div class="summary-value">${formatNumber(totalFame)}</div>
      <div class="summary-sub">Сумма очков славы</div>
    </div>
    <div class="summary-card">
      <div class="summary-label">Данные</div>
      <div class="summary-value">${escapeHtml(source)}</div>
      <div class="summary-sub">Координаты: ${withCoords}/${state.players.length}, защита новичка: ${protectedPlayers}</div>
    </div>
  `;
}

async function loadPlayers() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/players?serverId=${encodeURIComponent(serverId)}`);
  state.players = data.players || [];
  els.playersCount.textContent = `Игроки: ${state.players.length}`;
  renderPlayerSummary();
  renderPlayers();
}

function renderPlayers() {
  els.playersList.innerHTML = '';
  if (!state.players.length) {
    els.playersList.innerHTML = '<div class="card">Игроки не найдены.</div>';
    return;
  }

  const query = (els.playersSearch?.value || '').trim().toLowerCase();
  const sortBy = els.playersSort?.value || 'name';
  const filtered = state.players
    .filter(player => !query || getPlayerSearchBlob(player).includes(query))
    .sort((a, b) => {
      const av = getPlayerSortValue(a, sortBy);
      const bv = getPlayerSortValue(b, sortBy);
      if (typeof av === 'string' && typeof bv === 'string') return av.localeCompare(bv, 'ru');
      return Number(bv) - Number(av);
    });

  if (!filtered.length) {
    els.playersList.innerHTML = '<div class="card">По текущему фильтру игроки не найдены.</div>';
    return;
  }

  filtered.forEach(player => {
    const card = document.createElement('div');
    card.className = 'player-card oxygen-player-card';
    const displayIp = player.authorityIp || player.ipAddress || '—';
    const displayAlias = player.fakeName || player.authorityName || '—';
    const location = formatLocation(player);
    const quickAccessItems = Array.isArray(player.quickAccessItems) ? player.quickAccessItems.filter(Boolean) : [];
    const quickAccessHtml = quickAccessItems.length
      ? quickAccessItems.map((item, index) => `
        <div class="equip-slot">
          <span class="equip-slot-name">Слот ${index + 1}</span>
          <span class="equip-slot-value">${escapeHtml(item)}</span>
        </div>
      `).join('')
      : '<div class="muted">Сервер пока не отдал быстрые слоты.</div>';
    card.innerHTML = `
      <div class="player-top">
        <div class="player-overview">
          <div class="player-avatar">${escapeHtml(getPlayerInitials(player))}</div>
          <div>
            <div class="player-title-row">
              <div class="player-name">${escapeHtml(player.name || 'Неизвестно')}</div>
              <span class="tag green">Онлайн</span>
              <span class="tag">${escapeHtml(humanizePlayerSource(player.source || 'db'))}</span>
              ${player.hasUsedNewPlayerProtection ? '' : '<span class="tag gold">Защита новичка</span>'}
            </div>
            <div class="player-subline">SteamID: ${escapeHtml(player.steamId || '—')}</div>
            <div class="player-subline">ID заключённого: ${escapeHtml(player.prisonerId || '—')}</div>
            <div class="player-subline">Native ID: ${escapeHtml(player.nativePlayerId || '—')}</div>
          </div>
        </div>
        <div class="player-actions">
          <button class="btn ghost" data-action="kick">Кик</button>
          <button class="btn ghost danger" data-action="ban">Бан</button>
          <button class="btn ghost" data-action="teleport">Телепорт</button>
          <button class="btn ghost" data-action="spawn">Выдать</button>
        </div>
      </div>

      <div class="player-grid">
        <div class="player-stat">
          <span class="player-stat-label">Деньги</span>
          <strong>${formatNumber(player.money)}</strong>
        </div>
        <div class="player-stat">
          <span class="player-stat-label">Золото</span>
          <strong>${formatNumber(player.gold)}</strong>
        </div>
        <div class="player-stat">
          <span class="player-stat-label">Слава</span>
          <strong>${formatNumber(player.famePoints)}</strong>
        </div>
        <div class="player-stat">
          <span class="player-stat-label">Наиграно</span>
          <strong>${formatHours(player.playTime)}</strong>
        </div>
      </div>

      <div class="player-meta-grid">
        <div class="meta-line"><span>IP</span><strong>${escapeHtml(displayIp)}</strong></div>
        <div class="meta-line"><span>Псевдоним</span><strong>${escapeHtml(displayAlias)}</strong></div>
        <div class="meta-line"><span>Последний вход</span><strong>${formatStamp(player.lastLogin)}</strong></div>
        <div class="meta-line"><span>Последний выход</span><strong>${formatStamp(player.lastLogout)}</strong></div>
        <div class="meta-line"><span>Создан</span><strong>${formatStamp(player.createdAt)}</strong></div>
        <div class="meta-line"><span>Локация</span><strong>${escapeHtml(location)}</strong></div>
        <div class="meta-line"><span>В руках</span><strong>${escapeHtml(player.itemInHands || '—')}</strong></div>
        <div class="meta-line"><span>Live-обновление</span><strong>${formatStamp(player.lastNativeUpdate)}</strong></div>
      </div>

      <div class="player-equipment">
        <div class="player-section-title">Быстрые слоты</div>
        <div class="equipment-strip">${quickAccessHtml}</div>
      </div>
    `;
    card.querySelector('[data-action="kick"]').onclick = () => openCommandModal(`#kick ${player.steamId || player.name || ''}`);
    card.querySelector('[data-action="ban"]').onclick = () => openCommandModal(`#ban ${player.steamId || player.name || ''}`);
    card.querySelector('[data-action="teleport"]').onclick = () => openCommandModal(`#teleport ${player.steamId || player.name || ''}`);
    card.querySelector('[data-action="spawn"]').onclick = () => openSpawnModal(player);
    els.playersList.appendChild(card);
  });
}

function mapLayerEnabled(layerName) {
  const toggleMap = {
    players: els.mapPlayersToggle,
    vehicles: els.mapVehiclesToggle,
    chests: els.mapChestsToggle,
    flags: els.mapFlagsToggle
  };
  const toggle = toggleMap[layerName];
  return !toggle || !!toggle.checked;
}

function normalizeMapPoint(marker, bounds) {
  const width = Number(bounds.maxX) - Number(bounds.minX);
  const height = Number(bounds.maxY) - Number(bounds.minY);
  if (!width || !height) return null;

  const x = Number(marker.x || 0);
  const y = Number(marker.y || 0);
  const invertX = bounds.invertX !== false;
  const invertY = bounds.invertY !== false;
  const nx = invertX
    ? (Number(bounds.maxX) - x) / width
    : (x - Number(bounds.minX)) / width;
  const ny = invertY
    ? (Number(bounds.maxY) - y) / height
    : (y - Number(bounds.minY)) / height;

  if (!Number.isFinite(nx) || !Number.isFinite(ny)) return null;
  return {
    left: `${Math.max(0, Math.min(100, nx * 100)).toFixed(4)}%`,
    top: `${Math.max(0, Math.min(100, ny * 100)).toFixed(4)}%`
  };
}

function updateMapTransform() {
  if (!els.mapScene) return;
  const { scale, offsetX, offsetY } = state.mapView;
  els.mapScene.style.transformOrigin = '0 0';
  els.mapScene.style.transform = `matrix(${scale}, 0, 0, ${scale}, ${offsetX}, ${offsetY})`;
}

function renderMapSelection() {
  if (!els.mapSelection) return;
  const marker = state.mapSelection;
  if (!marker) {
    els.mapSelection.innerHTML = '<div class="muted">Ничего не выбрано.</div>';
    return;
  }

  const subtitle = marker.steamId
    ? `SteamID: ${escapeHtml(marker.steamId)}`
    : (marker.className || marker.asset ? escapeHtml(marker.className || marker.asset) : '—');

  els.mapSelection.innerHTML = `
    <div class="map-selection-card">
      <div class="map-selection-type type-${escapeHtml(marker.type || 'unknown')}">${escapeHtml(humanizeMapType(marker.type))}</div>
      <div class="map-selection-name">${escapeHtml(marker.label || marker.name || 'Неизвестно')}</div>
      <div class="map-selection-sub">${subtitle}</div>
      <div class="map-selection-coords">
        X: ${formatNumber(Math.round(Number(marker.x || 0)))}<br/>
        Y: ${formatNumber(Math.round(Number(marker.y || 0)))}<br/>
        Z: ${formatNumber(Math.round(Number(marker.z || 0)))}
      </div>
    </div>
  `;
}

function renderMapMeta() {
  const data = state.mapData || defaultMapData();
  if (els.mapMeta) {
    const zoom = `${Math.round((state.mapView.scale || 1) * 100)}%`;
    els.mapMeta.innerHTML = `
      <span class="pill">Игроки: ${formatNumber(data.counts?.players || 0)}</span>
      <span class="pill">Машины: ${formatNumber(data.counts?.vehicles || 0)}</span>
      <span class="pill">Сундуки: ${formatNumber(data.counts?.chests || 0)}</span>
      <span class="pill">Флаги: ${formatNumber(data.counts?.flags || 0)}</span>
      <span class="pill">Зум: ${zoom}</span>
      <span class="muted">Обновлено: ${escapeHtml(data.updatedAt || '—')}</span>
    `;
  }

  if (els.mapLayerStats) {
    els.mapLayerStats.innerHTML = `
      <div class="map-stat-row"><span>Люди</span><strong>${formatNumber(data.counts?.players || 0)}</strong></div>
      <div class="map-stat-row"><span>Машины</span><strong>${formatNumber(data.counts?.vehicles || 0)}</strong></div>
      <div class="map-stat-row"><span>Сундуки</span><strong>${formatNumber(data.counts?.chests || 0)}</strong></div>
      <div class="map-stat-row"><span>Флаги</span><strong>${formatNumber(data.counts?.flags || 0)}</strong></div>
      <a class="map-source-link" href="${escapeHtml(data.background?.sourceUrl || '#')}" target="_blank" rel="noreferrer">Источник карты</a>
    `;
  }
}

function renderMap() {
  if (!els.mapMarkers || !els.mapImage) return;

  const data = state.mapData || defaultMapData();
  const bounds = data.bounds || defaultMapData().bounds;
  els.mapImage.src = data.background?.url || defaultMapData().background.url;
  els.mapMarkers.innerHTML = '';

  const layerOrder = ['players', 'vehicles', 'chests', 'flags'];
  layerOrder.forEach(layerName => {
    if (!mapLayerEnabled(layerName)) return;
    const markers = Array.isArray(data.layers?.[layerName]) ? data.layers[layerName] : [];
    markers.forEach(marker => {
      const point = normalizeMapPoint(marker, bounds);
      if (!point) return;

      const button = document.createElement('button');
      const isSelected = state.mapSelection && String(state.mapSelection.id) === String(marker.id) && state.mapSelection.type === marker.type;
      button.className = `map-marker ${layerName}${isSelected ? ' selected' : ''}`;
      button.style.left = point.left;
      button.style.top = point.top;
      button.title = `${marker.label || marker.name || marker.type}\n${Math.round(Number(marker.x || 0))}, ${Math.round(Number(marker.y || 0))}, ${Math.round(Number(marker.z || 0))}`;
      button.innerHTML = `<span></span>`;
      button.addEventListener('click', ev => {
        ev.stopPropagation();
        state.mapSelection = marker;
        renderMap();
        renderMapSelection();
      });
      els.mapMarkers.appendChild(button);
    });
  });

  renderMapMeta();
  renderMapSelection();
  updateMapTransform();
}

async function loadMap() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/map?serverId=${encodeURIComponent(serverId)}`);
  state.mapData = Object.assign(defaultMapData(), data || {});
  renderMap();
}

function resetMapView() {
  state.mapView.scale = 1;
  state.mapView.offsetX = 0;
  state.mapView.offsetY = 0;
  updateMapTransform();
  renderMapMeta();
}

function zoomMap(factor, clientX, clientY) {
  if (!els.mapViewport || !els.mapScene) return;

  const oldScale = state.mapView.scale || 1;
  const newScale = Math.max(0.65, Math.min(5.5, oldScale * factor));
  if (Math.abs(newScale - oldScale) < 0.0001) return;

  const viewportRect = els.mapViewport.getBoundingClientRect();
  const sceneRect = els.mapScene.getBoundingClientRect();
  const pivotX = clientX ?? (viewportRect.left + viewportRect.width / 2);
  const pivotY = clientY ?? (viewportRect.top + viewportRect.height / 2);
  const sceneX = (pivotX - sceneRect.left) / oldScale;
  const sceneY = (pivotY - sceneRect.top) / oldScale;

  state.mapView.scale = newScale;
  state.mapView.offsetX = (pivotX - viewportRect.left) - (sceneX * newScale);
  state.mapView.offsetY = (pivotY - viewportRect.top) - (sceneY * newScale);
  updateMapTransform();
  renderMapMeta();
}

function bindMapInteractions() {
  if (!els.mapViewport || els.mapViewport.dataset.bound === '1') return;
  els.mapViewport.dataset.bound = '1';

  els.mapViewport.addEventListener('wheel', ev => {
    ev.preventDefault();
    zoomMap(ev.deltaY < 0 ? 1.14 : 1 / 1.14, ev.clientX, ev.clientY);
  }, { passive: false });

  els.mapViewport.addEventListener('pointerdown', ev => {
    if (ev.button !== 0) return;
    if (ev.target.closest('.map-marker')) return;
    state.mapView.dragging = true;
    state.mapView.pointerId = ev.pointerId;
    state.mapView.lastX = ev.clientX;
    state.mapView.lastY = ev.clientY;
    els.mapViewport.setPointerCapture(ev.pointerId);
  });

  els.mapViewport.addEventListener('pointermove', ev => {
    if (!state.mapView.dragging || state.mapView.pointerId !== ev.pointerId) return;
    state.mapView.offsetX += ev.clientX - state.mapView.lastX;
    state.mapView.offsetY += ev.clientY - state.mapView.lastY;
    state.mapView.lastX = ev.clientX;
    state.mapView.lastY = ev.clientY;
    updateMapTransform();
  });

  const stopDrag = ev => {
    if (state.mapView.pointerId !== ev.pointerId) return;
    state.mapView.dragging = false;
    state.mapView.pointerId = null;
  };

  els.mapViewport.addEventListener('pointerup', stopDrag);
  els.mapViewport.addEventListener('pointercancel', stopDrag);
  els.mapViewport.addEventListener('click', ev => {
    if (ev.target.closest('.map-marker')) return;
    state.mapSelection = null;
    renderMapSelection();
    renderMap();
  });
}

async function loadPlugins() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/plugins?serverId=${encodeURIComponent(serverId)}`);
  els.pluginSelect.innerHTML = '';
  (data.plugins || []).forEach(p => {
    const opt = document.createElement('option');
    opt.value = p;
    opt.textContent = p;
    els.pluginSelect.appendChild(opt);
  });
  if (data.plugins && data.plugins.length) {
    await loadSelectedPlugin();
  }
}

async function loadSelectedPlugin() {
  const serverId = currentServer();
  const name = els.pluginSelect.value;
  if (!serverId || !name) return;
  const data = await api(`/api/plugin?serverId=${encodeURIComponent(serverId)}&name=${encodeURIComponent(name)}`);
  els.editor.value = data.code || '';
  els.pluginName.value = name.replace('.cs', '');
}

async function savePlugin() {
  const serverId = currentServer();
  const name = els.pluginName.value.trim();
  if (!serverId || !name) return;
  await api('/api/plugin', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ serverId, name, code: els.editor.value })
  });
  notify('Плагин сохранен');
  await loadPlugins();
}

async function deletePlugin() {
  const serverId = currentServer();
  const name = els.pluginSelect.value;
  if (!serverId || !name) return;
  await api(`/api/plugin?serverId=${encodeURIComponent(serverId)}&name=${encodeURIComponent(name)}`, { method: 'DELETE' });
  notify('Плагин удален');
  await loadPlugins();
}

async function reloadPlugin() {
  const serverId = currentServer();
  const name = els.pluginSelect.value;
  if (!serverId || !name) return;
  await api('/api/reload', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ serverId, name })
  });
  notify('Плагин перезагружен');
}

async function loadLogs() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/logs?serverId=${encodeURIComponent(serverId)}`);
  els.logs.textContent = data.text || '';
}

async function loadChat() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/chat?serverId=${encodeURIComponent(serverId)}`);
  state.chat = data.messages || [];
  renderChat();
}

function renderChat() {
  const channelFilter = els.chatChannel.value;
  const q = (els.chatSearch.value || '').toLowerCase();
  els.chatList.innerHTML = '';
  const filtered = state.chat.filter(m => {
    if (channelFilter !== 'all' && m.channel !== channelFilter) return false;
    if (!q) return true;
    return (m.message || '').toLowerCase().includes(q) || (m.name || '').toLowerCase().includes(q);
  });
  if (!filtered.length) {
    els.chatList.innerHTML = '<div class="muted">Сообщений нет.</div>';
    return;
  }
  filtered.forEach(msg => {
    const item = document.createElement('div');
    item.className = 'chat-item';
    item.innerHTML = `
      <div class="chat-meta">
        <span class="chat-channel">${humanizeChannel(msg.channel || 'Global')}</span>
        <strong>${msg.name || 'Неизвестно'}</strong>
        <span>${msg.time || ''}</span>
      </div>
      <div>${msg.message || ''}</div>
    `;
    els.chatList.appendChild(item);
  });
}

async function loadKills() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/kills?serverId=${encodeURIComponent(serverId)}`);
  state.kills = data.kills || [];
  renderKills();
}

function renderKills() {
  els.killsList.innerHTML = '';
  if (!state.kills.length) {
    els.killsList.innerHTML = '<div class="muted">Нет данных об убийствах.</div>';
    return;
  }
  state.kills.forEach(k => {
    const item = document.createElement('div');
    item.className = 'kill-item';
    const killer = k.isSuicide ? 'Самоубийство' : (k.killer || 'Неизвестно');
    const weapon = k.weapon ? `${k.weapon}${k.weaponDamage ? ` (${k.weaponDamage})` : ''}` : 'Неизвестно';
    const distance = (k.distance || 0).toFixed ? (k.distance || 0).toFixed(2) : k.distance;
    item.innerHTML = `
      <div class="kill-main">
        <div class="kill-names">
          <span class="kill-killer">${killer}</span>
          <span class="kill-sep">→</span>
          <span class="kill-victim">${k.victim || 'Неизвестно'}</span>
        </div>
        <div class="kill-meta">
          <span>${weapon}</span>
          <span>Дистанция: ${distance}м</span>
        </div>
      </div>
      <div class="kill-time">${k.time || ''}</div>
    `;
    els.killsList.appendChild(item);
  });
}

async function sendChat() {
  const serverId = currentServer();
  const message = els.chatMessage.value.trim();
  if (!serverId || !message) return;
  await api('/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ serverId, channel: els.chatSendChannel.value, message })
  });
  els.chatMessage.value = '';
  notify('Сообщение отправлено');
}

async function loadSquads() {
  const serverId = currentServer();
  if (!serverId) return;
  const data = await api(`/api/squads?serverId=${encodeURIComponent(serverId)}`);
  state.squads = data.squads || [];
  renderSquads();
}

function renderSquads() {
  if (!state.squads.length) {
    els.squadsList.textContent = 'Нет данных по отрядам.';
    return;
  }
  els.squadsList.innerHTML = state.squads.map(s => `
    <div class="squad-card">
      <div class="player-header">
        <div>
          <div class="player-name">${s.name || 'Без названия'}</div>
          <div class="muted">ID: ${s.id ?? '—'} / Лимит: ${s.memberLimit ?? 0} / Очки: ${s.score ?? 0}</div>
        </div>
        <div class="player-tags">
          <span class="tag">${(s.members || []).length} участников</span>
        </div>
      </div>
      <div class="row">
        ${(s.members || []).length
          ? (s.members || []).map(m => `<span class="pill">${m.name || ('ID ' + (m.id ?? ''))}</span>`).join('')
          : '<span class="muted">Участников нет</span>'}
      </div>
    </div>
  `).join('');
}

function openCommandModal(prefill) {
  els.commandInput.value = prefill || '';
  els.commandModal.classList.remove('hidden');
}

function closeModal(id) {
  document.getElementById(id).classList.add('hidden');
}

async function sendCommand() {
  const serverId = currentServer();
  const command = els.commandInput.value.trim();
  if (!serverId || !command) return;
  await api('/api/command', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ serverId, command })
  });
  notify('Команда отправлена');
  closeModal('commandModal');
}

async function openSpawnModal(player) {
  state.selectedPlayer = player || null;
  els.spawnModal.classList.remove('hidden');
  els.spawnItem.value = '';
  els.spawnQty.value = '1';
  state.selectedItem = null;
  renderItems();
  if (player) {
    els.itemSelected.textContent = `Игрок: ${player.name || player.steamId || 'Неизвестно'}`;
  } else {
    els.itemSelected.textContent = 'Не выбран игрок';
  }
}

async function spawnItem() {
  const serverId = currentServer();
  const item = els.spawnItem.value.trim();
  const qty = parseInt(els.spawnQty.value || '1', 10);
  if (!serverId || !item) return;
  const target = state.selectedPlayer && (state.selectedPlayer.steamId || state.selectedPlayer.name)
    ? `${state.selectedPlayer.steamId || state.selectedPlayer.name} `
    : '';
  const command = `#spawnitem ${target}${item} ${isNaN(qty) ? 1 : qty}`.trim();
  await api('/api/command', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ serverId, command })
  });
  notify('Команда выдачи отправлена');
  closeModal('spawnModal');
}

async function loadItems() {
  try {
    let url = './items.json';
    if (typeof location !== 'undefined' && location.origin && location.origin !== 'null') {
      url = location.origin.replace(/\/+$/, '') + '/items.json';
    }
    const res = await fetch(url);
    if (!res.ok) return;
    const data = await res.json();
    const items = (data.items || []).map(i => {
      const path = i.path || '';
      const parts = path.split('\\');
      const name = parts[parts.length - 1] || path;
      const category = parts[0] || 'Other';
      return {
        name: name.replace('.uasset', ''),
        path,
        category
      };
    });
    state.items = items;
    state.categories = Array.from(new Set(items.map(i => i.category))).sort();
  } catch {
    state.items = [];
  }
}

function renderItems() {
  els.itemCategories.innerHTML = '';
  els.itemGrid.innerHTML = '';
  if (!state.items.length) {
    els.itemGrid.innerHTML = '<div class="muted">Список предметов не загружен.</div>';
    return;
  }
  const activeCategory = els.itemCategories.dataset.active || 'Все';
  const search = (els.itemSearch.value || '').toLowerCase();

  const categories = ['Все', ...state.categories];
  categories.forEach(cat => {
    const btn = document.createElement('button');
    btn.className = 'category-btn' + (cat === activeCategory ? ' active' : '');
    btn.textContent = cat;
    btn.onclick = () => {
      els.itemCategories.dataset.active = cat;
      renderItems();
    };
    els.itemCategories.appendChild(btn);
  });

  const filtered = state.items.filter(item => {
    if (activeCategory !== 'Все' && item.category !== activeCategory) return false;
    if (!search) return true;
    return item.name.toLowerCase().includes(search) || item.path.toLowerCase().includes(search);
  }).slice(0, 200);

  filtered.forEach(item => {
    const card = document.createElement('div');
    card.className = 'item-card' + (state.selectedItem && state.selectedItem.path === item.path ? ' active' : '');
    card.innerHTML = `<div>${item.name}</div><div class="muted">${item.category}</div>`;
    card.onclick = () => {
      state.selectedItem = item;
      els.spawnItem.value = item.name;
      els.itemSelected.textContent = `${item.name}`;
      renderItems();
    };
    els.itemGrid.appendChild(card);
  });
}

navLinks.forEach(link => {
  link.addEventListener('click', () => {
    setActivePage(link.dataset.page);
    refreshActive();
  });
});

els.serverSelect.addEventListener('change', async () => {
  state.currentServerId = els.serverSelect.value;
  await loadStatus();
  refreshActive();
});

function refreshActive() {
  switch (state.activePage) {
    case 'servers':
      loadServers();
      break;
    case 'players':
      loadPlayers();
      break;
    case 'map':
      loadMap();
      break;
    case 'squads':
      loadSquads();
      break;
    case 'chat':
      loadChat();
      break;
    case 'kills':
      loadKills();
      break;
    case 'economy':
      loadEconomy();
      break;
    case 'plugins':
      loadPlugins();
      break;
    case 'logs':
      loadLogs();
      break;
  }
}

let refreshTimer = null;
function ensureAutoRefresh() {
  if (refreshTimer) clearInterval(refreshTimer);
  refreshTimer = setInterval(() => {
    if (!apiBase()) return;
    refreshActive();
    loadStatus().catch(() => {});
  }, 10000);
}

els.refreshCurrent.addEventListener('click', refreshActive);
els.refreshPlayers.addEventListener('click', loadPlayers);
if (els.playersSearch) els.playersSearch.addEventListener('input', renderPlayers);
if (els.playersSort) els.playersSort.addEventListener('change', renderPlayers);
if (els.refreshMap) els.refreshMap.addEventListener('click', loadMap);
if (els.mapZoomIn) els.mapZoomIn.addEventListener('click', () => zoomMap(1.2));
if (els.mapZoomOut) els.mapZoomOut.addEventListener('click', () => zoomMap(1 / 1.2));
if (els.mapReset) els.mapReset.addEventListener('click', resetMapView);
if (els.mapPlayersToggle) els.mapPlayersToggle.addEventListener('change', renderMap);
if (els.mapVehiclesToggle) els.mapVehiclesToggle.addEventListener('change', renderMap);
if (els.mapChestsToggle) els.mapChestsToggle.addEventListener('change', renderMap);
if (els.mapFlagsToggle) els.mapFlagsToggle.addEventListener('change', renderMap);
els.refreshChat.addEventListener('click', loadChat);
els.refreshKills.addEventListener('click', loadKills);
if (els.refreshEconomy) els.refreshEconomy.addEventListener('click', loadEconomy);
els.refreshSquads.addEventListener('click', loadSquads);
els.refreshLogs.addEventListener('click', loadLogs);
els.chatChannel.addEventListener('change', renderChat);
els.chatSearch.addEventListener('input', renderChat);
els.sendChat.addEventListener('click', sendChat);

els.pluginSelect.addEventListener('change', loadSelectedPlugin);
els.saveBtn.addEventListener('click', savePlugin);
els.deleteBtn.addEventListener('click', deletePlugin);
els.reloadBtn.addEventListener('click', reloadPlugin);
if (els.createServerBtn) els.createServerBtn.addEventListener('click', () => notify('Создание сервера пока недоступно.'));
if (els.downloadPluginBtn) els.downloadPluginBtn.addEventListener('click', () => notify('Пакет будет добавлен позже.'));
if (els.connectBtn) els.connectBtn.addEventListener('click', async () => {
  saveSettings();
  await loadServers();
  ensureAutoRefresh();
});

els.sendCommand.addEventListener('click', sendCommand);
els.spawnItemBtn.addEventListener('click', spawnItem);
els.itemSearch.addEventListener('input', renderItems);

Array.from(document.querySelectorAll('[data-close]')).forEach(btn => {
  btn.addEventListener('click', () => closeModal(btn.dataset.close));
});

(async function init() {
  loadSettings();
  bindMapInteractions();
  await loadItems();
  if (apiBase()) {
    await loadServers();
    ensureAutoRefresh();
  }
})();
