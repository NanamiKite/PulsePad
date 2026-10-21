// SPDX-License-Identifier: GPL-3.0-only
export const patterns = Object.freeze([
  ['constant', '持续', '保持设定强度', .2],
  ['breathe', '呼吸', '平滑地增强，再慢慢减弱', .2],
  ['wave', '起伏', '在三成与满强度之间柔和变化', .2],
  ['pulse', '脉冲', '震动半拍，停半拍', .2],
  ['double', '双击', '连续两下，然后停顿', .4],
  ['triple', '三连击', '连续三下，然后停顿', .5],
  ['heartbeat', '心跳', '一下重、一下轻，留一段间隔', .5],
  ['burst', '短连发', '快速震动四下，再休息半拍', .8],
  ['rampUp', '渐强', '从零逐渐增强，随后重新开始', .2],
  ['rampDown', '渐弱', '从设定强度逐渐减弱', .2],
  ['triangle', '往复', '匀速增强，再匀速减弱', .2],
  ['stepUp', '阶梯上升', '分五档逐步增强', .2],
  ['stepDown', '阶梯下降', '分五档逐步减弱', .2],
  ['alternate', '左右交替', '两个马达各震动半拍', .2],
  ['crossfade', '左右交融', '一侧增强时，另一侧逐渐减弱', .2],
  ['random', '随机变化', '每拍换一个强度，平滑过渡', .2],
].map(([id, name, description, minPeriod]) => Object.freeze({id, name, description, minPeriod})));

export function clamp(value, min, max, fallback = min) {
  const number = Number(value);
  return Number.isFinite(number) ? Math.max(min, Math.min(max, number)) : fallback;
}

export function normalize(settings = {}) {
  const pattern = patterns.find(item => item.id === settings.pattern) || patterns[1];
  return {pattern: pattern.id, period: clamp(settings.period, pattern.minPeriod, 5, 1.5),
    left: clamp(settings.left, 0, 1, .2), right: clamp(settings.right, 0, 1, .2)};
}

function bell(phase, start, width) {
  return phase <= start || phase >= start + width ? 0 : Math.sin(Math.PI * (phase - start) / width) ** 2;
}

function noise(cycle) {
  const value = Math.sin((cycle + 1) * 127.1 + 311.7) * 43758.5453;
  return .15 + .85 * (value - Math.floor(value));
}

// Shared by preview and playback. Intensities are commands, not measured amplitudes.
export function sample(settings, seconds) {
  const s = normalize(settings);
  const cycles = Math.max(0, Number.isFinite(seconds) ? seconds : 0) / s.period;
  const cycle = Math.floor(cycles), phase = cycles - cycle;
  let left = 1, right = 1;
  switch (s.pattern) {
    case 'breathe': left = right = (1 - Math.cos(2 * Math.PI * phase)) / 2; break;
    case 'wave': left = right = .3 + .7 * (1 - Math.cos(2 * Math.PI * phase)) / 2; break;
    case 'pulse': left = right = phase < .5 ? 1 : 0; break;
    case 'double': left = right = phase < .12 || phase >= .24 && phase < .36 ? 1 : 0; break;
    case 'triple': left = right = phase < .1 || phase >= .2 && phase < .3 || phase >= .4 && phase < .5 ? 1 : 0; break;
    case 'heartbeat': left = right = Math.max(bell(phase, .02, .18), .6 * bell(phase, .25, .16)); break;
    case 'burst': left = right = phase < .5 && phase % .125 < .0625 ? 1 : 0; break;
    case 'rampUp': left = right = phase; break;
    case 'rampDown': left = right = 1 - phase; break;
    case 'triangle': left = right = 1 - Math.abs(2 * phase - 1); break;
    case 'stepUp': left = right = (Math.floor(phase * 5) + 1) / 5; break;
    case 'stepDown': left = right = (5 - Math.floor(phase * 5)) / 5; break;
    case 'alternate': left = phase < .5 ? 1 : 0; right = 1 - left; break;
    case 'crossfade': left = (1 - Math.cos(2 * Math.PI * phase)) / 2; right = 1 - left; break;
    case 'random': {
      const blend = phase * phase * (3 - 2 * phase);
      left = right = noise(cycle) * (1 - blend) + noise(cycle + 1) * blend;
      break;
    }
  }
  return {left: left * s.left, right: right * s.right};
}

export function actuatorFor(pad) {
  if (!pad || !pad.connected) return null;
  const actuator = pad.vibrationActuator;
  if (!actuator || typeof actuator.playEffect !== 'function') return null;
  // Older implementations expose type instead of effects. Explicit empty effects means unsupported.
  if (actuator.effects != null) return Array.from(actuator.effects).includes('dual-rumble') ? actuator : null;
  return actuator.type === 'dual-rumble' ? actuator : null;
}

