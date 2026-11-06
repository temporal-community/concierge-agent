// PROTOTYPE (ADR 0015): browser helpers for the AG-UI page (/agentic).
(function () {
    // Its own key: Home's "hotel-zgy-concierge" entry carries Home's pending turn and draft, which this
    // page does not have and must not overwrite.
    const sessionKey = "hotel-zgy-agentic";
    const nearBottomPx = 72;
    const followers = new WeakMap();

    function isNearBottom(element) {
        return element.scrollHeight - element.scrollTop - element.clientHeight <= nearBottomPx;
    }

    window.conciergeAgentic = {
        loadSession() {
            try {
                const raw = sessionStorage.getItem(sessionKey);
                return raw ? JSON.parse(raw) : null;
            } catch {
                return null;
            }
        },
        saveSession(state) {
            sessionStorage.setItem(sessionKey, JSON.stringify(state));
        },
        clearSession() {
            sessionStorage.removeItem(sessionKey);
        },

        // Keeps the transcript pinned to the newest message while the guest is reading the bottom, and
        // otherwise leaves their scroll position alone and reveals the "New message" button instead.
        followTranscript(element) {
            if (!element || followers.has(element)) return;
            const button = element.querySelector(".new-message-button");
            let pinned = true;
            const reveal = (show) => { if (button) button.hidden = !show; };
            const toBottom = () => { element.scrollTop = element.scrollHeight; reveal(false); };

            const onScroll = () => {
                pinned = isNearBottom(element);
                if (pinned) reveal(false);
            };
            const observer = new MutationObserver(() => {
                if (pinned) toBottom(); else reveal(true);
            });
            observer.observe(element, { childList: true, subtree: true, characterData: true });
            element.addEventListener("scroll", onScroll, { passive: true });
            if (button) button.addEventListener("click", () => { pinned = true; toBottom(); });

            followers.set(element, () => {
                observer.disconnect();
                element.removeEventListener("scroll", onScroll);
            });
            toBottom();
        },
        unfollowTranscript(element) {
            const stop = element && followers.get(element);
            if (stop) {
                stop();
                followers.delete(element);
            }
        },
    };
})();
