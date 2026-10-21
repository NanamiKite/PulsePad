// SPDX-License-Identifier: GPL-3.0-only
import test from 'node:test';
import assert from 'node:assert/strict';
import {patterns, sample, normalize, actuatorFor, RumblePlayer} from '../docs/engine.mjs';

function fixture() {
  let time = 0, next = 0, visible = true, denied = false;
  const pending = new Map(), writes = [], events = [];
  const pad = {index: 0, id: 'Test pad', connected: true, mapping: 'standard', buttons: [{pressed: false}, {pressed: false}],
    vibrationActuator: {effects: ['dual-rumble'], playEffect: (type, effect) => { writes.push({type, ...effect}); return Promise.resolve('preempted'); },
      reset: () => { writes.push({reset: true}); return Promise.resolve('complete'); }}};
  let pads = [pad];
  const player = new RumblePlayer({now: () => time, visible: () => visible, getPads: () => {
    if (denied) throw new Error('Permission denied'); return pads;
  }, schedule: (callback, delay) => { pending.set(++next, {callback, at: time + delay}); return next; },
  cancel: id => pending.delete(id), onChange: state => events.push(state)});
  function advance(milliseconds) {
    const end = time + milliseconds;
    for (;;) {
      const entry = [...pending].sort((a, b) => a[1].at - b[1].at)[0];
      if (!entry || entry[1].at > end) break;
      pending.delete(entry[0]); time = entry[1].at; entry[1].callback();
    }
    time = end;
  }
  return {player, pad, writes, events, pending, advance, start: duration => player.start(0, pad.id, duration),
    hide: () => { visible = false; }, deny: () => { denied = true; }, setPads: value => { pads = value; },
    stall: milliseconds => { time += milliseconds; player.tick(); }};
}

test('all sixteen patterns stay within independently configured motor limits', () => {
  assert.equal(new Set(patterns.map(p => p.id)).size, 16);
  for (const pattern of patterns) {
    for (const period of [.2, .8, 1.5, 5]) {
      for (let t = 0; t < 12; t += .013) {
        const value = sample({pattern: pattern.id, period, left: .31, right: .73}, t);
        assert.ok(value.left >= 0 && value.left <= .31, pattern.id);
        assert.ok(value.right >= 0 && value.right <= .73, pattern.id);
      }
    }
    assert.deepEqual(sample({pattern: pattern.id, period: 1, left: 0, right: 0}, 2.3), {left: 0, right: 0});
  }
});

test('pulse timings, complementary channels, floors and smooth random sequence match the desktop patterns', () => {
  const output = (pattern, t) => sample({pattern, period: 1, left: 1, right: 1}, t);
  assert.deepEqual(output('alternate', .25), {left: 1, right: 0});
  assert.deepEqual(output('alternate', .75), {left: 0, right: 1});
  assert.equal(output('pulse', .5).left, 0);
  assert.equal(output('breathe', .5).left, 1);
  assert.equal(output('wave', 0).left, .3);
  assert.equal(output('double', .25).left, 1);
  assert.equal(output('double', .4).left, 0);
  assert.equal(output('triple', .45).left, 1);
  assert.equal(output('burst', .13).left, 1);
  assert.equal(output('burst', .7).left, 0);
  assert.ok(Math.abs(output('heartbeat', .33).left - .6) < 1e-9);
  assert.equal(output('stepUp', .5).left, .6);
  assert.equal(output('stepDown', .9).left, .2);
  for (let time = 0; time < 7; time += .037) {
    const value = output('crossfade', time);
    assert.ok(Math.abs(value.left + value.right - 1) < 1e-10);
    assert.deepEqual(output('random', time), output('random', time));
  }
  assert.ok(Math.abs(output('random', .99999).left - output('random', 1.00001).left) < 1e-7);
});

test('settings clamp unsafe input and enforce short-pattern minimum periods', () => {
  assert.deepEqual(normalize(), {pattern: 'breathe', period: 1.5, left: .2, right: .2});
  assert.deepEqual(normalize({pattern: 'burst', period: .1, left: -100, right: 100}), {pattern: 'burst', period: .8, left: 0, right: 1});
  assert.ok(Number.isFinite(sample({left: NaN, right: Infinity, period: NaN}, Infinity).left));
});

test('capabilities honor explicit effects, older type and disconnected devices', () => {
  const {pad} = fixture();
  assert.ok(actuatorFor(pad));
  pad.vibrationActuator.effects = [];
  pad.vibrationActuator.type = 'dual-rumble';
  assert.equal(actuatorFor(pad), null);
  delete pad.vibrationActuator.effects;
  assert.ok(actuatorFor(pad));
  pad.connected = false;
  assert.equal(actuatorFor(pad), null);
  assert.equal(actuatorFor(null), null);
});

test('discovery, idle edits and invalid durations never send output', () => {
  const f = fixture();
  f.player.update({pattern: 'constant', left: 1, right: 1});
  f.advance(10000);
  for (const duration of [0, -1, 86401, .1, NaN, Infinity, undefined, '120']) assert.equal(f.start(duration), false);
  assert.equal(f.writes.length, 0);
  assert.equal(f.pending.size, 0);
});

