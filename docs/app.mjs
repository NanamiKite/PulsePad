// SPDX-License-Identifier: GPL-3.0-only
import {patterns, normalize, sample, actuatorFor, messages, RumblePlayer} from './engine.mjs';

const $ = id => document.getElementById(id);
const svgNS = 'http://www.w3.org/2000/svg';
let settings = normalize(), pads = [], selected = null, signature = '', readError = '';
let dirty = true, lastFrame = 0, previewWindow = -1;
const canRead = isSecureContext && typeof navigator.getGamepads === 'function';
const player = new RumblePlayer({getPads: () => navigator.getGamepads(), visible: () => !document.hidden,
  onChange: () => { dirty = true; refreshControls(); }});

function pathFor(channel, width, height, start, duration, values) {
  let path = '';
  const steps = width < 100 ? 64 : 480;
  for (let i = 0; i <= steps; i++) {
    const value = sample(values, start + i / steps * duration)[channel];
    path += `${i ? 'L' : 'M'}${(i / steps * width).toFixed(2)},${(height - value * (height - 6)).toFixed(2)}`;
  }
  return path;
}

for (const pattern of patterns) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'pattern-button';
  button.dataset.pattern = pattern.id;
  button.setAttribute('aria-pressed', String(pattern.id === settings.pattern));
  button.title = pattern.description;
  const name = document.createElement('span');
  name.textContent = pattern.name;
  const svg = document.createElementNS(svgNS, 'svg');
  svg.setAttribute('viewBox', '0 0 48 17');
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS(svgNS, 'path');
  path.setAttribute('d', pathFor('left', 48, 16, 0, 3, {pattern: pattern.id, period: 1.5, left: 1, right: 1}));
  svg.append(path);
  button.append(name, svg);
  button.addEventListener('click', () => {
    settings = normalize({...settings, pattern: pattern.id});
    updateSettings();
  });
  $('pattern-list').append(button);
}

function updateSettings() {
  const index = patterns.findIndex(item => item.id === settings.pattern);
  const pattern = patterns[index];
  for (const button of document.querySelectorAll('.pattern-button')) {
    button.setAttribute('aria-pressed', String(button.dataset.pattern === pattern.id));
  }
  $('pattern-title').textContent = pattern.name;
  $('pattern-description').textContent = pattern.description;
  $('pattern-number').textContent = `${String(index + 1).padStart(2, '0')} / 16`;
  $('period').min = pattern.minPeriod;
  $('period').value = settings.period;
  $('period').setAttribute('aria-valuetext', `${settings.period.toFixed(1)} 秒`);
  $('period-value').replaceChildren(document.createTextNode(settings.period.toFixed(1) + ' '));
  const seconds = document.createElement('small'); seconds.textContent = '秒'; $('period-value').append(seconds);
  for (const side of ['left', 'right']) {
    const value = Math.round(settings[side] * 100);
    $(side).value = value;
    $(side).setAttribute('aria-valuetext', `${value}%`);
    $(side + '-value').replaceChildren(document.createTextNode(value));
    const unit = document.createElement('span'); unit.textContent = '%'; $(side + '-value').append(unit);
  }
  for (const id of ['left', 'right', 'period']) {
    const input = $(id);
    input.style.setProperty('--fill', `${(Number(input.value) - Number(input.min)) / (Number(input.max) - Number(input.min)) * 100}%`);
  }
  player.update(settings);
  dirty = true;
}

for (const id of ['left', 'right', 'period']) {
  $(id).addEventListener('input', () => {
    settings = normalize({...settings, [id]: Number($(id).value) / (id === 'period' ? 1 : 100)});
    updateSettings();
  });
}

function duration() {
  return document.querySelector('input[name=timing]:checked').value === 'infinite' ? null : Number($('duration').value);
}

function validDuration() {
  const value = duration();
  return value === null || Number.isInteger(value) && value >= 1 && value <= 86400;
}

function formatTime(seconds) {
  const total = Math.max(0, Math.ceil(seconds));
  const hours = Math.floor(total / 3600), minutes = Math.floor(total % 3600 / 60), remainder = total % 60;
  return (hours ? String(hours).padStart(2, '0') + ':' : '') + String(minutes).padStart(2, '0') + ':' + String(remainder).padStart(2, '0');
}

function updateClock() {
  const state = player.snapshot();
  $('clock').textContent = state.active ? state.remaining === null ? '∞' : formatTime(state.remaining) :
    duration() === null ? '∞' : validDuration() ? formatTime(duration()) : '—';
  $('clock-label').textContent = duration() === null ? '不限时长' : state.active ? '剩余时间' : '播放时长';
  const progress = state.active && state.duration !== null ? Math.min(1, state.elapsed / state.duration) : 0;
  $('transport-progress').style.transform = `scaleX(${progress})`;
}

