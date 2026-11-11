// Run after building Concierge.Web:
// PLAYWRIGHT_MODULE=/absolute/path/to/playwright/index.mjs node --test tests/chat-layout.test.mjs
// Without PLAYWRIGHT_MODULE, uses an already installed "playwright" package.
// This is a browser layout fixture of the guest chat page (tests/agentic-page.mjs), not an
// authenticated Blazor session: each state is rendered as the page would render it.
import assert from "node:assert/strict";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import { chatMain, header, pageHtml, routeAssets, show, turn } from "./agentic-page.mjs";

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE
    ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href
    : "playwright");

const turns = Array.from({ length: 50 }, (_, i) => turn({
    date: i === 0 ? "Oct 25" : null,
    guest: `Question ${i + 1}: ${"Tell me about the hotel amenities and nearby places. ".repeat(3)}`,
    reply: `<p>Answer ${i + 1}: ${"A long stay conversation about hotel amenities and nearby places. ".repeat(5)}</p>`,
    ride: i === 10,
}));

// Paused voice is the reported regression; the idle-warning bar takes height from the transcript, and
// live voice adds the speaker button and the status row above the input.
const VARIANTS = [
    { name: "paused voice", voice: "paused" },
    { name: "paused voice, chat ending soon", voice: "paused", idleWarning: true },
    { name: "live voice", voice: "listening" },
];

async function geometry(page) {
    return page.evaluate(() => {
        const rect = selector => {
            const { top, bottom, left, right, width, height } = document.querySelector(selector).getBoundingClientRect();
            return { top, bottom, left, right, width, height };
        };
        const transcript = document.querySelector(".agentic-transcript");
        const reachable = selector => {
            const element = document.querySelector(selector);
            const r = element.getBoundingClientRect();
            return element.contains(document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2));
        };
        return {
            viewport: { width: innerWidth, height: innerHeight },
            composer: rect(".sc-ai-chat-page__footer"), textarea: rect(".sc-ai-input__textarea"),
            send: rect(".sc-ai-input__send"), mic: rect(".voice-input__mic"),
            status: rect(".voice-status-row"),
            chat: rect(".chat-panel"), drawer: rect(".ride-activity-drawer"),
            transcript: { ...rect(".agentic-transcript"), clientHeight: transcript.clientHeight,
                scrollHeight: transcript.scrollHeight, scrollTop: transcript.scrollTop },
            documentWidth: document.documentElement.scrollWidth,
            documentHeight: document.documentElement.scrollHeight,
            textareaReachable: reachable(".sc-ai-input__textarea"), sendReachable: reachable(".sc-ai-input__send"),
            micReachable: reachable(".voice-input__mic"),
        };
    });
}

function assertContained(g, overlay) {
    const evidence = JSON.stringify(g);
    for (const name of ["composer", "textarea", "send", "mic", "status", "chat"]) {
        const r = g[name];
        assert.ok(r.height > 0 && r.width > 0, `${name} has area: ${evidence}`);
        assert.ok(r.top >= 0 && r.bottom <= g.viewport.height + 1, `${name} vertically contained: ${evidence}`);
        assert.ok(r.left >= 0 && r.right <= g.viewport.width + 1, `${name} horizontally contained: ${evidence}`);
    }
    assert.ok(g.documentWidth <= g.viewport.width, `No horizontal document overflow: ${evidence}`);
    assert.ok(g.documentHeight <= g.viewport.height, `No offscreen document content: ${evidence}`);
    assert.ok(g.transcript.clientHeight >= 90, `Transcript has a usable viewport: ${evidence}`);
    assert.ok(g.transcript.scrollHeight > g.transcript.clientHeight * 2, `Transcript overflows internally: ${evidence}`);
    assert.ok(g.status.bottom <= g.composer.bottom && g.status.top >= g.transcript.top,
        `Voice status floats inside the chat, above the input: ${evidence}`);
    assert.equal(g.textareaReachable, !overlay, `Textarea hit target: ${evidence}`);
    assert.equal(g.sendReachable, !overlay, `Send hit target: ${evidence}`);
    assert.equal(g.micReachable, !overlay, `Mic hit target: ${evidence}`);
}

test("a long conversation keeps the message input in the viewport", async t => {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext({ reducedMotion: "reduce" });
        await routeAssets(context);
        for (const viewport of [
            { width: 1440, height: 900 }, { width: 1024, height: 768 },
            { width: 701, height: 768 }, { width: 390, height: 844 },
            { width: 375, height: 667 },
        ]) {
            await t.test(`${viewport.width}x${viewport.height}`, async t => {
                const page = await context.newPage();
                await page.setViewportSize(viewport);
                try {
                    for (const variant of VARIANTS) {
                        for (const open of [false, true]) {
                            await show(page, pageHtml({ body: header({ signedIn: true, activityOpen: open })
                                + chatMain({ turns, activityOpen: open, ...variant }) }));
                            await page.evaluate(() => new Promise(resolve =>
                                requestAnimationFrame(() => requestAnimationFrame(resolve))));

                            // Start at the oldest message, as in the reported regression.
                            await page.locator(".agentic-transcript").evaluate(el => el.scrollTo({ top: 0, behavior: "instant" }));
                            const g = await geometry(page);
                            const label = `${variant.name}, activity ${open ? "open" : "closed"}`;
                            await t.test(label, () => {
                                assertContained(g, open && viewport.width <= 700);
                                assert.equal(g.transcript.scrollTop, 0);
                                if (open) {
                                    assert.ok(g.drawer.bottom <= viewport.height + 1, "Drawer fits the viewport");
                                    if (viewport.width > 700)
                                        assert.ok(g.chat.right <= g.drawer.left, "Desktop drawer pushes, not overlays, chat");
                                    else assert.equal(g.drawer.width, viewport.width, "Mobile drawer covers chat");
                                }
                            });
                            if (!open) {
                                // No forced click/scrollIntoView: Playwright must find an accessible control.
                                await page.locator(".sc-ai-input__textarea").click();
                                await page.locator(".sc-ai-input__textarea").fill("Draft kept in the viewport");
                                await page.locator(".sc-ai-input__textarea").press("Tab");
                                assert.equal(await page.locator(".sc-ai-input__send").evaluate(el => el === document.activeElement), true,
                                    `${label}: Tab from the message moves to Send`);
                            }
                            // Prove scrollability, not just overflow CSS or scrollHeight.
                            await page.locator(".agentic-transcript").evaluate(el => el.scrollTo({ top: el.scrollHeight, behavior: "instant" }));
                            const scrolled = await geometry(page);
                            assert.ok(scrolled.transcript.scrollTop > 0, `${label}: transcript actually scrolls to latest`);
                            assert.equal(scrolled.composer.bottom, g.composer.bottom, `${label}: input stays put while the transcript scrolls`);
                            t.diagnostic(JSON.stringify({
                                variant: variant.name, open, composerBottom: g.composer.bottom,
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
