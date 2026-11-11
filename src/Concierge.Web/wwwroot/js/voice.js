// Browser side of the voice relay: microphone capture, paced agent playback with acknowledgements,
// and barge-in flushes. Ported from the relay-latency prototype. The page never starts the
// microphone on its own; every start is a guest click.
(() => {
    const protocol = "concierge-voice";
    const maxMicBacklogBytes = 64 * 1024;
    const closeMessages = {
        4000: "Voice disconnected.",
        4401: "Your chat ended. Verify your stay again to continue.",
        4503: "Voice is unavailable right now. Use text chat or try again later.",
    };

    let current = null;
    // Level meters (the AG-UI page): elements whose --level (0..1) follows the microphone and the
    // agent's voice. Set from an animation frame, never over the Blazor circuit.
    const meters = { mic: null, speaker: null };
    // Spoken replies muted by the guest; kept across reconnects.
    let speakerMuted = false;

    /** RMS of an analyser's current window, 0..1. */
    function levelOf(analyser, buffer) {
        if (!analyser) return 0;
        analyser.getFloatTimeDomainData(buffer);
        let sum = 0;
        for (let i = 0; i < buffer.length; i++) sum += buffer[i] * buffer[i];
        return Math.sqrt(sum / buffer.length);
    }

    function setLevel(element, level) {
        element?.style?.setProperty("--level", level.toFixed(3));
    }

    // Speech RMS sits around 0.02-0.2, so scale up; ease toward the new level to avoid flicker.
    function startMeters(voice) {
        if (typeof requestAnimationFrame !== "function") return;
        const buffer = new Float32Array(1024);
        let mic = 0;
        let speaker = 0;
        const frame = () => {
            if (voice.stopped || current !== voice) return;
            mic += (Math.min(1, levelOf(voice.micAnalyser, buffer) * 6) - mic) * 0.5;
            speaker += (Math.min(1, levelOf(voice.speakerAnalyser, buffer) * 4) - speaker) * 0.5;
            setLevel(meters.mic, mic);
            setLevel(meters.speaker, speaker);
            voice.meterFrame = requestAnimationFrame(frame);
        };
        voice.meterFrame = requestAnimationFrame(frame);
    }

    function stopMeters(voice) {
        if (voice.meterFrame && typeof cancelAnimationFrame === "function") cancelAnimationFrame(voice.meterFrame);
        voice.meterFrame = 0;
        setLevel(meters.mic, 0);
        setLevel(meters.speaker, 0);
    }

    function notify(voice, state, message = null, code = 0) {
        if (voice.state === state && state !== "disconnected") return;
        voice.state = state;
        voice.dotNet.invokeMethodAsync("OnVoiceState", state, message, code).catch(() => {});
    }

    function setIdleWarning(voice, active) {
        if (Boolean(voice.idleWarning) === active) return;
        voice.idleWarning = active;
        voice.dotNet.invokeMethodAsync("OnVoiceIdleWarning", active).catch(() => {});
    }

    async function createPlayback(voice) {
        const context = new AudioContext({ sampleRate: 24000, latencyHint: "interactive" });
        voice.playContext = context;
        await context.audioWorklet.addModule("/js/voice-play-worklet.js");
        const node = new AudioWorkletNode(context, "concierge-play", { outputChannelCount: [1] });
        // Muting replies happens after the worklet, so playback acks, pacing and barge-in are unchanged.
        if (typeof context.createGain === "function") {
            voice.speakerGain = context.createGain();
            voice.speakerGain.gain.value = speakerMuted ? 0 : 1;
            node.connect(voice.speakerGain);
            voice.speakerGain.connect(context.destination);
        } else {
            node.connect(context.destination);
        }
        if (typeof context.createAnalyser === "function") {
            voice.speakerAnalyser = context.createAnalyser();
            voice.speakerAnalyser.fftSize = 1024;
            node.connect(voice.speakerAnalyser);
        }
        node.port.onmessage = (event) => onPlayback(voice, event.data);
        voice.playNode = node;
        await context.resume();
    }

    async function startMicrophone(voice) {
        const context = new AudioContext({ latencyHint: "interactive" });
        voice.micContext = context;
        await context.audioWorklet.addModule("/js/voice-mic-worklet.js");
        voice.micStream = await navigator.mediaDevices.getUserMedia({
            audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
        });
        if (voice.stopped) {
            voice.micStream.getTracks().forEach((track) => track.stop());
            return;
        }

        const node = new AudioWorkletNode(context, "concierge-mic");
        node.port.onmessage = (event) => {
            if (voice.muted) return;
            const socket = voice.socket;
            if (!socket || socket.readyState !== WebSocket.OPEN) return;
            // Never drop speech silently: a stalled uplink ends voice so the guest can reconnect.
            if (socket.bufferedAmount > maxMicBacklogBytes) {
                end(voice, "disconnected", closeMessages[4000], 4000);
                return;
            }
            socket.send(event.data);
        };
        voice.micStream.getTracks().forEach((track) => { track.enabled = !voice.muted; });
        const source = context.createMediaStreamSource(voice.micStream);
        source.connect(node);
        if (typeof context.createAnalyser === "function") {
            voice.micAnalyser = context.createAnalyser();
            voice.micAnalyser.fftSize = 1024;
            source.connect(voice.micAnalyser);
        }
        await context.resume();
        notify(voice, "listening");
    }

    function onPlayback(voice, message) {
        if (voice.stopped) return;
        switch (message.type) {
            case "played":
                if (voice.socket?.readyState === WebSocket.OPEN) {
                    voice.socket.send(JSON.stringify({ type: "PlaybackAck", samples: message.samples }));
                }
                break;
            case "speaking":
                setIdleWarning(voice, false);
                notify(voice, "speaking");
                break;
            case "silenced":
                if (voice.socket?.readyState === WebSocket.OPEN) {
                    voice.socket.send(JSON.stringify({
                        type: "InterruptionSilenced",
                        interruptionNumber: message.interruptionNumber,
                        bargeIn: message.bargeIn,
                        playbackToken: message.playbackToken,
                    }));
                }
                break;
            case "drained":
                voice.playbackActive = false;
                if (voice.endAfterPlayback) requestEndChat(voice);
                if (voice.state === "speaking") notify(voice, "listening");
                break;
            case "overflow":
                end(voice, "disconnected", closeMessages[4000], 4000);
                break;
        }
    }

    function onSocketMessage(voice, event) {
        if (voice.stopped) return;
        if (typeof event.data !== "string") {
            voice.playbackActive = true;
            const playbackToken = voice.pendingPlaybackToken ?? null;
            voice.pendingPlaybackToken = null;
            const pcm = new Int16Array(event.data);
            const samples = new Float32Array(pcm.length);
            for (let i = 0; i < pcm.length; i++) samples[i] = pcm[i] / 0x8000;
            voice.playNode.port.postMessage({ type: "audio", samples, playbackToken }, [samples.buffer]);
            return;
        }

        const message = JSON.parse(event.data);
        if (message.type === "PlaybackSegment") {
            voice.pendingPlaybackToken = typeof message.playbackToken === "string" && message.playbackToken.length > 0
                ? message.playbackToken
                : null;
            return;
        }
        // A segment token applies only to the immediately following binary PCM message.
        voice.pendingPlaybackToken = null;
        switch (message.type) {
            case "IdleWarning":
                setIdleWarning(voice, message.active === true);
                break;
            case "VoiceSession":
                voice.dotNet.invokeMethodAsync("OnVoiceSession", message.voiceSessionId).catch(() => {});
                break;
            case "SettingsApplied":
                startMicrophone(voice).catch((error) => {
                    const denied = error && (error.name === "NotAllowedError" || error.name === "SecurityError");
                    end(voice, "disconnected", denied
                        ? "Microphone access is blocked. Allow it in the browser's site settings, then reconnect."
                        : "The microphone couldn't start. Check your input device, then reconnect.", 0);
                });
                break;
            case "UserStartedSpeaking":
                setIdleWarning(voice, false);
                voice.endAfterPlayback = false;
                voice.endRequested = false;
                voice.endReason = null;
                voice.playNode.port.postMessage({
                    type: "flush",
                    interruptionNumber: message.interruptionNumber,
                });
                notify(voice, "listening");
                break;
            case "ConversationText":
                setIdleWarning(voice, false);
                if (message.role === "user") notify(voice, "thinking");
                break;
            case "AgentStartedSpeaking":
                setIdleWarning(voice, false);
                break;
            case "AgentThinking":
                setIdleWarning(voice, false);
                notify(voice, "thinking");
                break;
            case "EndChatRequested":
                voice.endReason = message.reason;
                requestEndChat(voice);
                break;
            case "EndChatAfterPlayback":
                setIdleWarning(voice, false);
                voice.endReason = message.reason;
                voice.endAfterPlayback = true;
                if (!voice.playbackActive) requestEndChat(voice);
                break;
            case "CancelEndChat":
                voice.endAfterPlayback = false;
                voice.endRequested = false;
                voice.endReason = null;
                break;
        }
    }

    function requestEndChat(voice) {
        if (voice.endRequested || voice.stopped) return;
        setIdleWarning(voice, false);
        voice.endRequested = true;
        voice.dotNet.invokeMethodAsync("OnVoiceEndRequested", voice.endReason ?? null).catch(() => {});
    }

    function release(voice) {
        voice.micStream?.getTracks().forEach((track) => track.stop());
        voice.micContext?.close().catch(() => {});
        voice.playContext?.close().catch(() => {});
    }

    function end(voice, state, message, code) {
        if (voice.stopped) return;
        setIdleWarning(voice, false);
        voice.stopped = true;
        stopMeters(voice);
        if (current === voice) current = null;
        try {
            if (voice.socket && voice.socket.readyState <= WebSocket.OPEN) voice.socket.close(1000, "Voice stopped");
        } catch {
            // The socket is already gone.
        }
        release(voice);
        // End Chat owns the next UI state; a close racing its callback must not overwrite it.
        if (state && !voice.endRequested) notify(voice, state, message, code);
    }

    function onVisibilityChange() {
        const voice = current;
        if (!voice || voice.stopped || document.visibilityState !== "hidden") return;
        voice.micStream?.getTracks().forEach((track) => { track.enabled = false; });
        voice.micContext?.suspend().catch(() => {});
        voice.playContext?.suspend().catch(() => {});
        notify(voice, "paused");
    }

    document.addEventListener("visibilitychange", onVisibilityChange);
    window.addEventListener("pagehide", () => current && end(current, null));

    window.conciergeVoice = {
        /** Opens the relay for a verified chat. Must run from a guest click so audio may start. */
        async start(credential, voiceSessionId, dotNet) {
            if (current) end(current, null);
            const voice = { dotNet, state: null, stopped: false };
            current = voice;
            notify(voice, "connecting");
            startMeters(voice);
            try {
                await createPlayback(voice);
            } catch {
                end(voice, "disconnected", "Audio playback couldn't start in this browser.", 0);
                return;
            }
            if (voice.stopped) return;

            const scheme = location.protocol === "https:" ? "wss" : "ws";
            const query = voiceSessionId ? `?voiceSession=${encodeURIComponent(voiceSessionId)}` : "";
            const socket = new WebSocket(`${scheme}://${location.host}/api/concierge/voice${query}`, [protocol, credential]);
            socket.binaryType = "arraybuffer";
            voice.socket = socket;
            socket.onmessage = (event) => onSocketMessage(voice, event);
            socket.onclose = (event) => end(
                voice,
                "disconnected",
                closeMessages[event.code] ?? closeMessages[4000],
                event.code);
        },

        /** Resumes capture and playback after the page was hidden. */
        async resume() {
            const voice = current;
            if (!voice || voice.stopped) return;
            await voice.playContext?.resume();
            await voice.micContext?.resume();
            voice.micStream?.getTracks().forEach((track) => {
                track.enabled = !voice.muted && document.visibilityState !== "hidden";
            });
            notify(voice, "listening");
        },

        /** Stops microphone frames while the relay's existing KeepAlive maintains Deepgram's connection. */
        setMuted(muted) {
            const voice = current;
            if (!voice || voice.stopped) return false;
            voice.muted = Boolean(muted);
            voice.micStream?.getTracks().forEach((track) => {
                track.enabled = !voice.muted && document.visibilityState !== "hidden";
            });
            return voice.muted;
        },

        stop() {
            if (current) end(current, null);
        },

        /** Elements whose --level follows the microphone and the agent's voice; null detaches. */
        attachMeters(mic, speaker) {
            setLevel(meters.mic, 0);
            setLevel(meters.speaker, 0);
            meters.mic = mic ?? null;
            meters.speaker = speaker ?? null;
        },

        /** Detaches only if `mic` is still attached: a newer component may have replaced it. */
        detachMeters(mic) {
            if (meters.mic !== mic) return;
            this.attachMeters(null, null);
        },

        /** Mutes or unmutes spoken replies; the reply still plays silently, so pacing is unchanged. */
        setSpeakerMuted(muted) {
            speakerMuted = Boolean(muted);
            if (current?.speakerGain) current.speakerGain.gain.value = speakerMuted ? 0 : 1;
            return speakerMuted;
        },
    };
})();
