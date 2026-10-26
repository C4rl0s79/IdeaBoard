'use strict';

const $ = (sel, root = document) => root.querySelector(sel);
const boardEl = $('#board');
const dlg = $('#card-dlg');

let board = null;
let me = null;       // { key, name, email, role }
let users = [];      // [{ key, name, email, role, ... }]
let filterText = '';
let filterTag = null;
let filterProject = ''; // '' all, '-' none, else project id
let filterAssignee = ''; // '' all, '-' nobody, else user key
let editingId = null; // card open in the dialog; null = new card
let justDragged = false;

const canEdit = () => me && me.role !== 'viewer';
const isAdmin = () => me && me.role === 'admin';

// ---------- api ----------

async function api(method, path, body) {
  const headers = { 'X-IdeaBoard': '1' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch('/api' + path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
  });
  const data = (res.headers.get('content-type') || '').includes('json') ? await res.json() : null;
  if (res.status === 401 && path !== '/login') {
    showLogin();
    throw new Error('Sesja wygasła — zaloguj się ponownie.');
  }
  if (!res.ok) throw new Error((data && data.error) || `Błąd serwera (${res.status})`);
  return data;
}

async function change(method, path, body) {
  try {
    setBoard(await api(method, path, body));
    return true;
  } catch (e) {
    toast(e.message);
    return false;
  }
}

function setBoard(b) {
  board = b;
  render();
  if (dlg.open && editingId) {
    const card = findCard(editingId);
    if (card) renderNotes(card); else dlg.close();
  }
  if ($('#projects-dlg').open) renderProjects();
}

async function loadUsers() {
  users = await api('GET', '/users');
}

const findCard = id => board.cards.find(c => c.id === id);
const projectName = id => board.projects.find(p => p.id === id)?.name;
const userName = key => users.find(u => u.key === key)?.name || key;

// ---------- helpers ----------

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === 'class') node.className = v;
    else if (k === 'dataset') Object.assign(node.dataset, v);
    else if (k.startsWith('on')) node.addEventListener(k.slice(2), v);
    else if (v !== undefined && v !== null && v !== false) node.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) if (c != null) node.append(c);
  return node;
}

const fmt = new Intl.DateTimeFormat('pl-PL', { dateStyle: 'medium', timeStyle: 'short' });
const fmtDate = s => fmt.format(new Date(s));

let toastTimer;
function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => (t.hidden = true), 3500);
}

