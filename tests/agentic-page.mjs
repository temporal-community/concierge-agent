// Shared browser fixture for the guest chat page (Components/Pages/Agentic.razor), used by
// chat-layout.test.mjs and glass-contrast.test.mjs. Not a test file itself.
//
// The markup copies what the running page renders (captured from its DOM), so layout and contrast are
// measured on the real structure with the real stylesheets, loaded in the page's order. Keep it
// aligned with Agentic.razor, SiteHeader.razor, ThemePicker.razor, FocusCard.razor, StayMessage.razor,
// RideCard.razor, SuggestionChips.razor, VoiceInputControls.razor, VoiceStatus.razor and
// RideActivityPanel.razor.
import { readFileSync } from "node:fs";

export const ORIGIN = "https://concierge.test";

const web = new URL("../src/Concierge.Web/", import.meta.url);
const obj = new URL("obj/Debug/net11.0/", web);

function readBuilt(url, what) {
    try {
        return readFileSync(url, "utf8");
    } catch {
        throw new Error(`${what} not found at ${url.pathname}. Build Concierge.Web first: `
            + "dotnet build src/Concierge.Web/Concierge.Web.csproj");
    }
}

// The Components.AI kit's stylesheet ships in its NuGet package; the build manifest says where.
function kitStylesheet() {
    const manifest = JSON.parse(readBuilt(new URL("staticwebassets.build.json", obj), "The static web assets manifest"));
    const asset = manifest.Assets.find(a => a.BasePath === "_content/Microsoft.AspNetCore.Components.AI"
        && a.AssetRole === "Primary" && a.RelativePath.startsWith("ai-chat") && a.RelativePath.endsWith(".css"));
    if (!asset) {
        throw new Error("The static web assets manifest lists no ai-chat.css from Microsoft.AspNetCore.Components.AI; "
            + "restore and rebuild Concierge.Web.");
    }
    return asset.Identity;
}

// Same order as the page: App.razor's app.css and scoped bundle, then Agentic.razor's HeadContent.
const scopedCssUrl = new URL("Concierge.Web.styles.css", new URL("scopedcss/bundle/", obj));
const STYLESHEETS = [
    ["/css/app.css", new URL("wwwroot/css/app.css", web).pathname],
    ["/Concierge.Web.styles.css", scopedCssUrl.pathname],
    ["/_content/Microsoft.AspNetCore.Components.AI/ai-chat.css", kitStylesheet()],
    ["/css/agentic.css", new URL("wwwroot/css/agentic.css", web).pathname],
];

// Every element of RideActivityPanel carries its CSS isolation attribute.
const scope = readBuilt(scopedCssUrl, "The scoped CSS bundle").match(/\.ride-activity-drawer\[(b-[\w]+)\]/)?.[1];
if (!scope) {
    throw new Error("The scoped CSS bundle has no .ride-activity-drawer rule; rebuild Concierge.Web.");
}
const scoped = html => html.replace(/<([a-z][\w-]*)(?=[\s>])/g, `<$1 ${scope}`);

/** Serves the stylesheets and wwwroot images under ORIGIN, as the app does. */
export async function routeAssets(context) {
    const files = new Map(STYLESHEETS);
    await context.route(`${ORIGIN}/**`, route => {
        const path = new URL(route.request().url()).pathname;
        if (files.has(path)) return route.fulfill({ path: files.get(path), contentType: "text/css" });
        if (path.startsWith("/images/")) {
            return route.fulfill({ path: new URL(`wwwroot${path}`, web).pathname, contentType: "image/jpeg" });
        }
        return route.fallback();
    });
}

/** Loads html as the page at ORIGIN, with the assets from routeAssets. */
export async function show(page, html) {
    await page.route(`${ORIGIN}/`, route => route.fulfill({ body: html, contentType: "text/html" }));
    await page.goto(`${ORIGIN}/`, { waitUntil: "networkidle" });
    await page.unroute(`${ORIGIN}/`);
}

export const pageHtml = ({ brand = "temporal", mode = "light", body }) => `<!doctype html>
    <html lang="en" data-brand="${brand}" data-theme="${mode}"><head><meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    ${STYLESHEETS.map(([href]) => `<link rel="stylesheet" href="${href}">`).join("\n    ")}
    <title>Hotel ZGY Concierge</title></head>
    <body><div class="page-shell">${body}</div></body></html>`;

const svg = path => `<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="${path}"/></svg>`;

const themePicker = `
    <label class="theme-picker compact-theme-picker"><span class="visually-hidden">Theme</span>
        <svg class="theme-icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><circle cx="12" cy="12" r="4"/>
            <path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5l1.5 1.5M5 19l1.5-1.5M17.5 6.5 19 5"/></svg>
        <select aria-label="Color theme">
            <optgroup label="Temporal"><option value="temporal-light">Temporal · Light</option>
                <option value="temporal-dark">Temporal · Dark</option><option value="temporal-system">Temporal · System</option></optgroup>
            <optgroup label="Deepgram"><option value="deepgram-light">Deepgram · Light</option>
                <option value="deepgram-dark">Deepgram · Dark</option><option value="deepgram-system">Deepgram · System</option></optgroup>
        </select>
    </label>`;

