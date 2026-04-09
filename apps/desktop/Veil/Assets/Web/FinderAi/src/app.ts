import { MockHostTransport } from "./mockHost.js";
import type {
  HostStateEnvelope,
  WorkerInput,
  WorkerOutput,
  WorkerViewState
} from "./types.js";

interface NativeWebViewBridge {
  addEventListener(type: "message", listener: (event: MessageEvent<string>) => void): void;
  postMessage(message: unknown): void;
}

declare global {
  interface Window {
    __veilCommandQueue?: unknown[];
    __veilReceiveHostState?: (payload: HostStateEnvelope) => void;
  }
}

const worker = new Worker(new URL("./worker.js", import.meta.url), { type: "module" });

const root = document.getElementById("app") as HTMLDivElement;
const sidebar = document.getElementById("sidebar") as HTMLElement;
const sessionEmpty = document.getElementById("session-empty") as HTMLElement;
const sessionList = document.getElementById("session-list") as HTMLElement;
const providerLabel = document.getElementById("provider-label") as HTMLElement;
const providerStatus = document.getElementById("provider-status") as HTMLElement;
const modelButton = document.getElementById("model-button") as HTMLButtonElement;
const modelButtonLabel = document.getElementById("model-button-label") as HTMLElement;
const modelMenu = document.getElementById("model-menu") as HTMLElement;
const emptyState = document.getElementById("empty-state") as HTMLElement;
const conversation = document.getElementById("conversation") as HTMLElement;
const turnList = document.getElementById("turn-list") as HTMLElement;
const statusRow = document.getElementById("status-row") as HTMLElement;
const composer = document.getElementById("composer") as HTMLTextAreaElement;
const newChatButton = document.getElementById("new-chat-button") as HTMLButtonElement;
const deleteChatButton = document.getElementById("delete-chat-button") as HTMLButtonElement;
const backButton = document.getElementById("back-button") as HTMLButtonElement;
const sidebarToggleButton = document.getElementById("sidebar-toggle-button") as HTMLButtonElement;
const sendButton = document.getElementById("send-button") as HTMLButtonElement;
const cancelButton = document.getElementById("cancel-button") as HTMLButtonElement;

let lastDraft = "";
let lastTurnCount = 0;
let currentView: WorkerViewState | null = null;

const nativeWebview = (window as Window & {
  chrome?: {
    webview?: NativeWebViewBridge;
  };
}).chrome?.webview;
const usePollingBridge = new URLSearchParams(window.location.search).get("bridge") === "poll";

function dispatchToWorker(message: WorkerInput): void {
  worker.postMessage(message);
}

const hasNativeBridge = !usePollingBridge && nativeWebview !== undefined;

if (hasNativeBridge) {
  nativeWebview.addEventListener("message", (event: MessageEvent<string>) => {
    const envelope = JSON.parse(event.data) as HostStateEnvelope;
    if (envelope.type === "hostState") {
      dispatchToWorker({ type: "hostState", state: envelope.state });
    }
  });
} else if (usePollingBridge) {
  window.__veilCommandQueue = [];
  window.__veilReceiveHostState = (envelope: HostStateEnvelope) => {
    if (envelope.type === "hostState") {
      dispatchToWorker({ type: "hostState", state: envelope.state });
    }
  };
} else {
  const mock = new MockHostTransport((state) => {
    dispatchToWorker({ type: "hostState", state });
  });

  worker.addEventListener("message", (event: MessageEvent<WorkerOutput>) => {
    if (event.data.type === "hostCommand") {
      mock.dispatch(event.data.payload);
    }
  });
}

worker.addEventListener("message", (event: MessageEvent<WorkerOutput>) => {
  if (event.data.type === "hostCommand") {
    if (hasNativeBridge) {
      nativeWebview.postMessage(event.data.payload);
    } else if (usePollingBridge) {
      window.__veilCommandQueue ??= [];
      window.__veilCommandQueue.push(event.data.payload);
    }
    return;
  }

  render(event.data.view);
});

function autosizeComposer(): void {
  composer.style.height = "0px";
  composer.style.height = `${Math.min(Math.max(composer.scrollHeight, 54), 180)}px`;
}