const parseTags = s => s.split(',').map(t => t.trim().replace(/^#/, '')).filter(Boolean);

function initials(name) {
  const parts = name.replace(/@.*/, '').split(/[\s._-]+/).filter(Boolean);
  return ((parts[0]?.[0] || '?') + (parts[1]?.[0] || '')).toUpperCase();
}

// Stable hue per person, so the same owner always gets the same avatar colour.
function hue(s) {
  let h = 0;
  for (const ch of s) h = (h * 31 + ch.codePointAt(0)) % 360;
  return h;
}

function avatar(key) {
  const a = el('span', { class: 'avatar', title: `Odpowiedzialny: ${userName(key)}` }, initials(userName(key)));
  a.style.setProperty('--h', hue(key));
  return a;
}

function matches(card) {
  if (filterTag && !card.tags.some(t => t.toLowerCase() === filterTag)) return false;
  if (filterProject && (filterProject === '-' ? card.projectId : card.projectId !== filterProject)) return false;
  if (filterAssignee && (filterAssignee === '-' ? card.assignee : card.assignee !== filterAssignee)) return false;
  if (!filterText) return true;
  const hay = [card.title, card.description, ...card.tags, ...card.notes.map(n => n.text),
    projectName(card.projectId) || '', card.assignee ? userName(card.assignee) : ''].join('\n').toLowerCase();
  return filterText.split(/\s+/).every(w => hay.includes(w));
}

function fillSelect(select, options, value) {
  select.replaceChildren(...options.map(([v, label]) => el('option', { value: v }, label)));
  select.value = options.some(([v]) => v === value) ? value : options[0][0];
  return select.value;
}

// ---------- render ----------

function render() {
  const scroll = boardEl.scrollLeft;
  const colScroll = {};
  for (const c of boardEl.querySelectorAll('.col')) colScroll[c.dataset.id] = $('.col-cards', c).scrollTop;

  renderFilters();
  boardEl.replaceChildren(...board.columns.map((col, i) => renderColumn(col, i)));
  renderTagFilter();

  boardEl.scrollLeft = scroll;
  for (const c of boardEl.querySelectorAll('.col')) $('.col-cards', c).scrollTop = colScroll[c.dataset.id] || 0;
}

function renderFilters() {
  filterProject = fillSelect($('#f-project-filter'), [
    ['', 'Wszystkie projekty'],
    ...board.projects.map(p => [p.id, p.name]),
    ['-', '— bez projektu —'],
  ], filterProject);
  filterAssignee = fillSelect($('#f-assignee-filter'), [
    ['', 'Wszyscy odpowiedzialni'],
    [me.key, 'Moje'],
    ...users.filter(u => u.key !== me.key && u.role !== 'blocked').map(u => [u.key, u.name]),
    ['-', '— nieprzypisane —'],
  ], filterAssignee);
}

function renderColumn(col, index) {
  const cards = board.cards.filter(c => c.columnId === col.id);
  const visible = cards.filter(matches).length;
  const count = visible === cards.length ? String(cards.length) : `${visible}/${cards.length}`;

  const name = el('div', { class: 'col-name' }, col.name);
  let tools = null;
  if (isAdmin()) {
    name.title = 'Kliknij, aby zmienić nazwę';
    name.classList.add('editable');
    name.addEventListener('click', () => renameColumn(col, name));
    tools = el('div', { class: 'col-tools' },
      index > 0 ? el('button', { class: 'ghost', title: 'Przesuń w lewo', onclick: () => change('POST', `/columns/${col.id}/move`, { index: index - 1 }) }, '‹') : null,
      index < board.columns.length - 1 ? el('button', { class: 'ghost', title: 'Przesuń w prawo', onclick: () => change('POST', `/columns/${col.id}/move`, { index: index + 1 }) }, '›') : null,
      el('button', { class: 'ghost', title: 'Usuń kolumnę', onclick: () => deleteColumn(col, cards.length) }, '✕'),
    );
  }

  return el('section', { class: 'col', dataset: { id: col.id } },
    el('div', { class: 'col-head' }, name, el('span', { class: 'col-count' }, count), tools),
    el('div', { class: 'col-cards' }, cards.map(renderCard)),
    canEdit() ? renderQuickAdd(col) : null,
  );
}

function renderCard(card) {
  const project = projectName(card.projectId);
  const foot = el('div', { class: 'card-foot' },
    project ? el('span', { class: 'chip project' }, project) : null,
    card.tags.map(t => el('span', { class: 'chip' }, t)),
    el('span', { class: 'spacer' }),
    card.notes.length ? el('span', { class: 'muted', title: 'Notatki' }, `✎ ${card.notes.length}`) : null,
    card.assignee ? avatar(card.assignee) : null,
  );
  return el('article', {
    class: `card p-${card.priority}${matches(card) ? '' : ' filtered'}`,
    tabindex: 0,
    dataset: { id: card.id },
  },
    el('div', { class: 'card-title' }, card.title),
    card.description ? el('div', { class: 'card-desc' }, card.description) : null,
    project || card.tags.length || card.notes.length || card.assignee ? foot : null,
  );
}

function renderQuickAdd(col) {
  const wrap = el('div', { class: 'col-add' });
  const open = () => {
    const input = el('input', { placeholder: 'Tytuł pomysłu… (Enter)', maxlength: 200 });
    const close = () => wrap.replaceChildren(button);
    input.addEventListener('keydown', async e => {
      if (e.key === 'Escape') close();
      if (e.key === 'Enter' && input.value.trim()) {
        const title = input.value.trim();
        input.value = '';
        // new ideas inherit the project currently filtered on
        const projectId = filterProject && filterProject !== '-' ? filterProject : '';
        if (await change('POST', '/cards', { title, columnId: col.id, projectId })) {
          // re-render replaced this node; reopen the input in the fresh column
          const fresh = boardEl.querySelector(`.col[data-id="${col.id}"] .col-add > button`);
          if (fresh) fresh.click();
          const cards = boardEl.querySelector(`.col[data-id="${col.id}"] .col-cards`);
          if (cards) cards.scrollTop = cards.scrollHeight;
        }
      }
    });
    input.addEventListener('blur', () => setTimeout(() => { if (!input.value.trim()) close(); }, 150));
    wrap.replaceChildren(input);
    input.focus();
  };
  const button = el('button', { class: 'ghost', onclick: open }, '+ Dodaj pomysł');
  wrap.append(button);
  return wrap;
}

function renderTagFilter() {
  const counts = new Map();
  for (const c of board.cards) for (const t of c.tags) {
    const k = t.toLowerCase();
    counts.set(k, { name: counts.get(k)?.name || t, n: (counts.get(k)?.n || 0) + 1 });
  }
  if (filterTag && !counts.has(filterTag)) filterTag = null;
  const sorted = [...counts.entries()].sort((a, b) => b[1].n - a[1].n || a[0].localeCompare(b[0], 'pl'));
  $('#tag-filter').replaceChildren(...sorted.map(([k, v]) => el('button', {
    class: `chip${filterTag === k ? ' active' : ''}`,
    title: `${v.n} pomysł(ów)`,
    onclick: () => { filterTag = filterTag === k ? null : k; render(); },
  }, v.name)));
  $('#tag-list').replaceChildren(...sorted.map(([, v]) => el('option', { value: v.name })));
}

$('#f-project-filter').addEventListener('change', e => { filterProject = e.target.value; render(); });
$('#f-assignee-filter').addEventListener('change', e => { filterAssignee = e.target.value; render(); });

// ---------- columns ----------

function renameColumn(col, nameEl) {
  if (nameEl.querySelector('input')) return;
  const input = el('input', { value: col.name, maxlength: 60 });
  let done = false;
  const finish = async save => {
    if (done) return;
    done = true;
    const v = input.value.trim();
    if (save && v && v !== col.name) await change('PUT', `/columns/${col.id}`, { name: v });
    else render();
  };
  input.addEventListener('keydown', e => {
    if (e.key === 'Enter') finish(true);
    if (e.key === 'Escape') finish(false);
  });
  input.addEventListener('blur', () => finish(true));
  nameEl.replaceChildren(input);
  input.select();
}

function deleteColumn(col, count) {
  if (count > 0) return toast('Kolumna nie jest pusta — najpierw przenieś pomysły.');
  if (confirm(`Usunąć kolumnę „${col.name}”?`)) change('DELETE', `/columns/${col.id}`);
}

$('#add-col').addEventListener('click', () => {
  const name = prompt('Nazwa nowej kolumny:');
  if (name && name.trim()) change('POST', '/columns', { name: name.trim() }).then(() => {
    boardEl.scrollLeft = boardEl.scrollWidth;
  });
});

// ---------- card dialog ----------

function openCard(id, columnId) {
  editingId = id;
  const card = id ? findCard(id) : null;
  $('#f-title').value = card?.title || '';
  $('#f-desc').value = card?.description || '';
  $('#f-tags').value = card ? card.tags.join(', ') : (filterTag ? tagSpelling(filterTag) : '');
  $('#f-priority').value = card?.priority || 'medium';
  fillSelect($('#f-column'), board.columns.map(c => [c.id, c.name]), card?.columnId || columnId || board.columns[0].id);
  fillSelect($('#f-project'), [['', '— brak —'], ...board.projects.map(p => [p.id, p.name])],
    card ? card.projectId : (filterProject !== '-' ? filterProject : ''));
  // keep a removed or blocked user visible on cards that still point at them
  const people = users.filter(u => u.role !== 'blocked' || u.key === card?.assignee).map(u => [u.key, u.name]);
  if (card?.assignee && !users.some(u => u.key === card.assignee)) people.push([card.assignee, `${card.assignee} (usunięty)`]);
  fillSelect($('#f-assignee'), [['', '— nikt —'], ...people],
    card ? card.assignee : (filterAssignee && filterAssignee !== '-' ? filterAssignee : ''));
  $('#f-note').value = '';
  $('#notes-section').hidden = !card;
  $('#delete-card').hidden = !card || !canEdit();
  $('#card-meta').textContent = card
    ? `Dodał(a) ${card.createdBy ? userName(card.createdBy) : '—'}, ${fmtDate(card.createdAt)}`
    : '';
  for (const f of dlg.querySelectorAll('input, select, textarea')) f.disabled = !canEdit();
  if (card) renderNotes(card);
  dlg.showModal();
  if (!card) $('#f-title').focus();
}

// filter tag is stored lowercase; prefill with its displayed spelling
function tagSpelling(tagKey) {
  for (const c of board.cards) for (const t of c.tags) if (t.toLowerCase() === tagKey) return t;
  return tagKey;
}

function renderNotes(card) {
  $('#notes').replaceChildren(...card.notes.map(n => el('li', {},
    el('div', { class: 'note-meta' },
      el('span', {}, `${n.by ? userName(n.by) : '—'} · ${fmtDate(n.at)}`),
      canEdit() && (isAdmin() || n.by === me.key)
        ? el('button', { type: 'button', class: 'ghost', onclick: () => {
          if (confirm('Usunąć notatkę?')) change('DELETE', `/cards/${card.id}/notes/${n.id}`);
        } }, 'usuń')
        : null),
    n.text)));
  $('#history').replaceChildren(...[...card.history].reverse().map(h =>
    el('li', {}, `${fmtDate(h.at)} — ${h.text}${h.by ? ` (${userName(h.by)})` : ''}`)));
}

async function saveCard() {
  if (!canEdit()) return;
  const body = {
    title: $('#f-title').value,
    description: $('#f-desc').value,
    tags: parseTags($('#f-tags').value),
    priority: $('#f-priority').value,
    columnId: $('#f-column').value,
    projectId: $('#f-project').value,
    assignee: $('#f-assignee').value,
  };
  if (!body.title.trim()) return toast('Tytuł jest wymagany.');
  const ok = editingId ? await change('PUT', `/cards/${editingId}`, body) : await change('POST', '/cards', body);
  if (ok) dlg.close();
}

$('#card-form').addEventListener('submit', e => { e.preventDefault(); saveCard(); });
dlg.addEventListener('keydown', e => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); saveCard(); }
});
for (const d of document.querySelectorAll('dialog')) {
  for (const b of d.querySelectorAll('[data-close]')) b.addEventListener('click', () => d.close());
  d.addEventListener('click', e => { if (e.target === d) d.close(); }); // backdrop
}
dlg.addEventListener('close', () => { editingId = null; });

