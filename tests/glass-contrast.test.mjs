// Run after building Concierge.Web:
// PLAYWRIGHT_MODULE=/absolute/path/to/playwright/index.mjs node --test tests/glass-contrast.test.mjs
// Without PLAYWRIGHT_MODULE, uses an already installed "playwright" package.
//
// Text sits directly on the tinted glass over the hotel photo, so its contrast depends on the
// rendered pixels, not just the tokens. For every brand and mode this renders the page's stylesheets and
// photo, hides everything inside each glass container, screenshots the bare glass, and checks every
// text color used on that glass against the darkest and lightest pixel anywhere in the container.
// Checking the whole container covers text at any scroll position. The markup is the guest chat
// page's (tests/agentic-page.mjs).
import assert from "node:assert/strict";
import { test } from "node:test";
import { pathToFileURL } from "node:url";
import { chatMain, header, pageHtml, routeAssets, show, turn, verifyMain } from "./agentic-page.mjs";

const { chromium } = await import(process.env.PLAYWRIGHT_MODULE
    ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href
    : "playwright");

const THEMES = [["temporal", "light"], ["temporal", "dark"], ["deepgram", "light"], ["deepgram", "dark"]];
// The largest screens show the most photo behind the panels and set the limits; the phone covers
// the focus card, the only glass panel on the photo at that width besides the header.
const DESKTOP = [[1440, 900], [2560, 1440], [3840, 2160]];
const PHONE = [[390, 844]];
const CONTAINERS = [".site-header", ".focus-card", ".chat-panel", ".ride-activity-drawer"];
const TOKENS = ["text-primary", "text-secondary", "text-muted"];

const turns = [
    turn({ date: "Nov 8", guest: "Book a ride to Wayan Miami tomorrow at 10am",
        reply: "<p>Your ride to Wayan Miami is booked for tomorrow at 10:00 a.m.</p>", ride: true }),
    turn({ date: "Nov 9", guest: "What are the pool hours?",
        reply: "<p>The Hotel ZGY pool is open from 8:00 a.m. to 8:00 p.m.</p>" }),
];

// The verify card; the chat with history, paused voice and the activity drawer open; and a new stay,
// whose suggestion heading sits on the glass.
const STATES = [
    ["verify", header({ signedIn: false }) + verifyMain, [...DESKTOP, ...PHONE]],
    ["chat", header({ signedIn: true, activityOpen: true })
        + chatMain({ turns, activityOpen: true, voice: "paused" }), DESKTOP],
    ["new stay", header({ signedIn: true }) + chatMain({}), DESKTOP],
];

// Text with no painted layer between it and its glass container, plus every text token as it
// resolves on that container (timestamps, hints and labels can appear anywhere on the glass).
function textOnGlass({ containers, tokens }) {
    const out = {};
    for (const sel of containers) {
        const box = document.querySelector(sel);
        if (!box) continue;
        const rect = box.getBoundingClientRect();
        const style = getComputedStyle(box);
        if (rect.width < 40 || rect.height < 30 || style.visibility === "hidden" || +style.opacity === 0) continue;
        const glassy = style.backdropFilter.includes("blur");
        const texts = [];
        const walker = document.createTreeWalker(box, NodeFilter.SHOW_TEXT);
        for (let node; (node = walker.nextNode());) {
            if (!node.textContent.trim()) continue;
            const el = node.parentElement;
            if (el.closest("option, select, textarea, input, button, .message-bubble")) continue;
            let painted = false;
            for (let a = el; a && a !== box; a = a.parentElement) {
                const s = getComputedStyle(a);
                if (s.backgroundImage !== "none" || !/rgba\(0, 0, 0, 0\)|transparent/.test(s.backgroundColor)) { painted = true; break; }
            }
            if (painted) continue;
            const cs = getComputedStyle(el);
            const size = parseFloat(cs.fontSize);
            texts.push({ text: node.textContent.trim().slice(0, 30), color: cs.color,
                large: size >= 24 || (size >= 18.66 && +cs.fontWeight >= 700) });
        }
        const probe = document.createElement("span");
        box.append(probe);
        for (const token of [...tokens, ...(sel === ".chat-panel" ? ["glass-label"] : [])]) {
            probe.style.color = `var(--${token})`;
            texts.push({ text: `--${token}`, color: getComputedStyle(probe).color, large: false });
        }
        probe.remove();
        out[sel] = { glassy, rect: { x: rect.x, y: rect.y, w: rect.width, h: rect.height }, texts };
    }
    return out;
}