/** SiteHeader: only the theme picker before verification; the chat controls after. */
export function header({ signedIn, activityOpen = false }) {
    const controls = signedIn ? `
        <button class="voice-control-button activity-button" type="button" aria-controls="ride-activity-panel"
                aria-label="Activity" aria-expanded="${activityOpen}">
            ${svg("M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4")}<span>Activity</span></button>
        <button class="voice-end-voice-button" type="button" aria-label="Start over">
            ${svg("M4 4v5h5M4.6 15a8 8 0 1 0 1.8-8.3L4 9")}<span>Start over</span></button>
        <button class="end-chat-button voice-end-chat-button" type="button" aria-label="End chat">
            ${svg("m6 6 12 12M18 6 6 18")}<span>End chat</span></button>` : "";
    return `
    <header class="site-header${signedIn ? " voice-site-header" : ""}">
        <a class="hotel-brand" href="/" aria-label="Hotel ZGY Miami"><strong>Hotel ZGY</strong><span>/ Miami</span></a>
        <div class="header-actions voice-header-actions">
            <div class="voice-header-controls" role="group" aria-label="Chat controls">${controls}${themePicker}</div>
        </div>
    </header>`;
}

export const verifyMain = `
    <main class="main-content focus-main">
        <section class="focus-card " aria-labelledby="verify-title">
            <h1 id="verify-title">Verify your stay</h1>
            <p class="focus-card-intro">Enter your last name and room number to continue.</p>
            <form class="verification-form">
                <label for="last-name">Last name</label>
                <input id="last-name" type="text" placeholder="Last name" autocomplete="family-name" maxlength="100">
                <label for="room-number">Room number</label>
                <input id="room-number" type="text" placeholder="Room number" inputmode="numeric" autocomplete="off" maxlength="4">
                <p class="field-hint">Enter the room number exactly as provided.</p>
                <button class="primary-button focus-card-action" type="submit">Verify and continue</button>
            </form>
        </section>
    </main>`;

const row = (guest, time, content) => `
    <div class="message-row ${guest ? "guest-row" : "concierge-row"}">
        <div class="message-content">
            <div class="message-meta"><strong>${guest ? "GUEST" : "CONCIERGE"}</strong>
                <time datetime="2026-11-08T21:13:00-05:00">${time}</time></div>
            <div class="message-bubble ${guest ? "guest-bubble" : "concierge-bubble"}">${content}</div>
        </div>
    </div>`;

const rideCard = `
    <section class="ride-card-row" aria-label="Ride booked">
        <div class="ride-card">
            <header class="ride-card__header"><h3>Ride booked</h3><span class="ride-card__note">Hotel time (ET)</span></header>
            <ul class="ride-card__rides"><li class="ride-card__ride">
                <div class="ride-card__ride-heading"><strong class="ride-card__dropoff">Wayan Miami, 50 Northwest 24th Street</strong>
                    <span class="ride-card__status">Booked</span></div>
                <time class="ride-card__time" datetime="2026-11-09T10:00:00-05:00">Mon Nov 9 · 10:00 AM</time>
                <p class="ride-card__detail">Pickup: Hotel ZGY, 2217 NW Miami Court, Miami, FL 33127</p>
            </li></ul>
        </div>
    </section>`;

/** One StayMessage turn: the guest's message, the concierge's reply, and optionally a ride card. */
export const turn = ({ date, guest, reply, time = "9:13 PM", ride = false }) => `
    <div class="sc-ai-turn sc-ai-turn--user">
        ${date ? `<div class="date-separator"><span>${date}</span></div>` : ""}
        ${row(true, time, guest)}
        ${row(false, time, `<div class="assistant-response">${reply}</div>`)}
        ${ride ? rideCard : ""}
    </div>`;

const suggestions = `
    <section class="agentic-suggestions" aria-labelledby="agentic-suggestions-title">
        <h2 id="agentic-suggestions-title" class="agentic-suggestions__title">How can we help?</h2>
        <div class="agentic-suggestions__chips">
            <button type="button" class="agentic-suggestion-chip">What time is breakfast?</button>
            <button type="button" class="agentic-suggestion-chip">How do I connect to Wi-Fi?</button>
            <button type="button" class="agentic-suggestion-chip">What amenities are available?</button>
            <button type="button" class="agentic-suggestion-chip">Can you recommend a nearby restaurant?</button>
        </div>
    </section>`;

// The idle-warning bar Agentic.razor shows between the transcript and the input.
const expiring = voice => `
    <div class="session-expired" role="status" aria-live="polite" aria-atomic="true">
        <span class="expired-icon" aria-hidden="true">!</span>
        <div class="expired-copy"><strong>Chat ending soon</strong>
            <span>This chat will end within 30 seconds due to inactivity.</span>
            <small>${voice === "paused" ? "Resume voice and speak to continue." : "Speak to continue."}</small></div>
    </div>`;