$('#add-note').addEventListener('click', async () => {
  const text = $('#f-note').value.trim();
  if (!text || !editingId) return;
  if (await change('POST', `/cards/${editingId}/notes`, { text })) $('#f-note').value = '';
});

$('#delete-card').addEventListener('click', async () => {
  const card = findCard(editingId);
  if (card && confirm(`Usunąć pomysł „${card.title}”? Tego nie da się cofnąć.`)) {
    if (await change('DELETE', `/cards/${card.id}`)) dlg.close();
  }
});

$('#new-card').addEventListener('click', () => openCard(null));

boardEl.addEventListener('click', e => {
  const card = e.target.closest('.card');
  if (!card) return;
  if (justDragged) { justDragged = false; return; }
  openCard(card.dataset.id);
});
boardEl.addEventListener('keydown', e => {
  const card = e.target.closest('.card');
  if (card && e.key === 'Enter') openCard(card.dataset.id);
});

// ---------- projects ----------

function renderProjects() {
  $('#projects-list').replaceChildren(...board.projects.map(p => {
    const count = board.cards.filter(c => c.projectId === p.id).length;
    const input = el('input', { value: p.name, maxlength: 60, 'aria-label': 'Nazwa projektu' });
    input.addEventListener('change', () => {
      if (input.value.trim() && input.value.trim() !== p.name) change('PUT', `/projects/${p.id}`, { name: input.value.trim() });
    });
    return el('li', {},
      input,
      el('span', { class: 'muted small' }, `${count} pomysł(ów)`),
      el('button', { class: 'ghost danger', title: 'Usuń projekt', onclick: () => {
        if (confirm(`Usunąć projekt „${p.name}”? ${count} pomysł(ów) zostanie bez projektu.`)) change('DELETE', `/projects/${p.id}`);
      } }, '✕'));
  }));
  if (!board.projects.length) $('#projects-list').append(el('li', { class: 'muted' }, 'Brak projektów.'));
}

