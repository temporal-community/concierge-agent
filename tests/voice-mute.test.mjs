import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";
import { test } from "node:test";
import assert from "node:assert/strict";

function createVoiceHarness({ meters = false } = {}) {
    const sockets = [];
    const audioNodes = [];
    const tracks = [{ enabled: true, stop() { this.stopped = true; } }];
    const analysers = [];
    const gains = [];
    const frames = [];

    class FakeAudioContext {
        audioWorklet = { addModule: async () => {} };
        destination = {};
        async resume() {}
        async suspend() {}
        async close() {}
        createMediaStreamSource() {
            return { connect() {} };
        }
    }

    // The AG-UI page's meters: analysers report a constant sample value; gains record their level.
    if (meters) {
        FakeAudioContext.prototype.createAnalyser = function () {
            const analyser = { fftSize: 2048, value: 0, getFloatTimeDomainData(buffer) { buffer.fill(this.value); } };
            analysers.push(analyser);
            return analyser;
        };
        FakeAudioContext.prototype.createGain = function () {
            const gain = { gain: { value: 1 }, connect() {} };
            gains.push(gain);
            return gain;
        };
    }

    class FakeAudioWorkletNode {
        port = {
            messages: [],
            postMessage(message) { this.messages.push(message); },
            onmessage: null,
        };

        constructor(_context, name) {
            this.name = name;
            audioNodes.push(this);
        }

        connect() {}
    }

    class FakeWebSocket {
        static CONNECTING = 0;
        static OPEN = 1;
        static CLOSED = 3;
        readyState = FakeWebSocket.OPEN;
        bufferedAmount = 0;
        sent = [];

        constructor() {
            sockets.push(this);
        }

        send(data) { this.sent.push(data); }
        close() { this.readyState = FakeWebSocket.CLOSED; }
    }

    const document = {
        visibilityState: "visible",
        addEventListener() {},
    };
    const window = { addEventListener() {} };
    runInNewContext(
        readFileSync(new URL("../src/Concierge.Web/wwwroot/js/voice.js", import.meta.url), "utf8"),
        {
            AudioContext: FakeAudioContext,
            AudioWorkletNode: FakeAudioWorkletNode,
            WebSocket: FakeWebSocket,
            document,
            window,
            location: { protocol: "https:", host: "hotel.test" },
            navigator: {
                mediaDevices: {
                    getUserMedia: async () => ({ getTracks: () => tracks }),
                },
            },
            URL,
            encodeURIComponent,
            ...(meters
                ? {
                    requestAnimationFrame: (callback) => frames.push(callback),
                    cancelAnimationFrame: () => {},
                }
                : {}),
        });

    // Runs the frames queued so far, as the browser would on the next repaint.
    const runFrames = () => frames.splice(0).forEach((callback) => callback());
    return { voice: window.conciergeVoice, sockets, audioNodes, tracks, document, analysers, gains, runFrames };
}

const flushMicrotasks = () => new Promise(resolve => setImmediate(resolve));

test("mute suppresses microphone frames while keeping the relay and playback active", async () => {
    const harness = createVoiceHarness();
    await harness.voice.start("credential", null, { invokeMethodAsync: async () => {} });
    const socket = harness.sockets[0];
    socket.onmessage({ data: JSON.stringify({ type: "SettingsApplied" }) });
    await flushMicrotasks();

    const microphone = harness.audioNodes.find(node => node.name === "concierge-mic");
    const playback = harness.audioNodes.find(node => node.name === "concierge-play");
    assert.ok(microphone);
    assert.ok(playback);

    const microphoneFrame = new ArrayBuffer(640);
    harness.voice.setMuted(true);
    assert.equal(harness.tracks[0].enabled, false);
    microphone.port.onmessage({ data: microphoneFrame });
    assert.deepEqual(socket.sent, []);
    assert.equal(socket.readyState, 1);

    socket.onmessage({ data: new ArrayBuffer(2) });
    assert.equal(playback.port.messages[0].type, "audio");
    playback.port.onmessage({ data: { type: "played", samples: 240 } });
    assert.deepEqual(socket.sent, ['{"type":"PlaybackAck","samples":240}']);

    harness.voice.setMuted(false);
    assert.equal(harness.tracks[0].enabled, true);
    microphone.port.onmessage({ data: microphoneFrame });
    assert.equal(socket.sent.at(-1), microphoneFrame);
    assert.equal(socket.readyState, 1);
});