export function stopPressed(pad) {
  return pad?.mapping === 'standard' && Boolean(pad.buttons?.[1]?.pressed);
}

export const messages = Object.freeze({
  stopped: '已停止', finished: '定时结束', hidden: '页面已转入后台，播放已停止',
  disconnected: '手柄已断开，播放已停止', changed: '目标手柄已变化，请重新选择',
  button: '已通过手柄停止', delayed: '页面响应中断，播放已停止',
  unavailable: '浏览器无法读取手柄，请检查权限或换用 Chrome / Edge',
  unsupported: '当前手柄未向浏览器提供震动功能',
  failed: '震动指令未成功，请重新连接手柄后再试',
  duration: '请输入 1–86400 之间的整数秒数', held: '请松开手柄右侧面键后再开始',
});

export class RumblePlayer {
  constructor({getPads, visible = () => true, now = () => performance.now(),
    schedule = (fn, delay) => setTimeout(fn, delay), cancel = id => clearTimeout(id), onChange = () => {}}) {
    Object.assign(this, {getPads, visible, now, schedule, cancel, onChange});
    this.settings = normalize();
    this.active = false;
    this.generation = 0;
    this.elapsed = 0;
    this.duration = 120;
    this.reason = '';
    this.timer = null;
  }

  update(settings) { this.settings = normalize(settings); }

  snapshot() {
    const elapsed = this.active ? Math.max(0, (this.now() - this.started) / 1000) : this.elapsed;
    return {active: this.active, elapsed, duration: this.duration, reason: this.reason,
      remaining: this.duration === null ? null : Math.max(0, this.duration - elapsed)};
  }

  start(index, id, duration) {
    if (this.active) return false;
    const fail = reason => { this.reason = reason; this.onChange(this.snapshot()); return false; };
    if (duration !== null && (!Number.isInteger(duration) || duration < 1 || duration > 86400)) return fail('duration');
    if (!this.visible()) return fail('hidden');
    let pad;
    try { pad = Array.from(this.getPads()).find(item => item?.index === index && item.id === id && item.connected); }
    catch { return fail('unavailable'); }
    if (!pad) return fail('disconnected');
    const actuator = actuatorFor(pad);
    if (!actuator) return fail('unsupported');
    if (stopPressed(pad)) return fail('held');
    Object.assign(this, {index, id, actuator, duration, active: true, reason: '', elapsed: 0,
      started: this.now(), lastTick: this.now()});
    this.generation++;
    this.onChange(this.snapshot());
    this.tick();
    return this.active;
  }

  stop(reason = 'stopped') {
    if (!this.active) return;
    this.elapsed = this.snapshot().elapsed;
    this.active = false;
    this.reason = reason;
    this.generation++;
    this.cancel(this.timer);
    this.timer = null;
    try {
      const result = typeof this.actuator.reset === 'function' ? this.actuator.reset() :
        this.actuator.playEffect('dual-rumble', {duration: 1, startDelay: 0, strongMagnitude: 0, weakMagnitude: 0});
      // A hidden document may reject reset; effects are also short-lived and the browser stops them on hide.
      Promise.resolve(result).catch(() => {});
    } catch { /* Device removal can also invalidate the actuator. Never restart automatically. */ }
    this.onChange(this.snapshot());
  }

  disconnect(index) { if (this.active && this.index === index) this.stop('disconnected'); }

  tick() {
    if (!this.active) return;
    if (!this.visible()) return this.stop('hidden');
    const time = this.now();
    if (this.duration !== null && time - this.started >= this.duration * 1000) return this.stop('finished');
    if (time - this.lastTick > 300) return this.stop('delayed');
    this.lastTick = time;
    let pad;
    try { pad = Array.from(this.getPads()).find(item => item?.index === this.index); }
    catch { return this.stop('unavailable'); }
    if (!pad?.connected) return this.stop('disconnected');
    if (pad.id !== this.id) return this.stop('changed');
    if (stopPressed(pad)) return this.stop('button');
    if (!actuatorFor(pad)) return this.stop('unsupported');
    const value = sample(this.settings, (time - this.started) / 1000);
    const token = this.generation;
    const fail = error => {
      if (this.active && this.generation === token) {
        this.stop(error?.name === 'NotSupportedError' ? 'unsupported' : !this.visible() ? 'hidden' : 'failed');
      }
    };
    try {
      const result = pad.vibrationActuator.playEffect('dual-rumble', {startDelay: 0,
        duration: this.duration === null ? 100 : Math.min(100, this.duration * 1000 - (time - this.started)),
        strongMagnitude: value.left, weakMagnitude: value.right});
      // Replacement resolves "preempted"; do not await it or treat it as an output error.
      Promise.resolve(result).catch(fail);
    } catch (error) { fail(error); }
    if (this.active) this.timer = this.schedule(() => this.tick(), 25);
  }
}