$('#open-projects').addEventListener('click', () => {
  renderProjects();
  $('#projects-dlg').showModal();
  $('#project-name').focus();
});

$('#project-add').addEventListener('submit', async e => {
  e.preventDefault();
  if (await change('POST', '/projects', { name: $('#project-name').value.trim() })) $('#project-name').value = '';
});

// ---------- users ----------

const fmtShort = new Intl.DateTimeFormat('pl-PL', { dateStyle: 'short' });

function renderUsers() {
  $('#users-list').replaceChildren(...users.map(u => {
    const self = u.key === me.key;
    const local = u.key === 'admin';
    const role = el('select', { disabled: self || local || undefined, 'aria-label': 'Rola' },
      [['admin', 'admin'], ['editor', 'editor'], ['viewer', 'viewer'], ['blocked', 'zablokowany']]
        .map(([v, label]) => el('option', { value: v }, label)));
    role.value = u.role;
    role.addEventListener('change', async () => {
      try { users = await api('PUT', `/users/${encodeURIComponent(u.key)}`, { role: role.value }); render(); }
      catch (e) { toast(e.message); role.value = u.role; }
    });
    const fromDomain = (u.invitedBy || '').startsWith('domena ');
    return el('tr', { class: u.role === 'blocked' ? 'blocked' : '' },
      el('td', {}, el('div', {}, u.name), u.email && u.email !== u.name ? el('div', { class: 'muted small' }, u.email) : null,
        local ? el('div', { class: 'muted small' }, 'konto hasła (awaryjne)') : null,
        fromDomain ? el('div', { class: 'muted small' }, `dołączył(a) z ${u.invitedBy}`) : null),
      el('td', {}, role),
      el('td', { class: 'muted small' }, local ? '—' : u.lastLoginAt ? fmtShort.format(new Date(u.lastLoginAt)) : 'zaproszony'),
      el('td', {}, self || local ? null : el('button', { class: 'ghost danger', title: 'Usuń z listy', onclick: async () => {
        const openDomain = settingsState?.domains.some(d => d.joinRole && u.email.endsWith('@' + d.domain));
        const msg = openDomain
          ? `Usunąć ${u.email} z listy?\n\nTa domena jest otwarta — przy następnym logowaniu wróci jako nowy użytkownik. Żeby odciąć dostęp, ustaw rolę „zablokowany”.`
          : `Odebrać dostęp ${u.email}?`;
        if (!confirm(msg)) return;
        try { users = await api('DELETE', `/users/${encodeURIComponent(u.key)}`); renderUsers(); render(); }
        catch (e) { toast(e.message); }
      } }, '✕')));
  }));
  if (settingsState) renderSettings(); // the "locked out by domain" warning depends on the user list
}