test("mute set before microphone setup keeps the newly acquired track disabled", async () => {
    const harness = createVoiceHarness();
    await harness.voice.start("credential", null, { invokeMethodAsync: async () => {} });
    harness.voice.setMuted(true);

    const socket = harness.sockets[0];
    socket.onmessage({ data: JSON.stringify({ type: "SettingsApplied" }) });
    await flushMicrotasks();

    assert.equal(harness.tracks[0].enabled, false);
    const microphone = harness.audioNodes.find(node => node.name === "concierge-mic");
    microphone.port.onmessage({ data: new ArrayBuffer(640) });
    assert.deepEqual(socket.sent, []);
    assert.equal(socket.readyState, 1);
});

test("idle warning follows server events, not mic frames, mute, or resume", async () => {
    const harness = createVoiceHarness();
    const calls = [];
    await harness.voice.start("credential", null, {
        invokeMethodAsync: async (...args) => { calls.push(args); },
    });
    const socket = harness.sockets[0];
    const emit = message => socket.onmessage({ data: JSON.stringify(message) });
    emit({ type: "SettingsApplied" });
    await flushMicrotasks();
    emit({ type: "IdleWarning", active: true });
    const microphone = harness.audioNodes.find(node => node.name === "concierge-mic");
    microphone.port.onmessage({ data: new ArrayBuffer(640) });
    harness.voice.setMuted(true);
    harness.voice.setMuted(false);
    await harness.voice.resume();
    assert.deepEqual(calls.filter(call => call[0] === "OnVoiceIdleWarning"), [
        ["OnVoiceIdleWarning", true],
    ]);
    emit({ type: "IdleWarning", active: false });
    emit({ type: "IdleWarning", active: true });
    emit({ type: "UserStartedSpeaking", interruptionNumber: 1 });
    emit({ type: "IdleWarning", active: true });
    emit({ type: "AgentThinking" });
    assert.deepEqual(calls.filter(call => call[0] === "OnVoiceIdleWarning").map(call => call[1]),
        [true, false, true, false, true, false]);
});

test("idle ending carries its reason and a socket close cannot replace it with generic disconnect", async () => {
    const harness = createVoiceHarness();
    const calls = [];
    await harness.voice.start("credential", null, {
        invokeMethodAsync: async (...args) => { calls.push(args); },
    });
    const socket = harness.sockets[0];
    socket.onmessage({ data: '{"type":"EndChatRequested","reason":"idle_timeout"}' });
    socket.onclose({ code: 4401 });
    assert.deepEqual(calls.filter(call => call[0] === "OnVoiceEndRequested"),
        [["OnVoiceEndRequested", "idle_timeout"]]);
    assert.equal(calls.some(call => call[0] === "OnVoiceState" && call[1] === "disconnected"), false);
});

test("stopped or replaced sockets cannot restore an idle warning or end the new chat", async () => {
    const harness = createVoiceHarness();
    const calls = [];
    const dotNet = { invokeMethodAsync: async (...args) => { calls.push(args); } };
    await harness.voice.start("credential", null, dotNet);
    const stale = harness.sockets[0];
    stale.onmessage({ data: '{"type":"IdleWarning","active":true}' });
    await harness.voice.start("credential", "session", dotNet);
    stale.onmessage({ data: '{"type":"IdleWarning","active":true}' });
    stale.onmessage({ data: '{"type":"EndChatRequested","reason":"idle_timeout"}' });
    assert.deepEqual(calls.filter(call => call[0] === "OnVoiceIdleWarning").map(call => call[1]), [true, false]);
    assert.equal(calls.some(call => call[0] === "OnVoiceEndRequested"), false);
    harness.sockets[1].onmessage({ data: '{"type":"IdleWarning","active":true}' });
    harness.voice.stop();
    assert.equal(calls.filter(call => call[0] === "OnVoiceIdleWarning").at(-1)[1], false);
});

