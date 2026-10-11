// What the Install app page needs from the browser
export function state() {
    const agent = navigator.userAgent;
    // iPadOS asks for the desktop site and says Macintosh; the touch screen gives it away
    const ios = /iPhone|iPad|iPod/.test(agent) || (/Macintosh/.test(agent) && navigator.maxTouchPoints > 1);
    const platform = ios ? "ios" : /Android/.test(agent) ? "android" : /Windows/.test(agent) ? "windows" : "other";
    return {
        platform,
        secure: window.isSecureContext,
        installed: window.matchMedia("(display-mode: standalone)").matches || navigator.standalone === true,
        canPrompt: !!window.dcInstallPrompt
    };
}

// Chrome's and Edge's own install dialog; "accepted", "dismissed" or "unavailable"
export async function install() {
    const prompt = window.dcInstallPrompt;
    if (!prompt) return "unavailable";
    window.dcInstallPrompt = null;
    await prompt.prompt();
    const choice = await prompt.userChoice;
    return choice.outcome;
}
