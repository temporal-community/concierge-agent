// Run after building Concierge.Web:
// PLAYWRIGHT_MODULE=/absolute/path/to/playwright/index.mjs node --test tests/chat-layout.test.mjs
// Without PLAYWRIGHT_MODULE, uses an already installed "playwright" package.
// This is a browser layout fixture, not an authenticated Blazor session. Keep the
// markup aligned with Home.razor (paused voice) and RideActivityPanel.razor.
import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import { test } from "node:test";
import { pathToFileURL } from "node:url";

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE
    ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href
    : "playwright");
const appCss = readFileSync(new URL("../src/Concierge.Web/wwwroot/css/app.css", import.meta.url), "utf8");
const scopedCss = readFileSync(new URL(
    "../src/Concierge.Web/obj/Debug/net11.0/scopedcss/bundle/Concierge.Web.styles.css",
    import.meta.url), "utf8");
const scope = scopedCss.match(/\.ride-activity-drawer\[(b-[\w]+)\]/)?.[1];
assert.ok(scope, "Build Concierge.Web first to generate the actual drawer scoped CSS");

const icon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M18 6 6 18"/></svg>';
const messages = Array.from({ length: 100 }, (_, i) => `
    <div class="message-row ${i % 2 ? "guest-row" : "concierge-row"}">
        <div class="message-content">
            <div class="message-meta"><strong>${i % 2 ? "GUEST" : "CONCIERGE"}</strong><time>1:30 PM</time></div>
            <div class="message-bubble ${i % 2 ? "guest-bubble" : "concierge-bubble"}">
                Message ${i + 1}: ${"A long stay conversation about hotel amenities and nearby places. ".repeat(5)}
            </div>
        </div>
    </div>`).join("");