const micPaths = "M12 15a3 3 0 0 0 3-3V6a3 3 0 0 0-6 0v6a3 3 0 0 0 3 3ZM19 11a7 7 0 0 1-14 0M12 18v3";
const voiceLabels = { connecting: "Connecting…", listening: "Listening", thinking: "Thinking…", speaking: "Speaking", paused: "Paused" };
const micLabel = voice => !voice ? "Start voice" : voice === "paused" ? "Resume voice" : "End voice";

// VoiceStatus, then MessageInput with VoiceInputControls as LeadingActions. Voice is null (text only)
// or the controller's state; the speaker button exists only while voice is live.
function input(voice) {
    const live = voice && voice !== "paused";
    return `
    <div class="sc-ai-chat-page__footer">
        <div class="sc-ai-chat-page__input-container">
            ${voice ? `<div class="voice-status-row" data-state="${voice}">
                <span class="voice-input__status" role="status" aria-live="polite">
                    <span class="voice-input__dot" aria-hidden="true"></span>
                    <span class="voice-input__label">${voiceLabels[voice]}</span></span></div>` : ""}
            <div class="sc-ai-input">
                <div class="voice-input" data-state="${voice ?? "idle"}">
                    <span class="voice-input__meter">
                        <button type="button" class="voice-input__button voice-input__mic" title="${micLabel(voice)}"
                                aria-label="${micLabel(voice)}" aria-pressed="${Boolean(live)}">
                            <svg class="voice-input__icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="${micPaths}"/></svg>
                            <svg class="voice-input__icon voice-input__icon--off" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
                                <path d="${micPaths}"/><path d="m4 4 16 16"/></svg>
                        </button>
                    </span>
                    ${live ? `<span class="voice-input__meter voice-input__meter--speaker">
                        <button type="button" class="voice-input__button voice-input__speaker" title="Mute spoken replies"
                                aria-label="Mute spoken replies" aria-pressed="false">
                            ${svg("M4 9v6h4l5 4V5L8 9H4ZM16.5 8.5a5 5 0 0 1 0 7")}</button></span>` : ""}
                </div>
                <div class="sc-ai-input__body">
                    <textarea class="sc-ai-input__textarea" placeholder="Ask the concierge…" aria-label="Ask the concierge…"></textarea>
                </div>
                <button type="button" class="sc-ai-input__send" aria-label="Send message"><svg viewBox="0 0 24 24" fill="none"
                    stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 2 11 13"/>
                    <path d="M22 2 15 22 11 13 2 9z"/></svg></button>
            </div>
        </div>
    </div>`;
}

const drawer = open => scoped(`
    <aside id="ride-activity-panel" class="ride-activity-drawer${open ? " is-open" : ""}" aria-labelledby="ride-activity-title"
           ${open ? "" : 'aria-hidden="true" inert'} tabindex="-1">
        <header class="ride-activity-header">
            <div><h2 id="ride-activity-title">Activity</h2>
                <p class="ride-activity-subtitle">Updates for your stay · Miami time (ET)</p></div>
            <button class="ride-activity-close" type="button" aria-label="Close activity">${svg("m6 6 12 12M18 6 6 18")}</button>
        </header>
        <div class="ride-activity-content">
            <div class="ride-activity-empty" role="status">
                <span class="ride-activity-empty-icon" aria-hidden="true"><svg viewBox="0 0 24 24" focusable="false">
                    <circle cx="8" cy="5" r="1.5"/><circle cx="16" cy="12" r="1.5"/><circle cx="10" cy="19" r="1.5"/></svg></span>
                <h3>No activity yet</h3>
                <p>Ride updates will appear here when you book a ride with the concierge.</p>
            </div>
        </div>
        <footer class="ride-activity-footer"><p>To change or cancel a ride, ask the concierge.</p></footer>
    </aside>`);

/**
 * The signed-in chat: the transcript (turns, or the suggestion chips when empty), the optional
 * idle-warning bar, the input, and the activity drawer.
 */
export function chatMain({ turns = [], activityOpen = false, voice = null, idleWarning = false }) {
    return `
    <main class="main-content${activityOpen ? " activity-main-content" : ""}">
        <div class="activity-layout${activityOpen ? " activity-layout-open" : ""}">
            <section class="chat-panel" aria-label="Hotel concierge chat">
                <div class="conversation-heading"><span>YOUR STAY CONVERSATION</span></div>
                <div class="sc-ai-root sc-ai-chat-page">
                    <div class="agentic-transcript" aria-label="Stay conversation">
                        <div class="sc-ai-message-list">
                            ${turns.length ? turns.join("") : suggestions}
                            <div class="sc-ai-message-list__footer"></div>
                        </div>
                        <button class="new-message-button" type="button" hidden>New message ↓</button>
                    </div>
                    ${idleWarning ? expiring(voice) : ""}
                    ${input(voice)}
                </div>
            </section>${drawer(activityOpen)}
        </div>
    </main>`;
}