function refreshControls() {
  const state = player.snapshot(), supported = Boolean(actuatorFor(selected));
  const valid = validDuration();
  $('start').disabled = state.active || !supported || !valid || !canRead || Boolean(readError) || document.hidden;
  $('stop').disabled = !state.active;
  $('device').disabled = state.active || pads.length === 0;
  $('time-options').disabled = state.active;
  $('duration').disabled = duration() === null;
  $('duration').setAttribute('aria-invalid', String(!valid));
  $('duration-error').hidden = valid;
  $('status-dot').classList.toggle('active', state.active);
  const status = state.active ? '正在播放' : !valid ? messages.duration : readError ||
    messages[state.reason] || (supported ? '准备就绪' : selected ? '当前设备无法震动' : '等待连接手柄');
  if ($('status').textContent !== status) $('status').textContent = status;
  $('stop-hint').textContent = selected?.mapping === 'standard' ? 'Esc / 手柄右侧面键停止' : 'Esc 停止';
  updateClock();
}

function refreshDevices() {
  if (!canRead) {
    readError = !isSecureContext ? '请通过 HTTPS 或 localhost 打开此页面' : '此浏览器无法读取手柄，请换用 Chrome / Edge';
    $('device-hint').textContent = readError;
    refreshControls();
    return;
  }
  try {
    pads = Array.from(navigator.getGamepads()).filter(pad => pad?.connected);
    readError = '';
  } catch {
    pads = [];
    readError = messages.unavailable;
    player.stop('unavailable');
  }
  if (player.active) {
    const target = pads.find(pad => pad.index === player.index && pad.id === player.id);
    if (!target) player.stop('disconnected');
  }
  const previous = selected;
  selected = pads.find(pad => pad.index === previous?.index && pad.id === previous.id) ||
    pads.find(pad => actuatorFor(pad)) || pads[0] || null;
  const nextSignature = JSON.stringify(pads.map(pad => [pad.index, pad.id, Boolean(actuatorFor(pad))]));
  if (nextSignature !== signature) {
    signature = nextSignature;
    $('device').replaceChildren();
    if (!pads.length) {
      $('device').append(new Option('等待连接手柄', ''));
    } else {
      for (const pad of pads) {
        $('device').append(new Option(`${pad.index + 1} · ${pad.id}${actuatorFor(pad) ? '' : '（不支持震动）'}`, String(pad.index)));
      }
    }
  }
  if (selected) $('device').value = selected.index;
  const supported = Boolean(actuatorFor(selected));
  const badge = $('connection');
  badge.className = 'connection' + (supported ? ' ready' : selected ? ' unsupported' : '');
  badge.querySelector('span').textContent = supported ? '可震动' : selected ? '不可震动' : '未连接';
  $('device-hint').textContent = readError || (supported ? '已连接 · 点击开始后才会震动' :
    selected ? '浏览器未提供震动功能，可尝试 USB 连接或桌面版。' : '连接后按一下手柄按键，即可在这里选择。');
  refreshControls();
}

$('device').addEventListener('change', () => {
  player.stop();
  selected = pads.find(pad => String(pad.index) === $('device').value) || null;
  player.reason = '';
  refreshDevices();
});
for (const input of document.querySelectorAll('input[name=timing], #duration')) {
  input.addEventListener('input', refreshControls);
}
$('start').addEventListener('click', () => {
  refreshDevices();
  if (selected && validDuration() && !$('start').disabled) player.start(selected.index, selected.id, duration());
});
$('stop').addEventListener('click', () => player.stop());
document.addEventListener('keydown', event => { if (event.key === 'Escape') player.stop(); });
window.addEventListener('gamepadconnected', refreshDevices);
window.addEventListener('gamepaddisconnected', event => {
  player.disconnect(event.gamepad.index);
  refreshDevices();
});
document.addEventListener('visibilitychange', () => {
  if (document.hidden) player.stop('hidden');
  else { refreshDevices(); dirty = true; }
});
window.addEventListener('pagehide', () => player.stop('hidden'));
window.addEventListener('pageshow', refreshDevices);
document.addEventListener('freeze', () => player.stop('hidden'));
window.addEventListener('beforeunload', () => player.stop('hidden'));
$('help-open').addEventListener('click', () => $('help').showModal());
$('help-close').addEventListener('click', () => $('help').close());
$('help').addEventListener('click', event => { if (event.target === $('help')) $('help').close(); });

function render(time) {
  const state = player.snapshot();
  const start = state.active ? Math.floor(state.elapsed / 6) * 6 : 0;
  if (start !== previewWindow) { dirty = true; previewWindow = start; }
  if (dirty) {
    const left = pathFor('left', 720, 140, start, 6, settings);
    $('wave-left').setAttribute('d', left);
    $('wave-left-fill').setAttribute('d', left + 'L720,140L0,140Z');
    $('wave-right').setAttribute('d', pathFor('right', 720, 140, start, 6, settings));
    $('axis-start').textContent = start + ' s';
    $('axis-end').textContent = start + 6 + ' s';
    $('playhead').setAttribute('visibility', state.active ? 'visible' : 'hidden');
    dirty = false;
  }
  if (state.active && time - lastFrame > 33) {
    const x = (state.elapsed - start) / 6 * 720;
    $('playhead').setAttribute('x1', x);
    $('playhead').setAttribute('x2', x);
    updateClock();
    lastFrame = time;
  }
  requestAnimationFrame(render);
}

updateSettings();
refreshDevices();
// Discovery never requests vibration. The playback clock lives in RumblePlayer.
setInterval(() => { if (!document.hidden) refreshDevices(); }, 500);
requestAnimationFrame(render);
