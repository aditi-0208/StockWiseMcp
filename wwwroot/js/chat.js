// StockWise chat page. Replies are inserted with textContent, so nothing from the server is treated as HTML.
(function () {
    "use strict";

    const MIN_LENGTH = 2;
    const MAX_LENGTH = 500;
    const STORAGE_KEY = "stockwise.chat.history";
    const MAX_HISTORY = 50;

    const log = document.getElementById("chat-log");
    const form = document.getElementById("chat-form");
    const input = document.getElementById("chat-input");
    const send = document.getElementById("chat-send");
    const clear = document.getElementById("chat-clear");
    const mode = document.getElementById("chat-mode");
    const count = document.getElementById("chat-count");
    const errorBox = document.getElementById("chat-error");

    let csrf = null;
    let busy = false;
    let history = loadHistory();

    function loadHistory() {
        try {
            return JSON.parse(sessionStorage.getItem(STORAGE_KEY)) || [];
        } catch {
            return [];
        }
    }

    function saveHistory() {
        history = history.slice(-MAX_HISTORY);
        try {
            sessionStorage.setItem(STORAGE_KEY, JSON.stringify(history));
        } catch {
            // Storage can be unavailable; the conversation still works for this page view.
        }
    }

    function showError(message) {
        errorBox.textContent = message;
        errorBox.classList.toggle("d-none", !message);
    }

    function badge(text, style) {
        const span = document.createElement("span");
        span.className = "badge " + style;
        span.textContent = text;
        return span;
    }

    function renderMessage(entry) {
        const wrapper = document.createElement("div");
        wrapper.className = "chat-msg " + (entry.role === "user" ? "chat-msg-user" : "chat-msg-bot");

        const bubble = document.createElement("div");
        bubble.className = "chat-bubble";
        bubble.textContent = entry.text;
        wrapper.appendChild(bubble);

        if (entry.role === "bot") {
            const meta = document.createElement("div");
            meta.className = "chat-meta";
            meta.appendChild(badge(entry.mode, "text-bg-secondary"));
            (entry.tools || []).forEach(function (tool) {
                meta.appendChild(badge("MCP tool: " + tool, "text-bg-info"));
            });
            wrapper.appendChild(meta);

            if (entry.notice) {
                const notice = document.createElement("div");
                notice.className = "chat-notice";
                notice.textContent = entry.notice;
                wrapper.appendChild(notice);
            }
        }

        log.appendChild(wrapper);
    }

    function renderAll() {
        log.replaceChildren();
        if (history.length === 0) {
            const empty = document.createElement("p");
            empty.className = "chat-empty";
            empty.textContent = "Ask a question below, or pick an example.";
            log.appendChild(empty);
            return;
        }

        history.forEach(renderMessage);
        log.scrollTop = log.scrollHeight;
    }

    function updateCount() {
        const length = input.value.trim().length;
        count.textContent = length + " / " + MAX_LENGTH;
        count.classList.toggle("chat-count-over", length > MAX_LENGTH);
    }

    function setBusy(value) {
        busy = value;
        send.disabled = value;
        send.textContent = value ? "Sending…" : "Send";
    }

    async function getToken() {
        const response = await fetch("/api/chat/token", { credentials: "same-origin" });
        if (!response.ok) {
            throw new Error("token");
        }

        csrf = await response.json();
        return csrf;
    }

    function postQuestion(message) {
        return fetch("/api/chat", {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json", [csrf.headerName]: csrf.token },
            body: JSON.stringify({ message: message, mode: mode.value })
        });
    }

    function UserError(message) {
        this.userMessage = message;
    }

    async function ask(message) {
        if (!csrf) {
            await getToken();
        }

        let response = await postQuestion(message);

        // A 400 with no field errors means the token was rejected (for example it expired): retry once.
        if (response.status === 400) {
            const problem = await response.clone().json().catch(function () { return {}; });
            if (problem.errors) {
                const first = Object.values(problem.errors)[0];
                throw new UserError(first && first[0] ? first[0] : "That message can't be sent.");
            }

            await getToken();
            response = await postQuestion(message);
        }

        if (!response.ok) {
            throw new Error("status " + response.status);
        }

        return response.json();
    }

    async function submit() {
        if (busy) {
            return;
        }

        const message = input.value.trim();
        if (message.length < MIN_LENGTH || message.length > MAX_LENGTH) {
            showError("Please enter between " + MIN_LENGTH + " and " + MAX_LENGTH + " characters.");
            input.focus();
            return;
        }

        showError("");
        setBusy(true);
        try {
            const result = await ask(message);
            history.push({ role: "user", text: message });
            history.push({ role: "bot", text: result.reply, mode: result.mode, tools: result.toolsUsed, notice: result.notice });
            saveHistory();
            renderAll();
            input.value = "";
            updateCount();
        } catch (error) {
            showError(error.userMessage || "Sorry, the assistant can't be reached right now. Please try again.");
        } finally {
            setBusy(false);
            input.focus();
        }
    }

    async function loadModes() {
        try {
            const response = await fetch("/api/chat/modes", { credentials: "same-origin" });
            if (!response.ok) {
                return;
            }

            const data = await response.json();
            mode.replaceChildren();
            data.modes.forEach(function (item) {
                const option = document.createElement("option");
                option.value = item.id;
                option.textContent = item.available ? item.name : item.name + " (not configured)";
                option.disabled = !item.available;
                option.selected = item.id === data.default && item.available;
                mode.appendChild(option);
            });
        } catch {
            // The Guided Demo option already in the page still works.
        }
    }

    form.addEventListener("submit", function (event) {
        event.preventDefault();
        submit();
    });

    input.addEventListener("input", updateCount);
    input.addEventListener("keydown", function (event) {
        if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            submit();
        }
    });

    clear.addEventListener("click", function () {
        history = [];
        saveHistory();
        showError("");
        renderAll();
        input.focus();
    });

    document.querySelectorAll("[data-question]").forEach(function (button) {
        button.addEventListener("click", function () {
            input.value = button.dataset.question;
            updateCount();
            submit();
        });
    });

    renderAll();
    updateCount();
    loadModes();
}());
