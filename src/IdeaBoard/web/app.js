'use strict';

const $ = (sel, root = document) => root.querySelector(sel);
const boardEl = $('#board');
const dlg = $('#card-dlg');

let board = null;
let filterText = '';
let filterTag = null;
let editingId = null; // card open in the dialog; null = new card
let justDragged = false;

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
}

const findCard = id => board.cards.find(c => c.id === id);

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

function matches(card) {
  if (filterTag && !card.tags.some(t => t.toLowerCase() === filterTag)) return false;
  if (!filterText) return true;
  const hay = [card.title, card.description, ...card.tags, ...card.notes.map(n => n.text)].join('\n').toLowerCase();
  return filterText.split(/\s+/).every(w => hay.includes(w));
}

// ---------- render ----------

function render() {
  const scroll = boardEl.scrollLeft;
  const colScroll = {};
  for (const c of boardEl.querySelectorAll('.col')) colScroll[c.dataset.id] = $('.col-cards', c).scrollTop;

  boardEl.replaceChildren(...board.columns.map((col, i) => renderColumn(col, i)));
  renderTagFilter();

  boardEl.scrollLeft = scroll;
  for (const c of boardEl.querySelectorAll('.col')) $('.col-cards', c).scrollTop = colScroll[c.dataset.id] || 0;
}

function renderColumn(col, index) {
  const cards = board.cards.filter(c => c.columnId === col.id);
  const visible = cards.filter(matches).length;
  const count = visible === cards.length ? String(cards.length) : `${visible}/${cards.length}`;

  const name = el('div', { class: 'col-name', title: 'Kliknij, aby zmienić nazwę', onclick: () => renameColumn(col, name) }, col.name);
  const tools = el('div', { class: 'col-tools' },
    index > 0 ? el('button', { class: 'ghost', title: 'Przesuń w lewo', onclick: () => change('POST', `/columns/${col.id}/move`, { index: index - 1 }) }, '‹') : null,
    index < board.columns.length - 1 ? el('button', { class: 'ghost', title: 'Przesuń w prawo', onclick: () => change('POST', `/columns/${col.id}/move`, { index: index + 1 }) }, '›') : null,
    el('button', { class: 'ghost', title: 'Usuń kolumnę', onclick: () => deleteColumn(col, cards.length) }, '✕'),
  );

  return el('section', { class: 'col', dataset: { id: col.id } },
    el('div', { class: 'col-head' }, name, el('span', { class: 'col-count' }, count), tools),
    el('div', { class: 'col-cards' }, cards.map(renderCard)),
    renderQuickAdd(col),
  );
}

function renderCard(card) {
  const foot = el('div', { class: 'card-foot' }, card.tags.map(t => el('span', { class: 'chip' }, t)));
  if (card.notes.length) foot.append(el('span', { class: 'muted', title: 'Notatki' }, `✎ ${card.notes.length}`));
  return el('article', {
    class: `card p-${card.priority}${matches(card) ? '' : ' filtered'}`,
    tabindex: 0,
    dataset: { id: card.id },
  },
    el('div', { class: 'card-title' }, card.title),
    card.description ? el('div', { class: 'card-desc' }, card.description) : null,
    card.tags.length || card.notes.length ? foot : null,
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
        if (await change('POST', '/cards', { title, columnId: col.id })) {
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
  $('#f-column').replaceChildren(...board.columns.map(c => el('option', { value: c.id }, c.name)));
  $('#f-column').value = card?.columnId || columnId || board.columns[0].id;
  $('#f-note').value = '';
  $('#notes-section').hidden = !card;
  $('#delete-card').hidden = !card;
  $('#card-meta').textContent = card ? `Utworzono ${fmtDate(card.createdAt)}` : '';
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
      el('span', {}, fmtDate(n.at)),
      el('button', { type: 'button', class: 'ghost', onclick: () => {
        if (confirm('Usunąć notatkę?')) change('DELETE', `/cards/${card.id}/notes/${n.id}`);
      } }, 'usuń')),
    n.text)));
  $('#history').replaceChildren(...[...card.history].reverse().map(h =>
    el('li', {}, `${fmtDate(h.at)} — ${h.text}`)));
}

async function saveCard() {
  const body = {
    title: $('#f-title').value,
    description: $('#f-desc').value,
    tags: parseTags($('#f-tags').value),
    priority: $('#f-priority').value,
    columnId: $('#f-column').value,
  };
  if (!body.title.trim()) return toast('Tytuł jest wymagany.');
  const ok = editingId ? await change('PUT', `/cards/${editingId}`, body) : await change('POST', '/cards', body);
  if (ok) dlg.close();
}

$('#card-form').addEventListener('submit', e => { e.preventDefault(); saveCard(); });
dlg.addEventListener('keydown', e => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); saveCard(); }
});
for (const b of dlg.querySelectorAll('[data-close]')) b.addEventListener('click', () => dlg.close());
dlg.addEventListener('click', e => { if (e.target === dlg) dlg.close(); }); // backdrop
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

// ---------- drag & drop (mouse + touch long-press) ----------

let drag = null;

boardEl.addEventListener('pointerdown', e => {
  const card = e.target.closest('.card');
  if (!card || e.button !== 0) return;
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
  if (dlg.open || /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName)) return;
  if (e.key === '/') { e.preventDefault(); $('#search').focus(); }
  if (e.key === 'n' || e.key === 'N') { e.preventDefault(); openCard(null); }
});

async function refresh() {
  if (drag || dlg.open || document.activeElement?.closest?.('.col')?.querySelector('input')) return;
  try {
    const b = await api('GET', '/board');
    if (!board || b.rev !== board.rev) setBoard(b);
  } catch { /* api() already handled 401 */ }
}
document.addEventListener('visibilitychange', () => { if (!document.hidden && board) refresh(); });
setInterval(() => { if (!document.hidden && board) refresh(); }, 60_000);

// ---------- login ----------

function showLogin() {
  $('#app').hidden = true;
  $('#login').hidden = false;
  if (dlg.open) dlg.close();
  $('#login-pass').focus();
}

async function showApp() {
  $('#login').hidden = true;
  $('#app').hidden = false;
  setBoard(await api('GET', '/board'));
}

$('#login-form').addEventListener('submit', async e => {
  e.preventDefault();
  const err = $('#login-err');
  err.textContent = '';
  try {
    await api('POST', '/login', { password: $('#login-pass').value });
    $('#login-pass').value = '';
    await showApp();
  } catch (ex) {
    err.textContent = ex.message;
  }
});

$('#logout').addEventListener('click', async () => {
  await api('POST', '/logout').catch(() => {});
  board = null;
  showLogin();
});

(async () => {
  try {
    const me = await api('GET', '/me');
    if (me.authenticated) await showApp();
    else {
      showLogin();
      if (!me.passwordSet) $('#login-err').textContent = 'Hasło nie jest ustawione. Na serwerze uruchom: IdeaBoard.exe set-password';
    }
  } catch (e) {
    showLogin();
    $('#login-err').textContent = e.message;
  }
})();