$('#open-users').addEventListener('click', async () => {
  try {
    [, settingsState] = await Promise.all([loadUsers(), api('GET', '/settings')]);
  } catch (e) {
    return toast(e.message);
  }
  renderUsers();
  renderSettings();
  $('#invite-result').hidden = true;
  $('#users-dlg').showModal();
});

// ---------- domains (admin) ----------

let settingsState = null;
const joinRoles = [['viewer', 'każdy jako viewer'], ['editor', 'każdy jako editor'], ['', 'tylko zaproszeni']];

function renderSettings() {
  const s = settingsState;
  $('#domains-list').replaceChildren(...s.domains.map(d => {
    const role = el('select', { 'aria-label': `Kto wchodzi z ${d.domain}` }, joinRoles.map(([v, label]) => el('option', { value: v }, label)));
    role.value = d.joinRole;
    role.addEventListener('change', () => saveSettings('POST', '/settings/domains', { domain: d.domain, joinRole: role.value }));
    return el('li', {},
      el('span', { class: 'domain' }, '@' + d.domain),
      role,
      el('button', { class: 'ghost danger', title: 'Usuń domenę', onclick: () => {
        if (confirm(`Usunąć domenę ${d.domain} z listy?`)) saveSettings('DELETE', `/settings/domains/${encodeURIComponent(d.domain)}`);
      } }, '✕'));
  }));
  if (!s.domains.length) $('#domains-list').append(el('li', { class: 'muted small' }, 'Brak domen — wchodzą tylko zaproszeni.'));

  $('#restrict-domains').checked = s.restrict;
  $('#restrict-domains').disabled = !s.domains.length && !s.restrict;
  // invited people the restriction locks out — the admin should see that before it bites
  const allowed = new Set(s.domains.map(d => d.domain));
  const outside = users.filter(u => u.email && u.role !== 'blocked' && !allowed.has(u.email.split('@')[1]));
  const warn = $('#restrict-warn');
  warn.hidden = !outside.length || !s.domains.length;
  warn.textContent = `${s.restrict ? 'Zablokowani przez domenę' : 'Po włączeniu blokady nie wejdą'}: ${outside.map(u => u.email).join(', ')}`
    + (s.restrict ? '' : ' (konto hasła działa dalej).');

  $('#refused-count').textContent = s.refused.length;
  $('#refused-list').replaceChildren(...s.refused.map(r =>
    el('li', {}, `${fmtDate(r.at)} — ${r.email || '(brak adresu)'}: ${r.reason}${r.ip ? ` [${r.ip}]` : ''}`)));
  if (!s.refused.length) $('#refused-list').append(el('li', {}, 'Brak.'));
}