// Darkest and lightest relative luminance in each container, 16px in from the edge highlight.
async function luminanceRange(canvasPage, png, rects) {
    return canvasPage.evaluate(async ({ b64, rects }) => {
        const img = new Image();
        img.src = "data:image/png;base64," + b64;
        await img.decode();
        const canvas = document.createElement("canvas");
        canvas.width = img.width;
        canvas.height = img.height;
        const ctx = canvas.getContext("2d", { willReadFrequently: true });
        ctx.drawImage(img, 0, 0);
        const lin = v => { v /= 255; return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; };
        const out = {};
        for (const [sel, r] of Object.entries(rects)) {
            const inset = 16;
            const data = ctx.getImageData(Math.round(r.x + inset), Math.round(r.y + inset),
                Math.round(r.w - 2 * inset), Math.round(r.h - 2 * inset)).data;
            let min = 1, max = 0;
            for (let i = 0; i < data.length; i += 4) {
                const l = 0.2126 * lin(data[i]) + 0.7152 * lin(data[i + 1]) + 0.0722 * lin(data[i + 2]);
                if (l < min) min = l;
                if (l > max) max = l;
            }
            out[sel] = { min, max };
        }
        return out;
    }, { b64: png.toString("base64"), rects });
}

// Computed colors arrive as rgb(0-255) or, after color-mix(), color(srgb 0-1).
function luminance(css) {
    const nums = (css.match(/-?[\d.]+/g) || []).map(Number).slice(0, 3);
    const unit = css.startsWith("color(") ? nums : nums.map(v => v / 255);
    const [r, g, b] = unit.map(v => v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4);
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

// The weakest contrast over a background luminance range; 1 when the text's own luminance is inside it.
function worstContrast(color, { min, max }) {
    const f = luminance(color);
    if (f >= min && f <= max) return 1;
    const ratio = b => (Math.max(f, b) + 0.05) / (Math.min(f, b) + 0.05);
    return Math.min(ratio(min), ratio(max));
}

test("text on the tinted glass meets AA over the hotel photo in every brand and mode", async t => {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext();
        await routeAssets(context);
        const page = await context.newPage();
        const canvasPage = await context.newPage();
        await canvasPage.setContent("<!doctype html><title>pixels</title>");
        const failures = [];
        let measured = 0;

        for (const [brand, mode] of THEMES) {
            for (const [state, body, viewports] of STATES) {
                for (const [width, height] of viewports) {
                    await page.setViewportSize({ width, height });
                    await show(page, pageHtml({ brand, mode, body }));
                    // The photo must actually load; a missing image would make the glass look safer than it is.
                    const photoLoaded = await page.evaluate(async () => {
                        const url = getComputedStyle(document.querySelector(".page-shell")).backgroundImage.match(/url\("([^"]+)"\)/)?.[1];
                        if (!url) return false;
                        const img = new Image();
                        img.src = url;
                        return img.decode().then(() => img.naturalWidth > 0, () => false);
                    });
                    assert.ok(photoLoaded, "The page shell must paint the hotel photo");

                    const boxes = await page.evaluate(textOnGlass, { containers: CONTAINERS, tokens: TOKENS });
                    for (const [sel, box] of Object.entries(boxes)) {
                        assert.ok(box.glassy, `${brand} ${mode} ${state} ${width}x${height}: ${sel} must have a backdrop blur`);
                    }

                    await page.addStyleTag({ content: `${CONTAINERS.map(s => `${s} *`).join(", ")} { visibility: hidden !important; }` });
                    const png = await page.screenshot();
                    const ranges = await luminanceRange(canvasPage,
                        png, Object.fromEntries(Object.entries(boxes).map(([sel, box]) => [sel, box.rect])));

                    for (const [sel, box] of Object.entries(boxes)) {
                        for (const text of box.texts) {
                            const ratio = worstContrast(text.color, ranges[sel]);
                            const needed = text.large ? 3 : 4.5;
                            measured++;
                            if (ratio < needed) {
                                failures.push(`${brand} ${mode} ${state} ${width}x${height} ${sel} "${text.text}": ` +
                                    `${ratio.toFixed(2)}:1, needs ${needed}:1`);
                            }
                        }
                    }
                }
            }
        }

        t.diagnostic(`${measured} text checks`);
        assert.ok(measured > 400, `Expected the fixture to expose text on every glass container, measured ${measured}`);
        assert.deepEqual(failures, []);
    } finally {
        await browser.close();
    }
});
