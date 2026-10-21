// SPDX-License-Identifier: GPL-3.0-only
// Dependency-free headless Chromium checks. No real gamepad is ever passed to the page.
// node tests/web-ui.mjs [absolute path to chrome.exe or msedge.exe]
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {access, mkdir, mkdtemp, readFile, writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {setTimeout as delay} from 'node:timers/promises';
import {createWebServer} from '../tools/serve-web.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
const candidates = [process.argv[2], process.env.CHROME_PATH,
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  '/usr/bin/chromium', '/usr/bin/google-chrome',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'].filter(Boolean);
let browser;
for (const candidate of candidates) { try { await access(candidate); browser = candidate; break; } catch {} }
assert.ok(browser, 'Install Chrome/Edge or pass its executable path');
const profile = await mkdtemp(path.join(tmpdir(), 'pulsepad-web-test-'));
const server = createWebServer();
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}/PulsePad/`;
const child = spawn(browser, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`,
  '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--disable-background-networking',
  '--disable-component-update', '--disable-sync', '--disable-gpu', 'about:blank'], {windowsHide: true, stdio: 'ignore'});
let socket, sequence = 0, browserError;
const pending = new Map(), errors = [], networkErrors = [];
child.on('error', error => { browserError = error; });
async function poll(fn, label, timeout = 10000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await fn()) return; await delay(50); }
  throw new Error(`Timed out: ${label}`);
}
function cdp(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, 10000);
    pending.set(id, {resolve: value => { clearTimeout(timeout); resolve(value); }, reject: error => { clearTimeout(timeout); reject(error); }});
    socket.send(JSON.stringify({id, method, params}));
  });
}
async function evaluate(expression) {
  const result = await cdp('Runtime.evaluate', {expression, returnByValue: true, awaitPromise: true, userGesture: true});
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
  return result.result.value;
}
const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
const setValue = (id, value) => evaluate(`(() => { const input = document.getElementById(${JSON.stringify(id)}); input.value = ${JSON.stringify(String(value))}; input.dispatchEvent(new Event('input', {bubbles:true})); })()`);
const state = () => evaluate(`({startDisabled: document.getElementById('start').disabled, stopDisabled: document.getElementById('stop').disabled, status: document.getElementById('status').textContent, writes: window.fixture.writes.length, clock: document.getElementById('clock').textContent})`);