async function saveSettings(method, path, body) {
  try {
    settingsState = await api(method, path, body);
  } catch (e) {
    toast(e.message);
  }
  renderSettings();
}

$('#domain-form').addEventListener('submit', async e => {
  e.preventDefault();
  const domain = $('#domain-name').value.trim().replace(/^@/, '').toLowerCase();
  await saveSettings('POST', '/settings/domains', { domain, joinRole: $('#domain-role').value });
  if (settingsState.domains.some(d => d.domain === domain)) $('#domain-name').value = '';
});

$('#restrict-domains').addEventListener('change', e => {
  const on = e.target.checked;
  if (on && !confirm('Włączyć blokadę? Logowanie Google będzie możliwe tylko z adresów w wymienionych domenach.')) {
    e.target.checked = false;
    return;
  }
  saveSettings('PUT', '/settings/restrict', { restrict: on });
});

$('#invite-form').addEventListener('submit', async e => {
  e.preventDefault();
  const email = $('#invite-email').value.trim().toLowerCase();
  try {
    users = await api('POST', '/users', { email, role: $('#invite-role').value });
  } catch (ex) {
    return toast(ex.message);
  }
  $('#invite-email').value = '';
  renderUsers();
  render();
  const link = location.origin + '/';
  const text = `Cześć,\n\nzapraszam do tablicy pomysłów IdeaBoard: ${link}\n` +
    `Zaloguj się przyciskiem „Zaloguj przez Google”, kontem ${email}.\n\n${me.name}`;
  $('#invite-who').textContent = email;
  $('#invite-text').value = text;
  $('#invite-mail').href = `mailto:${encodeURIComponent(email)}?subject=${encodeURIComponent('Zaproszenie do IdeaBoard')}&body=${encodeURIComponent(text)}`;
  $('#invite-result').hidden = false;
});

$('#invite-copy').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText($('#invite-text').value); toast('Skopiowano.'); }
  catch { $('#invite-text').select(); }
});

// ---------- drag & drop (mouse + touch long-press) ----------

let drag = null;

boardEl.addEventListener('pointerdown', e => {
  const card = e.target.closest('.card');
  if (!card || e.button !== 0 || !canEdit()) return;
  drag = { card, x: e.clientX, y: e.clientY, pointerId: e.pointerId, touch: e.pointerType !== 'mouse', active: false };
  if (drag.touch) drag.timer = setTimeout(() => drag && !drag.active && beginDrag(), 350);
});

window.addEventListener('pointermove', e => {
  if (!drag || e.pointerId !== drag.pointerId) return;
  const moved = Math.hypot(e.clientX - drag.x, e.clientY - drag.y) > 8;
  if (!drag.active) {
    if (!moved) return;
    if (drag.touch) { clearTimeout(drag.timer); drag = null; return; } // finger is scrolling
    beginDrag();
  }
  drag.x = e.clientX;
  drag.y = e.clientY;
  placeGhost();
});

window.addEventListener('pointerup', e => {
  if (!drag || e.pointerId !== drag.pointerId) return;
  clearTimeout(drag.timer);
  if (drag.active) finishDrag(true); else drag = null;
});
window.addEventListener('pointercancel', () => { if (drag) { clearTimeout(drag.timer); drag.active ? finishDrag(false) : (drag = null); } });
document.addEventListener('touchmove', e => { if (drag?.active) e.preventDefault(); }, { passive: false });
document.addEventListener('contextmenu', e => { if (drag) e.preventDefault(); });

