// Browser helpers for the guest chat page (Agentic.razor, ADR 0015).
(function () {
    // Not "hotel-zgy-concierge": that was the removed Home page's key, and browsers that used Home may
    // still hold an entry there in Home's format.
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