try {
  let port;
  await poll(async () => {
    if (browserError) throw browserError;
    try { port = Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); return Boolean(port); } catch { return false; }
  }, 'headless browser startup');
  const page = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, {method: 'PUT'})).json();
  socket = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, {once: true}); socket.addEventListener('error', reject, {once: true}); });
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const entry = pending.get(message.id); if (!entry) return; pending.delete(message.id);
      if (message.error) entry.reject(new Error(message.error.message)); else entry.resolve(message.result);
    } else if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails);
    else if (message.method === 'Network.loadingFailed') networkErrors.push(message.params.errorText);
    else if (message.method === 'Network.responseReceived' && message.params.response.status >= 400) networkErrors.push(message.params.response.url);
  });
  await cdp('Page.enable'); await cdp('Runtime.enable'); await cdp('Network.enable');
  await cdp('Page.addScriptToEvaluateOnNewDocument', {source: `
    window.fixture = {writes: [], pads: [], hidden: false, denied: false, failWrite: false};
    Object.defineProperty(navigator, 'getGamepads', {value: () => {
      if (fixture.denied) throw new DOMException('Denied', 'SecurityError'); return fixture.pads;
    }});
    Object.defineProperty(document, 'hidden', {get: () => fixture.hidden});
    fixture.add = (id = 'Xbox Controller (mock)', index = 0, supported = true, mapping = 'standard') => {
      const pad = {id, index, connected: true, mapping, buttons: Array.from({length: 16}, () => ({pressed: false, value: 0})),
        vibrationActuator: {effects: supported ? ['dual-rumble'] : [],
          playEffect: (type, value) => { fixture.writes.push({index, type, ...value}); return fixture.failWrite ? Promise.reject(new Error('Output failed')) : Promise.resolve('preempted'); },
          reset: () => { fixture.writes.push({index, reset: true}); return Promise.resolve('complete'); }}};
      fixture.pads[index] = pad; return pad;
    };
    fixture.connect = () => window.dispatchEvent(new Event('gamepadconnected'));
    fixture.setHidden = value => { fixture.hidden = value; document.dispatchEvent(new Event('visibilitychange')); };
  `});
  await cdp('Emulation.setDeviceMetricsOverride', {width: 1440, height: 1080, deviceScaleFactor: 1, mobile: false});
  await cdp('Page.navigate', {url: base});
  await poll(() => evaluate(`document.querySelectorAll('.pattern-button').length === 16`), 'web app initialization');
  assert.equal((await state()).startDisabled, true);
  assert.equal((await state()).writes, 0);
  console.log('PASS: relative module loading on /PulsePad/, empty connection state');

  await evaluate(`fixture.add(); fixture.connect()`);
  assert.equal((await state()).startDisabled, false);
  for (const id of ['constant','breathe','wave','pulse','double','triple','heartbeat','burst','rampUp','rampDown','triangle','stepUp','stepDown','alternate','crossfade','random']) {
    await click(`[data-pattern="${id}"]`);
    assert.equal(await evaluate(`document.querySelector('[data-pattern="${id}"]').getAttribute('aria-pressed')`), 'true');
  }
  await setValue('period', '.2'); await click('[data-pattern="burst"]');
  assert.equal(await evaluate(`document.getElementById('period').value`), '0.8');
  assert.equal((await state()).writes, 0);
  console.log('PASS: all pattern controls, period clamp, no output while idle');

  await setValue('duration', ''); assert.equal((await state()).startDisabled, true);
  assert.equal(await evaluate(`document.getElementById('duration-error').hidden`), false);
  await setValue('duration', '86401'); assert.equal((await state()).startDisabled, true);
  await click('input[value="infinite"]'); assert.equal((await state()).clock, '∞');
  await click('input[value="timed"]'); assert.equal((await state()).startDisabled, true);
  await setValue('duration', '1'); await click('[data-pattern="constant"]'); await click('#start');
  assert.equal((await state()).stopDisabled, false);
  assert.equal(await evaluate(`document.getElementById('time-options').disabled && document.getElementById('device').disabled`), true);
  await setValue('left', '43'); await setValue('right', '0');
  await poll(() => evaluate(`fixture.writes.some(value => value.strongMagnitude === .43 && value.weakMagnitude === 0)`), 'live motor controls');
  await poll(async () => (await state()).stopDisabled, 'one second timer');
  assert.equal((await state()).status, '定时结束');
  console.log('PASS: finite/infinite validation, live strength edits, lock settings, timed stop');

  await click('input[value="infinite"]'); await click('#start');
  await evaluate(`fixture.setHidden(true)`); assert.equal((await state()).stopDisabled, true);
  const stoppedWrites = (await state()).writes;
  await evaluate(`fixture.setHidden(false)`); await delay(150);
  assert.equal((await state()).writes, stoppedWrites);
  await click('#start');
  await cdp('Input.dispatchKeyEvent', {type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27});
  await cdp('Input.dispatchKeyEvent', {type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27});
  assert.equal((await state()).stopDisabled, true);
  await click('#start'); await evaluate(`fixture.pads[0].buttons[1].pressed = true`);
  await poll(async () => (await state()).status === '已通过手柄停止', 'controller stop button');
  await evaluate(`fixture.pads[0].buttons[1].pressed = false`);
  console.log('PASS: hidden-page stop, no auto resume, Escape and controller stop');

  await evaluate(`fixture.add('Other pad', 1); fixture.connect()`);
  await click('#start');
  await evaluate(`const event = new Event('gamepaddisconnected'); event.gamepad = fixture.pads[0]; fixture.pads[0] = null; window.dispatchEvent(event)`);
  assert.equal((await state()).stopDisabled, true);
  assert.equal(await evaluate(`fixture.writes.some(value => value.index === 1 && !value.reset)`), false);
  await click('#start'); await delay(40); await click('#stop');
  assert.equal(await evaluate(`fixture.writes.some(value => value.index === 1 && !value.reset)`), true);
  await evaluate(`fixture.failWrite = true`); await click('#start');
  await poll(async () => (await state()).status.includes('震动指令未成功'), 'write error feedback');
  await evaluate(`fixture.failWrite = false; fixture.pads = []; fixture.add('<img src=x onerror=alert(1)>', 0, false); fixture.connect()`);
  assert.equal((await state()).startDisabled, true);
  assert.equal(await evaluate(`document.querySelector('#device img') !== null`), false);
  await evaluate(`fixture.denied = true; fixture.connect()`);
  assert.equal((await state()).startDisabled, true);
  assert.ok((await state()).status.includes('浏览器无法读取'));
  console.log('PASS: hot unplug, explicit target start, write errors, unsupported and denied devices, safe device labels');

  await evaluate(`fixture.denied = false; fixture.pads = []; fixture.add(); fixture.connect()`);
  await click('[data-pattern="breathe"]'); await setValue('left', '20'); await setValue('right', '20'); await setValue('period', '1.5');
  await click('input[value="timed"]'); await setValue('duration', '120');
  // Select the device to clear any previous stop/error state before rendering the idle view.
  await evaluate(`document.getElementById('device').dispatchEvent(new Event('change'))`);
  await mkdir(path.join(root, 'build'), {recursive: true});
  for (const [name, width, height] of [['desktop', 1440, 1080], ['tablet', 768, 1024], ['mobile', 390, 1000], ['small', 320, 700]]) {
    await cdp('Emulation.setDeviceMetricsOverride', {width, height, deviceScaleFactor: 1, mobile: false});
    await delay(80);
    assert.ok(await evaluate(`document.documentElement.scrollWidth <= document.documentElement.clientWidth`), `${name}: horizontal overflow`);
    assert.ok(await evaluate(`Array.from(document.querySelectorAll('.pattern-button, #start, #stop, #left, #right')).every(element => { const box = element.getBoundingClientRect(); return box.width > 0 && box.left >= 0 && box.right <= document.documentElement.clientWidth; })`), `${name}: visible controls`);
    if (name === 'desktop' || name === 'mobile') {
      const metrics = await cdp('Page.getLayoutMetrics');
      const content = metrics.cssContentSize;
      // Render the whole document in the viewport so sticky controls do not cover fields in the artifact.
      await cdp('Emulation.setDeviceMetricsOverride', {width, height: Math.ceil(content.height), deviceScaleFactor: 1, mobile: false});
      await delay(50);
      const screenshot = await cdp('Page.captureScreenshot', {format: 'png', captureBeyondViewport: true,
        clip: {x: 0, y: 0, width, height: content.height, scale: 1}});
      await writeFile(path.join(root, 'build', `web-${name}.png`), Buffer.from(screenshot.data, 'base64'));
    }
  }
  await click('#help-open'); assert.equal(await evaluate(`document.getElementById('help').open`), true);
  await click('#help-close'); assert.equal(await evaluate(`document.getElementById('help').open`), false);
  assert.deepEqual(errors, []);
  assert.deepEqual(networkErrors, []);
  for (const file of ['index.html', 'styles.css', 'app.mjs', 'engine.mjs', 'icon.svg', 'LICENSE', '.nojekyll']) {
    assert.equal((await fetch(base + file)).status, 200, file);
  }
  assert.equal((await fetch(base + '%2e%2e%5cREADME.md')).status, 403);
  assert.equal((await fetch(base + '%')).status, 404);
  console.log('PASS: 320/390/768/1440 px layouts, help dialog, all static assets, no browser errors');
  console.log('All web UI checks passed. Headless mocks only; no physical controller output.');
} finally {
  if (socket?.readyState === WebSocket.OPEN) {
    try { await cdp('Browser.close'); } catch {} socket.close();
  }
  if (child.exitCode === null) child.kill();
  for (const entry of pending.values()) entry.reject(new Error('Browser closed'));
  pending.clear();
  await new Promise(resolve => server.close(resolve));
  // Keep the isolated temporary profile for debugging. Never delete browser/user directories.
}