test("farewell still waits for playback and is cancellable by speech", async () => {
    const harness = createVoiceHarness();
    const calls = [];
    await harness.voice.start("credential", null, {
        invokeMethodAsync: async (...args) => { calls.push(args); },
    });
    const socket = harness.sockets[0];
    const playback = harness.audioNodes.find(node => node.name === "concierge-play");
    const emit = message => socket.onmessage({ data: JSON.stringify(message) });
    socket.onmessage({ data: new ArrayBuffer(2) });
    emit({ type: "EndChatAfterPlayback", reason: "farewell_playback_ready" });
    assert.equal(calls.some(call => call[0] === "OnVoiceEndRequested"), false);
    emit({ type: "UserStartedSpeaking", interruptionNumber: 1 });
    playback.port.onmessage({ data: { type: "drained" } });
    assert.equal(calls.some(call => call[0] === "OnVoiceEndRequested"), false);
    socket.onmessage({ data: new ArrayBuffer(2) });
    emit({ type: "EndChatAfterPlayback", reason: "farewell_playback_ready" });
    playback.port.onmessage({ data: { type: "drained" } });
    assert.deepEqual(calls.filter(call => call[0] === "OnVoiceEndRequested"),
        [["OnVoiceEndRequested", "farewell_playback_ready"]]);
});

test("unexpected disconnect clears the warning and still reports the connection failure", async () => {
    const harness = createVoiceHarness();
    const calls = [];
    await harness.voice.start("credential", null, {
        invokeMethodAsync: async (...args) => { calls.push(args); },
    });
    const socket = harness.sockets[0];
    socket.onmessage({ data: '{"type":"IdleWarning","active":true}' });
    socket.onclose({ code: 4000 });
    assert.deepEqual(calls.filter(call => call[0] === "OnVoiceIdleWarning").map(call => call[1]), [true, false]);
    assert.deepEqual(calls.at(-1), ["OnVoiceState", "disconnected", "Voice disconnected.", 4000]);
    assert.equal(calls.some(call => call[0] === "OnVoiceEndRequested"), false);
});

function meterElement() {
    const values = {};
    return { values, style: { setProperty(name, value) { values[name] = value; } } };
}

test("meters follow the microphone and the agent's voice, and reset when voice ends", async () => {
    const harness = createVoiceHarness({ meters: true });
    const mic = meterElement();
    const speaker = meterElement();
    harness.voice.attachMeters(mic, speaker);
    await harness.voice.start("credential", null, { invokeMethodAsync: async () => {} });
    harness.sockets[0].onmessage({ data: JSON.stringify({ type: "SettingsApplied" }) });
    await flushMicrotasks();

    // Playback's analyser is created first, then the microphone's.
    const [speakerAnalyser, micAnalyser] = harness.analysers;
    micAnalyser.value = 0.1;
    harness.runFrames();
    harness.runFrames();
    assert.ok(Number(mic.values["--level"]) > 0.3, mic.values["--level"]);
    assert.equal(speaker.values["--level"], "0.000");

    speakerAnalyser.value = 0.2;
    micAnalyser.value = 0;
    for (let i = 0; i < 8; i++) harness.runFrames();
    assert.ok(Number(speaker.values["--level"]) > 0.6, speaker.values["--level"]);
    assert.ok(Number(mic.values["--level"]) < 0.05, mic.values["--level"]);

    // A stale component's detach must not clear a newer component's meters.
    harness.voice.detachMeters(meterElement());
    harness.runFrames();
    assert.ok(Number(speaker.values["--level"]) > 0.6);

    harness.voice.stop();
    assert.equal(mic.values["--level"], "0.000");
    assert.equal(speaker.values["--level"], "0.000");
    harness.runFrames();
    assert.equal(speaker.values["--level"], "0.000");
});

test("muting spoken replies silences output only and carries over to the next connection", async () => {
    const harness = createVoiceHarness({ meters: true });
    await harness.voice.start("credential", null, { invokeMethodAsync: async () => {} });
    const socket = harness.sockets[0];
    const playback = harness.audioNodes.find(node => node.name === "concierge-play");

    assert.equal(harness.voice.setSpeakerMuted(true), true);
    assert.equal(harness.gains[0].gain.value, 0);
    // Audio still reaches the worklet, and its acknowledgements still pace the relay.
    socket.onmessage({ data: new ArrayBuffer(2) });
    assert.equal(playback.port.messages[0].type, "audio");
    playback.port.onmessage({ data: { type: "played", samples: 240 } });
    assert.deepEqual(socket.sent, ['{"type":"PlaybackAck","samples":240}']);

    await harness.voice.start("credential", "voice-1", { invokeMethodAsync: async () => {} });
    assert.equal(harness.gains[1].gain.value, 0);
    assert.equal(harness.voice.setSpeakerMuted(false), false);
    assert.equal(harness.gains[1].gain.value, 1);
});