function fixture() {
    // All drawer descendants receive the generated Blazor CSS isolation attribute.
    const drawer = `
        <aside id="ride-activity-panel" class="ride-activity-drawer" aria-labelledby="ride-activity-title"
               aria-hidden="true" inert tabindex="-1">
            <header class="ride-activity-header">
                <div><h2 id="ride-activity-title">Activity</h2>
                    <p class="ride-activity-subtitle">Updates for your stay · Miami time (ET)</p></div>
                <button class="ride-activity-close" type="button" aria-label="Close activity">${icon}</button>
            </header>
            <div class="ride-activity-content">
                <div class="ride-activity-empty" role="status">
                    <span class="ride-activity-empty-icon" aria-hidden="true">${icon}</span>
                    <h3>No activity yet</h3>
                    <p>Ride updates will appear here when you book a ride with the concierge.</p>
                </div>
            </div>
            <footer class="ride-activity-footer"><p>To change or cancel a ride, ask the concierge.</p></footer>
        </aside>`.replace(/<([a-z][\w-]*)(?=[\s>])/g, `<$1 ${scope}`);
    return `<!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>${appCss}\n${scopedCss}</style></head><body>
        <div class="page-shell">
            <header class="site-header voice-site-header">
                <a class="hotel-brand" href="/" aria-label="Hotel ZGY Miami"><strong>Hotel ZGY</strong><span>/ Miami</span></a>
                <div class="header-actions voice-header-actions">
                    <div class="voice-status voice-paused" role="status"><span class="voice-indicator"></span><strong>Paused</strong></div>
                    <div class="voice-header-controls has-voice-mode" role="group" aria-label="Chat controls">
                        <button class="voice-control-button activity-button" type="button" aria-expanded="false"
                                aria-controls="ride-activity-panel">${icon}<span>Activity</span></button>
                        <button class="voice-control-button" type="button" aria-label="Resume voice">${icon}<span>Resume</span></button>
                        <button class="voice-end-voice-button" type="button" aria-label="End voice">${icon}<span>End voice</span></button>
                        <button class="end-chat-button voice-end-chat-button" type="button" aria-label="End chat">${icon}<span>End chat</span></button>
                        <label class="theme-picker compact-theme-picker"><span class="visually-hidden">Theme</span>
                            <svg class="theme-icon" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="4"/></svg>
                            <select aria-label="Color theme"><option>System</option><option>Light</option><option>Dark</option></select>
                        </label>
                    </div>
                </div>
            </header>
            <main class="main-content">
                <div class="activity-layout">
                    <section class="chat-panel" aria-label="Hotel concierge chat">
                        <button class="questions-heading" type="button" aria-expanded="false" aria-controls="suggested-question-content">
                            <span class="wave-icon questions-wave-icon" aria-hidden="true">${icon}</span>
                            <span>How can we help?</span><svg class="accordion-chevron" viewBox="0 0 24 24"><path d="m6 9 6 6 6-6"/></svg>
                        </button>
                        <section id="suggested-question-content" class="suggestions" aria-label="Suggested questions" hidden>
                            <label for="suggestion-select">Suggested question</label>
                            <select id="suggestion-select"><option>Choose a suggested question…</option></select>
                            <div class="suggestion-footer"><p>Choose a question, then send it.</p>
                                <button class="suggestion-send" type="button">Send suggestion</button></div>
                        </section>
                        <div class="conversation-heading"><span>YOUR STAY CONVERSATION</span></div>
                        <div class="transcript-wrap">
                            <div class="transcript" aria-label="Stay conversation">
                                <div class="date-separator"><span>Oct 25</span></div>${messages}
                            </div>
                        </div>
                        <form class="composer">
                            <label class="message-label" for="message-draft">MESSAGE</label>
                            <div class="compose-box">
                                <textarea id="message-draft" placeholder="Type a message…" rows="3" maxlength="2000"></textarea>
                                <div class="compose-footer"><button class="primary-button send-button" type="submit" aria-label="Send message">
                                    <span>Send</span>${icon}</button></div>
                            </div>
                        </form>
                    </section>${drawer}
                </div>
            </main>
        </div>
        <script>
            const activity = document.querySelector(".activity-button");
            const drawer = document.querySelector(".ride-activity-drawer");
            function setActivity(open) {
                document.querySelector(".main-content").classList.toggle("activity-main-content", open);
                document.querySelector(".activity-layout").classList.toggle("activity-layout-open", open);
                drawer.classList.toggle("is-open", open);
                drawer.inert = !open;
                drawer.setAttribute("aria-hidden", !open);
                activity.setAttribute("aria-expanded", open);
            }
            activity.onclick = () => setActivity(activity.getAttribute("aria-expanded") !== "true");
            document.querySelector(".ride-activity-close").onclick = () => setActivity(false);
            document.querySelector(".questions-heading").onclick = (event) => {
                const button = event.currentTarget;
                const expanded = button.getAttribute("aria-expanded") !== "true";
                button.setAttribute("aria-expanded", expanded);
                document.querySelector(".suggestions").hidden = !expanded;
                document.querySelector(".accordion-chevron").classList.toggle("is-expanded", expanded);
            };
            document.querySelector("form").onsubmit = event => event.preventDefault();
        </script></body></html>`;
}

async function geometry(page) {
    return page.evaluate(() => {
        const rect = selector => {
            const { top, bottom, left, right, width, height } = document.querySelector(selector).getBoundingClientRect();
            return { top, bottom, left, right, width, height };
        };
        const transcript = document.querySelector(".transcript");
        const reachable = selector => {
            const element = document.querySelector(selector);
            const r = element.getBoundingClientRect();
            return element.contains(document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2));
        };
        return {
            viewport: { width: innerWidth, height: innerHeight },
            composer: rect(".composer"), textarea: rect("textarea"), send: rect(".send-button"),
            chat: rect(".chat-panel"), drawer: rect(".ride-activity-drawer"),
            transcript: { ...rect(".transcript"), clientHeight: transcript.clientHeight,
                scrollHeight: transcript.scrollHeight, scrollTop: transcript.scrollTop },
            documentWidth: document.documentElement.scrollWidth,
            documentHeight: document.documentElement.scrollHeight,
            textareaReachable: reachable("textarea"), sendReachable: reachable(".send-button"),
        };
    });
}

