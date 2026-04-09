import type {
  HostAppState,
  HostCommand,
  WorkerInput,
  WorkerOutput,
  WorkerViewState
} from "./types.js";

let hostState: HostAppState | null = null;
let draft = "";
let appliedDraftVersion = -1;
let modelMenuOpen = false;

function post(message: WorkerOutput): void {
  self.postMessage(message);
}

function sendHostCommand(payload: HostCommand): void {
  post({ type: "hostCommand", payload });
}

function getCurrentModelLabel(state: HostAppState | null): string {
  if (!state) {
    return "Select model";
  }

  const selected = state.modelOptions.find((item) => item.selected);
  return selected?.label ?? state.currentModel ?? "Select model";
}

function getStatusText(state: HostAppState | null): string {
  if (!state) {
    return "Connecting to Finder AI";
  }

  if (state.busy) {
    return `Thinking with ${state.providerLabel}...`;
  }

  return state.providerStatus;
}

function emitRender(): void {
  const view: WorkerViewState = {
    ready: hostState !== null,
    sidebarVisible: hostState?.sidebarVisible ?? true,
    providerLabel: hostState?.providerLabel ?? "Finder AI",
    providerStatus: hostState?.providerStatus ?? "Loading provider",
    providerStatusTone: hostState?.providerStatusTone ?? "warning",
    currentModelLabel: getCurrentModelLabel(hostState),
    modelMenuOpen,
    modelOptions: hostState?.modelOptions ?? [],
    sessions: hostState?.sessions ?? [],
    activeConversationId: hostState?.activeConversationId ?? null,
    turns: hostState?.turns ?? [],
    showEmptyState: (hostState?.turns.length ?? 0) === 0,
    draft,
    busy: hostState?.busy ?? false,
    canDelete: hostState?.canDelete ?? false,
    canCancel: hostState?.canCancel ?? false,
    statusText: getStatusText(hostState)
  };

  post({ type: "render", view });
}

function handleHostState(state: HostAppState): void {
  hostState = state;
  if (state.draftVersion !== appliedDraftVersion) {
    appliedDraftVersion = state.draftVersion;
    draft = state.draftPrompt;
  }

  if (state.busy) {
    modelMenuOpen = false;
  }

  emitRender();
}

function onInput(message: WorkerInput): void {
  switch (message.type) {
    case "init":
      sendHostCommand({ command: "ready" });
      emitRender();
      break;
    case "hostState":
      handleHostState(message.state);
      break;
    case "draftChanged":
      draft = message.value;
      emitRender();
      break;
    case "submit": {
      const prompt = draft.trim();
      if (!prompt || hostState?.busy) {
        return;
      }

      draft = "";
      modelMenuOpen = false;
      emitRender();
      sendHostCommand({ command: "submitPrompt", prompt });
      break;
    }
    case "newChat":
      draft = "";
      modelMenuOpen = false;
      emitRender();
      sendHostCommand({ command: "newChat" });
      break;
    case "deleteChat":
      sendHostCommand({ command: "deleteChat" });
      break;
    case "loadConversation":
      modelMenuOpen = false;
      sendHostCommand({ command: "loadConversation", sessionId: message.sessionId });
      break;
    case "toggleSidebar":
      sendHostCommand({ command: "toggleSidebar" });
      break;
    case "toggleModelMenu":
      modelMenuOpen = !modelMenuOpen;
      emitRender();
      break;
    case "selectModel":
      modelMenuOpen = false;
      emitRender();
      sendHostCommand({ command: "selectModel", modelId: message.modelId });
      break;
    case "refreshModels":
      sendHostCommand({ command: "refreshModels" });
      break;
    case "cancelRequest":
      sendHostCommand({ command: "cancelRequest" });
      break;
    case "openFinder":
      sendHostCommand({ command: "openFinder" });
      break;
  }
}

self.onmessage = (event: MessageEvent<WorkerInput>) => {
  onInput(event.data);
};