function beginDrag() {
  const rect = drag.card.getBoundingClientRect();
  drag.active = true;
  drag.dx = drag.x - rect.left;
  drag.dy = drag.y - rect.top;
  drag.fromColumn = drag.card.closest('.col').dataset.id;
  drag.fromIndex = cardIndex(drag.card);
  drag.ghost = drag.card.cloneNode(true);
  drag.ghost.classList.add('ghost-card');
  drag.ghost.style.width = rect.width + 'px';
  document.body.append(drag.ghost);
  document.body.classList.add('dragging');
  drag.card.classList.add('placeholder');
  if (navigator.vibrate && drag.touch) navigator.vibrate(15);
  placeGhost();
  autoScroll();
}

function placeGhost() {
  drag.ghost.style.left = drag.x - drag.dx + 'px';
  drag.ghost.style.top = drag.y - drag.dy + 'px';

  const col = document.elementsFromPoint(drag.x, drag.y).map(n => n.closest?.('.col')).find(Boolean);
  if (!col) return;
  const list = $('.col-cards', col);
  const before = [...list.children].find(c => {
    if (c === drag.card || c.classList.contains('filtered')) return false;
    const r = c.getBoundingClientRect();
    return drag.y < r.top + r.height / 2;
  });
  if (before) { if (before.previousSibling !== drag.card) list.insertBefore(drag.card, before); }
  else if (list.lastChild !== drag.card) list.append(drag.card);
}

function autoScroll() {
  if (!drag?.active) return;
  const edge = 50, speed = 14;
  if (drag.x < edge) boardEl.scrollLeft -= speed;
  else if (drag.x > innerWidth - edge) boardEl.scrollLeft += speed;
  const col = document.elementsFromPoint(drag.x, drag.y).map(n => n.closest?.('.col')).find(Boolean);
  if (col) {
    const list = $('.col-cards', col);
    const r = list.getBoundingClientRect();
    if (drag.y < r.top + 30) list.scrollTop -= speed;
    else if (drag.y > r.bottom - 30) list.scrollTop += speed;
  }
  requestAnimationFrame(autoScroll);
}

function cardIndex(cardEl) {
  return [...cardEl.parentElement.children].indexOf(cardEl);
}

function finishDrag(commit) {
  const { card, ghost, fromColumn, fromIndex } = drag;
  drag = null;
  ghost.remove();
  document.body.classList.remove('dragging');
  card.classList.remove('placeholder');
  justDragged = true;
  setTimeout(() => (justDragged = false), 0);

  const columnId = card.closest('.col').dataset.id;
  const index = cardIndex(card);
  if (!commit) return render();
  if (columnId === fromColumn && index === fromIndex) return;
  change('POST', `/cards/${card.dataset.id}/move`, { columnId, index });
}

// ---------- search, shortcuts, refresh ----------

$('#search').addEventListener('input', e => {
  filterText = e.target.value.trim().toLowerCase();
  render();
});

document.addEventListener('keydown', e => {
  if (document.querySelector('dialog[open]') || /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName)) return;
  if (e.key === '/') { e.preventDefault(); $('#search').focus(); }
  if ((e.key === 'n' || e.key === 'N') && canEdit()) { e.preventDefault(); openCard(null); }
});

async function refresh() {
  if (drag || document.querySelector('dialog[open]') || document.activeElement?.closest?.('.col')?.querySelector('input')) return;
  try {
    const [b] = await Promise.all([api('GET', '/board'), loadUsers()]);
    if (!board || b.rev !== board.rev) setBoard(b); else render();
  } catch { /* api() already handled 401 */ }
}
document.addEventListener('visibilitychange', () => { if (!document.hidden && board) refresh(); });
setInterval(() => { if (!document.hidden && board) refresh(); }, 60_000);

// ---------- login ----------

let loginOptions = { google: false, password: true };

function showLogin() {
  $('#app').hidden = true;
  $('#login').hidden = false;
  for (const d of document.querySelectorAll('dialog[open]')) d.close();
  $('#google-login').hidden = !loginOptions.google;
  $('#login-form').hidden = !loginOptions.password;
  $('#login-sep').hidden = !(loginOptions.google && loginOptions.password);
  if (loginOptions.password && !loginOptions.google) $('#login-pass').focus();
}