function assertContained(g, overlay) {
    const evidence = JSON.stringify(g);
    for (const name of ["composer", "textarea", "send", "chat"]) {
        const r = g[name];
        assert.ok(r.height > 0 && r.width > 0, `${name} has area: ${evidence}`);
        assert.ok(r.top >= 0 && r.bottom <= g.viewport.height + 1, `${name} vertically contained: ${evidence}`);
        assert.ok(r.left >= 0 && r.right <= g.viewport.width + 1, `${name} horizontally contained: ${evidence}`);
    }
    assert.ok(g.documentWidth <= g.viewport.width, `No horizontal document overflow: ${evidence}`);
    assert.ok(g.documentHeight <= g.viewport.height, `No offscreen document content: ${evidence}`);
    assert.ok(g.transcript.clientHeight >= 90, `Transcript has a usable viewport: ${evidence}`);
    assert.ok(g.transcript.scrollHeight > g.transcript.clientHeight * 2, `Transcript overflows internally: ${evidence}`);
    assert.equal(g.textareaReachable, !overlay, `Textarea hit target: ${evidence}`);
    assert.equal(g.sendReachable, !overlay, `Send hit target: ${evidence}`);
}

test("long paused-voice conversation keeps the composer in the viewport", async t => {
    const browser = await chromium.launch({ headless: true });
    try {
        for (const viewport of [
            { width: 1440, height: 900 }, { width: 1024, height: 768 },
            { width: 701, height: 768 }, { width: 390, height: 844 },
            { width: 375, height: 667 },
        ]) {
            await t.test(`${viewport.width}x${viewport.height}`, async t => {
                const page = await browser.newPage({ viewport, reducedMotion: "reduce" });
                try {
                    await page.setContent(fixture());
                    for (const expanded of [false, true]) {
                        if (expanded) await page.locator(".questions-heading").click();
                        for (const open of [false, true, false]) {
                            if (open) await page.locator(".activity-button").click();
                            else if (await page.locator(".ride-activity-drawer").getAttribute("aria-hidden") === "false")
                                await page.locator(".ride-activity-close").click();
                            await page.waitForFunction(expected =>
                                document.querySelector(".ride-activity-drawer").getAttribute("aria-hidden") === String(!expected), open);
                            await page.evaluate(() => new Promise(resolve =>
                                requestAnimationFrame(() => requestAnimationFrame(resolve))));

                            // Start at the oldest message, as in the reported regression.
                            await page.locator(".transcript").evaluate(el => el.scrollTo({ top: 0, behavior: "instant" }));
                            const g = await geometry(page);
                            assertContained(g, open && viewport.width <= 700);
                            assert.equal(g.transcript.scrollTop, 0);
                            if (open) {
                                assert.ok(g.drawer.bottom <= viewport.height + 1);
                                if (viewport.width > 700)
                                    assert.ok(g.chat.right <= g.drawer.left, "Desktop drawer pushes, not overlays, chat");
                                else assert.equal(g.drawer.width, viewport.width, "Mobile drawer covers chat");
                            } else {
                                // No forced click/scrollIntoView: Playwright must find an accessible control.
                                await page.locator("textarea").click();
                                await page.locator("textarea").fill("Draft kept in the viewport");
                                await page.locator("textarea").press("Tab");
                                assert.equal(await page.locator(".send-button").evaluate(el => el === document.activeElement), true);
                            }
                            // Prove scrollability, not just overflow CSS or scrollHeight.
                            await page.locator(".transcript").evaluate(el => el.scrollTo({ top: el.scrollHeight, behavior: "instant" }));
                            const scrolled = await geometry(page);
                            assert.ok(scrolled.transcript.scrollTop > 0, "Transcript actually scrolls to latest");
                            assert.equal(scrolled.composer.bottom, g.composer.bottom, "Composer stays put while transcript scrolls");
                            t.diagnostic(JSON.stringify({
                                expanded, open, composerBottom: g.composer.bottom,
                                textareaBottom: g.textarea.bottom, sendBottom: g.send.bottom,
                                transcriptClientHeight: g.transcript.clientHeight,
                                transcriptScrollHeight: g.transcript.scrollHeight,
                                scrolledTo: scrolled.transcript.scrollTop,
                            }));
                        }
                    }
                } finally {
                    await page.close();
                }
            });
        }
    } finally {
        await browser.close();
    }
});