test('finite playback renews short effects and stops at the wall-clock deadline', () => {
  const f = fixture();
  f.player.update({pattern: 'constant', left: .2, right: .3});
  assert.ok(f.start(1));
  f.advance(975);
  assert.ok(f.player.active);
  assert.equal(f.writes.length, 40);
  for (const effect of f.writes) {
    assert.equal(effect.strongMagnitude, .2);
    assert.equal(effect.weakMagnitude, .3);
    assert.ok(effect.duration > 0 && effect.duration <= 100);
  }
  assert.equal(f.writes.at(-1).duration, 25);
  f.advance(25);
  assert.equal(f.player.active, false);
  assert.equal(f.player.reason, 'finished');
  assert.deepEqual(f.writes.at(-1), {reset: true});
  assert.equal(f.pending.size, 0);
  const count = f.writes.length;
  f.advance(5000);
  assert.equal(f.writes.length, count);
});

test('unlimited playback, live settings and manual stop work without restarting the clock', () => {
  const f = fixture();
  assert.ok(f.start(null));
  f.advance(125000);
  assert.equal(f.player.snapshot().remaining, null);
  assert.equal(f.player.snapshot().elapsed, 125);
  f.player.update({pattern: 'constant', left: .9, right: 0});
  f.advance(25);
  assert.equal(f.writes.at(-1).strongMagnitude, .9);
  assert.equal(f.writes.at(-1).weakMagnitude, 0);
  f.player.stop();
  assert.equal(f.player.active, false);
  assert.equal(f.pending.size, 0);
});

test('hidden, disconnected, replaced, blocked and stalled sessions stop without rerouting', () => {
  for (const [reason, change] of [
    ['hidden', f => f.hide()], ['disconnected', f => f.setPads([])],
    ['changed', f => f.setPads([{...f.pad, id: 'Another controller'}])],
    ['unavailable', f => f.deny()], ['delayed', f => f.stall(500)],
  ]) {
    const f = fixture(); f.start(null); change(f); f.advance(25);
    assert.equal(f.player.reason, reason);
    assert.equal(f.player.active, false);
    assert.deepEqual(f.writes.at(-1), {reset: true});
    assert.equal(f.writes.filter(effect => !effect.reset).length, 1);
  }
});

test('disconnect events stop a session even when the same index and ID reconnect quickly', () => {
  const f = fixture(); f.start(null);
  f.player.disconnect(2);
  assert.ok(f.player.active);
  f.player.disconnect(0);
  f.advance(1000);
  assert.equal(f.player.reason, 'disconnected');
  assert.equal(f.writes.length, 2);
});

test('standard right face button stops, held button blocks start, unmapped buttons are not guessed', () => {
  const f = fixture(); f.pad.buttons[1].pressed = true;
  assert.equal(f.start(null), false);
  assert.equal(f.player.reason, 'held');
  assert.equal(f.writes.length, 0);
  f.pad.buttons[1].pressed = false; f.start(null);
  f.pad.buttons[1].pressed = true; f.advance(25);
  assert.equal(f.player.reason, 'button');
  f.pad.mapping = ''; assert.ok(f.start(null));
  f.advance(25); assert.ok(f.player.active); f.player.stop();
});

test('preempted promises are normal and obsolete failures cannot stop a newer session', async () => {
  const f = fixture(); f.start(null); f.advance(100);
  await Promise.resolve(); assert.ok(f.player.active); f.player.stop();
  let rejectOld;
  f.pad.vibrationActuator.playEffect = () => new Promise((_, reject) => { rejectOld = reject; });
  f.start(null); f.player.stop();
  f.pad.vibrationActuator.playEffect = () => Promise.resolve('preempted');
  f.start(null); rejectOld(new Error('Old device error'));
  await Promise.resolve(); assert.ok(f.player.active); f.player.stop();
});

test('synchronous and asynchronous write failures stop and cancel the next tick', async () => {
  for (const async of [false, true]) {
    const f = fixture();
    f.pad.vibrationActuator.playEffect = () => { if (async) return Promise.reject(new Error()); throw new Error(); };
    f.start(null); await Promise.resolve();
    assert.equal(f.player.active, false);
    assert.equal(f.player.reason, 'failed');
    assert.equal(f.pending.size, 0);
  }
});

test('unsupported/hidden/permission denied starts never write; reset fallback sends only zero', async () => {
  for (const change of [f => f.hide(), f => f.deny(), f => { f.pad.vibrationActuator.effects = []; }]) {
    const f = fixture(); change(f); assert.equal(f.start(null), false); assert.equal(f.writes.length, 0);
  }
  const f = fixture(); delete f.pad.vibrationActuator.reset;
  f.start(null); f.player.stop();
  assert.equal(f.writes.at(-1).strongMagnitude, 0);
  assert.equal(f.writes.at(-1).weakMagnitude, 0);
  const g = fixture(); g.pad.vibrationActuator.reset = () => Promise.reject(new Error());
  g.start(null); g.player.stop(); await Promise.resolve(); assert.equal(g.player.active, false);
});