function renderModelMenu(view: WorkerViewState): void {
  modelMenu.innerHTML = "";

  for (const option of view.modelOptions) {
    const row = document.createElement("button");
    row.type = "button";
    row.className = `model-row${option.selected ? " active" : ""}`;
    row.addEventListener("click", () => {
      dispatchToWorker({ type: "selectModel", modelId: option.id });
    });

    const label = document.createElement("span");
    label.textContent = option.label;
    row.appendChild(label);

    if (option.badge) {
      const badge = document.createElement("span");
      badge.className = "model-badge";
      badge.textContent = option.badge;
      row.appendChild(badge);
    }

    modelMenu.appendChild(row);
  }

  modelMenu.classList.toggle("hidden", !view.modelMenuOpen);
}

function renderSessions(view: WorkerViewState): void {
  sessionList.innerHTML = "";
  sessionEmpty.classList.toggle("hidden", view.sessions.length > 0);

  for (const session of view.sessions) {
    const row = document.createElement("button");
    row.type = "button";
    row.className = `session-row${session.selected ? " active" : ""}`;
    row.addEventListener("click", () => {
      dispatchToWorker({ type: "loadConversation", sessionId: session.id });
    });

    const title = document.createElement("div");
    title.className = "session-title";
    title.textContent = session.title;

    const meta = document.createElement("div");
    meta.className = "session-meta";
    meta.textContent = `${session.provider} • ${session.model}`;

    const preview = document.createElement("div");
    preview.className = "session-preview";
    preview.textContent = `${session.preview} • ${session.updatedAtLabel}`;

    row.append(title, meta, preview);
    sessionList.appendChild(row);
  }
}

function renderTurns(view: WorkerViewState): void {
  turnList.innerHTML = "";

  for (const turn of view.turns) {
    const row = document.createElement("div");
    row.className = `turn ${turn.role}`;

    const bubble = document.createElement("div");
    bubble.className = "bubble";
    bubble.textContent = turn.content;
    row.appendChild(bubble);
    turnList.appendChild(row);
  }

  if (view.turns.length !== lastTurnCount) {
    turnList.scrollTop = turnList.scrollHeight;
    lastTurnCount = view.turns.length;
  }
}

function render(view: WorkerViewState): void {
  currentView = view;
  root.classList.toggle("sidebar-collapsed", !view.sidebarVisible);
  sidebar.classList.toggle("hidden", false);
  providerLabel.textContent = view.providerLabel;
  providerStatus.textContent = view.providerStatus;
  providerStatus.className = `provider-status ${view.providerStatusTone}`;
  modelButtonLabel.textContent = view.currentModelLabel;
  emptyState.classList.toggle("hidden", !view.showEmptyState);
  conversation.classList.toggle("hidden", view.showEmptyState);
  deleteChatButton.disabled = !view.canDelete || view.busy;
  newChatButton.disabled = view.busy;
  sendButton.disabled = view.busy || !view.draft.trim();
  cancelButton.classList.toggle("hidden", !view.canCancel);
  statusRow.classList.toggle("hidden", false);
  statusRow.textContent = view.statusText;

  renderSessions(view);
  renderModelMenu(view);
  renderTurns(view);

  if (view.draft !== lastDraft && composer.value !== view.draft) {
    composer.value = view.draft;
    autosizeComposer();
    lastDraft = view.draft;
  }

  if (document.activeElement !== composer && !view.busy) {
    composer.focus();
  }
}

composer.addEventListener("input", () => {
  lastDraft = composer.value;
  autosizeComposer();
  dispatchToWorker({ type: "draftChanged", value: composer.value });
});

composer.addEventListener("keydown", (event) => {
  if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
    event.preventDefault();
    dispatchToWorker({ type: "submit" });
  }
});

newChatButton.addEventListener("click", () => {
  dispatchToWorker({ type: "newChat" });
});

deleteChatButton.addEventListener("click", () => {
  dispatchToWorker({ type: "deleteChat" });
});

backButton.addEventListener("click", () => {
  dispatchToWorker({ type: "openFinder" });
});

sidebarToggleButton.addEventListener("click", () => {
  dispatchToWorker({ type: "toggleSidebar" });
});

modelButton.addEventListener("click", () => {
  dispatchToWorker({ type: "toggleModelMenu" });
});

sendButton.addEventListener("click", () => {
  dispatchToWorker({ type: "submit" });
});

cancelButton.addEventListener("click", () => {
  dispatchToWorker({ type: "cancelRequest" });
});

document.addEventListener("click", (event) => {
  if (!currentView?.modelMenuOpen) {
    return;
  }

  const target = event.target as Node;
  if (!modelMenu.contains(target) && !modelButton.contains(target)) {
    dispatchToWorker({ type: "toggleModelMenu" });
  }
});

autosizeComposer();
dispatchToWorker({ type: "init" });