async function showApp(user) {
  me = user;
  $('#login').hidden = true;
  $('#app').hidden = false;
  for (const n of document.querySelectorAll('[data-edit]')) n.hidden = !canEdit();
  for (const n of document.querySelectorAll('[data-admin]')) n.hidden = !isAdmin();
  $('#google-off').hidden = loginOptions.google;
  const badge = el('span', {}, me.name);
  $('#me').replaceChildren(avatarFor(me), badge, el('span', { class: 'muted small' }, me.role));
  const [b] = await Promise.all([api('GET', '/board'), loadUsers()]);
  setBoard(b);
  if (isAdmin()) checkUpdate();
}

// ---------- updates (admin) ----------

let update = null;

async function checkUpdate() {
  try {
    update = await api('GET', '/update');
  } catch {
    return; // GitHub unreachable — try again on the next page load
  }
  const btn = $('#update-btn');
  $('h1', $('.top')).title = `IdeaBoard ${update.current}`;
  btn.hidden = !update.available;
  btn.textContent = `Aktualizacja ${update.latest}`;
}

$('#update-btn').addEventListener('click', async () => {
  if (!update?.available) return;
  if (update.blocked) return alert(update.blocked);
  const notes = update.notes ? `\n\n${update.notes.slice(0, 1500)}` : '';
  if (!confirm(`Zainstalować IdeaBoard ${update.latest}? (teraz ${update.current})${notes}\n\nTablica będzie niedostępna przez kilka sekund.`)) return;
  const btn = $('#update-btn');
  btn.disabled = true;
  btn.textContent = 'Aktualizuję…';
  try {
    await api('POST', '/update');
  } catch (e) {
    btn.disabled = false;
    btn.textContent = `Aktualizacja ${update.latest}`;
    return toast(e.message);
  }
  // wait for the service to come back on the new version
  const deadline = Date.now() + 120_000;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 2000));
    try {
      const info = await fetch('/api/me').then(r => r.json());
      if (info.version === update.latest) return location.reload();
    } catch { /* restarting */ }
  }
  btn.disabled = false;
  btn.textContent = `Aktualizacja ${update.latest}`;
  toast('Serwer nie wrócił po 2 minutach — sprawdź usługę IdeaBoard na serwerze.');
});

function avatarFor(user) {
  const a = el('span', { class: 'avatar' }, initials(user.name));
  a.style.setProperty('--h', hue(user.key));
  return a;
}

$('#login-form').addEventListener('submit', async e => {
  e.preventDefault();
  const err = $('#login-err');
  err.textContent = '';
  try {
    await api('POST', '/login', { password: $('#login-pass').value });
    $('#login-pass').value = '';
    const meInfo = await api('GET', '/me');
    await showApp(meInfo.user);
  } catch (ex) {
    err.textContent = ex.message;
  }
});

$('#logout').addEventListener('click', async () => {
  await api('POST', '/logout').catch(() => {});
  board = null;
  me = null;
  showLogin();
});

(async () => {
  const params = new URLSearchParams(location.search);
  const denied = params.get('denied');
  const why = params.get('why');
  if (params.has('denied')) history.replaceState(null, '', '/');
  try {
    const info = await api('GET', '/me');
    loginOptions = { google: info.google, password: info.password };
    if (info.authenticated) return await showApp(info.user);
    showLogin();
    if (denied !== null) {
      $('#login-err').textContent = !denied
        ? 'Logowanie przez Google nie powiodło się. Spróbuj ponownie.'
        : why === 'domain' ? `Logowanie z domeny ${denied.split('@')[1]} jest zablokowane. Użyj konta z dozwolonej domeny.`
        : why === 'blocked' ? `Konto ${denied} ma zablokowany dostęp do tej tablicy.`
        : why === 'unverified' ? `Google nie potwierdził adresu ${denied}.`
        : `Konto ${denied} nie ma zaproszenia do tej tablicy. Poproś administratora o dodanie.`;
    } else if (info.password && !info.passwordSet && !info.google) {
      $('#login-err').textContent = 'Hasło nie jest ustawione. Na serwerze uruchom: IdeaBoard.exe set-password';
    } else if (!info.password && !info.google) {
      $('#login-err').textContent = 'Żadna metoda logowania nie jest włączona (appsettings.json).';
    }
  } catch (e) {
    showLogin();
    $('#login-err').textContent = e.message;
  }
})();
